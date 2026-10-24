using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

// 檔案/資料夾傳輸的畫面。可以同時連線多台裝置,每台各自一個傳輸(ConnectedPeerItem 記住各自的傳輸卡片狀態),
// 傳輸卡片只顯示選取中的那台;勾選「同時傳給所有已連線的裝置」時,同一批檔案會分別送給每一台。
public partial class MainWindow
{
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

    /// <summary>這次要傳給哪些裝置:勾選「同時傳給所有已連線的裝置」時是全部,否則是選取中的那台。</summary>
    private IReadOnlyList<ConnectedPeerItem> SendTargets() =>
        SendToAllCheckBox.IsChecked == true && _connectedPeers.Count > 1
            ? _connectedPeers.ToArray()
            : _selectedPeer is { } selected ? [selected] : [];

    private void ConnectedViewRoot_DragEnter(object sender, DragEventArgs e)
    {
        var targets = SendTargets();
        if (targets.Count == 0 || targets.All(t => t.Session.IsFileTransferActive) || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropOverlayText.Text = targets.Count > 1 ? $"放開以傳送給 {targets.Count} 台裝置" : $"放開以傳送給 {targets[0].Name}";
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

    /// <summary>傳給 <see cref="SendTargets"/> 裡每一台(或指定的 <paramref name="targets"/>);正在傳輸中的裝置會被略過。</summary>
    private async Task StartSendFilesAsync(IReadOnlyList<string> filePaths, IReadOnlyList<ConnectedPeerItem>? targets = null)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        var displayName = filePaths.Count == 1 ? Path.GetFileName(filePaths[0]) : $"{filePaths.Count} 個檔案";
        foreach (var target in ReadyTargets(targets ?? SendTargets()))
        {
            ShowFileTransferPanel(target, displayName, "等待對方接受...");
            await target.Session.SendFilesAsync(filePaths);
        }
    }

    private async Task StartSendFolderAsync(string folderPath, IReadOnlyList<ConnectedPeerItem>? targets = null)
    {
        var displayName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var target in ReadyTargets(targets ?? SendTargets()))
        {
            ShowFileTransferPanel(target, displayName, "等待對方接受...");
            await target.Session.SendFolderAsync(folderPath);
        }
    }

    /// <summary>過濾掉正在傳輸中的裝置(每條連線同一時間只能有一個傳輸),並告訴使用者哪些被略過了。</summary>
    private List<ConnectedPeerItem> ReadyTargets(IReadOnlyList<ConnectedPeerItem> targets)
    {
        var busy = targets.Where(t => t.Session.IsFileTransferActive).ToList();
        if (busy.Count > 0)
        {
            MainSnackbar.MessageQueue?.Enqueue(targets.Count == 1
                ? "目前已有檔案傳輸正在進行,請稍後再試。"
                : $"{string.Join("、", busy.Select(b => b.Name))} 正在傳輸中,這次先略過。");
        }

        return targets.Except(busy).ToList();
    }

