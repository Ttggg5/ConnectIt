using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 一筆可以續傳的紀錄(接收端):連線中斷時保留下來的部分檔案(<see cref="PartialPath"/>),
/// 或是一個還沒收完的資料夾(<see cref="IsFolder"/>,<see cref="FinalPath"/> 是資料夾根目錄)。
/// 以「哪台裝置(<see cref="PeerDeviceId"/>)送來的哪個項目(<see cref="ResumeKey"/>)」比對。
/// </summary>
public sealed record ResumeEntry(string PeerDeviceId, string ResumeKey, string PartialPath, string FinalPath, bool IsFolder, DateTime SavedAtUtc);

/// <summary>
/// 傳輸中斷後續傳的接收端紀錄,存成 json(程式資料夾裡的 resume-state.json)。
/// 傳送端在 file-offer/folder-offer 帶上 ResumeKey(見 <see cref="ResumeKeys"/>);同一台裝置重新連線後送來相同
/// ResumeKey 的提議,接收端就接著寫進保留的部分檔案,file-accept 帶上 Offset 告訴傳送端從哪裡開始送。
/// 超過 <see cref="MaxAge"/> 沒有續傳的紀錄會被清掉(連同部分檔案)。
/// </summary>
public sealed class ResumeStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<ResumeEntry> _entries;

    public ResumeStore()
        : this(Path.Combine(AppContext.BaseDirectory, "resume-state.json"))
    {
    }

    /// <summary>測試用:指定紀錄檔位置。</summary>
    public ResumeStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load();
    }

    public IReadOnlyList<ResumeEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public ResumeEntry? Find(string? peerDeviceId, string? resumeKey)
    {
        if (peerDeviceId == null || resumeKey == null)
        {
            return null;
        }

        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.PeerDeviceId == peerDeviceId && e.ResumeKey == resumeKey
                && DateTime.UtcNow - e.SavedAtUtc <= MaxAge);
        }
    }

    public void Save(ResumeEntry entry)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.PeerDeviceId == entry.PeerDeviceId && e.ResumeKey == entry.ResumeKey);
            _entries.Add(entry);
            Persist();
        }
    }

    public void Remove(string? peerDeviceId, string? resumeKey)
    {
        if (peerDeviceId == null || resumeKey == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.RemoveAll(e => e.PeerDeviceId == peerDeviceId && e.ResumeKey == resumeKey) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>清掉過期的紀錄,並刪除它們保留的部分檔案(資料夾本身不刪,裡面可能有已經收完的檔案)。</summary>
    public void PurgeExpired()
    {
        List<ResumeEntry> expired;
        lock (_lock)
        {
            expired = _entries.Where(e => DateTime.UtcNow - e.SavedAtUtc > MaxAge).ToList();
            if (expired.Count == 0)
            {
                return;
            }

            _entries.RemoveAll(expired.Contains);
            Persist();
        }

        foreach (var entry in expired.Where(e => !e.IsFolder))
        {
            try
            {
                File.Delete(entry.PartialPath);
            }
            catch
            {
                // 刪不掉就留著,不影響之後的傳輸。
            }
        }
    }

    private List<ResumeEntry> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                return JsonSerializer.Deserialize<List<ResumeEntry>>(File.ReadAllText(_filePath)) ?? [];
            }
        }
        catch
        {
            // 紀錄檔損毀就當作沒有可續傳的項目。
        }

        return [];
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries));
        }
        catch
        {
            // 存不起來只是重新啟動後不能續傳,不影響這次的傳輸。
        }
    }
}

/// <summary>
/// 傳送端產生 ResumeKey:同一個來源檔案(路徑、大小、修改時間都沒變)每次都算出同一個值,
/// 檔案被修改過就會不同,接收端就不會把新內容接在舊的部分檔案後面。
/// </summary>
public static class ResumeKeys
{
    public static string ForFile(FileInfo file) =>
        Hash($"file|{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");

    public static string ForFolder(DirectoryInfo folder) =>
        Hash($"folder|{folder.FullName.ToLowerInvariant()}");

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
}

/// <summary>
/// 傳送端:連線中斷時還沒送完的內容,同一台裝置重新連線後可以用 <see cref="PeerSession.ResumeSendAsync"/> 繼續。
/// 資料夾傳輸記住資料夾路徑與已經送完的相對路徑;單檔/多檔記住還沒送完的檔案路徑。
/// </summary>
public sealed record PendingSend(string DisplayName, IReadOnlyList<string> FilePaths, string? FolderPath, IReadOnlySet<string> CompletedRelativePaths);
