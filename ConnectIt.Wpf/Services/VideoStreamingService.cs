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

            await WriteHtmlAsync(stream, BuildHomePageHtml(), token).ConfigureAwait(false);
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

            await WriteHtmlAsync(stream, BuildWatchPageHtml(i), token).ConfigureAwait(false);
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

        await WriteStatusOnlyAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
    }

    private static int? ParseVideoIndex(string rawPathWithQuery)
    {
        var queryIndex = rawPathWithQuery.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        foreach (var pair in rawPathWithQuery[(queryIndex + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "v" && int.TryParse(Uri.UnescapeDataString(kv[1]), out var value))
            {
                return value;
            }
        }

        return null;
    }

    // ===================== 網頁(像 YouTube 一樣的首頁清單 + 觀看頁) =====================

    private const string SharedCss = """
        :root{color-scheme:dark;}
        *{box-sizing:border-box;}
        body{margin:0;font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;background:#0f0f0f;color:#f1f1f1;}
        header{padding:16px 20px;border-bottom:1px solid #272727;}
        header h1{margin:0;font-size:18px;}
        a.back{color:#f1f1f1;text-decoration:none;font-size:16px;font-weight:600;}
        main.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;padding:20px;}
        .card{color:inherit;text-decoration:none;background:#181818;border-radius:12px;overflow:hidden;display:block;}
        .card:hover{background:#222;}
        .thumb{aspect-ratio:16/9;display:flex;align-items:center;justify-content:center;font-size:40px;background:#272727;}
        .card .title{padding:10px 12px 2px;font-weight:600;font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
        .card .meta{padding:0 12px 12px;font-size:12px;color:#aaa;}
        main.watch{max-width:960px;margin:0 auto;padding:20px;}
        main.watch video{width:100%;border-radius:12px;background:#000;}
        main.watch h1{font-size:18px;margin:16px 0 4px;}
        main.watch .meta{color:#aaa;font-size:13px;margin:0 0 16px;}
        .sidebar{border-top:1px solid #272727;padding-top:12px;display:flex;flex-direction:column;gap:8px;}
        .side-item{color:#f1f1f1;text-decoration:none;padding:8px;border-radius:8px;}
        .side-item:hover{background:#272727;}
        """;

    private string BuildHomePageHtml()
    {
        var cards = string.Join('\n', Manifest.Select((entry, index) => $"""
            <a class="card" href="/watch?v={index}">
              <div class="thumb">▶</div>
              <div class="title">{HtmlEncode(entry.Name)}</div>
              <div class="meta">{FormatFileSize(entry.Size)}</div>
            </a>
            """));

        var title = HtmlEncode(_serverName);
        return $"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title}</title>
            <style>{SharedCss}</style></head>
            <body>
              <header><h1>{title}</h1></header>
              <main class="grid">
                {cards}
              </main>
            </body>
            </html>
            """;
    }

    private string BuildWatchPageHtml(int index)
    {
        var entry = Manifest[index];
        var others = string.Join('\n', Manifest.Select((e, i) => (Entry: e, Index: i))
            .Where(x => x.Index != index)
            .Select(x => $"""<a class="side-item" href="/watch?v={x.Index}">{HtmlEncode(x.Entry.Name)}</a>"""));

        var sidebar = others.Length > 0 ? $"""<aside class="sidebar">{others}</aside>""" : string.Empty;
        var title = HtmlEncode(entry.Name);
        var backLabel = HtmlEncode(_serverName);

        return $"""
            <!doctype html>
            <html lang="zh-Hant">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title}</title>
            <style>{SharedCss}</style></head>
            <body>
              <header><a class="back" href="/">← {backLabel}</a></header>
              <main class="watch">
                <video controls autoplay src="/media/{EscapeRelativePathForUrl(entry.RelativePath)}"></video>
                <h1>{title}</h1>
                <p class="meta">{FormatFileSize(entry.Size)}</p>
                {sidebar}
              </main>
            </body>
            </html>
            """;
    }

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
