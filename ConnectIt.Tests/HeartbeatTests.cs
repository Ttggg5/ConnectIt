using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>
/// 連線心跳:對方沒有正常關閉連線就消失時(程式當掉、斷網),另一端要能在逾時後判定斷線;
/// 正常閒置的連線、或是不送心跳的舊版對方,則不能被誤判斷線。
/// </summary>
public class HeartbeatTests : IDisposable
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(600);

    private readonly ConnectionService _service = new() { HeartbeatInterval = FastInterval, HeartbeatTimeout = FastTimeout };
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        _service.Dispose();
        foreach (var item in _cleanup)
        {
            item.Dispose();
        }
    }

    /// <summary>用原始 socket(加上 TLS)扮演「另一台裝置」:送出連線請求、等對方接受,之後完全由測試控制要送什麼。</summary>
    private async Task<(TcpClient Client, Stream Stream)> ConnectRawPeerAsync()
    {
        var port = _service.StartListening();
        _service.ConnectionRequested += (_, e) => e.Respond(true);

        var client = new TcpClient();
        _cleanup.Add(client);
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        _cleanup.Add(stream);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "connectit",
            ClientCertificates = [DeviceIdentity.CreateEphemeral().Certificate],
        });

        var request = Encoding.UTF8.GetBytes("{\"Type\":\"request\",\"DeviceName\":\"RawPeer\"}\n");
        await stream.WriteAsync(request);

        // 讀到換行為止(對方回覆的 accept)。
        var buffer = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1 && one[0] != (byte)'\n')
        {
            buffer.Add(one[0]);
        }

        Assert.Contains("accept", Encoding.UTF8.GetString(buffer.ToArray()));
        return (client, stream);
    }

    private static async Task SendPingAsync(Stream stream)
    {
        var payload = Encoding.UTF8.GetBytes("{\"Type\":\"ping\"}");
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = 0; // 控制訊息
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame);
    }

    [Fact]
    public async Task WhenPeerSendsHeartbeatThenGoesSilent_RaisesRemoteDisconnected()
    {
        var disconnected = new TaskCompletionSource();
        _service.RemoteDisconnected += (_, _) => disconnected.TrySetResult();

        var (_, stream) = await ConnectRawPeerAsync();
        await SendPingAsync(stream);

        // 之後完全不再送任何東西,也不關閉連線(模擬對方當掉/斷網)。
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_service.IsConnected);
    }

    [Fact]
    public async Task WhenPeerNeverSendsHeartbeat_DoesNotDisconnectOnIdle()
    {
        var disconnected = false;
        _service.RemoteDisconnected += (_, _) => disconnected = true;

        await ConnectRawPeerAsync();

        // 舊版對方不送心跳:閒置超過逾時時間也不能被判定斷線。
        await Task.Delay(FastTimeout * 3);
        Assert.False(disconnected);
        Assert.True(_service.IsConnected);
    }

    [Fact]
    public async Task TwoServicesWithHeartbeat_StayConnectedWhileIdle()
    {
        using var initiator = new ConnectionService { HeartbeatInterval = FastInterval, HeartbeatTimeout = FastTimeout };
        var port = _service.StartListening();
        _service.ConnectionRequested += (_, e) => e.Respond(true);

        var disconnected = false;
        _service.RemoteDisconnected += (_, _) => disconnected = true;
        initiator.RemoteDisconnected += (_, _) => disconnected = true;

        Assert.True(await initiator.RequestConnectionAsync(IPAddress.Loopback, port, "Initiator", "Acceptor"));

        await Task.Delay(FastTimeout * 3);
        Assert.False(disconnected);
        Assert.True(_service.IsConnected);
        Assert.True(initiator.IsConnected);
    }
}
