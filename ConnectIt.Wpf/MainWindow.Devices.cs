using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // 開啟 App 時自動搜尋裝置的秒數,可在設定頁調整(預設值見 DiscoverySettingsService)。
    private TimeSpan AutoSearchDuration => TimeSpan.FromSeconds(_discoverySettings.SearchDurationSeconds);

    // 搜尋期間內每隔幾秒就重送一次查詢,而不是只查一次就等到搜尋時間結束。
    // 兩台裝置如果幾乎同時啟動,單次查詢很容易在對方還沒完成廣播註冊前就送出而互相找不到對方,
    // 定期重試可以避開這種啟動時機的競爭問題。
    private static readonly TimeSpan SearchQueryInterval = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _searchCts;

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
}
