using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>管理連線相關設定(監聽連接埠、主動連線逾時秒數),並把設定儲存在本機供下次啟動使用。</summary>
public sealed class ConnectionSettingsService
{
    public const int DefaultConnectTimeoutSeconds = 8;
    public const int MinConnectTimeoutSeconds = 3;
    public const int MaxConnectTimeoutSeconds = 120;

    private readonly string _settingsFilePath;

    /// <summary>監聽用的 TCP 連接埠,0 代表由系統自動指派。變更後需要重新啟動 App 才會生效
    /// (監聽連接埠只會在啟動時綁定一次)。</summary>
    public int PreferredPort { get; private set; }

    public int ConnectTimeoutSeconds { get; private set; }

    public ConnectionSettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "connection-settings.json");
        var data = LoadSavedData();
        PreferredPort = data.PreferredPort;
        ConnectTimeoutSeconds = data.ConnectTimeoutSeconds;
    }

    /// <summary>[port] 0 代表交由系統自動指派;其餘會被夾在 1024-65535 之間(避免需要系統管理員權限的特權連接埠)。</summary>
    public int SetPreferredPort(int port)
    {
        var normalized = port == 0 ? 0 : Math.Clamp(port, 1024, 65535);
        if (normalized != PreferredPort)
        {
            PreferredPort = normalized;
            Save();
        }

        return normalized;
    }

    /// <summary>變更主動連線的逾時秒數(超出合理範圍會被夾住),並儲存供下次啟動使用。</summary>
    public int SetConnectTimeoutSeconds(int seconds)
    {
        var clamped = Math.Clamp(seconds, MinConnectTimeoutSeconds, MaxConnectTimeoutSeconds);
        if (clamped != ConnectTimeoutSeconds)
        {
            ConnectTimeoutSeconds = clamped;
            Save();
        }

        return clamped;
    }

    private ConnectionSettingsData LoadSavedData()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<ConnectionSettingsData>(json);
                if (data != null)
                {
                    return new ConnectionSettingsData
                    {
                        PreferredPort = data.PreferredPort == 0 ? 0 : Math.Clamp(data.PreferredPort, 1024, 65535),
                        ConnectTimeoutSeconds = Math.Clamp(data.ConnectTimeoutSeconds, MinConnectTimeoutSeconds, MaxConnectTimeoutSeconds),
                    };
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return new ConnectionSettingsData { PreferredPort = 0, ConnectTimeoutSeconds = DefaultConnectTimeoutSeconds };
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new ConnectionSettingsData
            {
                PreferredPort = PreferredPort,
                ConnectTimeoutSeconds = ConnectTimeoutSeconds,
            });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class ConnectionSettingsData
    {
        public int PreferredPort { get; set; }

        public int ConnectTimeoutSeconds { get; set; } = DefaultConnectTimeoutSeconds;
    }
}
