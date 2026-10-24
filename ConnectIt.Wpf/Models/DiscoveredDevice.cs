using System.Net;

namespace ConnectIt.Wpf.Models;

/// <summary>
/// 代表透過 mDNS/DNS-SD 在區網內找到的一台裝置。
/// </summary>
public class DiscoveredDevice
{
    /// <summary>DNS-SD 服務實體名稱,例如 "小明的手機._connectit._tcp.local"。</summary>
    public required string InstanceName { get; init; }

    /// <summary>主機名稱 (SRV target),例如 "android-xxxx.local"。</summary>
    public required string HostName { get; init; }

    /// <summary>解析出的 IP 位址。</summary>
    public required IPAddress Address { get; init; }

    /// <summary>裝置開放連線用的 TCP 連接埠。</summary>
    public required int Port { get; init; }

    /// <summary>
    /// 從 TXT 記錄讀到的原始裝置名稱(不含防撞名用的短碼後綴)。
    /// 沒有的話就退回用實體名稱的第一段顯示。
    /// </summary>
    public string? FriendlyName { get; init; }

    /// <summary>TXT 記錄的 "id":對方的穩定裝置識別碼(舊版沒有)。</summary>
    public string? DeviceId { get; init; }

    /// <summary>TXT 記錄的 "v":對方支援的協定版本。null = 還沒收到 TXT(不知道);1 = 不支援加密連線的舊版。</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>確定對方是舊版(不支援加密連線),連不上,要請對方更新。</summary>
    public bool NeedsUpdate => ProtocolVersion < ConnectIt.Wpf.Services.ConnectionService.ProtocolVersion;

    /// <summary>用來在清單中比對/去重的唯一鍵。</summary>
    public string Key => InstanceName;

    public string DisplayName => !string.IsNullOrEmpty(FriendlyName) ? FriendlyName : InstanceName.Split('.')[0];

    public override string ToString() => $"{DisplayName} ({Address}:{Port})";
}
