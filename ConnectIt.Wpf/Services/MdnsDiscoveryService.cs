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
    public const string DefaultServiceType = "_connectit._tcp";

    /// <summary>這個實例廣播/搜尋的 DNS-SD 服務類型。不同用途(裝置配對、影片伺服器…)
    /// 用不同的服務類型,各自建立一個獨立的 <see cref="MdnsDiscoveryService"/> 實例,
    /// 彼此的廣播/搜尋互不干擾(這個類別本身沒有任何靜態共用狀態)。</summary>
    public string ServiceType { get; }

    // 完整實體名稱的結尾樣式,例如 "手機A._connectit._tcp.local" 結尾是 "._connectit._tcp.local"。
    // mDNS 是廣播式協定,同一網段上其他 App(Chromecast、印表機、Visual Studio 配對等)
    // 的服務公告也會被我們收到,必須用這個結尾比對過濾掉,只留下真的有開啟本 App 的裝置。
    private readonly string _serviceTypeSuffix;

    public MdnsDiscoveryService(string serviceType = DefaultServiceType)
    {
        ServiceType = serviceType;
        _serviceTypeSuffix = "." + serviceType + ".local";
    }

    // 對方正常關閉 App 時會送出 mDNS goodbye 封包(ServiceInstanceShutdown),但那是
    // fire-and-forget 的 UDP,可能遺失,對方如果是被強制關閉/當機/斷網則完全不會送出。
    // 所以額外用「多久沒再收到回應」做逾時偵測,當作保險機制。
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(6);

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _selfProfile;
    private System.Threading.Timer? _heartbeatTimer;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeenUtc = new();

    // 用「自己廣播出去的 IP + Port」而不是單純比對名稱字串來判斷是不是自己,
    // 這樣就算 DomainName 比對有大小寫/句點結尾等差異也不會漏判。
    private readonly HashSet<IPAddress> _selfAddresses = new();
    private int _selfPort = -1;

    // 這個處理序這輩子廣播過的所有完整實體名稱(不會移除)。StopAdvertising() 送出 goodbye 之後
    // 會立刻把 _selfProfile/_selfPort/_selfAddresses 清空,但殘留在網路上、或殘留在
    // Makaretu.Dns.Multicast 內部 Catalog 裡的舊封包(見 Advertise() 上面的說明)還是可能在那之後
    // 才被 OnAnswerReceived 處理到——這時候單靠「目前的 _selfProfile/_selfPort」已經沒辦法判斷
    // 那是不是自己了,所以額外用這份永久清單保底:只要是自己生成過的名稱(帶有隨機短碼,不可能被
    // 別的裝置用到),就永遠當作自己過濾掉,不管現在還有沒有在廣播。
    private readonly HashSet<string> _ownInstanceNames = new(StringComparer.OrdinalIgnoreCase);

    // 附加在 DNS-SD 實體名稱後面的短碼,確保「同一台電腦開兩個視窗測試」或「兩台裝置剛好同名」時,
    // 彼此的服務名稱不會撞名(撞名的話,原本用名稱字串比對「是不是自己」的邏輯會誤判)。
    //
    // 這組短碼在每次呼叫 Advertise() 時都會重新產生(而不是整個物件存活期間固定一組),原因是
    // Makaretu.Dns.Multicast 的 ServiceDiscovery.Unadvertise() 只會移除 PTR 記錄,不會清掉先前
    // Advertise() 加進 Catalog 裡的 SRV/位址記錄(見官方 XML 文件)。如果重新廣播時沿用同一個
    // 實體名稱,舊的 SRV 記錄(帶著舊的連接埠)可能還留在 Catalog 裡,導致對方查到的還是舊連接埠
    // (影片伺服器重新啟動、連接埠換了一個新的之後,最容易踩到這個問題)。每次都換一個新名稱,
    // 就一定是全新的 DNS-SD 記錄,不會跟任何殘留的舊記錄混在一起。
    private string _instanceSuffix = Guid.NewGuid().ToString("N")[..4];

    // 上一次真正送出廣播時用的友善名稱,搭配 _selfPort/_selfAddresses 判斷這次 Advertise() 呼叫
    // 是不是名稱/連接埠/位址其實都沒變——見 Advertise() 裡的說明。
    private string? _lastAdvertisedFriendlyName;

    // 等待位址解析的 host -> (instance, port, 顯示用的友善名稱)
    private readonly ConcurrentDictionary<DomainName, (DomainName Instance, int Port, string? FriendlyName)> _pendingByHost = new();

    // 每個實體名稱最後一次看到的友善名稱(TXT 記錄的 "name" 屬性)。mDNS 的 SRV/TXT/A 記錄
    // 常常分散在不同的 UDP 封包裡送達(尤其是心跳重送查詢時,對方不一定每次都把 TXT 附在
    // 同一個回應裡),單次 OnAnswerReceived 收到的封包不保證同時帶有 TXT——如果就這樣直接
    // 把「這次沒附 TXT」當成「這台裝置沒有友善名稱」,DisplayName 就會在原始的實體名稱
    // (含隨機短碼,例如 "SM-F9710-f597")跟正確的友善名稱("SM-F9710")之間忽隱忽現地跳動。
    // 用這份快取記住學到的友善名稱,之後即使某次回應剛好沒附 TXT,也優先沿用先前學到的值。
    private readonly ConcurrentDictionary<string, string> _knownFriendlyNames = new(StringComparer.OrdinalIgnoreCase);

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
        _heartbeatTimer = new System.Threading.Timer(_ => OnHeartbeatTick(), null, HeartbeatInterval, HeartbeatInterval);
        Log("mDNS 已啟動,正在搜尋裝置...");
    }

    /// <summary>
    /// 定期重送查詢(維持對方裝置的存活時間)並清掉太久沒回應的裝置,
    /// 當作對方沒送出/送丟 goodbye 封包時的保險機制。
    /// </summary>
    private void OnHeartbeatTick()
    {
        _sd?.QueryServiceInstances(ServiceType);

        var cutoff = DateTime.UtcNow - StaleTimeout;
        foreach (var (instanceName, lastSeen) in _lastSeenUtc)
        {
            if (lastSeen < cutoff && _lastSeenUtc.TryRemove(instanceName, out _))
            {
                _knownFriendlyNames.TryRemove(instanceName, out _);
                DeviceRemoved?.Invoke(this, instanceName);
            }
        }
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

        var friendlyName = MakeSafeInstanceName(deviceName);
        var addresses = GetRoutableIPv4Addresses();

        // 名稱、連接埠、位址都沒變的話,不需要真的重新廣播一次——重新廣播一定會用新的隨機短碼
        // (見 _instanceSuffix 的說明)產生全新的實體名稱,對方(尤其是 Android NsdManager)
        // 不保證會馬上處理到舊名稱的 goodbye 封包(Unadvertise 也只會移除 PTR,不會清掉 SRV/
        // 位址記錄),短時間內反而會同時看到新舊兩個實體名稱,變成同一台裝置在清單裡出現兩筆。
        // 「中斷連線後回到搜尋畫面」這種名稱/連接埠其實都沒變的情況,沿用原本的廣播即可。
        if (_selfProfile != null && friendlyName == _lastAdvertisedFriendlyName && port == _selfPort
            && addresses.Count == _selfAddresses.Count && addresses.All(_selfAddresses.Contains))
        {
            return;
        }

        if (_selfProfile != null)
        {
            _sd.Unadvertise(_selfProfile);
        }

        _instanceSuffix = Guid.NewGuid().ToString("N")[..4];
        _lastAdvertisedFriendlyName = friendlyName;
        var instanceName = $"{friendlyName}-{_instanceSuffix}";
        _selfProfile = addresses.Count > 0
            ? new ServiceProfile(instanceName, ServiceType, (ushort)port, addresses)
            : new ServiceProfile(instanceName, ServiceType, (ushort)port);
        _selfProfile.AddProperty("name", friendlyName);
        _ownInstanceNames.Add(_selfProfile.FullyQualifiedName.ToString());

        // 一定要在呼叫 Advertise() 之前就先更新好「自己」的位址/連接埠:Advertise() 會立刻送出
        // 未經請求的公告封包,而我們自己的 _mdns 也會收到這個公告(多播本來就會回送給自己),
        // 由 OnAnswerReceived -> Emit() 處理。如果 _selfPort/_selfAddresses 這時候還沒更新成
        // 新的值,Emit() 裡「IP+Port 是不是自己」的保險判斷就會誤判失敗,導致剛開啟的服務把
        // 自己也當成一台「找到的裝置」顯示出來,而且因為之後的公告都會被正確過濾掉,不會再有
        // 移除事件,這個誤判的項目就會一直卡在清單裡。
        _selfAddresses.Clear();
        foreach (var address in addresses)
        {
            _selfAddresses.Add(address);
        }
        _selfPort = port;

        _sd.Advertise(_selfProfile);

        Log($"已廣播本機服務「{instanceName}」,連接埠 {port},位址:{string.Join(", ", addresses)}");
    }

    /// <summary>
    /// 只挑選有預設閘道的實體網卡位址(通常是 Wi-Fi/有線網卡),
    /// 避免把 WSL、Hyper-V、VPN 等虛擬網卡的 IP 廣播出去導致 Android 連不到。
    /// </summary>
    /// <summary>internal 而非 private:<see cref="VideoStreamingService"/> 綁定 HTTP 伺服器時重用同一套
    /// 「只挑真正可路由的網卡位址」邏輯,不用另外複製一份。</summary>
    internal static List<IPAddress> GetRoutableIPv4Addresses()
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
        _lastAdvertisedFriendlyName = null;
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

    private bool IsConnectItInstance(DomainName name) =>
        name.ToString().EndsWith(_serviceTypeSuffix, StringComparison.OrdinalIgnoreCase);

    // 除了跟目前的 _selfProfile 比對之外,也要比對「這輩子廣播過的所有名稱」——
    // StopAdvertising() 之後 _selfProfile 會被清成 null,但殘留/延遲處理到的自己的舊封包
    // 還是要能被認得出來,不能只看「目前」有沒有在廣播。
    private bool IsSelf(DomainName instanceName) =>
        (_selfProfile != null && instanceName == _selfProfile.FullyQualifiedName)
        || _ownInstanceNames.Contains(instanceName.ToString());

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var instanceKey = e.ServiceInstanceName.ToString();
        _lastSeenUtc.TryRemove(instanceKey, out _);
        _knownFriendlyNames.TryRemove(instanceKey, out _);
        DeviceRemoved?.Invoke(this, instanceKey);
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

            var instanceKey = srv.Name.ToString();
            if (friendlyName != null)
            {
                _knownFriendlyNames[instanceKey] = friendlyName;
            }
            else
            {
                _knownFriendlyNames.TryGetValue(instanceKey, out friendlyName);
            }

            var inlineAddress = records.OfType<AddressRecord>().FirstOrDefault(a => a.Name == srv.Target && IsIPv4(a));
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

        foreach (var addr in records.OfType<AddressRecord>().Where(IsIPv4))
        {
            if (_pendingByHost.TryRemove(addr.Name, out var pending))
            {
                Emit(pending.Instance, addr.Name, addr.Address, pending.Port, pending.FriendlyName);
            }
        }
    }

    // 對方裝置(尤其是手機)常常是雙棧(IPv4 + IPv6)並在同一個 mDNS 封包裡同時附上 A 與 AAAA
    // 記錄;這裡一律只挑 IPv4——跟 GetRoutableIPv4Addresses() 只廣播 IPv4 位址的政策一致,
    // 也避免撿到不保證可連線的 IPv6 位址(例如連結本地位址缺少 scope id、或雙方 IPv6 連通性
    // 不對稱)導致 TcpClient.ConnectAsync 卡住或失敗,卻沒有任何逾時/錯誤訊息可看。
    private static bool IsIPv4(AddressRecord record) => record.Address.AddressFamily == AddressFamily.InterNetwork;

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

        _lastSeenUtc[instance.ToString()] = DateTime.UtcNow;

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
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _lastSeenUtc.Clear();

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
        _knownFriendlyNames.Clear();
        _selfProfile = null;
        _selfAddresses.Clear();
        _selfPort = -1;
        _lastAdvertisedFriendlyName = null;
    }

    public void Dispose() => Stop();
}
