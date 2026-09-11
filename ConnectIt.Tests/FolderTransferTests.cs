using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class FolderTransferTests : IDisposable
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

    private async Task ConnectAsync()
    {
        var port = _acceptor.StartListening();
        _acceptor.ConnectionRequested += (_, e) => e.Respond(true);

        var accepted = await _initiator.RequestConnectionAsync(IPAddress.Loopback, port, "Initiator", "Acceptor");

        Assert.True(accepted);
        Assert.True(await WaitUntilAsync(() => _acceptor.IsConnected, WaitTimeout));
    }

    /// <summary>建立一個含子目錄的來源資料夾:root/a.txt、root/sub/b.txt。</summary>
    private string CreateSourceFolder(string suffix, int fileSizeInBytes = 2048)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src-" + suffix)).FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        var random = new Random(42);
        File.WriteAllBytes(Path.Combine(root, "a.txt"), RandomBytes(random, fileSizeInBytes));
        File.WriteAllBytes(Path.Combine(root, "sub", "b.txt"), RandomBytes(random, fileSizeInBytes));

        return root;
    }

    private static byte[] RandomBytes(Random random, int size)
    {
        var bytes = new byte[size];
        random.NextBytes(bytes);
        return bytes;
    }

    private string CreateDownloadFolder(string suffix) =>
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "download-" + suffix)).FullName;

    [Fact]
    public async Task SendFolderAsync_WhenAccepted_DeliversAllFilesIncludingSubdirectories()
    {
        await ConnectAsync();

        var sourceFolder = CreateSourceFolder("happy");
        var downloadFolder = CreateDownloadFolder("happy");

        _acceptor.FolderOffered += (_, e) => _acceptor.RespondToFolderOffer(e.TransferId, true, downloadFolder);

        FolderTransferEndedEventArgs? senderEnded = null;
        FolderTransferEndedEventArgs? receiverEnded = null;
        _initiator.FolderTransferEnded += (_, e) => senderEnded = e;
        _acceptor.FolderTransferEnded += (_, e) => receiverEnded = e;

        await _initiator.SendFolderAsync(sourceFolder);

        Assert.True(await WaitUntilAsync(() => senderEnded != null && receiverEnded != null, WaitTimeout));

        Assert.Equal(FileTransferEndReason.Completed, senderEnded!.Reason);
        Assert.Equal(FileTransferEndReason.Completed, receiverEnded!.Reason);
        Assert.Equal(2, senderEnded.EntryCount);
        Assert.Equal(2, receiverEnded.EntryCount);

        Assert.NotNull(receiverEnded.SavedFolderPath);
        var savedRoot = receiverEnded.SavedFolderPath!;
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(sourceFolder, "a.txt")),
            File.ReadAllBytes(Path.Combine(savedRoot, "a.txt")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(sourceFolder, "sub", "b.txt")),
            File.ReadAllBytes(Path.Combine(savedRoot, "sub", "b.txt")));
    }

    [Fact]
    public async Task SendFolderAsync_WhenRejected_SenderGetsRejectedAndNothingIsSaved()
    {
        await ConnectAsync();

        var sourceFolder = CreateSourceFolder("reject");
        var downloadFolder = CreateDownloadFolder("reject");

        _acceptor.FolderOffered += (_, e) => _acceptor.RespondToFolderOffer(e.TransferId, false, downloadFolder);

        FolderTransferEndedEventArgs? senderEnded = null;
        _initiator.FolderTransferEnded += (_, e) => senderEnded = e;

        await _initiator.SendFolderAsync(sourceFolder);

        Assert.True(await WaitUntilAsync(() => senderEnded != null, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Rejected, senderEnded!.Reason);
        Assert.Empty(Directory.GetDirectories(downloadFolder));
        Assert.Empty(Directory.GetFiles(downloadFolder));
    }

    [Fact]
    public async Task CancelFileTransfer_MidFolderTransfer_EndsFolderOnBothSides()
    {
        await ConnectAsync();

        // 用夠大的檔案確保在第一個進度事件時傳輸還沒完成,取消才有意義。
        var sourceFolder = CreateSourceFolder("cancel", fileSizeInBytes: 5 * 1024 * 1024);
        var downloadFolder = CreateDownloadFolder("cancel");

        _acceptor.FolderOffered += (_, e) => _acceptor.RespondToFolderOffer(e.TransferId, true, downloadFolder);

        FolderTransferEndedEventArgs? senderEnded = null;
        FolderTransferEndedEventArgs? receiverEnded = null;
        _initiator.FolderTransferEnded += (_, e) => senderEnded = e;
        _acceptor.FolderTransferEnded += (_, e) => receiverEnded = e;

        var cancelled = false;
        _initiator.FileTransferProgress += (_, _) =>
        {
            if (!cancelled)
            {
                cancelled = true;
                _initiator.CancelFileTransfer();
            }
        };

        await _initiator.SendFolderAsync(sourceFolder);

        Assert.True(await WaitUntilAsync(() => senderEnded != null && receiverEnded != null, WaitTimeout));

        Assert.Equal(FileTransferEndReason.Cancelled, senderEnded!.Reason);
        Assert.Equal(FileTransferEndReason.CancelledByRemote, receiverEnded!.Reason);
        Assert.False(_initiator.IsFileTransferActive);
        Assert.False(_acceptor.IsFileTransferActive);
    }

    private string CreateTempSourceFile(string fileName, int sizeInBytes)
    {
        var path = Path.Combine(_tempDirectory, "src-" + fileName);
        var bytes = new byte[sizeInBytes];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task SendFilesAsync_WithMultipleFiles_WhenAccepted_DeliversAllFilesFlatIntoDownloadFolder()
    {
        await ConnectAsync();

        var firstSource = CreateTempSourceFile("one.txt", 1024);
        var secondSource = CreateTempSourceFile("two.txt", 2048);
        var downloadFolder = CreateDownloadFolder("batch-happy");

        _acceptor.FolderOffered += (_, e) => _acceptor.RespondToFolderOffer(e.TransferId, true, downloadFolder);

        FolderOfferedEventArgs? offer = null;
        _acceptor.FolderOffered += (_, e) => offer = e;

        FolderTransferEndedEventArgs? senderEnded = null;
        FolderTransferEndedEventArgs? receiverEnded = null;
        _initiator.FolderTransferEnded += (_, e) => senderEnded = e;
        _acceptor.FolderTransferEnded += (_, e) => receiverEnded = e;

        await _initiator.SendFilesAsync([firstSource, secondSource]);

        Assert.True(await WaitUntilAsync(() => senderEnded != null && receiverEnded != null, WaitTimeout));

        Assert.True(offer!.IsBatch);
        Assert.Equal(FileTransferEndReason.Completed, senderEnded!.Reason);
        Assert.Equal(FileTransferEndReason.Completed, receiverEnded!.Reason);
        Assert.True(senderEnded.IsBatch);
        Assert.True(receiverEnded.IsBatch);
        Assert.Equal(2, receiverEnded.EntryCount);

        // 批次傳送不應該在下載資料夾裡多包一層子資料夾,檔案要直接落在 downloadFolder 底下。
        Assert.Equal(downloadFolder, receiverEnded.SavedFolderPath);
        Assert.Equal(
            File.ReadAllBytes(firstSource),
            File.ReadAllBytes(Path.Combine(downloadFolder, Path.GetFileName(firstSource))));
        Assert.Equal(
            File.ReadAllBytes(secondSource),
            File.ReadAllBytes(Path.Combine(downloadFolder, Path.GetFileName(secondSource))));
    }

    [Fact]
    public async Task SendFilesAsync_WithSingleFile_FallsBackToSingleFileEvents()
    {
        await ConnectAsync();

        var sourcePath = CreateTempSourceFile("solo.txt", 512);
        var downloadFolder = CreateDownloadFolder("batch-single");

        _acceptor.FileOffered += (_, e) => _acceptor.RespondToFileOffer(e.TransferId, true, downloadFolder);

        FileTransferEndedEventArgs? fileEnded = null;
        FolderTransferEndedEventArgs? folderEnded = null;
        _initiator.FileTransferEnded += (_, e) => fileEnded = e;
        _initiator.FolderTransferEnded += (_, e) => folderEnded = e;

        await _initiator.SendFilesAsync([sourcePath]);

        Assert.True(await WaitUntilAsync(() => fileEnded != null, WaitTimeout));
        Assert.Equal(FileTransferEndReason.Completed, fileEnded!.Reason);
        Assert.Null(folderEnded);
    }
}
