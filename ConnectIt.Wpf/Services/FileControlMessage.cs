namespace ConnectIt.Wpf.Services;

/// <summary>檔案傳輸交握用的控制訊息(以 <see cref="ConnectionService"/> 的訊框協定傳送)。</summary>
public sealed class FileControlMessage
{
    public required string Type { get; init; } // "file-offer" | "file-accept" | "file-reject" | "file-cancel" | "file-complete"

    public string? TransferId { get; init; }

    public string? FileName { get; init; }

    public long? Size { get; init; }
}
