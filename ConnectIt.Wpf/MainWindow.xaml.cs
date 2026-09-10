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
    private static readonly TimeSpan AutoSearchDuration = TimeSpan.FromSeconds(30);

    // 搜尋期間內每隔幾秒就重送一次查詢,而不是只查一次就等 30 秒。
    // 兩台裝置如果幾乎同時啟動,單次查詢很容易在對方還沒完成廣播註冊前就送出而互相找不到對方,
    // 定期重試可以避開這種啟動時機的競爭問題。
    private static readonly TimeSpan SearchQueryInterval = TimeSpan.FromSeconds(3);

    private readonly MdnsDiscoveryService _discovery = new();
    private readonly ConnectionService _connection = new();
    private readonly ThemeService _themeService = new();
    private readonly FileTransferSettingsService _fileTransferSettings = new();
    private readonly ObservableCollection<DiscoveredDevice> _devices = new();

    private CancellationTokenSource? _searchCts;

    // 收到檔案傳輸提議時開出的確認對話框:記著是哪個 transferId,這樣如果對方在使用者回應前就
    // 取消了提議,可以把這個還開著的對話框強制關掉,而不是讓它永遠停在畫面上。
    private TaskCompletionSource<bool>? _pendingFileOfferTcs;
    private string? _pendingFileOfferTransferId;

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
        _connection.FileTransferProgress += OnFileTransferProgress;
        _connection.FileTransferEnded += OnFileTransferEnded;

        _themeService.Initialize(this);
        SetThemeRadioButtonForMode(_themeService.CurrentMode);

        DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;

        NavListBox.SelectedIndex = 0;

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _searchCts?.Cancel();
            _discovery.Dispose();
            _connection.Dispose();
            _themeService.Dispose();
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 一開機就自動廣播、自動開始監聽,不需要使用者手動按按鈕。
        _discovery.Start();
        _connection.StartListening();
        AdvertiseCurrentName();

        StartSearchCycle();
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
        Settings,
    }

    private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetActivePage(NavListBox.SelectedIndex == 1 ? AppPage.Settings : AppPage.Devices);
    }

    /// <summary>切換導覽列選到的頁面。「裝置」頁會依目前是否已連線,顯示搜尋畫面或已連線畫面。</summary>
    private void SetActivePage(AppPage page)
    {
        if (page == AppPage.Settings)
        {
            DiscoveryViewRoot.Visibility = Visibility.Collapsed;
            ConnectedViewRoot.Visibility = Visibility.Collapsed;
            SettingsViewRoot.Visibility = Visibility.Visible;
            return;
        }

        SettingsViewRoot.Visibility = Visibility.Collapsed;
        var isConnected = _connection.IsConnected;
        ConnectedViewRoot.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryViewRoot.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
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
            var dialogResultTask = ShowConnectionRequestDialog(e.RequesterName, e.RequesterAddress);
            _ = RespondWhenDialogClosedAsync(dialogResultTask, e);
        });
    }

    private static async Task RespondWhenDialogClosedAsync(Task<bool> dialogResultTask, ConnectionRequestedEventArgs e)
    {
        bool accepted;
        try
        {
            accepted = await dialogResultTask;
        }
        catch
        {
            accepted = false;
        }

        e.Respond(accepted);
    }

    /// <summary>必須在 UI 執行緒上呼叫。同步建立並顯示確認對話框,回傳使用者按下按鈕後才會完成的 Task。</summary>
    private Task<bool> ShowConnectionRequestDialog(string requesterName, IPAddress address)
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
            Margin = new Thickness(0, 0, 0, 20),
        });

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

        var tcs = new TaskCompletionSource<bool>();
        rejectButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult(false);
        };
        acceptButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult(true);
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
        });
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _connection.Disconnect();
        ReturnToDiscoveryView("已中斷連線。");
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇要傳送的檔案" };
        if (dialog.ShowDialog() == true)
        {
            await StartSendFileAsync(dialog.FileName);
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

        var filePath = ((string[])e.Data.GetData(DataFormats.FileDrop)!).FirstOrDefault(File.Exists);
        if (filePath != null)
        {
            await StartSendFileAsync(filePath);
        }
    }

    private async Task StartSendFileAsync(string filePath)
    {
        if (_connection.IsFileTransferActive)
        {
            MainSnackbar.MessageQueue?.Enqueue("目前已有檔案傳輸正在進行,請稍後再試。");
            return;
        }

        ShowFileTransferPanel(Path.GetFileName(filePath), "等待對方接受...");
        await _connection.SendFileAsync(filePath);
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
        Dispatcher.BeginInvoke(() => _ = HandleFileOfferedAsync(e));
    }

    private async Task HandleFileOfferedAsync(FileOfferedEventArgs e)
    {
        var accepted = await ShowFileOfferDialog(e.TransferId, e.FileName, e.FileSize);
        _pendingFileOfferTcs = null;
        _pendingFileOfferTransferId = null;

        _connection.RespondToFileOffer(e.TransferId, accepted, _fileTransferSettings.DownloadFolder);

        if (accepted)
        {
            ShowFileTransferPanel(e.FileName, "接收中...");
        }
    }

    /// <summary>必須在 UI 執行緒上呼叫。跟 <see cref="ShowConnectionRequestDialog"/> 同樣的對話框樣式,問使用者要不要接收這個檔案。</summary>
    private Task<bool> ShowFileOfferDialog(string transferId, string fileName, long fileSize)
    {
        var panel = new StackPanel { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new PackIcon
        {
            Kind = PackIconKind.Paperclip,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("MaterialDesign.Brush.Primary"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "對方想要傳送檔案給你",
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{fileName}({FormatBytes(fileSize)})",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        });

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
        System.Windows.Automation.AutomationProperties.SetAutomationId(rejectButton, "RejectFileButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(acceptButton, "AcceptFileButton");
        buttonPanel.Children.Add(rejectButton);
        buttonPanel.Children.Add(acceptButton);
        panel.Children.Add(buttonPanel);

        var tcs = new TaskCompletionSource<bool>();
        _pendingFileOfferTcs = tcs;
        _pendingFileOfferTransferId = transferId;

        rejectButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult(false);
        };
        acceptButton.Click += (_, _) =>
        {
            DialogHost.Close("RootDialog");
            tcs.TrySetResult(true);
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

    private void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var percent = e.TotalBytes > 0 ? (double)e.BytesTransferred / e.TotalBytes * 100 : 100;
            FileTransferProgressBar.Value = percent;
            FileTransferStatusText.Text = $"{FormatBytes(e.BytesTransferred)} / {FormatBytes(e.TotalBytes)}";
        });
    }

    private void OnFileTransferEnded(object? sender, FileTransferEndedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 如果對方在使用者回應提議之前就把傳輸結束掉(例如取消提議),
            // 把還開著的確認對話框強制關掉,不然它會一直卡在畫面上。
            if (e.TransferId == _pendingFileOfferTransferId && _pendingFileOfferTcs is { Task.IsCompleted: false } tcs)
            {
                DialogHost.Close("RootDialog");
                tcs.TrySetResult(false);
            }

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
            MainSnackbar.MessageQueue?.Enqueue("對方已中斷連線。");
            ReturnToDiscoveryView("對方已中斷連線。");
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
}
