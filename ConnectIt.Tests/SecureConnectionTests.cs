using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>協定第 2 版:配對連線一律走雙向 TLS,兩邊算出同一組配對碼,信任清單只認憑證指紋。</summary>
public class SecureConnectionTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionService _acceptor = new();
    private readonly ConnectionService _initiator = new();

    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ConnectItTests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        _acceptor.Dispose();
        _initiator.Dispose();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 測試環境清不掉暫存目錄不影響測試結果本身。
        }
    }

    // 跟 Android 端 DeviceIdentityTest 用同一組測試向量,確保兩邊算出一樣的配對碼。
    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "979741")]
    [InlineData("0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f", "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1", "328450")]
    public void PairingCode_MatchesSharedTestVectors_RegardlessOfOrder(string a, string b, string expected)
    {
        Assert.Equal(expected, DeviceIdentity.PairingCode(a, b));
        Assert.Equal(expected, DeviceIdentity.PairingCode(b, a));
    }

    [Fact]
    public async Task BothSides_SeeTheSamePairingCode_AndEachOthersFingerprint()
    {
        var port = _acceptor.StartListening();
        ConnectionRequestedEventArgs? request = null;
        _acceptor.ConnectionRequested += (_, e) =>
        {
            request = e;
            e.Respond(true);
        };

        string? initiatorCode = null;
        var outcome = await _initiator.RequestConnectionWithOutcomeAsync(
            IPAddress.Loopback, port, "Initiator", "Acceptor", code => initiatorCode = code);

        Assert.Equal(ConnectionRequestOutcome.Accepted, outcome);
        Assert.NotNull(request);
        Assert.Matches("^[0-9]{6}$", request!.PairingCode);
        Assert.Equal(request.PairingCode, initiatorCode);
        Assert.Equal(_initiator.Identity.Fingerprint, request.RequesterFingerprint);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected));
        Assert.Equal(_acceptor.Identity.Fingerprint, _initiator.Sessions.Single().RemoteFingerprint);
        Assert.Equal(request.PairingCode, _acceptor.Sessions.Single().PairingCode);
    }

    [Fact]
    public async Task LegacyPlaintextRequest_IsRejectedWithUpgradeRequired()
    {
        var port = _acceptor.StartListening();
        var asked = false;
        _acceptor.ConnectionRequested += (_, e) =>
        {
            asked = true;
            e.Respond(true);
        };

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"Type\":\"request\",\"DeviceName\":\"Old\"}\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync().WaitAsync(WaitTimeout);

        var reply = JsonSerializer.Deserialize<ConnectMessage>(line!);
        Assert.Equal("reject", reply!.Type);
        Assert.Equal(ConnectionService.UpgradeRequiredReason, reply.Reason);
        Assert.False(asked);
        Assert.False(_acceptor.IsConnected);
    }

    [Fact]
    public async Task ServerThatDoesNotSpeakTls_ReportsIncompatibleVersion()
    {
        // 模擬舊版接受端:把收到的東西當成一行文字讀,讀不到換行就關掉連線。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await Task.Delay(300);
            server.Close();
        });

        var outcome = await _initiator.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, port, "Initiator", "Old");

        listener.Stop();
        Assert.Equal(ConnectionRequestOutcome.IncompatibleVersion, outcome);
    }

    [Fact]
    public void TrustedDevices_MatchByFingerprintOnly_AndPersist()
    {
        var path = Path.Combine(_tempDirectory, "trusted.json");
        var trusted = new TrustedDevicesService(path);
        trusted.Trust("abc", "我的手機", "device-1");

        Assert.True(trusted.IsTrusted("abc"));
        Assert.True(trusted.IsTrusted("ABC"));
        Assert.False(trusted.IsTrusted("def"));

        // 同一台裝置(相同 DeviceId)換了金鑰重新配對:舊的指紋被取代。
        trusted.Trust("def", "我的手機", "device-1");
        Assert.False(trusted.IsTrusted("abc"));

        var reloaded = new TrustedDevicesService(path);
        Assert.Equal([new TrustedDevice("def", "我的手機", "device-1")], reloaded.TrustedDevices);

        reloaded.Untrust("def");
        Assert.Empty(new TrustedDevicesService(path).TrustedDevices);
    }

    [Fact]
    public void Unattended_RequiresMasterSwitchAndPerDeviceFlag_AndPersists()
    {
        var path = Path.Combine(_tempDirectory, "trusted-unattended.json");
        var trusted = new TrustedDevicesService(path);
        trusted.Trust("abc", "我的手機", "device-1");
        Assert.False(trusted.IsUnattendedAllowed("abc"));

        // 只開個別裝置、沒開總開關:不生效。
        trusted.SetAllowUnattended("ABC", true);
        Assert.False(trusted.IsUnattendedAllowed("abc"));

        trusted.SetUnattendedEnabled(true);
        Assert.True(trusted.IsUnattendedAllowed("abc"));
        Assert.False(trusted.IsUnattendedAllowed("def"));

        var reloaded = new TrustedDevicesService(path);
        Assert.True(reloaded.UnattendedEnabled);
        Assert.True(reloaded.IsUnattendedAllowed("abc"));

        // 關掉總開關立刻撤銷,但個別裝置的勾選保留。
        reloaded.SetUnattendedEnabled(false);
        Assert.False(reloaded.IsUnattendedAllowed("abc"));
        Assert.True(reloaded.TrustedDevices.Single().AllowUnattended);
    }

    [Fact]
    public void Unattended_KeptOnRename_ButNotWhenKeyChanges()
    {
        var path = Path.Combine(_tempDirectory, "trusted-rekey.json");
        var trusted = new TrustedDevicesService(path);
        trusted.SetUnattendedEnabled(true);
        trusted.Trust("abc", "舊名字", "device-1");
        trusted.SetAllowUnattended("abc", true);

        var changes = 0;
        trusted.Changed += (_, _) => changes++;

        trusted.Trust("abc", "新名字", "device-1");
        Assert.True(trusted.IsUnattendedAllowed("abc"));

        // 同一個 DeviceId 換了金鑰:要重新允許,不能沿用。
        trusted.Trust("def", "新名字", "device-1");
        Assert.False(trusted.IsUnattendedAllowed("def"));
        Assert.Equal(2, changes);

        trusted.Untrust("def");
        Assert.False(trusted.IsUnattendedAllowed("def"));
    }

    [Fact]
    public void Identity_IsPersistedAndReloaded()
    {
        var path = Path.Combine(_tempDirectory, "identity.bin");
        var first = DeviceIdentity.LoadOrCreate(path);
        var second = DeviceIdentity.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.True(second.Certificate.HasPrivateKey);
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
