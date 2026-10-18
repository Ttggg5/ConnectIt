using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 記住「最近一次在哪台影片伺服器看了哪一部」,讓影片頁可以顯示「繼續觀看」。
/// 觀看是在內嵌瀏覽器裡開對方的網站,播放進度本身由網頁的 localStorage 記住(見 VideoStreamingService
/// 的觀看頁腳本),這裡只需要記住觀看頁的網址與標題,回來時直接導到同一個網址即可。
/// </summary>
public sealed class RecentlyWatchedService
{
    private readonly string _settingsFilePath;

    public RecentlyWatchedEntry? Last { get; private set; }

    public RecentlyWatchedService()
        : this(Path.Combine(AppContext.BaseDirectory, "recently-watched.json"))
    {
    }

    /// <summary>測試用:指定設定檔位置。</summary>
    public RecentlyWatchedService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        Last = Load();
    }

    /// <summary>
    /// 內嵌瀏覽器每次導覽完成時呼叫。只有觀看頁(<c>/watch?...</c>)才會被記下來,首頁/資料夾頁不算。
    /// 網址只保留路徑與查詢字串,不含主機與連接埠——對方重新開啟伺服器時連接埠通常會變,
    /// 「繼續觀看」時改用當下探索到的位址組回完整網址。
    /// </summary>
    public bool Record(string serverName, Uri pageUri, string? pageTitle)
    {
        if (!IsWatchPage(pageUri))
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(pageTitle) ? serverName : pageTitle.Trim();
        Last = new RecentlyWatchedEntry
        {
            ServerName = serverName,
            PathAndQuery = pageUri.PathAndQuery,
            Title = title,
            WatchedAtUtc = DateTime.UtcNow,
        };
        Save();
        return true;
    }

    public void Clear()
    {
        Last = null;
        try
        {
            File.Delete(_settingsFilePath);
        }
        catch
        {
            // 刪不掉就算了,下次 Record 會覆蓋。
        }
    }

    public static bool IsWatchPage(Uri uri) =>
        uri.Scheme is "http" or "https" && uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase);

    private RecentlyWatchedEntry? Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var entry = JsonSerializer.Deserialize<RecentlyWatchedEntry>(File.ReadAllText(_settingsFilePath));
                return entry is { ServerName.Length: > 0, PathAndQuery.Length: > 0 } ? entry : null;
            }
        }
        catch
        {
            // 檔案損毀時當作沒有紀錄。
        }

        return null;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(Last));
        }
        catch
        {
            // 儲存失敗只影響下次啟動時的「繼續觀看」。
        }
    }
}

public sealed class RecentlyWatchedEntry
{
    public string ServerName { get; set; } = string.Empty;

    /// <summary>觀看頁的路徑與查詢字串,例如 <c>/watch?v=3&amp;sort=name_asc</c>。</summary>
    public string PathAndQuery { get; set; } = string.Empty;

    /// <summary>觀看頁的網頁標題(影片名稱)。</summary>
    public string Title { get; set; } = string.Empty;

    public DateTime WatchedAtUtc { get; set; }
}
