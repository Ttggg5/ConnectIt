using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 負責 mDNS 找到裝置之後的實際 TCP 連線,包含連線請求/接受/拒絕的交握協定:
/// 發起端連上後先送出 "request",對方要等使用者確認才會回 "accept"/"reject"。
/// 交握成功後,每台裝置各有一個 <see cref="PeerSession"/>(同一個 socket 改用訊框協定傳輸檔案、
/// 螢幕鏡像等),所以可以同時連線多台裝置。各 session 的事件會轉發到這裡(sender 是該 <see cref="PeerSession"/>),
/// UI 只需要訂閱這個類別。
/// </summary>
public sealed class ConnectionService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    // TcpClient.ConnectAsync() 本身沒有逾時機制——如果對方位址不可達(例如撿到一個實際上
    // 不可路由的位址),很多網路環境會直接把 SYN 封包丟掉而不回 RST,這個 await 就會永遠掛著,
    // 使用者會覺得「按下去完全沒反應」卻連個錯誤訊息都看不到。額外套一個逾時,至少能明確
    // 回報「連線逾時」讓使用者知道發生了什麼事。可在設定頁調整(見 ConnectionSettingsService)。
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>單一訊框的資料長度上限(扣掉 1 個型別 byte),螢幕畫面的 JPEG 也必須壓在這之內。</summary>
    public const int MaxFramePayloadSize = PeerSession.MaxFramePayloadSize;

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;

    private readonly object _sessionsLock = new();
    private readonly List<PeerSession> _sessions = [];

    public int Port { get; private set; }

    /// <summary>這台裝置的穩定識別碼(見 <see cref="DeviceSettingsService.DeviceId"/>),交握時送給對方。</summary>
    public string LocalDeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>新建立的連線使用的心跳間隔。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>新建立的連線使用的心跳逾時。</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>目前所有已連線的裝置(快照)。</summary>
    public IReadOnlyList<PeerSession> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.ToArray();
            }
        }
    }

    public bool IsConnected => Sessions.Count > 0;

    public bool IsFileTransferActive => Sessions.Any(s => s.IsFileTransferActive);

    public PeerSession? FindSession(string peerId) => Sessions.FirstOrDefault(s => s.PeerId == peerId);

    /// <summary>有裝置送出連線請求,需要使用者確認。</summary>
    public event EventHandler<ConnectionRequestedEventArgs>? ConnectionRequested;

    /// <summary>連線交握完成(不論是發起方還是被連線方)。</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    /// <summary>某一台裝置的連線結束(不論是對方斷線還是本機中斷),sender 是該 <see cref="PeerSession"/>。</summary>
    public event EventHandler<SessionClosedEventArgs>? SessionClosed;

    /// <summary>對方關閉了連線(不是本機呼叫 Disconnect() 造成的),sender 是該 <see cref="PeerSession"/>。</summary>
    public event EventHandler? RemoteDisconnected;

    public event EventHandler<string>? StatusChanged;

    // 以下事件從各個 PeerSession 轉發過來,sender 是觸發事件的那個 PeerSession。
    public event EventHandler<FileOfferedEventArgs>? FileOffered;
    public event EventHandler<FolderOfferedEventArgs>? FolderOffered;
    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;
    public event EventHandler<FileTransferEndedEventArgs>? FileTransferEnded;
    public event EventHandler<FolderTransferEndedEventArgs>? FolderTransferEnded;
    public event EventHandler<ScreenMessage>? ScreenMessageReceived;
    public event EventHandler<byte[]>? ScreenFrameReceived;
    public event EventHandler<byte[]>? ScreenAudioReceived;

    /// <summary>開始監聽,回傳實際使用的連接埠(0 代表由系統挑選)。</summary>
    public int StartListening(int preferredPort = 0)
    {
        StopListening();

        try
        {
            _listener = new TcpListener(IPAddress.Any, preferredPort);
            _listener.Start();
        }
        catch (SocketException) when (preferredPort != 0)
        {
            StatusChanged?.Invoke(this, $"連接埠 {preferredPort} 無法使用,已改用系統自動指派的連接埠。");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptCts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _acceptCts.Token);

        StatusChanged?.Invoke(this, $"已開始監聽連接埠 {Port}。");
        return Port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(this, $"監聽發生錯誤:{ex.Message}");
                break;
            }

            _ = HandleIncomingAsync(client, token);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        StatusChanged?.Invoke(this, $"收到來自 {remoteAddress} 的連線嘗試。");
        client.NoDelay = true;

        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var (_, line) = await ReadLineWithTimeoutAsync(reader, RequestTimeout, token).ConfigureAwait(false);

            ConnectMessage? request = null;
            if (line != null)
            {
                try
                {
                    request = JsonSerializer.Deserialize<ConnectMessage>(line);
                }
                catch (JsonException ex)
                {
                    StatusChanged?.Invoke(this, $"連線內容格式錯誤,已忽略:{ex.Message}");
                }
            }
            else
            {
                StatusChanged?.Invoke(this, "等待對方送出連線請求逾時。");
            }

            if (request is not { Type: "request" })
            {
                if (line != null)
                {
                    StatusChanged?.Invoke(this, $"收到非連線請求的內容,已忽略:{line}");
                }

                client.Close();
                return;
            }

            var requesterName = string.IsNullOrWhiteSpace(request.DeviceName)
                ? remoteAddress.ToString()
                : request.DeviceName;

            var tcs = new TaskCompletionSource<bool>();
            ConnectionRequested?.Invoke(this, new ConnectionRequestedEventArgs
            {
                RequesterName = requesterName,
                RequesterAddress = remoteAddress,
                RequesterDeviceId = request.DeviceId,
                Respond = accepted => tcs.TrySetResult(accepted),
            });

            var accepted2 = await tcs.Task.ConfigureAwait(false);

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = accepted2 ? "accept" : "reject",
                DeviceId = LocalDeviceId,
            })).ConfigureAwait(false);

            if (!accepted2)
            {
                client.Close();
                return;
            }

            AddSession(new PeerSession(client, requesterName, remoteAddress, ConnectionRole.Acceptor, request.DeviceId));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"處理連入連線時發生錯誤:{ex.Message}");
            client.Close();
        }
    }

    /// <summary>主動連線到指定裝置並送出連線請求,等待對方確認。只關心成功與否時用這個;
    /// 要在畫面上說明失敗原因(被拒絕/逾時/連不到/已取消)請用 <see cref="RequestConnectionWithOutcomeAsync"/>。</summary>
    public async Task<bool> RequestConnectionAsync(
        IPAddress address, int port, string myDeviceName, string remoteDisplayName, CancellationToken cancellationToken = default)
    {
        var outcome = await RequestConnectionWithOutcomeAsync(address, port, myDeviceName, remoteDisplayName, cancellationToken)
            .ConfigureAwait(false);
        return outcome == ConnectionRequestOutcome.Accepted;
    }

    /// <summary>主動連線到指定裝置並送出連線請求,等待對方確認,回傳詳細的結果供 UI 顯示對應的說明。
    /// <paramref name="cancellationToken"/> 取消時(使用者在「等待對方接受」對話框按了取消)回傳
    /// <see cref="ConnectionRequestOutcome.Cancelled"/>,並關閉這條尚未建立完成的連線。</summary>
    public async Task<ConnectionRequestOutcome> RequestConnectionWithOutcomeAsync(
        IPAddress address, int port, string myDeviceName, string remoteDisplayName, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(address, port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    StatusChanged?.Invoke(this, $"已取消對 {remoteDisplayName} 的連線請求。");
                    client.Close();
                    return ConnectionRequestOutcome.Cancelled;
                }
                catch (OperationCanceledException)
                {
                    StatusChanged?.Invoke(this, $"連線失敗:連線逾時({remoteDisplayName} 可能離線或位址不可達)。");
                    client.Close();
                    return ConnectionRequestOutcome.Unreachable;
                }
                catch (SocketException ex)
                {
                    StatusChanged?.Invoke(this, $"連線失敗:{ex.Message}");
                    client.Close();
                    return ConnectionRequestOutcome.Unreachable;
                }
            }

            client.NoDelay = true;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            StatusChanged?.Invoke(this, $"已送出連線請求給 {remoteDisplayName},等待對方確認...");

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = "request",
                DeviceName = myDeviceName,
                DeviceId = LocalDeviceId,
            })).ConfigureAwait(false);

            var (timedOut, line) = await ReadLineWithTimeoutAsync(reader, ResponseTimeout, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                StatusChanged?.Invoke(this, $"已取消對 {remoteDisplayName} 的連線請求。");
                client.Close();
                return ConnectionRequestOutcome.Cancelled;
            }

            var response = line != null ? JsonSerializer.Deserialize<ConnectMessage>(line) : null;

            if (response is { Type: "accept" })
            {
                AddSession(new PeerSession(client, remoteDisplayName, address, ConnectionRole.Initiator, response.DeviceId));
                return ConnectionRequestOutcome.Accepted;
            }

            StatusChanged?.Invoke(this, timedOut
                ? $"連線逾時,{remoteDisplayName} 沒有回應。"
                : $"{remoteDisplayName} 拒絕了連線請求。");
            client.Close();
            return timedOut ? ConnectionRequestOutcome.Timeout : ConnectionRequestOutcome.Rejected;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"連線失敗:{ex.Message}");
            client.Close();
            return cancellationToken.IsCancellationRequested ? ConnectionRequestOutcome.Cancelled : ConnectionRequestOutcome.Unreachable;
        }
    }

    /// <summary>讀取一行回應,最多等 <paramref name="timeout"/>。回傳 TimedOut=true 代表時間到(或被取消)
    /// 還沒收到任何東西;TimedOut=false 而 Line 為 null 代表對方直接把連線關掉了。</summary>
    private static async Task<(bool TimedOut, string? Line)> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken token)
    {
        var readTask = reader.ReadLineAsync();
        var delayTask = Task.Delay(timeout, token);

        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        return completed == readTask ? (false, await readTask.ConfigureAwait(false)) : (true, null);
    }

    /// <summary>
    /// 登記一條剛完成交握的連線並開始監控。同一台裝置(相同 DeviceId)如果還留著一條舊連線——通常是對方
    /// 斷線後重新連線,舊連線還沒被心跳判定斷線——先把舊的關掉,避免同一台裝置出現兩次。
    /// </summary>
    private void AddSession(PeerSession session)
    {
        session.HeartbeatInterval = HeartbeatInterval;
        session.HeartbeatTimeout = HeartbeatTimeout;

        PeerSession[] stale;
        lock (_sessionsLock)
        {
            stale = session.RemoteDeviceId == null
                ? []
                : _sessions.Where(s => s.RemoteDeviceId == session.RemoteDeviceId).ToArray();
            _sessions.Add(session);
        }

        foreach (var old in stale)
        {
            old.Disconnect();
        }

        session.StatusChanged += (_, message) => StatusChanged?.Invoke(session, message);
        session.FileOffered += (_, e) => FileOffered?.Invoke(session, e);
        session.FolderOffered += (_, e) => FolderOffered?.Invoke(session, e);
        session.FileTransferProgress += (_, e) => FileTransferProgress?.Invoke(session, e);
        session.FileTransferEnded += (_, e) => FileTransferEnded?.Invoke(session, e);
        session.FolderTransferEnded += (_, e) => FolderTransferEnded?.Invoke(session, e);
        session.ScreenMessageReceived += (_, e) => ScreenMessageReceived?.Invoke(session, e);
        session.ScreenFrameReceived += (_, e) => ScreenFrameReceived?.Invoke(session, e);
        session.ScreenAudioReceived += (_, e) => ScreenAudioReceived?.Invoke(session, e);
        session.Closed += (_, byRemote) =>
        {
            lock (_sessionsLock)
            {
                _sessions.Remove(session);
            }

            SessionClosed?.Invoke(session, new SessionClosedEventArgs { Session = session, ByRemote = byRemote });
            if (byRemote)
            {
                RemoteDisconnected?.Invoke(session, EventArgs.Empty);
            }
        };

        // 先通知 UI 有新連線,再開始讀取:不然對方很快送來的訊息(例如檔案提議)或斷線,
        // 可能比「已連線」更早被 UI 處理到,UI 會找不到這台裝置。
        Connected?.Invoke(this, new ConnectedEventArgs
        {
            Session = session,
            RemoteName = session.RemoteName,
            RemoteAddress = session.RemoteAddress,
            Role = session.Role,
        });
        session.Start();
    }

    /// <summary>接收到 <see cref="FileOffered"/> 之後,使用者確認是否接受這個檔案時呼叫(依 TransferId 找到對應的連線)。</summary>
    public void RespondToFileOffer(string transferId, bool accept, string downloadFolder) =>
        Sessions.FirstOrDefault(s => s.HasPendingOffer(transferId))?.RespondToFileOffer(transferId, accept, downloadFolder);

    /// <summary>接收到 <see cref="FolderOffered"/> 之後,使用者確認是否接受整個資料夾時呼叫(依 TransferId 找到對應的連線)。</summary>
    public void RespondToFolderOffer(string folderTransferId, bool accept, string downloadFolder) =>
        Sessions.FirstOrDefault(s => s.HasPendingOffer(folderTransferId))?.RespondToFolderOffer(folderTransferId, accept, downloadFolder);

    /// <summary>中斷指定裝置的連線。</summary>
    public void Disconnect(string peerId) => FindSession(peerId)?.Disconnect();

    /// <summary>中斷所有連線(不影響監聽,對方可以再送新的連線請求)。</summary>
    public void Disconnect()
    {
        foreach (var session in Sessions)
        {
            session.Disconnect();
        }
    }

    public void StopListening()
    {
        _acceptCts?.Cancel();
        _acceptCts?.Dispose();
        _acceptCts = null;

        _listener?.Stop();
        _listener = null;
    }

    public void Dispose()
    {
        Disconnect();
        StopListening();
    }
}

/// <summary>某一台裝置的連線結束。</summary>
public sealed class SessionClosedEventArgs : EventArgs
{
    public required PeerSession Session { get; init; }

    /// <summary>true 代表是對方斷線或心跳逾時,false 代表本機主動中斷。</summary>
    public required bool ByRemote { get; init; }
}
