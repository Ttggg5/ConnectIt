using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

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

    private void ConnectedViewRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (_connection.IsFileTransferActive || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropOverlayText.Text = $"放開以傳送給 {ConnectedDeviceNameText.Text}";
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
        _lastReceivedRecord = null;
        FileTransferNameText.Text = fileName;
        FileTransferIcon.Kind = PackIconKind.SwapVertical;
        FileTransferProgressBar.Value = 0;
        FileTransferProgressBar.Visibility = Visibility.Visible;
        FileTransferStatusText.Text = status;
        FileTransferDoneActions.Visibility = Visibility.Collapsed;
        CancelFileTransferButton.Visibility = Visibility.Visible;
        DismissFileTransferButton.Visibility = Visibility.Collapsed;
        FileTransferCard.Visibility = Visibility.Visible;
    }

    private void HideFileTransferPanel()
    {
        _lastReceivedRecord = null;
        FileTransferCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>最近一次「接收成功」的紀錄;傳輸卡片在完成狀態時的「開啟」/「在資料夾中顯示」就是作用在它上面。</summary>
    private TransferRecord? _lastReceivedRecord;

    /// <summary>傳輸結束時呼叫:寫入傳輸紀錄;接收成功的話把傳輸卡片切成「完成」狀態並提供開啟按鈕,
    /// 其他情況(傳送完成、失敗、取消)收起卡片,結果由 Snackbar 告知。</summary>
    private void CompleteTransfer(TransferRecord record)
    {
        _transferHistory.Insert(0, record);

        if (!record.CanOpen)
        {
            HideFileTransferPanel();
            return;
        }

        _lastReceivedRecord = record;
        FileTransferNameText.Text = record.Name;
        FileTransferIcon.Kind = PackIconKind.CheckCircle;
        FileTransferProgressBar.Visibility = Visibility.Collapsed;
        FileTransferStatusText.Text = $"已儲存到 {(record.IsFolder ? record.SavedPath : Path.GetDirectoryName(record.SavedPath))}";
        OpenReceivedButtonText.Text = record.IsFolder ? "開啟資料夾" : "開啟檔案";
        ShowReceivedInFolderButton.Visibility = record.IsFolder ? Visibility.Collapsed : Visibility.Visible;
        FileTransferDoneActions.Visibility = Visibility.Visible;
        CancelFileTransferButton.Visibility = Visibility.Collapsed;
        DismissFileTransferButton.Visibility = Visibility.Visible;
        FileTransferCard.Visibility = Visibility.Visible;
    }

    private void DismissFileTransferButton_Click(object sender, RoutedEventArgs e) => HideFileTransferPanel();

    private void OpenReceivedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReceivedRecord != null)
        {
            OpenTransferRecord(_lastReceivedRecord);
        }
    }

    private void ShowReceivedInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReceivedRecord != null)
        {
            ShowTransferRecordInFolder(_lastReceivedRecord);
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
            CompleteTransfer(new TransferRecord
            {
                Direction = e.Direction,
                Reason = e.Reason,
                Name = e.FileName,
                SavedPath = e.SavedFilePath,
                IsFolder = false,
            });

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
            CompleteTransfer(new TransferRecord
            {
                Direction = e.Direction,
                Reason = e.Reason,
                Name = e.IsBatch ? $"{e.EntryCount} 個檔案" : e.FolderName,
                SavedPath = e.SavedFolderPath,
                IsFolder = true,
            });

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
}
