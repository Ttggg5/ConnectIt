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

    public int Port { get; private set; }

    public bool IsConnected => _activeClient != null;

    public bool IsFileTransferActive => _activeTransfer != null;

    /// <summary>有裝置送出連線請求,需要使用者確認。</summary>
    public event EventHandler<ConnectionRequestedEventArgs>? ConnectionRequested;

    /// <summary>連線交握完成(不論是發起方還是被連線方)。</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    /// <summary>對方關閉了連線(不是本機呼叫 Disconnect() 造成的)。</summary>
    public event EventHandler? RemoteDisconnected;

    public event EventHandler<string>? StatusChanged;

    /// <summary>對方想要傳送檔案給我們,需要使用者確認。</summary>
    public event EventHandler<FileOfferedEventArgs>? FileOffered;

    /// <summary>檔案傳輸進度更新(傳送中或接收中皆會觸發)。</summary>
    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;

    /// <summary>檔案傳輸結束,不論成功、被拒絕、被取消或失敗。</summary>
    public event EventHandler<FileTransferEndedEventArgs>? FileTransferEnded;

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

        // 不論是對方斷線、本機主動斷線還是發生例外,只要還有進行中的檔案傳輸就必須清乾淨
        // (關閉/刪除尚未寫完的接收檔案),避免留下殘破的檔案或卡住的狀態。
        AbortActiveTransfer(FileTransferEndReason.ConnectionClosed);

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

        if (_activeTransfer != null)
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

    /// <summary>取消目前進行中的檔案傳輸(不論是傳送中、接收中,還是還在等待對方回應提議階段)。</summary>
    public void CancelFileTransfer()
    {
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
        }
    }

    private void HandleFileOffer(FileControlMessage message)
    {
        if (_activeTransfer != null || string.IsNullOrWhiteSpace(message.FileName) || message.Size is not (>= 0))
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

        FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = FileTransferDirection.Receiving,
            BytesTransferred = transfer.TransferredBytes,
            TotalBytes = transfer.TotalBytes,
        });

        if (transfer.TransferredBytes == transfer.TotalBytes)
        {
            _activeTransfer = null;
            transfer.FileStream.Dispose();
            _ = WriteControlAsync(new FileControlMessage { Type = "file-complete", TransferId = transfer.TransferId });
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

                FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
                {
                    TransferId = transfer.TransferId,
                    Direction = FileTransferDirection.Sending,
                    BytesTransferred = transfer.TransferredBytes,
                    TotalBytes = transfer.TotalBytes,
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

        FileTransferEnded?.Invoke(this, new FileTransferEndedEventArgs
        {
            TransferId = transfer.TransferId,
            Direction = transfer.Direction,
            Reason = reason,
            FileName = transfer.FileName,
        });
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

        public CancellationTokenSource Cts { get; } = new();
    }
}
