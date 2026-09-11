using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class VideoStreamingServiceTests : IDisposable
{
    private readonly VideoStreamingService _service = new();
    private readonly HttpClient _client = new();

    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ConnectItVideoTests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        _service.Dispose();
        _client.Dispose();

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 測試環境清不掉暫存目錄不影響測試結果本身。
        }
    }

    private string CreateFile(string relativePath, int sizeInBytes, int seed = 42)
    {
        var path = Path.Combine(_tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[sizeInBytes];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string BaseUrl => $"http://{_service.BoundAddresses[0]}:{_service.Port}";

    [Fact]
    public async Task Start_SingleFile_ManifestHasOneEntry()
    {
        var filePath = CreateFile("movie.mp4", 5000);

        _service.Start(filePath, "我的伺服器");

        var manifest = await _client.GetFromJsonAsync<VideoManifestResponse>($"{BaseUrl}/manifest");

        Assert.NotNull(manifest);
        Assert.Equal("我的伺服器", manifest!.Name);
        Assert.Single(manifest.Entries);
        Assert.Equal("movie.mp4", manifest.Entries[0].Name);
        Assert.Equal(5000, manifest.Entries[0].Size);
    }

    [Fact]
    public async Task Start_Folder_ManifestOnlyContainsVideoFiles()
    {
        CreateFile("a.mp4", 100);
        CreateFile("notes.txt", 50);
        CreateFile(Path.Combine("sub", "b.mkv"), 200);

        _service.Start(_tempDirectory, "資料夾伺服器");

        var manifest = await _client.GetFromJsonAsync<VideoManifestResponse>($"{BaseUrl}/manifest");

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Entries.Count);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "a.mp4");
        Assert.Contains(manifest.Entries, e => e.RelativePath == "sub/b.mkv");
    }

    [Fact]
    public async Task GetRoot_SingleFile_RedirectsToWatchPage()
    {
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        using var noRedirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await noRedirectClient.GetAsync(BaseUrl + "/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/watch?v=0", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetRoot_Folder_ReturnsHtmlGridLinkingToEachVideo()
    {
        CreateFile("a.mp4", 100);
        CreateFile(Path.Combine("sub", "b.mkv"), 200);
        _service.Start(_tempDirectory, "我的資料夾");

        using var response = await _client.GetAsync(BaseUrl + "/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("a.mp4", html);
        Assert.Contains("b.mkv", html);
        Assert.Contains("/watch?v=0", html);
        Assert.Contains("/watch?v=1", html);
    }

    [Fact]
    public async Task GetRoot_And_GetWatch_BothIncludeBackToHomePopstateTrap()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 200);
        _service.Start(_tempDirectory, "server");

        var homeHtml = await _client.GetStringAsync(BaseUrl + "/");
        var watchHtml = await _client.GetStringAsync($"{BaseUrl}/watch?v=0");

        // 兩個頁面都要有「上一頁永遠回首頁」的 popstate 攔截邏輯,不能漏掉任何一個,
        // 不然使用者從漏掉的那一頁按瀏覽器上一頁時就會照瀏覽紀錄跳走,而不是回首頁。
        Assert.Contains("addEventListener('popstate'", homeHtml);
        Assert.Contains("location.href = '/';", homeHtml);
        Assert.Contains("addEventListener('popstate'", watchHtml);
        Assert.Contains("location.href = '/';", watchHtml);
    }

    [Fact]
    public async Task GetRoot_WithSizeSort_OrdersCardsBySize()
    {
        CreateFile("a.mp4", 300);
        CreateFile("b.mkv", 100);
        CreateFile("c.mp4", 200);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync(BaseUrl + "/?sort=size_asc");

        var posB = html.IndexOf("b.mkv", StringComparison.Ordinal);
        var posC = html.IndexOf("c.mp4", StringComparison.Ordinal);
        var posA = html.IndexOf("a.mp4", StringComparison.Ordinal);
        Assert.True(posB >= 0 && posC >= 0 && posA >= 0);
        Assert.True(posB < posC, "100 bytes 的 b.mkv 應該排在 200 bytes 的 c.mp4 前面");
        Assert.True(posC < posA, "200 bytes 的 c.mp4 應該排在 300 bytes 的 a.mp4 前面");
        Assert.Contains("value=\"size_asc\" selected", html);
    }

    [Fact]
    public async Task GetRoot_WithUnknownSort_FallsBackToNameAscending()
    {
        CreateFile("b.mkv", 100);
        CreateFile("a.mp4", 300);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync(BaseUrl + "/?sort=not-a-real-option");

        var posA = html.IndexOf("a.mp4", StringComparison.Ordinal);
        var posB = html.IndexOf("b.mkv", StringComparison.Ordinal);
        Assert.True(posA >= 0 && posB >= 0 && posA < posB);
        Assert.Contains("value=\"name_asc\" selected", html);
    }

    [Fact]
    public async Task GetWatch_WithSizeSort_PrevAndNextFollowSortedOrderNotCanonicalOrder()
    {
        // 檔名(canonical)順序是 a,b,c(索引 0,1,2);依大小由小到大排序則是 b(100)→c(200)→a(300)。
        CreateFile("a.mp4", 300);
        CreateFile("b.mkv", 100);
        CreateFile("c.mp4", 200);
        _service.Start(_tempDirectory, "server");

        // 用 size_asc 排序觀看索引 2(c.mp4):排序後排在它前面的是 b(索引 1),後面是 a(索引 0)。
        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=2&sort=size_asc");

        Assert.Contains("prevIndex: 1", html);
        Assert.Contains("nextIndex: 0", html);
    }

    [Fact]
    public async Task GetWatch_ValidIndex_ReturnsHtmlWithVideoTag()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 200);
        _service.Start(_tempDirectory, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/watch?v=0");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<video", html);
        Assert.Contains("/media/a.mp4", html);
    }

    [Fact]
    public async Task GetWatch_Sidebar_ListsAllVideosWithCurrentOneMarkedAndNotLinked()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 200);
        CreateFile("c.mp4", 300);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=0");

        // 目前播放中的 a.mp4 應該也出現在清單裡(不是被排除),用 <div class="side-item current"> 標示,
        // 不能再被點成連結;其他兩部則是可以點的 <a class="side-item" href="/watch?v=...">。
        Assert.Contains("class=\"side-item current\"", html);
        Assert.Contains("now-playing", html);
        Assert.Contains("href=\"/watch?v=1&sort=name_asc\"", html);
        Assert.Contains("href=\"/watch?v=2&sort=name_asc\"", html);
        Assert.Contains("/thumbnail/a.mp4", html);
        Assert.Contains("/thumbnail/b.mkv", html);
        Assert.Contains("/thumbnail/c.mp4", html);
        Assert.DoesNotContain("href=\"/watch?v=0&sort=name_asc\"", html);
    }

    [Fact]
    public async Task GetWatch_FirstVideo_PrevButtonDisabledNextEnabled()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 100);
        CreateFile("c.mp4", 100);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=0");

        Assert.Contains("id=\"prevBtn\" type=\"button\" title=\"上一部\" disabled", html);
        Assert.DoesNotContain("id=\"nextBtn\" type=\"button\" title=\"下一部\" disabled", html);
        Assert.Contains("prevIndex: null", html);
        Assert.Contains("nextIndex: 1", html);
    }

    [Fact]
    public async Task GetWatch_MiddleVideo_BothPrevAndNextEnabled()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 100);
        CreateFile("c.mp4", 100);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=1");

        Assert.DoesNotContain("id=\"prevBtn\" type=\"button\" title=\"上一部\" disabled", html);
        Assert.DoesNotContain("id=\"nextBtn\" type=\"button\" title=\"下一部\" disabled", html);
        Assert.Contains("prevIndex: 0", html);
        Assert.Contains("nextIndex: 2", html);
    }

    [Fact]
    public async Task GetWatch_LastVideo_NextButtonDisabledPrevEnabled()
    {
        CreateFile("a.mp4", 100);
        CreateFile("b.mkv", 100);
        CreateFile("c.mp4", 100);
        _service.Start(_tempDirectory, "server");

        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=2");

        Assert.Contains("id=\"nextBtn\" type=\"button\" title=\"下一部\" disabled", html);
        Assert.DoesNotContain("id=\"prevBtn\" type=\"button\" title=\"上一部\" disabled", html);
        Assert.Contains("prevIndex: 1", html);
        Assert.Contains("nextIndex: null", html);
    }

    [Fact]
    public async Task GetWatch_SingleVideo_BothPrevAndNextDisabled()
    {
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        var html = await _client.GetStringAsync($"{BaseUrl}/watch?v=0");

        Assert.Contains("id=\"prevBtn\" type=\"button\" title=\"上一部\" disabled", html);
        Assert.Contains("id=\"nextBtn\" type=\"button\" title=\"下一部\" disabled", html);
    }

    [Fact]
    public async Task GetWatch_InvalidIndex_ReturnsNotFound()
    {
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/watch?v=99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetThumbnail_UnsupportedOrInvalidFile_ReturnsNotFoundGracefully()
    {
        // 測試用的假影片檔案沒有真的視訊內容,Windows 殼層縮圖 API 理當生不出縮圖 —— 這裡驗證的是
        // 「產生失敗時要乾淨地回 404,不能整個連線掛掉/丟例外」,而不是驗證縮圖產生的畫面本身
        // (那需要一支真的可解碼的影片檔案,不適合在單元測試裡做)。
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/thumbnail/movie.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetThumbnail_UnknownRelativePath_ReturnsNotFound()
    {
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/thumbnail/does-not-exist.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMedia_ReturnsFullContent()
    {
        var filePath = CreateFile("movie.mp4", 5000);
        var expected = File.ReadAllBytes(filePath);

        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/media/movie.mp4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetMedia_WithRangeHeader_ReturnsPartialContent()
    {
        var filePath = CreateFile("movie.mp4", 5000);
        var expected = File.ReadAllBytes(filePath);

        _service.Start(filePath, "server");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/media/movie.mp4");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 10-19/5000", response.Content.Headers.ContentRange!.ToString());
        var actual = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(expected[10..20], actual);
    }

    [Fact]
    public async Task GetMedia_UnknownRelativePath_ReturnsNotFound()
    {
        var filePath = CreateFile("movie.mp4", 100);

        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/media/does-not-exist.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMedia_MultipleConcurrentViewers_AllSucceed()
    {
        var moviePath = CreateFile("movie.mp4", 200_000, seed: 1);
        var showPath = CreateFile("show.mkv", 200_000, seed: 2);

        _service.Start(_tempDirectory, "server");

        var expectedMovie = File.ReadAllBytes(moviePath);
        var expectedShow = File.ReadAllBytes(showPath);

        var tasks = Enumerable.Range(0, 6).Select(async i =>
        {
            using var client = new HttpClient();
            var (path, expected) = i % 2 == 0 ? ("movie.mp4", expectedMovie) : ("show.mkv", expectedShow);
            using var response = await client.GetAsync($"{BaseUrl}/media/{path}");
            var actual = await response.Content.ReadAsByteArrayAsync();
            return (response.StatusCode, Matches: actual.SequenceEqual(expected));
        }).ToList();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result =>
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True(result.Matches);
        });
    }

    [Fact]
    public async Task Stop_ClosesServer_SubsequentRequestsFail()
    {
        var filePath = CreateFile("movie.mp4", 100);

        _service.Start(filePath, "server");

        var url = $"{BaseUrl}/media/movie.mp4";
        _service.Stop();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.GetAsync(url));
    }
}
