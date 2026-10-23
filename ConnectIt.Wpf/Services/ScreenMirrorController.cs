using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConnectIt.Wpf.Services;

public enum ScreenMirrorPhase
{
    Idle,

    /// <summary>我請對方分享他的螢幕,等對方同意。</summary>
    OutgoingRequest,

    /// <summary>我要分享我的螢幕給對方,等對方接受。</summary>
    OutgoingOffer,

    /// <summary>對方想看我的螢幕,等我同意。</summary>
    IncomingRequest,

    /// <summary>對方想分享他的螢幕給我看,等我接受。</summary>
    IncomingOffer,

    /// <summary>正在分享我的螢幕。</summary>
    Sharing,

    /// <summary>正在看對方的螢幕。</summary>
    Viewing,
}

/// <summary>螢幕鏡像目前的狀態快照。Width/Height 是畫面尺寸(分享中是我的、觀看中是對方的,還不知道時為 0)。</summary>
public sealed record ScreenMirrorStatus(
    ScreenMirrorPhase Phase,
    string? SessionId = null,
    bool AllowControl = false,
    int Width = 0,
    int Height = 0,
    string? Platform = null)
{
    public static readonly ScreenMirrorStatus Idle = new(ScreenMirrorPhase.Idle);
}

/// <summary>
/// 解碼好的一張畫面(Bgr32 像素)。<see cref="Pixels"/> 是從 ArrayPool 借來的,用完(寫進 WriteableBitmap 之後)
/// 一定要呼叫 <see cref="Release"/> 還回去。
/// </summary>
public sealed class DecodedScreenFrame(byte[] pixels, int width, int height, int stride)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    private int _released;

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            ArrayPool<byte>.Shared.Return(Pixels);
        }
    }
}

