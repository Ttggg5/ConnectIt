using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>裝置名稱/首次啟動引導(<see cref="DeviceSettingsService"/>)與「繼續觀看」(<see cref="RecentlyWatchedService"/>)的本機儲存。</summary>
public class LocalPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ConnectItTests_" + Guid.NewGuid().ToString("N"));

    public LocalPreferencesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 測試暫存資料夾刪不掉不影響結果。
        }
    }

    private string FilePath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void DeviceSettings_DefaultsToMachineNameAndOnboardingNotCompleted()
    {
        var settings = new DeviceSettingsService(FilePath("device.json"));

        Assert.Equal(Environment.MachineName, settings.DeviceName);
        Assert.False(settings.OnboardingCompleted);
    }

    [Fact]
    public void DeviceSettings_PersistsNameAndOnboardingAcrossInstances()
    {
        var path = FilePath("device.json");
        var settings = new DeviceSettingsService(path);
        settings.SetDeviceName("  客廳電腦  ");
        settings.SetOnboardingCompleted(true);

        var reloaded = new DeviceSettingsService(path);

        Assert.Equal("客廳電腦", reloaded.DeviceName);
        Assert.True(reloaded.OnboardingCompleted);
    }

    [Fact]
    public void DeviceSettings_BlankNameFallsBackToMachineNameAndLongNameIsTruncated()
    {
        var settings = new DeviceSettingsService(FilePath("device.json"));

        Assert.Equal(Environment.MachineName, settings.SetDeviceName("   "));
        Assert.Equal(DeviceSettingsService.MaxDeviceNameLength, settings.SetDeviceName(new string('a', 100)).Length);
    }

    [Fact]
    public void RecentlyWatched_RecordsOnlyWatchPagesAndPersists()
    {
        var path = FilePath("recent.json");
        var service = new RecentlyWatchedService(path);

        Assert.False(service.Record("書房電腦", new Uri("http://192.168.0.5:5000/?sort=name_asc"), "首頁"));
        Assert.Null(service.Last);

        Assert.True(service.Record("書房電腦", new Uri("http://192.168.0.5:5000/watch?v=2&sort=name_asc"), "海邊日落.mp4"));

        var reloaded = new RecentlyWatchedService(path);
        Assert.NotNull(reloaded.Last);
        Assert.Equal("書房電腦", reloaded.Last!.ServerName);
        Assert.Equal("/watch?v=2&sort=name_asc", reloaded.Last.PathAndQuery);
        Assert.Equal("海邊日落.mp4", reloaded.Last.Title);
    }

    [Fact]
    public void RecentlyWatched_ClearRemovesTheEntry()
    {
        var path = FilePath("recent.json");
        var service = new RecentlyWatchedService(path);
        service.Record("書房電腦", new Uri("http://192.168.0.5:5000/watch?v=0"), null);

        service.Clear();

        Assert.Null(service.Last);
        Assert.Null(new RecentlyWatchedService(path).Last);
    }
}
