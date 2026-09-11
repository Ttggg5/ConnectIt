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

/// <summary>對方送出資料夾傳輸請求,需要使用者確認。</summary>
public sealed class FolderOfferedEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required string FolderName { get; init; }

    public required long TotalSize { get; init; }

    public required int TotalEntries { get; init; }

    /// <summary>true 代表這是多個各自獨立的檔案一起送過來,不是一整個資料夾。</summary>
    public required bool IsBatch { get; init; }
}

/// <summary>傳輸進度更新(不論是傳送中還是接收中)。資料夾傳輸時,額外的 Folder* 欄位會有值。</summary>
public sealed class FileTransferProgressEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required FileTransferDirection Direction { get; init; }

    public required long BytesTransferred { get; init; }

    public required long TotalBytes { get; init; }

    /// <summary>屬於資料夾傳輸時,所屬的資料夾傳輸 TransferId,否則為 null。</summary>
    public string? FolderTransferId { get; init; }

    /// <summary>資料夾傳輸時目前這個檔案的檔名(含相對路徑資訊已在別處處理,這裡只是顯示用檔名)。</summary>
    public string? FileName { get; init; }

    /// <summary>資料夾傳輸時,目前這個檔案是第幾個(1-based)。</summary>
    public int? EntryIndex { get; init; }

    /// <summary>資料夾傳輸時的檔案總數。</summary>
    public int? TotalEntries { get; init; }

    /// <summary>資料夾傳輸時,累計已完成的位元組數(含之前已完成的檔案)。</summary>
    public long? FolderBytesTransferred { get; init; }

    /// <summary>資料夾傳輸時,所有檔案加總的位元組數。</summary>
    public long? FolderTotalBytes { get; init; }
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

/// <summary>資料夾傳輸結束(不論是成功完成、被拒絕、被取消,還是失敗)。</summary>
public sealed class FolderTransferEndedEventArgs : EventArgs
{
    public required string TransferId { get; init; }

    public required FileTransferDirection Direction { get; init; }

    public required FileTransferEndReason Reason { get; init; }

    public required string FolderName { get; init; }

    /// <summary>成功完成的檔案數量(失敗/取消時代表已完成的部分)。</summary>
    public required int EntryCount { get; init; }

    /// <summary>true 代表這是多個各自獨立的檔案一起送過來,不是一整個資料夾。</summary>
    public required bool IsBatch { get; init; }

    /// <summary>接收方成功完成時,檔案實際儲存的資料夾路徑(IsBatch 時就是使用者設定的下載資料夾)。</summary>
    public string? SavedFolderPath { get; init; }
}
