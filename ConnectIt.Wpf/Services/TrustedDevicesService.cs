using System.IO;
using System.Linq;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>信任清單裡的一台裝置。<see cref="Name"/> 只是顯示用,比對一律用 <see cref="Fingerprint"/>。</summary>
public sealed record TrustedDevice(string Fingerprint, string Name, string? DeviceId);

/// <summary>
/// 管理已標記為信任的裝置。收到這些裝置的連線請求時不再跳出確認對話框,直接自動接受。
/// 比對的是對方 TLS 憑證的指紋(見 <see cref="DeviceIdentity"/>),不是名稱——名稱任何人都能取成一樣的。
/// 舊版只存名稱(trusted-devices.json),沒辦法換算成指紋,所以改用新的檔案,升級後需要重新配對一次。
/// </summary>
public sealed class TrustedDevicesService
{
    private readonly string _settingsFilePath;
    private readonly List<TrustedDevice> _devices;

    public IReadOnlyList<TrustedDevice> TrustedDevices => _devices;

    public TrustedDevicesService()
        : this(Path.Combine(AppContext.BaseDirectory, "trusted-devices-v2.json"))
    {
    }

    /// <summary>測試用:指定設定檔位置。</summary>
    public TrustedDevicesService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        _devices = LoadSaved();
    }

    public bool IsTrusted(string fingerprint) =>
        !string.IsNullOrEmpty(fingerprint) && _devices.Any(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>加入(或更新)一台信任裝置;同一台裝置(相同指紋或相同 DeviceId)只留最新的一筆。</summary>
    public void Trust(string fingerprint, string name, string? deviceId)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        _devices.RemoveAll(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            || (deviceId != null && d.DeviceId == deviceId));
        _devices.Add(new TrustedDevice(fingerprint, name, deviceId));
        Save();
    }

    public void Untrust(string fingerprint)
    {
        if (_devices.RemoveAll(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    private List<TrustedDevice> LoadSaved()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<TrustedDevicesData>(json);
                if (data?.Devices != null)
                {
                    return data.Devices.Where(d => !string.IsNullOrEmpty(d.Fingerprint)).ToList();
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回空清單。
        }

        return [];
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new TrustedDevicesData { Devices = _devices.ToList() });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會遺失這筆信任紀錄。
        }
    }

    private sealed class TrustedDevicesData
    {
        public List<TrustedDevice> Devices { get; set; } = [];
    }
}
