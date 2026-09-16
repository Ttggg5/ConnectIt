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
    private VideoServerPlaybackOptions _options = VideoServerPlaybackOptions.Default;

    // 縮圖產生(呼叫 Windows 殼層 API)相對昂貴,同一支影片整個伺服器存活期間只算一次,
    // 存的是 Task 而不是結果本身,這樣同時間好幾個請求剛好都在搶同一支還沒算完的縮圖時,
    // 大家會一起等同一個工作的結果,而不是每個請求都各自重新觸發一次產生。
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    // 影片長度只有遙控面板畫時間軸時才需要,一樣用 Task 快取,同一支影片整個伺服器存活期間
    // 只探測一次。
    private readonly ConcurrentDictionary<string, Task<long?>> _durationCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _listeners.Count > 0;

    public IReadOnlyList<IPAddress> BoundAddresses { get; private set; } = [];

    public int Port { get; private set; }

    public IReadOnlyList<VideoManifestEntry> Manifest { get; private set; } = [];

    /// <summary>遠端控制模式的播放狀態——主機端遙控面板直接呼叫這裡的方法下指令(同一個 process,
    /// 不用透過網路),觀眾端則透過 <c>GET /control/state</c> 輪詢讀取。</summary>
    public PlaybackControlState ControlState { get; } = new();

    /// <summary>開始分享。<paramref name="path"/> 可以是單一影片檔案,也可以是裝滿影片的資料夾
    /// (見 <see cref="VideoLibraryScanner.BuildManifest"/>)。<paramref name="options"/> 是使用者在
    /// 設定頁調整過的自訂選項(預設排序、播放器預設行為、額外副檔名),省略時套用內建預設值。</summary>
    public void Start(string path, string serverName, VideoServerPlaybackOptions? options = null)
    {
        Stop();

        _options = options ?? VideoServerPlaybackOptions.Default;
        var (manifest, filesByRelativePath) = VideoLibraryScanner.BuildManifest(path, _options.ExtraExtensions);
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
        _durationCache.Clear();
        Manifest = [];
        BoundAddresses = [];
        Port = 0;
        ControlState.Reset();
    }

    /// <summary>供遙控面板畫時間軸用——同一個 process 內直接呼叫,不像縮圖/媒體是走 HTTP 端點。
    /// 探測失敗(格式不支援/檔案有問題)回傳 null,呼叫端要自行處理「不知道總長度」的情況。</summary>
    public Task<long?> GetDurationMsAsync(string relativePath)
    {
        if (!_filesByRelativePath.TryGetValue(relativePath, out var fullPath))
        {
            return Task.FromResult<long?>(null);
        }

        return _durationCache.GetOrAdd(relativePath, _ => VideoDurationProbe.TryGetDurationMsAsync(fullPath));
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

        // 遠端控制模式中,觀眾不該看得到可以自己瀏覽/挑影片的首頁清單——跟原生 App 端「鎖住
        // VideoServerScreen,只能看主機正在播的那一部」是同一個道理。所以首頁、觀看頁都改成
        // 完全以主機目前選的影片為準,忽略請求本身要求的是哪一部/哪個排序。
        var controlSnapshot = ControlState.Snapshot();

        if (path is "/" or "/index.html")
        {
            if (controlSnapshot.Enabled)
            {
                if (controlSnapshot.VideoRelativePath is { } activeRelativePath)
                {
                    await WriteRedirectAsync(
                        stream, $"/watch?path={Uri.EscapeDataString(activeRelativePath)}", token).ConfigureAwait(false);
                }
                else
                {
                    await WriteHtmlAsync(stream, BuildRemoteWaitingPageHtml(), token).ConfigureAwait(false);
                }

                return;
            }

            // 只有一部影片時不用另外顯示「只有一格」的清單頁,直接跳到觀看頁。
            if (Manifest.Count == 1)
            {
                await WriteRedirectAsync(stream, "/watch?v=0", token).ConfigureAwait(false);
                return;
            }

            var sort = ParseSortOption(request.Path);
            var flat = GetQueryParam(request.Path, "flat") == "1";
            var folder = flat ? string.Empty : NormalizeFolder(GetQueryParam(request.Path, "folder"));
            await WriteHtmlAsync(stream, BuildHomePageHtml(sort, folder, flat), token).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var watchFlat = GetQueryParam(request.Path, "flat") == "1";
            var watchFolder = watchFlat ? string.Empty : NormalizeFolder(GetQueryParam(request.Path, "folder"));
            var watchShuffle = ParseShuffle(request.Path);
            var watchSeed = ParseSeed(request.Path);

            if (controlSnapshot.Enabled)
            {
                if (controlSnapshot.VideoRelativePath is not { } activeRelativePath)
                {
                    await WriteHtmlAsync(stream, BuildRemoteWaitingPageHtml(), token).ConfigureAwait(false);
                    return;
                }

                // 忽略請求本身要求的是哪一部——不管網址列打的是哪個 v=/path=,遠端控制中一律
                // 只顯示主機目前選的那一部,這樣觀眾沒有辦法透過改網址繞過鎖定。
                if (ResolveIndexByRelativePath(activeRelativePath) is not { } activeIndex)
                {
                    await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                    return;
                }

                await WriteHtmlAsync(
                    stream,
                    BuildWatchPageHtml(activeIndex, ParseSortOption(request.Path), watchFolder, watchFlat, watchShuffle, watchSeed),
                    token).ConfigureAwait(false);
                return;
            }

            var index = ResolveVideoIndex(request.Path);
            if (index is not { } i || i < 0 || i >= Manifest.Count)
            {
                await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                return;
            }

            var sort = ParseSortOption(request.Path);
            await WriteHtmlAsync(
                stream, BuildWatchPageHtml(i, sort, watchFolder, watchFlat, watchShuffle, watchSeed), token).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/manifest", StringComparison.OrdinalIgnoreCase))
        {
            await WriteManifestAsync(stream, token).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/control/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteControlStateAsync(stream, token).ConfigureAwait(false);
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

    /// <summary>除了原本以 index 選片("?v=")外,也支援以相對路徑選片("?path=")——遠端控制切換
    /// 影片時 index 會因為排序方式不同而不穩定,相對路徑才是跨排序、跨裝置都穩定的識別方式。</summary>
    private int? ResolveVideoIndex(string rawPathWithQuery)
    {
        var path = GetQueryParam(rawPathWithQuery, "path");
        if (path != null)
        {
            return ResolveIndexByRelativePath(path);
        }

        return ParseVideoIndex(rawPathWithQuery);
    }

    private int? ResolveIndexByRelativePath(string relativePath)
    {
        for (var i = 0; i < Manifest.Count; i++)
        {
            if (string.Equals(Manifest[i].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

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

    private string ParseSortOption(string rawPathWithQuery)
    {
        var value = GetQueryParam(rawPathWithQuery, "sort");
        return value != null && SortOptions.Any(o => o.Value == value) ? value : _options.DefaultSort;
    }

    /// <summary>跟 <see cref="ParseSortOption"/> 同一個道理:網址沒帶 "shuffle" 查詢參數時(例如
    /// 直接打 /watch?v=0,或首頁清單上沒特別點「隨機播放」的一般影片卡片),退回伺服器啟動時
    /// 套用的「預設隨機播放」設定(<see cref="_options"/>.Shuffle),而不是一律當作沒開。</summary>
    private bool ParseShuffle(string rawPathWithQuery)
    {
        var value = GetQueryParam(rawPathWithQuery, "shuffle");
        return value != null ? value == "1" : _options.Shuffle;
    }

    private static int ParseSeed(string rawPathWithQuery) =>
        int.TryParse(GetQueryParam(rawPathWithQuery, "seed"), out var seed) ? seed : 0;

    /// <summary>依排序方式,回傳 manifest 項目「顯示順序」的索引清單(內容還是指向 <see cref="Manifest"/>
    /// 裡的原始索引,只是走訪順序不同)。首頁清單、觀看頁側邊清單、上一部/下一部都共用同一份排序邏輯。
    /// <paramref name="shuffle"/> 為真時,在排序結果之上用 <paramref name="seed"/> 洗牌一次——同一組
    /// seed 每次重算結果都一樣,讓「隨機播放」這一輪瀏覽過程中上一部/下一部順序穩定、不會每次請求
    /// 都重新洗一次牌。</summary>
    private List<int> GetDisplayOrder(string sort, bool shuffle = false, int seed = 0)
    {
        var indices = Enumerable.Range(0, Manifest.Count);
        var ordered = sort switch
        {
            "name_desc" => indices.OrderByDescending(i => Manifest[i].Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "size_asc" => indices.OrderBy(i => Manifest[i].Size).ToList(),
            "size_desc" => indices.OrderByDescending(i => Manifest[i].Size).ToList(),
            "date_asc" => indices.OrderBy(i => Manifest[i].Modified).ToList(),
            "date_desc" => indices.OrderByDescending(i => Manifest[i].Modified).ToList(),
            _ => indices.OrderBy(i => Manifest[i].Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        if (!shuffle)
        {
            return ordered;
        }

        var random = new Random(seed);
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        return ordered;
    }

    private static readonly (double Value, string Label)[] SpeedOptions =
    [
        (0.5, "0.5x"),
        (1, "1x"),
        (1.25, "1.25x"),
        (1.5, "1.5x"),
        (2, "2x"),
    ];

    private string BuildSpeedOptionsHtml() => string.Join('\n', SpeedOptions.Select(o =>
    {
        var selected = Math.Abs(o.Value - _options.DefaultSpeed) < 0.0001 ? " selected" : "";
        // "0.5"/"1"/"1.25" 這種 invariant 格式跟前端 JS 用同一個字串當 <option value> 比對,
        // 不能用會受系統語系影響的預設 ToString()。
        var value = o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"""<option value="{value}"{selected}>{o.Label}</option>""";
    }));

    private static string BuildSortSelectHtml(string selected, string onChangeUrlPrefix, string onChangeUrlSuffix = "") => $$"""
        <label class="sort-label">排序方式
          <select onchange="location.href='{{onChangeUrlPrefix}}'+this.value+'{{onChangeUrlSuffix}}'">
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
        public const string Folder = "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z";
        public const string Settings = "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z";
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
        .player-fullscreen-wrap:fullscreen,.player-fullscreen-wrap:-webkit-full-screen{width:100%;height:100%;background:#000;}
        .player-fullscreen-wrap:fullscreen .player-wrap,.player-fullscreen-wrap:-webkit-full-screen .player-wrap{width:100%;height:100%;display:flex;align-items:center;justify-content:center;border-radius:0;}
        .player-fullscreen-wrap:fullscreen .player-wrap video,.player-fullscreen-wrap:-webkit-full-screen .player-wrap video{width:100%;height:100%;max-height:none;object-fit:contain;}
        .controls{position:absolute;left:0;right:0;bottom:0;background:linear-gradient(to top, rgba(0,0,0,.85), rgba(0,0,0,.55) 65%, transparent);padding:32px 12px 10px;opacity:0;pointer-events:none;transition:opacity .15s ease;}
        .player-wrap.show-controls .controls{opacity:1;pointer-events:auto;}
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
        .settings-wrap{position:relative;}
        .settings-panel{position:absolute;right:0;bottom:calc(100% + 8px);background:#282828;border-radius:10px;padding:6px 0;min-width:210px;box-shadow:0 4px 16px rgba(0,0,0,.5);display:flex;flex-direction:column;z-index:1;}
        .settings-panel[hidden]{display:none;}
        .settings-row{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:8px 14px;font-size:13px;color:#f1f1f1;white-space:nowrap;cursor:pointer;}
        .settings-row:hover{background:#333;}
        .settings-row select{background:#3a3a3a;color:#f1f1f1;border:1px solid #4a4a4a;border-radius:6px;font-size:12px;padding:4px 6px;}
        .settings-row.volume-row{cursor:default;}
        .settings-row.volume-row:hover{background:none;}
        .settings-row.volume-row button{background:none;border:none;color:#f1f1f1;cursor:pointer;padding:4px;display:inline-flex;align-items:center;justify-content:center;}
        .settings-row.volume-row input[type=range]{flex:1;min-width:0;width:auto;}
        .next-overlay{position:absolute;right:16px;bottom:96px;background:rgba(24,24,24,.95);border-radius:10px;padding:12px 14px;display:flex;align-items:center;gap:12px;box-shadow:0 4px 16px rgba(0,0,0,.5);}
        .next-overlay[hidden]{display:none;}
        .next-overlay span{font-size:13px;}
        .next-overlay button{background:var(--accent);color:#fff;border:none;border-radius:6px;padding:6px 10px;font-size:12px;cursor:pointer;}

        .player-wrap.remote-controlled .controls{display:none;}

        /* 遠端控制中改成劇院模式(不一定拿得到瀏覽器原生全螢幕權限,因為是輪詢回應後才觸發、
           不是使用者直接點擊觸發,不算「使用者手勢」——所以額外用純 CSS 讓播放區塊佔滿整個
           版面當作保底,真正的全螢幕 API 有成功的話畫面也不會衝突。) */
        body.remote-fullscreen header{display:none;}
        body.remote-fullscreen main.watch{grid-template-columns:minmax(0,1fr);max-width:none;padding:0;gap:0;}
        body.remote-fullscreen main.watch .sidebar{display:none;}
        body.remote-fullscreen main.watch h1,body.remote-fullscreen main.watch .primary>.meta{display:none;}
        body.remote-fullscreen .player-fullscreen-wrap{height:100vh;}
        body.remote-fullscreen .player-wrap{border-radius:0;height:100%;display:flex;align-items:center;justify-content:center;}
        body.remote-fullscreen .player-wrap video{max-height:100vh;height:100vh;}

        .waiting-page{display:flex;align-items:center;justify-content:center;height:100vh;text-align:center;padding:24px;}
        .waiting-page p{font-size:16px;color:#ccc;}

        .flatten-toggle{color:#ccc;text-decoration:none;font-size:12px;white-space:nowrap;border:1px solid #3a3a3a;border-radius:6px;padding:6px 10px;}
        .flatten-toggle:hover{background:#2a2a2a;}
        .breadcrumb{font-size:13px;color:#aaa;display:flex;gap:6px;flex-wrap:wrap;padding:12px 20px 0;}
        .breadcrumb a{color:#ccc;text-decoration:none;}
        .breadcrumb a:hover{text-decoration:underline;}
        .folder-thumb{color:#f5c451;display:flex;align-items:center;justify-content:center;}
        .empty-folder{padding:40px 20px;color:#888;text-align:center;}
        """;

    /// <summary>遠端控制模式已開啟、但主機還沒選任何影片時顯示——不能讓觀眾自己從首頁清單挑,
    /// 只能等主機選好,這裡定期輪詢 <c>/control/state</c>,一有影片就自動跳轉過去。</summary>
    private string BuildRemoteWaitingPageHtml()
    {
        var title = HtmlEncode(_serverName);
        return $$"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>{{SharedCss}}</style></head>
            <body>
              <main class="waiting-page"><p>遙控模式已開啟,等待主機選擇影片…</p></main>
              <script>
                setInterval(function () {
                  fetch('/control/state').then(function (r) { return r.json(); }).then(function (state) {
                    if (!state || !state.Enabled) { location.href = '/'; return; }
                    if (state.VideoRelativePath) { location.href = '/watch?path=' + encodeURIComponent(state.VideoRelativePath); }
                  }).catch(function () {});
                }, 800);
              </script>
            </body>
            </html>
            """;
    }

    /// <summary>folder/flat 兩個查詢參數共同決定首頁怎麼列影片:預設(flat=false)照實際資料夾結構
    /// 逐層瀏覽,folder 是目前瀏覽到的相對路徑(空字串代表根目錄);flat=true 則無視資料夾,
    /// 把整個 manifest 攤平成單一清單(等同這個功能加入前的行為),供使用者在網頁上自行切換。</summary>
    private string BuildHomePageHtml(string sort, string folder, bool flat)
    {
        var extraQuery = BuildExtraQuery(folder, flat, shuffle: false, seed: 0);
        string cardsHtml;
        List<int> displayedVideoIndices;

        if (flat)
        {
            displayedVideoIndices = GetDisplayOrder(sort);
            cardsHtml = BuildVideoCardsHtml(displayedVideoIndices, sort, extraQuery);
        }
        else
        {
            var (subfolders, videoIndices) = GetFolderContents(folder);
            var videoIndexSet = videoIndices.ToHashSet();
            displayedVideoIndices = GetDisplayOrder(sort).Where(videoIndexSet.Contains).ToList();

            var folderCards = string.Join('\n', subfolders.Select(name =>
            {
                var childPath = folder.Length == 0 ? name : $"{folder}/{name}";
                var count = CountVideosUnder(childPath);
                return $"""
                    <a class="card folder-card" href="/?folder={Uri.EscapeDataString(childPath)}&sort={sort}">
                      <div class="thumb folder-thumb">{Svg(Icons.Folder, 40)}</div>
                      <div class="title">{HtmlEncode(name)}</div>
                      <div class="meta">{count} 部影片</div>
                    </a>
                    """;
            }));

            cardsHtml = subfolders.Count == 0 && videoIndices.Count == 0
                ? """<p class="empty-folder">這個資料夾是空的。</p>"""
                : folderCards + "\n" + BuildVideoCardsHtml(displayedVideoIndices, sort, extraQuery);
        }

        var title = HtmlEncode(_serverName);
        var breadcrumb = flat ? string.Empty : BuildBreadcrumbHtml(folder, sort);
        var flattenToggle = flat
            ? $"""<a class="flatten-toggle" href="/?sort={sort}">依資料夾顯示</a>"""
            : $"""<a class="flatten-toggle" href="/?flat=1&sort={sort}">顯示成單一清單</a>""";
        var shuffleButtonHtml = BuildShuffleStartButtonHtml(displayedVideoIndices, sort, extraQuery);

        return $"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title}</title>
            <style>{SharedCss}</style></head>
            <body>
              <header>
                <h1>{title}</h1>
                {BuildSortSelectHtml(sort, BuildHomeSortPrefix(folder, flat))}
                {shuffleButtonHtml}
                {flattenToggle}
              </header>
              {breadcrumb}
              <main class="grid">
                {cardsHtml}
              </main>
              <script>{BackToHomeTrapScript}</script>
            </body>
            </html>
            """;
    }

    /// <summary>首頁標頭的「隨機播放」按鈕:目前這個畫面(依 flat/folder 篩選過)沒有任何影片時不顯示。
    /// 點擊時用內嵌 JS 從 <paramref name="videoIndices"/> 隨機挑一個,帶著新產生的 seed 導到觀看頁,
    /// 從此進入「隨機播放」狀態——後續上一部/下一部/側欄/自動播放下一部都會沿用同一組 seed
    /// (見 <see cref="BuildExtraQuery"/> 如何把 shuffle/seed 一路帶進觀看頁產生的每個連結)。</summary>
    private string BuildShuffleStartButtonHtml(IReadOnlyList<int> videoIndices, string sort, string extraQuery)
    {
        if (videoIndices.Count == 0)
        {
            return string.Empty;
        }

        // 用單引號包 onclick 屬性(而不是跟其他屬性一樣用雙引號),因為值裡的 JSON 字串
        // (sortJson/extraQueryJson)本身含雙引號——用單引號包屬性就不用另外處理跳脫。
        var indicesJson = JsonSerializer.Serialize(videoIndices);
        var sortJson = JsonSerializer.Serialize(sort);
        var extraQueryJson = JsonSerializer.Serialize(extraQuery);
        return $$"""
            <a class="flatten-toggle" href="#" onclick='var indices={{indicesJson}};var pick=indices[Math.floor(Math.random()*indices.length)];var seed=Math.floor(Math.random()*2147483647);location.href="/watch?v="+pick+"&sort="+{{sortJson}}+"&shuffle=1&seed="+seed+{{extraQueryJson}};return false;'>隨機播放</a>
            """;
    }

    private string BuildVideoCardsHtml(IEnumerable<int> indices, string sort, string extraQuery) => string.Join('\n', indices.Select(index =>
    {
        var entry = Manifest[index];
        return $"""
            <a class="card" href="/watch?v={index}&sort={sort}{extraQuery}">
              <div class="thumb">
                <div class="thumb-fallback">{Svg(Icons.PlayCircle, 40)}</div>
                <img src="/thumbnail/{EscapeRelativePathForUrl(entry.RelativePath)}" alt="" loading="lazy" onerror="this.style.display='none'">
              </div>
              <div class="title">{HtmlEncode(entry.Name)}</div>
              <div class="meta">{FormatFileSize(entry.Size)}</div>
            </a>
            """;
    }));

    /// <summary>依相對路徑字首比對 <see cref="Manifest"/>,回傳 <paramref name="folder"/> 底下「直屬」的
    /// 子資料夾名稱(不含更深層的孫層)以及直接放在這一層的影片索引——首頁逐層瀏覽的核心邏輯。
    /// 只跟 Manifest 的字串比對,不會真的去讀檔案系統,folder 也不需要額外做路徑穿越檢查。</summary>
    private (List<string> Subfolders, List<int> VideoIndices) GetFolderContents(string folder)
    {
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var subfolders = new List<string>();
        var videoIndices = new List<int>();

        for (var i = 0; i < Manifest.Count; i++)
        {
            var relativePath = Manifest[i].RelativePath;
            if (prefix.Length > 0 && !relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = relativePath[prefix.Length..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
            {
                videoIndices.Add(i);
            }
            else
            {
                var name = remainder[..slashIndex];
                if (!subfolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    subfolders.Add(name);
                }
            }
        }

        subfolders.Sort(StringComparer.OrdinalIgnoreCase);
        return (subfolders, videoIndices);
    }

    private int CountVideosUnder(string folderPath)
    {
        var prefix = folderPath + "/";
        return Manifest.Count(e => e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeFolder(string? folder) => string.IsNullOrEmpty(folder) ? string.Empty : folder.Trim('/');

    /// <summary>接在 "&sort={sort}" 後面的額外查詢字串——flat=true 帶 "&flat=1",folder 模式底下
    /// 非根目錄則帶 "&folder=...",shuffle 開啟則另外帶 "&shuffle=1&seed=...",讓 watch 頁的
    /// 返回/上一部/下一部/側欄連結都能保留目前瀏覽的情境(含是否處於隨機播放狀態)。</summary>
    private static string BuildExtraQuery(string folder, bool flat, bool shuffle, int seed)
    {
        var query = flat ? "&flat=1" : (folder.Length == 0 ? string.Empty : $"&folder={Uri.EscapeDataString(folder)}");
        return shuffle ? $"{query}&shuffle=1&seed={seed}" : query;
    }

    private static string BuildHomeSortPrefix(string folder, bool flat)
    {
        if (flat)
        {
            return "/?flat=1&sort=";
        }

        return folder.Length == 0 ? "/?sort=" : $"/?folder={Uri.EscapeDataString(folder)}&sort=";
    }

    private static string BuildHomeHref(string sort, string folder, bool flat) => BuildHomeSortPrefix(folder, flat) + sort;

    private static string BuildBreadcrumbHtml(string folder, string sort)
    {
        if (folder.Length == 0)
        {
            return string.Empty;
        }

        var segments = folder.Split('/');
        var parts = new List<string> { $"""<a href="/?sort={sort}">首頁</a>""" };
        var accumulated = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            accumulated = accumulated.Length == 0 ? segments[i] : $"{accumulated}/{segments[i]}";
            parts.Add(i == segments.Length - 1
                ? HtmlEncode(segments[i])
                : $"""<a href="/?folder={Uri.EscapeDataString(accumulated)}&sort={sort}">{HtmlEncode(segments[i])}</a>""");
        }

        return $"""<nav class="breadcrumb">{string.Join(" / ", parts)}</nav>""";
    }

    private string BuildWatchPageHtml(int index, string sort, string folder, bool flat, bool shuffle, int seed)
    {
        var entry = Manifest[index];

        // 觀看頁的上一部/下一部、右側清單預設只在「目前這個資料夾」裡走(跟首頁逐層瀏覽一致),
        // 只有攤平模式才會照全域排序橫跨所有資料夾——不然使用者會在不知情的狀況下被帶去別的
        // 資料夾。folder 模式下用的還是全域排序,只是先篩選成這個資料夾直屬的影片而已。shuffle
        // 開啟時,GetDisplayOrder 回傳的就已經是洗牌過的順序,不用再另外處理。
        var order = flat
            ? GetDisplayOrder(sort, shuffle, seed)
            : GetDisplayOrder(sort, shuffle, seed).Where(GetFolderContents(folder).VideoIndices.ToHashSet().Contains).ToList();
        var position = order.IndexOf(index);
        var hasPrev = position > 0;
        var hasNext = position >= 0 && position < order.Count - 1;
        var prevIndex = hasPrev ? order[position - 1] : (int?)null;
        var nextIndex = hasNext ? order[position + 1] : (int?)null;
        var extraQuery = BuildExtraQuery(folder, flat, shuffle, seed);

        // 隨機播放 checkbox 用:關閉時導到的網址(不帶 shuffle/seed),開啟時導到的網址前綴
        // (JS 在使用者勾選當下才補上新產生的 seed,每次打開都重新洗一次牌)。
        var baseExtraQuery = BuildExtraQuery(folder, flat, shuffle: false, seed: 0);
        var shuffleOffHref = $"/watch?v={index}&sort={sort}{baseExtraQuery}";
        var shuffleOnHrefPrefix = $"/watch?v={index}&sort={sort}{baseExtraQuery}&shuffle=1&seed=";

        var sidebar = order.Count > 1
            ? $"""
                <aside class="sidebar">
                  {BuildSortSelectHtml(sort, $"/watch?v={index}&sort=", extraQuery)}
                  {BuildSidebarItems(order, index, sort, extraQuery)}
                </aside>
                """
            : string.Empty;
        var title = HtmlEncode(entry.Name);
        var backLabel = HtmlEncode(_serverName);
        var homeHref = BuildHomeHref(sort, folder, flat);

        // 傳給前端 JS 用的中繼資料,以 JSON 安全編碼字串內容,並把 "</" 斷開避免檔名剛好含有
        // "</script>" 這種字串時提早把 <script> 區塊截斷。
        var relativePathJson = JsonSerializer.Serialize(entry.RelativePath).Replace("</", "<\\/");
        var sortJson = JsonSerializer.Serialize(sort).Replace("</", "<\\/");
        var extraQueryJson = JsonSerializer.Serialize(extraQuery).Replace("</", "<\\/");
        var prevIndexJson = prevIndex is { } p ? p.ToString() : "null";
        var nextIndexJson = nextIndex is { } n ? n.ToString() : "null";
        var controlStateJson = JsonSerializer.Serialize(ControlState.Snapshot()).Replace("</", "<\\/");
        var defaultVolumeJson = _options.DefaultVolumePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var defaultSpeedJson = _options.DefaultSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var autoplayCountdownJson = _options.AutoplayCountdownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $$"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>{{SharedCss}}</style></head>
            <body>
              <header><a class="back" href="{{homeHref}}">{{Svg(Icons.ArrowBack, 18)}} {{backLabel}}</a></header>
              <main class="watch">
                <div class="primary">
                  <div class="player-fullscreen-wrap" id="playerFullscreenWrap">
                    <div class="player-wrap" id="playerWrap">
                      <video id="player" src="/media/{{EscapeRelativePathForUrl(entry.RelativePath)}}" poster="/thumbnail/{{EscapeRelativePathForUrl(entry.RelativePath)}}"></video>
                      <div class="next-overlay" id="nextOverlay" hidden>
                        <span id="nextOverlayText"></span>
                        <button id="cancelNextBtn" type="button">取消</button>
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
                          <div class="settings-wrap" id="settingsWrap">
                            <button id="settingsBtn" type="button" title="設定">{{Svg(Icons.Settings)}}</button>
                            <div class="settings-panel" id="settingsPanel" hidden>
                              <div class="settings-row volume-row">
                                <button id="muteBtn" type="button" title="靜音">{{Svg(Icons.VolumeUp)}}</button>
                                <input type="range" id="volume" min="0" max="100" value="100">
                              </div>
                              <label class="settings-row">自動播放下一部 <input type="checkbox" id="autoplayNext"{{(_options.AutoplayNext ? " checked" : "")}}></label>
                              <label class="settings-row">隨機播放 <input type="checkbox" id="shuffleCheckbox"{{(shuffle ? " checked" : "")}} onchange="location.href=this.checked ? '{{shuffleOnHrefPrefix}}'+Math.floor(Math.random()*2147483647) : '{{shuffleOffHref}}'"></label>
                              <label class="settings-row">播放速度
                                <select id="speed" title="播放速度">
                                  {{BuildSpeedOptionsHtml()}}
                                </select>
                              </label>
                            </div>
                          </div>
                          <button id="fsBtn" type="button" title="全螢幕">{{Svg(Icons.Fullscreen)}}</button>
                        </div>
                      </div>
                    </div>
                  </div>
                  <h1>{{title}}</h1>
                  <p class="meta">{{FormatFileSize(entry.Size)}}</p>
                </div>
                {{sidebar}}
              </main>
              <script>{{BackToHomeTrapScript}}</script>
              <script>{{BuildWatchPageScript(relativePathJson, prevIndexJson, nextIndexJson, sortJson, extraQueryJson, controlStateJson, defaultVolumeJson, defaultSpeedJson, autoplayCountdownJson)}}</script>
            </body>
            </html>
            """;
    }

    /// <summary>右側影片清單:依目前選的排序方式列出全部影片(含目前播放中的那一部),每筆都附縮圖,
    /// 目前播放中的那筆用樣式標示、不能再點(不用整個重新導向到自己)。</summary>
    private string BuildSidebarItems(IReadOnlyList<int> order, int currentIndex, string sort, string extraQuery) => string.Join('\n', order.Select(i =>
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
            : $"""<a class="side-item" href="/watch?v={i}&sort={sort}{extraQuery}">{thumb}{info}</a>""";
    }));

    private static string BuildWatchPageScript(
        string relativePathJson, string prevIndexJson, string nextIndexJson, string sortJson, string extraQueryJson, string controlStateJson,
        string defaultVolumeJson, string defaultSpeedJson, string autoplayCountdownJson) => $$"""
        (function () {
          var meta = { relativePath: {{relativePathJson}}, prevIndex: {{prevIndexJson}}, nextIndex: {{nextIndexJson}}, sort: {{sortJson}}, extraQuery: {{extraQueryJson}} };
          var DEFAULT_VOLUME_PERCENT = {{defaultVolumeJson}};
          var DEFAULT_SPEED = {{defaultSpeedJson}};
          var AUTOPLAY_COUNTDOWN_SECONDS = {{autoplayCountdownJson}};
          function watchUrl(index) { return '/watch?v=' + index + '&sort=' + meta.sort + meta.extraQuery; }
          function watchUrlForPath(path) { return '/watch?path=' + encodeURIComponent(path) + '&sort=' + meta.sort + meta.extraQuery; }
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
          var fullscreenWrap = document.getElementById('playerFullscreenWrap');
          var playerWrap = document.getElementById('playerWrap');
          var controls = playerWrap.querySelector('.controls');
          var settingsWrap = document.getElementById('settingsWrap');
          var settingsBtn = document.getElementById('settingsBtn');
          var settingsPanel = document.getElementById('settingsPanel');

          var posKey = 'connectit-pos:' + meta.relativePath;
          var volKey = 'connectit-volume';
          var speedKey = 'connectit-speed';
          var seeking = false;
          var autoplayTimer = null;
          var hideControlsTimer = null;

          function showControls() {
            playerWrap.classList.add('show-controls');
            clearTimeout(hideControlsTimer);
            if (!video.paused) {
              hideControlsTimer = setTimeout(function () {
                playerWrap.classList.remove('show-controls');
              }, 2500);
            }
          }
          video.addEventListener('pause', function () { clearTimeout(hideControlsTimer); playerWrap.classList.add('show-controls'); });
          video.addEventListener('play', showControls);
          playerWrap.addEventListener('mousemove', showControls);
          playerWrap.addEventListener('click', showControls);
          controls.addEventListener('input', showControls);
          showControls();

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

          // 音量/播放速度:優先還原這個瀏覽器上次自己調過的值(整台伺服器共用,不分影片),
          // 第一次觀看(還沒有 localStorage 紀錄)則套用主機端設定的預設值。
          var savedVol = localStorage.getItem(volKey);
          video.volume = Math.min(1, Math.max(0, (savedVol !== null ? parseFloat(savedVol) : DEFAULT_VOLUME_PERCENT / 100)));
          volume.value = String(Math.round(video.volume * 100));

          var savedSpeed = localStorage.getItem(speedKey);
          video.playbackRate = savedSpeed !== null ? parseFloat(savedSpeed) : DEFAULT_SPEED;
          speed.value = String(video.playbackRate);
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

          video.addEventListener('ended', function () {
            localStorage.removeItem(posKey);
            if (meta.nextIndex === null || !autoplayNext.checked) { return; }

            var secondsLeft = AUTOPLAY_COUNTDOWN_SECONDS;
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

          settingsBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            settingsPanel.hidden = !settingsPanel.hidden;
          });
          document.addEventListener('click', function (e) {
            if (!settingsPanel.hidden && !settingsWrap.contains(e.target)) { settingsPanel.hidden = true; }
          });

          fsBtn.addEventListener('click', function () {
            if (document.fullscreenElement) { document.exitFullscreen(); } else { fullscreenWrap.requestFullscreen(); }
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

          // ===== 遠端控制模式:主機正在控制播放時,鎖住手動控制列,改成跟隨輪詢到的狀態播放 =====
          var REMOTE_POLL_MS = 800;
          var REMOTE_DRIFT_THRESHOLD_SEC = 1.5;
          var REMOTE_RESYNC_COOLDOWN_MS = 1000;
          var remoteEnabled = false;
          var lastResyncAt = 0;

          function applyRemoteState(state) {
            if (!state || !state.Enabled) {
              if (remoteEnabled) {
                remoteEnabled = false;
                playerWrap.classList.remove('remote-controlled');
                document.body.classList.remove('remote-fullscreen');
                if (document.fullscreenElement) { document.exitFullscreen().catch(function () {}); }
              }
              return;
            }

            if (!remoteEnabled) {
              remoteEnabled = true;
              playerWrap.classList.add('remote-controlled');
              document.body.classList.add('remote-fullscreen');
              // 這裡是輪詢回應後才觸發,不是使用者直接點擊觸發,不算瀏覽器要求的「使用者手勢」,
              // 原生全螢幕 API 很可能會被擋下來——失敗就靠上面的 remote-fullscreen CSS 頂著,
              // 不需要特別處理這個 rejection。
              if (!document.fullscreenElement) { fullscreenWrap.requestFullscreen().catch(function () {}); }
            }

            if (state.VideoRelativePath && state.VideoRelativePath !== meta.relativePath) {
              location.href = watchUrlForPath(state.VideoRelativePath);
              return;
            }

            if (state.IsPlaying && video.paused) { video.play().catch(function () {}); }
            else if (!state.IsPlaying && !video.paused) { video.pause(); }

            var targetSec = state.PositionMs / 1000;
            var cooldownActive = (Date.now() - lastResyncAt) < REMOTE_RESYNC_COOLDOWN_MS;
            if (!cooldownActive && Math.abs(video.currentTime - targetSec) > REMOTE_DRIFT_THRESHOLD_SEC) {
              video.currentTime = isFinite(video.duration) ? Math.min(targetSec, Math.max(0, video.duration)) : targetSec;
              lastResyncAt = Date.now();
            }

            // 遠端控制中,音量/靜音/播放速度也一併跟主機同步(控制列被鎖住了,觀眾本來就
            // 不能自己調),跟 play/pause 一樣直接套用,不用額外的漂移容忍。
            if (video.playbackRate !== state.PlaybackRate) { video.playbackRate = state.PlaybackRate; }
            if (video.muted !== state.Muted) { video.muted = state.Muted; }
            if (Math.abs(video.volume - state.Volume) > 0.001) { video.volume = state.Volume; }
          }

          applyRemoteState({{controlStateJson}});
          setInterval(function () {
            fetch('/control/state').then(function (r) { return r.json(); }).then(applyRemoteState).catch(function () {});
          }, REMOTE_POLL_MS);
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
        var payload = JsonSerializer.SerializeToUtf8Bytes(new VideoManifestResponse
        {
            Name = _serverName,
            Entries = Manifest,
            RemoteControlEnabled = ControlState.Snapshot().Enabled,
        });

        await WriteHeadersAsync(
            stream, 200, "OK",
            [("Content-Type", "application/json"), ("Content-Length", payload.Length.ToString())], token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    private async Task WriteControlStateAsync(NetworkStream stream, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(ControlState.Snapshot());

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
