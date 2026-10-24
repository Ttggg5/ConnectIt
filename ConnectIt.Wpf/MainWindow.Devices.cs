using System.Collections.ObjectModel;
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
    // 開啟 App(或手動重新搜尋)後「快速搜尋」的秒數,可在設定頁調整(預設值見 DiscoverySettingsService)。
    // 快速搜尋結束後不會停止,而是改成低頻率在背景持續搜尋,使用者不需要再手動按重新整理。
    private TimeSpan FastSearchDuration => TimeSpan.FromSeconds(_discoverySettings.SearchDurationSeconds);

    // 視窗重新回到前景時的快速搜尋秒數:不用跑完整個設定的秒數,短暫密集查詢幾次就足以讓新上線的裝置出現。
    private static readonly TimeSpan ActivationSearchDuration = TimeSpan.FromSeconds(8);

    // 快速搜尋期間內每隔幾秒就重送一次查詢,而不是只查一次就等到搜尋時間結束。
    // 兩台裝置如果幾乎同時啟動,單次查詢很容易在對方還沒完成廣播註冊前就送出而互相找不到對方,
    // 定期重試可以避開這種啟動時機的競爭問題。
    private static readonly TimeSpan SearchQueryInterval = TimeSpan.FromSeconds(1);

    // 快速搜尋結束後,背景持續搜尋的查詢間隔。
    private static readonly TimeSpan BackgroundSearchInterval = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _searchCts;
    private bool _isFastSearching;

    private void AdvertiseCurrentName() => _discovery.Advertise(MyDeviceName, _connection.Port);

    // ===================== 裝置名稱(Hero 上的就地編輯) =====================

    private void EditDeviceNameButton_Click(object sender, RoutedEventArgs e) => BeginEditDeviceName();

    private void BeginEditDeviceName()
    {
        DeviceNameTextBox.Text = MyDeviceName;
        DeviceNameDisplayPanel.Visibility = Visibility.Collapsed;
        DeviceNameEditPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            DeviceNameTextBox.Focus();
            DeviceNameTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitDeviceName()
    {
        ApplyDeviceName(DeviceNameTextBox.Text);
        EndEditDeviceName();
    }

    /// <summary>套用並儲存新的裝置名稱,馬上用新名稱重新廣播(引導頁取名也走這裡)。</summary>
    private void ApplyDeviceName(string? name)
    {
        var previous = MyDeviceName;
        var applied = _deviceSettings.SetDeviceName(name);
        DeviceNameText.Text = applied;
        DeviceNameTextBox.Text = applied;

        if (applied != previous)
        {
            AdvertiseCurrentName();
            AppendLog($"裝置名稱已改為「{applied}」。");
        }
    }

    private void EndEditDeviceName()
    {
        DeviceNameEditPanel.Visibility = Visibility.Collapsed;
        DeviceNameDisplayPanel.Visibility = Visibility.Visible;
        Keyboard_ClearFocus();
    }

    private void DeviceNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDeviceName();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEditDeviceName();
            e.Handled = true;
        }
    }

    private void SaveDeviceNameButton_Click(object sender, RoutedEventArgs e) => CommitDeviceName();

    private void CancelDeviceNameButton_Click(object sender, RoutedEventArgs e) => EndEditDeviceName();

    // ===================== 搜尋 =====================

    /// <summary>
    /// 開始一輪搜尋:先在 <paramref name="fastDuration"/> 內每秒查詢一次(快速搜尋),
    /// 之後改成每 <see cref="BackgroundSearchInterval"/> 在背景持續查詢,直到下一輪搜尋或視窗關閉。
    /// </summary>
    private void StartSearchCycle(TimeSpan? fastDuration = null)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        SetFastSearching(true);
        _ = RunSearchCycleAsync(fastDuration ?? FastSearchDuration, cts);
    }

    private async Task RunSearchCycleAsync(TimeSpan fastDuration, CancellationTokenSource cts)
    {
        var deadline = DateTime.UtcNow + fastDuration;

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

            SetFastSearching(false);

            while (true)
            {
                await Task.Delay(BackgroundSearchInterval, cts.Token);
                _discovery.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的搜尋週期取消(或視窗關閉),不用再處理。
        }
    }

    private void SetFastSearching(bool fast)
    {
        _isFastSearching = fast;
        SearchProgressBar.Visibility = fast ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = fast ? "正在搜尋..." : "持續在背景搜尋中";
        UpdateEmptyState();
    }

    /// <summary>視窗回到前景時(例如從系統匣叫出來)主動密集搜尋一小段時間,讓剛上線的裝置盡快出現。</summary>
    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (IsLoaded && !_isFastSearching)
        {
            StartSearchCycle(ActivationSearchDuration < FastSearchDuration ? ActivationSearchDuration : FastSearchDuration);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _devices.Clear();
        StartSearchCycle();
    }

    private void UpdateEmptyState()
    {
        EmptyStateStackPanel.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== 主動連線(等待對方接受) =====================

    private async void ConnectToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredDevice device })
        {
            return;
        }

        await ConnectWithWaitingDialogAsync(device);
    }

    /// <summary>送出連線請求,同時顯示「正在等待對方接受」對話框(可以取消);
    /// 失敗時依原因(被拒絕/沒回應/連不到)顯示具體的說明與建議,而不是只有一句「未接受」。</summary>
    private async Task ConnectWithWaitingDialogAsync(DiscoveredDevice device)
    {
        using var cts = new CancellationTokenSource();
        var dialogShown = false;

        if (!DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            var dialog = BuildWaitingForAcceptDialog(device.DisplayName, () => cts.Cancel());
            _ = DialogHost.Show(dialog, RootDialogIdentifier);
            dialogShown = true;
        }

        var outcome = await _connection.RequestConnectionWithOutcomeAsync(
            device.Address, device.Port, MyDeviceName, device.DisplayName, cts.Token);

        if (dialogShown && DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            DialogHost.Close(RootDialogIdentifier);
        }

        switch (outcome)
        {
            case ConnectionRequestOutcome.Rejected:
                await ShowMessageDialogAsync(PackIconKind.AccountCancelOutline, $"「{device.DisplayName}」拒絕了連線",
                    "對方按下了「拒絕」。如果是誤按,可以再試一次。");
                break;
            case ConnectionRequestOutcome.Timeout:
                await ShowMessageDialogAsync(PackIconKind.TimerSandEmpty, $"「{device.DisplayName}」沒有回應",
                    "對方沒有在時間內按下接受。請確認對方的畫面上有看到連線請求(Android 可能在通知列)。");
                break;
            case ConnectionRequestOutcome.Unreachable:
                await ShowMessageDialogAsync(PackIconKind.LanDisconnect, $"連不到「{device.DisplayName}」",
                    "請確認對方仍開著 ConnectIt,而且兩台裝置連在同一個 Wi-Fi 或區網。也可能是防火牆擋下了連線。");
                break;
            case ConnectionRequestOutcome.Cancelled:
                MainSnackbar.MessageQueue?.Enqueue("已取消連線請求。");
                break;
        }
    }

    private const string RootDialogIdentifier = "RootDialog";

    private FrameworkElement BuildWaitingForAcceptDialog(string deviceName, Action onCancel)
    {
        var panel = new StackPanel { Width = 320 };
        panel.Children.Add(new ContentControl
        {
            Style = (Style)FindResource("Brand.Radar"),
            Width = 130,
            Height = 130,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new PackIcon { Kind = PackIconKind.LanConnect, Width = 26, Height = 26 },
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"正在等待「{deviceName}」接受",
            Style = (Style)FindResource("Brand.Text.Title"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "請在對方的裝置上按下「接受」。",
            Style = (Style)FindResource("Brand.Text.Caption"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        });

        var cancelButton = new Button
        {
            Content = "取消",
            Style = (Style)FindResource("Brand.Button.Ghost"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "CancelConnectButton");
        cancelButton.Click += (_, _) =>
        {
            cancelButton.IsEnabled = false;
            onCancel();
        };
        panel.Children.Add(cancelButton);

        return WrapInDialogCard(panel);
    }

    /// <summary>顯示一個只有「知道了」按鈕的說明對話框。已經有其他對話框開著時改用 Snackbar,避免重疊開啟。</summary>
    private Task ShowMessageDialogAsync(PackIconKind icon, string title, string message)
    {
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            MainSnackbar.MessageQueue?.Enqueue($"{title}。{message}");
            return Task.CompletedTask;
        }

        var panel = new StackPanel { Width = 340 };
        panel.Children.Add(new Border
        {
            Style = (Style)FindResource("Brand.IconTile"),
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new PackIcon
            {
                Kind = icon,
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Brand.Text.Title"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Style = (Style)FindResource("Brand.Text.Caption"),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 22),
        });

        var okButton = new Button
        {
            Content = "知道了",
            Style = (Style)FindResource("Brand.Button.Gradient"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        okButton.Click += (_, _) => DialogHost.Close(RootDialogIdentifier);
        panel.Children.Add(okButton);

        return DialogHost.Show(WrapInDialogCard(panel), RootDialogIdentifier);
    }

    private Border WrapInDialogCard(UIElement content) => new()
    {
        Background = (Brush)FindResource("Brand.Brush.Surface"),
        BorderBrush = (Brush)FindResource("Brand.Brush.Outline"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(24),
        Padding = new Thickness(28),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = content,
    };

    // ===================== 被動連線(別人連進來) =====================

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

            if (DialogHost.IsDialogOpen(RootDialogIdentifier))
            {
                // 正在顯示其他對話框(例如自己也正在等待別人接受)時,同時只能處理一件事,直接婉拒。
                AppendLog($"目前正在處理其他操作,已自動婉拒「{e.RequesterName}」的連線請求。");
                e.Respond(false);
                return;
            }

            // 視窗藏在系統匣時把它叫出來,不然使用者看不到確認對話框。
            ((App)Application.Current).ShowMainWindow();

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
        var panel = new StackPanel { Width = 340 };
        panel.Children.Add(new Border
        {
            Style = (Style)FindResource("Brand.Avatar"),
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(36),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new PackIcon
            {
                Kind = PackIconKind.Devices,
                Width = 34,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        panel.Children.Add(new TextBlock
        {
            Text = "連線請求",
            Style = (Style)FindResource("Brand.Text.Overline"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"「{requesterName}」想要與你連線",
            Style = (Style)FindResource("Brand.Text.Title"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = NormalizeAddress(address),
            Style = (Style)FindResource("Brand.Text.Caption"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var trustCheckBox = new CheckBox
        {
            Content = "信任此裝置,之後自動接受",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        };
        panel.Children.Add(trustCheckBox);

        var buttonGrid = new Grid();
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition());
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var rejectButton = new Button
        {
            Content = "拒絕",
            Style = (Style)FindResource("Brand.Button.Ghost"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        var acceptButton = new Button
        {
            Content = "接受",
            Style = (Style)FindResource("Brand.Button.Gradient"),
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(acceptButton, 1);
        System.Windows.Automation.AutomationProperties.SetAutomationId(rejectButton, "RejectConnectionButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(acceptButton, "AcceptConnectionButton");
        buttonGrid.Children.Add(rejectButton);
        buttonGrid.Children.Add(acceptButton);
        panel.Children.Add(buttonGrid);

        var tcs = new TaskCompletionSource<(bool Accepted, bool Trust)>();
        rejectButton.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult((false, false));
        };
        acceptButton.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult((true, trustCheckBox.IsChecked == true));
        };

        _ = DialogHost.Show(WrapInDialogCard(panel), RootDialogIdentifier);
        acceptButton.Focus();
        return tcs.Task;
    }

    // ===================== 連線狀態 =====================
    //
    // 可以同時連線多台裝置:_connectedPeers 是所有已連線的裝置,「目前連線」面板一次顯示其中一台
    // (_selectedPeer,由面板頂端的裝置切換列選擇),傳送檔案、螢幕鏡像、中斷連線都作用在選取的那台。

    private readonly ObservableCollection<ConnectedPeerItem> _connectedPeers = new();
    private ConnectedPeerItem? _selectedPeer;

    private ConnectedPeerItem? FindPeerItem(object? session) => _connectedPeers.FirstOrDefault(p => p.Session == session);

    private void OnConnected(object? sender, ConnectedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var item = new ConnectedPeerItem(e.Session, NormalizeAddress(e.RemoteAddress));
            _connectedPeers.Add(item);
            RefreshConnectedAddresses();
            SelectPeer(item);

            NavListBox.SelectedIndex = 0;
            SetActivePage(AppPage.Devices);

            AppendLog($"已與 {e.RemoteName} ({e.RemoteAddress}) 建立連線。");
            ((App)Application.Current).ShowConnectionAlert("已建立連線", $"已與 {e.RemoteName} 建立連線。");
        });
    }

    /// <summary>裝置卡片的「已連線」標籤:ItemsControl.Tag 放所有連線對象的位址(見 ContainedInMultiConverter)。</summary>
    private void RefreshConnectedAddresses() =>
        DevicesItemsControl.Tag = _connectedPeers.Count == 0 ? null : new HashSet<string>(_connectedPeers.Select(p => p.Address));

    private void SelectPeer(ConnectedPeerItem? item)
    {
        _selectedPeer = item;
        if (!Equals(ConnectedPeersListBox.SelectedItem, item))
        {
            ConnectedPeersListBox.SelectedItem = item;
        }

        RenderConnectedPanel();
    }

    private void ConnectedPeersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectedPeersListBox.SelectedItem is ConnectedPeerItem item && item != _selectedPeer)
        {
            SelectPeer(item);
        }
    }

    /// <summary>依選取的裝置更新面板的名稱、位址、裝置切換列與傳輸卡片。</summary>
    private void RenderConnectedPanel()
    {
        UpdateConnectedPanel();
        var multiple = _connectedPeers.Count > 1;
        ConnectedOverlineText.Text = multiple ? $"目前連線({_connectedPeers.Count} 台)" : "目前連線";
        ConnectedPeersListBox.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        SendToAllCheckBox.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        if (!multiple)
        {
            SendToAllCheckBox.IsChecked = false;
        }

        ConnectedDeviceNameText.Text = _selectedPeer?.Name ?? string.Empty;
        ConnectedDeviceAddressText.Text = _selectedPeer?.Address ?? string.Empty;
        RenderTransferCard();
        ApplyScreenMirrorStatus(ScreenMirror.Status);
    }

    /// <summary>依目前是否已連線,顯示/收起裝置頁右側的「目前連線」面板。</summary>
    private void UpdateConnectedPanel()
    {
        var connected = _connectedPeers.Count > 0;
        ConnectedViewRoot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanelColumn.Width = connected ? new GridLength(400) : new GridLength(0);
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        // 之後的畫面更新由 SessionClosed 事件(OnSessionClosed)統一處理。
        _selectedPeer?.Session.Disconnect();
    }

    private void OnSessionClosed(object? sender, SessionClosedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            OnScreenMirrorConnectionEnded(e.Session);
            var item = FindPeerItem(e.Session);
            if (item == null)
            {
                return;
            }

            var peerName = item.Name;
            _connectedPeers.Remove(item);
            RefreshConnectedAddresses();
            if (!_connectedPeers.Any(p => p.TransferInProgress))
            {
                ClearTransferProgressIndicators();
            }

            if (e.ByRemote)
            {
                MainSnackbar.MessageQueue?.Enqueue($"{peerName} 已中斷連線。");
                // NotifyIcon.ShowBalloonTip 在 tipText 為空字串時會丟 ArgumentException——這裡是從
                // ConnectionService 背景執行緒觸發的事件,例外被吃掉不會有任何錯誤訊息,氣泡也就永遠不會顯示,
                // 所以訊息一定要給非空字串。
                ((App)Application.Current).ShowConnectionAlert("連線已中斷", $"{peerName} 已中斷連線。");
            }

            AppendLog(e.ByRemote ? $"{peerName} 已中斷連線。" : $"已中斷與 {peerName} 的連線。");

            if (_selectedPeer == item)
            {
                SelectPeer(_connectedPeers.FirstOrDefault());
            }
            else
            {
                RenderConnectedPanel();
            }

            if (_connectedPeers.Count == 0)
            {
                // 所有連線都結束了:用目前的名稱重新廣播,並重新快速搜尋一輪。
                AdvertiseCurrentName();
                StartSearchCycle();
            }
        });
    }

    private static string NormalizeAddress(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

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
