using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 管理「這台裝置」本身的設定:廣播用的裝置名稱,以及是否已經看過首次啟動引導。
/// 裝置名稱以前只存在主畫面的輸入框裡、每次啟動都退回電腦名稱;首次啟動引導讓使用者取名後,
/// 名稱要能保留到下次啟動,所以改由這裡儲存。
/// </summary>
public sealed class DeviceSettingsService
{
    public const int MaxDeviceNameLength = 40;

    private readonly string _settingsFilePath;

    /// <summary>使用者自訂的裝置名稱;沒有自訂時是電腦名稱。</summary>
    public string DeviceName { get; private set; }

    /// <summary>首次啟動引導是否已經完成(或被略過)。</summary>
    public bool OnboardingCompleted { get; private set; }

    public DeviceSettingsService()
        : this(Path.Combine(AppContext.BaseDirectory, "device-settings.json"))
    {
    }

    /// <summary>測試用:指定設定檔位置。</summary>
    public DeviceSettingsService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        var data = LoadSavedData();
        DeviceName = NormalizeDeviceName(data.DeviceName);
        OnboardingCompleted = data.OnboardingCompleted;
    }

    /// <summary>變更裝置名稱(去頭尾空白、空字串退回電腦名稱、過長截斷),回傳實際套用的名稱。</summary>
    public string SetDeviceName(string? name)
    {
        var normalized = NormalizeDeviceName(name);
        if (normalized != DeviceName)
        {
            DeviceName = normalized;
            Save();
        }

        return normalized;
    }

    public void SetOnboardingCompleted(bool completed)
    {
        if (completed != OnboardingCompleted)
        {
            OnboardingCompleted = completed;
            Save();
        }
    }

    public static string NormalizeDeviceName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Environment.MachineName;
        }

        return trimmed.Length > MaxDeviceNameLength ? trimmed[..MaxDeviceNameLength] : trimmed;
    }

    private DeviceSettingsData LoadSavedData()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<DeviceSettingsData>(json) ?? new DeviceSettingsData();
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return new DeviceSettingsData();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new DeviceSettingsData
            {
                DeviceName = DeviceName,
                OnboardingCompleted = OnboardingCompleted,
            });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class DeviceSettingsData
    {
        public string? DeviceName { get; set; }

        public bool OnboardingCompleted { get; set; }
    }
}
