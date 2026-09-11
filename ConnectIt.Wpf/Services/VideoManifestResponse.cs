namespace ConnectIt.Wpf.Services;

/// <summary>影片伺服器 <c>GET /manifest</c> 回傳的 JSON 內容,主機端序列化、觀看端反序列化共用同一個型別。</summary>
public sealed class VideoManifestResponse
{
    public required string Name { get; init; }

    public required IReadOnlyList<VideoManifestEntry> Entries { get; init; }
}
