using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 是否在收到檔案/資料夾傳輸、對方斷線時跳出系統匣氣泡通知。連線請求的確認對話框不受這個設定影響——
/// 那是唯一能提醒使用者去處理連線請求的管道,關掉的話請求會被無聲地晾在那裡。
/// </summary>
public sealed class NotificationSettingsService
{
    private readonly string _settingsFilePath;

    public bool NotificationsEnabled { get; private set; }

    public NotificationSettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "notification-settings.json");
        NotificationsEnabled = LoadSavedValue();
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        if (enabled == NotificationsEnabled)
        {
            return;
        }

        NotificationsEnabled = enabled;
        Save(enabled);
    }

    private bool LoadSavedValue()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<NotificationSettingsData>(json);
                if (data != null)
                {
                    return data.NotificationsEnabled;
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return true;
    }

    private void Save(bool enabled)
    {
        try
        {
            var json = JsonSerializer.Serialize(new NotificationSettingsData { NotificationsEnabled = enabled });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class NotificationSettingsData
    {
        public bool NotificationsEnabled { get; set; } = true;
    }
}
