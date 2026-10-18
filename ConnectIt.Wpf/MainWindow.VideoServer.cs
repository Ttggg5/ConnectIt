using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using QRCoder;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // ===================== 影片頁:觀看 / 分享 分頁 =====================

    private void VideoTabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || VideoWatchPanel is null || VideoSharePanel is null)
        {
            return;
        }

        var showShare = VideoShareTabButton.IsChecked == true;
        VideoWatchPanel.Visibility = showShare ? Visibility.Collapsed : Visibility.Visible;
        VideoSharePanel.Visibility = showShare ? Visibility.Visible : Visibility.Collapsed;
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

    private string BuildVideoFileFilter()
    {
        var extensions = string.Join(
            ';', VideoLibraryScanner.VideoExtensions.Concat(_videoServerSettings.ExtraExtensions).Select(ext => $"*{ext}"));
        return $"影片檔案|{extensions}";
    }

    private void StartVideoServer(string path)
    {
        var name = MyDeviceName;

        var options = new VideoServerPlaybackOptions
        {
            DefaultSort = _videoServerSettings.DefaultSort,
            AutoplayNext = _videoServerSettings.AutoplayNext,
            Shuffle = _videoServerSettings.Shuffle,
            DefaultVolumePercent = _videoServerSettings.DefaultVolume,
            DefaultSpeed = _videoServerSettings.DefaultSpeed,
            AutoplayCountdownSeconds = _videoServerSettings.AutoplayCountdownSeconds,
            ExtraExtensions = _videoServerSettings.ExtraExtensions,
        };
        _videoStreaming.Start(path, name, options);
        if (!_videoStreaming.IsRunning)
        {
            MainSnackbar.MessageQueue?.Enqueue("沒有找到可以分享的影片,請換一個資料夾或檔案試試。");
            return;
        }

        _videoDiscovery.Advertise(name, _videoStreaming.Port);

        var sharedName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        VideoServerStatusText.Text = $"分享中:{sharedName}・共 {_videoStreaming.Manifest.Count} 部影片";
        StreamVideoButton.Visibility = Visibility.Collapsed;
        ShareVideoFolderButton.Visibility = Visibility.Collapsed;
        VideoShareIdleHintText.Visibility = Visibility.Collapsed;
        StopVideoServerButton.Visibility = Visibility.Visible;
        VideoShareRunningDot.Visibility = Visibility.Visible;
        ShowVideoServerAddress();

        RemoteControlCard.Visibility = Visibility.Visible;
        RemoteControlSubtitleText.Text = $"{sharedName}・{_videoStreaming.Manifest.Count} 部影片";
        RemoteControlModeToggle.IsChecked = false;
        UpdateRemoteControlModeUi(enabled: false);
        // 排序/隨機播放/自動播放的初始值沿用設定頁的影片伺服器預設值,之後在遙控面板上的調整
        // 只影響這一次分享,不會寫回設定。
        _remoteShuffleSeed = Random.Shared.Next();
        _remoteAutoplayCountdownSeconds = options.AutoplayCountdownSeconds;
        CancelRemoteAutoplay();
        SelectComboBoxItemByTag(RemoteControlSortComboBox, options.DefaultSort);
        RemoteControlShuffleCheckBox.IsChecked = options.Shuffle;
        RemoteControlAutoplayCheckBox.IsChecked = options.AutoplayNext;
        RemoteControlSearchTextBox.Text = string.Empty;
        RefreshRemoteControlPlaylist();
        RemoteControlSpeedComboBox.SelectedIndex = 1;
        RemoteControlVolumeSlider.Value = 1;
        RemoteControlMuteIcon.Kind = PackIconKind.VolumeHigh;
        _remoteDurationMs = null;
        _remoteDurationProbedForPath = null;
        RemoteControlTimelineSlider.IsEnabled = false;
        RemoteControlTimelineSlider.Value = 0;
    }

    private void StopVideoServerButton_Click(object sender, RoutedEventArgs e)
    {
        _videoDiscovery.StopAdvertising();
        _videoStreaming.Stop();

        VideoServerStatusText.Text = "尚未開啟";
        StreamVideoButton.Visibility = Visibility.Visible;
        ShareVideoFolderButton.Visibility = Visibility.Visible;
        VideoShareIdleHintText.Visibility = Visibility.Visible;
        StopVideoServerButton.Visibility = Visibility.Collapsed;
        VideoShareRunningDot.Visibility = Visibility.Collapsed;
        VideoShareInfoCard.Visibility = Visibility.Collapsed;
        VideoServerQrImage.Source = null;

        CancelRemoteAutoplay();
        RemoteControlCard.Visibility = Visibility.Collapsed;
        RemoteControlVideoListItemsControl.ItemsSource = null;
        RemoteControlVideoListItemsControl.Tag = null;
        _remoteVideoItems = [];

        if (_activePage == AppPage.RemoteControl)
        {
            SetActivePage(AppPage.Video);
        }
    }

    /// <summary>本機影片伺服器給其他裝置用的網址(綁定的第一個區網位址);沒有在跑時為 null。</summary>
    private string? VideoServerBaseUrl =>
        _videoStreaming.IsRunning && _videoStreaming.BoundAddresses.Count > 0
            ? $"http://{_videoStreaming.BoundAddresses[0]}:{_videoStreaming.Port}/"
            : null;

    /// <summary>顯示分享網址與對應的 QR Code,讓沒有安裝 App 的裝置用瀏覽器(或相機掃描)直接觀看。</summary>
    private void ShowVideoServerAddress()
    {
        if (VideoServerBaseUrl is not { } url)
        {
            VideoShareInfoCard.Visibility = Visibility.Collapsed;
            return;
        }

        VideoServerUrlTextBox.Text = url;
        VideoServerQrImage.Source = CreateQrCodeImage(url);
        VideoShareInfoCard.Visibility = Visibility.Visible;
    }

    private static BitmapImage? CreateQrCodeImage(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);

            var image = new BitmapImage();
            using var stream = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void CopyVideoServerUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (VideoServerBaseUrl is not { } url)
        {
            return;
        }

        try
        {
            Clipboard.SetText(url);
            MainSnackbar.MessageQueue?.Enqueue("已複製網址。");
        }
        catch (Exception ex)
        {
            AppendLog($"複製到剪貼簿失敗:{ex.Message}");
        }
    }

    private void OpenRemoteControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_videoStreaming.IsRunning)
        {
            SetActivePage(AppPage.RemoteControl);
        }
    }
}
