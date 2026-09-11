using System.IO;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 把「一支影片」或「一個裝滿影片的資料夾」統一轉換成同一套 manifest(清單)概念,
/// 供 <see cref="VideoStreamingService"/> 使用:單一影片就是只有一筆項目的 manifest,
/// 資料夾就是遞迴掃描後的多筆項目,兩者共用同一套後續的 HTTP 路由邏輯。
/// </summary>
public static class VideoLibraryScanner
{
    public static readonly IReadOnlyList<string> VideoExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg",
    ];

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 掃描 <paramref name="path"/>(可以是單一檔案或資料夾),回傳 manifest 項目清單,
    /// 以及一份「RelativePath -> 完整路徑」的查找字典(伺服器實際讀檔時唯一信任的來源,
    /// 不會用請求端傳來的字串做路徑組合,天然避免路徑穿越問題)。
    /// </summary>
    public static (IReadOnlyList<VideoManifestEntry> Manifest, IReadOnlyDictionary<string, string> FilesByRelativePath) BuildManifest(string path)
    {
        var files = new FileInfo(path).Exists
            ? BuildSingleFileEntries(path)
            : BuildFolderEntries(path);

        // 依相對路徑排序,讓首頁清單順序穩定,同時也讓「上一部/下一部」照著這個順序前進有意義。
        files = files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        var manifest = new List<VideoManifestEntry>();
        var filesByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fullPath, relativePath, size, modified) in files)
        {
            manifest.Add(new VideoManifestEntry { Name = Path.GetFileName(fullPath), RelativePath = relativePath, Size = size, Modified = modified });
            filesByRelativePath[relativePath] = fullPath;
        }

        return (manifest, filesByRelativePath);
    }

    private static List<(string FullPath, string RelativePath, long Size, DateTime Modified)> BuildSingleFileEntries(string filePath)
    {
        var info = new FileInfo(filePath);
        return [(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc)];
    }

    private static List<(string FullPath, string RelativePath, long Size, DateTime Modified)> BuildFolderEntries(string folderPath)
    {
        var root = new DirectoryInfo(folderPath).FullName;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsVideoFile)
            .Select(fullPath =>
            {
                var info = new FileInfo(fullPath);
                return (
                    FullPath: fullPath,
                    RelativePath: Path.GetRelativePath(root, fullPath).Replace('\\', '/'),
                    Size: info.Length,
                    Modified: info.LastWriteTimeUtc);
            })
            .ToList();
    }
}
