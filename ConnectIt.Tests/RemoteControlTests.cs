using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>
/// 遙控器的狀態機(真正的 loopback 連線)。刻意不在測試裡送按鍵:受控端會用 SendInput 真的按下去,
/// 會干擾跑測試的這台電腦。按鍵名稱的對應另外測 <see cref="ScreenInputInjector.MapKey"/>。
/// </summary>
public class RemoteControlTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionService _target = new();
    private readonly ConnectionService _controllerConnection = new();
    private readonly RemoteControlController _targetRemote;
    private readonly RemoteControlController _controllerRemote;

    public RemoteControlTests()
    {
        _targetRemote = new RemoteControlController(_target);
        _controllerRemote = new RemoteControlController(_controllerConnection);
    }

    public void Dispose()
    {
        _targetRemote.Dispose();
        _controllerRemote.Dispose();
        _target.Dispose();
        _controllerConnection.Dispose();
    }

    private async Task<PeerSession> ConnectAsync()
    {
        var port = _target.StartListening();
        _target.ConnectionRequested += (_, e) => e.Respond(true);
        Assert.True(await _controllerConnection.RequestConnectionAsync(IPAddress.Loopback, port, "Controller", "Target"));
        Assert.True(await WaitUntilAsync(() => _target.IsConnected));
        return _controllerConnection.Sessions.Single();
    }

    [Theory]
    [InlineData(RemoteKeys.PlayPause, 0xB3)]
    [InlineData(RemoteKeys.Next, 0xB0)]
    [InlineData(RemoteKeys.Previous, 0xB1)]
    [InlineData(RemoteKeys.Stop, 0xB2)]
    [InlineData(RemoteKeys.VolumeUp, 0xAF)]
    [InlineData(RemoteKeys.VolumeDown, 0xAE)]
    [InlineData(RemoteKeys.VolumeMute, 0xAD)]
    [InlineData("Meta", 0x5B)]
    [InlineData("F5", 0x74)]
    public void MapKey_KnowsMediaAndShortcutKeys(string key, int expectedVk)
    {
        Assert.Equal((ushort)expectedVk, ScreenInputInjector.MapKey(key));
        Assert.Equal(RemoteKeys.IsMediaKey(key), key.StartsWith("Media") || key.StartsWith("Audio"));
    }

    [Fact]
    public async Task RequestThenAccept_ControllerSeesTargetPlatform()
    {
        var peer = await ConnectAsync();

        _controllerRemote.RequestControl(peer);
        Assert.Equal(RemoteControlPhase.OutgoingRequest, _controllerRemote.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.IncomingRequest));

        _targetRemote.RespondToIncomingRequest(true);

        Assert.Equal(RemoteControlPhase.Controlled, _targetRemote.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _controllerRemote.Status.Phase == RemoteControlPhase.Controlling));
        Assert.Equal("windows", _controllerRemote.Status.Platform);
        Assert.Equal(_targetRemote.Status.SessionId, _controllerRemote.Status.SessionId);
    }

    [Fact]
    public async Task RequestThenReject_NotifiesController()
    {
        var peer = await ConnectAsync();
        var notice = new TaskCompletionSource<string>();
        _controllerRemote.Notice += (_, message) => notice.TrySetResult(message);

        _controllerRemote.RequestControl(peer);
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.IncomingRequest));
        _targetRemote.RespondToIncomingRequest(false);

        Assert.Contains("拒絕", await notice.Task.WaitAsync(WaitTimeout));
        Assert.Equal(RemoteControlPhase.Idle, _controllerRemote.Status.Phase);
        Assert.Equal(RemoteControlPhase.Idle, _targetRemote.Status.Phase);
    }

    [Fact]
    public async Task StopFromEitherSide_EndsBoth()
    {
        var peer = await ConnectAsync();
        _controllerRemote.RequestControl(peer);
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.IncomingRequest));
        _targetRemote.RespondToIncomingRequest(true);
        Assert.True(await WaitUntilAsync(() => _controllerRemote.Status.Phase == RemoteControlPhase.Controlling));

        _targetRemote.Stop();

        Assert.Equal(RemoteControlPhase.Idle, _targetRemote.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _controllerRemote.Status.Phase == RemoteControlPhase.Idle));
    }

    [Fact]
    public async Task ConnectionClosed_ResetsBothSides()
    {
        var peer = await ConnectAsync();
        _controllerRemote.RequestControl(peer);
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.IncomingRequest));
        _targetRemote.RespondToIncomingRequest(true);
        Assert.True(await WaitUntilAsync(() => _controllerRemote.Status.Phase == RemoteControlPhase.Controlling));

        peer.Disconnect();

        Assert.True(await WaitUntilAsync(() => _controllerRemote.Status.Phase == RemoteControlPhase.Idle));
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.Idle));
    }

    [Fact]
    public async Task SecondController_GetsBusy()
    {
        var peer = await ConnectAsync();
        using var other = new ConnectionService();
        using var otherRemote = new RemoteControlController(other);
        Assert.True(await other.RequestConnectionAsync(IPAddress.Loopback, _target.Port, "Other", "Target"));
        var busyNotice = new TaskCompletionSource<string>();
        otherRemote.Notice += (_, message) => busyNotice.TrySetResult(message);

        _controllerRemote.RequestControl(peer);
        Assert.True(await WaitUntilAsync(() => _targetRemote.Status.Phase == RemoteControlPhase.IncomingRequest));
        otherRemote.RequestControl(other.Sessions.Single());

        Assert.Contains("其他裝置", await busyNotice.Task.WaitAsync(WaitTimeout));
        Assert.Equal(RemoteControlPhase.IncomingRequest, _targetRemote.Status.Phase);
        Assert.Same(_target.Sessions.Single(s => s.RemoteName == "Controller"), _targetRemote.Status.Peer);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
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
}
