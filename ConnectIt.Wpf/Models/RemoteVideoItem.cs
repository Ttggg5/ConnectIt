using ConnectIt.Wpf.Services;

namespace ConnectIt.Wpf.Models;

/// <summary>遙控面板影片庫裡的一張卡片:manifest 項目加上縮圖網址(縮圖由本機影片伺服器的 /thumbnail/ 端點提供)。</summary>
public sealed class RemoteVideoItem
{
    public required VideoManifestEntry Entry { get; init; }

    public required string ThumbnailUrl { get; init; }

    public string Name => Entry.Name;

    public string RelativePath => Entry.RelativePath;

    /// <summary>所在的子資料夾(根目錄的影片為空字串),顯示在卡片標題下方幫助辨識同名影片。</summary>
    public string Folder
    {
        get
        {
            var index = Entry.RelativePath.LastIndexOf('/');
            return index > 0 ? Entry.RelativePath[..index] : string.Empty;
        }
    }
}
