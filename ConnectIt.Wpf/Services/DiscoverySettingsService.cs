using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>管理裝置搜尋相關的設定(目前只有自動搜尋秒數),並把設定儲存在本機供下次啟動使用。</summary>
public sealed class DiscoverySettingsService
{
    public const int DefaultSearchDurationSeconds = 30;
    public const int MinSearchDurationSeconds = 5;
    public const int MaxSearchDurationSeconds = 300;

    private readonly string _settingsFilePath;

    public int SearchDurationSeconds { get; private set; }

    public DiscoverySettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "discovery-settings.json");
        SearchDurationSeconds = LoadSavedDuration();
    }

    /// <summary>變更自動搜尋秒數(超出合理範圍會被夾住),並儲存供下次啟動使用。</summary>
    public int SetSearchDurationSeconds(int seconds)
    {
        var clamped = Math.Clamp(seconds, MinSearchDurationSeconds, MaxSearchDurationSeconds);
        if (clamped != SearchDurationSeconds)
        {
            SearchDurationSeconds = clamped;
            SaveDuration(clamped);
        }

        return clamped;
    }

    private int LoadSavedDuration()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<DiscoverySettingsData>(json);
                if (data != null && data.SearchDurationSeconds is >= MinSearchDurationSeconds and <= MaxSearchDurationSeconds)
                {
                    return data.SearchDurationSeconds;
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return DefaultSearchDurationSeconds;
    }

    private void SaveDuration(int seconds)
    {
        try
        {
            var json = JsonSerializer.Serialize(new DiscoverySettingsData { SearchDurationSeconds = seconds });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class DiscoverySettingsData
    {
        public int SearchDurationSeconds { get; set; } = DefaultSearchDurationSeconds;
    }
}
