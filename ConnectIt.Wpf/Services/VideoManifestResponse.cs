namespace ConnectIt.Wpf.Services;

/// <summary>影片伺服器 <c>GET /manifest</c> 回傳的 JSON 內容,主機端序列化、觀看端反序列化共用同一個型別。</summary>
public sealed class VideoManifestResponse
{
    public required string Name { get; init; }

    public required IReadOnlyList<VideoManifestEntry> Entries { get; init; }

    /// <summary>主機是否開了遠端控制模式——只是輪詢 <c>/control/state</c> 前的初步提示,真正即時的值
    /// 以那個端點為準(host 可能在觀眾已經進入觀看頁之後才切換模式)。</summary>
    public bool RemoteControlEnabled { get; init; }
}
