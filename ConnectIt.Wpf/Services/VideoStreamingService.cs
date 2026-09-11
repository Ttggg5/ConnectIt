using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 把這台裝置變成一個獨立的影片伺服器:自己實作一個最小可用的 HTTP/1.1 伺服器(只支援
/// GET/HEAD、Range),不依賴 <see cref="ConnectionService"/> 的配對連線 — 任何在區網上發現
/// 這個服務的裝置都可以直接觀看,支援多個裝置同時觀看(每個連線各自獨立處理)。
///
/// 特意不用 <see cref="System.Net.HttpListener"/>:它底層是 Windows 的 http.sys,即使綁定的是
/// 單一 IP(不是 "+"/"*" 這種萬用字元),非系統管理員身分照樣會丟出「存取被拒」,一般使用者
/// 執行這個 App 時完全用不了。改用原始 <see cref="TcpListener"/> 自己解析/組出最小的 HTTP
/// 訊息,就不會受這個限制,跟 <see cref="ConnectionService"/> 自訂 TCP 協定的做法也算是一致的風格。
/// </summary>
public sealed class VideoStreamingService : IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;
    private Dictionary<string, string> _filesByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private string _serverName = string.Empty;

    // 縮圖產生(呼叫 Windows 殼層 API)相對昂貴,同一支影片整個伺服器存活期間只算一次,
    // 存的是 Task 而不是結果本身,這樣同時間好幾個請求剛好都在搶同一支還沒算完的縮圖時,
    // 大家會一起等同一個工作的結果,而不是每個請求都各自重新觸發一次產生。
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _listeners.Count > 0;

    public IReadOnlyList<IPAddress> BoundAddresses { get; private set; } = [];

    public int Port { get; private set; }

    public IReadOnlyList<VideoManifestEntry> Manifest { get; private set; } = [];

    /// <summary>開始分享。<paramref name="path"/> 可以是單一影片檔案,也可以是裝滿影片的資料夾
    /// (見 <see cref="VideoLibraryScanner.BuildManifest"/>)。</summary>
    public void Start(string path, string serverName)
    {
        Stop();

        var (manifest, filesByRelativePath) = VideoLibraryScanner.BuildManifest(path);
        if (manifest.Count == 0)
        {
            StatusChanged?.Invoke(this, "找不到可分享的影片。");
            return;
        }

        // 正常情況下(有 Wi-Fi/有線網卡)一定找得到可路由的位址;找不到的話(例如沒有網卡的測試環境、
        // 或只接了一個沒有預設閘道的隔離熱點)退回只綁 loopback,至少同一台機器上還能用,
        // 而不是整個功能直接開不了。
        var addresses = MdnsDiscoveryService.GetRoutableIPv4Addresses();
        if (addresses.Count == 0)
        {
            StatusChanged?.Invoke(this, "找不到區網網路介面,影片伺服器只會在本機開放。");
            addresses = [IPAddress.Loopback];
        }

        var port = GetFreeTcpPort(addresses[0]);

        var listeners = new List<TcpListener>();
        try
        {
            foreach (var address in addresses)
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                listeners.Add(listener);
            }
        }
        catch (SocketException ex)
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            StatusChanged?.Invoke(this, $"開啟影片伺服器失敗:{ex.Message}");
            return;
        }

        _listeners.AddRange(listeners);
        _serverName = serverName;
        _filesByRelativePath = new Dictionary<string, string>(filesByRelativePath, StringComparer.OrdinalIgnoreCase);
        _thumbnailCache.Clear();
        Manifest = manifest;
        BoundAddresses = addresses;
        Port = port;

        var cts = new CancellationTokenSource();
        _cts = cts;
        foreach (var listener in listeners)
        {
            _ = AcceptLoopAsync(listener, cts.Token);
        }

        StatusChanged?.Invoke(this, $"影片伺服器已啟動(連接埠 {port}),共 {manifest.Count} 部影片。");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        var wasRunning = _listeners.Count > 0;
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        _listeners.Clear();

        if (wasRunning)
        {
            StatusChanged?.Invoke(this, "影片伺服器已關閉。");
        }

        _filesByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _thumbnailCache.Clear();
        Manifest = [];
        BoundAddresses = [];
        Port = 0;
    }

    public void Dispose() => Stop();

    private static int GetFreeTcpPort(IPAddress bindAddress)
    {
        var listener = new TcpListener(bindAddress, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // listener 已經被 Stop(),結束這個迴圈。
            }

            _ = HandleClientAsync(client, token);
        }
    }

    /// <summary>每個連線只處理一個請求就關閉(回應帶 "Connection: close"),不做 keep-alive/pipelining,
    /// 對觀看/拖曳進度來說夠用,也大幅簡化實作。多個裝置/多個並行請求各自是獨立的 TcpClient,互不影響。</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                if (request != null)
                {
                    await RouteRequestAsync(stream, request, token).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class ParsedRequest
    {
        public required string Method { get; init; }

        public required string Path { get; init; }

        public required Dictionary<string, string> Headers { get; init; }
    }

    private static async Task<ParsedRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLine))
        {
            return null;
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? headerLine;
        while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync(token).ConfigureAwait(false)))
        {
            var idx = headerLine.IndexOf(':');
            if (idx > 0)
            {
                headers[headerLine[..idx].Trim()] = headerLine[(idx + 1)..].Trim();
            }
        }

        return new ParsedRequest { Method = parts[0], Path = parts[1], Headers = headers };
    }

    private async Task RouteRequestAsync(NetworkStream stream, ParsedRequest request, CancellationToken token)
    {
        // 請求列裡的路徑可能帶查詢字串(例如 "/watch?v=2"),而且是尚未解碼的原始形式
        // (媒體路徑觀看端組 URL 時逐段用 Uri.EscapeDataString 編碼過),要先拆開、解碼才能比對路由。
        var path = Uri.UnescapeDataString(request.Path.Split('?')[0]);

        if (path is "/" or "/index.html")
        {
            // 只有一部影片時不用另外顯示「只有一格」的清單頁,直接跳到觀看頁。
            if (Manifest.Count == 1)
            {
                await WriteRedirectAsync(stream, "/watch?v=0", token).ConfigureAwait(false);
                return;
            }

            var sort = ParseSortOption(request.Path);
            await WriteHtmlAsync(stream, BuildHomePageHtml(sort), token).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var index = ParseVideoIndex(request.Path);
            if (index is not { } i || i < 0 || i >= Manifest.Count)
            {
                await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                return;
            }

            var sort = ParseSortOption(request.Path);
            await WriteHtmlAsync(stream, BuildWatchPageHtml(i, sort), token).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/manifest", StringComparison.OrdinalIgnoreCase))
        {
            await WriteManifestAsync(stream, token).ConfigureAwait(false);
            return;
        }

        const string mediaPrefix = "/media/";
        if (path.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteMediaAsync(stream, request, path[mediaPrefix.Length..], token).ConfigureAwait(false);
            return;
        }

        const string thumbnailPrefix = "/thumbnail/";
        if (path.StartsWith(thumbnailPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteThumbnailAsync(stream, path[thumbnailPrefix.Length..], token).ConfigureAwait(false);
            return;
        }

        await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
    }

    private static string? GetQueryParam(string rawPathWithQuery, string key)
    {
        var queryIndex = rawPathWithQuery.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        foreach (var pair in rawPathWithQuery[(queryIndex + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }

    private static int? ParseVideoIndex(string rawPathWithQuery) =>
        int.TryParse(GetQueryParam(rawPathWithQuery, "v"), out var value) ? value : null;

    // 排序方式清單:(query string 用的值, 下拉選單顯示的文字)。放在同一個地方定義,
    // 避免「合法值檢查」「排序邏輯」「下拉選單選項」三處各自維護一份清單而漏改到某一處。
    private static readonly (string Value, string Label)[] SortOptions =
    [
        ("name_asc", "檔名(A→Z)"),
        ("name_desc", "檔名(Z→A)"),
        ("size_asc", "檔案大小(小→大)"),
        ("size_desc", "檔案大小(大→小)"),
        ("date_desc", "修改時間(新→舊)"),
        ("date_asc", "修改時間(舊→新)"),
    ];

    private const string DefaultSort = "name_asc";

    private static string ParseSortOption(string rawPathWithQuery)
    {
        var value = GetQueryParam(rawPathWithQuery, "sort");
        return value != null && SortOptions.Any(o => o.Value == value) ? value : DefaultSort;
    }

    /// <summary>依排序方式,回傳 manifest 項目「顯示順序」的索引清單(內容還是指向 <see cref="Manifest"/>
    /// 裡的原始索引,只是走訪順序不同)。首頁清單、觀看頁側邊清單、上一部/下一部都共用同一份排序邏輯。</summary>
    private List<int> GetDisplayOrder(string sort)
    {
        var indices = Enumerable.Range(0, Manifest.Count);
        return sort switch
        {
            "name_desc" => indices.OrderByDescending(i => Manifest[i].Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "size_asc" => indices.OrderBy(i => Manifest[i].Size).ToList(),
            "size_desc" => indices.OrderByDescending(i => Manifest[i].Size).ToList(),
            "date_asc" => indices.OrderBy(i => Manifest[i].Modified).ToList(),
            "date_desc" => indices.OrderByDescending(i => Manifest[i].Modified).ToList(),
            _ => indices.OrderBy(i => Manifest[i].Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static string BuildSortSelectHtml(string selected, string onChangeUrlPrefix) => $$"""
        <label class="sort-label">排序方式
          <select onchange="location.href='{{onChangeUrlPrefix}}'+this.value">
            {{string.Join('\n', SortOptions.Select(o => $"""<option value="{o.Value}"{(o.Value == selected ? " selected" : "")}>{o.Label}</option>"""))}}
          </select>
        </label>
        """;

    // ===================== 網頁(像 YouTube 一樣的首頁清單 + 觀看頁) =====================

    // 直接內嵌 Google Material Icons 的原始 SVG path 資料(而不是連到 Google Fonts/CDN 拉圖示字型)——
    // 這台伺服器只在區網內服務,不能假設觀看端連得到網際網路,所以整個網頁(含圖示)都要能離線運作。
    private static class Icons
    {
        public const string PlayArrow = "M8 5v14l11-7z";
        public const string Pause = "M6 19h4V5H6v14zm8-14v14h4V5h-4z";
        public const string SkipPrevious = "M6 6h2v12H6zm3.5 6l8.5 6V6z";
        public const string SkipNext = "M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z";
        public const string FastRewind = "M11 18V6l-8.5 6 8.5 6zm.5-6l8.5 6V6l-8.5 6z";
        public const string FastForward = "M4 18l8.5-6L4 6v12zm9-12v12l8.5-6L13 6z";
        public const string VolumeUp = "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z";
        public const string VolumeOff = "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z";
        public const string Fullscreen = "M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z";
        public const string FullscreenExit = "M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z";
        public const string ArrowBack = "M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z";
        public const string PlayCircle = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 14.5v-9l6 4.5-6 4.5z";
    }

    private static string Svg(string path, int size = 20) =>
        $"""<svg viewBox="0 0 24 24" width="{size}" height="{size}" fill="currentColor" aria-hidden="true"><path d="{path}"/></svg>""";

    // 讓瀏覽器的「上一頁」(滑鼠側鍵、Alt+左鍵、右鍵選單的上一頁等,不管哪種手勢都會觸發同一個
    // history 事件)永遠回到首頁,而不是照瀏覽紀錄一頁一頁往回跳到上一支影片。做法是每次進頁面時
    // 先自己疊一筆「內容跟目前網址相同」的 history 紀錄,使用者按上一頁時瀏覽器彈掉這筆紀錄、
    // 因為網址沒變所以判定成「同文件內導覽」而觸發 popstate(不會真的重新整理成上一份文件),
    // 這時我們攔截這個事件、自己導到首頁,使用者實際感受到的行為就是「上一頁 = 回首頁」。
    private const string BackToHomeTrapScript = """
        (function () {
          if (!window.history || !window.history.pushState) { return; }
          history.pushState(null, '', location.href);
          window.addEventListener('popstate', function () {
            location.href = '/';
          });
        })();
        """;

    private const string SharedCss = """
        :root{color-scheme:dark;--accent:#8b5cf6;}
        *{box-sizing:border-box;}
        body{margin:0;font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;background:#0f0f0f;color:#f1f1f1;}
        header{padding:16px 20px;border-bottom:1px solid #272727;display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap;}
        header h1{margin:0;font-size:18px;}
        a.back{color:#f1f1f1;text-decoration:none;font-size:16px;font-weight:600;display:inline-flex;align-items:center;gap:6px;}
        .sort-label{font-size:12px;color:#aaa;display:flex;align-items:center;gap:8px;white-space:nowrap;}
        .sort-label select{background:#2a2a2a;color:#f1f1f1;border:1px solid #3a3a3a;border-radius:6px;font-size:12px;padding:6px 8px;}
        .sidebar .sort-label{padding:0 2px 8px;}
        main.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;padding:20px;}
        .card{color:inherit;text-decoration:none;background:#181818;border-radius:12px;overflow:hidden;display:block;}
        .card:hover{background:#222;}
        .thumb{aspect-ratio:16/9;position:relative;overflow:hidden;color:#555;background:#272727;}
        .thumb-fallback{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;}
        .thumb img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;background:#272727;}
        .card .title{padding:10px 12px 2px;font-weight:600;font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
        .card .meta{padding:0 12px 12px;font-size:12px;color:#aaa;}
        main.watch{max-width:1400px;margin:0 auto;padding:20px;display:grid;grid-template-columns:minmax(0,1fr) 360px;gap:24px;align-items:start;}
        main.watch .primary{min-width:0;}
        main.watch h1{font-size:18px;margin:16px 0 4px;}
        main.watch .meta{color:#aaa;font-size:13px;margin:0 0 16px;}
        @media (max-width:860px){main.watch{grid-template-columns:minmax(0,1fr);}}

        .sidebar{display:flex;flex-direction:column;gap:4px;}
        .side-item{color:#f1f1f1;text-decoration:none;padding:6px;border-radius:8px;display:flex;gap:10px;align-items:flex-start;cursor:pointer;}
        a.side-item:hover{background:#272727;}
        .side-item.current{background:#211a2e;}
        .side-thumb{position:relative;flex:0 0 130px;width:130px;aspect-ratio:16/9;border-radius:8px;overflow:hidden;background:#272727;color:#555;}
        .side-thumb-fallback{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;}
        .side-thumb img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;}
        .side-info{min-width:0;flex:1;padding-top:1px;}
        .side-title{font-size:13px;font-weight:600;line-height:1.35;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;}
        .side-item.current .side-title{color:var(--accent);}
        .side-meta{font-size:11px;color:#aaa;margin-top:4px;}
        .now-playing{font-size:11px;color:var(--accent);font-weight:600;margin-top:4px;display:flex;align-items:center;gap:4px;}
        .now-playing svg{width:12px;height:12px;}

        .player-wrap{position:relative;background:#000;border-radius:12px;overflow:hidden;}
        .player-wrap video{width:100%;max-height:70vh;display:block;background:#000;}
        .controls{background:#181818;padding:6px 12px 10px;}
        .controls input[type=range]{-webkit-appearance:none;appearance:none;width:100%;height:5px;border-radius:3px;background:#3a3a3a;outline:none;cursor:pointer;}
        .controls input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;appearance:none;width:13px;height:13px;border-radius:50%;background:var(--accent);cursor:pointer;}
        .controls input[type=range]::-moz-range-thumb{width:13px;height:13px;border:none;border-radius:50%;background:var(--accent);cursor:pointer;}
        .progress-row{padding:6px 0 4px;}
        .controls-row{display:flex;align-items:center;gap:2px;flex-wrap:wrap;}
        .controls-row button{background:none;border:none;color:#f1f1f1;cursor:pointer;padding:6px 8px;border-radius:6px;display:inline-flex;align-items:center;justify-content:center;}
        .controls-row button svg{display:block;}
        .controls-row button:hover{background:#2a2a2a;}
        .controls-row button:disabled{opacity:.3;cursor:default;}
        .controls-row button:disabled:hover{background:none;}
        .time{font-size:12px;color:#ccc;white-space:nowrap;padding:0 6px;}
        .spacer{flex:1;}
        .autoplay-toggle{display:flex;align-items:center;gap:4px;font-size:12px;color:#ccc;white-space:nowrap;cursor:pointer;padding:0 6px;}
        #volume{width:70px;}
        #speed{background:#2a2a2a;color:#f1f1f1;border:1px solid #3a3a3a;border-radius:6px;font-size:12px;padding:4px 2px;margin:0 4px;}
        .next-overlay{position:absolute;right:16px;bottom:16px;background:rgba(24,24,24,.95);border-radius:10px;padding:12px 14px;display:flex;align-items:center;gap:12px;box-shadow:0 4px 16px rgba(0,0,0,.5);}
        .next-overlay[hidden]{display:none;}
        .next-overlay span{font-size:13px;}
        .next-overlay button{background:var(--accent);color:#fff;border:none;border-radius:6px;padding:6px 10px;font-size:12px;cursor:pointer;}
        """;

    private string BuildHomePageHtml(string sort)
    {
        var cards = string.Join('\n', GetDisplayOrder(sort).Select(index =>
        {
            var entry = Manifest[index];
            return $"""
                <a class="card" href="/watch?v={index}&sort={sort}">
                  <div class="thumb">
                    <div class="thumb-fallback">{Svg(Icons.PlayCircle, 40)}</div>
                    <img src="/thumbnail/{EscapeRelativePathForUrl(entry.RelativePath)}" alt="" loading="lazy" onerror="this.style.display='none'">
                  </div>
                  <div class="title">{HtmlEncode(entry.Name)}</div>
                  <div class="meta">{FormatFileSize(entry.Size)}</div>
                </a>
                """;
        }));

        var title = HtmlEncode(_serverName);
        return $"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title}</title>
            <style>{SharedCss}</style></head>
            <body>
              <header>
                <h1>{title}</h1>
                {BuildSortSelectHtml(sort, "/?sort=")}
              </header>
              <main class="grid">
                {cards}
              </main>
              <script>{BackToHomeTrapScript}</script>
            </body>
            </html>
            """;
    }

    private string BuildWatchPageHtml(int index, string sort)
    {
        var entry = Manifest[index];
        var order = GetDisplayOrder(sort);
        var position = order.IndexOf(index);
        var hasPrev = position > 0;
        var hasNext = position >= 0 && position < order.Count - 1;
        var prevIndex = hasPrev ? order[position - 1] : (int?)null;
        var nextIndex = hasNext ? order[position + 1] : (int?)null;

        var sidebar = Manifest.Count > 1
            ? $"""
                <aside class="sidebar">
                  {BuildSortSelectHtml(sort, $"/watch?v={index}&sort=")}
                  {BuildSidebarItems(order, index, sort)}
                </aside>
                """
            : string.Empty;
        var title = HtmlEncode(entry.Name);
        var backLabel = HtmlEncode(_serverName);

        // 傳給前端 JS 用的中繼資料,以 JSON 安全編碼字串內容,並把 "</" 斷開避免檔名剛好含有
        // "</script>" 這種字串時提早把 <script> 區塊截斷。
        var relativePathJson = JsonSerializer.Serialize(entry.RelativePath).Replace("</", "<\\/");
        var sortJson = JsonSerializer.Serialize(sort).Replace("</", "<\\/");
        var prevIndexJson = prevIndex is { } p ? p.ToString() : "null";
        var nextIndexJson = nextIndex is { } n ? n.ToString() : "null";

        return $$"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>{{SharedCss}}</style></head>
            <body>
              <header><a class="back" href="/?sort={{sort}}">{{Svg(Icons.ArrowBack, 18)}} {{backLabel}}</a></header>
              <main class="watch">
                <div class="primary">
                  <div class="player-wrap" id="playerWrap">
                    <video id="player" src="/media/{{EscapeRelativePathForUrl(entry.RelativePath)}}" poster="/thumbnail/{{EscapeRelativePathForUrl(entry.RelativePath)}}"></video>
                    <div class="next-overlay" id="nextOverlay" hidden>
                      <span id="nextOverlayText"></span>
                      <button id="cancelNextBtn" type="button">取消</button>
                    </div>
                  </div>
                  <div class="controls">
                    <div class="progress-row">
                      <input type="range" id="seek" min="0" max="0" step="0.1" value="0">
                    </div>
                    <div class="controls-row">
                      <button id="prevBtn" type="button" title="上一部"{{(hasPrev ? "" : " disabled")}}>{{Svg(Icons.SkipPrevious)}}</button>
                      <button id="backBtn" type="button" title="倒退 10 秒">{{Svg(Icons.FastRewind)}}</button>
                      <button id="playBtn" type="button" title="播放/暫停">{{Svg(Icons.PlayArrow)}}</button>
                      <button id="fwdBtn" type="button" title="快轉 10 秒">{{Svg(Icons.FastForward)}}</button>
                      <button id="nextBtn" type="button" title="下一部"{{(hasNext ? "" : " disabled")}}>{{Svg(Icons.SkipNext)}}</button>
                      <span class="time" id="time">0:00 / 0:00</span>
                      <span class="spacer"></span>
                      <label class="autoplay-toggle"><input type="checkbox" id="autoplayNext" checked> 自動播放下一部</label>
                      <select id="speed" title="播放速度">
                        <option value="0.5">0.5x</option>
                        <option value="1" selected>1x</option>
                        <option value="1.25">1.25x</option>
                        <option value="1.5">1.5x</option>
                        <option value="2">2x</option>
                      </select>
                      <button id="muteBtn" type="button" title="靜音">{{Svg(Icons.VolumeUp)}}</button>
                      <input type="range" id="volume" min="0" max="100" value="100">
                      <button id="fsBtn" type="button" title="全螢幕">{{Svg(Icons.Fullscreen)}}</button>
                    </div>
                  </div>
                  <h1>{{title}}</h1>
                  <p class="meta">{{FormatFileSize(entry.Size)}}</p>
                </div>
                {{sidebar}}
              </main>
              <script>{{BackToHomeTrapScript}}</script>
              <script>{{BuildWatchPageScript(relativePathJson, prevIndexJson, nextIndexJson, sortJson)}}</script>
            </body>
            </html>
            """;
    }

    /// <summary>右側影片清單:依目前選的排序方式列出全部影片(含目前播放中的那一部),每筆都附縮圖,
    /// 目前播放中的那筆用樣式標示、不能再點(不用整個重新導向到自己)。</summary>
    private string BuildSidebarItems(IReadOnlyList<int> order, int currentIndex, string sort) => string.Join('\n', order.Select(i =>
    {
        var entry = Manifest[i];
        var isCurrent = i == currentIndex;
        var thumb = $"""
            <div class="side-thumb">
              <div class="side-thumb-fallback">{Svg(Icons.PlayCircle, 24)}</div>
              <img src="/thumbnail/{EscapeRelativePathForUrl(entry.RelativePath)}" alt="" loading="lazy" onerror="this.style.display='none'">
            </div>
            """;
        var info = $"""
            <div class="side-info">
              <div class="side-title">{HtmlEncode(entry.Name)}</div>
              <div class="side-meta">{FormatFileSize(entry.Size)}</div>
              {(isCurrent ? $"""<div class="now-playing">{Svg(Icons.PlayArrow)} 正在播放</div>""" : string.Empty)}
            </div>
            """;

        return isCurrent
            ? $"""<div class="side-item current">{thumb}{info}</div>"""
            : $"""<a class="side-item" href="/watch?v={i}&sort={sort}">{thumb}{info}</a>""";
    }));

    private static string BuildWatchPageScript(string relativePathJson, string prevIndexJson, string nextIndexJson, string sortJson) => $$"""
        (function () {
          var meta = { relativePath: {{relativePathJson}}, prevIndex: {{prevIndexJson}}, nextIndex: {{nextIndexJson}}, sort: {{sortJson}} };
          function watchUrl(index) { return '/watch?v=' + index + '&sort=' + meta.sort; }
          var ICON_PLAY = '{{Svg(Icons.PlayArrow)}}';
          var ICON_PAUSE = '{{Svg(Icons.Pause)}}';
          var ICON_VOLUME_UP = '{{Svg(Icons.VolumeUp)}}';
          var ICON_VOLUME_OFF = '{{Svg(Icons.VolumeOff)}}';
          var ICON_FULLSCREEN = '{{Svg(Icons.Fullscreen)}}';
          var ICON_FULLSCREEN_EXIT = '{{Svg(Icons.FullscreenExit)}}';
          var video = document.getElementById('player');
          var playBtn = document.getElementById('playBtn');
          var seek = document.getElementById('seek');
          var timeLabel = document.getElementById('time');
          var backBtn = document.getElementById('backBtn');
          var fwdBtn = document.getElementById('fwdBtn');
          var prevBtn = document.getElementById('prevBtn');
          var nextBtn = document.getElementById('nextBtn');
          var muteBtn = document.getElementById('muteBtn');
          var volume = document.getElementById('volume');
          var speed = document.getElementById('speed');
          var fsBtn = document.getElementById('fsBtn');
          var autoplayNext = document.getElementById('autoplayNext');
          var nextOverlay = document.getElementById('nextOverlay');
          var nextOverlayText = document.getElementById('nextOverlayText');
          var cancelNextBtn = document.getElementById('cancelNextBtn');
          var playerWrap = document.getElementById('playerWrap');

          var posKey = 'connectit-pos:' + meta.relativePath;
          var volKey = 'connectit-volume';
          var speedKey = 'connectit-speed';
          var seeking = false;
          var autoplayTimer = null;

          function fmt(sec) {
            if (!isFinite(sec) || sec < 0) sec = 0;
            sec = Math.floor(sec);
            var h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
            var mm = (h > 0 && m < 10) ? ('0' + m) : String(m);
            var ss = s < 10 ? ('0' + s) : String(s);
            return h > 0 ? (h + ':' + mm + ':' + ss) : (mm + ':' + ss);
          }

          function updateSeekFill() {
            var dur = video.duration || 0;
            var playedPct = dur ? (video.currentTime / dur * 100) : 0;
            var bufferedPct = 0;
            if (video.buffered.length) {
              bufferedPct = dur ? (video.buffered.end(video.buffered.length - 1) / dur * 100) : 0;
            }
            seek.style.background = 'linear-gradient(to right, var(--accent) ' + playedPct + '%, #666 ' + playedPct + '%, #666 ' + bufferedPct + '%, #3a3a3a ' + bufferedPct + '%)';
          }

          function updateMuteIcon() {
            muteBtn.innerHTML = (video.muted || video.volume === 0) ? ICON_VOLUME_OFF : ICON_VOLUME_UP;
          }

          function togglePlay() {
            if (video.paused) { video.play().catch(function () {}); } else { video.pause(); }
          }

          // 還原上次的音量/播放速度(整台伺服器共用,不分影片)。
          var savedVol = localStorage.getItem(volKey);
          if (savedVol !== null) {
            video.volume = Math.min(1, Math.max(0, parseFloat(savedVol)));
            volume.value = String(Math.round(video.volume * 100));
          }
          var savedSpeed = localStorage.getItem(speedKey);
          if (savedSpeed !== null) {
            video.playbackRate = parseFloat(savedSpeed);
            speed.value = savedSpeed;
          }
          updateMuteIcon();

          video.addEventListener('loadedmetadata', function () {
            seek.max = String(video.duration || 0);
            var savedPos = parseFloat(localStorage.getItem(posKey) || '0');
            if (savedPos > 5 && savedPos < video.duration - 5) {
              video.currentTime = savedPos;
            }
            timeLabel.textContent = fmt(video.currentTime) + ' / ' + fmt(video.duration);
            video.play().catch(function () {});
          });

          video.addEventListener('timeupdate', function () {
            if (!seeking) { seek.value = String(video.currentTime); }
            timeLabel.textContent = fmt(video.currentTime) + ' / ' + fmt(video.duration);
            updateSeekFill();
            if (video.currentTime > 2) {
              localStorage.setItem(posKey, String(video.currentTime));
            }
          });
          video.addEventListener('progress', updateSeekFill);
          video.addEventListener('play', function () { playBtn.innerHTML = ICON_PAUSE; });
          video.addEventListener('pause', function () { playBtn.innerHTML = ICON_PLAY; });
          video.addEventListener('click', togglePlay);

          video.addEventListener('ended', function () {
            localStorage.removeItem(posKey);
            if (meta.nextIndex === null || !autoplayNext.checked) { return; }

            var secondsLeft = 5;
            nextOverlay.hidden = false;
            nextOverlayText.textContent = '即將播放下一部…(' + secondsLeft + ')';
            autoplayTimer = setInterval(function () {
              secondsLeft--;
              if (secondsLeft <= 0) {
                clearInterval(autoplayTimer);
                location.href = watchUrl(meta.nextIndex);
              } else {
                nextOverlayText.textContent = '即將播放下一部…(' + secondsLeft + ')';
              }
            }, 1000);
          });
          cancelNextBtn.addEventListener('click', function () {
            clearInterval(autoplayTimer);
            nextOverlay.hidden = true;
          });

          playBtn.addEventListener('click', togglePlay);
          backBtn.addEventListener('click', function () { video.currentTime = Math.max(0, video.currentTime - 10); });
          fwdBtn.addEventListener('click', function () { video.currentTime = Math.min(video.duration || 1e9, video.currentTime + 10); });

          seek.addEventListener('input', function () {
            seeking = true;
            timeLabel.textContent = fmt(parseFloat(seek.value)) + ' / ' + fmt(video.duration);
          });
          seek.addEventListener('change', function () {
            video.currentTime = parseFloat(seek.value);
            seeking = false;
          });

          volume.addEventListener('input', function () {
            video.volume = Number(volume.value) / 100;
            video.muted = false;
            localStorage.setItem(volKey, String(video.volume));
            updateMuteIcon();
          });
          muteBtn.addEventListener('click', function () { video.muted = !video.muted; updateMuteIcon(); });

          speed.addEventListener('change', function () {
            video.playbackRate = parseFloat(speed.value);
            localStorage.setItem(speedKey, speed.value);
          });

          fsBtn.addEventListener('click', function () {
            if (document.fullscreenElement) { document.exitFullscreen(); } else { playerWrap.requestFullscreen(); }
          });
          document.addEventListener('fullscreenchange', function () {
            fsBtn.innerHTML = document.fullscreenElement ? ICON_FULLSCREEN_EXIT : ICON_FULLSCREEN;
          });

          if (meta.prevIndex !== null) {
            prevBtn.addEventListener('click', function () { location.href = watchUrl(meta.prevIndex); });
          }
          if (meta.nextIndex !== null) {
            nextBtn.addEventListener('click', function () { location.href = watchUrl(meta.nextIndex); });
          }

          document.addEventListener('keydown', function (e) {
            var tag = (e.target && e.target.tagName) || '';
            if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') { return; }

            if (e.key === ' ') { e.preventDefault(); togglePlay(); }
            else if (e.key === 'ArrowLeft') { video.currentTime = Math.max(0, video.currentTime - 5); }
            else if (e.key === 'ArrowRight') { video.currentTime = Math.min(video.duration || 1e9, video.currentTime + 5); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); volume.value = String(Math.min(100, Number(volume.value) + 5)); volume.dispatchEvent(new Event('input')); }
            else if (e.key === 'ArrowDown') { e.preventDefault(); volume.value = String(Math.max(0, Number(volume.value) - 5)); volume.dispatchEvent(new Event('input')); }
            else if (e.key === 'f' || e.key === 'F') { fsBtn.click(); }
            else if (e.key === 'm' || e.key === 'M') { muteBtn.click(); }
          });
        })();
        """;

    private static string HtmlEncode(string value) => WebUtility.HtmlEncode(value);

    private static string EscapeRelativePathForUrl(string relativePath) =>
        string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }

    private static async Task WriteHtmlAsync(NetworkStream stream, string html, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        await WriteHeadersAsync(
            stream, 200, "OK", [("Content-Type", "text/html; charset=utf-8"), ("Content-Length", bytes.Length.ToString())], token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static Task WriteRedirectAsync(NetworkStream stream, string location, CancellationToken token) =>
        WriteHeadersAsync(stream, 302, "Found", [("Location", location), ("Content-Length", "0")], token);

    private static async Task WriteHeadersAsync(
        NetworkStream stream, int statusCode, string statusText, IEnumerable<(string Name, string Value)> headers, CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(statusText).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static Task WriteStatusOnlyAsync(NetworkStream stream, int statusCode, string statusText, CancellationToken token) =>
        WriteHeadersAsync(stream, statusCode, statusText, [("Content-Length", "0")], token);

    private async Task WriteManifestAsync(NetworkStream stream, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new VideoManifestResponse { Name = _serverName, Entries = Manifest });

        await WriteHeadersAsync(
            stream, 200, "OK",
            [("Content-Type", "application/json"), ("Content-Length", payload.Length.ToString())], token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    private async Task WriteThumbnailAsync(NetworkStream stream, string relativePath, CancellationToken token)
    {
        if (!_filesByRelativePath.TryGetValue(relativePath, out var fullPath))
        {
            await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        var jpeg = await _thumbnailCache.GetOrAdd(relativePath, _ => VideoThumbnailGenerator.TryGenerateAsync(fullPath)).ConfigureAwait(false);
        if (jpeg == null)
        {
            await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        await WriteHeadersAsync(
            stream, 200, "OK",
            [("Content-Type", "image/jpeg"), ("Content-Length", jpeg.Length.ToString()), ("Cache-Control", "public, max-age=86400")],
            token).ConfigureAwait(false);
        await stream.WriteAsync(jpeg, token).ConfigureAwait(false);
    }

    private async Task WriteMediaAsync(NetworkStream stream, ParsedRequest request, string relativePath, CancellationToken token)
    {
        if (!_filesByRelativePath.TryGetValue(relativePath, out var fullPath))
        {
            await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        var totalLength = fileInfo.Length;
        request.Headers.TryGetValue("Range", out var rangeHeader);
        var (start, end, isRangeRequest) = ParseRange(rangeHeader, totalLength);
        if (start < 0)
        {
            await WriteHeadersAsync(
                stream, 416, "Range Not Satisfiable",
                [("Content-Range", $"bytes */{totalLength}"), ("Content-Length", "0")], token).ConfigureAwait(false);
            return;
        }

        var contentLength = end - start + 1;
        var headers = new List<(string Name, string Value)>
        {
            ("Accept-Ranges", "bytes"),
            ("Content-Type", GetContentType(fileInfo.Extension)),
            ("Content-Length", contentLength.ToString()),
        };

        if (isRangeRequest)
        {
            headers.Add(("Content-Range", $"bytes {start}-{end}/{totalLength}"));
        }

        await WriteHeadersAsync(stream, isRangeRequest ? 206 : 200, isRangeRequest ? "Partial Content" : "OK", headers, token).ConfigureAwait(false);

        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fileStream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[CopyBufferSize];
        var remaining = contentLength;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>解析 "Range: bytes=start-end" 標頭。回傳 Start &lt; 0 代表範圍不合法(應回 416)。</summary>
    private static (long Start, long End, bool IsRangeRequest) ParseRange(string? rangeHeader, long totalLength)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (0, totalLength - 1, false);
        }

        var spec = rangeHeader["bytes=".Length..].Split('-');
        if (spec.Length != 2)
        {
            return (0, totalLength - 1, false);
        }

        var hasStart = long.TryParse(spec[0], out var start);
        var hasEnd = long.TryParse(spec[1], out var end);

        if (!hasStart && !hasEnd)
        {
            return (-1, -1, true);
        }

        if (!hasStart)
        {
            // "-500" 代表檔案最後 500 bytes。
            start = totalLength - end;
            end = totalLength - 1;
        }
        else if (!hasEnd)
        {
            end = totalLength - 1;
        }

        if (start < 0 || end >= totalLength || start > end)
        {
            return (-1, -1, true);
        }

        return (start, end, true);
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        ".webm" => "video/webm",
        ".ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        _ => "application/octet-stream",
    };
}
