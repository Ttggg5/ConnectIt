using System.Net;
using System.Net.Sockets;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 負責 mDNS 找到裝置之後的實際 TCP 連線(監聽 + 撥出)。
/// mDNS 只負責「找到對方在哪」,真正的資料交換走這裡建立的 TcpClient。
/// </summary>
public sealed class ConnectionService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;

    public int Port { get; private set; }

    public event EventHandler<TcpClient>? ClientConnected;
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

        StatusChanged?.Invoke(this, $"已開始監聽連接埠 {Port},等待其他裝置連入...");
        return Port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint;
                StatusChanged?.Invoke(this, $"接受來自 {remote} 的連線。");
                ClientConnected?.Invoke(this, client);
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

    /// <summary>主動連線到指定裝置(由 mDNS 搜尋結果取得的 IP/Port)。</summary>
    public async Task<TcpClient> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, $"已連線到 {address}:{port}。");
        return client;
    }

    public void Dispose() => StopListening();
}
