using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>管理影片伺服器的自訂設定(預設排序方式、播放器預設行為、額外支援的副檔名),
/// 並把設定儲存在本機供下次啟動使用。這些設定只影響下一次按「開始分享」時套用的初始值,
/// 不會在伺服器執行中即時生效(跟 <see cref="VideoStreamingService.ControlState"/> 的即時遙控不同)。</summary>
public sealed class VideoServerSettingsService
{
    public const string DefaultSortValue = "name_asc";
    public const bool DefaultAutoplayNext = true;
    public const bool DefaultShuffle = false;
    public const int DefaultVolumePercent = 100;
    public const double DefaultPlaybackSpeed = 1;
    public const int DefaultAutoplayCountdownSeconds = 5;
    public const int MinAutoplayCountdownSeconds = 1;
    public const int MaxAutoplayCountdownSeconds = 30;

    // 跟 VideoStreamingService.SortOptions 的合法值保持一致——兩邊都要能各自獨立編譯/測試,
    // 所以沒有共用同一份定義,但語意上是同一組排序方式。
    public static readonly IReadOnlyList<string> ValidSortOptions =
        ["name_asc", "name_desc", "size_asc", "size_desc", "date_desc", "date_asc"];

    public static readonly IReadOnlyList<double> ValidPlaybackSpeeds = [0.5, 1, 1.25, 1.5, 2];

    private readonly string _settingsFilePath;

    public string DefaultSort { get; private set; } = DefaultSortValue;

    public bool AutoplayNext { get; private set; } = DefaultAutoplayNext;

    public bool Shuffle { get; private set; } = DefaultShuffle;

    public int DefaultVolume { get; private set; } = DefaultVolumePercent;

    public double DefaultSpeed { get; private set; } = DefaultPlaybackSpeed;

    /// <summary>自動播放下一部前的倒數秒數(觀看頁右下角的「即將播放下一部…」提示)。</summary>
    public int AutoplayCountdownSeconds { get; private set; } = DefaultAutoplayCountdownSeconds;

    /// <summary>使用者額外加入的副檔名(內建清單以外),已正規化成小寫、有前置 "."、不重複。</summary>
    public IReadOnlyList<string> ExtraExtensions { get; private set; } = [];

    public VideoServerSettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "video-server-settings.json");
        Load();
    }

    public string SetDefaultSort(string sort)
    {
        var normalized = ValidSortOptions.Contains(sort) ? sort : DefaultSortValue;
        if (normalized != DefaultSort)
        {
            DefaultSort = normalized;
            Save();
        }

        return normalized;
    }

    public void SetAutoplayNext(bool enabled)
    {
        if (enabled != AutoplayNext)
        {
            AutoplayNext = enabled;
            Save();
        }
    }

    public void SetShuffle(bool enabled)
    {
        if (enabled != Shuffle)
        {
            Shuffle = enabled;
            Save();
        }
    }

    public int SetDefaultVolume(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped != DefaultVolume)
        {
            DefaultVolume = clamped;
            Save();
        }

        return clamped;
    }

    public double SetDefaultSpeed(double speed)
    {
        var normalized = ValidPlaybackSpeeds.Contains(speed) ? speed : DefaultPlaybackSpeed;
        if (Math.Abs(normalized - DefaultSpeed) > 0.0001)
        {
            DefaultSpeed = normalized;
            Save();
        }

        return normalized;
    }

    public int SetAutoplayCountdownSeconds(int seconds)
    {
        var clamped = Math.Clamp(seconds, MinAutoplayCountdownSeconds, MaxAutoplayCountdownSeconds);
        if (clamped != AutoplayCountdownSeconds)
        {
            AutoplayCountdownSeconds = clamped;
            Save();
        }

        return clamped;
    }

    /// <summary>[rawText] 逗號/分號/空白分隔的副檔名清單(有沒有前置 "." 都可以),正規化成小寫、
    /// 有 "." 開頭、去重,並跳過已經內建支援的副檔名(見 <see cref="VideoLibraryScanner.VideoExtensions"/>)。</summary>
    public IReadOnlyList<string> SetExtraExtensions(string rawText)
    {
        var normalized = ParseExtensions(rawText);
        ExtraExtensions = normalized;
        Save();
        return normalized;
    }

    private static IReadOnlyList<string> ParseExtensions(string rawText)
    {
        var separators = new[] { ',', ';', ' ', '\t', '\r', '\n' };
        var result = new List<string>();
        foreach (var token in rawText.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = token.Trim().ToLowerInvariant();
            if (!ext.StartsWith('.'))
            {
                ext = "." + ext;
            }

            if (ext.Length <= 1 || VideoLibraryScanner.VideoExtensions.Contains(ext) || result.Contains(ext))
            {
                continue;
            }

            result.Add(ext);
        }

        return result;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<VideoServerSettingsData>(json);
                if (data != null)
                {
                    DefaultSort = ValidSortOptions.Contains(data.DefaultSort) ? data.DefaultSort : DefaultSortValue;
                    AutoplayNext = data.AutoplayNext;
                    Shuffle = data.Shuffle;
                    DefaultVolume = Math.Clamp(data.DefaultVolume, 0, 100);
                    DefaultSpeed = ValidPlaybackSpeeds.Contains(data.DefaultSpeed) ? data.DefaultSpeed : DefaultPlaybackSpeed;
                    AutoplayCountdownSeconds = Math.Clamp(
                        data.AutoplayCountdownSeconds, MinAutoplayCountdownSeconds, MaxAutoplayCountdownSeconds);
                    ExtraExtensions = ParseExtensions(string.Join(',', data.ExtraExtensions ?? []));
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new VideoServerSettingsData
            {
                DefaultSort = DefaultSort,
                AutoplayNext = AutoplayNext,
                Shuffle = Shuffle,
                DefaultVolume = DefaultVolume,
                DefaultSpeed = DefaultSpeed,
                AutoplayCountdownSeconds = AutoplayCountdownSeconds,
                ExtraExtensions = ExtraExtensions.ToList(),
            });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class VideoServerSettingsData
    {
        public string DefaultSort { get; set; } = DefaultSortValue;

        public bool AutoplayNext { get; set; } = DefaultAutoplayNext;

        public bool Shuffle { get; set; } = DefaultShuffle;

        public int DefaultVolume { get; set; } = DefaultVolumePercent;

        public double DefaultSpeed { get; set; } = DefaultPlaybackSpeed;

        public int AutoplayCountdownSeconds { get; set; } = DefaultAutoplayCountdownSeconds;

        public List<string>? ExtraExtensions { get; set; }
    }
}
