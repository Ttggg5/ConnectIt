using System.Windows;
using System.Windows.Controls;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 螢幕鏡像(走已配對的連線):「目前連線」面板裡的觀看/分享按鈕、確認與等待對話框、觀看視窗。
/// 狀態機本身在 <see cref="ScreenMirrorController"/>,這裡只負責把狀態反映到畫面上。
/// </summary>
public partial class MainWindow
{
    private ScreenMirrorController? _screenMirror;
    private ScreenViewerWindow? _screenViewerWindow;

    /// <summary>目前由螢幕鏡像開啟的對話框屬於哪個工作階段/哪種狀態,狀態離開時只關自己開的那個。</summary>
    private (string SessionId, ScreenMirrorPhase Phase)? _screenMirrorDialog;

    private ScreenMirrorStatus _lastScreenMirrorStatus = ScreenMirrorStatus.Idle;
    private bool _suppressAllowControlEvent;

    private ScreenMirrorController ScreenMirror => _screenMirror ??= CreateScreenMirror();

    private ScreenMirrorController CreateScreenMirror()
    {
        var controller = new ScreenMirrorController(_connection);
        controller.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() => ApplyScreenMirrorStatus(status));
        controller.Notice += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            AppendLog(message);
            MainSnackbar.MessageQueue?.Enqueue(message);
        });
        return controller;
    }

    /// <summary>建構子呼叫:先把控制器建好,對方送來的請求才收得到。</summary>
    private void InitializeScreenMirror()
    {
        _ = ScreenMirror;
        Closed += (_, _) =>
        {
            _screenViewerWindow?.CloseFromController();
            _screenMirror?.Dispose();
        };
    }

    private void ViewPeerScreenButton_Click(object sender, RoutedEventArgs e) => ScreenMirror.RequestViewPeer();

    private async void ShareMyScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            return;
        }

        var (confirmed, allowControl, shareAudio) = await ShowShareScreenDialogAsync();
        if (confirmed)
        {
            ScreenMirror.StartShareOffer(allowControl, shareAudio);
        }
    }

    private void StopScreenMirrorButton_Click(object sender, RoutedEventArgs e) => ScreenMirror.Stop();

    private void ShowScreenViewerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_screenViewerWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }
    }

    private void AllowRemoteControlCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAllowControlEvent)
        {
            return;
        }

        ScreenMirror.SetAllowControl(AllowRemoteControlCheckBox.IsChecked == true);
    }

    private void ShareAudioCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAllowControlEvent)
        {
            return;
        }

        ScreenMirror.SetShareAudio(ShareAudioCheckBox.IsChecked == true);
    }

    /// <summary>連線結束(不論哪一方)時呼叫。</summary>
    private void OnScreenMirrorConnectionEnded() => _screenMirror?.OnConnectionClosed();

    /// <summary>UI 執行緒上呼叫:依最新狀態更新面板、對話框、觀看視窗與系統匣提示。</summary>
    private void ApplyScreenMirrorStatus(ScreenMirrorStatus status)
    {
        // 事件是排隊送過來的,可能已經過時——一律以控制器目前的狀態為準。
        status = ScreenMirror.Status;
        var previous = _lastScreenMirrorStatus;
        _lastScreenMirrorStatus = status;

        ScreenMirrorIdlePanel.Visibility = status.Phase is ScreenMirrorPhase.Sharing or ScreenMirrorPhase.Viewing ? Visibility.Collapsed : Visibility.Visible;
        ScreenMirrorIdlePanel.IsEnabled = status.Phase == ScreenMirrorPhase.Idle;
        ScreenMirrorSharingPanel.Visibility = status.Phase == ScreenMirrorPhase.Sharing ? Visibility.Visible : Visibility.Collapsed;
        ScreenMirrorViewingPanel.Visibility = status.Phase == ScreenMirrorPhase.Viewing ? Visibility.Visible : Visibility.Collapsed;
        if (status.Phase == ScreenMirrorPhase.Sharing)
        {
            _suppressAllowControlEvent = true;
            AllowRemoteControlCheckBox.IsChecked = status.AllowControl;
            ShareAudioCheckBox.IsChecked = status.ShareAudio;
            _suppressAllowControlEvent = false;
        }

        CloseStaleScreenMirrorDialog(status);
        UpdateScreenViewerWindow(status);

        if (status.Phase != previous.Phase || status.SessionId != previous.SessionId)
        {
            OnScreenMirrorPhaseEntered(status);
        }
    }

    private void OnScreenMirrorPhaseEntered(ScreenMirrorStatus status)
    {
        var peerName = string.IsNullOrEmpty(ConnectedDeviceNameText.Text) ? "對方" : ConnectedDeviceNameText.Text;
        switch (status.Phase)
        {
            case ScreenMirrorPhase.OutgoingRequest:
                ShowScreenMirrorDialog(status, BuildScreenMirrorWaitingDialog(
                    $"正在等待「{peerName}」同意", "對方同意後,就會開新視窗顯示對方的螢幕。", () => ScreenMirror.CancelOutgoing()));
                break;
            case ScreenMirrorPhase.OutgoingOffer:
                ShowScreenMirrorDialog(status, BuildScreenMirrorWaitingDialog(
                    $"正在等待「{peerName}」接受", "對方接受後就會開始分享你的主螢幕。", () => ScreenMirror.CancelOutgoing()));
                break;
            case ScreenMirrorPhase.IncomingRequest:
                ((App)Application.Current).ShowMainWindow();
                ((App)Application.Current).ShowConnectionAlert("螢幕鏡像", $"{peerName} 想要觀看你的螢幕。");
                ShowIncomingScreenRequestDialog(status, peerName);
                break;
            case ScreenMirrorPhase.IncomingOffer:
                ((App)Application.Current).ShowMainWindow();
                ((App)Application.Current).ShowConnectionAlert("螢幕鏡像", $"{peerName} 想要分享螢幕給你。");
                ShowIncomingScreenOfferDialog(status, peerName);
                break;
            case ScreenMirrorPhase.Sharing:
                AppendLog($"開始分享螢幕給 {peerName}。");
                ((App)Application.Current).SetTrayStatus("正在分享螢幕");
                break;
            case ScreenMirrorPhase.Viewing:
                AppendLog($"開始觀看 {peerName} 的螢幕。");
                break;
            case ScreenMirrorPhase.Idle:
                ((App)Application.Current).SetTrayStatus(null);
                break;
        }
    }

    private void UpdateScreenViewerWindow(ScreenMirrorStatus status)
    {
        if (status.Phase == ScreenMirrorPhase.Viewing)
        {
            if (_screenViewerWindow == null)
            {
                var peerName = string.IsNullOrEmpty(ConnectedDeviceNameText.Text) ? "對方" : ConnectedDeviceNameText.Text;
                var window = new ScreenViewerWindow(ScreenMirror, peerName, status);
                window.Closed += (_, _) =>
                {
                    if (_screenViewerWindow == window)
                    {
                        _screenViewerWindow = null;
                    }
                };
                _screenViewerWindow = window;
                window.Show();
                window.Activate();
            }
            else
            {
                _screenViewerWindow.ApplyStatus(status);
            }

            return;
        }

        _screenViewerWindow?.CloseFromController();
        _screenViewerWindow = null;
    }

    private void ShowScreenMirrorDialog(ScreenMirrorStatus status, FrameworkElement content)
    {
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            // 已經有別的對話框開著(例如連線請求),不要疊上去;改用 Snackbar 提示,操作仍可以從面板進行。
            MainSnackbar.MessageQueue?.Enqueue(status.Phase switch
            {
                ScreenMirrorPhase.IncomingRequest => "對方想要觀看你的螢幕,但目前有其他對話框開著,已自動婉拒。",
                ScreenMirrorPhase.IncomingOffer => "對方想要分享螢幕給你,但目前有其他對話框開著,已自動婉拒。",
                _ => "正在等待對方回應螢幕鏡像…",
            });
            if (status.Phase == ScreenMirrorPhase.IncomingRequest)
            {
                ScreenMirror.RespondToIncomingRequest(false, false);
            }
            else if (status.Phase == ScreenMirrorPhase.IncomingOffer)
            {
                ScreenMirror.RespondToIncomingOffer(false);
            }

            return;
        }

        _screenMirrorDialog = (status.SessionId!, status.Phase);
        _ = DialogHost.Show(content, RootDialogIdentifier);
    }

    /// <summary>狀態已經離開對話框對應的階段(對方回應了、取消了、逾時、斷線)時,把我們開的對話框關掉。</summary>
    private void CloseStaleScreenMirrorDialog(ScreenMirrorStatus status)
    {
        if (_screenMirrorDialog is not { } dialog)
        {
            return;
        }

        if (dialog.SessionId == status.SessionId && dialog.Phase == status.Phase)
        {
            return;
        }

        _screenMirrorDialog = null;
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            DialogHost.Close(RootDialogIdentifier);
        }
    }

    /// <summary>使用者自己按了對話框上的按鈕:先清掉記錄再關,避免狀態更新時又關一次別人的對話框。</summary>
    private void CloseOwnScreenMirrorDialog()
    {
        _screenMirrorDialog = null;
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            DialogHost.Close(RootDialogIdentifier);
        }
    }

    private FrameworkElement BuildScreenMirrorWaitingDialog(string title, string caption, Action onCancel)
    {
        var panel = new StackPanel { Width = 320 };
        panel.Children.Add(new ContentControl
        {
            Style = (Style)FindResource("Brand.Radar"),
            Width = 130,
            Height = 130,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new PackIcon { Kind = PackIconKind.CastConnected, Width = 26, Height = 26 },
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Brand.Text.Title"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Style = (Style)FindResource("Brand.Text.Caption"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        });

        var cancelButton = new Button
        {
            Content = "取消",
            Style = (Style)FindResource("Brand.Button.Ghost"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        cancelButton.Click += (_, _) =>
        {
            CloseOwnScreenMirrorDialog();
            onCancel();
        };
        panel.Children.Add(cancelButton);

        return WrapInDialogCard(panel);
    }

    private StackPanel BuildScreenMirrorDialogHeader(PackIconKind icon, string overline, string title, string? caption)
    {
        var panel = new StackPanel { Width = 360 };
        panel.Children.Add(new Border
        {
            Style = (Style)FindResource("Brand.Avatar"),
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(36),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new PackIcon
            {
                Kind = icon,
                Width = 34,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        panel.Children.Add(new TextBlock
        {
            Text = overline,
            Style = (Style)FindResource("Brand.Text.Overline"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Brand.Text.Title"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, caption == null ? 20 : 6),
        });
        if (caption != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                Style = (Style)FindResource("Brand.Text.Caption"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            });
        }

        return panel;
    }

    private CheckBox BuildAllowControlCheckBox() => new()
    {
        Content = "允許對方操控這台電腦(滑鼠與鍵盤)",
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 20),
    };

    /// <summary>預設勾選:分享畫面時大多也想讓對方聽到聲音(例如一起看影片)。</summary>
    private CheckBox BuildShareAudioCheckBox() => new()
    {
        Content = "同時分享這台電腦的聲音",
        IsChecked = true,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private (Button Left, Button Right) AddDialogButtons(StackPanel panel, string leftText, string rightText)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Button { Content = leftText, Style = (Style)FindResource("Brand.Button.Ghost"), Margin = new Thickness(0, 0, 6, 0) };
        var right = new Button { Content = rightText, Style = (Style)FindResource("Brand.Button.Gradient"), Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        panel.Children.Add(grid);
        return (left, right);
    }

    private Task<(bool Confirmed, bool AllowControl, bool ShareAudio)> ShowShareScreenDialogAsync()
    {
        var panel = BuildScreenMirrorDialogHeader(PackIconKind.MonitorShare, "螢幕鏡像", "分享你的螢幕",
            "對方接受後會看到這台電腦的主螢幕(包含滑鼠游標)。");
        var shareAudio = BuildShareAudioCheckBox();
        panel.Children.Add(shareAudio);
        var allowControl = BuildAllowControlCheckBox();
        panel.Children.Add(allowControl);
        var (cancel, share) = AddDialogButtons(panel, "取消", "開始分享");

        var tcs = new TaskCompletionSource<(bool, bool, bool)>();
        cancel.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult((false, false, false));
        };
        share.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult((true, allowControl.IsChecked == true, shareAudio.IsChecked == true));
        };

        _ = DialogHost.Show(WrapInDialogCard(panel), RootDialogIdentifier);
        share.Focus();
        return tcs.Task;
    }

    private void ShowIncomingScreenRequestDialog(ScreenMirrorStatus status, string peerName)
    {
        var panel = BuildScreenMirrorDialogHeader(PackIconKind.MonitorShare, "螢幕鏡像", $"「{peerName}」想要觀看你的螢幕",
            "同意後對方會看到這台電腦的主螢幕(包含滑鼠游標)。");
        var shareAudio = BuildShareAudioCheckBox();
        panel.Children.Add(shareAudio);
        var allowControl = BuildAllowControlCheckBox();
        panel.Children.Add(allowControl);
        var (reject, accept) = AddDialogButtons(panel, "拒絕", "同意");
        System.Windows.Automation.AutomationProperties.SetAutomationId(reject, "RejectScreenRequestButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(accept, "AcceptScreenRequestButton");

        reject.Click += (_, _) =>
        {
            CloseOwnScreenMirrorDialog();
            ScreenMirror.RespondToIncomingRequest(false, false);
        };
        accept.Click += (_, _) =>
        {
            CloseOwnScreenMirrorDialog();
            ScreenMirror.RespondToIncomingRequest(true, allowControl.IsChecked == true, shareAudio.IsChecked == true);
        };

        ShowScreenMirrorDialog(status, WrapInDialogCard(panel));
    }

    private void ShowIncomingScreenOfferDialog(ScreenMirrorStatus status, string peerName)
    {
        var panel = BuildScreenMirrorDialogHeader(PackIconKind.CastConnected, "螢幕鏡像", $"「{peerName}」想要分享螢幕給你", null);
        var (reject, accept) = AddDialogButtons(panel, "拒絕", "觀看");

        reject.Click += (_, _) =>
        {
            CloseOwnScreenMirrorDialog();
            ScreenMirror.RespondToIncomingOffer(false);
        };
        accept.Click += (_, _) =>
        {
            CloseOwnScreenMirrorDialog();
            ScreenMirror.RespondToIncomingOffer(true);
        };

        ShowScreenMirrorDialog(status, WrapInDialogCard(panel));
    }
}
