using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class ConnectionServiceTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ConnectionService _acceptor = new();
    private readonly ConnectionService _initiator = new();

    public void Dispose()
    {
        _acceptor.Dispose();
        _initiator.Dispose();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task RequestConnectionAsync_WhenAccepted_RaisesConnectedOnBothSidesWithCorrectRoles()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        ConnectedEventArgs? acceptorConnected = null;
        ConnectedEventArgs? initiatorConnected = null;
        _acceptor.Connected += (_, e) => acceptorConnected = e;
        _initiator.Connected += (_, e) => initiatorConnected = e;

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "InitiatorDevice", "AcceptorDevice");

        Assert.True(accepted);
        Assert.True(await WaitUntilAsync(() => acceptorConnected != null, WaitTimeout));

        Assert.Equal("InitiatorDevice", acceptorConnected!.RemoteName);
        Assert.Equal(ConnectionRole.Acceptor, acceptorConnected.Role);

        Assert.Equal("AcceptorDevice", initiatorConnected!.RemoteName);
        Assert.Equal(ConnectionRole.Initiator, initiatorConnected.Role);

        Assert.True(_acceptor.IsConnected);
        Assert.True(_initiator.IsConnected);
    }

    [Fact]
    public async Task RequestConnectionAsync_WhenRejected_ReturnsFalseAndNeitherSideConnects()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(false);

        var connectedRaised = false;
        _acceptor.Connected += (_, _) => connectedRaised = true;
        _initiator.Connected += (_, _) => connectedRaised = true;

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "InitiatorDevice", "AcceptorDevice");

        Assert.False(accepted);
        Assert.False(connectedRaised);
        Assert.False(_acceptor.IsConnected);
        Assert.False(_initiator.IsConnected);
    }

    [Fact]
    public async Task RequestConnectionAsync_WhenNoOneListening_ReturnsFalse()
    {
        // 隨便挑一個沒有人監聽的連接埠(先跟系統要一個再馬上放掉)。
        int freePort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, freePort, "InitiatorDevice", "Nobody");

        Assert.False(accepted);
        Assert.False(_initiator.IsConnected);
    }

    [Fact]
    public async Task Disconnect_ByInitiator_RaisesRemoteDisconnectedOnAcceptorOnly()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var acceptorSawRemoteDisconnect = false;
        var initiatorSawRemoteDisconnect = false;
        _acceptor.RemoteDisconnected += (_, _) => acceptorSawRemoteDisconnect = true;
        _initiator.RemoteDisconnected += (_, _) => initiatorSawRemoteDisconnect = true;

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "InitiatorDevice", "AcceptorDevice");
        Assert.True(accepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));

        _initiator.Disconnect();

        Assert.True(await WaitUntilAsync(() => acceptorSawRemoteDisconnect, WaitTimeout));
        Assert.False(initiatorSawRemoteDisconnect);
        Assert.False(_acceptor.IsConnected);
    }

    [Fact]
    public async Task Disconnect_ByAcceptor_RaisesRemoteDisconnectedOnInitiatorOnly()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var acceptorSawRemoteDisconnect = false;
        var initiatorSawRemoteDisconnect = false;
        _acceptor.RemoteDisconnected += (_, _) => acceptorSawRemoteDisconnect = true;
        _initiator.RemoteDisconnected += (_, _) => initiatorSawRemoteDisconnect = true;

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "InitiatorDevice", "AcceptorDevice");
        Assert.True(accepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));

        _acceptor.Disconnect();

        Assert.True(await WaitUntilAsync(() => initiatorSawRemoteDisconnect, WaitTimeout));
        Assert.False(acceptorSawRemoteDisconnect);
        Assert.False(_initiator.IsConnected);
    }

    [Fact]
    public async Task Acceptor_CanAcceptANewConnectionAfterThePreviousOneDisconnects()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var firstAccepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "First", "AcceptorDevice");
        Assert.True(firstAccepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));

        _initiator.Disconnect();
        Assert.True(await WaitUntilAsync(() => !_acceptor.IsConnected, WaitTimeout));

        using var secondInitiator = new ConnectionService();
        var secondAccepted = await secondInitiator.RequestConnectionAsync(IPAddress.Loopback, port, "Second", "AcceptorDevice");

        Assert.True(secondAccepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));
    }
}