/// <summary>
/// 螢幕鏡像的狀態機(分享端與觀看端都在這裡),跑在已配對的 <see cref="ConnectionService"/> 連線上,
/// 協定見 <see cref="ScreenMessage"/>,與 Android 端 ScreenMirrorController.kt 一一對應。
///
/// 狀態轉換都在 <see cref="_sync"/> 鎖內完成(UI 操作、讀取迴圈送來的訊息來自不同執行緒),
/// 事件一律在鎖外觸發、而且是從背景執行緒觸發,UI 要自己切回 Dispatcher。
/// </summary>
public sealed class ScreenMirrorController : IDisposable
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>畫面有變化時最快約 60fps(Desktop Duplication 擷取 + 縮放 + JPEG 一張約 11ms)。</summary>
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>等桌面出現變化最多等多久;沒變化時不用一直空轉擷取。</summary>
    private const int CaptureWaitMs = 16;

    /// <summary>
    /// 游標位置回報的最小間隔:觀看端(Android 觸控板模式)用它讓畫面上的圓圈對準真正的游標,
    /// 包括分享端的人自己移動滑鼠的時候。
    /// </summary>
    private const long CursorReportIntervalMs = 100;

    private readonly ConnectionService _connection;
    private readonly object _sync = new();
    private readonly ScreenInputInjector _injector = new();

    private ScreenMirrorStatus _status = ScreenMirrorStatus.Idle;
    private bool _pendingOfferAllowControl;
    private CancellationTokenSource? _timeoutCts;

    // ---- 分享端 ----
    private CancellationTokenSource? _senderCts;
    private int _lastSentSeq;
    private int _lastAckedSeq;
    private readonly SemaphoreSlim _ackSignal = new(0);

    // ---- 觀看端 ----
    private int _framesDecoded;
    private long _fpsWindowStartedAt;

    public ScreenMirrorController(ConnectionService connection)
    {
        _connection = connection;
        _connection.ScreenMessageReceived += (_, message) => HandleMessage(message);
        _connection.ScreenFrameReceived += (_, payload) => HandleFrame(payload);
    }

    public ScreenMirrorStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>狀態改變(背景執行緒觸發)。</summary>
    public event EventHandler<ScreenMirrorStatus>? StatusChanged;

    /// <summary>給使用者看的訊息,例如「對方拒絕了」(背景執行緒觸發)。</summary>
    public event EventHandler<string>? Notice;

    /// <summary>觀看中解碼好一張畫面(背景執行緒觸發)。處理完一定要呼叫 <see cref="DecodedScreenFrame.Release"/>。</summary>
    public event EventHandler<DecodedScreenFrame>? FrameDecoded;

    /// <summary>觀看中每秒更新一次的實際畫面更新率。</summary>
    public event EventHandler<int>? FpsChanged;

    // ===================== UI 操作 =====================

    /// <summary>請對方分享他的螢幕給我看。</summary>
    public void RequestViewPeer()
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Idle || !_connection.IsConnected)
            {
                return;
            }

            id = NewSessionId();
            SetStatus(new ScreenMirrorStatus(ScreenMirrorPhase.OutgoingRequest, id));
            StartTimeout(id, "對方沒有回應。對方可能還沒按下同意,或使用的是不支援螢幕鏡像的舊版 ConnectIt。");
        }

        Send(new ScreenMessage { Type = ScreenMessage.Request, SessionId = id });
        RaiseStatusChanged();
    }

    /// <summary>邀請對方觀看我的螢幕。</summary>
    public void StartShareOffer(bool allowControl)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Idle || !_connection.IsConnected)
            {
                return;
            }

            id = NewSessionId();
            _pendingOfferAllowControl = allowControl;
            SetStatus(new ScreenMirrorStatus(ScreenMirrorPhase.OutgoingOffer, id));
            StartTimeout(id, "對方沒有回應。對方可能還沒按下接受,或使用的是不支援螢幕鏡像的舊版 ConnectIt。");
        }

        Send(new ScreenMessage { Type = ScreenMessage.Offer, SessionId = id });
        RaiseStatusChanged();
    }

    public void RespondToIncomingRequest(bool accept, bool allowControl)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.IncomingRequest)
            {
                return;
            }

            id = _status.SessionId!;
            if (!accept)
            {
                SetStatus(ScreenMirrorStatus.Idle);
            }
        }

        if (!accept)
        {
            Send(new ScreenMessage { Type = ScreenMessage.Reject, SessionId = id, Reason = ScreenRejectReason.Declined });
            RaiseStatusChanged();
            return;
        }

        StartSharing(id, allowControl);
    }

    public void RespondToIncomingOffer(bool accept)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.IncomingOffer)
            {
                return;
            }

            id = _status.SessionId!;
            if (accept)
            {
                BeginViewing(id);
            }
            else
            {
                SetStatus(ScreenMirrorStatus.Idle);
            }
        }

        Send(accept
            ? new ScreenMessage { Type = ScreenMessage.Accept, SessionId = id }
            : new ScreenMessage { Type = ScreenMessage.Reject, SessionId = id, Reason = ScreenRejectReason.Declined });
        RaiseStatusChanged();
    }

    /// <summary>取消還在等對方回應的請求/邀請。</summary>
    public void CancelOutgoing()
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase is not (ScreenMirrorPhase.OutgoingRequest or ScreenMirrorPhase.OutgoingOffer))
            {
                return;
            }

            id = _status.SessionId!;
            ResetLocked();
        }

        Send(new ScreenMessage { Type = ScreenMessage.Stop, SessionId = id });
        RaiseStatusChanged();
    }

    /// <summary>分享中切換是否允許對方操控。</summary>
    public void SetAllowControl(bool allow)
    {
        ScreenMirrorStatus updated;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Sharing || _status.AllowControl == allow)
            {
                return;
            }

            updated = _status with { AllowControl = allow };
            SetStatus(updated);
        }

        if (!allow)
        {
            _injector.ReleaseAll();
        }

        SendInfo(updated);
        RaiseStatusChanged();
    }

    /// <summary>停止分享/停止觀看。</summary>
    public void Stop()
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase is not (ScreenMirrorPhase.Sharing or ScreenMirrorPhase.Viewing))
            {
                return;
            }

            id = _status.SessionId!;
            ResetLocked();
        }

        Send(new ScreenMessage { Type = ScreenMessage.Stop, SessionId = id });
        RaiseStatusChanged();
    }

    /// <summary>觀看端:把使用者的滑鼠/鍵盤操作送給分享端(分享端沒有允許操控時不送)。</summary>
    public void SendInput(ScreenMessage input)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Viewing || !_status.AllowControl)
            {
                return;
            }

            id = _status.SessionId!;
        }

        Send(new ScreenMessage
        {
            Type = ScreenMessage.Input,
            SessionId = id,
            Action = input.Action,
            X = input.X,
            Y = input.Y,
            Button = input.Button,
            DeltaY = input.DeltaY,
            Key = input.Key,
            Text = input.Text,
        });
    }

    /// <summary>連線已經中斷(不論哪一方造成的):不能再送任何訊息,直接清理。</summary>
    public void OnConnectionClosed()
    {
        lock (_sync)
        {
            if (_status.Phase == ScreenMirrorPhase.Idle)
            {
                return;
            }

            ResetLocked();
        }

        RaiseStatusChanged();
    }

    // ===================== 收到對方的訊息 =====================

    private void HandleMessage(ScreenMessage message)
    {
        if (message.SessionId is not { } id)
        {
            return;
        }

        switch (message.Type)
        {
            case ScreenMessage.Ack:
                HandleAck(id, message.Seq);
                return;
            case ScreenMessage.Input:
                lock (_sync)
                {
                    if (_status.Phase != ScreenMirrorPhase.Sharing || _status.SessionId != id || !_status.AllowControl)
                    {
                        return;
                    }
                }

                _injector.Handle(message);
                return;
        }

        string? notice = null;
        ScreenMessage? reply = null;
        var startSharing = false;
        var allowControl = false;

        lock (_sync)
        {
            switch (message.Type)
            {
                case ScreenMessage.Request or ScreenMessage.Offer:
                    if (_status.Phase != ScreenMirrorPhase.Idle)
                    {
                        reply = new ScreenMessage { Type = ScreenMessage.Reject, SessionId = id, Reason = ScreenRejectReason.Busy };
                        break;
                    }

                    SetStatus(new ScreenMirrorStatus(
                        message.Type == ScreenMessage.Request ? ScreenMirrorPhase.IncomingRequest : ScreenMirrorPhase.IncomingOffer, id));
                    break;

                case ScreenMessage.Accept:
                    if (_status.Phase == ScreenMirrorPhase.OutgoingOffer && _status.SessionId == id)
                    {
                        startSharing = true;
                        allowControl = _pendingOfferAllowControl;
                    }

                    break;

                case ScreenMessage.Reject:
                    if (_status.Phase is ScreenMirrorPhase.OutgoingRequest or ScreenMirrorPhase.OutgoingOffer && _status.SessionId == id)
                    {
                        ResetLocked();
                        notice = DescribeReject(message.Reason);
                    }

                    break;

                case ScreenMessage.Info:
                    if (_status.Phase == ScreenMirrorPhase.OutgoingRequest && _status.SessionId == id)
                    {
                        BeginViewing(id);
                    }

                    if (_status.Phase == ScreenMirrorPhase.Viewing && _status.SessionId == id)
                    {
                        SetStatus(_status with
                        {
                            Width = message.Width ?? _status.Width,
                            Height = message.Height ?? _status.Height,
                            Platform = message.Platform ?? _status.Platform,
                            AllowControl = message.AllowControl ?? _status.AllowControl,
                        });
                    }

                    break;

                case ScreenMessage.Stop:
                    if (_status.SessionId == id)
                    {
                        var wasActive = _status.Phase is ScreenMirrorPhase.Sharing or ScreenMirrorPhase.Viewing;
                        ResetLocked();
                        notice = wasActive ? "對方已結束螢幕鏡像。" : "對方已取消螢幕鏡像的請求。";
                    }

                    break;

                default:
                    return;
            }
        }

        if (reply != null)
        {
            Send(reply);
            return;
        }

        if (startSharing)
        {
            StartSharing(id, allowControl);
            return;
        }

        RaiseStatusChanged();
        if (notice != null)
        {
            Notice?.Invoke(this, notice);
        }
    }

    private void HandleAck(string id, int? seq)
    {
        if (seq is not { } acked)
        {
            return;
        }

        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Sharing || _status.SessionId != id)
            {
                return;
            }

            if (acked > _lastAckedSeq)
            {
                _lastAckedSeq = acked;
            }
        }

        _ackSignal.Release();
    }

    /// <summary>收到一張畫面(讀取迴圈上呼叫)。解碼丟到背景執行緒,解完才回 ack,分享端就不會送得比我們解得快。</summary>
    private void HandleFrame(byte[] payload)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != ScreenMirrorPhase.Viewing || payload.Length <= ScreenMessage.FrameHeaderSize)
            {
                return;
            }

            id = _status.SessionId!;
        }

        var seq = BinaryPrimitives.ReadInt32BigEndian(payload);
        _ = Task.Run(() =>
        {
            DecodedScreenFrame? frame = null;
            try
            {
                frame = Decode(payload);
            }
            catch
            {
                // 壞掉的畫面就跳過,下一張還會來。
            }

            Send(new ScreenMessage { Type = ScreenMessage.Ack, SessionId = id, Seq = seq });

            if (frame == null)
            {
                return;
            }

            lock (_sync)
            {
                if (_status.Phase != ScreenMirrorPhase.Viewing || _status.SessionId != id)
                {
                    frame.Release();
                    return;
                }
            }

            CountFrame();
            var handler = FrameDecoded;
            if (handler == null)
            {
                frame.Release();
            }
            else
            {
                handler(this, frame);
            }
        });
    }

    /// <summary>JPEG → Bgr32 像素(放進 ArrayPool 借來的陣列,避免每張都配置好幾 MB 的大型物件)。</summary>
    public static DecodedScreenFrame Decode(byte[] payload)
    {
        using var stream = new MemoryStream(payload, ScreenMessage.FrameHeaderSize, payload.Length - ScreenMessage.FrameHeaderSize, writable: false);
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgr32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
        source.CopyPixels(pixels, stride, 0);
        return new DecodedScreenFrame(pixels, width, height, stride);
    }

    // ===================== 內部 =====================

    private void BeginViewing(string id)
    {
        CancelTimeoutLocked();
        _framesDecoded = 0;
        _fpsWindowStartedAt = Environment.TickCount64;
        SetStatus(new ScreenMirrorStatus(ScreenMirrorPhase.Viewing, id));
    }

    private void CountFrame()
    {
        int? fps = null;
        lock (_sync)
        {
            _framesDecoded++;
            var now = Environment.TickCount64;
            var elapsed = now - _fpsWindowStartedAt;
            if (elapsed >= 1000)
            {
                fps = (int)(_framesDecoded * 1000 / elapsed);
                _framesDecoded = 0;
                _fpsWindowStartedAt = now;
            }
        }

        if (fps is { } value)
        {
            FpsChanged?.Invoke(this, value);
        }
    }

    private void StartSharing(string id, bool allowControl)
    {
        var (screenWidth, screenHeight) = ScreenCaptureService.PrimaryScreenSize();
        var (width, height) = ScreenCaptureService.TargetSize(screenWidth, screenHeight);
        CancellationTokenSource cts;
        ScreenMirrorStatus sharing;
        lock (_sync)
        {
            CancelTimeoutLocked();
            _senderCts?.Cancel();
            cts = new CancellationTokenSource();
            _senderCts = cts;
            _lastSentSeq = 0;
            _lastAckedSeq = 0;
            sharing = new ScreenMirrorStatus(ScreenMirrorPhase.Sharing, id, allowControl, width, height, "windows");
            SetStatus(sharing);
        }

        // 先告訴觀看端畫面尺寸(對觀看請求來說這也代表「已同意」),不用等第一張畫面擷取成功。
        SendInfo(sharing);
        RaiseStatusChanged();
        _ = RunSenderAsync(id, width, height, cts.Token);
    }

    /// <summary>
    /// 傳送迴圈:還沒確認的畫面少於 <see cref="ScreenMessage.MaxFramesInFlight"/> 張時擷取一張,畫面有變化才送。
    /// 擷取到的尺寸跟已經告知觀看端的不同時(使用者改了解析度),送出前先補送 screen-info。
    /// </summary>
    private async Task RunSenderAsync(string id, int announcedWidth, int announcedHeight, CancellationToken token)
    {
        using var capture = new ScreenCaptureService();
        (double X, double Y)? reportedCursor = null;
        long cursorReportedAt = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var startedAt = Environment.TickCount64;

                while (Volatile.Read(ref _lastSentSeq) - Volatile.Read(ref _lastAckedSeq) >= ScreenMessage.MaxFramesInFlight)
                {
                    // 正常情況下 ack 很快就會回來;等太久(對方卡住)就當作已確認,避免整個畫面停住。
                    if (!await _ackSignal.WaitAsync(AckTimeout, token).ConfigureAwait(false))
                    {
                        Volatile.Write(ref _lastAckedSeq, Volatile.Read(ref _lastSentSeq));
                    }
                }

                var frame = await Task.Run(() => capture.Capture(waitMs: CaptureWaitMs), token).ConfigureAwait(false);
                if (frame is { } encoded && !token.IsCancellationRequested)
                {
                    if (encoded.Width != announcedWidth || encoded.Height != announcedHeight)
                    {
                        announcedWidth = encoded.Width;
                        announcedHeight = encoded.Height;
                        ScreenMirrorStatus? updated = null;
                        lock (_sync)
                        {
                            if (_status.Phase == ScreenMirrorPhase.Sharing && _status.SessionId == id)
                            {
                                updated = _status with { Width = encoded.Width, Height = encoded.Height };
                                SetStatus(updated);
                            }
                        }

                        if (updated == null)
                        {
                            return;
                        }

                        await SendInfoAsync(updated).ConfigureAwait(false);
                        RaiseStatusChanged();
                    }

                    if (capture.CursorPosition is { } cursor
                        && (reportedCursor is not { } last || Math.Abs(last.X - cursor.X) > 0.001 || Math.Abs(last.Y - cursor.Y) > 0.001)
                        && Environment.TickCount64 - cursorReportedAt >= CursorReportIntervalMs)
                    {
                        reportedCursor = cursor;
                        cursorReportedAt = Environment.TickCount64;
                        ScreenMirrorStatus? current;
                        lock (_sync)
                        {
                            current = _status.Phase == ScreenMirrorPhase.Sharing && _status.SessionId == id ? _status : null;
                        }

                        if (current != null)
                        {
                            await SendInfoAsync(current, cursor).ConfigureAwait(false);
                        }
                    }

                    var seq = Volatile.Read(ref _lastSentSeq) + 1;
                    BinaryPrimitives.WriteInt32BigEndian(encoded.Buffer, seq);
                    await _connection.SendScreenFrameAsync(encoded.Buffer.AsMemory(0, encoded.Length), token).ConfigureAwait(false);
                    Volatile.Write(ref _lastSentSeq, seq);
                }

                var spent = Environment.TickCount64 - startedAt;
                var wait = MinFrameInterval.TotalMilliseconds - spent;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止分享。
        }
        catch (Exception ex)
        {
            string? stoppedId = null;
            lock (_sync)
            {
                if (_status.Phase == ScreenMirrorPhase.Sharing && _status.SessionId == id)
                {
                    stoppedId = id;
                    ResetLocked();
                }
            }

            if (stoppedId != null)
            {
                Send(new ScreenMessage { Type = ScreenMessage.Stop, SessionId = stoppedId });
                RaiseStatusChanged();
                Notice?.Invoke(this, $"螢幕擷取失敗,已停止分享:{ex.Message}");
            }
        }
    }

    private void SendInfo(ScreenMirrorStatus sharing) => _ = SendInfoAsync(sharing);

    private Task SendInfoAsync(ScreenMirrorStatus sharing, (double X, double Y)? cursor = null) => SafeSendAsync(new ScreenMessage
    {
        Type = ScreenMessage.Info,
        SessionId = sharing.SessionId,
        Width = sharing.Width,
        Height = sharing.Height,
        Platform = "windows",
        AllowControl = sharing.AllowControl,
        X = cursor?.X,
        Y = cursor?.Y,
    });

    /// <summary>回到 Idle 並停止所有進行中的工作。必須在 <see cref="_sync"/> 鎖內呼叫。</summary>
    private void ResetLocked()
    {
        CancelTimeoutLocked();
        _senderCts?.Cancel();
        _senderCts = null;
        if (_status.Phase == ScreenMirrorPhase.Sharing)
        {
            _injector.ReleaseAll();
        }

        SetStatus(ScreenMirrorStatus.Idle);
    }

    private void StartTimeout(string id, string message)
    {
        CancelTimeoutLocked();
        var cts = new CancellationTokenSource();
        _timeoutCts = cts;
        _ = Task.Delay(PendingTimeout, cts.Token).ContinueWith(task =>
        {
            if (task.IsCanceled)
            {
                return;
            }

            lock (_sync)
            {
                if (_status.Phase is not (ScreenMirrorPhase.OutgoingRequest or ScreenMirrorPhase.OutgoingOffer) || _status.SessionId != id)
                {
                    return;
                }

                ResetLocked();
            }

            Send(new ScreenMessage { Type = ScreenMessage.Stop, SessionId = id });
            RaiseStatusChanged();
            Notice?.Invoke(this, message);
        }, TaskScheduler.Default);
    }

    private void CancelTimeoutLocked()
    {
        _timeoutCts?.Cancel();
        _timeoutCts = null;
    }

    private void SetStatus(ScreenMirrorStatus status) => _status = status;

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, Status);

    private void Send(ScreenMessage message) => _ = SafeSendAsync(message);

    private async Task SafeSendAsync(ScreenMessage message)
    {
        try
        {
            await _connection.SendScreenMessageAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // 寫入失敗代表連線已經斷了,ConnectionService 的監控迴圈會通知斷線。
        }
    }

    private static string DescribeReject(string? reason) => reason switch
    {
        ScreenRejectReason.Busy => "對方正在進行另一個螢幕鏡像,請稍後再試。",
        ScreenRejectReason.Unsupported => "對方的裝置不支援螢幕擷取。",
        ScreenRejectReason.PermissionDenied => "對方沒有允許擷取螢幕。",
        _ => "對方拒絕了螢幕鏡像。",
    };

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        lock (_sync)
        {
            ResetLocked();
        }

        _ackSignal.Dispose();
    }
}
