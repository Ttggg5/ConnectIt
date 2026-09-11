using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 負責 mDNS 找到裝置之後的實際 TCP 連線,包含連線請求/接受/拒絕的交握協定:
/// 發起端連上後先送出 "request",對方要等使用者確認才會回 "accept"/"reject"。
/// 連線建立後,同一個 socket 也用來傳輸檔案(見檔案下方「檔案傳輸」區塊)。
/// </summary>
public sealed class ConnectionService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    // 訊框長度前綴的長度(4 bytes),以及單一訊框的長度上限(1 型別 byte + 資料),
    // 避免對方送出異常大的長度值時整個配置一大塊記憶體。
    private const int FrameLengthPrefixSize = 4;
    private const int MaxFrameSize = 1 * 1024 * 1024;
    private const int FileChunkSize = 64 * 1024;

    private static class FrameType
    {
        public const byte Control = 0;
        public const byte Chunk = 1;
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;

    private TcpClient? _activeClient;
    private CancellationTokenSource? _monitorCts;

    // 保護「送出訊框」這個動作本身,避免背景的檔案傳送迴圈跟使用者觸發的取消/控制訊息同時寫入同一個 socket,
    // 導致兩段內容在網路上交錯、對方解析出不完整的訊框。
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ActiveTransfer? _activeTransfer;
    private FolderSession? _activeFolderSession;

    public int Port { get; private set; }

    public bool IsConnected => _activeClient != null;

    public bool IsFileTransferActive => _activeTransfer != null || _activeFolderSession != null;

    /// <summary>有裝置送出連線請求,需要使用者確認。</summary>
    public event EventHandler<ConnectionRequestedEventArgs>? ConnectionRequested;

    /// <summary>連線交握完成(不論是發起方還是被連線方)。</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    /// <summary>對方關閉了連線(不是本機呼叫 Disconnect() 造成的)。</summary>
    public event EventHandler? RemoteDisconnected;

    public event EventHandler<string>? StatusChanged;

    /// <summary>對方想要傳送檔案給我們,需要使用者確認。</summary>
    public event EventHandler<FileOfferedEventArgs>? FileOffered;

    /// <summary>對方想要傳送整個資料夾給我們,需要使用者確認。</summary>
    public event EventHandler<FolderOfferedEventArgs>? FolderOffered;

    /// <summary>檔案傳輸進度更新(傳送中或接收中皆會觸發)。</summary>
    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;

    /// <summary>檔案傳輸結束,不論成功、被拒絕、被取消或失敗。</summary>
    public event EventHandler<FileTransferEndedEventArgs>? FileTransferEnded;

    /// <summary>資料夾傳輸結束,不論成功、被拒絕、被取消或失敗。</summary>
    public event EventHandler<FolderTransferEndedEventArgs>? FolderTransferEnded;

    /// <summary>開始監聽,回傳實際使用的連接埠(0 代表由系統挑選)。</summary>
    public int StartListening(int preferredPort = 0)
    {
        StopListening();

        _listener = new TcpListener(IPAddress.Any, preferredPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptCts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _acceptCts.Token);

        StatusChanged?.Invoke(this, $"已開始監聽連接埠 {Port}。");
        return Port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(this, $"監聽發生錯誤:{ex.Message}");
                break;
            }

            _ = HandleIncomingAsync(client, token);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        StatusChanged?.Invoke(this, $"收到來自 {remoteAddress} 的連線嘗試。");

        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var line = await ReadLineWithTimeoutAsync(reader, RequestTimeout, token).ConfigureAwait(false);

            ConnectMessage? request = null;
            if (line != null)
            {
                try
                {
                    request = JsonSerializer.Deserialize<ConnectMessage>(line);
                }
                catch (JsonException ex)
                {
                    StatusChanged?.Invoke(this, $"連線內容格式錯誤,已忽略:{ex.Message}");
                }
            }
            else
            {
                StatusChanged?.Invoke(this, "等待對方送出連線請求逾時。");
            }

            if (request is not { Type: "request" })
            {
                if (line != null)
                {
                    StatusChanged?.Invoke(this, $"收到非連線請求的內容,已忽略:{line}");
                }

                client.Close();
                return;
            }

            var requesterName = string.IsNullOrWhiteSpace(request.DeviceName)
                ? remoteAddress.ToString()
                : request.DeviceName;

            var tcs = new TaskCompletionSource<bool>();
            ConnectionRequested?.Invoke(this, new ConnectionRequestedEventArgs
            {
                RequesterName = requesterName,
                RequesterAddress = remoteAddress,
                Respond = accepted => tcs.TrySetResult(accepted),
            });

            var accepted2 = await tcs.Task.ConfigureAwait(false);

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = accepted2 ? "accept" : "reject",
            })).ConfigureAwait(false);

            if (!accepted2)
            {
                client.Close();
                return;
            }

            _activeClient = client;
            StartMonitoring(client);
            Connected?.Invoke(this, new ConnectedEventArgs
            {
                RemoteName = requesterName,
                RemoteAddress = remoteAddress,
                Role = ConnectionRole.Acceptor,
            });
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"處理連入連線時發生錯誤:{ex.Message}");
            client.Close();
        }
    }

    /// <summary>主動連線到指定裝置並送出連線請求,等待對方確認。</summary>
    public async Task<bool> RequestConnectionAsync(
        IPAddress address, int port, string myDeviceName, string remoteDisplayName, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            StatusChanged?.Invoke(this, $"已送出連線請求給 {remoteDisplayName},等待對方確認...");

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ConnectMessage
            {
                Type = "request",
                DeviceName = myDeviceName,
            })).ConfigureAwait(false);

            var line = await ReadLineWithTimeoutAsync(reader, ResponseTimeout, cancellationToken).ConfigureAwait(false);
            var response = line != null ? JsonSerializer.Deserialize<ConnectMessage>(line) : null;

            if (response is { Type: "accept" })
            {
                _activeClient = client;
                StartMonitoring(client);
                Connected?.Invoke(this, new ConnectedEventArgs
                {
                    RemoteName = remoteDisplayName,
                    RemoteAddress = address,
                    Role = ConnectionRole.Initiator,
                });
                return true;
            }

            StatusChanged?.Invoke(this, response == null
                ? $"連線逾時,{remoteDisplayName} 沒有回應。"
                : $"{remoteDisplayName} 拒絕了連線請求。");
            client.Close();
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"連線失敗:{ex.Message}");
            client.Close();
            return false;
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken token)
    {
        var readTask = reader.ReadLineAsync();
        var delayTask = Task.Delay(timeout, token);

        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        return completed == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// 連線建立後,持續在背景讀取這個 socket:一方面用來偵測「對方把連線關掉」,
    /// 一方面把收到的訊框(控制訊息/檔案內容)分派給對應的處理邏輯。
    /// </summary>
    private void StartMonitoring(TcpClient client)
    {
        _monitorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        _ = MonitorConnectionAsync(client, cts.Token);
    }

    private async Task MonitorConnectionAsync(TcpClient client, CancellationToken token)
    {
        var remoteClosed = false;

        try
        {
            var stream = client.GetStream();

            while (!token.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (frame == null)
                {
                    remoteClosed = true;
                    break;
                }

                var (type, payload) = frame.Value;
                if (type == FrameType.Control)
                {
                    HandleControlFrame(payload);
                }
                else if (type == FrameType.Chunk)
                {
                    HandleChunkFrame(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 是本機呼叫 Disconnect() 取消的,不算對方斷線
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                remoteClosed = true;
            }
        }

        // 不論是對方斷線、本機主動斷線還是發生例外,只要還有進行中的檔案/資料夾傳輸就必須清乾淨
        // (關閉/刪除尚未寫完的接收檔案),避免留下殘破的檔案或卡住的狀態。
        AbortActiveTransfer(FileTransferEndReason.ConnectionClosed);
        AbortActiveFolderSession(FileTransferEndReason.ConnectionClosed);

        if (remoteClosed)
        {
            _activeClient = null;
            StatusChanged?.Invoke(this, "對方已中斷連線。");
            RemoteDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>中斷目前的連線(不影響監聽,對方可以再送新的連線請求)。</summary>
    public void Disconnect()
    {
        _monitorCts?.Cancel();
        _monitorCts = null;
        _activeClient?.Close();
        _activeClient = null;
    }

    public void StopListening()
    {
        _acceptCts?.Cancel();
        _acceptCts?.Dispose();
        _acceptCts = null;

        _listener?.Stop();
        _listener = null;
    }

    public void Dispose()
    {
        Disconnect();
        StopListening();
        _writeLock.Dispose();
    }

    // ===================== 訊框協定(讀取/寫入) =====================
    //
    // 連線建立後的 socket 用一個簡單的長度前綴訊框協定傳輸資料:
    //   [4 bytes big-endian 長度(= 1 個型別 byte + 資料長度)][1 byte 型別][資料]
    // 型別 0 = 控制訊息(UTF8 JSON,見 FileControlMessage),型別 1 = 檔案內容區塊(原始 bytes)。

    private async Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        var stream = _activeClient?.GetStream();
        if (stream == null)
        {
            return;
        }

        var header = new byte[FrameLengthPrefixSize];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);

        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(new[] { type }, token).ConfigureAwait(false);
            await stream.WriteAsync(payload, token).ConfigureAwait(false);
        }
        catch
        {
            // 寫入失敗通常代表連線已經斷了,交給背景的監控迴圈去處理斷線通知即可。
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task WriteControlAsync(FileControlMessage message, CancellationToken token = default) =>
        WriteFrameAsync(FrameType.Control, JsonSerializer.SerializeToUtf8Bytes(message), token);

    private static async Task<(byte Type, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[FrameLengthPrefixSize];
        if (!await ReadExactAsync(stream, header, allowLeadingEof: true, token).ConfigureAwait(false))
        {
            return null; // 剛好在訊框邊界收到 EOF,代表對方正常關閉連線。
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrameSize)
        {
            throw new InvalidDataException($"收到不合法的訊框長度:{length}");
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, allowLeadingEof: false, token).ConfigureAwait(false))
        {
            throw new EndOfStreamException("訊框讀到一半連線就中斷了。");
        }

        return (body[0], body[1..]);
    }

    /// <summary>把 buffer 填滿為止。<paramref name="allowLeadingEof"/> 為 true 時,若一開始就收到 EOF 會回傳 false 而不是丟例外。</summary>
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, bool allowLeadingEof, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowLeadingEof)
                {
                    return false;
                }

                throw new EndOfStreamException("讀取資料時連線意外中斷。");
            }

            offset += read;
        }

        return true;
    }

    // ===================== 檔案傳輸 =====================
    //
    // 同一時間只允許一個進行中的傳輸(跟這個 App 同一時間只允許一個連線的模型一致)。
    // 狀態機:
    //   傳送端:SendFileAsync 送出 file-offer -> 收到 file-accept 後背景逐塊送出 Chunk 訊框
    //           -> 送完後等待對方回傳 file-complete 才算真正完成。
    //   接收端:收到 file-offer 觸發 FileOffered -> 使用者用 RespondToFileOffer 回應
    //           -> 接受的話建立檔案,陸續把收到的 Chunk 訊框寫入,收滿宣告的大小後
    //              關閉檔案、回傳 file-complete。
    //   取消:任一方呼叫 CancelFileTransfer() 都會送出 file-cancel,對方收到後做鏡像的清理。

    public async Task SendFileAsync(string filePath)
    {
        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送檔案。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            StatusChanged?.Invoke(this, $"找不到檔案:{filePath}");
            return;
        }

        var transfer = new ActiveTransfer
        {
            TransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FileName = info.Name,
            TotalBytes = info.Length,
            FilePath = info.FullName,
        };
        _activeTransfer = transfer;

        StatusChanged?.Invoke(this, $"已送出檔案「{transfer.FileName}」的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "file-offer",
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Size = transfer.TotalBytes,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 傳送整個資料夾(遞迴包含所有子目錄與檔案)。流程跟單一檔案類似:
    /// 先送出 folder-offer 讓對方決定是否接受整個資料夾,對方一旦接受,底下每個檔案
    /// 會依序沿用既有的 file-offer/file-accept/Chunk/file-complete 狀態機自動傳送,
    /// 不會再逐一詢問使用者。
    /// </summary>
    public async Task SendFolderAsync(string folderPath)
    {
        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送資料夾。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        var info = new DirectoryInfo(folderPath);
        if (!info.Exists)
        {
            StatusChanged?.Invoke(this, $"找不到資料夾:{folderPath}");
            return;
        }

        List<(string FullPath, string RelativePath, long Size)> files;
        try
        {
            files = Directory.EnumerateFiles(info.FullName, "*", SearchOption.AllDirectories)
                .Select(fullPath => (
                    FullPath: fullPath,
                    RelativePath: Path.GetRelativePath(info.FullName, fullPath).Replace('\\', '/'),
                    Size: new FileInfo(fullPath).Length))
                .ToList();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"讀取資料夾內容失敗:{ex.Message}");
            return;
        }

        if (files.Count == 0)
        {
            StatusChanged?.Invoke(this, "資料夾是空的,沒有可以傳送的檔案。");
            return;
        }

        var session = new FolderSession
        {
            FolderTransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FolderName = info.Name,
            TotalBytes = files.Sum(f => f.Size),
            TotalEntries = files.Count,
            PendingFiles = new Queue<(string, string, long)>(files),
            IsBatch = false,
        };
        _activeFolderSession = session;

        StatusChanged?.Invoke(this, $"已送出資料夾「{session.FolderName}」({session.TotalEntries} 個檔案)的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "folder-offer",
            TransferId = session.FolderTransferId,
            FileName = session.FolderName,
            Size = session.TotalBytes,
            TotalEntries = session.TotalEntries,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 傳送多個各自獨立的檔案(使用者一次選取或拖曳多個檔案)。跟 <see cref="SendFolderAsync"/>
    /// 共用同一套資料夾層級的狀態機,差別只在於接收端不會另外建立一層子資料夾,
    /// 每個檔案會直接落在使用者設定的下載資料夾裡(檔名相同時一樣會依序改用「(1)」、「(2)」...)。
    /// 只有一個檔案時,直接走 <see cref="SendFileAsync"/> 原本的單檔流程。
    /// </summary>
    public async Task SendFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 1)
        {
            await SendFileAsync(filePaths[0]).ConfigureAwait(false);
            return;
        }

        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, "尚未連線,無法傳送檔案。");
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null)
        {
            StatusChanged?.Invoke(this, "目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        if (filePaths.Count == 0)
        {
            return;
        }

        var files = new List<(string FullPath, string RelativePath, long Size)>();
        foreach (var filePath in filePaths)
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                StatusChanged?.Invoke(this, $"找不到檔案:{filePath}");
                return;
            }

            files.Add((info.FullName, info.Name, info.Length));
        }

        var session = new FolderSession
        {
            FolderTransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FolderName = $"{files.Count} 個檔案",
            TotalBytes = files.Sum(f => f.Size),
            TotalEntries = files.Count,
            PendingFiles = new Queue<(string, string, long)>(files),
            IsBatch = true,
        };
        _activeFolderSession = session;

        StatusChanged?.Invoke(this, $"已送出 {session.TotalEntries} 個檔案的傳送請求,等待對方確認...");

        await WriteControlAsync(new FileControlMessage
        {
            Type = "folder-offer",
            TransferId = session.FolderTransferId,
            FileName = session.FolderName,
            Size = session.TotalBytes,
            TotalEntries = session.TotalEntries,
            IsBatch = true,
        }).ConfigureAwait(false);
    }

    /// <summary>接收到 <see cref="FileOffered"/> 之後,使用者確認是否接受這個檔案時呼叫。</summary>
    public void RespondToFileOffer(string transferId, bool accept, string downloadFolder)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Receiving } || transfer.TransferId != transferId)
        {
            return;
        }

        if (!accept)
        {
            _activeTransfer = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = transferId });
            FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
            {
                TransferId = transferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Rejected,
                FileName = transfer.FileName,
            });
            return;
        }

        try
        {
            Directory.CreateDirectory(downloadFolder);
            var destinationPath = GetUniqueDestinationPath(downloadFolder, transfer.FileName);
            transfer.FileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
            transfer.SavedFilePath = destinationPath;
        }
        catch (Exception ex)
        {
            _activeTransfer = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = transferId });
            StatusChanged?.Invoke(this, $"無法建立檔案:{ex.Message}");
            FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
            {
                TransferId = transferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Failed,
                FileName = transfer.FileName,
            });
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "file-accept", TransferId = transferId });
    }

    /// <summary>接收到 <see cref="FolderOffered"/> 之後,使用者確認是否接受整個資料夾時呼叫。</summary>
    public void RespondToFolderOffer(string folderTransferId, bool accept, string downloadFolder)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving } || session.FolderTransferId != folderTransferId)
        {
            return;
        }

        if (!accept)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = folderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderTransferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Rejected,
                FolderName = session.FolderName,
                EntryCount = 0,
                IsBatch = session.IsBatch,
            });
            return;
        }

        try
        {
            Directory.CreateDirectory(downloadFolder);
            if (session.IsBatch)
            {
                // 多個各自獨立的檔案:不另外建立一層子資料夾,直接落在下載資料夾裡。
                session.RootDestinationFolder = downloadFolder;
            }
            else
            {
                session.RootDestinationFolder = GetUniqueDestinationDirectory(downloadFolder, session.FolderName);
                Directory.CreateDirectory(session.RootDestinationFolder);
            }
        }
        catch (Exception ex)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = folderTransferId });
            StatusChanged?.Invoke(this, $"無法建立資料夾:{ex.Message}");
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderTransferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Failed,
                FolderName = session.FolderName,
                EntryCount = 0,
                IsBatch = session.IsBatch,
            });
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "folder-accept", TransferId = folderTransferId });
    }

    /// <summary>
    /// 取消目前進行中的傳輸(不論是傳送中、接收中,還是還在等待對方回應提議階段)。
    /// 若目前是資料夾傳輸,會連同整個資料夾一起取消,而不是只取消當下這一個檔案。
    /// </summary>
    public void CancelFileTransfer()
    {
        var folderSession = _activeFolderSession;
        if (folderSession != null)
        {
            _activeFolderSession = null;

            var currentEntry = _activeTransfer;
            if (currentEntry is { FolderTransferId: not null })
            {
                _activeTransfer = null;
                CleanUpTransferResources(currentEntry);
            }

            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderSession.FolderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = folderSession.FolderTransferId,
                Direction = folderSession.Direction,
                Reason = FileTransferEndReason.Cancelled,
                FolderName = folderSession.FolderName,
                EntryCount = folderSession.CompletedEntries,
                IsBatch = folderSession.IsBatch,
            });
            return;
        }

        var transfer = _activeTransfer;
        if (transfer == null)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        _ = WriteControlAsync(new FileControlMessage { Type = "file-cancel", TransferId = transfer.TransferId });
        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Cancelled,
            FileName = transfer.FileName,
        });
    }

    private void HandleControlFrame(byte[] payload)
    {
        FileControlMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<FileControlMessage>(payload);
        }
        catch (JsonException)
        {
            return;
        }

        if (message?.TransferId == null)
        {
            return;
        }

        switch (message.Type)
        {
            case "file-offer":
                HandleFileOffer(message);
                break;
            case "file-accept":
                HandleFileAccept(message);
                break;
            case "file-reject":
                HandleFileReject(message);
                break;
            case "file-cancel":
                HandleFileCancel(message);
                break;
            case "file-complete":
                HandleFileCompleteAck(message);
                break;
            case "folder-offer":
                HandleFolderOffer(message);
                break;
            case "folder-accept":
                HandleFolderAccept(message);
                break;
            case "folder-reject":
                HandleFolderReject(message);
                break;
            case "folder-cancel":
                HandleFolderCancel(message);
                break;
            case "folder-complete":
                HandleFolderComplete(message);
                break;
        }
    }

    private void HandleFileOffer(FileControlMessage message)
    {
        if (message.FolderTransferId != null)
        {
            HandleFolderFileOffer(message);
            return;
        }

        if (_activeTransfer != null || _activeFolderSession != null || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        var transfer = new ActiveTransfer
        {
            TransferId = message.TransferId!,
            Direction = FileTransferDirection.Receiving,
            FileName = SanitizeFileName(message.FileName),
            TotalBytes = message.Size!.Value,
        };
        _activeTransfer = transfer;

        FileOffered?.Invoke(this, new FileOfferedEventArgs
        {
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            FileSize = transfer.TotalBytes,
        });
    }

    private void HandleFolderOffer(FileControlMessage message)
    {
        if (_activeTransfer != null || _activeFolderSession != null
            || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0) || message.TotalEntries is not (> 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-reject", TransferId = message.TransferId });
            return;
        }

        var isBatch = message.IsBatch == true;

        var session = new FolderSession
        {
            FolderTransferId = message.TransferId!,
            Direction = FileTransferDirection.Receiving,
            // 批次傳送的「資料夾名稱」只是顯示用的摘要文字(例如「3 個檔案」),不會被當成檔案系統路徑,
            // 所以不需要、也不應該套用只適用於真實檔名的 SanitizeFileName。
            FolderName = isBatch ? message.FileName! : SanitizeFileName(message.FileName),
            TotalBytes = message.Size!.Value,
            TotalEntries = message.TotalEntries!.Value,
            IsBatch = isBatch,
        };
        _activeFolderSession = session;

        FolderOffered?.Invoke(this, new FolderOfferedEventArgs
        {
            TransferId = session.FolderTransferId,
            FolderName = session.FolderName,
            TotalSize = session.TotalBytes,
            TotalEntries = session.TotalEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>
    /// 資料夾傳輸裡,某一個檔案的 file-offer:因為使用者已經同意過整個資料夾,
    /// 這裡不再另外詢問,而是直接依 <see cref="FileControlMessage.RelativePath"/> 在
    /// 資料夾根目錄底下建立對應的子目錄與檔案,然後自動回 file-accept。
    /// </summary>
    private void HandleFolderFileOffer(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving, RootDestinationFolder: not null }
            || session.FolderTransferId != message.FolderTransferId
            || _activeTransfer != null
            || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0))
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        var relativePath = SanitizeRelativePath(message.RelativePath, message.FileName);
        var destinationPath = Path.Combine(session.RootDestinationFolder, relativePath);

        var fullRoot = Path.GetFullPath(session.RootDestinationFolder) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(destinationPath).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            // 正常情況下 SanitizeRelativePath 已經擋掉路徑穿越,這裡是最後一道防線。
            _ = WriteControlAsync(new FileControlMessage { Type = "file-reject", TransferId = message.TransferId });
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(directory);
            destinationPath = GetUniqueDestinationPath(directory, Path.GetFileName(destinationPath));

            var transfer = new ActiveTransfer
            {
                TransferId = message.TransferId!,
                Direction = FileTransferDirection.Receiving,
                FileName = Path.GetFileName(destinationPath),
                TotalBytes = message.Size!.Value,
                FolderTransferId = session.FolderTransferId,
                EntryIndex = message.EntryIndex,
                FileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write),
                SavedFilePath = destinationPath,
            };
            _activeTransfer = transfer;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"無法建立檔案:{ex.Message}");
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = session.FolderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "file-accept", TransferId = message.TransferId });
    }

    private void HandleFolderAccept(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Sending } || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        SendNextFolderFileOrComplete(session);
    }

    private void HandleFolderReject(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session == null || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;
        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = FileTransferEndReason.Rejected,
            FolderName = session.FolderName,
            EntryCount = 0,
            IsBatch = session.IsBatch,
        });
    }

    private void HandleFolderCancel(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session == null || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;

        var currentEntry = _activeTransfer;
        if (currentEntry is { FolderTransferId: not null } && currentEntry.FolderTransferId == session.FolderTransferId)
        {
            _activeTransfer = null;
            CleanUpTransferResources(currentEntry);
        }

        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = FileTransferEndReason.CancelledByRemote,
            FolderName = session.FolderName,
            EntryCount = session.CompletedEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>接收端收到 folder-complete:代表傳送端已經把所有檔案都送完,且每個都收到接收端的 file-complete 確認了。</summary>
    private void HandleFolderComplete(FileControlMessage message)
    {
        var session = _activeFolderSession;
        if (session is not { Direction: FileTransferDirection.Receiving } || session.FolderTransferId != message.TransferId)
        {
            return;
        }

        _activeFolderSession = null;
        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = FileTransferDirection.Receiving,
            Reason = FileTransferEndReason.Completed,
            FolderName = session.FolderName,
            EntryCount = session.TotalEntries,
            IsBatch = session.IsBatch,
            SavedFolderPath = session.RootDestinationFolder,
        });
    }

    /// <summary>傳送端:把資料夾佇列裡的下一個檔案送出 file-offer;如果已經沒有檔案了,結束整個資料夾傳輸。</summary>
    private void SendNextFolderFileOrComplete(FolderSession session)
    {
        if (session.PendingFiles == null || session.PendingFiles.Count == 0)
        {
            _activeFolderSession = null;
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-complete", TransferId = session.FolderTransferId });
            FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
            {
                TransferId = session.FolderTransferId,
                Direction = FileTransferDirection.Sending,
                Reason = FileTransferEndReason.Completed,
                FolderName = session.FolderName,
                EntryCount = session.TotalEntries,
                IsBatch = session.IsBatch,
            });
            return;
        }

        var (fullPath, relativePath, size) = session.PendingFiles.Dequeue();
        var entryIndex = session.CompletedEntries + 1;

        var transfer = new ActiveTransfer
        {
            TransferId = Guid.NewGuid().ToString("N"),
            Direction = FileTransferDirection.Sending,
            FileName = Path.GetFileName(fullPath),
            TotalBytes = size,
            FilePath = fullPath,
            FolderTransferId = session.FolderTransferId,
            EntryIndex = entryIndex,
        };
        _activeTransfer = transfer;

        _ = WriteControlAsync(new FileControlMessage
        {
            Type = "file-offer",
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Size = transfer.TotalBytes,
            FolderTransferId = session.FolderTransferId,
            RelativePath = relativePath,
            EntryIndex = entryIndex,
            TotalEntries = session.TotalEntries,
        });
    }

    private void HandleFileAccept(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Sending } || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _ = SendFileChunksAsync(transfer);
    }

    private void HandleFileReject(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer == null || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;

        if (transfer.FolderTransferId is { } folderTransferId)
        {
            // 資料夾裡的其中一個檔案被拒絕是不應該發生的狀況(整個資料夾已經被同意過了),
            // 保守起見直接把整個資料夾傳輸結束掉,而不是嘗試略過這個檔案繼續傳其他的。
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Rejected,
            FileName = transfer.FileName,
        });
    }

    private void HandleFileCancel(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer == null || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        if (transfer.FolderTransferId != null)
        {
            AbortActiveFolderSession(FileTransferEndReason.CancelledByRemote);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.CancelledByRemote,
            FileName = transfer.FileName,
        });
    }

    private void HandleFileCompleteAck(FileControlMessage message)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Sending } || transfer.TransferId != message.TransferId)
        {
            return;
        }

        _activeTransfer = null;

        if (transfer.FolderTransferId is { } folderTransferId
            && _activeFolderSession is { } session && session.FolderTransferId == folderTransferId)
        {
            session.CompletedBytes += transfer.TotalBytes;
            session.CompletedEntries += 1;
            SendNextFolderFileOrComplete(session);
            return;
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Sending,
            Reason = FileTransferEndReason.Completed,
            FileName = transfer.FileName,
        });
    }

    private void HandleChunkFrame(byte[] payload)
    {
        var transfer = _activeTransfer;
        if (transfer is not { Direction: FileTransferDirection.Receiving, FileStream: not null })
        {
            return; // 已被取消/拒絕之後仍在路上的區塊,直接丟棄。
        }

        if (transfer.TransferredBytes + payload.Length > transfer.TotalBytes)
        {
            FailActiveTransfer(transfer, "收到超過預期大小的檔案內容。");
            return;
        }

        try
        {
            transfer.FileStream.Write(payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            FailActiveTransfer(transfer, $"寫入檔案失敗:{ex.Message}");
            return;
        }

        transfer.TransferredBytes += payload.Length;

        var (folderBytes, folderTotal) = GetFolderProgressTotals(transfer);
        FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Receiving,
            BytesTransferred = transfer.TransferredBytes,
            TotalBytes = transfer.TotalBytes,
            FolderTransferId = transfer.FolderTransferId,
            FileName = transfer.FolderTransferId != null ? transfer.FileName : null,
            EntryIndex = transfer.EntryIndex,
            TotalEntries = _activeFolderSession?.TotalEntries,
            FolderBytesTransferred = folderBytes,
            FolderTotalBytes = folderTotal,
        });

        if (transfer.TransferredBytes == transfer.TotalBytes)
        {
            _activeTransfer = null;
            transfer.FileStream.Dispose();
            _ = WriteControlAsync(new FileControlMessage { Type = "file-complete", TransferId = transfer.TransferId });

            if (transfer.FolderTransferId is { } folderTransferId
                && _activeFolderSession is { } session && session.FolderTransferId == folderTransferId)
            {
                session.CompletedBytes += transfer.TotalBytes;
                session.CompletedEntries += 1;
                return; // 資料夾層級的完成事件要等收到傳送端送來的 folder-complete 才觸發。
            }

            FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
            {
                TransferId = transfer.TransferId,
                Direction = FileTransferDirection.Receiving,
                Reason = FileTransferEndReason.Completed,
                FileName = transfer.FileName,
                SavedFilePath = transfer.SavedFilePath,
            });
        }
    }

    private async Task SendFileChunksAsync(ActiveTransfer transfer)
    {
        try
        {
            await using var fileStream = new FileStream(transfer.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[FileChunkSize];
            int read;
            while ((read = await fileStream.ReadAsync(buffer, transfer.Cts.Token).ConfigureAwait(false)) > 0)
            {
                await WriteFrameAsync(FrameType.Chunk, buffer.AsMemory(0, read), transfer.Cts.Token).ConfigureAwait(false);
                transfer.TransferredBytes += read;

                var (folderBytes, folderTotal) = GetFolderProgressTotals(transfer);
                FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
                {
                    TransferId = transfer.TransferId,
                    Direction = FileTransferDirection.Sending,
                    BytesTransferred = transfer.TransferredBytes,
                    TotalBytes = transfer.TotalBytes,
                    FolderTransferId = transfer.FolderTransferId,
                    FileName = transfer.FolderTransferId != null ? transfer.FileName : null,
                    EntryIndex = transfer.EntryIndex,
                    TotalEntries = _activeFolderSession?.TotalEntries,
                    FolderBytesTransferred = folderBytes,
                    FolderTotalBytes = folderTotal,
                });
            }

            StatusChanged?.Invoke(this, $"「{transfer.FileName}」已送出,等待對方確認接收完成...");
        }
        catch (OperationCanceledException)
        {
            // 本機或對方取消,CancelFileTransfer()/HandleFileCancel() 已經處理過清理跟事件了。
        }
        catch (Exception ex)
        {
            FailActiveTransfer(transfer, $"傳送檔案失敗:{ex.Message}");
        }
    }

    private void FailActiveTransfer(ActiveTransfer transfer, string reason)
    {
        if (_activeTransfer != transfer)
        {
            return; // 已經被別的路徑(取消/斷線)結束了。
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        StatusChanged?.Invoke(this, reason);

        if (transfer.FolderTransferId is { } folderTransferId)
        {
            _ = WriteControlAsync(new FileControlMessage { Type = "folder-cancel", TransferId = folderTransferId });
            AbortActiveFolderSession(FileTransferEndReason.Failed);
            return;
        }

        _ = WriteControlAsync(new FileControlMessage { Type = "file-cancel", TransferId = transfer.TransferId });
        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = FileTransferEndReason.Failed,
            FileName = transfer.FileName,
        });
    }

    /// <summary>連線中斷時清理任何還在進行中的傳輸,不嘗試再送出任何控制訊息(socket 已經不可用)。</summary>
    private void AbortActiveTransfer(FileTransferEndReason reason)
    {
        var transfer = _activeTransfer;
        if (transfer == null)
        {
            return;
        }

        _activeTransfer = null;
        CleanUpTransferResources(transfer);

        if (transfer.FolderTransferId != null)
        {
            return; // 資料夾層級的結束事件由 AbortActiveFolderSession 統一觸發,避免同時跳出兩則訊息。
        }

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = reason,
            FileName = transfer.FileName,
        });
    }

    /// <summary>連線中斷或發生無法復原的錯誤時,結束整個資料夾傳輸,不嘗試再送出任何控制訊息。</summary>
    private void AbortActiveFolderSession(FileTransferEndReason reason)
    {
        var session = _activeFolderSession;
        if (session == null)
        {
            return;
        }

        _activeFolderSession = null;

        FolderTransferEnded?.Invoke(this, new FolderTransferEndedEventArgs
        {
            TransferId = session.FolderTransferId,
            Direction = session.Direction,
            Reason = reason,
            FolderName = session.FolderName,
            EntryCount = session.CompletedEntries,
            IsBatch = session.IsBatch,
        });
    }

    /// <summary>算出目前這個檔案在所屬資料夾傳輸裡的累計進度(含之前已完成的檔案),不屬於資料夾傳輸則回傳 null。</summary>
    private (long? BytesTransferred, long? TotalBytes) GetFolderProgressTotals(ActiveTransfer transfer)
    {
        if (transfer.FolderTransferId is not { } folderTransferId
            || _activeFolderSession is not { } session || session.FolderTransferId != folderTransferId)
        {
            return (null, null);
        }

        return (session.CompletedBytes + transfer.TransferredBytes, session.TotalBytes);
    }

    /// <summary>停止背景送出迴圈(若有)、關閉並刪除尚未寫完的接收檔案(若有)。</summary>
    private static void CleanUpTransferResources(ActiveTransfer transfer)
    {
        transfer.Cts.Cancel();

        if (transfer.Direction != FileTransferDirection.Receiving)
        {
            return;
        }

        try
        {
            transfer.FileStream?.Dispose();
        }
        catch
        {
            // 忽略關閉檔案時的例外,接下來還是要嘗試刪除檔案。
        }

        try
        {
            if (transfer.SavedFilePath != null && File.Exists(transfer.SavedFilePath))
            {
                File.Delete(transfer.SavedFilePath);
            }
        }
        catch
        {
            // 刪不掉殘破的檔案不影響傳輸狀態的回報,使用者之後可以手動清掉。
        }
    }

    /// <summary>把對方宣告的檔名限制在單純的檔名(避免路徑穿越),並過濾不合法字元。</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "file";
        }

        return name;
    }

    /// <summary>如果目的路徑的檔案已存在,依序改用「檔名 (1).副檔名」、「檔名 (2).副檔名」...直到找到未使用的檔名。</summary>
    private static string GetUniqueDestinationPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{nameWithoutExtension} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>如果目的路徑的資料夾已存在(或撞到同名檔案),依序改用「資料夾名稱 (1)」、「資料夾名稱 (2)」...直到找到未使用的名稱。</summary>
    private static string GetUniqueDestinationDirectory(string parentFolder, string folderName)
    {
        var candidate = Path.Combine(parentFolder, folderName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(parentFolder, $"{folderName} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把對方宣告的相對路徑限制在資料夾根目錄底下(移除 "."/".." 這種可能造成路徑穿越的片段),
    /// 並比照 <see cref="SanitizeFileName"/> 過濾每一段路徑名稱裡的不合法字元。
    /// </summary>
    private static string SanitizeRelativePath(string? relativePath, string fallbackFileName)
    {
        var fileName = SanitizeFileName(fallbackFileName);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return fileName;
        }

        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .Select(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "_" : segment)
            .ToArray();

        if (segments.Length == 0)
        {
            return fileName;
        }

        // 最後一段(檔名)一律以 file-offer 訊息裡另外宣告的 FileName 為準,RelativePath 只用來決定子目錄結構。
        segments[^1] = fileName;
        return Path.Combine(segments);
    }

    private sealed class ActiveTransfer
    {
        public required string TransferId { get; init; }

        public required FileTransferDirection Direction { get; init; }

        public required string FileName { get; init; }

        public required long TotalBytes { get; init; }

        public long TransferredBytes { get; set; }

        /// <summary>傳送端:來源檔案的完整路徑。</summary>
        public string? FilePath { get; init; }

        /// <summary>接收端:目的檔案的寫入串流。</summary>
        public FileStream? FileStream { get; set; }

        /// <summary>接收端:目的檔案的完整路徑。</summary>
        public string? SavedFilePath { get; set; }

        /// <summary>屬於資料夾傳輸時,所屬的資料夾傳輸 TransferId,否則為 null。</summary>
        public string? FolderTransferId { get; init; }

        /// <summary>屬於資料夾傳輸時,這個檔案是第幾個(1-based)。</summary>
        public int? EntryIndex { get; init; }

        public CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>一次資料夾傳輸的整體狀態,底下每個檔案仍沿用 <see cref="ActiveTransfer"/> 逐一傳送。</summary>
    private sealed class FolderSession
    {
        public required string FolderTransferId { get; init; }

        public required FileTransferDirection Direction { get; init; }

        public required string FolderName { get; init; }

        public required long TotalBytes { get; init; }

        public required int TotalEntries { get; init; }

        public long CompletedBytes { get; set; }

        public int CompletedEntries { get; set; }

        /// <summary>傳送端:尚未送出的檔案佇列(完整路徑、相對於資料夾根目錄的路徑、檔案大小)。</summary>
        public Queue<(string FullPath, string RelativePath, long Size)>? PendingFiles { get; init; }

        /// <summary>接收端:這個資料夾實際落地的根目錄(已處理重名)。</summary>
        public string? RootDestinationFolder { get; set; }

        /// <summary>true 代表這是多個各自獨立的檔案一起送過來,不是使用者選了一整個資料夾。</summary>
        public required bool IsBatch { get; init; }
    }
}
