using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConnectIt.Wpf.Models;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

/// <summary>
/// 快速傳送:從檔案總管右鍵選單/「傳送到」(見 ShellIntegrationService)或系統匣選單送進來的檔案,
/// 跳出「傳送到哪幾台」的選擇視窗——已連線的裝置直接傳,附近還沒連線的裝置會先送出連線請求。
/// </summary>
public partial class MainWindow
{
    private bool _quickSendInProgress;

    /// <summary>App 收到一批要傳送的路徑時呼叫(UI 執行緒)。空清單代表讓使用者自己挑檔案。</summary>
    public async void HandleQuickSend(IReadOnlyList<string> paths)
    {
        ((App)Application.Current).ShowMainWindow();
        NavListBox.SelectedIndex = 0;
        SetActivePage(AppPage.Devices);

        if (_quickSendInProgress || DialogHost.IsDialogOpen(RootDialogIdentifier))
        {
            MainSnackbar.MessageQueue?.Enqueue("目前有其他視窗開著,請先完成後再傳送。");
            return;
        }

        if (paths.Count == 0)
        {
            var picker = new OpenFileDialog { Title = "選擇要傳送的檔案", Multiselect = true };
            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            paths = picker.FileNames;
        }

        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count == 0 && folders.Count == 0)
        {
            MainSnackbar.MessageQueue?.Enqueue("找不到要傳送的檔案。");
            return;
        }

        // 一次只能傳一個資料夾(或一批檔案);同時選了檔案和資料夾時先傳檔案。
        var sendFolder = files.Count == 0 ? folders[0] : null;
        if ((sendFolder != null && folders.Count > 1) || (sendFolder == null && folders.Count > 0))
        {
            MainSnackbar.MessageQueue?.Enqueue("一次只能傳一個資料夾,其他資料夾請另外傳送。");
        }

        var itemName = sendFolder != null
            ? Path.GetFileName(sendFolder.TrimEnd(Path.DirectorySeparatorChar))
            : files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} 個檔案";

        _quickSendInProgress = true;
        try
        {
            var (connected, toConnect) = await ShowQuickSendTargetsDialogAsync(itemName);
            var targets = connected.ToList();
            foreach (var device in toConnect)
            {
                await ConnectWithWaitingDialogAsync(device);
                var address = NormalizeAddress(device.Address);
                if (_connectedPeers.FirstOrDefault(p => p.Address == address && !targets.Contains(p)) is { } peer)
                {
                    targets.Add(peer);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            if (sendFolder != null)
            {
                await StartSendFolderAsync(sendFolder, targets);
            }
            else
            {
                await StartSendFilesAsync(files, targets);
            }
        }
        finally
        {
            _quickSendInProgress = false;
        }
    }

    /// <summary>選擇要傳給哪幾台:已連線的裝置,以及附近還沒連線(版本相容)的裝置。取消時兩個清單都是空的。</summary>
    private Task<(IReadOnlyList<ConnectedPeerItem> Connected, IReadOnlyList<DiscoveredDevice> ToConnect)> ShowQuickSendTargetsDialogAsync(string itemName)
    {
        var panel = BuildScreenMirrorDialogHeader(PackIconKind.Send, "快速傳送", $"要把「{itemName}」傳給哪幾台?",
            _connectedPeers.Count == 0 && _devices.Count == 0 ? "還沒有找到其他裝置。請確認對方也開著 ConnectIt,而且在同一個網路。" : null);

        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var connectedBoxes = new List<(CheckBox Box, ConnectedPeerItem Peer)>();
        foreach (var peer in _connectedPeers)
        {
            var box = new CheckBox
            {
                Content = $"{peer.Name}(已連線)",
                IsChecked = _connectedPeers.Count == 1 || peer == _selectedPeer,
                Margin = new Thickness(0, 4, 0, 4),
            };
            connectedBoxes.Add((box, peer));
            list.Children.Add(box);
        }

        var connectedAddresses = _connectedPeers.Select(p => p.Address).ToHashSet();
        var discoveredBoxes = new List<(CheckBox Box, DiscoveredDevice Device)>();
        foreach (var device in _devices.Where(d => !d.NeedsUpdate && !connectedAddresses.Contains(NormalizeAddress(d.Address))))
        {
            var box = new CheckBox
            {
                Content = $"{device.DisplayName}(會先送出連線請求)",
                IsChecked = _connectedPeers.Count == 0 && _devices.Count == 1,
                Margin = new Thickness(0, 4, 0, 4),
            };
            discoveredBoxes.Add((box, device));
            list.Children.Add(box);
        }

        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var (cancel, send) = AddDialogButtons(panel, "取消", "傳送");

        var tcs = new TaskCompletionSource<(IReadOnlyList<ConnectedPeerItem>, IReadOnlyList<DiscoveredDevice>)>();
        cancel.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult(([], []));
        };
        send.Click += (_, _) =>
        {
            DialogHost.Close(RootDialogIdentifier);
            tcs.TrySetResult((
                connectedBoxes.Where(c => c.Box.IsChecked == true).Select(c => c.Peer).ToList(),
                discoveredBoxes.Where(d => d.Box.IsChecked == true).Select(d => d.Device).ToList()));
        };

        _ = DialogHost.Show(WrapInDialogCard(panel), RootDialogIdentifier);
        send.Focus();
        return tcs.Task;
    }
}
