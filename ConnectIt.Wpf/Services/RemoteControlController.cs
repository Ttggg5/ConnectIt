namespace ConnectIt.Wpf.Services;

public enum RemoteControlPhase
{
    Idle,

    /// <summary>我送出遙控請求,等對方同意。</summary>
    OutgoingRequest,

    /// <summary>我正在遙控對方。</summary>
    Controlling,

    /// <summary>對方想遙控我,等我決定。</summary>
    IncomingRequest,

    /// <summary>對方正在遙控我。</summary>
    Controlled,
}

/// <param name="Platform">遙控中對方的平台("windows"/"android"),控制端依此決定顯示哪些按鈕。</param>
public sealed record RemoteControlStatus(RemoteControlPhase Phase, string? SessionId = null, PeerSession? Peer = null, string? Platform = null)
{
    public static readonly RemoteControlStatus Idle = new(RemoteControlPhase.Idle);
}

/// <summary>
/// 遙控器:不用開螢幕鏡像,只把按鍵(媒體鍵、音量、簡報翻頁、快捷鍵)送到另一台裝置,協定見
/// <see cref="ScreenMessage.RemoteRequest"/>。對方要先同意(信任的裝置可以由畫面自動同意);同一時間只有一個遙控工作階段,
/// 跟螢幕鏡像互相獨立(鏡像中也可以另外開遙控器)。受控端是 Windows 時任何按鍵都用 SendInput 注入;
/// Android 受控端只支援媒體鍵與音量(Android 不允許 App 注入任意按鍵)。
/// </summary>
public sealed class RemoteControlController : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ConnectionService _connection;
    private readonly ScreenInputInjector _injector = new();
    private readonly object _sync = new();
    private RemoteControlStatus _status = RemoteControlStatus.Idle;
    private CancellationTokenSource? _timeoutCts;

    public RemoteControlController(ConnectionService connection)
    {
        _connection = connection;
        _connection.ScreenMessageReceived += (sender, message) => HandleMessage((PeerSession)sender!, message);
        _connection.SessionClosed += (_, e) => OnConnectionClosed(e.Session);
    }

    public RemoteControlStatus Status
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
    public event EventHandler<RemoteControlStatus>? StatusChanged;

    /// <summary>給使用者看的訊息,例如「對方拒絕了」(背景執行緒觸發)。</summary>
    public event EventHandler<string>? Notice;

    // ===================== 控制端 =====================

    /// <summary>請求遙控 <paramref name="peer"/>。</summary>
    public void RequestControl(PeerSession peer)
    {
        string id;
        lock (_sync)
        {
            if (_status.Phase != RemoteControlPhase.Idle || peer.IsClosed)
            {
                return;
            }

            id = Guid.NewGuid().ToString("N");
            _status = new RemoteControlStatus(RemoteControlPhase.OutgoingRequest, id, peer);
            StartTimeoutLocked(id);
        }

        Send(peer, new ScreenMessage { Type = ScreenMessage.RemoteRequest, SessionId = id });
        RaiseStatusChanged();
    }

    /// <summary>遙控中送出一個按鍵(按下再放開)。</summary>
    public void SendKey(string key) => SendShortcut(key);

    /// <summary>遙控中送出組合鍵:依序按下,再反過來依序放開(例如 Control、c)。</summary>
    public void SendShortcut(params string[] keys)
    {
        RemoteControlStatus status;
        lock (_sync)
        {
            status = _status;
        }

        if (status is not { Phase: RemoteControlPhase.Controlling, Peer: { } peer, SessionId: { } id } || keys.Length == 0)
        {
            return;
        }

        foreach (var key in keys)
        {
            Send(peer, new ScreenMessage { Type = ScreenMessage.Input, SessionId = id, Action = ScreenInputAction.KeyDown, Key = key });
        }

        foreach (var key in keys.Reverse())
        {
            Send(peer, new ScreenMessage { Type = ScreenMessage.Input, SessionId = id, Action = ScreenInputAction.KeyUp, Key = key });
        }
    }

    // ===================== 受控端 =====================

    public void RespondToIncomingRequest(bool accept)
    {
        RemoteControlStatus status;
        lock (_sync)
        {
            status = _status;
            if (status.Phase != RemoteControlPhase.IncomingRequest)
            {
                return;
            }

            _status = accept ? status with { Phase = RemoteControlPhase.Controlled } : RemoteControlStatus.Idle;
        }

        Send(status.Peer!, accept
            ? new ScreenMessage { Type = ScreenMessage.RemoteAccept, SessionId = status.SessionId, Platform = "windows" }
            : new ScreenMessage { Type = ScreenMessage.RemoteReject, SessionId = status.SessionId, Reason = ScreenRejectReason.Declined });
        RaiseStatusChanged();
    }

    // ===================== 兩端共用 =====================

    /// <summary>結束遙控(不論是控制端還是受控端),或取消還在等對方回應的請求。</summary>
    public void Stop()
    {
        RemoteControlStatus status;
        lock (_sync)
        {
            status = _status;
            if (status.Phase == RemoteControlPhase.Idle)
            {
                return;
            }

            ResetLocked();
        }

        _injector.ReleaseAll();
        if (status.Phase != RemoteControlPhase.IncomingRequest)
        {
            Send(status.Peer!, new ScreenMessage { Type = ScreenMessage.RemoteStop, SessionId = status.SessionId });
        }
        else
        {
            Send(status.Peer!, new ScreenMessage { Type = ScreenMessage.RemoteReject, SessionId = status.SessionId, Reason = ScreenRejectReason.Declined });
        }

        RaiseStatusChanged();
    }

    private void OnConnectionClosed(PeerSession session)
    {
        lock (_sync)
        {
            if (_status.Phase == RemoteControlPhase.Idle || _status.Peer != session)
            {
                return;
            }

            ResetLocked();
        }

        _injector.ReleaseAll();
        RaiseStatusChanged();
    }

    private void HandleMessage(PeerSession sender, ScreenMessage message)
    {
        if (message.SessionId is not { } id)
        {
            return;
        }

        string? notice = null;
        var changed = false;
        lock (_sync)
        {
            switch (message.Type)
            {
                case ScreenMessage.RemoteRequest:
                    if (_status.Phase != RemoteControlPhase.Idle)
                    {
                        Send(sender, new ScreenMessage { Type = ScreenMessage.RemoteReject, SessionId = id, Reason = ScreenRejectReason.Busy });
                        return;
                    }

                    _status = new RemoteControlStatus(RemoteControlPhase.IncomingRequest, id, sender);
                    changed = true;
                    break;

                case ScreenMessage.RemoteAccept when _status is { Phase: RemoteControlPhase.OutgoingRequest } && _status.SessionId == id && _status.Peer == sender:
                    CancelTimeoutLocked();
                    _status = _status with { Phase = RemoteControlPhase.Controlling, Platform = message.Platform };
                    changed = true;
                    break;

                case ScreenMessage.RemoteReject when _status is { Phase: RemoteControlPhase.OutgoingRequest } && _status.SessionId == id && _status.Peer == sender:
                    ResetLocked();
                    notice = message.Reason == ScreenRejectReason.Busy
                        ? "對方正在被其他裝置遙控,請稍後再試。"
                        : "對方拒絕了遙控請求。";
                    changed = true;
                    break;

                case ScreenMessage.RemoteStop when _status.SessionId == id && _status.Peer == sender:
                    var wasActive = _status.Phase is RemoteControlPhase.Controlling or RemoteControlPhase.Controlled;
                    ResetLocked();
                    notice = wasActive ? "遙控已結束。" : "對方取消了遙控請求。";
                    changed = true;
                    break;

                case ScreenMessage.Input when _status is { Phase: RemoteControlPhase.Controlled } && _status.SessionId == id && _status.Peer == sender:
                    // 遙控器只送按鍵;滑鼠操作要透過螢幕鏡像(而且要分享端允許操控)。
                    if (message.Action is ScreenInputAction.KeyDown or ScreenInputAction.KeyUp)
                    {
                        _injector.Handle(message);
                    }

                    return;

                default:
                    return;
            }
        }

        if (!changed)
        {
            return;
        }

        if (Status.Phase == RemoteControlPhase.Idle)
        {
            _injector.ReleaseAll();
        }

        if (notice != null)
        {
            Notice?.Invoke(this, notice);
        }

        RaiseStatusChanged();
    }

    private void StartTimeoutLocked(string id)
    {
        CancelTimeoutLocked();
        var cts = new CancellationTokenSource();
        _timeoutCts = cts;
        _ = Task.Delay(RequestTimeout, cts.Token).ContinueWith(task =>
        {
            if (task.IsCanceled)
            {
                return;
            }

            PeerSession? peer;
            lock (_sync)
            {
                if (_status.Phase != RemoteControlPhase.OutgoingRequest || _status.SessionId != id)
                {
                    return;
                }

                peer = _status.Peer;
                ResetLocked();
            }

            if (peer != null)
            {
                Send(peer, new ScreenMessage { Type = ScreenMessage.RemoteStop, SessionId = id });
            }

            Notice?.Invoke(this, "對方沒有回應遙控請求。對方可能還沒按下同意,或使用的是不支援遙控器的舊版 ConnectIt。");
            RaiseStatusChanged();
        }, TaskScheduler.Default);
    }

    private void CancelTimeoutLocked()
    {
        _timeoutCts?.Cancel();
        _timeoutCts = null;
    }

    private void ResetLocked()
    {
        CancelTimeoutLocked();
        _status = RemoteControlStatus.Idle;
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, Status);

    private static void Send(PeerSession peer, ScreenMessage message) => _ = SafeSendAsync(peer, message);

    private static async Task SafeSendAsync(PeerSession peer, ScreenMessage message)
    {
        try
        {
            await peer.SendScreenMessageAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // 連線斷了,交給 SessionClosed 處理。
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CancelTimeoutLocked();
        }

        _injector.ReleaseAll();
    }
}
