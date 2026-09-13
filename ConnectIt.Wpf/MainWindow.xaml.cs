using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

public partial class MainWindow : Window
{
    // 開啟 App 時自動搜尋裝置的秒數,可在設定頁調整(預設值見 DiscoverySettingsService)。
    private TimeSpan AutoSearchDuration => TimeSpan.FromSeconds(_discoverySettings.SearchDurationSeconds);

    // 搜尋期間內每隔幾秒就重送一次查詢,而不是只查一次就等到搜尋時間結束。
    // 兩台裝置如果幾乎同時啟動,單次查詢很容易在對方還沒完成廣播註冊前就送出而互相找不到對方,
    // 定期重試可以避開這種啟動時機的競爭問題。
    private static readonly TimeSpan SearchQueryInterval = TimeSpan.FromSeconds(1);

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
    private readonly ObservableCollection<DiscoveredDevice> _videoServers = new();

    private DiscoveredDevice? _activeVideoServer;

    // 網頁內(例如影片播放器)按下全螢幕時,WebView2 預設只會讓該元素填滿 WebView2 控制項本身的範圍,
    // 不會真的變成視窗全螢幕,所以旁邊的導覽列、返回列還是看得到。這裡監聽 ContainsFullScreenElementChanged,
    // 手動把導覽列/返回列收起來並讓視窗真正全螢幕,離開全螢幕時再還原。
    private bool _isWebViewFullScreen;
    private WindowState _preFullScreenWindowState;
    private WindowStyle _preFullScreenWindowStyle;
    private ResizeMode _preFullScreenResizeMode;

    private CancellationTokenSource? _searchCts;

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

        _themeService.Initialize(this);
        SetThemeRadioButtonForMode(_themeService.CurrentMode);

        DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;
        SearchDurationTextBox.Text = _discoverySettings.SearchDurationSeconds.ToString();

        NotificationsEnabledCheckBox.IsChecked = ((App)Application.Current).NotificationSettings.NotificationsEnabled;
        AutoStartCheckBox.IsChecked = _startupService.IsEnabled;
        PreferredPortTextBox.Text = _connectionSettings.PreferredPort == 0 ? string.Empty : _connectionSettings.PreferredPort.ToString();
        ConnectTimeoutTextBox.Text = _connectionSettings.ConnectTimeoutSeconds.ToString();
        RefreshTrustedDevicesList();

        NavListBox.SelectedIndex = 0;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
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

    private void AdvertiseCurrentName()
    {
        var name = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text)
            ? Environment.MachineName
            : DeviceNameTextBox.Text.Trim();

