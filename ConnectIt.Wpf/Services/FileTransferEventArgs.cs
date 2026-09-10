namespace ConnectIt.Wpf.Services;

public enum FileTransferDirection
{
    Sending,
    Receiving,
}

public enum FileTransferEndReason
{
    Completed,
    Rejected,
    Cancelled,
    CancelledByRemote,
    Failed,
    ConnectionClosed,
}

/// <summary>對方送出檔案傳輸請求,需要使用者確認。</summary>
public sealed class FileOfferedEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }
}

/// <summary>傳輸進度更新(不論是傳送中還是接收中)。</summary>
public sealed class FileTransferProgressEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required FileTransferDirection Direction { get; init; }

    public required long BytesTransferred { get; init; }

    public required long TotalBytes { get; init; }
}

/// <summary>傳輸結束(不論是成功完成、被拒絕、被取消,還是失敗)。</summary>
public sealed class FileTransferEndedEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required FileTransferDirection Direction { get; init; }

    public required FileTransferEndReason Reason { get; init; }

    public required string FileName { get; init; }

    /// <summary>接收方成功完成時,檔案實際儲存的路徑。</summary>
    public string? SavedFilePath { get; init; }
}
