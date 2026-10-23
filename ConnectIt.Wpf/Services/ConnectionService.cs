using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 負責 mDNS 找到裝置之後的實際 TCP 連線,包含連線請求/接受/拒絕的交握協定:
/// 發起端連上後先送出 "request",對方要等使用者確認才會回 "accept"/"reject"。
/// 連線建立後,同一個 socket 也用來傳輸檔案(見檔案下方「檔案傳輸」區塊)。
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

    // 訊框長度前綴的長度(4 bytes),以及單一訊框的長度上限(1 型別 byte + 資料),
    // 避免對方送出異常大的長度值時整個配置一大塊記憶體。
    private const int FrameLengthPrefixSize = 4;
    private const int MaxFrameSize = 1 * 1024 * 1024;
    private const int FileChunkSize = 64 * 1024;

    private static class FrameType
    {
        public const byte Control = 0;
        public const byte Chunk = 1;
        public const byte ScreenControl = 2;
        public const byte ScreenFrame = 3;
        public const byte ScreenAudio = 4;
    }

    /// <summary>單一訊框的資料長度上限(扣掉 1 個型別 byte),螢幕畫面的 JPEG 也必須壓在這之內。</summary>
    public const int MaxFramePayloadSize = MaxFrameSize - 1;

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;

    private TcpClient? _activeClient;
    private CancellationTokenSource? _monitorCts;

    // 心跳:連線期間每隔 HeartbeatInterval 送一個 "ping" 控制訊息;只要確認過對方也會送心跳
    // (收到過對方的 ping),超過 HeartbeatTimeout 完全沒收到對方任何訊框,就判定連線已經斷了。
    // 用來偵測「對方沒有正常關閉連線」的情況(程式當掉、直接關機、Wi-Fi 斷掉…)——這時不會有
    // TCP FIN,讀取會一直卡著,不靠心跳就永遠不知道對方已經不在了。
    // 只在對方也送心跳時才啟用逾時判斷,舊版(不送心跳)的對方不會因為閒置太久被誤判斷線。
    private long _lastReceivedAtMs;
    private volatile bool _peerSendsHeartbeat;
    private int _heartbeatWriteInFlight;

    /// <summary>送出心跳的間隔。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>超過這麼久沒收到對方任何訊框(而且對方會送心跳)就判定連線中斷。</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    // 保護「送出訊框」這個動作本身,避免背景的檔案傳送迴圈跟使用者觸發的取消/控制訊息同時寫入同一個 socket,
    // 導致兩段內容在網路上交錯、對方解析出不完整的訊框。
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ActiveTransfer? _activeTransfer;
    private FolderSession? _activeFolderSession;

    public int Port { get; private set; }

    public bool IsConnected => _activeClient != null;

    public bool IsFileTransferActive => _activeTransfer != null || _activeFolderSession != null;

    /// <summary>有裝置送出連線請求,需要使用者確認。</summary>
    public event EventHandler<ConnectionRequestedEventArgs>? ConnectionRequested;

    /// <summary>連線交握完成(不論是發起方還是被連線方)。</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    /// <summary>對方關閉了連線(不是本機呼叫 Disconnect() 造成的)。</summary>
    public event EventHandler? RemoteDisconnected;

    public event EventHandler<string>? StatusChanged;

    /// <summary>對方想要傳送檔案給我們,需要使用者確認。</summary>
    public event EventHandler<FileOfferedEventArgs>? FileOffered;

    /// <summary>對方想要傳送整個資料夾給我們,需要使用者確認。</summary>
    public event EventHandler<FolderOfferedEventArgs>? FolderOffered;

    /// <summary>檔案傳輸進度更新(傳送中或接收中皆會觸發)。</summary>
    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;

    /// <summary>檔案傳輸結束,不論成功、被拒絕、被取消或失敗。</summary>
    public event EventHandler<FileTransferEndedEventArgs>? FileTransferEnded;

    /// <summary>資料夾傳輸結束,不論成功、被拒絕、被取消或失敗。</summary>
    public event EventHandler<FolderTransferEndedEventArgs>? FolderTransferEnded;

    /// <summary>收到螢幕鏡像控制訊息(見 <see cref="ScreenMessage"/>)。在讀取迴圈上觸發,處理時不能阻塞太久。</summary>
    public event EventHandler<ScreenMessage>? ScreenMessageReceived;

    /// <summary>收到一張螢幕畫面:[序號 4 bytes][JPEG]。在讀取迴圈上觸發,解碼請另外開工作。</summary>
    public event EventHandler<byte[]>? ScreenFrameReceived;

    /// <summary>收到一段螢幕鏡像的聲音(16-bit PCM)。在讀取迴圈上觸發,不能阻塞太久。</summary>
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
                Respond = accepted => tcs.TrySetResult(accepted),
            });

            var accepted2 = await tcs.Task.ConfigureAwait(false);

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = accepted2 ? "accept" : "reject",
            })).ConfigureAwait(false);

            if (!accepted2)
            {
                client.Close();
                return;
            }

            _activeClient = client;
            StartMonitoring(client);
            Connected?.Invoke(this, new ConnectedEventArgs
            {
                RemoteName = requesterName,
                RemoteAddress = remoteAddress,
                Role = ConnectionRole.Acceptor,
            });
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
                _activeClient = client;
                StartMonitoring(client);
                Connected?.Invoke(this, new ConnectedEventArgs
                {
                    RemoteName = remoteDisplayName,
                    RemoteAddress = address,
                    Role = ConnectionRole.Initiator,
                });
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
    /// 連線建立後,持續在背景讀取這個 socket:一方面用來偵測「對方把連線關掉」,
    /// 一方面把收到的訊框(控制訊息/檔案內容)分派給對應的處理邏輯。
    /// </summary>
    private void StartMonitoring(TcpClient client)
    {
        _monitorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _monitorCts = cts;

        _peerSendsHeartbeat = false;
        Volatile.Write(ref _lastReceivedAtMs, Environment.TickCount64);
        EnableTcpKeepAlive(client);

        _ = MonitorConnectionAsync(client, cts.Token);
        _ = HeartbeatLoopAsync(client, cts.Token);
    }

    /// <summary>
    /// 作業系統層級的保險:就算對方是不送心跳的舊版本,TCP keepalive 也會在對方消失約 20 秒後
    /// 讓讀取失敗,觸發一般的斷線處理。部分平台不支援調整這些參數,失敗就維持系統預設值。
    /// </summary>
    private static void EnableTcpKeepAlive(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch
        {
            // 不支援就算了,還有應用層心跳。
        }
    }

    private async Task HeartbeatLoopAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);

                var silentMs = Environment.TickCount64 - Volatile.Read(ref _lastReceivedAtMs);
                if (_peerSendsHeartbeat && silentMs > HeartbeatTimeout.TotalMilliseconds)
                {
                    StatusChanged?.Invoke(this, $"超過 {(int)HeartbeatTimeout.TotalSeconds} 秒沒有收到對方的回應,判定連線已中斷。");
                    // 關掉 socket 讓卡在讀取的監控迴圈丟出例外,走一般的「對方斷線」流程(清理傳輸、通知 UI)。
                    client.Close();
                    return;
                }

                // 不 await:對方已經不在的話,送出可能卡在寫入(傳送緩衝區滿了),不能因此擋住上面的逾時判斷。
                if (Interlocked.Exchange(ref _heartbeatWriteInFlight, 1) == 0)
                {
                    _ = SendHeartbeatAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 連線結束(本機斷線或監控迴圈結束),不用再送心跳。
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken token)
    {
        try
        {
            await WriteControlAsync(new FileControlMessage { Type = HeartbeatMessageType }, token).ConfigureAwait(false);
        }
        catch
        {
            // 寫入失敗代表連線已經有問題,交給監控迴圈/逾時判斷處理。
        }
        finally
        {
            Volatile.Write(ref _heartbeatWriteInFlight, 0);
        }
    }

    private const string HeartbeatMessageType = "ping";

    private async Task MonitorConnectionAsync(TcpClient client, CancellationToken token)
    {
        var remoteClosed = false;

        try
        {
            var stream = client.GetStream();

            while (!token.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (frame == null)
                {
                    remoteClosed = true;
                    break;
                }

                Volatile.Write(ref _lastReceivedAtMs, Environment.TickCount64);

                var (type, payload) = frame.Value;
                if (type == FrameType.Control)
                {
                    HandleControlFrame(payload);
                }
                else if (type == FrameType.Chunk)
                {
                    HandleChunkFrame(payload);
                }
                else if (type == FrameType.ScreenControl)
                {
                    if (ScreenMessage.Parse(payload) is { } screenMessage)
                    {
                        ScreenMessageReceived?.Invoke(this, screenMessage);
                    }
                }
                else if (type == FrameType.ScreenFrame)
                {
                    ScreenFrameReceived?.Invoke(this, payload);
                }
                else if (type == FrameType.ScreenAudio)
                {
                    ScreenAudioReceived?.Invoke(this, payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 是本機呼叫 Disconnect() 取消的,不算對方斷線
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                remoteClosed = true;
            }
        }

        // 不論是對方斷線、本機主動斷線還是發生例外,只要還有進行中的檔案/資料夾傳輸就必須清乾淨
        // (關閉/刪除尚未寫完的接收檔案),避免留下殘破的檔案或卡住的狀態。
        AbortActiveTransfer(FileTransferEndReason.ConnectionClosed);
        AbortActiveFolderSession(FileTransferEndReason.ConnectionClosed);

        if (remoteClosed)
        {
            _activeClient = null;
            StatusChanged?.Invoke(this, "對方已中斷連線。");
            RemoteDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>中斷目前的連線(不影響監聽,對方可以再送新的連線請求)。</summary>
    public void Disconnect()
    {
        _monitorCts?.Cancel();
        _monitorCts = null;
        _activeClient?.Close();
        _activeClient = null;
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
        _writeLock.Dispose();
    }

    // ===================== 訊框協定(讀取/寫入) =====================
    //
    // 連線建立後的 socket 用一個簡單的長度前綴訊框協定傳輸資料:
    //   [4 bytes big-endian 長度(= 1 個型別 byte + 資料長度)][1 byte 型別][資料]
    // 型別 0 = 控制訊息(UTF8 JSON,見 FileControlMessage),型別 1 = 檔案內容區塊(原始 bytes)。

    private async Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        var stream = _activeClient?.GetStream();
        if (stream == null)
        {
            return;
        }

        // 把 header/type/payload 合併成單一緩衝區一次寫入,避免拆成多個小封包各自等待
        // ACK(TCP Nagle 演算法 + 對方的 delayed ACK 交互作用下,每個小封包都可能多花數十毫秒)。
        var frame = new byte[FrameLengthPrefixSize + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[FrameLengthPrefixSize] = type;
        payload.CopyTo(frame.AsMemory(FrameLengthPrefixSize + 1));

        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch
        {
            // 寫入失敗通常代表連線已經斷了,交給背景的監控迴圈去處理斷線通知即可。
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task WriteControlAsync(FileControlMessage message, CancellationToken token = default) =>
        WriteFrameAsync(FrameType.Control, JsonSerializer.SerializeToUtf8Bytes(message), token);

    /// <summary>送出螢幕鏡像控制訊息,沒有連線時直接忽略。</summary>
    public Task SendScreenMessageAsync(ScreenMessage message, CancellationToken token = default) =>
        WriteFrameAsync(FrameType.ScreenControl, message.ToUtf8Json(), token);

    /// <summary>送出一張螢幕畫面。<paramref name="payload"/> 前 4 bytes 必須已經填好 big-endian 的序號,後面接 JPEG。</summary>
    public Task SendScreenFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken token = default) =>
        WriteFrameAsync(FrameType.ScreenFrame, payload, token);

    /// <summary>送出一段螢幕鏡像的聲音(16-bit little-endian PCM,交錯排列)。</summary>
    public Task SendScreenAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken token = default) =>
        WriteFrameAsync(FrameType.ScreenAudio, pcm, token);

    private static async Task<(byte Type, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[FrameLengthPrefixSize];
        if (!await ReadExactAsync(stream, header, allowLeadingEof: true, token).ConfigureAwait(false))
        {
            return null; // 剛好在訊框邊界收到 EOF,代表對方正常關閉連線。
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrameSize)
        {
            throw new InvalidDataException($"收到不合法的訊框長度:{length}");
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, allowLeadingEof: false, token).ConfigureAwait(false))
        {
            throw new EndOfStreamException("訊框讀到一半連線就中斷了。");
        }

        return (body[0], body[1..]);
    }

    /// <summary>把 buffer 填滿為止。<paramref name="allowLeadingEof"/> 為 true 時,若一開始就收到 EOF 會回傳 false 而不是丟例外。</summary>
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, bool allowLeadingEof, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowLeadingEof)
                {
                    return false;
                }

                throw new EndOfStreamException("讀取資料時連線意外中斷。");
            }

            offset += read;
        }

        return true;
    }

    // ===================== 檔案傳輸 =====================
    //
    // 同一時間只允許一個進行中的傳輸(跟這個 App 同一時間只允許一個連線的模型一致)。
    // 狀態機:
    //   傳送端:SendFileAsync 送出 file-offer -> 收到 file-accept 後背景逐塊送出 Chunk 訊框
    //           -> 送完後等待對方回傳 file-complete 才算真正完成。
    //   接收端:收到 file-offer 觸發 FileOffered -> 使用者用 RespondToFileOffer 回應
    //           -> 接受的話建立檔案,陸續把收到的 Chunk 訊框寫入,收滿宣告的大小後
    //              關閉檔案、回傳 file-complete。
    //   取消:任一方呼叫 CancelFileTransfer() 都會送出 file-cancel,對方收到後做鏡像的清理。

    public async Task SendFileAsync(string filePath)
    {
        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送檔案。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            StatusChanged?.Invoke(this, $"找不到檔案:{filePath}");
            return;
        }

        var transfer = new ActiveTransfer
        {
            TransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FileName = info.Name,
            TotalBytes = info.Length,
            FilePath = info.FullName,
        };
        _activeTransfer = transfer;

        StatusChanged?.Invoke(this, $"已送出檔案「{transfer.FileName}」的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "file-offer",
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Size = transfer.TotalBytes,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 傳送整個資料夾(遞迴包含所有子目錄與檔案)。流程跟單一檔案類似:
    /// 先送出 folder-offer 讓對方決定是否接受整個資料夾,對方一旦接受,底下每個檔案
    /// 會依序沿用既有的 file-offer/file-accept/Chunk/file-complete 狀態機自動傳送,
    /// 不會再逐一詢問使用者。
    /// </summary>
    public async Task SendFolderAsync(string folderPath)
    {
        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送資料夾。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        var info = new DirectoryInfo(folderPath);
        if (!info.Exists)
        {
            StatusChanged?.Invoke(this, $"找不到資料夾:{folderPath}");
            return;
        }

        List<(string FullPath, string RelativePath, long Size)> files;
        try
        {
            files = Directory.EnumerateFiles(info.FullName, "*", SearchOption.AllDirectories)
                .Select(fullPath => (
                    FullPath: fullPath,
                    RelativePath: Path.GetRelativePath(info.FullName, fullPath).Replace('\\', '/'),
                    Size: new FileInfo(fullPath).Length))
                .ToList();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"讀取資料夾內容失敗:{ex.Message}");
            return;
        }

        if (files.Count == 0)
        {
            StatusChanged?.Invoke(this, "資料夾是空的,沒有可以傳送的檔案。");
            return;
        }

        var session = new FolderSession
        {
            FolderTransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FolderName = info.Name,
            TotalBytes = files.Sum(f => f.Size),
            TotalEntries = files.Count,
            PendingFiles = new Queue<(string, string, long)>(files),
            IsBatch = false,
        };
        _activeFolderSession = session;

        StatusChanged?.Invoke(this, $"已送出資料夾「{session.FolderName}」({session.TotalEntries} 個檔案)的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "folder-offer",
            TransferId = session.FolderTransferId,
            FileName = session.FolderName,
            Size = session.TotalBytes,
            TotalEntries = session.TotalEntries,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 傳送多個各自獨立的檔案(使用者一次選取或拖曳多個檔案)。跟 <see cref="SendFolderAsync"/>
    /// 共用同一套資料夾層級的狀態機,差別只在於接收端不會另外建立一層子資料夾,
    /// 每個檔案會直接落在使用者設定的下載資料夾裡(檔名相同時一樣會依序改用「(1)」、「(2)」...)。
    /// 只有一個檔案時,直接走 <see cref="SendFileAsync"/> 原本的單檔流程。
    /// </summary>
    public async Task SendFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 1)
        {
            await SendFileAsync(filePaths[0]).ConfigureAwait(false);
            return;
        }

        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送檔案。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        if (filePaths.Count == 0)
        {
            return;
        }

        var files = new List<(string FullPath, string RelativePath, long Size)>();
        foreach (var filePath in filePaths)
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                StatusChanged?.Invoke(this, $"找不到檔案:{filePath}");
                return;
            }

            files.Add((info.FullName, info.Name, info.Length));
        }

        var session = new FolderSession
        {
            FolderTransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FolderName = $"{files.Count} 個檔案",
            TotalBytes = files.Sum(f => f.Size),
            TotalEntries = files.Count,
            PendingFiles = new Queue<(string, string, long)>(files),
            IsBatch = true,
        };
        _activeFolderSession = session;

        StatusChanged?.Invoke(this, $"已送出 {session.TotalEntries} 個檔案的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "folder-offer",
            TransferId = session.FolderTransferId,
            FileName = session.FolderName,
            Size = session.TotalBytes,
            TotalEntries = session.TotalEntries,
            IsBatch = true,
        }).ConfigureAwait(false);
    }

    /// <summary>接收到 <see cref="FileOffered"/> 之後,使用者確認是否接受這個檔案時呼叫。</summary>
    public void RespondToFileOffer(string transferId, bool accept, string downloadFolder)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Receiving } || transfer.TransferId != transferId)
        {
            return;
        }

        if (!accept)
        {
            _activeTransfer = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = transferId });
            FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
            {
                TransferId = transferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Rejected,
                FileName = transfer.FileName,
            });
            return;
        }

        try
        {
            Directory.CreateDirectory(downloadFolder);
            var destinationPath = GetUniqueDestinationPath(downloadFolder, transfer.FileName);
            transfer.FileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
            transfer.SavedFilePath = destinationPath;
        }
        catch (Exception ex)
        {
            _activeTransfer = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = transferId });
            StatusChanged?.Invoke(this, $"無法建立檔案:{ex.Message}");
            FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
            {
                TransferId = transferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Failed,
                FileName = transfer.FileName,
            });
            return;
        }

        _ = AcceptIncomingFileAsync(transfer);
    }

    /// <summary>接收到 <see cref="FolderOffered"/> 之後,使用者確認是否接受整個資料夾時呼叫。</summary>
    public void RespondToFolderOffer(string folderTransferId, bool accept, string downloadFolder)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving } || session.FolderTransferId != folderTransferId)
        {
            return;
        }

        if (!accept)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = folderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderTransferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Rejected,
                FolderName = session.FolderName,
                EntryCount = 0,
                IsBatch = session.IsBatch,
            });
            return;
        }

        try
        {
            Directory.CreateDirectory(downloadFolder);
            if (session.IsBatch)
            {
                // 多個各自獨立的檔案:不另外建立一層子資料夾,直接落在下載資料夾裡。
                session.RootDestinationFolder = downloadFolder;
            }
            else
            {
                session.RootDestinationFolder = GetUniqueDestinationDirectory(downloadFolder, session.FolderName);
                Directory.CreateDirectory(session.RootDestinationFolder);
            }
        }
        catch (Exception ex)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = folderTransferId });
            StatusChanged?.Invoke(this, $"無法建立資料夾:{ex.Message}");
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderTransferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Failed,
                FolderName = session.FolderName,
                EntryCount = 0,
                IsBatch = session.IsBatch,
            });
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "folder-accept", TransferId = folderTransferId });
    }

    /// <summary>
    /// 取消目前進行中的傳輸(不論是傳送中、接收中,還是還在等待對方回應提議階段)。
    /// 若目前是資料夾傳輸,會連同整個資料夾一起取消,而不是只取消當下這一個檔案。
    /// </summary>
    public void CancelFileTransfer()
    {
        var folderSession = _activeFolderSession;
        if (folderSession != null)
        {
            _activeFolderSession = null;

            var currentEntry = _activeTransfer;
            if (currentEntry is { FolderTransferId: not null })
            {
                _activeTransfer = null;
                CleanUpTransferResources(currentEntry);
            }

            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderSession.FolderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderSession.FolderTransferId,
                Direction = folderSession.Direction,
                Reason = FileTransferEndReason.Cancelled,
                FolderName = folderSession.FolderName,
                EntryCount = folderSession.CompletedEntries,
                IsBatch = folderSession.IsBatch,
            });
            return;
        }

        var transfer = _activeTransfer;
        if (transfer == null)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        _ = WriteControlAsync(new FileControlMessage { Type = "file-cancel", TransferId = transfer.TransferId });
        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Cancelled,
            FileName = transfer.FileName,
        });
    }

    private void HandleControlFrame(byte[] payload)
    {
        FileControlMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<FileControlMessage>(payload);
        }
        catch (JsonException)
        {
            return;
        }

        if (message?.Type == HeartbeatMessageType)
        {
            // 心跳本身不需要回應,收到代表對方支援心跳,之後才啟用逾時判斷。
            _peerSendsHeartbeat = true;
            return;
        }

        if (message?.TransferId == null)
        {
            return;
        }

        switch (message.Type)
        {
            case "file-offer":
                HandleFileOffer(message);
                break;
            case "file-accept":
                HandleFileAccept(message);
                break;
            case "file-reject":
                HandleFileReject(message);
                break;
            case "file-cancel":
                HandleFileCancel(message);
                break;
            case "file-complete":
                HandleFileCompleteAck(message);
                break;
            case "folder-offer":
                HandleFolderOffer(message);
                break;
            case "folder-accept":
                HandleFolderAccept(message);
                break;
            case "folder-reject":
                HandleFolderReject(message);
                break;
            case "folder-cancel":
                HandleFolderCancel(message);
                break;
            case "folder-complete":
                HandleFolderComplete(message);
                break;
        }
    }

    private void HandleFileOffer(FileControlMessage message)
    {
        if (message.FolderTransferId != null)
        {
            HandleFolderFileOffer(message);
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        var transfer = new ActiveTransfer
        {
            TransferId = message.TransferId!,
            Direction = FileTransferDirection.Receiving,
            FileName = SanitizeFileName(message.FileName),
            TotalBytes = message.Size!.Value,
        };
        _activeTransfer = transfer;

        FileOffered?.Invoke(this, new FileOfferedEventArgs
        {
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            FileSize = transfer.TotalBytes,
        });
    }

    private void HandleFolderOffer(FileControlMessage message)
    {
        if (_activeTransfer != null || _activeFolderSession != null
            || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0) || message.TotalEntries is not (> 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = message.TransferId });
            return;
        }

        var isBatch = message.IsBatch == true;

        var session = new FolderSession
        {
            FolderTransferId = message.TransferId!,
            Direction = FileTransferDirection.Receiving,
            // 批次傳送的「資料夾名稱」只是顯示用的摘要文字(例如「3 個檔案」),不會被當成檔案系統路徑,
            // 所以不需要、也不應該套用只適用於真實檔名的 SanitizeFileName。
            FolderName = isBatch ? message.FileName! : SanitizeFileName(message.FileName),
            TotalBytes = message.Size!.Value,
            TotalEntries = message.TotalEntries!.Value,
            IsBatch = isBatch,
        };
        _activeFolderSession = session;

        FolderOffered?.Invoke(this, new FolderOfferedEventArgs
        {
            TransferId = session.FolderTransferId,
            FolderName = session.FolderName,
            TotalSize = session.TotalBytes,
            TotalEntries = session.TotalEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>
    /// 資料夾傳輸裡,某一個檔案的 file-offer:因為使用者已經同意過整個資料夾,
    /// 這裡不再另外詢問,而是直接依 <see cref="FileControlMessage.RelativePath"/> 在
    /// 資料夾根目錄底下建立對應的子目錄與檔案,然後自動回 file-accept。
    /// </summary>
    private void HandleFolderFileOffer(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving, RootDestinationFolder: not null }
            || session.FolderTransferId != message.FolderTransferId
            || _activeTransfer != null
            || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        var relativePath = SanitizeRelativePath(message.RelativePath, message.FileName);
        var destinationPath = Path.Combine(session.RootDestinationFolder, relativePath);

        var fullRoot = Path.GetFullPath(session.RootDestinationFolder) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(destinationPath).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            // 正常情況下 SanitizeRelativePath 已經擋掉路徑穿越,這裡是最後一道防線。
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        ActiveTransfer transfer;
        try
        {
            var directory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(directory);
            destinationPath = GetUniqueDestinationPath(directory, Path.GetFileName(destinationPath));

            transfer = new ActiveTransfer
            {
                TransferId = message.TransferId!,
                Direction = FileTransferDirection.Receiving,
                FileName = Path.GetFileName(destinationPath),
                TotalBytes = message.Size!.Value,
                FolderTransferId = session.FolderTransferId,
                EntryIndex = message.EntryIndex,
                FileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write),
                SavedFilePath = destinationPath,
            };
            _activeTransfer = transfer;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"無法建立檔案:{ex.Message}");
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = session.FolderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        _ = AcceptIncomingFileAsync(transfer);
    }

    private void HandleFolderAccept(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Sending } || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        SendNextFolderFileOrComplete(session);
    }

    private void HandleFolderReject(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session == null || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;
        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = FileTransferEndReason.Rejected,
            FolderName = session.FolderName,
            EntryCount = 0,
            IsBatch = session.IsBatch,
        });
    }

    private void HandleFolderCancel(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session == null || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;

        var currentEntry = _activeTransfer;
        if (currentEntry is { FolderTransferId: not null } && currentEntry.FolderTransferId == session.FolderTransferId)
        {
            _activeTransfer = null;
            CleanUpTransferResources(currentEntry);
        }

        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = FileTransferEndReason.CancelledByRemote,
            FolderName = session.FolderName,
            EntryCount = session.CompletedEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>接收端收到 folder-complete:代表傳送端已經把所有檔案都送完,且每個都收到接收端的 file-complete 確認了。</summary>
    private void HandleFolderComplete(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving } || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;
        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = FileTransferDirection.Receiving,
            Reason = FileTransferEndReason.Completed,
            FolderName = session.FolderName,
            EntryCount = session.TotalEntries,
            IsBatch = session.IsBatch,
            SavedFolderPath = session.RootDestinationFolder,
        });
    }

    /// <summary>傳送端:把資料夾佇列裡的下一個檔案送出 file-offer;如果已經沒有檔案了,結束整個資料夾傳輸。</summary>
    private void SendNextFolderFileOrComplete(FolderSession session)
    {
        if (session.PendingFiles == null || session.PendingFiles.Count == 0)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-complete", TransferId = session.FolderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = session.FolderTransferId,
                Direction = FileTransferDirection.Sending,
                Reason = FileTransferEndReason.Completed,
                FolderName = session.FolderName,
                EntryCount = session.TotalEntries,
                IsBatch = session.IsBatch,
            });
            return;
        }

        var (fullPath, relativePath, size) = session.PendingFiles.Dequeue();
        var entryIndex = session.CompletedEntries + 1;

        var transfer = new ActiveTransfer
        {
            TransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FileName = Path.GetFileName(fullPath),
            TotalBytes = size,
            FilePath = fullPath,
            FolderTransferId = session.FolderTransferId,
            EntryIndex = entryIndex,
        };
        _activeTransfer = transfer;

        _ = WriteControlAsync(new FileControlMessage
        {
            Type = "file-offer",
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Size = transfer.TotalBytes,
            FolderTransferId = session.FolderTransferId,
            RelativePath = relativePath,
            EntryIndex = entryIndex,
            TotalEntries = session.TotalEntries,
        });
    }

    private void HandleFileAccept(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Sending } || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _ = SendFileChunksAsync(transfer);
    }

    private void HandleFileReject(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer == null || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;

        if (transfer.FolderTransferId is { } folderTransferId)
        {
            // 資料夾裡的其中一個檔案被拒絕是不應該發生的狀況(整個資料夾已經被同意過了),
            // 保守起見直接把整個資料夾傳輸結束掉,而不是嘗試略過這個檔案繼續傳其他的。
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Rejected,
            FileName = transfer.FileName,
        });
    }

    private void HandleFileCancel(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer == null || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        if (transfer.FolderTransferId != null)
        {
            AbortActiveFolderSession(FileTransferEndReason.CancelledByRemote);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.CancelledByRemote,
            FileName = transfer.FileName,
        });
    }

    private void HandleFileCompleteAck(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Sending } || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;

        if (transfer.FolderTransferId is { } folderTransferId
            && _activeFolderSession is { } session && session.FolderTransferId == folderTransferId)
        {
            session.CompletedBytes += transfer.TotalBytes;
            session.CompletedEntries += 1;
            SendNextFolderFileOrComplete(session);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Sending,
            Reason = FileTransferEndReason.Completed,
            FileName = transfer.FileName,
        });
    }

    private void HandleChunkFrame(byte[] payload)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Receiving, FileStream: not null })
        {
            return; // 已被取消/拒絕之後仍在路上的區塊,直接丟棄。
        }

        if (transfer.TransferredBytes + payload.Length > transfer.TotalBytes)
        {
            FailActiveTransfer(transfer, "收到超過預期大小的檔案內容。");
            return;
        }

        try
        {
            transfer.FileStream.Write(payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            FailActiveTransfer(transfer, $"寫入檔案失敗:{ex.Message}");
            return;
        }

        transfer.TransferredBytes += payload.Length;

        var (folderBytes, folderTotal) = GetFolderProgressTotals(transfer);
        FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Receiving,
            BytesTransferred = transfer.TransferredBytes,
            TotalBytes = transfer.TotalBytes,
            FolderTransferId = transfer.FolderTransferId,
            FileName = transfer.FolderTransferId != null ? transfer.FileName : null,
            EntryIndex = transfer.EntryIndex,
            TotalEntries = _activeFolderSession?.TotalEntries,
            FolderBytesTransferred = folderBytes,
            FolderTotalBytes = folderTotal,
        });

        if (transfer.TransferredBytes == transfer.TotalBytes)
        {
            CompleteReceivedFile(transfer);
        }
    }

    /// <summary>
    /// 接收端:回 file-accept。0 byte 的檔案不會有任何 Chunk 訊框,也就不會走到
    /// <see cref="HandleChunkFrame"/> 裡「收滿宣告大小」的完成判斷,所以接受後直接完成,
    /// 否則雙方會一直互等(傳送端等 file-complete、接收端等資料)。
    /// </summary>
    private async Task AcceptIncomingFileAsync(ActiveTransfer transfer)
    {
        await WriteControlAsync(new FileControlMessage { Type = "file-accept", TransferId = transfer.TransferId }).ConfigureAwait(false);

        if (transfer.TotalBytes == 0 && _activeTransfer == transfer)
        {
            CompleteReceivedFile(transfer);
        }
    }

    private void CompleteReceivedFile(ActiveTransfer transfer)
    {
        _activeTransfer = null;
        transfer.FileStream!.Dispose();
        _ = WriteControlAsync(new FileControlMessage { Type = "file-complete", TransferId = transfer.TransferId });

        if (transfer.FolderTransferId is { } folderTransferId
            && _activeFolderSession is { } session && session.FolderTransferId == folderTransferId)
        {
            session.CompletedBytes += transfer.TotalBytes;
            session.CompletedEntries += 1;
            return; // 資料夾層級的完成事件要等收到傳送端送來的 folder-complete 才觸發。
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Receiving,
            Reason = FileTransferEndReason.Completed,
            FileName = transfer.FileName,
            SavedFilePath = transfer.SavedFilePath,
        });
    }

    private async Task SendFileChunksAsync(ActiveTransfer transfer)
    {
        try
        {
            await using var fileStream = new FileStream(transfer.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 讀取磁碟(生產者)跟寫入 socket(消費者)用有界 channel 重疊執行,而不是
            // 完全循序的「讀一塊→寫一塊」——這樣寫入目前這塊的同時就能開始讀下一塊,
            // 磁碟較慢(例如網路磁碟機、隨身碟)時能明顯縮短整體傳輸時間。
            using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(transfer.Cts.Token);
            var channel = Channel.CreateBounded<(byte[] Buffer, int Length)>(
                new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = true });

            var readTask = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    while (true)
                    {
                        var chunk = ArrayPool<byte>.Shared.Rent(FileChunkSize);
                        int read;
                        try
                        {
                            read = await fileStream.ReadAsync(chunk.AsMemory(0, FileChunkSize), pipelineCts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(chunk);
                            throw;
                        }

                        if (read <= 0)
                        {
                            ArrayPool<byte>.Shared.Return(chunk);
                            break;
                        }

                        await channel.Writer.WriteAsync((chunk, read), pipelineCts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    channel.Writer.Complete(failure);
                }
            });

            try
            {
                await foreach (var (chunk, length) in channel.Reader.ReadAllAsync(pipelineCts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await WriteFrameAsync(FrameType.Chunk, chunk.AsMemory(0, length), pipelineCts.Token).ConfigureAwait(false);
                        transfer.TransferredBytes += length;

                        var (folderBytes, folderTotal) = GetFolderProgressTotals(transfer);
                        FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
                        {
                            TransferId = transfer.TransferId,
                            Direction = FileTransferDirection.Sending,
                            BytesTransferred = transfer.TransferredBytes,
                            TotalBytes = transfer.TotalBytes,
                            FolderTransferId = transfer.FolderTransferId,
                            FileName = transfer.FolderTransferId != null ? transfer.FileName : null,
                            EntryIndex = transfer.EntryIndex,
                            TotalEntries = _activeFolderSession?.TotalEntries,
                            FolderBytesTransferred = folderBytes,
                            FolderTotalBytes = folderTotal,
                        });
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk);
                    }
                }
            }
            finally
            {
                // 不管消費者迴圈是正常結束還是中途丟例外,都要先讓背景讀取工作停下來,
                // 才能安全釋放上面的 fileStream——否則讀取工作可能還卡在等 channel 有空位。
                pipelineCts.Cancel();
                await readTask.ConfigureAwait(false);
            }

            StatusChanged?.Invoke(this, $"「{transfer.FileName}」已送出,等待對方確認接收完成...");
        }
        catch (OperationCanceledException)
        {
            // 本機或對方取消,CancelFileTransfer()/HandleFileCancel() 已經處理過清理跟事件了。
        }
        catch (Exception ex)
        {
            FailActiveTransfer(transfer, $"傳送檔案失敗:{ex.Message}");
        }
    }

    private void FailActiveTransfer(ActiveTransfer transfer, string reason)
    {
        if (_activeTransfer != transfer)
        {
            return; // 已經被別的路徑(取消/斷線)結束了。
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        StatusChanged?.Invoke(this, reason);

        if (transfer.FolderTransferId is { } folderTransferId)
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "file-cancel", TransferId = transfer.TransferId });
        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Failed,
            FileName = transfer.FileName,
        });
    }

    /// <summary>連線中斷時清理任何還在進行中的傳輸,不嘗試再送出任何控制訊息(socket 已經不可用)。</summary>
    private void AbortActiveTransfer(FileTransferEndReason reason)
    {
        var transfer = _activeTransfer;
        if (transfer == null)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        if (transfer.FolderTransferId != null)
        {
            return; // 資料夾層級的結束事件由 AbortActiveFolderSession 統一觸發,避免同時跳出兩則訊息。
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = reason,
            FileName = transfer.FileName,
        });
    }

    /// <summary>連線中斷或發生無法復原的錯誤時,結束整個資料夾傳輸,不嘗試再送出任何控制訊息。</summary>
    private void AbortActiveFolderSession(FileTransferEndReason reason)
    {
        var session = _activeFolderSession;
        if (session == null)
        {
            return;
        }

        _activeFolderSession = null;

        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = reason,
            FolderName = session.FolderName,
            EntryCount = session.CompletedEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>算出目前這個檔案在所屬資料夾傳輸裡的累計進度(含之前已完成的檔案),不屬於資料夾傳輸則回傳 null。</summary>
    private (long? BytesTransferred, long? TotalBytes) GetFolderProgressTotals(ActiveTransfer transfer)
    {
        if (transfer.FolderTransferId is not { } folderTransferId
            || _activeFolderSession is not { } session || session.FolderTransferId != folderTransferId)
        {
            return (null, null);
        }

        return (session.CompletedBytes + transfer.TransferredBytes, session.TotalBytes);
    }

    /// <summary>停止背景送出迴圈(若有)、關閉並刪除尚未寫完的接收檔案(若有)。</summary>
    private static void CleanUpTransferResources(ActiveTransfer transfer)
    {
        transfer.Cts.Cancel();

        if (transfer.Direction != FileTransferDirection.Receiving)
        {
            return;
        }

        try
        {
            transfer.FileStream?.Dispose();
        }
        catch
        {
            // 忽略關閉檔案時的例外,接下來還是要嘗試刪除檔案。
        }

        try
        {
            if (transfer.SavedFilePath != null && File.Exists(transfer.SavedFilePath))
            {
                File.Delete(transfer.SavedFilePath);
            }
        }
        catch
        {
            // 刪不掉殘破的檔案不影響傳輸狀態的回報,使用者之後可以手動清掉。
        }
    }

    /// <summary>把對方宣告的檔名限制在單純的檔名(避免路徑穿越),並過濾不合法字元。</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "file";
        }

        return name;
    }

    /// <summary>如果目的路徑的檔案已存在,依序改用「檔名 (1).副檔名」、「檔名 (2).副檔名」...直到找到未使用的檔名。</summary>
    private static string GetUniqueDestinationPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{nameWithoutExtension} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>如果目的路徑的資料夾已存在(或撞到同名檔案),依序改用「資料夾名稱 (1)」、「資料夾名稱 (2)」...直到找到未使用的名稱。</summary>
    private static string GetUniqueDestinationDirectory(string parentFolder, string folderName)
    {
        var candidate = Path.Combine(parentFolder, folderName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(parentFolder, $"{folderName} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把對方宣告的相對路徑限制在資料夾根目錄底下(移除 "."/".." 這種可能造成路徑穿越的片段),
    /// 並比照 <see cref="SanitizeFileName"/> 過濾每一段路徑名稱裡的不合法字元。
    /// </summary>
    private static string SanitizeRelativePath(string? relativePath, string fallbackFileName)
    {
        var fileName = SanitizeFileName(fallbackFileName);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return fileName;
        }

        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .Select(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "_" : segment)
            .ToArray();

        if (segments.Length == 0)
        {
            return fileName;
        }

        // 最後一段(檔名)一律以 file-offer 訊息裡另外宣告的 FileName 為準,RelativePath 只用來決定子目錄結構。
        segments[^1] = fileName;
        return Path.Combine(segments);
    }

    private sealed class ActiveTransfer
    {
        public required string TransferId { get; init; }

        public required FileTransferDirection Direction { get; init; }

        public required string FileName { get; init; }

        public required long TotalBytes { get; init; }

        public long TransferredBytes { get; set; }

        /// <summary>傳送端:來源檔案的完整路徑。</summary>
        public string? FilePath { get; init; }

        /// <summary>接收端:目的檔案的寫入串流。</summary>
        public FileStream? FileStream { get; set; }

        /// <summary>接收端:目的檔案的完整路徑。</summary>
        public string? SavedFilePath { get; set; }

        /// <summary>屬於資料夾傳輸時,所屬的資料夾傳輸 TransferId,否則為 null。</summary>
        public string? FolderTransferId { get; init; }

        /// <summary>屬於資料夾傳輸時,這個檔案是第幾個(1-based)。</summary>
        public int? EntryIndex { get; init; }

        public CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>一次資料夾傳輸的整體狀態,底下每個檔案仍沿用 <see cref="ActiveTransfer"/> 逐一傳送。</summary>
    private sealed class FolderSession
    {
        public required string FolderTransferId { get; init; }

        public required FileTransferDirection Direction { get; init; }

        public required string FolderName { get; init; }

        public required long TotalBytes { get; init; }

        public required int TotalEntries { get; init; }

        public long CompletedBytes { get; set; }

        public int CompletedEntries { get; set; }

        /// <summary>傳送端:尚未送出的檔案佇列(完整路徑、相對於資料夾根目錄的路徑、檔案大小)。</summary>
        public Queue<(string FullPath, string RelativePath, long Size)>? PendingFiles { get; init; }

        /// <summary>接收端:這個資料夾實際落地的根目錄(已處理重名)。</summary>
        public string? RootDestinationFolder { get; set; }

        /// <summary>true 代表這是多個各自獨立的檔案一起送過來,不是使用者選了一整個資料夾。</summary>
        public required bool IsBatch { get; init; }
    }
}
