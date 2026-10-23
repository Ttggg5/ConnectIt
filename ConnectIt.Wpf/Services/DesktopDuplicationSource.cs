using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 用 DXGI Desktop Duplication 擷取主螢幕(座標原點 (0,0) 的那個輸出):直接從 GPU 拿到 DWM 合成好的桌面,
/// 2560×1440 取一張約 3ms + 複製 2ms;GDI BitBlt 要約 25ms。而且桌面沒有變化時 AcquireNextFrame 會逾時,
/// 天然就有「沒變化不送」的效果。
///
/// 解析度改變、切到 UAC 安全桌面、全螢幕遊戲切換模式時會丟出 DXGI_ERROR_ACCESS_LOST,這時要整組重建;
/// 呼叫端(<see cref="ScreenCaptureService"/>)在建立失敗時改用 GDI 擷取。
/// 不包含滑鼠游標(Desktop Duplication 的設計),游標由呼叫端自己畫。非執行緒安全。
/// </summary>
internal sealed class DesktopDuplicationSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;

    public int Width { get; }

    public int Height { get; }

    private DesktopDuplicationSource(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, int width, int height)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        Width = width;
        Height = height;
        _staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
    }

    /// <summary>建立失敗(遠端桌面工作階段、不支援的驅動程式、主螢幕是直立旋轉的…)時回傳 null。</summary>
    public static DesktopDuplicationSource? TryCreate()
    {
        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput? chosenOutput = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; chosenOutput == null && factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    var bounds = output.Description.DesktopCoordinates;
                    if (chosenOutput == null && bounds.Left == 0 && bounds.Top == 0)
                    {
                        chosenOutput = output;
                    }
                    else
                    {
                        output.Dispose();
                    }
                }

                if (chosenOutput != null)
                {
                    chosenAdapter = adapter;
                }
                else
                {
                    adapter.Dispose();
                }
            }

            if (chosenAdapter == null || chosenOutput == null)
            {
                return null;
            }

            if (D3D11CreateDevice(chosenAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out device, out context).Failure
                || device == null || context == null)
            {
                return null;
            }

            using var output1 = chosenOutput.QueryInterface<IDXGIOutput1>();
            var duplication = output1.DuplicateOutput(device);
            var description = duplication.Description;
            if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            {
                // 直立螢幕拿到的是沒轉過的材質,要自己旋轉;少見,交給 GDI。
                duplication.Dispose();
                device.Dispose();
                context.Dispose();
                return null;
            }

            return new DesktopDuplicationSource(device, context, duplication,
                (int)description.ModeDescription.Width, (int)description.ModeDescription.Height);
        }
        catch
        {
            context?.Dispose();
            device?.Dispose();
            return null;
        }
        finally
        {
            chosenOutput?.Dispose();
            chosenAdapter?.Dispose();
        }
    }

    /// <summary>
    /// 等最多 <paramref name="timeoutMs"/> 毫秒拿下一張桌面畫面,有新畫面就複製進 <paramref name="destination"/>
    /// (BGRA,每列 Width*4 bytes)並回傳 true;桌面沒變化(逾時,或只有滑鼠移動)回傳 false。
    /// 擷取通道失效(需要重建)時丟出 <see cref="DuplicationLostException"/>。
    /// </summary>
    public bool TryAcquire(byte[] destination, int timeoutMs)
    {
        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return false;
        }

        if (result.Failure || resource == null)
        {
            throw new DuplicationLostException();
        }

        try
        {
            // LastPresentTime 為 0 代表這次只有滑鼠游標的位置/形狀變了,桌面內容本身沒變。
            if (info.LastPresentTime == 0)
            {
                return false;
            }

            using (var texture = resource.QueryInterface<ID3D11Texture2D>())
            {
                _context.CopyResource(_staging, texture);
            }

            var mapped = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                var rowBytes = Width * 4;
                for (var y = 0; y < Height; y++)
                {
                    Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), destination, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _staging.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

/// <summary>Desktop Duplication 失效(解析度改變、安全桌面…),要丟掉重建。</summary>
internal sealed class DuplicationLostException : Exception;
