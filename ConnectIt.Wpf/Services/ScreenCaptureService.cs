using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ConnectIt.Wpf.Services;

/// <summary>擷取並壓縮好的一張畫面。<see cref="Buffer"/> 前 4 bytes 預留給呼叫端填序號,是共用緩衝區,下一次擷取前必須用完。</summary>
public readonly record struct EncodedScreenFrame(byte[] Buffer, int Length, int Width, int Height);

/// <summary>
/// 螢幕鏡像分享端的擷取:拿到主螢幕的原始像素 → 長邊超過 <see cref="MaxLongEdge"/> 時縮小 → 畫上滑鼠游標 → 壓成 JPEG。
///
/// 原始像素優先用 DXGI Desktop Duplication(見 <see cref="DesktopDuplicationSource"/>,快、而且桌面沒變化時
/// 直接告訴我們);建立失敗(遠端桌面工作階段、舊驅動程式…)才退回 GDI BitBlt,並用逐 byte 比對判斷有沒有變化。
/// 桌面沒變、游標也沒動時回傳 null 不送,靜止的桌面就不會一直佔用頻寬。
///
/// 實測 2560×1440 主螢幕:GDI 擷取 25ms + 高品質縮放 18ms + 其他 ≈ 52ms/張(上限不到 20fps);
/// Desktop Duplication 3ms + 複製 2ms + 平行縮放 4ms + JPEG 5ms ≈ 15ms/張。
///
/// 只擷取主螢幕:遠端操控用的 SendInput 絕對座標(不帶 MOUSEEVENTF_VIRTUALDESK)剛好也是以主螢幕為範圍。
/// 非執行緒安全,同一時間只能有一個執行緒呼叫 <see cref="Capture"/>。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    /// <summary>長邊上限:1080p 維持原尺寸,2K/4K 螢幕縮到 1920 寬,文字仍清楚、一張 JPEG 約 100~400KB。</summary>
    public const int MaxLongEdge = 1920;

    public const int DefaultQuality = 70;
    private const int FallbackQuality = 40;

    /// <summary>Desktop Duplication 失效後,隔多久才再試著重建(安全桌面期間每次都重建只會浪費時間)。</summary>
    private const long DuplicationRetryIntervalMs = 2000;

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private DesktopDuplicationSource? _duplication;
    private long _duplicationRetryAt;

    // 原始螢幕像素(BGRA,不含游標)與 GDI 模式比對用的上一張。
    private byte[] _raw = [];
    private byte[] _gdiScratch = [];
    private int _rawWidth;
    private int _rawHeight;
    private bool _hasRaw;
    private Bitmap? _gdiBitmap;

    // 要送出的那張:縮放後的像素 + 畫游標/壓 JPEG 用的 GDI+ 點陣圖(直接包在固定住的陣列上,不用另外複製)。
    private byte[] _frame = [];
    private Bitmap? _frameBitmap;
    private int _frameWidth;
    private int _frameHeight;

    private bool _forceNext = true;
    private (IntPtr Handle, int X, int Y) _lastCursor;
    private readonly MemoryStream _jpeg = new(512 * 1024);

    /// <summary>主螢幕的實際像素大小。</summary>
    public static (int Width, int Height) PrimaryScreenSize()
    {
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            return (GetDeviceCaps(hdc, DESKTOPHORZRES), GetDeviceCaps(hdc, DESKTOPVERTRES));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>擷取尺寸為 <paramref name="width"/>×<paramref name="height"/> 的螢幕時,實際送出的畫面尺寸(長邊超過上限時等比例縮小,寬高取偶數)。</summary>
    public static (int Width, int Height) TargetSize(int width, int height)
    {
        var longEdge = Math.Max(width, height);
        if (longEdge <= MaxLongEdge)
        {
            return (width, height);
        }

        var scale = (double)MaxLongEdge / longEdge;
        return (Math.Max(2, (int)Math.Round(width * scale) / 2 * 2), Math.Max(2, (int)Math.Round(height * scale) / 2 * 2));
    }

    /// <summary>下一張不論有沒有變化都要送。</summary>
    public void ForceNextFrame() => _forceNext = true;

    /// <summary>最近一次擷取時游標的位置(0~1 相對座標),沒有顯示游標時為 null。</summary>
    public (double X, double Y)? CursorPosition =>
        _lastCursor.Handle == IntPtr.Zero || _rawWidth <= 0 || _rawHeight <= 0
            ? null
            : (Math.Clamp(_lastCursor.X / (double)_rawWidth, 0, 1), Math.Clamp(_lastCursor.Y / (double)_rawHeight, 0, 1));

    /// <summary>目前是不是用 Desktop Duplication(false 代表退回 GDI),診斷用。</summary>
    public bool UsingDesktopDuplication => _duplication != null;

    /// <summary>
    /// 擷取一張畫面,最多等 <paramref name="waitMs"/> 毫秒讓桌面出現變化。畫面沒變(而且游標沒動)、
    /// 或擷取失敗(例如切到 UAC 安全桌面、螢幕鎖定)時回傳 null。
    /// </summary>
    public EncodedScreenFrame? Capture(int quality = DefaultQuality, int waitMs = 0)
    {
        bool imageChanged;
        try
        {
            imageChanged = AcquireRaw(waitMs);
        }
        catch (Exception)
        {
            return null;
        }

        if (!_hasRaw)
        {
            return null;
        }

        var cursor = GetCursor();
        if (!imageChanged && !_forceNext && cursor == _lastCursor)
        {
            return null;
        }

        _forceNext = false;
        _lastCursor = cursor;

        PrepareFrame();
        DrawCursor(cursor);

        var length = Encode(quality) ?? Encode(FallbackQuality);
        return length is null ? null : new EncodedScreenFrame(_jpeg.GetBuffer(), length.Value, _frameWidth, _frameHeight);
    }

    /// <summary>更新 <see cref="_raw"/>,回傳桌面內容有沒有變化。</summary>
    private bool AcquireRaw(int waitMs)
    {
        if (_duplication == null && Environment.TickCount64 >= _duplicationRetryAt)
        {
            _duplication = DesktopDuplicationSource.TryCreate();
            if (_duplication == null)
            {
                _duplicationRetryAt = Environment.TickCount64 + DuplicationRetryIntervalMs;
            }
        }

        if (_duplication is { } duplication)
        {
            EnsureRaw(duplication.Width, duplication.Height);
            try
            {
                if (duplication.TryAcquire(_raw, waitMs))
                {
                    _hasRaw = true;
                    return true;
                }

                return false;
            }
            catch (DuplicationLostException)
            {
                // 解析度改變、安全桌面等:丟掉重建(下一次呼叫),這次先試 GDI。
                duplication.Dispose();
                _duplication = null;
                _duplicationRetryAt = Environment.TickCount64 + 200;
            }
        }

        return AcquireRawWithGdi();
    }

    private bool AcquireRawWithGdi()
    {
        var (width, height) = PrimaryScreenSize();
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        EnsureRaw(width, height);
        if (_gdiBitmap is null || _gdiBitmap.Width != width || _gdiBitmap.Height != height)
        {
            _gdiBitmap?.Dispose();
            _gdiBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }

        using (var graphics = Graphics.FromImage(_gdiBitmap))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        var data = _gdiBitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, _gdiScratch, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _gdiBitmap.UnlockBits(data);
        }

        if (_hasRaw && _gdiScratch.AsSpan().SequenceEqual(_raw))
        {
            return false;
        }

        (_raw, _gdiScratch) = (_gdiScratch, _raw);
        _hasRaw = true;
        return true;
    }

    private void EnsureRaw(int width, int height)
    {
        if (_rawWidth == width && _rawHeight == height)
        {
            return;
        }

        _rawWidth = width;
        _rawHeight = height;
        _raw = new byte[width * height * 4];
        _gdiScratch = new byte[width * height * 4];
        _hasRaw = false;
        _forceNext = true;
    }

    /// <summary>把原始像素(縮放後)放進 <see cref="_frame"/>,每次都從乾淨的原始畫面開始,才能重畫游標。</summary>
    private void PrepareFrame()
    {
        var (width, height) = TargetSize(_rawWidth, _rawHeight);
        if (_frameBitmap == null || _frameWidth != width || _frameHeight != height)
        {
            _frameBitmap?.Dispose();
            _frameWidth = width;
            _frameHeight = height;
            // 固定在 Pinned Object Heap 上,GDI+ 點陣圖直接包住這塊記憶體。
            _frame = GC.AllocateArray<byte>(width * height * 4, pinned: true);
            _frameBitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, Marshal.UnsafeAddrOfPinnedArrayElement(_frame, 0));
        }

        if (width == _rawWidth && height == _rawHeight)
        {
            Buffer.BlockCopy(_raw, 0, _frame, 0, _raw.Length);
        }
        else
        {
            ScaleBilinear(_raw, _rawWidth, _rawHeight, _frame, width, height);
        }
    }

    /// <summary>雙線性縮小(逐列平行處理)。只用在縮小 2 倍以內(1440p/1600p → 1920),不會有明顯的鋸齒。</summary>
    internal static void ScaleBilinear(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int width, int height)
    {
        var fx = (double)sourceWidth / width;
        var fy = (double)sourceHeight / height;
        Parallel.For(0, height, y =>
        {
            var sy = (y + 0.5) * fy - 0.5;
            var y0 = Math.Clamp((int)sy, 0, sourceHeight - 1);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var wy = Math.Clamp((int)((sy - y0) * 256), 0, 256);
            var row0 = y0 * sourceWidth * 4;
            var row1 = y1 * sourceWidth * 4;
            var o = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5) * fx - 0.5;
                var x0 = Math.Clamp((int)sx, 0, sourceWidth - 1);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var wx = Math.Clamp((int)((sx - x0) * 256), 0, 256);
                int a = row0 + x0 * 4, b = row0 + x1 * 4, c = row1 + x0 * 4, d = row1 + x1 * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source[a + channel] * (256 - wx) + source[b + channel] * wx;
                    var bottom = source[c + channel] * (256 - wx) + source[d + channel] * wx;
                    destination[o + channel] = (byte)((top * (256 - wy) + bottom * wy) >> 16);
                }

                destination[o + 3] = 255;
                o += 4;
            }
        });
    }

    /// <summary>壓成 JPEG 放進 <see cref="_jpeg"/>(前面預留序號),太大塞不進單一訊框時回傳 null。</summary>
    private int? Encode(int quality)
    {
        _jpeg.SetLength(0);
        _jpeg.Write(stackalloc byte[ScreenMessage.FrameHeaderSize]);

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        _frameBitmap!.Save(_jpeg, JpegCodec, parameters);

        var length = (int)_jpeg.Length;
        return length <= ConnectionService.MaxFramePayloadSize ? length : null;
    }

    /// <summary>目前的游標(形狀 + 位置,換算成原始螢幕像素);沒有顯示游標時 Handle 為 0。</summary>
    private (IntPtr Handle, int X, int Y) GetCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0)
        {
            return (IntPtr.Zero, 0, 0);
        }

        // GetCursorInfo 的座標依這個程式的 DPI 感知模式而定(可能是縮放過的邏輯座標),
        // 用「邏輯螢幕寬度 ↔ 實際像素寬度」的比例換算,不管哪種模式都對得上。
        var logicalWidth = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        var logicalHeight = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        var x = (int)Math.Round(info.ptScreenPos.X * (double)_rawWidth / logicalWidth);
        var y = (int)Math.Round(info.ptScreenPos.Y * (double)_rawHeight / logicalHeight);
        return (info.hCursor, x, y);
    }

    private void DrawCursor((IntPtr Handle, int X, int Y) cursor)
    {
        if (cursor.Handle == IntPtr.Zero || _frameBitmap == null)
        {
            return;
        }

        var hotspotX = 0;
        var hotspotY = 0;
        if (GetIconInfo(cursor.Handle, out var iconInfo))
        {
            hotspotX = iconInfo.xHotspot;
            hotspotY = iconInfo.yHotspot;
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmMask);
            }

            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmColor);
            }
        }

        var x = (int)Math.Round(cursor.X * (double)_frameWidth / _rawWidth) - hotspotX;
        var y = (int)Math.Round(cursor.Y * (double)_frameHeight / _rawHeight) - hotspotY;

        using var graphics = Graphics.FromImage(_frameBitmap);
        var hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, x, y, cursor.Handle, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _gdiBitmap?.Dispose();
        _frameBitmap?.Dispose();
        _jpeg.Dispose();
    }

    // ===================== Win32 =====================

    private const int DESKTOPVERTRES = 117;
    private const int DESKTOPHORZRES = 118;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int width, int height, int step, IntPtr brush, int flags);
}
