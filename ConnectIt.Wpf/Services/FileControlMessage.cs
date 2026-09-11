namespace ConnectIt.Wpf.Services;

/// <summary>檔案/資料夾傳輸交握用的控制訊息(以 <see cref="ConnectionService"/> 的訊框協定傳送)。</summary>
public sealed class FileControlMessage
{
    // "file-offer" | "file-accept" | "file-reject" | "file-cancel" | "file-complete" |
    // "folder-offer" | "folder-accept" | "folder-reject" | "folder-cancel" | "folder-complete"
    public required string Type { get; init; }

    public string? TransferId { get; init; }

    public string? FileName { get; init; }

    public long? Size { get; init; }

    /// <summary>當這個檔案是資料夾傳輸裡的其中一個項目時,指向所屬的資料夾傳輸 TransferId。</summary>
    public string? FolderTransferId { get; init; }

    /// <summary>資料夾傳輸裡,檔案相對於資料夾根目錄的路徑(使用 '/' 分隔,可能包含子目錄)。</summary>
    public string? RelativePath { get; init; }

    /// <summary>這個檔案在資料夾傳輸裡的序號(1-based),僅供 UI 顯示進度用。</summary>
    public int? EntryIndex { get; init; }

    /// <summary>資料夾傳輸裡的檔案總數。</summary>
    public int? TotalEntries { get; init; }

    /// <summary>
    /// folder-offer 是不是「多個各自獨立的檔案」而不是一整個資料夾:
    /// 這種情況下接收端不會另外建立一層子資料夾,檔案會直接落在使用者設定的下載資料夾裡。
    /// </summary>
    public bool? IsBatch { get; init; }
}
