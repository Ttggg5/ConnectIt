using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>
/// <see cref="ConnectionService.RequestConnectionWithOutcomeAsync"/> 要能分辨「被拒絕」「連不到」「使用者取消」,
/// UI 才能顯示對應的說明(而不是一律「對方未接受」)。
/// </summary>
public class ConnectionRequestOutcomeTests : IDisposable
{
    private readonly ConnectionService _acceptor = new();
    private readonly ConnectionService _initiator = new();

    public void Dispose()
    {
        _acceptor.Dispose();
        _initiator.Dispose();
    }

    [Fact]
    public async Task WhenAccepted_ReturnsAccepted()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var outcome = await _initiator.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, port, "Initiator", "Acceptor");

        Assert.Equal(ConnectionRequestOutcome.Accepted, outcome);
        Assert.True(_initiator.IsConnected);
    }

    [Fact]
    public async Task WhenRejected_ReturnsRejected()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(false);

        var outcome = await _initiator.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, port, "Initiator", "Acceptor");

        Assert.Equal(ConnectionRequestOutcome.Rejected, outcome);
        Assert.False(_initiator.IsConnected);
    }

    [Fact]
    public async Task WhenNoOneListening_ReturnsUnreachable()
    {
        int freePort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var outcome = await _initiator.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, freePort, "Initiator", "Nobody");

        Assert.Equal(ConnectionRequestOutcome.Unreachable, outcome);
    }

    [Fact]
    public async Task WhenCancelledWhileWaitingForResponse_ReturnsCancelledAndDoesNotConnect()
    {
        var port = _acceptor.StartListening();
        var requestReceived = new TaskCompletionSource();
        // 對方收到請求但一直不回覆(模擬使用者還沒按接受/拒絕)。
        _acceptor.ConnectionRequested += (_, _) => requestReceived.TrySetResult();

        using var cts = new CancellationTokenSource();
        var requestTask = _initiator.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, port, "Initiator", "Acceptor", cts.Token);

        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var outcome = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionRequestOutcome.Cancelled, outcome);
        Assert.False(_initiator.IsConnected);
    }

    [Fact]
    public async Task RequestConnectionAsync_StillReturnsBoolForBackwardCompatibility()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(false);

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "Initiator", "Acceptor");

        Assert.False(accepted);
    }
}
