using System.Windows;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 遙控器(走已配對的連線,不用開螢幕鏡像):「目前連線」面板的「遙控器」按鈕、對方請求遙控時的確認對話框、
/// 遙控器視窗,以及「正在被遙控」的狀態與結束按鈕。狀態機本身在 <see cref="RemoteControlController"/>。
/// (跟 MainWindow.RemoteControl.cs 的「影片遙控面板」是不同的功能。)
/// </summary>
public partial class MainWindow
{
    private RemoteControlController? _remoteControl;
    private RemoteControlWindow? _remoteControlWindow;

    /// <summary>由遙控器開啟的對話框屬於哪個工作階段/狀態,狀態離開時只關自己開的那個。</summary>
    private (string SessionId, RemoteControlPhase Phase)? _remoteControlDialog;

    private RemoteControlStatus _lastRemoteControlStatus = RemoteControlStatus.Idle;

    private RemoteControlController RemoteControl => _remoteControl ??= CreateRemoteControl();

    private RemoteControlController CreateRemoteControl()
    {
        var controller = new RemoteControlController(_connection);
        controller.StatusChanged += (_, _) => Dispatcher.BeginInvoke(ApplyRemoteControlStatus);
        controller.Notice += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            AppendLog(message);
            MainSnackbar.MessageQueue?.Enqueue(message);
        });
        return controller;
    }

    /// <summary>建構子呼叫:先把控制器建好,對方送來的遙控請求才收得到。</summary>
    private void InitializeRemoteKeys()
    {
        _ = RemoteControl;
        Closed += (_, _) =>
        {
            _remoteControlWindow?.CloseFromController();
            _remoteControl?.Dispose();
        };
    }

    private string RemoteControlPeerName => RemoteControl.Status.Peer?.RemoteName is { Length: > 0 } name ? name : "對方";

    private void RemoteControlButton_Click(object sender, RoutedEventArgs e)
    {
        var status = RemoteControl.Status;
        if (status.Phase == RemoteControlPhase.Controlling)
        {
            _remoteControlWindow?.Activate();
            return;
        }

        if (_selectedPeer is { } peer)
        {
            RemoteControl.RequestControl(peer.Session);
        }
    }

    private void StopRemoteControlButton_Click(object sender, RoutedEventArgs e) => RemoteControl.Stop();

    /// <summary>UI 執行緒上呼叫:依控制器目前的狀態更新按鈕、面板、對話框與遙控器視窗。</summary>
    private void ApplyRemoteControlStatus()
    {
        var status = RemoteControl.Status;
        var previous = _lastRemoteControlStatus;
        _lastRemoteControlStatus = status;

        RemoteControlButton.IsEnabled = status.Phase is RemoteControlPhase.Idle or RemoteControlPhase.Controlling;
        RemoteControlButtonText.Text = status.Phase == RemoteControlPhase.Controlling ? $"開啟遙控器({RemoteControlPeerName})" : "遙控器";
        RemoteControlledPanel.Visibility = status.Phase == RemoteControlPhase.Controlled ? Visibility.Visible : Visibility.Collapsed;
        RemoteControlledText.Text = $"{RemoteControlPeerName} 正在遙控這台電腦";

        if (_remoteControlDialog is { } dialog && (dialog.SessionId != status.SessionId || dialog.Phase != status.Phase))
        {
            _remoteControlDialog = null;
            if (DialogHost.IsDialogOpen(RootDialogIdentifier))
            {
                DialogHost.Close(RootDialogIdentifier);
            }
        }

        if (status.Phase != RemoteControlPhase.Controlling && _remoteControlWindow != null)
        {
            _remoteControlWindow.CloseFromController();
            _remoteControlWindow = null;
        }

        if (status.Phase == previous.Phase && status.SessionId == previous.SessionId)
        {
            return;
        }

        var peerName = RemoteControlPeerName;
        switch (status.Phase)
        {
            case RemoteControlPhase.OutgoingRequest:
                ShowRemoteControlDialog(status, BuildScreenMirrorWaitingDialog(
                    $"正在等待「{peerName}」同意", "對方同意後就會開啟遙控器。", () => RemoteControl.Stop()));
                break;
            case RemoteControlPhase.IncomingRequest:
                OnIncomingRemoteControlRequest(status, peerName);
                break;
            case RemoteControlPhase.Controlling:
                AppendLog($"開始遙控 {peerName}。");
                var window = new RemoteControlWindow(RemoteControl, peerName, status.Platform);
                window.Closed += (_, _) =>
                {
                    if (_remoteControlWindow == window)
                    {
                        _remoteControlWindow = null;
                    }
                };
                _remoteControlWindow = window;
                window.Show();
                window.Activate();
                break;
            case RemoteControlPhase.Controlled:
                AppendLog($"{peerName} 開始遙控這台電腦。");
                ((App)Application.Current).ShowConnectionAlert("遙控器", $"{peerName} 正在遙控這台電腦。");
                break;
        }
    }

    private void OnIncomingRemoteControlRequest(RemoteControlStatus status, string peerName)
    {
        // 信任的裝置(只認憑證指紋)直接同意,不用每次都確認。
        if (status.Peer is { } peer && _trustedDevices.IsTrusted(peer.RemoteFingerprint))
        {
            AppendLog($"已自動同意信任裝置「{peerName}」的遙控請求。");
            RemoteControl.RespondToIncomingRequest(true);
            return;
        }

        ((App)Application.Current).ShowMainWindow();
        ((App)Application.Current).ShowConnectionAlert("遙控器", $"{peerName} 想要遙控這台電腦。");
        var panel = BuildScreenMirrorDialogHeader(PackIconKind.RemoteTv, "遙控器", $"「{peerName}」想要遙控這台電腦",
            "同意後對方可以送出按鍵到這台電腦:播放/暫停、音量、簡報翻頁,以及 Ctrl+C、Alt+Tab 這類快捷鍵(不能操作滑鼠)。");
        var (reject, accept) = AddDialogButtons(panel, "拒絕", "同意");
        reject.Click += (_, _) =>
        {
            CloseOwnRemoteControlDialog();
            RemoteControl.RespondToIncomingRequest(false);
        };
        accept.Click += (_, _) =>
        {
            CloseOwnRemoteControlDialog();
            RemoteControl.RespondToIncomingRequest(true);
        };
        ShowRemoteControlDialog(status, WrapInDialogCard(panel));
    }

    private void ShowRemoteControlDialog(RemoteControlStatus status, FrameworkElement content)
    {
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            // 已經有別的對話框開著:不要疊上去。對方的請求直接婉拒,自己的請求照樣等待(面板上看得到狀態)。
            if (status.Phase == RemoteControlPhase.IncomingRequest)
            {
                MainSnackbar.MessageQueue?.Enqueue("對方想要遙控這台電腦,但目前有其他對話框開著,已自動婉拒。");
                RemoteControl.RespondToIncomingRequest(false);
            }

            return;
        }

        _remoteControlDialog = (status.SessionId!, status.Phase);
        _ = DialogHost.Show(content, RootDialogIdentifier);
    }

    private void CloseOwnRemoteControlDialog()
    {
        _remoteControlDialog = null;
        if (DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            DialogHost.Close(RootDialogIdentifier);
        }
    }
}
