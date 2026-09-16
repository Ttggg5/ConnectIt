using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

// 這個類別的程式碼依功能拆到多個 partial 檔案:MainWindow.Devices.cs(裝置探索與配對連線)、
// MainWindow.FileTransfer.cs(檔案/資料夾傳輸)、MainWindow.Settings.cs(設定頁)、
// MainWindow.VideoServer.cs(影片伺服器主機端操作)、MainWindow.RemoteControl.cs(遙控面板)、
// MainWindow.VideoViewer.cs(影片伺服器觀看端)。這個檔案只留下跨功能共用的欄位/建構子/
// 視窗生命週期與導覽切換。
public partial class MainWindow : Window
{
    private readonly MdnsDiscoveryService _discovery = new();
    private readonly ConnectionService _connection = new();
    private readonly ThemeService _themeService = new();
    private readonly FileTransferSettingsService _fileTransferSettings = new();
    private readonly DiscoverySettingsService _discoverySettings = new();
    private readonly TrustedDevicesService _trustedDevices = new();
    private readonly ConnectionSettingsService _connectionSettings = new();
    private readonly StartupService _startupService = new();
    private readonly ObservableCollection<DiscoveredDevice> _devices = new();

    // 影片伺服器功能是完全獨立於裝置配對連線的:自己一組 mDNS 服務類型(廣播/搜尋「誰開了影片伺服器」)、
    // 自己一組 HTTP 伺服器,不會用到 _connection。觀看端直接用內嵌瀏覽器(WebView2)開啟對方伺服器
    // 提供的網頁(首頁清單 + 觀看頁,見 VideoStreamingService),不需要 App 自己實作播放器/清單畫面。
    private readonly MdnsDiscoveryService _videoDiscovery = new("_connectit-video._tcp");
    private readonly VideoStreamingService _videoStreaming = new();
    private readonly VideoServerSettingsService _videoServerSettings = new();
    private readonly ObservableCollection<DiscoveredDevice> _videoServers = new();

    public MainWindow()
    {
        InitializeComponent();

        DevicesItemsControl.ItemsSource = _devices;
        _devices.CollectionChanged += (_, _) => UpdateEmptyState();
        DeviceNameTextBox.Text = Environment.MachineName;
        MainSnackbar.MessageQueue = new SnackbarMessageQueue(TimeSpan.FromSeconds(3));

        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _discovery.DeviceRemoved += OnDeviceRemoved;
        _discovery.StatusChanged += OnServiceStatusChanged;
        _connection.StatusChanged += OnServiceStatusChanged;
        _connection.ConnectionRequested += OnConnectionRequested;
        _connection.Connected += OnConnected;
        _connection.RemoteDisconnected += OnRemoteDisconnected;
        _connection.FileOffered += OnFileOffered;
        _connection.FolderOffered += OnFolderOffered;
        _connection.FileTransferProgress += OnFileTransferProgress;
        _connection.FileTransferEnded += OnFileTransferEnded;
        _connection.FolderTransferEnded += OnFolderTransferEnded;

        VideoServersItemsControl.ItemsSource = _videoServers;
        _videoServers.CollectionChanged += (_, _) => UpdateVideoServersEmptyState();
        _videoDiscovery.DeviceDiscovered += OnVideoServerDiscovered;
        _videoDiscovery.DeviceRemoved += OnVideoServerRemoved;
        _videoDiscovery.StatusChanged += OnServiceStatusChanged;
        _videoStreaming.StatusChanged += OnServiceStatusChanged;
        _remoteControlPollTimer.Tick += (_, _) => RefreshRemoteControlUi();
        _remoteControlPollTimer.Start();

        _themeService.Initialize(this);
        SetThemeRadioButtonForMode(_themeService.CurrentMode);

        DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;
        SearchDurationTextBox.Text = _discoverySettings.SearchDurationSeconds.ToString();

        NotificationsEnabledCheckBox.IsChecked = ((App)Application.Current).NotificationSettings.NotificationsEnabled;
        AutoStartCheckBox.IsChecked = _startupService.IsEnabled;
        PreferredPortTextBox.Text = _connectionSettings.PreferredPort == 0 ? string.Empty : _connectionSettings.PreferredPort.ToString();
        ConnectTimeoutTextBox.Text = _connectionSettings.ConnectTimeoutSeconds.ToString();
        RefreshTrustedDevicesList();

        SelectComboBoxItemByTag(VideoDefaultSortComboBox, _videoServerSettings.DefaultSort);
        VideoAutoplayNextCheckBox.IsChecked = _videoServerSettings.AutoplayNext;
        VideoAutoplayCountdownTextBox.Text = _videoServerSettings.AutoplayCountdownSeconds.ToString();
        VideoDefaultVolumeSlider.Value = _videoServerSettings.DefaultVolume;
        VideoDefaultVolumeText.Text = $"{_videoServerSettings.DefaultVolume}%";
        SelectComboBoxItemByTag(VideoDefaultSpeedComboBox, _videoServerSettings.DefaultSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        VideoExtraExtensionsTextBox.Text = string.Join(", ", _videoServerSettings.ExtraExtensions);

        NavListBox.SelectedIndex = 0;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _remoteControlPollTimer.Stop();
            _searchCts?.Cancel();
            _discovery.Dispose();
            _connection.Dispose();
            _themeService.Dispose();

            VideoWebView.Dispose();
            _videoStreaming.Dispose();
            _videoDiscovery.Dispose();
        };
    }

