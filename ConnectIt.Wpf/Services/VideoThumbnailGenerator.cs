using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 用 Windows 內建的殼層縮圖功能(跟檔案總管顯示影片縮圖用的是同一套機制:
/// <c>IShellItemImageFactory</c>)產生影片縮圖,不需要額外掛 ffmpeg 之類的外部工具/相依套件。
/// 前提是 Windows 本身已經有對應格式的縮圖處理常式(常見的 MP4/MOV/WMV 開箱即用;
/// MKV/AVI 等格式視使用者有沒有裝對應的殼層擴充套件而定,沒有的話就會產生失敗,
/// 呼叫端需要有 fallback 畫面)。
/// </summary>
public static class VideoThumbnailGenerator
{
    private const int ThumbnailSize = 320;

    // 限制同時間最多幾個縮圖產生工作(每個都會開一條 STA 執行緒呼叫殼層 COM API),
    // 避免使用者一次打開有很多影片的資料夾清單頁時,瞬間衝出大量執行緒同時讀取磁碟。
    private static readonly SemaphoreSlim ConcurrencyLimit = new(4);

    /// <summary>嘗試產生縮圖,回傳 JPEG 位元組;格式不支援/檔案有問題等情況回傳 null。</summary>
    public static async Task<byte[]?> TryGenerateAsync(string filePath)
    {
        await ConcurrencyLimit.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]?>();

            // IShellItemImageFactory 是殼層(STA-only)COM 介面,必須在 STA 執行緒上呼叫,
            // 不能直接在執行緒集區(預設 MTA)的背景工作上用,所以另外開一條專用的 STA 執行緒。
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(GenerateOnStaThread(filePath));
                }
                catch (Exception)
                {
                    tcs.TrySetResult(null);
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            ConcurrencyLimit.Release();
        }
    }

    private static byte[]? GenerateOnStaThread(string filePath)
    {
        var riid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref riid, out var factory) != 0 || factory == null)
        {
            return null;
        }

        try
        {
            var size = new SIZE { cx = ThumbnailSize, cy = ThumbnailSize };
            if (factory.GetImage(size, SIIGBF.BiggerSizeOk | SIIGBF.ThumbnailOnly, out var hBitmap) != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            finally
            {
                // GetImage() 回傳的 HBITMAP 所有權歸呼叫端,用完一定要釋放,不然會漏 GDI 控制代碼。
                DeleteObject(hBitmap);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? shellItem);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }
}