    private void CancelFileTransferButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedPeer?.Session.CancelFileTransfer();
    }

    private void ShowFileTransferPanel(ConnectedPeerItem peer, string fileName, string status)
    {
        peer.LastReceivedRecord = null;
        peer.Rate.Reset();
        peer.TransferVisible = true;
        peer.TransferInProgress = true;
        peer.TransferName = fileName;
        peer.TransferStatus = status;
        peer.TransferPercent = 0;
        peer.TransferPercentText = string.Empty;
        peer.ChipStatus = "準備傳輸";

        TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        ((App)Application.Current).SetTrayStatus($"準備傳輸「{fileName}」");
        RenderTransferCardIfSelected(peer);
    }

    private void HideFileTransferPanel(ConnectedPeerItem peer)
    {
        peer.LastReceivedRecord = null;
        peer.TransferVisible = false;
        peer.TransferInProgress = false;
        peer.ChipStatus = null;
        RenderTransferCardIfSelected(peer);
    }

    private void RenderTransferCardIfSelected(ConnectedPeerItem peer)
    {
        if (peer == _selectedPeer)
        {
            RenderTransferCard();
        }
    }

    /// <summary>把選取中裝置的傳輸狀態畫到傳輸卡片上。</summary>
    private void RenderTransferCard()
    {
        if (_selectedPeer is not { TransferVisible: true } peer)
        {
            FileTransferCard.Visibility = Visibility.Collapsed;
            return;
        }

        FileTransferNameText.Text = peer.TransferName;
        FileTransferStatusText.Text = peer.TransferStatus;
        FileTransferPercentText.Text = peer.TransferPercentText;
        FileTransferProgressBar.Value = peer.TransferPercent;

        var done = !peer.TransferInProgress && peer.LastReceivedRecord is { } record;
        FileTransferIcon.Kind = done ? PackIconKind.CheckCircle : PackIconKind.SwapVertical;
        FileTransferProgressBar.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        FileTransferDoneActions.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        CancelFileTransferButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        DismissFileTransferButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        if (done)
        {
            OpenReceivedButtonText.Text = peer.LastReceivedRecord!.IsFolder ? "開啟資料夾" : "開啟檔案";
            ShowReceivedInFolderButton.Visibility = peer.LastReceivedRecord.IsFolder ? Visibility.Collapsed : Visibility.Visible;
        }

        FileTransferCard.Visibility = Visibility.Visible;
    }

    /// <summary>沒有任何裝置在傳輸時,收起工作列進度與系統匣提示。</summary>
    private void ClearTransferProgressIndicators()
    {
        if (_connectedPeers.Any(p => p.TransferInProgress))
        {
            return;
        }

        TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        TaskbarInfo.ProgressValue = 0;
        ((App)Application.Current).SetTrayStatus(null);
    }

    /// <summary>傳輸結束時呼叫:寫入傳輸紀錄;接收成功的話把該裝置的傳輸卡片切成「完成」狀態並提供開啟按鈕,
    /// 其他情況(傳送完成、失敗、取消)收起卡片,結果由 Snackbar 告知。</summary>
    private void CompleteTransfer(ConnectedPeerItem? peer, TransferRecord record)
    {
        _transferHistory.Insert(0, record);
        if (peer == null)
        {
            ClearTransferProgressIndicators();
            return;
        }

        peer.TransferInProgress = false;
        peer.ChipStatus = null;
        peer.TransferPercentText = string.Empty;
        ClearTransferProgressIndicators();

        if (!record.CanOpen)
        {
            HideFileTransferPanel(peer);
            return;
        }

        peer.LastReceivedRecord = record;
        peer.TransferVisible = true;
        peer.TransferName = record.Name;
        peer.TransferStatus = $"已儲存到 {(record.IsFolder ? record.SavedPath : Path.GetDirectoryName(record.SavedPath))}";
        RenderTransferCardIfSelected(peer);
    }

    private void DismissFileTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is { } peer)
        {
            HideFileTransferPanel(peer);
        }
    }

    private void OpenReceivedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer?.LastReceivedRecord is { } record)
        {
            OpenTransferRecord(record);
        }
    }

    private void ShowReceivedInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer?.LastReceivedRecord is { } record)
        {
            ShowTransferRecordInFolder(record);
        }
    }

    private void OpenTransferRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferRecord record })
        {
            OpenTransferRecord(record);
        }
    }

    private void ShowTransferRecordInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferRecord record })
        {
            ShowTransferRecordInFolder(record);
        }
    }

    /// <summary>用系統預設的程式開啟收到的檔案(資料夾則用檔案總管開啟)。</summary>
    private void OpenTransferRecord(TransferRecord record)
    {
        if (record.SavedPath is not { } path || !(record.IsFolder ? Directory.Exists(path) : File.Exists(path)))
        {
            MainSnackbar.MessageQueue?.Enqueue("找不到這個檔案,可能已經被移動或刪除。");
            return;
        }

        TryStartShell(path);
    }

    /// <summary>開啟檔案總管並選取收到的檔案(資料夾則直接開啟該資料夾)。</summary>
    private void ShowTransferRecordInFolder(TransferRecord record)
    {
        if (record.SavedPath is not { } path)
        {
            return;
        }

        if (record.IsFolder)
        {
            OpenTransferRecord(record);
            return;
        }

        if (!File.Exists(path))
        {
            MainSnackbar.MessageQueue?.Enqueue("找不到這個檔案,可能已經被移動或刪除。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"無法開啟檔案總管:{ex.Message}");
        }
    }

    private void TryStartShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainSnackbar.MessageQueue?.Enqueue("沒有可以開啟這個檔案的程式。");
            AppendLog($"無法開啟「{path}」:{ex.Message}");
        }
    }

    private void OnFileOffered(object? sender, FileOfferedEventArgs e)
    {
        // 跟 OnConnectionRequested 一樣,這是從背景的 TCP 讀取迴圈觸發的,要排到 UI 執行緒處理。
        // 對方能送出檔案提議,前提是連線已經通過使用者確認過了,所以這裡不再重複詢問,直接接受。
        Dispatcher.BeginInvoke(() =>
        {
            _connection.RespondToFileOffer(e.TransferId, true, _fileTransferSettings.DownloadFolder);
            if (FindPeerItem(sender) is not { } peer)
            {
                return;
            }

            ShowFileTransferPanel(peer, e.FileName, "接收中...");
            ((App)Application.Current).ShowConnectionAlert($"{peer.Name} 正在傳送檔案給你", e.FileName);
        });
    }

    private void OnFolderOffered(object? sender, FolderOfferedEventArgs e)
    {
        // 跟 OnFileOffered 一樣:連線已經是使用者同意過的,資料夾/多檔案提議不再另外詢問。
        Dispatcher.BeginInvoke(() =>
        {
            _connection.RespondToFolderOffer(e.TransferId, true, _fileTransferSettings.DownloadFolder);
            if (FindPeerItem(sender) is not { } peer)
            {
                return;
            }

            var panelName = e.IsBatch ? $"{e.TotalEntries} 個檔案" : e.FolderName;
            ShowFileTransferPanel(peer, panelName, $"接收中...(共 {e.TotalEntries} 個檔案)");
            ((App)Application.Current).ShowConnectionAlert(
                $"{peer.Name} 正在傳送{(e.IsBatch ? "多個檔案" : "資料夾")}給你", panelName);
        });
    }

    private void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (FindPeerItem(sender) is not { TransferInProgress: true } peer)
            {
                return;
            }

            // 資料夾/多檔案傳輸以整批的累計位元組計算,百分比、速度、剩餘時間才是「整個傳輸」的。
            var isFolder = e.FolderTransferId != null && e.TotalEntries is not null;
            var transferred = isFolder ? e.FolderBytesTransferred ?? e.BytesTransferred : e.BytesTransferred;
            var total = isFolder ? e.FolderTotalBytes ?? e.TotalBytes : e.TotalBytes;
            var snapshot = peer.Rate.Update(transferred, total);
            var fraction = total > 0 ? snapshot.Fraction : 1;
            var percent = (int)Math.Floor(fraction * 100);

            peer.TransferPercent = fraction * 100;
            peer.TransferPercentText = $"{percent}%";
            var bytesText = $"{FormatBytes(transferred)} / {FormatBytes(total)}・{snapshot.RateText}";
            peer.TransferStatus = isFolder
                ? $"第 {e.EntryIndex}/{e.TotalEntries} 個檔案:{e.FileName}\n{bytesText}"
                : bytesText;
            var verb = e.Direction == FileTransferDirection.Receiving ? "接收中" : "傳送中";
            peer.ChipStatus = $"{verb} {percent}%";
            RenderTransferCardIfSelected(peer);

            TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            TaskbarInfo.ProgressValue = fraction;
            ((App)Application.Current).SetTrayStatus($"{verb} {percent}%・{peer.TransferName}");
        });
    }

    private void OnFileTransferEnded(object? sender, FileTransferEndedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var peer = FindPeerItem(sender);
            var peerName = peer?.Name ?? (sender as PeerSession)?.RemoteName ?? string.Empty;
            CompleteTransfer(peer, new TransferRecord
            {
                Direction = e.Direction,
                Reason = e.Reason,
                Name = e.FileName,
                SavedPath = e.SavedFilePath,
                IsFolder = false,
                PeerName = peerName,
            });

            var message = e.Reason switch
            {
                FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                    $"已收到 {peerName} 傳來的「{e.FileName}」,已儲存到 {_fileTransferSettings.DownloadFolder}。",
                FileTransferEndReason.Completed => $"「{e.FileName}」已傳送給 {peerName}。",
                FileTransferEndReason.Rejected => $"{peerName} 拒絕接收「{e.FileName}」。",
                FileTransferEndReason.Cancelled => $"已取消「{e.FileName}」的傳輸。",
                FileTransferEndReason.CancelledByRemote => $"{peerName} 取消了「{e.FileName}」的傳輸。",
                FileTransferEndReason.ConnectionClosed => $"與 {peerName} 的連線已中斷,「{e.FileName}」的傳輸已中止。",
                _ => $"「{e.FileName}」傳輸失敗。",
            };

            MainSnackbar.MessageQueue?.Enqueue(message);
            AppendLog(message);
            NotifyTransferEnded(e.Reason, message);
        });
    }

    private void OnFolderTransferEnded(object? sender, FolderTransferEndedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var peer = FindPeerItem(sender);
            var peerName = peer?.Name ?? (sender as PeerSession)?.RemoteName ?? string.Empty;
            CompleteTransfer(peer, new TransferRecord
            {
                Direction = e.Direction,
                Reason = e.Reason,
                Name = e.IsBatch ? $"{e.EntryCount} 個檔案" : e.FolderName,
                SavedPath = e.SavedFolderPath,
                IsFolder = true,
                PeerName = peerName,
            });

            var message = e.IsBatch
                ? e.Reason switch
                {
                    FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                        $"已收到 {peerName} 傳來的 {e.EntryCount} 個檔案,已儲存到 {_fileTransferSettings.DownloadFolder}。",
                    FileTransferEndReason.Completed => $"{e.EntryCount} 個檔案已傳送給 {peerName}。",
                    FileTransferEndReason.Rejected => $"{peerName} 拒絕接收這些檔案。",
                    FileTransferEndReason.Cancelled => $"已取消傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.CancelledByRemote => $"{peerName} 取消了傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.ConnectionClosed => $"與 {peerName} 的連線已中斷,傳輸已中止(已完成 {e.EntryCount} 個檔案)。",
                    _ => $"傳輸失敗(已完成 {e.EntryCount} 個檔案)。",
                }
                : e.Reason switch
                {
                    FileTransferEndReason.Completed when e.Direction == FileTransferDirection.Receiving =>
                        $"已收到 {peerName} 傳來的資料夾「{e.FolderName}」(共 {e.EntryCount} 個檔案),已儲存到 {_fileTransferSettings.DownloadFolder}。",
                    FileTransferEndReason.Completed => $"資料夾「{e.FolderName}」已傳送給 {peerName}(共 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.Rejected => $"{peerName} 拒絕接收資料夾「{e.FolderName}」。",
                    FileTransferEndReason.Cancelled => $"已取消資料夾「{e.FolderName}」的傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.CancelledByRemote => $"{peerName} 取消了資料夾「{e.FolderName}」的傳輸(已完成 {e.EntryCount} 個檔案)。",
                    FileTransferEndReason.ConnectionClosed => $"與 {peerName} 的連線已中斷,資料夾「{e.FolderName}」的傳輸已中止(已完成 {e.EntryCount} 個檔案)。",
                    _ => $"資料夾「{e.FolderName}」傳輸失敗(已完成 {e.EntryCount} 個檔案)。",
                };

            MainSnackbar.MessageQueue?.Enqueue(message);
            AppendLog(message);
            NotifyTransferEnded(e.Reason, message);
        });
    }

    /// <summary>傳輸結束時跳出系統匣通知(視窗在背景/縮到系統匣時特別有用),可在設定頁關閉通知。
    /// 使用者自己按取消的不另外通知。</summary>
    private void NotifyTransferEnded(FileTransferEndReason reason, string message)
    {
        if (reason == FileTransferEndReason.Cancelled)
        {
            return;
        }

        var title = reason == FileTransferEndReason.Completed ? "傳輸完成" : "傳輸未完成";
        ((App)Application.Current).ShowConnectionAlert(title, message);
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
}
