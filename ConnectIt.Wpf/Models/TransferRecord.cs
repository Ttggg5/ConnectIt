using ConnectIt.Wpf.Services;

namespace ConnectIt.Wpf.Models;

/// <summary>
/// 這次連線期間的一筆傳輸紀錄(傳送或接收的檔案/資料夾),顯示在連線面板的「傳輸紀錄」清單,
/// 接收成功的項目可以直接開啟檔案或在檔案總管中顯示。只存在記憶體裡,斷線後清空。
/// </summary>
public sealed class TransferRecord
{
    public required FileTransferDirection Direction { get; init; }

    public required FileTransferEndReason Reason { get; init; }

    /// <summary>顯示名稱:檔名、資料夾名稱,或「N 個檔案」。</summary>
    public required string Name { get; init; }

    /// <summary>接收成功時,檔案(或資料夾)實際儲存的位置;其他情況為 null。</summary>
    public string? SavedPath { get; init; }

    /// <summary>true 代表 <see cref="SavedPath"/> 是資料夾(資料夾/多檔案傳輸),false 代表單一檔案。</summary>
    public bool IsFolder { get; init; }

    public DateTime Time { get; init; } = DateTime.Now;

    public bool IsReceived => Direction == FileTransferDirection.Receiving;

    public bool Succeeded => Reason == FileTransferEndReason.Completed;

    /// <summary>可以「開啟」的前提:接收成功,而且有實際儲存位置。</summary>
    public bool CanOpen => IsReceived && Succeeded && !string.IsNullOrEmpty(SavedPath);

    public string StatusText => Reason switch
    {
        FileTransferEndReason.Completed => IsReceived ? "已接收" : "已傳送",
        FileTransferEndReason.Rejected => "對方拒絕",
        FileTransferEndReason.Cancelled => "已取消",
        FileTransferEndReason.CancelledByRemote => "對方取消",
        FileTransferEndReason.ConnectionClosed => "連線中斷",
        _ => "失敗",
    };

    public string TimeText => Time.ToString("HH:mm");
}
