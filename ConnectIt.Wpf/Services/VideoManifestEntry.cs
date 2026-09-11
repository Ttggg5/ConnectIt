namespace ConnectIt.Wpf.Services;

/// <summary>影片伺服器清單(manifest)裡的一筆項目,對應一支可播放的影片檔案。</summary>
public sealed class VideoManifestEntry
{
    public required string Name { get; init; }

    /// <summary>相對於分享根目錄的路徑(使用 '/' 分隔),同時也是 <c>/media/{RelativePath}</c> 的查找鍵。</summary>
    public required string RelativePath { get; init; }

    public required long Size { get; init; }

    /// <summary>檔案最後修改時間(UTC),用來支援「依修改時間排序」。</summary>
    public required DateTime Modified { get; init; }
}
