using ConnectIt.Wpf.Services;
using Microsoft.Win32;

namespace ConnectIt.Tests;

/// <summary>快速傳送:命令列參數、檔案總管整合的登錄檔/捷徑,以及把多次啟動合併成一批的具名管道。</summary>
public class QuickSendTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ConnectItTests-" + Guid.NewGuid().ToString("N"))).FullName;

    // 測試只動 HKCU\Software\ConnectItTests\<隨機> 底下,不碰真正的右鍵選單。
    private readonly string _registryRoot = $@"Software\ConnectItTests\{Guid.NewGuid():N}\";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot.TrimEnd('\\'), throwOnMissingSubKey: false);
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 測試環境清不掉暫存目錄不影響測試結果本身。
        }
    }

    [Fact]
    public void ParseSendArgs_ReturnsPathsAfterSendFlag()
    {
        Assert.Null(ShellIntegrationService.ParseSendArgs(["--minimized"]));
        Assert.Empty(ShellIntegrationService.ParseSendArgs(["--send"])!);
        Assert.Equal([@"C:\a.txt", @"D:\b c.png"], ShellIntegrationService.ParseSendArgs(["--send", @"C:\a.txt", "\"D:\\b c.png\""])!);
        Assert.Equal([@"C:\x"], ShellIntegrationService.ParseSendArgs(["--SEND", @"C:\x", " "])!);
    }

    [Fact]
    public void EnableThenDisable_WritesAndRemovesVerbs()
    {
        var settings = Path.Combine(_tempDirectory, "shell.json");
        var sendTo = Path.Combine(_tempDirectory, "SendTo");
        var service = new ShellIntegrationService(settings, _registryRoot, sendTo);
        const string exe = @"C:\Apps\ConnectIt\ConnectIt.Wpf.exe";

        service.SetEnabled(true, exe);

        Assert.True(new ShellIntegrationService(settings, _registryRoot, sendTo).Enabled);
        Assert.Equal($"\"{exe}\" --send \"%1\"", service.ReadCommand(0));
        Assert.Equal($"\"{exe}\" --send \"%1\"", service.ReadCommand(1));
        Assert.True(File.Exists(service.SendToShortcutPath));

        // 程式搬到別的地方:重新整理會用新的路徑覆寫。
        service.RefreshIfEnabled(@"D:\New\ConnectIt.Wpf.exe");
        Assert.Contains(@"D:\New\ConnectIt.Wpf.exe", service.ReadCommand(0));

        service.SetEnabled(false, exe);

        Assert.Null(service.ReadCommand(0));
        Assert.Null(service.ReadCommand(1));
        Assert.False(File.Exists(service.SendToShortcutPath));
        Assert.False(new ShellIntegrationService(settings, _registryRoot, sendTo).Enabled);
    }

    [Fact]
    public async Task SeparateLaunches_AreMergedIntoOneBatch()
    {
        var pipe = "ConnectItTests-" + Guid.NewGuid().ToString("N");
        using var server = new SendRequestServer(pipe);
        var batches = new List<IReadOnlyList<string>>();
        var received = new TaskCompletionSource();
        server.BatchReady += (_, paths) =>
        {
            lock (batches)
            {
                batches.Add(paths);
            }

            received.TrySetResult();
        };
        server.Start();

        // 檔案總管對每個選取的檔案各啟動一次 ConnectIt。
        Assert.True(SendRequestServer.TrySend(pipe, [@"C:\a.txt"], TimeSpan.FromSeconds(5)));
        Assert.True(SendRequestServer.TrySend(pipe, [@"C:\b.txt"], TimeSpan.FromSeconds(5)));
        Assert.True(SendRequestServer.TrySend(pipe, [@"C:\A.TXT"], TimeSpan.FromSeconds(5)));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(SendRequestServer.BatchWindow * 2);

        var batch = Assert.Single(batches);
        Assert.Equal(2, batch.Count);
        Assert.Contains(@"C:\b.txt", batch);
    }
}
