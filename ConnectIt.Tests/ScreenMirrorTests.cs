using System.Net;
using System.Text;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>
/// 螢幕鏡像:訊息格式必須跟 Android 端(ScreenMessages.kt,PascalCase、可省略 null)相容;
/// 兩台裝置透過真正的 loopback 連線跑完請求/邀請/拒絕/停止/斷線的狀態轉換。
/// </summary>
public class ScreenMirrorTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionService _acceptor = new();
    private readonly ConnectionService _initiator = new();
    private readonly ScreenMirrorController _acceptorMirror;
    private readonly ScreenMirrorController _initiatorMirror;

    public ScreenMirrorTests()
    {
        _acceptorMirror = new ScreenMirrorController(_acceptor);
        _initiatorMirror = new ScreenMirrorController(_initiator);
    }

    public void Dispose()
    {
        _acceptorMirror.Dispose();
        _initiatorMirror.Dispose();
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

    private async Task ConnectAsync()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        Assert.True(await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "Initiator", "Acceptor"));
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));
    }

    // ===================== 訊息格式 =====================

    [Fact]
    public void ToUtf8Json_UsesPascalCaseKeysAndOmitsNulls()
    {
        var json = Encoding.UTF8.GetString(new ScreenMessage
        {
            Type = ScreenMessage.Info,
            SessionId = "abc",
            Width = 1920,
            Height = 1080,
            Platform = "windows",
            AllowControl = false,
        }.ToUtf8Json());

        Assert.Contains("\"Type\":\"screen-info\"", json);
        Assert.Contains("\"SessionId\":\"abc\"", json);
        Assert.Contains("\"Width\":1920", json);
        Assert.Contains("\"AllowControl\":false", json);
        Assert.DoesNotContain("Seq", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Parse_AcceptsAndroidStyleJson()
    {
        // Android 的 org.json 會把 1.0 寫成 1、省略 null 欄位;中文字直接以 UTF-8 送出。
        var json = "{\"Type\":\"screen-input\",\"SessionId\":\"s1\",\"Action\":\"wheel\",\"X\":1,\"Y\":0.5,\"DeltaY\":-2,\"Text\":\"你好\"}";

        var message = ScreenMessage.Parse(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message);
        Assert.Equal(ScreenMessage.Input, message.Type);
        Assert.Equal(ScreenInputAction.Wheel, message.Action);
        Assert.Equal(1.0, message.X);
        Assert.Equal(0.5, message.Y);
        Assert.Equal(-2.0, message.DeltaY);
        Assert.Equal("你好", message.Text);
        Assert.Null(message.Button);
    }

    [Fact]
    public void Parse_ReturnsNullForMalformedOrTypelessJson()
    {
        Assert.Null(ScreenMessage.Parse("not json"u8));
        Assert.Null(ScreenMessage.Parse("{\"SessionId\":\"x\"}"u8));
    }

    [Theory]
    [InlineData("Enter", (ushort)0x0D)]
    [InlineData("Backspace", (ushort)0x08)]
    [InlineData("ArrowLeft", (ushort)0x25)]
    [InlineData("Control", (ushort)0x11)]
    [InlineData("a", (ushort)'A')]
    [InlineData("Z", (ushort)'Z')]
    [InlineData("7", (ushort)'7')]
    [InlineData("F5", (ushort)0x74)]
    [InlineData("F12", (ushort)0x7B)]
    public void MapKey_MapsWebKeyNamesToVirtualKeys(string key, ushort expected)
    {
        Assert.Equal(expected, ScreenInputInjector.MapKey(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F13")]
    [InlineData("Unidentified")]
    [InlineData("é")]
    public void MapKey_ReturnsNullForUnknownKeys(string? key)
    {
        Assert.Null(ScreenInputInjector.MapKey(key));
    }

    [Fact]
    public void TargetSize_KeepsSmallScreensAndScalesLargeOnes()
    {
        Assert.Equal((1920, 1080), ScreenCaptureService.TargetSize(1920, 1080));
        Assert.Equal((1920, 1080), ScreenCaptureService.TargetSize(3840, 2160));
        var (width, height) = ScreenCaptureService.TargetSize(2560, 1600);
        Assert.Equal(1920, width);
        Assert.Equal(1200, height);
    }

    // ===================== 狀態機(真正的 loopback 連線) =====================

    [Fact]
    public async Task RequestThenDecline_ReturnsBothSidesToIdleAndNotifiesRequester()
    {
        await ConnectAsync();
        var notice = new TaskCompletionSource<string>();
        _initiatorMirror.Notice += (_, message) => notice.TrySetResult(message);

        _initiatorMirror.RequestViewPeer();
        Assert.Equal(ScreenMirrorPhase.OutgoingRequest, _initiatorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));
        Assert.Equal(_initiatorMirror.Status.SessionId, _acceptorMirror.Status.SessionId);

        _acceptorMirror.RespondToIncomingRequest(accept: false, allowControl: false);

        Assert.Equal(ScreenMirrorPhase.Idle, _acceptorMirror.Status.Phase);
        Assert.Contains("拒絕", await notice.Task.WaitAsync(WaitTimeout));
        Assert.Equal(ScreenMirrorPhase.Idle, _initiatorMirror.Status.Phase);
    }

    [Fact]
    public async Task RequestThenAccept_RequesterStartsViewingWithSharerScreenInfo()
    {
        await ConnectAsync();

        _initiatorMirror.RequestViewPeer();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));

        _acceptorMirror.RespondToIncomingRequest(accept: true, allowControl: true);

        Assert.Equal(ScreenMirrorPhase.Sharing, _acceptorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _initiatorMirror.Status is { Phase: ScreenMirrorPhase.Viewing, Width: > 0 }, WaitTimeout));
        var viewing = _initiatorMirror.Status;
        Assert.Equal("windows", viewing.Platform);
        Assert.True(viewing.AllowControl);
        Assert.Equal(_acceptorMirror.Status.Width, viewing.Width);
        Assert.Equal(_acceptorMirror.Status.Height, viewing.Height);

        // 分享端取消允許操控 → 觀看端也要跟著更新。
        _acceptorMirror.SetAllowControl(false);
        Assert.True(await WaitUntilAsync(() => !_initiatorMirror.Status.AllowControl, WaitTimeout));

        // 觀看端停止 → 分享端回到 Idle。
        _initiatorMirror.Stop();
        Assert.Equal(ScreenMirrorPhase.Idle, _initiatorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.Idle, WaitTimeout));
    }

    [Fact]
    public async Task Sharing_DeliversDecodedFramesMatchingAnnouncedSize()
    {
        // 需要一個可以擷取的桌面(一般開發機/CI 的互動式工作階段);擷取不到(例如服務帳戶)時就不驗證畫面內容。
        using (var probe = new ScreenCaptureService())
        {
            if (probe.Capture() is null)
            {
                return;
            }
        }

        await ConnectAsync();
        var frames = 0;
        var firstFrame = new TaskCompletionSource<(int Width, int Height)>();
        _initiatorMirror.FrameDecoded += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            firstFrame.TrySetResult((frame.Width, frame.Height));
            frame.Release();
        };

        _initiatorMirror.RequestViewPeer();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));
        _acceptorMirror.RespondToIncomingRequest(accept: true, allowControl: false);

        var (width, height) = await firstFrame.Task.WaitAsync(WaitTimeout);
        Assert.Equal(_acceptorMirror.Status.Width, width);
        Assert.Equal(_acceptorMirror.Status.Height, height);

        _initiatorMirror.Stop();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.Idle, WaitTimeout));
    }

    [Fact]
    public async Task OfferThenAccept_SharerStartsSharingAndViewerSeesInfo()
    {
        await ConnectAsync();

        _initiatorMirror.StartShareOffer(allowControl: false);
        Assert.Equal(ScreenMirrorPhase.OutgoingOffer, _initiatorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingOffer, WaitTimeout));

        _acceptorMirror.RespondToIncomingOffer(accept: true);

        Assert.Equal(ScreenMirrorPhase.Viewing, _acceptorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _initiatorMirror.Status.Phase == ScreenMirrorPhase.Sharing, WaitTimeout));
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Width > 0, WaitTimeout));
        Assert.False(_acceptorMirror.Status.AllowControl);
    }

    [Fact]
    public async Task CancelOutgoingRequest_DismissesIncomingRequestOnOtherSide()
    {
        await ConnectAsync();
        var notice = new TaskCompletionSource<string>();
        _acceptorMirror.Notice += (_, message) => notice.TrySetResult(message);

        _initiatorMirror.RequestViewPeer();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));

        _initiatorMirror.CancelOutgoing();

        Assert.Equal(ScreenMirrorPhase.Idle, _initiatorMirror.Status.Phase);
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.Idle, WaitTimeout));
        Assert.Contains("取消", await notice.Task.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task RequestWhileAnotherSessionIsPending_IsIgnored()
    {
        await ConnectAsync();

        _initiatorMirror.RequestViewPeer();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));
        var firstSession = _acceptorMirror.Status.SessionId;

        // 接受端此時也想看對方:自己不是 Idle,控制器本身就不會送出。
        _acceptorMirror.RequestViewPeer();
        Assert.Equal(ScreenMirrorPhase.IncomingRequest, _acceptorMirror.Status.Phase);
        Assert.Equal(firstSession, _acceptorMirror.Status.SessionId);
    }

    [Fact]
    public async Task ConnectionClosed_ResetsSessionToIdle()
    {
        await ConnectAsync();

        _initiatorMirror.RequestViewPeer();
        Assert.True(await WaitUntilAsync(() => _acceptorMirror.Status.Phase == ScreenMirrorPhase.IncomingRequest, WaitTimeout));

        _initiatorMirror.OnConnectionClosed();
        _acceptorMirror.OnConnectionClosed();

        Assert.Equal(ScreenMirrorPhase.Idle, _initiatorMirror.Status.Phase);
        Assert.Equal(ScreenMirrorPhase.Idle, _acceptorMirror.Status.Phase);
    }

    [Fact]
    public void RequestWithoutConnection_DoesNothing()
    {
        _initiatorMirror.RequestViewPeer();
        _initiatorMirror.StartShareOffer(allowControl: true);

        Assert.Equal(ScreenMirrorPhase.Idle, _initiatorMirror.Status.Phase);
    }
}
