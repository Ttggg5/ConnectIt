using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 自我更新:向 GitHub Releases 查詢最新版本(CI 在 master 每次建置都會發佈一個 <c>v1.0.{建置編號}</c>
/// 的 Release,附上整個 publish 資料夾壓成的 zip,見 .github/workflows/build-publish.yml),
/// 比目前執行中的版本新就下載、解壓到暫存資料夾,再交給一個獨立的 PowerShell 腳本:
/// 等這個程序結束 → 用 robocopy 把新檔案覆蓋到程式資料夾 → 重新啟動。
/// 執行中的 exe/dll 被鎖住無法直接覆蓋,所以一定要等程序結束後由外部腳本來複製。
/// <para>
/// 設定檔(*-settings.json 等)存在程式資料夾裡(<see cref="AppContext.BaseDirectory"/>),
/// 所以只「覆蓋/新增」檔案、不刪除多出來的檔案(不用 robocopy /MIR),設定才不會在更新後消失。
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryOwner = "Ttggg5";
    public const string RepositoryName = "ConnectIt";
    public const string ReleaseAssetName = "ConnectIt-windows-x64.zip";
    public static readonly string ReleasesPageUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private static readonly string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub API 沒有 User-Agent 會直接回 403。
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ConnectIt", CurrentVersion.ToString()));
    }

    /// <summary>目前執行中的版本。CI 建置時用 <c>-p:Version=1.0.{建置編號}</c> 帶入,本機建置是 1.0.0。</summary>
    public static Version CurrentVersion { get; } = NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    /// <summary>查詢 GitHub 上的最新版本;比目前版本新才回傳,否則回傳 null。網路錯誤會直接丟例外。</summary>
    public async Task<AvailableUpdate?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 還沒有發佈過任何 Release。
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
        if (release is null || !TryParseTagVersion(release.TagName, out var latestVersion) || latestVersion <= CurrentVersion)
        {
            return null;
        }

        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
        {
            return null;
        }

        return new AvailableUpdate(latestVersion, release.Body ?? string.Empty, asset.BrowserDownloadUrl, asset.Size, asset.Digest);
    }

    /// <summary>程式資料夾能不能寫入(例如被裝在 Program Files 底下就不行),不能的話只能請使用者手動下載。</summary>
    public static bool CanUpdateInPlace()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下載並解壓新版本到暫存資料夾,回傳解壓後的資料夾路徑。<paramref name="progress"/> 回報 0–1 的下載進度
    /// (伺服器沒給檔案大小時不回報)。
    /// </summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), "ConnectIt-update");
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(workDirectory);
        var zipPath = Path.Combine(workDirectory, ReleaseAssetName);

        using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? update.SizeBytes;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(zipPath);
            var buffer = new byte[81920];
            long receivedBytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                receivedBytes += read;
                if (totalBytes > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)receivedBytes / totalBytes));
                }
            }
        }

        VerifyDigest(zipPath, update.Digest);

        var extractDirectory = Path.Combine(workDirectory, "files");
        ZipFile.ExtractToDirectory(zipPath, extractDirectory);
        if (!File.Exists(Path.Combine(extractDirectory, Path.GetFileName(Environment.ProcessPath!))))
        {
            throw new InvalidDataException("下載的更新檔內容不完整(找不到主程式)。");
        }

        return extractDirectory;
    }

    /// <summary>
    /// 啟動外部更新腳本(隱藏視窗),它會等目前這個程序結束後覆蓋檔案並重新啟動 App。
    /// 呼叫端接著要自己結束程式(<see cref="App.RequestExit"/>)。
    /// </summary>
    public static void LaunchInstaller(string extractedDirectory)
    {
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Environment.ProcessPath!;
        var scriptPath = Path.Combine(Path.GetDirectoryName(extractedDirectory)!, "apply-update.ps1");
        File.WriteAllText(scriptPath, InstallerScript, System.Text.Encoding.UTF8);

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath,
                     "-ProcessId", Environment.ProcessId.ToString(),
                     "-Source", extractedDirectory,
                     "-Target", targetDirectory,
                     "-Exe", exePath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }

    // robocopy 的結束碼 0–7 都代表成功(有沒有複製到檔案、有沒有多出的檔案),8 以上才是失敗;
    // 剛結束的程序偶爾還沒完全釋放檔案鎖,所以失敗時多試幾次。不管成功與否最後都重新啟動 App,
    // 失敗的話至少還能用舊版本繼續執行。
    private const string InstallerScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Exe)
        try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 500
        for ($i = 0; $i -lt 5; $i++) {
            robocopy $Source $Target /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -lt 8) { break }
            Start-Sleep -Seconds 2
        }
        Start-Process -FilePath $Exe
        Remove-Item -LiteralPath (Split-Path -Parent $Source) -Recurse -Force -ErrorAction SilentlyContinue
        """;

    /// <summary>GitHub 對 Release 附件提供 <c>sha256:...</c> 摘要時就驗證一次,避免下載不完整或被竄改的檔案。</summary>
    private static void VerifyDigest(string filePath, string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrEmpty(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, digest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("下載的更新檔驗證失敗,請稍後再試。");
        }
    }

    /// <summary>"v1.0.42" / "1.0.42" → 1.0.42。</summary>
    public static bool TryParseTagVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    /// <summary>把沒寫到的版本欄位補成 0(Version 的 Build/Revision 沒指定時是 -1,直接比大小會出錯)。</summary>
    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    public void Dispose() => _httpClient.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

public sealed record AvailableUpdate(Version Version, string ReleaseNotes, string DownloadUrl, long SizeBytes, string? Digest);
