using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectIt.Wpf.Models;
using Makaretu.Dns;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 使用標準 mDNS/DNS-SD (RFC 6762/6763) 在區網廣播/搜尋裝置。
/// 因為是標準協定,Android 端用 NsdManager 註冊相同的 service type
/// (例如 "_connectit._tcp.") 也能被這裡搜尋到,反之亦然。
/// </summary>
public sealed class MdnsDiscoveryService : IDisposable
{
    public const string ServiceType = "_connectit._tcp";

    // 完整實體名稱的結尾樣式,例如 "手機A._connectit._tcp.local" 結尾是 "._connectit._tcp.local"。
    // mDNS 是廣播式協定,同一網段上其他 App(Chromecast、印表機、Visual Studio 配對等)
    // 的服務公告也會被我們收到,必須用這個結尾比對過濾掉,只留下真的有開啟本 App 的裝置。
    private const string ServiceTypeSuffix = "." + ServiceType + ".local";

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _selfProfile;

    // 用「自己廣播出去的 IP + Port」而不是單純比對名稱字串來判斷是不是自己,
    // 這樣就算 DomainName 比對有大小寫/句點結尾等差異也不會漏判。
    private readonly HashSet<IPAddress> _selfAddresses = new();
    private int _selfPort = -1;

    // 每次啟動 App 都會產生一組固定的短碼,附加在 DNS-SD 實體名稱後面,
    // 確保「同一台電腦開兩個視窗測試」或「兩台裝置剛好同名」時,彼此的服務名稱不會撞名。
    // 撞名的話,原本用名稱字串比對「是不是自己」的邏輯會誤判,導致兩邊互相把對方當成自己而看不到彼此。
    private readonly string _instanceSuffix = Guid.NewGuid().ToString("N")[..4];

    // 等待位址解析的 host -> (instance, port, 顯示用的友善名稱)
    private readonly ConcurrentDictionary<DomainName, (DomainName Instance, int Port, string? FriendlyName)> _pendingByHost = new();

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;
    public event EventHandler<string>? DeviceRemoved;
    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _mdns != null;

    public void Start()
    {
        if (_mdns != null)
        {
            return;
        }

        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);

        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _sd.ServiceInstanceShutdown += OnServiceInstanceShutdown;
        _mdns.AnswerReceived += OnAnswerReceived;
        _mdns.NetworkInterfaceDiscovered += (_, _) => _sd.QueryServiceInstances(ServiceType);

