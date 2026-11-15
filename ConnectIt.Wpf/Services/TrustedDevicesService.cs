using System.IO;
using System.Linq;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 信任清單裡的一台裝置。<see cref="Name"/> 只是顯示用,比對一律用 <see cref="Fingerprint"/>。
/// <see cref="AllowUnattended"/>:這台裝置要看螢幕/操控時免確認(還要總開關 <see cref="TrustedDevicesService.UnattendedEnabled"/> 有開)。
/// </summary>
public sealed record TrustedDevice(string Fingerprint, string Name, string? DeviceId, bool AllowUnattended = false);

/// <summary>
/// 管理已標記為信任的裝置。收到這些裝置的連線請求時不再跳出確認對話框,直接自動接受。
/// 比對的是對方 TLS 憑證的指紋(見 <see cref="DeviceIdentity"/>),不是名稱——名稱任何人都能取成一樣的。
/// 舊版只存名稱(trusted-devices.json),沒辦法換算成指紋,所以改用新的檔案,升級後需要重新配對一次。
///
/// 無人值守存取(免確認的觀看螢幕與操控)是更進一步的授權:總開關 <see cref="UnattendedEnabled"/> 要開,
/// 而且那台裝置本身也要勾 <see cref="TrustedDevice.AllowUnattended"/>,兩者都只能在這台電腦上設定。
/// </summary>
public sealed class TrustedDevicesService
{
    private readonly string _settingsFilePath;
    private readonly List<TrustedDevice> _devices;

    public IReadOnlyList<TrustedDevice> TrustedDevices => _devices;

    /// <summary>無人值守存取的總開關。關掉時所有裝置都恢復成每次都要確認(個別裝置的勾選會保留)。</summary>
    public bool UnattendedEnabled { get; private set; }

    /// <summary>信任清單或無人值守設定有變動(在呼叫端的執行緒觸發)。</summary>
    public event EventHandler? Changed;

    public TrustedDevicesService()
        : this(Path.Combine(AppContext.BaseDirectory, "trusted-devices-v2.json"))
    {
    }

    /// <summary>測試用:指定設定檔位置。</summary>
    public TrustedDevicesService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        (_devices, UnattendedEnabled) = LoadSaved();
    }

    public bool IsTrusted(string fingerprint) => Find(fingerprint) != null;

    /// <summary>這台裝置要看螢幕/操控時能不能免確認:總開關有開、是信任裝置、而且個別允許了。</summary>
    public bool IsUnattendedAllowed(string fingerprint) => UnattendedEnabled && Find(fingerprint) is { AllowUnattended: true };

    /// <summary>
    /// 加入(或更新)一台信任裝置;同一台裝置(相同指紋或相同 DeviceId)只留最新的一筆。
    /// 指紋相同時沿用原本的無人值守設定;指紋換了(換了金鑰)就要重新允許,不能沿用。
    /// </summary>
    public void Trust(string fingerprint, string name, string? deviceId)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        var allowUnattended = Find(fingerprint)?.AllowUnattended ?? false;
        _devices.RemoveAll(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            || (deviceId != null && d.DeviceId == deviceId));
        _devices.Add(new TrustedDevice(fingerprint, name, deviceId, allowUnattended));
        SaveAndNotify();
    }

    public void Untrust(string fingerprint)
    {
        if (_devices.RemoveAll(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveAndNotify();
        }
    }

    public void SetUnattendedEnabled(bool enabled)
    {
        if (UnattendedEnabled == enabled)
        {
            return;
        }

        UnattendedEnabled = enabled;
        SaveAndNotify();
    }

    public void SetAllowUnattended(string fingerprint, bool allow)
    {
        var index = _devices.FindIndex(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || _devices[index].AllowUnattended == allow)
        {
            return;
        }

        _devices[index] = _devices[index] with { AllowUnattended = allow };
        SaveAndNotify();
    }

    private TrustedDevice? Find(string fingerprint) =>
        string.IsNullOrEmpty(fingerprint)
            ? null
            : _devices.FirstOrDefault(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    private (List<TrustedDevice> Devices, bool UnattendedEnabled) LoadSaved()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<TrustedDevicesData>(json);
                if (data?.Devices != null)
                {
                    return (data.Devices.Where(d => !string.IsNullOrEmpty(d.Fingerprint)).ToList(), data.UnattendedEnabled);
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回空清單(無人值守也一併關閉)。
        }

        return ([], false);
    }

    private void SaveAndNotify()
    {
        try
        {
            var json = JsonSerializer.Serialize(new TrustedDevicesData { Devices = _devices.ToList(), UnattendedEnabled = UnattendedEnabled });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會遺失這筆紀錄。
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TrustedDevicesData
    {
        public List<TrustedDevice> Devices { get; set; } = [];

        public bool UnattendedEnabled { get; set; }
    }
}
