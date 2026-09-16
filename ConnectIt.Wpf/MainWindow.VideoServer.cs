using System.Windows;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
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
        var name = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text)
            ? Environment.MachineName
            : DeviceNameTextBox.Text.Trim();

        var options = new VideoServerPlaybackOptions
        {
            DefaultSort = _videoServerSettings.DefaultSort,
            AutoplayNext = _videoServerSettings.AutoplayNext,
            DefaultVolumePercent = _videoServerSettings.DefaultVolume,
            DefaultSpeed = _videoServerSettings.DefaultSpeed,
            AutoplayCountdownSeconds = _videoServerSettings.AutoplayCountdownSeconds,
            ExtraExtensions = _videoServerSettings.ExtraExtensions,
        };
        _videoStreaming.Start(path, name, options);
        if (!_videoStreaming.IsRunning)
        {
            return;
        }

        _videoDiscovery.Advertise(name, _videoStreaming.Port);

        VideoServerStatusText.Text = $"執行中,共 {_videoStreaming.Manifest.Count} 部影片(連接埠 {_videoStreaming.Port})";
        StreamVideoButton.Visibility = Visibility.Collapsed;
        ShareVideoFolderButton.Visibility = Visibility.Collapsed;
        StopVideoServerButton.Visibility = Visibility.Visible;

        RemoteControlCard.Visibility = Visibility.Visible;
        RemoteControlModeToggle.IsChecked = false;
        RemoteControlDetailPanel.Visibility = Visibility.Collapsed;
        RemoteControlVideoListItemsControl.ItemsSource = _videoStreaming.Manifest;
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
        StopVideoServerButton.Visibility = Visibility.Collapsed;

        RemoteControlCard.Visibility = Visibility.Collapsed;
        RemoteControlVideoListItemsControl.ItemsSource = null;
    }
}
