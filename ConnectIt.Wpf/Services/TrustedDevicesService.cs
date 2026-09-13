using System.IO;
using System.Linq;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 管理已標記為信任的裝置名稱(比對 <see cref="ConnectionRequestedEventArgs.RequesterName"/>)。
/// 收到這些裝置的連線請求時不再跳出確認對話框,直接自動接受。
/// </summary>
public sealed class TrustedDevicesService
{
    private readonly string _settingsFilePath;
    private readonly HashSet<string> _trustedNames;

    public IReadOnlyCollection<string> TrustedDevices => _trustedNames;

    public TrustedDevicesService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "trusted-devices.json");
        _trustedNames = new HashSet<string>(LoadSavedNames(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsTrusted(string name) => !string.IsNullOrWhiteSpace(name) && _trustedNames.Contains(name);

    public void Trust(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_trustedNames.Add(name))
        {
            return;
        }

        Save();
    }

    public void Untrust(string name)
    {
        if (!_trustedNames.Remove(name))
        {
            return;
        }

        Save();
    }

    private List<string> LoadSavedNames()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<TrustedDevicesData>(json);
                if (data?.TrustedDevices != null)
                {
                    return data.TrustedDevices;
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回空清單。
        }

        return new List<string>();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new TrustedDevicesData { TrustedDevices = _trustedNames.ToList() });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會遺失這筆信任紀錄。
        }
    }

    private sealed class TrustedDevicesData
    {
        public List<string> TrustedDevices { get; set; } = new();
    }
}