    private bool _hasShownBackgroundNotification;

    /// <summary>按下視窗的關閉鈕時,改成隱藏到系統匣而不是真正結束程式,讓裝置探索/連線繼續在背景運作。
    /// 只有透過系統匣選單按「結束」(<see cref="App.IsExiting"/> 為 true)才會真的關閉視窗。</summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (((App)Application.Current).IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_hasShownBackgroundNotification)
        {
            _hasShownBackgroundNotification = true;
            ((App)Application.Current).ShowBackgroundNotification();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 一開機就自動廣播、自動開始監聽,不需要使用者手動按按鈕。
        _discovery.Start();
        _connection.ConnectTimeout = TimeSpan.FromSeconds(_connectionSettings.ConnectTimeoutSeconds);
        _connection.StartListening(_connectionSettings.PreferredPort);
        AdvertiseCurrentName();

        StartSearchCycle();

        // 影片伺服器的「搜尋其他人開的影片伺服器」也是一開機就開始,不需要等使用者切到「影片」頁籤。
        _videoDiscovery.Start();
    }

    private void Keyboard_ClearFocus() => FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);

    private enum AppPage
    {
        Devices,
        Video,
        Settings,
        VideoWebsite,
    }

    private void NavColorZone_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var colorZone = (FrameworkElement)sender;
        colorZone.Clip = new RectangleGeometry(new Rect(0, 0, colorZone.ActualWidth, colorZone.ActualHeight), 10, 10);
    }

    private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetActivePage(NavListBox.SelectedIndex switch
        {
            1 => AppPage.Video,
            2 => AppPage.Settings,
            _ => AppPage.Devices,
        });
    }

    /// <summary>切換導覽列選到的頁面。「裝置」頁會依目前是否已連線,顯示搜尋畫面或已連線畫面。
    /// 「影片網站」頁不對應任何導覽列項目,是從「影片」頁點「觀看」另外疊上去的。</summary>
    private void SetActivePage(AppPage page)
    {
        if (page != AppPage.VideoWebsite && VideoWebView.CoreWebView2 != null)
        {
            // 離開網站頁時導到空白頁,順便停止裡面正在播放的影片。
            VideoWebView.CoreWebView2.Navigate("about:blank");
            RestoreFromWebViewFullScreen();
        }

        DiscoveryViewRoot.Visibility = Visibility.Collapsed;
        ConnectedViewRoot.Visibility = Visibility.Collapsed;
        VideoViewRoot.Visibility = Visibility.Collapsed;
        SettingsViewRoot.Visibility = Visibility.Collapsed;
        VideoWebViewRoot.Visibility = Visibility.Collapsed;

        switch (page)
        {
            case AppPage.Settings:
                SettingsViewRoot.Visibility = Visibility.Visible;
                _settingsCardColumnCount = -1;
                Dispatcher.InvokeAsync(ArrangeSettingsCards, DispatcherPriority.Loaded);
                break;
            case AppPage.Video:
                VideoViewRoot.Visibility = Visibility.Visible;
                break;
            case AppPage.VideoWebsite:
                VideoWebViewRoot.Visibility = Visibility.Visible;
                break;
            default:
                var isConnected = _connection.IsConnected;
                ConnectedViewRoot.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
                DiscoveryViewRoot.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
                break;
        }
    }

    private void OnServiceStatusChanged(object? sender, string message)
    {
        Dispatcher.Invoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }
}
