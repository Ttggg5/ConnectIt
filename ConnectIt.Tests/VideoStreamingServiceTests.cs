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
    public async Task GetWatch_InvalidIndex_ReturnsNotFound()
    {
        var filePath = CreateFile("movie.mp4", 100);
        _service.Start(filePath, "server");

        using var response = await _client.GetAsync($"{BaseUrl}/watch?v=99");

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
