using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class FileTransferTests : IDisposable
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

    /// <summary>建立好初始端/接受端之間的連線,兩邊都進入已連線狀態。</summary>
    private async Task ConnectAsync()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "Initiator", "Acceptor");

        Assert.True(accepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));
    }

    private string CreateTempSourceFile(string fileName, int sizeInBytes)
    {
        var path = Path.Combine(_tempDirectory, "src-" + fileName);
        var bytes = new byte[sizeInBytes];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CreateDownloadFolder(string suffix)
    {
        return Directory.CreateDirectory(Path.Combine(_tempDirectory, "download-" + suffix)).FullName;
    }

    [Fact]
    public async Task SendFileAsync_WhenAccepted_DeliversIdenticalFileToReceiver()
    {
        await ConnectAsync();

        var sourcePath = CreateTempSourceFile("hello.txt", 12_345);
        var downloadFolder = CreateDownloadFolder("happy");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, true, downloadFolder);

        FileTransferEndedEventArgs? senderEnded = null;
        FileTransferEndedEventArgs? receiverEnded = null;
        _initiator.FileTransferEnded += (_, e) => senderEnded = e;
        _acceptor.FileTransferEnded += (_, e) => receiverEnded = e;

        await _initiator.SendFileAsync(sourcePath);

        Assert.True(await WaitUntilAsync(() => senderEnded != null && receiverEnded != null, WaitTimeout));

        Assert.Equal(FileTransferEndReason.Completed, senderEnded!.Reason);
        Assert.Equal(FileTransferEndReason.Completed, receiverEnded!.Reason);
        Assert.Equal(FileTransferDirection.Sending, senderEnded.Direction);
        Assert.Equal(FileTransferDirection.Receiving, receiverEnded.Direction);

        Assert.NotNull(receiverEnded.SavedFilePath);
        Assert.True(File.Exists(receiverEnded.SavedFilePath));
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(receiverEnded.SavedFilePath!));
    }

    [Fact]
    public async Task SendFileAsync_WhenRejected_SenderGetsRejectedAndNothingIsSaved()
    {
        await ConnectAsync();

        var sourcePath = CreateTempSourceFile("secret.txt", 1024);
        var downloadFolder = CreateDownloadFolder("reject");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, false, downloadFolder);

        FileTransferEndedEventArgs? senderEnded = null;
        _initiator.FileTransferEnded += (_, e) => senderEnded = e;

        await _initiator.SendFileAsync(sourcePath);

        Assert.True(await WaitUntilAsync(() => senderEnded != null, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Rejected, senderEnded!.Reason);
        Assert.Empty(Directory.GetFiles(downloadFolder));
    }

    [Fact]
    public async Task CancelFileTransfer_MidTransfer_EndsOnBothSidesAndDeletesPartialFile()
    {
        await ConnectAsync();

        // 用夠大的檔案(遠大於單一區塊)確保在第一個進度事件時傳輸還沒完成,取消才有意義。
        var sourcePath = CreateTempSourceFile("big.bin", 5 * 1024 * 1024);
        var downloadFolder = CreateDownloadFolder("cancel");
        var expectedDestinationPath = Path.Combine(downloadFolder, "big.bin");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, true, downloadFolder);

        FileTransferEndedEventArgs? senderEnded = null;
        FileTransferEndedEventArgs? receiverEnded = null;
        _initiator.FileTransferEnded += (_, e) => senderEnded = e;
        _acceptor.FileTransferEnded += (_, e) => receiverEnded = e;

        var cancelled = false;
        _initiator.FileTransferProgress += (_, _) =>
        {
            if (!cancelled)
            {
                cancelled = true;
                _initiator.CancelFileTransfer();
            }
        };

        await _initiator.SendFileAsync(sourcePath);

        Assert.True(await WaitUntilAsync(() => senderEnded != null && receiverEnded != null, WaitTimeout));

        Assert.Equal(FileTransferEndReason.Cancelled, senderEnded!.Reason);
        Assert.Equal(FileTransferEndReason.CancelledByRemote, receiverEnded!.Reason);
        Assert.True(await WaitUntilAsync(() => !File.Exists(expectedDestinationPath), WaitTimeout));
    }

    [Fact]
    public async Task SendFileAsync_WhileAnotherTransferIsActive_IsAutoRejectedWithoutDisturbingTheFirst()
    {
        await ConnectAsync();

        var firstSourcePath = CreateTempSourceFile("first.bin", 3 * 1024 * 1024);
        var secondSourcePath = CreateTempSourceFile("second.txt", 512);
        var downloadFolder = CreateDownloadFolder("busy");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, true, downloadFolder);

        FileTransferEndedEventArgs? firstSenderEnded = null;
        _initiator.FileTransferEnded += (_, e) => firstSenderEnded = e;

        await _initiator.SendFileAsync(firstSourcePath);

        // 等到接收端真的已經在忙(收到 offer 並接受)之後,再嘗試送出第二個檔案。
        Assert.True(await WaitUntilAsync(() => _acceptor.IsFileTransferActive, WaitTimeout));

        await _initiator.SendFileAsync(secondSourcePath);

        Assert.True(await WaitUntilAsync(() => firstSenderEnded != null, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Completed, firstSenderEnded!.Reason);
        Assert.DoesNotContain("second.txt", Directory.GetFiles(downloadFolder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SendFileAsync_WhenDestinationFileNameAlreadyExists_SavesWithDeduplicatedName()
    {
        await ConnectAsync();

        var sourcePath = CreateTempSourceFile("report.txt", 2048);
        var downloadFolder = CreateDownloadFolder("dedupe");
        var existingPath = Path.Combine(downloadFolder, Path.GetFileName(sourcePath));
        File.WriteAllText(existingPath, "already here");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, true, downloadFolder);

        FileTransferEndedEventArgs? receiverEnded = null;
        _acceptor.FileTransferEnded += (_, e) => receiverEnded = e;

        await _initiator.SendFileAsync(sourcePath);

        Assert.True(await WaitUntilAsync(() => receiverEnded != null, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Completed, receiverEnded!.Reason);
        Assert.NotNull(receiverEnded.SavedFilePath);
        var expectedDedupedName = $"{Path.GetFileNameWithoutExtension(sourcePath)} (1){Path.GetExtension(sourcePath)}";
        Assert.NotEqual(existingPath, receiverEnded.SavedFilePath);
        Assert.Equal(expectedDedupedName, Path.GetFileName(receiverEnded.SavedFilePath));
        Assert.Equal("already here", File.ReadAllText(existingPath));
    }
}