        _mdns.Start();
        Log("mDNS 已啟動,正在搜尋裝置...");
    }

    /// <summary>主動重新送出搜尋請求(例如使用者按下重新整理)。</summary>
    public void Refresh()
    {
        if (_sd == null)
        {
            return;
        }

        _sd.QueryServiceInstances(ServiceType);
        Log("已重新搜尋裝置...");
    }

    /// <summary>把這台裝置廣播出去,讓其他裝置(含 Android)可以找到並連進來。</summary>
    public void Advertise(string deviceName, int port)
    {
        if (_mdns == null || _sd == null)
        {
            throw new InvalidOperationException("請先呼叫 Start()。");
        }

        if (_selfProfile != null)
        {
            _sd.Unadvertise(_selfProfile);
        }

        var friendlyName = MakeSafeInstanceName(deviceName);
        var instanceName = $"{friendlyName}-{_instanceSuffix}";
        var addresses = GetRoutableIPv4Addresses();
        _selfProfile = addresses.Count > 0
            ? new ServiceProfile(instanceName, ServiceType, (ushort)port, addresses)
            : new ServiceProfile(instanceName, ServiceType, (ushort)port);
        _selfProfile.AddProperty("name", friendlyName);
        _sd.Advertise(_selfProfile);

        _selfAddresses.Clear();
        foreach (var address in addresses)
        {
            _selfAddresses.Add(address);
        }
        _selfPort = port;

        Log($"已廣播本機服務「{instanceName}」,連接埠 {port},位址:{string.Join(", ", addresses)}");
    }

    /// <summary>
    /// 只挑選有預設閘道的實體網卡位址(通常是 Wi-Fi/有線網卡),
    /// 避免把 WSL、Hyper-V、VPN 等虛擬網卡的 IP 廣播出去導致 Android 連不到。
    /// </summary>
    private static List<IPAddress> GetRoutableIPv4Addresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count == 0)
            {
                continue; // 沒有預設閘道通常代表虛擬/隔離網卡
            }

            addresses.AddRange(props.UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(ua => ua.Address));
        }

        return addresses;
    }

    public void StopAdvertising()
    {
        if (_sd != null && _selfProfile != null)
        {
            _sd.Unadvertise(_selfProfile);
            Log("已停止廣播本機服務。");
        }

        _selfProfile = null;
        _selfAddresses.Clear();
        _selfPort = -1;
    }

    private static string MakeSafeInstanceName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        return trimmed.Replace('.', '-');
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        // ServiceInstanceDiscovered 其實是「網路上看到的所有服務」都會觸發,
        // 不會自動過濾服務類型,所以要自己檢查是不是本 App 的服務。
        if (!IsConnectItInstance(e.ServiceInstanceName))
        {
            return;
        }

        if (IsSelf(e.ServiceInstanceName))
        {
            return; // 略過自己
        }

        // ServiceInstanceDiscovered 只帶回實體名稱,還需要主動查 SRV/位址才能拿到 IP:Port。
        _mdns!.SendQuery(e.ServiceInstanceName, type: DnsType.SRV);
    }

    private static bool IsConnectItInstance(DomainName name) =>
        name.ToString().EndsWith(ServiceTypeSuffix, StringComparison.OrdinalIgnoreCase);

    private bool IsSelf(DomainName instanceName) =>
        _selfProfile != null && instanceName == _selfProfile.FullyQualifiedName;

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        DeviceRemoved?.Invoke(this, e.ServiceInstanceName.ToString());
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();

        foreach (var srv in records.OfType<SRVRecord>().Where(srv => IsConnectItInstance(srv.Name) && !IsSelf(srv.Name)))
        {
            var friendlyName = records.OfType<TXTRecord>()
                .Where(t => t.Name == srv.Name)
                .SelectMany(t => t.Strings)
                .Select(ParseTxtFriendlyName)
                .FirstOrDefault(n => n != null);

            var inlineAddress = records.OfType<AddressRecord>().FirstOrDefault(a => a.Name == srv.Target);
            if (inlineAddress != null)
            {
                Emit(srv.Name, srv.Target, inlineAddress.Address, srv.Port, friendlyName);
                _pendingByHost.TryRemove(srv.Target, out _);
            }
            else
            {
                _pendingByHost[srv.Target] = (srv.Name, srv.Port, friendlyName);
                _mdns!.SendQuery(srv.Target, type: DnsType.A);
            }
        }

        foreach (var addr in records.OfType<AddressRecord>())
        {
            if (_pendingByHost.TryRemove(addr.Name, out var pending))
            {
                Emit(pending.Instance, addr.Name, addr.Address, pending.Port, pending.FriendlyName);
            }
        }
    }

    private static string? ParseTxtFriendlyName(string entry)
    {
        var idx = entry.IndexOf('=');
        if (idx < 0)
        {
            return null;
        }

        return entry[..idx].Equals("name", StringComparison.OrdinalIgnoreCase) ? entry[(idx + 1)..] : null;
    }

    private void Emit(DomainName instance, DomainName host, IPAddress address, int port, string? friendlyName)
    {
        // 保險判斷:不管名稱字串比對是否成功,只要「IP + Port」跟自己廣播的一樣,
        // 就一定是自己(不可能有另一台裝置剛好用一樣的位址和連接埠)。
        if (port == _selfPort && _selfAddresses.Contains(address))
        {
            return;
        }

        var device = new DiscoveredDevice
        {
            InstanceName = instance.ToString(),
            HostName = host.ToString(),
            Address = address,
            Port = port,
            FriendlyName = friendlyName,
        };
        DeviceDiscovered?.Invoke(this, device);
    }

    private void Log(string message) => StatusChanged?.Invoke(this, message);

    public void Stop()
    {
        if (_sd != null)
        {
            if (_selfProfile != null)
            {
                _sd.Unadvertise(_selfProfile);
            }

            _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown -= OnServiceInstanceShutdown;
            _sd.Dispose();
            _sd = null;
        }

        if (_mdns != null)
        {
            _mdns.AnswerReceived -= OnAnswerReceived;
            _mdns.Stop();
            _mdns.Dispose();
            _mdns = null;
        }

        _pendingByHost.Clear();
        _selfProfile = null;
        _selfAddresses.Clear();
        _selfPort = -1;
    }

    public void Dispose() => Stop();
}