        _discovery.Advertise(name, _connection.Port);
    }

    private void DeviceNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AdvertiseCurrentName();
            Keyboard_ClearFocus();
        }
    }

    private void DeviceNameTextBox_LostFocus(object sender, RoutedEventArgs e) => AdvertiseCurrentName();

    private void Keyboard_ClearFocus() => FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);

    /// <summary>自動搜尋 30 秒後停止,並顯示手動重新整理按鈕。</summary>
    private void StartSearchCycle()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        RefreshButton.Visibility = Visibility.Collapsed;
        SearchProgressBar.Visibility = Visibility.Visible;
        SearchStatusText.Text = "正在搜尋裝置...";
        UpdateEmptyState();

        _ = RunSearchCycleAsync(cts);
    }

    private async Task RunSearchCycleAsync(CancellationTokenSource cts)
    {
        var deadline = DateTime.UtcNow + AutoSearchDuration;

        try
        {
            while (true)
            {
                _discovery.Refresh();

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < SearchQueryInterval ? remaining : SearchQueryInterval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return; // 被新的搜尋週期或斷線流程取消,不用再處理
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        SearchProgressBar.Visibility = Visibility.Collapsed;
        SearchStatusText.Text = "搜尋已停止";
        RefreshButton.Visibility = Visibility.Visible;
        UpdateEmptyState();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _devices.Clear();
        StartSearchCycle();
    }

    private enum AppPage
    {
        Devices,
        Video,
        Settings,
        VideoWebsite,
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

    private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<AppThemeMode>(tag, out var mode))
        {
            _themeService.SetMode(mode);
        }
    }

    private void SetThemeRadioButtonForMode(AppThemeMode mode)
    {
        var radioButton = mode switch
        {
            AppThemeMode.Light => ThemeLightRadioButton,
            AppThemeMode.Dark => ThemeDarkRadioButton,
            _ => ThemeAutoRadioButton,
        };
        radioButton.IsChecked = true;
    }

    private void BrowseDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇接收檔案要儲存的資料夾",
            InitialDirectory = _fileTransferSettings.DownloadFolder,
        };

        if (dialog.ShowDialog() == true && dialog.FolderName is { Length: > 0 } folder)
        {
            _fileTransferSettings.SetDownloadFolder(folder);
            DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;
        }
    }

    private void SearchDurationTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void SearchDurationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySearchDuration();
            Keyboard_ClearFocus();
        }
    }

    private void SearchDurationTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplySearchDuration();

    /// <summary>套用搜尋秒數設定,超出合理範圍會被夾住,並把夾住後的值顯示回輸入框。</summary>
    private void ApplySearchDuration()
    {
        if (!int.TryParse(SearchDurationTextBox.Text, out var seconds))
        {
            seconds = DiscoverySettingsService.DefaultSearchDurationSeconds;
        }

        SearchDurationTextBox.Text = _discoverySettings.SetSearchDurationSeconds(seconds).ToString();
    }

    private void NotificationsEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).NotificationSettings.SetNotificationsEnabled(NotificationsEnabledCheckBox.IsChecked == true);
    }

    private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _startupService.SetEnabled(AutoStartCheckBox.IsChecked == true);
    }

    private void RefreshTrustedDevicesList()
    {
        TrustedDevicesItemsControl.ItemsSource = _trustedDevices.TrustedDevices.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        TrustedDevicesEmptyText.Visibility = _trustedDevices.TrustedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UntrustDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
        {
            return;
        }

        _trustedDevices.Untrust(name);
        RefreshTrustedDevicesList();
    }

    private void PreferredPortTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void PreferredPortTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPreferredPort();
            Keyboard_ClearFocus();
        }
    }

    private void PreferredPortTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyPreferredPort();

    /// <summary>套用監聽連接埠設定,留空代表交由系統自動指派;變更只會在下次啟動 App 時生效。</summary>
    private void ApplyPreferredPort()
    {
        var port = int.TryParse(PreferredPortTextBox.Text, out var parsed) ? parsed : 0;
        var applied = _connectionSettings.SetPreferredPort(port);
        PreferredPortTextBox.Text = applied == 0 ? string.Empty : applied.ToString();
    }

    private void ConnectTimeoutTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void ConnectTimeoutTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyConnectTimeout();
            Keyboard_ClearFocus();
        }
    }

    private void ConnectTimeoutTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyConnectTimeout();

    /// <summary>套用主動連線的逾時秒數,超出合理範圍會被夾住,立即套用到目前的 ConnectionService。</summary>
    private void ApplyConnectTimeout()
    {
        if (!int.TryParse(ConnectTimeoutTextBox.Text, out var seconds))
        {
            seconds = ConnectionSettingsService.DefaultConnectTimeoutSeconds;
        }

        var applied = _connectionSettings.SetConnectTimeoutSeconds(seconds);
        ConnectTimeoutTextBox.Text = applied.ToString();
        _connection.ConnectTimeout = TimeSpan.FromSeconds(applied);
    }

    private void UpdateEmptyState()
    {
        var searching = SearchProgressBar.Visibility == Visibility.Visible;
        EmptyStateStackPanel.Visibility = !searching && _devices.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void ConnectToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredDevice device } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var myName = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text)
                ? Environment.MachineName
                : DeviceNameTextBox.Text.Trim();

            var accepted = await _connection.RequestConnectionAsync(device.Address, device.Port, myName, device.DisplayName);
            if (!accepted)
            {
                MainSnackbar.MessageQueue?.Enqueue($"{device.DisplayName} 未接受連線請求。");
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnConnectionRequested(object? sender, ConnectionRequestedEventArgs e)
    {
        // 這裡是從背景執行緒(TCP accept loop)呼叫過來的,所以要排到 UI 執行緒上處理。
        // 用 BeginInvoke(排入訊息佇列,非阻塞)而不是 Invoke(從背景執行緒同步阻塞呼叫) ——
        // 後者會讓 DialogHost 的顯示動畫/版面更新在一個「巢狀」的 Dispatcher 呼叫裡執行,
        // 曾經遇過這樣子彈窗背景會變暗但內容一直不畫出來的狀況;
        // 用 BeginInvoke 讓它變成一般排隊的 UI 操作,行為就跟一般 Click 事件叫出對話框一樣正常。
        Dispatcher.BeginInvoke(() =>
        {
            if (_trustedDevices.IsTrusted(e.RequesterName))
            {
                AppendLog($"已自動接受信任裝置「{e.RequesterName}」的連線請求。");
                e.Respond(true);
                return;
            }

            var dialogResultTask = ShowConnectionRequestDialog(e.RequesterName, e.RequesterAddress);
            _ = RespondWhenDialogClosedAsync(dialogResultTask, e, _trustedDevices);
        });
    }

    private static async Task RespondWhenDialogClosedAsync(
        Task<(bool Accepted, bool Trust)> dialogResultTask, ConnectionRequestedEventArgs e, TrustedDevicesService trustedDevices)
    {
        bool accepted;
        bool trust;
        try
        {
            (accepted, trust) = await dialogResultTask;
        }
        catch
        {
            accepted = false;
            trust = false;
        }

        if (accepted && trust)
        {
            trustedDevices.Trust(e.RequesterName);
        }

        e.Respond(accepted);
    }

    /// <summary>必須在 UI 執行緒上呼叫。同步建立並顯示確認對話框,回傳使用者按下按鈕後才會完成的 Task
    /// (是否接受、是否同時勾選了「信任此裝置」)。</summary>
    private Task<(bool Accepted, bool Trust)> ShowConnectionRequestDialog(string requesterName, IPAddress address)
    {
        var panel = new StackPanel { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new PackIcon
        {
            Kind = PackIconKind.AccountQuestion,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("MaterialDesign.Brush.Primary"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"「{requesterName}」想要與你連線",
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = address.ToString(),
            Opacity = 0.6,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var trustCheckBox = new CheckBox
        {
            Content = "信任此裝置,之後自動接受",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(trustCheckBox);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var rejectButton = new Button
        {
            Content = "拒絕",
            Style = (Style)FindResource("MaterialDesignFlatButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var acceptButton = new Button
        {
            Content = "接受",
            Style = (Style)FindResource("MaterialDesignRaisedButton"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(rejectButton, "RejectConnectionButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(acceptButton, "AcceptConnectionButton");
        buttonPanel.Children.Add(rejectButton);
        buttonPanel.Children.Add(acceptButton);
        panel.Children.Add(buttonPanel);

        var tcs = new TaskCompletionSource<(bool Accepted, bool Trust)>();
        rejectButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult((false, false));
        };
        acceptButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult((true, trustCheckBox.IsChecked == true));
        };

        var card = new Border
        {
            Background = (Brush)FindResource("MaterialDesignPaper"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };

        _ = DialogHost.Show(card, "RootDialog");
        return tcs.Task;
    }

    private void OnConnected(object? sender, ConnectedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _searchCts?.Cancel();

            ConnectedDeviceNameText.Text = e.RemoteName;
            NavListBox.SelectedIndex = 0;
            SetActivePage(AppPage.Devices);

            AppendLog($"已與 {e.RemoteName} ({e.RemoteAddress}) 建立連線。");
            ((App)Application.Current).ShowConnectionAlert("已建立連線", $"已與 {e.RemoteName} 建立連線。");
        });
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _connection.Disconnect();
        ReturnToDiscoveryView("已中斷連線。");
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇要傳送的檔案", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            await StartSendFilesAsync(dialog.FileNames);
        }
    }

    private async void SendFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "選擇要傳送的資料夾" };
        if (dialog.ShowDialog() == true && dialog.FolderName is { Length: > 0 } folderPath)
        {
            await StartSendFolderAsync(folderPath);
        }
    }

    private void ConnectedViewRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (_connection.IsFileTransferActive || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void ConnectedViewRoot_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ConnectedViewRoot_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var filePaths = paths.Where(File.Exists).ToArray();
        if (filePaths.Length > 0)
        {
            await StartSendFilesAsync(filePaths);
            return;
        }

        var folderPath = paths.FirstOrDefault(Directory.Exists);
        if (folderPath != null)
        {
            await StartSendFolderAsync(folderPath);
        }
    }

    private async Task StartSendFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (_connection.IsFileTransferActive)
        {
            MainSnackbar.MessageQueue?.Enqueue("目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        if (filePaths.Count == 1)
        {
            ShowFileTransferPanel(Path.GetFileName(filePaths[0]), "等待對方接受...");
            await _connection.SendFileAsync(filePaths[0]);
            return;
        }

        ShowFileTransferPanel($"{filePaths.Count} 個檔案", "等待對方接受...");
        await _connection.SendFilesAsync(filePaths);
    }

    private async Task StartSendFolderAsync(string folderPath)
    {
        if (_connection.IsFileTransferActive)
        {
            MainSnackbar.MessageQueue?.Enqueue("目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        ShowFileTransferPanel(Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "等待對方接受...");
        await _connection.SendFolderAsync(folderPath);
    }

    private void CancelFileTransferButton_Click(object sender, RoutedEventArgs e)
    {
        _connection.CancelFileTransfer();
    }

    private void ShowFileTransferPanel(string fileName, string status)
    {
        FileTransferNameText.Text = fileName;
        FileTransferProgressBar.Value = 0;
        FileTransferStatusText.Text = status;
        FileTransferCard.Visibility = Visibility.Visible;
    }

    private void OnFileOffered(object? sender, FileOfferedEventArgs e)
    {
        // 跟 OnConnectionRequested 一樣,這是從背景的 TCP 讀取迴圈觸發的,要排到 UI 執行緒處理。
        // 對方能送出檔案提議,前提是連線已經通過使用者確認過了,所以這裡不再重複詢問,直接接受。
        Dispatcher.BeginInvoke(() =>
        {
            _connection.RespondToFileOffer(e.TransferId, true, _fileTransferSettings.DownloadFolder);
            ShowFileTransferPanel(e.FileName, "接收中...");
            ((App)Application.Current).ShowConnectionAlert(
                $"{ConnectedDeviceNameText.Text} 正在傳送檔案給你", e.FileName);
        });
    }

    private void OnFolderOffered(object? sender, FolderOfferedEventArgs e)
    {
        // 跟 OnFileOffered 一樣:連線已經是使用者同意過的,資料夾/多檔案提議不再另外詢問。
        Dispatcher.BeginInvoke(() =>
        {
            _connection.RespondToFolderOffer(e.TransferId, true, _fileTransferSettings.DownloadFolder);
            var panelName = e.IsBatch ? $"{e.TotalEntries} 個檔案" : e.FolderName;
            ShowFileTransferPanel(panelName, $"接收中...(共 {e.TotalEntries} 個檔案)");
            ((App)Application.Current).ShowConnectionAlert(
                $"{ConnectedDeviceNameText.Text} 正在傳送{(e.IsBatch ? "多個檔案" : "資料夾")}給你", panelName);
        });
    }

    private void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.FolderTransferId != null && e.TotalEntries is { } totalEntries)
            {
                var folderBytesTransferred = e.FolderBytesTransferred ?? e.BytesTransferred;
                var folderTotalBytes = e.FolderTotalBytes ?? e.TotalBytes;
                var percent = folderTotalBytes > 0 ? (double)folderBytesTransferred / folderTotalBytes * 100 : 100;
                FileTransferProgressBar.Value = percent;
                FileTransferStatusText.Text =
                    $"第 {e.EntryIndex}/{totalEntries} 個檔案:{e.FileName}({FormatBytes(folderBytesTransferred)} / {FormatBytes(folderTotalBytes)})";
                return;
            }

            var singlePercent = e.TotalBytes > 0 ? (double)e.BytesTransferred / e.TotalBytes * 100 : 100;
            FileTransferProgressBar.Value = singlePercent;
            FileTransferStatusText.Text = $"{FormatBytes(e.BytesTransferred)} / {FormatBytes(e.TotalBytes)}";
        });
    }

    private void OnFileTransferEnded(object? sender, FileTransferEndedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FileTransferCard.Visibility = Visibility.Collapsed;

            var message = e.Reason switch
            {
                FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                    $"已收到檔案「{e.FileName}」,已儲存到 {_fileTransferSettings.DownloadFolder}。",
                FileTransferEndReason.Completed => $"「{e.FileName}」傳送完成。",
                FileTransferEndReason.Rejected => $"對方拒絕接收「{e.FileName}」。",
                FileTransferEndReason.Cancelled => $"已取消「{e.FileName}」的傳輸。",
                FileTransferEndReason.CancelledByRemote => $"對方取消了「{e.FileName}」的傳輸。",
                FileTransferEndReason.ConnectionClosed => $"連線已中斷,「{e.FileName}」的傳輸已中止。",
                _ => $"「{e.FileName}」傳輸失敗。",
            };

            MainSnackbar.MessageQueue?.Enqueue(message);
            AppendLog(message);
        });
    }

    private void OnFolderTransferEnded(object? sender, FolderTransferEndedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FileTransferCard.Visibility = Visibility.Collapsed;

            var message = e.IsBatch
                ? e.Reason switch
                {
                    FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                        $"已收到 {e.EntryCount} 個檔案,已儲存到 {_fileTransferSettings.DownloadFolder}。",
                    FileTransferEndReason.Completed => $"{e.EntryCount} 個檔案傳送完成。",
                    FileTransferEndReason.Rejected => "對方拒絕接收這些檔案。",
                    FileTransferEndReason.Cancelled => $"已取消傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.CancelledByRemote => $"對方取消了傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.ConnectionClosed => $"連線已中斷,傳輸已中止(已完成 {e.EntryCount} 個檔案)。",
                    _ => $"傳輸失敗(已完成 {e.EntryCount} 個檔案)。",
                }
                : e.Reason switch
                {
                    FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                        $"已收到資料夾「{e.FolderName}」(共 {e.EntryCount} 個檔案),已儲存到 {_fileTransferSettings.DownloadFolder}。",
                    FileTransferEndReason.Completed => $"資料夾「{e.FolderName}」傳送完成(共 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.Rejected => $"對方拒絕接收資料夾「{e.FolderName}」。",
                    FileTransferEndReason.Cancelled => $"已取消資料夾「{e.FolderName}」的傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.CancelledByRemote => $"對方取消了資料夾「{e.FolderName}」的傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.ConnectionClosed => $"連線已中斷,資料夾「{e.FolderName}」的傳輸已中止(已完成 {e.EntryCount} 個檔案)。",
                    _ => $"資料夾「{e.FolderName}」傳輸失敗(已完成 {e.EntryCount} 個檔案)。",
                };

            MainSnackbar.MessageQueue?.Enqueue(message);
            AppendLog(message);
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }

    private void OnRemoteDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var peerName = ConnectedDeviceNameText.Text;
            MainSnackbar.MessageQueue?.Enqueue("對方已中斷連線。");
            ReturnToDiscoveryView("對方已中斷連線。");
            // NotifyIcon.ShowBalloonTip 在 tipText 為空字串時會丟 ArgumentException——這裡是從
            // ConnectionService 背景執行緒觸發的事件,例外被吃掉不會有任何錯誤訊息,氣泡也就永遠不會顯示,
            // 所以訊息一定要給非空字串。
            ((App)Application.Current).ShowConnectionAlert("連線已中斷", $"{peerName} 已中斷連線。");
        });
    }

    /// <summary>不管是自己按了中斷連線,還是對方把連線關掉,都回到搜尋畫面並重新開始廣播/搜尋。</summary>
    private void ReturnToDiscoveryView(string logMessage)
    {
        NavListBox.SelectedIndex = 0;
        SetActivePage(AppPage.Devices);

        _devices.Clear();
        AdvertiseCurrentName();
        StartSearchCycle();

        AppendLog(logMessage);
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _devices.FirstOrDefault(d => d.Key == device.Key);
            if (existing != null)
            {
                var index = _devices.IndexOf(existing);
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
                AppendLog($"找到裝置:{device.DisplayName} ({device.Address}:{device.Port})");
            }
        });
    }

    private void OnDeviceRemoved(object? sender, string instanceName)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _devices.FirstOrDefault(d => d.Key == instanceName);
            if (existing != null)
            {
                _devices.Remove(existing);
                AppendLog($"裝置已離線:{existing.DisplayName}");
            }
        });
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

    // ===================== 影片伺服器(主機端) =====================

    private void StreamVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇要分享的影片", Filter = BuildVideoFileFilter() };
        if (dialog.ShowDialog() == true)
        {
            StartVideoServer(dialog.FileName);
        }
    }

    private void ShareVideoFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "選擇要分享的影片資料夾" };
        if (dialog.ShowDialog() == true && dialog.FolderName is { Length: > 0 } folderPath)
        {
            StartVideoServer(folderPath);
        }
    }

    private static string BuildVideoFileFilter()
    {
        var extensions = string.Join(';', VideoLibraryScanner.VideoExtensions.Select(ext => $"*{ext}"));
        return $"影片檔案|{extensions}";
    }

    private void StartVideoServer(string path)
    {
        var name = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text)
            ? Environment.MachineName
            : DeviceNameTextBox.Text.Trim();

        _videoStreaming.Start(path, name);
        if (!_videoStreaming.IsRunning)
        {
            return;
        }

        _videoDiscovery.Advertise(name, _videoStreaming.Port);

        VideoServerStatusText.Text = $"執行中,共 {_videoStreaming.Manifest.Count} 部影片(連接埠 {_videoStreaming.Port})";
        StreamVideoButton.Visibility = Visibility.Collapsed;
        ShareVideoFolderButton.Visibility = Visibility.Collapsed;
        StopVideoServerButton.Visibility = Visibility.Visible;
    }

    private void StopVideoServerButton_Click(object sender, RoutedEventArgs e)
    {
        _videoDiscovery.StopAdvertising();
        _videoStreaming.Stop();

        VideoServerStatusText.Text = "尚未開啟";
        StreamVideoButton.Visibility = Visibility.Visible;
        ShareVideoFolderButton.Visibility = Visibility.Visible;
        StopVideoServerButton.Visibility = Visibility.Collapsed;
    }

    // ===================== 影片伺服器(觀看端:發現與瀏覽) =====================

    private void OnVideoServerDiscovered(object? sender, DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _videoServers.FirstOrDefault(d => d.Key == device.Key);
            if (existing != null)
            {
                var index = _videoServers.IndexOf(existing);
                _videoServers[index] = device;
            }
            else
            {
                _videoServers.Add(device);
                AppendLog($"找到影片伺服器:{device.DisplayName} ({device.Address}:{device.Port})");
            }
        });
    }

    private void OnVideoServerRemoved(object? sender, string instanceName)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _videoServers.FirstOrDefault(d => d.Key == instanceName);
            if (existing != null)
            {
                _videoServers.Remove(existing);
                AppendLog($"影片伺服器已離線:{existing.DisplayName}");
            }
        });
    }

    private void UpdateVideoServersEmptyState()
    {
        VideoServersEmptyStateStackPanel.Visibility = _videoServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>點「觀看」直接用內嵌瀏覽器開啟對方影片伺服器的網站(首頁清單/單片直接跳轉、觀看頁
    /// 都是伺服器端組好的 HTML,見 <see cref="VideoStreamingService"/>),App 這邊不用自己解析
    /// manifest 或實作播放器 UI。</summary>
    private void WatchVideoServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredDevice server })
        {
            return;
        }

        _activeVideoServer = server;
        VideoWebViewTitleText.Text = server.DisplayName;
        VideoWebView.Source = new Uri($"http://{server.Address}:{server.Port}/");

        SetActivePage(AppPage.VideoWebsite);
    }

    private void VideoWebViewBackButton_Click(object sender, RoutedEventArgs e)
    {
        SetActivePage(AppPage.Video);
    }

    private void VideoWebView_CoreWebView2InitializationCompleted(object sender,
        Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess && VideoWebView.CoreWebView2 != null)
        {
            VideoWebView.CoreWebView2.ContainsFullScreenElementChanged += VideoWebView_ContainsFullScreenElementChanged;
        }
    }

    /// <summary>網頁(播放器)按下全螢幕/離開全螢幕時觸發,負責把 App 本身的 UI(導覽列、返回列、
    /// 視窗邊框)一併收起來/還原,讓 WebView2 裡的內容真正填滿整個螢幕。</summary>
    private void VideoWebView_ContainsFullScreenElementChanged(object? sender, object e)
    {
        if (VideoWebView.CoreWebView2 == null)
        {
            return;
        }

        if (VideoWebView.CoreWebView2.ContainsFullScreenElement)
        {
            EnterWebViewFullScreen();
        }
        else
        {
            RestoreFromWebViewFullScreen();
        }
    }

    private void EnterWebViewFullScreen()
    {
        if (_isWebViewFullScreen)
        {
            return;
        }

        _isWebViewFullScreen = true;
        _preFullScreenWindowState = WindowState;
        _preFullScreenWindowStyle = WindowStyle;
        _preFullScreenResizeMode = ResizeMode;

        NavColorZone.Visibility = Visibility.Collapsed;
        VideoWebViewHeader.Visibility = Visibility.Collapsed;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
    }

    private void RestoreFromWebViewFullScreen()
    {
        if (!_isWebViewFullScreen)
        {
            return;
        }

        _isWebViewFullScreen = false;

        NavColorZone.Visibility = Visibility.Visible;
        VideoWebViewHeader.Visibility = Visibility.Visible;

        WindowState = _preFullScreenWindowState;
        WindowStyle = _preFullScreenWindowStyle;
        ResizeMode = _preFullScreenResizeMode;
    }
}
