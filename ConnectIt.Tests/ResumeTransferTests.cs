using System.Net;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

/// <summary>傳輸中斷後續傳:接收端保留部分檔案,同一台裝置重新連線後從中斷處接著傳,最後內容逐位元組一致。</summary>
public class ResumeTransferTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ConnectItTests-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly DeviceIdentity _receiverIdentity = DeviceIdentity.CreateEphemeral();
    private readonly DeviceIdentity _senderIdentity = DeviceIdentity.CreateEphemeral();
    private readonly ResumeStore _receiverStore;
    private readonly ConnectionService _receiver;
    private readonly List<ConnectionService> _senders = [];
    private readonly string _downloadFolder;

    public ResumeTransferTests()
    {
        _receiverStore = new ResumeStore(Path.Combine(_tempDirectory, "resume.json"));
        _receiver = new ConnectionService { LocalDeviceId = "receiver", Identity = _receiverIdentity, ResumeStore = _receiverStore };
        _downloadFolder = Directory.CreateDirectory(Path.Combine(_tempDirectory, "download")).FullName;
        _receiver.ConnectionRequested += (_, e) => e.Respond(true);
        _receiver.FileOffered += (_, e) => _receiver.RespondToFileOffer(e.TransferId, true, _downloadFolder);
        _receiver.FolderOffered += (_, e) => _receiver.RespondToFolderOffer(e.TransferId, true, _downloadFolder);
    }

    public void Dispose()
    {
        _receiver.Dispose();
        foreach (var sender in _senders)
        {
            sender.Dispose();
        }

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 測試環境清不掉暫存目錄不影響測試結果本身。
        }
    }

    /// <summary>每次「重新連線」都用新的 ConnectionService,但 DeviceId 與身分不變(就像同一台裝置重新開 App)。</summary>
    private async Task<PeerSession> ConnectSenderAsync(int port)
    {
        var sender = new ConnectionService { LocalDeviceId = "sender", Identity = _senderIdentity };
        _senders.Add(sender);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sender.StatusChanged += (_, m) => log.Enqueue("S: " + m);
        _receiver.StatusChanged += (_, m) => log.Enqueue("R: " + m);
        var outcome = await sender.RequestConnectionWithOutcomeAsync(IPAddress.Loopback, port, "Sender", "Receiver");
        Assert.True(outcome == ConnectionRequestOutcome.Accepted, outcome + " | " + string.Join(" | ", log));
        Assert.True(await WaitUntilAsync(() => _receiver.Sessions.Any(s => s.RemoteDeviceId == "sender" && !s.IsClosed)));
        return sender.Sessions.Single();
    }

    private string CreateSourceFile(string relativePath, int size, int seed)
    {
        var path = Path.Combine(_tempDirectory, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task InterruptedFile_ResumesFromOffset_AndArrivesIntact()
    {
        var port = _receiver.StartListening();
        var source = CreateSourceFile("big.bin", 24 * 1024 * 1024, 7);
        var first = await ConnectSenderAsync(port);

        PendingSend? pending = null;
        first.TransferInterrupted += (_, e) => pending = e;
        var interruptedOnReceiver = new TaskCompletionSource<FileTransferEndedEventArgs>();
        EventHandler<FileTransferEndedEventArgs> onEnded = (_, e) => interruptedOnReceiver.TrySetResult(e);
        _receiver.FileTransferEnded += onEnded;
        var cut = 0;
        _receiver.FileTransferProgress += (_, e) =>
        {
            if (e.BytesTransferred > 2 * 1024 * 1024 && Interlocked.Exchange(ref cut, 1) == 0)
            {
                first.Disconnect();
            }
        };

        await first.SendFileAsync(source);
        var interrupted = await interruptedOnReceiver.Task.WaitAsync(WaitTimeout);
        _receiver.FileTransferEnded -= onEnded;

        Assert.Equal(FileTransferEndReason.ConnectionClosed, interrupted.Reason);
        Assert.True(interrupted.Resumable);
        Assert.True(await WaitUntilAsync(() => pending != null));
        var entry = Assert.Single(_receiverStore.Entries);
        var keptBytes = new FileInfo(entry.PartialPath).Length;
        Assert.InRange(keptBytes, 1, new FileInfo(source).Length - 1);
        Assert.False(File.Exists(entry.FinalPath)); // 還沒收完,不能出現看起來完整的正式檔名

        // 重新連線並續傳:第一個進度事件就應該從保留的位置開始,不是從 0。
        long? firstProgress = null;
        _receiver.FileTransferProgress += (_, e) => firstProgress ??= e.BytesTransferred;
        var completed = new TaskCompletionSource<FileTransferEndedEventArgs>();
        _receiver.FileTransferEnded += (_, e) => completed.TrySetResult(e);

        var second = await ConnectSenderAsync(port);
        await second.ResumeSendAsync(pending!);
        var done = await completed.Task.WaitAsync(WaitTimeout);

        Assert.Equal(FileTransferEndReason.Completed, done.Reason);
        Assert.True(firstProgress > keptBytes);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(done.SavedFilePath!));
        Assert.Equal(Path.Combine(_downloadFolder, "big.bin"), done.SavedFilePath);
        Assert.Empty(_receiverStore.Entries);
        Assert.False(File.Exists(entry.PartialPath));
    }

    [Fact]
    public async Task InterruptedFolder_ResumesIntoSameFolder_WithoutResendingFinishedFiles()
    {
        var port = _receiver.StartListening();
        var small = CreateSourceFile(Path.Combine("album", "a-small.bin"), 64 * 1024, 1);
        var big = CreateSourceFile(Path.Combine("album", "sub", "b-big.bin"), 24 * 1024 * 1024, 2);
        var folder = Path.GetDirectoryName(small)!;
        var first = await ConnectSenderAsync(port);

        PendingSend? pending = null;
        first.TransferInterrupted += (_, e) => pending = e;
        var interrupted = new TaskCompletionSource<FolderTransferEndedEventArgs>();
        EventHandler<FolderTransferEndedEventArgs> onFolderEnded = (_, e) => interrupted.TrySetResult(e);
        _receiver.FolderTransferEnded += onFolderEnded;
        var cut = 0;
        _receiver.FileTransferProgress += (_, e) =>
        {
            if (e.FileName == "b-big.bin" && e.BytesTransferred > 2 * 1024 * 1024 && Interlocked.Exchange(ref cut, 1) == 0)
            {
                first.Disconnect();
            }
        };

        await first.SendFolderAsync(folder);
        var firstEnd = await interrupted.Task.WaitAsync(WaitTimeout);
        _receiver.FolderTransferEnded -= onFolderEnded;
        Assert.True(firstEnd.Resumable);
        Assert.True(await WaitUntilAsync(() => pending != null));
        Assert.Contains("a-small.bin", pending!.CompletedRelativePaths);

        var completed = new TaskCompletionSource<FolderTransferEndedEventArgs>();
        _receiver.FolderTransferEnded += (_, e) => completed.TrySetResult(e);
        var second = await ConnectSenderAsync(port);
        await second.ResumeSendAsync(pending);
        var done = await completed.Task.WaitAsync(WaitTimeout);

        Assert.Equal(FileTransferEndReason.Completed, done.Reason);
        Assert.Equal(1, done.EntryCount); // 只補送沒收完的那一個
        var root = Path.Combine(_downloadFolder, "album");
        Assert.Equal(root, done.SavedFolderPath);
        Assert.Equal(File.ReadAllBytes(small), File.ReadAllBytes(Path.Combine(root, "a-small.bin")));
        Assert.Equal(File.ReadAllBytes(big), File.ReadAllBytes(Path.Combine(root, "sub", "b-big.bin")));
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
        Assert.Single(Directory.GetDirectories(_downloadFolder)); // 沒有另外開「album (1)」
    }

    [Fact]
    public async Task ModifiedSource_IsSentFromScratch()
    {
        var port = _receiver.StartListening();
        var source = CreateSourceFile("doc.bin", 24 * 1024 * 1024, 3);
        var first = await ConnectSenderAsync(port);
        PendingSend? pending = null;
        first.TransferInterrupted += (_, e) => pending = e;
        var cut = 0;
        _receiver.FileTransferProgress += (_, e) =>
        {
            if (e.BytesTransferred > 2 * 1024 * 1024 && Interlocked.Exchange(ref cut, 1) == 0)
            {
                first.Disconnect();
            }
        };
        await first.SendFileAsync(source);
        Assert.True(await WaitUntilAsync(() => pending != null && _receiverStore.Entries.Count == 1));

        // 來源檔案在斷線期間被改過:ResumeKey 不同,不能接在舊內容後面。
        var changed = new byte[1024 * 1024];
        new Random(99).NextBytes(changed);
        File.WriteAllBytes(source, changed);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));

        var completed = new TaskCompletionSource<FileTransferEndedEventArgs>();
        _receiver.FileTransferEnded += (_, e) =>
        {
            if (e.Reason == FileTransferEndReason.Completed)
            {
                completed.TrySetResult(e);
            }
        };
        var second = await ConnectSenderAsync(port);
        await second.ResumeSendAsync(pending!);
        var done = await completed.Task.WaitAsync(WaitTimeout);

        Assert.Equal(changed, File.ReadAllBytes(done.SavedFilePath!));
    }

    [Fact]
    public void ResumeStore_PurgesExpiredEntriesAndTheirPartialFiles()
    {
        var path = Path.Combine(_tempDirectory, "store.json");
        var partial = Path.Combine(_tempDirectory, "old.bin.part");
        File.WriteAllText(partial, "x");
        var store = new ResumeStore(path);
        store.Save(new ResumeEntry("peer", "old", partial, partial[..^5], false, DateTime.UtcNow - ResumeStore.MaxAge - TimeSpan.FromMinutes(1)));
        store.Save(new ResumeEntry("peer", "fresh", "fresh.part", "fresh", false, DateTime.UtcNow));

        Assert.Null(store.Find("peer", "old"));
        new ResumeStore(path).PurgeExpired();

        var reloaded = new ResumeStore(path);
        Assert.Equal("fresh", Assert.Single(reloaded.Entries).ResumeKey);
        Assert.False(File.Exists(partial));
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
