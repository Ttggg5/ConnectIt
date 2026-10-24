using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>同時連線多台裝置:每台各自一個 PeerSession,傳輸、斷線、螢幕鏡像互不影響。</summary>
public class MultiDeviceTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionService _hub = new() { LocalDeviceId = "hub" };
    private readonly ConnectionService _first = new() { LocalDeviceId = "first" };
    private readonly ConnectionService _second = new() { LocalDeviceId = "second" };

    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ConnectItTests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        _hub.Dispose();
        _first.Dispose();
        _second.Dispose();

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 測試環境清不掉暫存目錄不影響測試結果本身。
        }
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

    /// <summary>_first 與 _second 都連到 _hub。</summary>
    private async Task<int> ConnectBothAsync()
    {
        var port = _hub.StartListening();
        _hub.ConnectionRequested += (_, e) => e.Respond(true);

        Assert.True(await _first.RequestConnectionAsync(IPAddress.Loopback, port, "First", "Hub"));
        Assert.True(await _second.RequestConnectionAsync(IPAddress.Loopback, port, "Second", "Hub"));
        Assert.True(await WaitUntilAsync(() => _hub.Sessions.Count == 2, WaitTimeout));
        return port;
    }

    private string CreateSourceFile(string name, int size, int seed)
    {
        var path = Path.Combine(_tempDirectory, name);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task TwoDevices_CanConnectAtTheSameTime_AndCarryTheirDeviceIds()
    {
        await ConnectBothAsync();

        var ids = _hub.Sessions.Select(s => s.RemoteDeviceId ?? string.Empty).OrderBy(id => id).ToArray();
        Assert.Equal(["first", "second"], ids);
        Assert.Equal("hub", _first.Sessions.Single().RemoteDeviceId);
    }

    [Fact]
    public async Task TwoDevices_SendFilesSimultaneously_EachArrivesIntact()
    {
        await ConnectBothAsync();
        var downloadFolder = Directory.CreateDirectory(Path.Combine(_tempDirectory, "download")).FullName;
        _hub.FileOffered += (_, e) => _hub.RespondToFileOffer(e.TransferId, true, downloadFolder);

        var received = new System.Collections.Concurrent.ConcurrentDictionary<string, FileTransferEndedEventArgs>();
        _hub.FileTransferEnded += (sender, e) => received[((PeerSession)sender!).RemoteDeviceId!] = e;

        var fromFirst = CreateSourceFile("from-first.bin", 700_000, 1);
        var fromSecond = CreateSourceFile("from-second.bin", 900_000, 2);
        await Task.WhenAll(
            _first.Sessions.Single().SendFileAsync(fromFirst),
            _second.Sessions.Single().SendFileAsync(fromSecond));

        Assert.True(await WaitUntilAsync(() => received.Count == 2, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Completed, received["first"].Reason);
        Assert.Equal(FileTransferEndReason.Completed, received["second"].Reason);
        Assert.Equal(File.ReadAllBytes(fromFirst), File.ReadAllBytes(received["first"].SavedFilePath!));
        Assert.Equal(File.ReadAllBytes(fromSecond), File.ReadAllBytes(received["second"].SavedFilePath!));
    }

    [Fact]
    public async Task OneDeviceDisconnecting_LeavesTheOtherConnected()
    {
        await ConnectBothAsync();
        SessionClosedEventArgs? closed = null;
        _hub.SessionClosed += (_, e) => closed = e;

        _first.Disconnect();

        Assert.True(await WaitUntilAsync(() => closed != null, WaitTimeout));
        Assert.True(closed!.ByRemote);
        Assert.Equal("first", closed.Session.RemoteDeviceId);
        Assert.Equal("second", _hub.Sessions.Single().RemoteDeviceId);
        Assert.True(_second.IsConnected);
    }

    [Fact]
    public async Task SameDeviceReconnecting_ReplacesItsStaleSession()
    {
        var port = await ConnectBothAsync();
        var staleSession = _hub.Sessions.Single(s => s.RemoteDeviceId == "first");

        // 模擬同一台裝置(相同 DeviceId)在舊連線還沒被判定斷線前又連進來。
        using var firstAgain = new ConnectionService { LocalDeviceId = "first" };
        Assert.True(await firstAgain.RequestConnectionAsync(IPAddress.Loopback, port, "First", "Hub"));

        Assert.True(await WaitUntilAsync(() => staleSession.IsClosed && _hub.Sessions.Count == 2, WaitTimeout));
        Assert.Single(_hub.Sessions, s => s.RemoteDeviceId == "first");
    }

    [Fact]
    public async Task ScreenMirrorWithOneDevice_RepliesBusyToAnother()
    {
        await ConnectBothAsync();
        using var hubMirror = new ScreenMirrorController(_hub);
        using var firstMirror = new ScreenMirrorController(_first);
        using var secondMirror = new ScreenMirrorController(_second);
        var secondNotice = new TaskCompletionSource<string>();
        secondMirror.Notice += (_, message) => secondNotice.TrySetResult(message);

        firstMirror.RequestViewPeer(_first.Sessions.Single());
        Assert.True(await WaitUntilAsync(() => hubMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));
        Assert.Equal("first", hubMirror.Peer?.RemoteDeviceId);

        secondMirror.RequestViewPeer(_second.Sessions.Single());

        Assert.Contains("另一個螢幕鏡像", await secondNotice.Task.WaitAsync(WaitTimeout));
        Assert.Equal(ScreenMirrorPhase.Idle, secondMirror.Status.Phase);
        Assert.Equal(ScreenMirrorPhase.IncomingRequest, hubMirror.Status.Phase);
        Assert.Equal("first", hubMirror.Peer?.RemoteDeviceId);
    }
}
