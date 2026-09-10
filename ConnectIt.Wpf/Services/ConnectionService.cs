using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 負責 mDNS 找到裝置之後的實際 TCP 連線,包含連線請求/接受/拒絕的交握協定:
/// 發起端連上後先送出 "request",對方要等使用者確認才會回 "accept"/"reject"。
/// </summary>
public sealed class ConnectionService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;

    private TcpClient? _activeClient;
    private CancellationTokenSource? _monitorCts;

    public int Port { get; private set; }

    public bool IsConnected => _activeClient != null;

    /// <summary>有裝置送出連線請求,需要使用者確認。</summary>
    public event EventHandler<ConnectionRequestedEventArgs>? ConnectionRequested;

    /// <summary>連線交握完成(不論是發起方還是被連線方)。</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    /// <summary>對方關閉了連線(不是本機呼叫 Disconnect() 造成的)。</summary>
    public event EventHandler? RemoteDisconnected;

    public event EventHandler<string>? StatusChanged;

    /// <summary>開始監聽,回傳實際使用的連接埠(0 代表由系統挑選)。</summary>
    public int StartListening(int preferredPort = 0)
    {
        StopListening();

        _listener = new TcpListener(IPAddress.Any, preferredPort);
        _listener.Start();
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

        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var line = await ReadLineWithTimeoutAsync(reader, RequestTimeout, token).ConfigureAwait(false);

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

    /// <summary>主動連線到指定裝置並送出連線請求,等待對方確認。</summary>
    public async Task<bool> RequestConnectionAsync(
        IPAddress address, int port, string myDeviceName, string remoteDisplayName, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            StatusChanged?.Invoke(this, $"已送出連線請求給 {remoteDisplayName},等待對方確認...");

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = "request",
                DeviceName = myDeviceName,
            })).ConfigureAwait(false);

            var line = await ReadLineWithTimeoutAsync(reader, ResponseTimeout, cancellationToken).ConfigureAwait(false);
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
                return true;
            }

            StatusChanged?.Invoke(this, response == null
                ? $"連線逾時,{remoteDisplayName} 沒有回應。"
                : $"{remoteDisplayName} 拒絕了連線請求。");
            client.Close();
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"連線失敗:{ex.Message}");
            client.Close();
            return false;
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken token)
    {
        var readTask = reader.ReadLineAsync();
        var delayTask = Task.Delay(timeout, token);

        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        return completed == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// 連線建立後,持續在背景讀取這個 socket,用來偵測「對方把連線關掉」。
    /// 目前應用層還沒有實際資料要收發,所以收到的任何位元組都直接忽略,
    /// 只在意 EOF(對方正常關閉)或例外(對方強制中斷/網路斷線)這兩種情況。
    /// </summary>
    private void StartMonitoring(TcpClient client)
    {
        _monitorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        _ = MonitorConnectionAsync(client, cts.Token);
    }

    private async Task MonitorConnectionAsync(TcpClient client, CancellationToken token)
    {
        var remoteClosed = false;

        try
        {
            var stream = client.GetStream();
            var buffer = new byte[256];

            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    remoteClosed = true;
                    break;
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
    }
}
