using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // ===================== 遙控模式(主機端控制面板) =====================

    // 遙控面板的時間軸需要知道目前選片的總長度,但探測(VideoDurationProbe)是非同步的,
    // 用這兩個欄位記著「探測結果屬於哪一支影片」,避免使用者很快切了好幾次片之後,
    // 舊的探測結果晚到卻蓋掉了新選的那一部。拖曳時間軸中(_isDraggingRemoteTimeline)則暫停
    // 定時重畫覆蓋使用者正在拖曳的值,道理跟音量滑桿不用防一樣——那個滑桿是純粹單向下指令,
    // 時間軸則需要跟著播放進度自己走動,兩者行為不同。
    private long? _remoteDurationMs;
    private string? _remoteDurationProbedForPath;
    private bool _isDraggingRemoteTimeline;

    // 遙控面板的進度顯示要能在播放中自己走動(不是只有下指令那一刻才刷新),所以用一個
    // 輕量的本機計時器(不是網路輪詢,跟觀眾端打 `/control/state` 不同)定期重畫。
    private readonly DispatcherTimer _remoteControlPollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void RemoteControlModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = RemoteControlModeToggle.IsChecked == true;
        _videoStreaming.ControlState.SetEnabled(enabled);
        RemoteControlDetailPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshRemoteControlUi();
    }

    private void RemoteControlVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoManifestEntry entry })
        {
            return;
        }

        _videoStreaming.ControlState.SetVideo(entry.RelativePath);
        UpdateRemoteDurationAsync(entry.RelativePath);
        RefreshRemoteControlUi();
    }

    /// <summary>探測目前選片的總長度(見 <see cref="VideoDurationProbe"/>),供時間軸畫用。非同步,
    /// 用 <see cref="_remoteDurationProbedForPath"/> 確保探測完成前使用者又切了別部影片時,
    /// 晚到的結果不會蓋掉新選的那一部。</summary>
    private async void UpdateRemoteDurationAsync(string relativePath)
    {
        _remoteDurationProbedForPath = relativePath;
        _remoteDurationMs = null;
        RefreshRemoteControlUi();

        var duration = await _videoStreaming.GetDurationMsAsync(relativePath).ConfigureAwait(true);

        if (_remoteDurationProbedForPath == relativePath)
        {
            _remoteDurationMs = duration;
            RefreshRemoteControlUi();
        }
    }

    private void RemoteControlPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _videoStreaming.ControlState.Snapshot();
        _videoStreaming.ControlState.SetPlaying(!snapshot.IsPlaying);
        RefreshRemoteControlUi();
    }

    private void RemoteControlRewindButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _videoStreaming.ControlState.Snapshot();
        _videoStreaming.ControlState.Seek(Math.Max(0, snapshot.PositionMs - 10_000));
        RefreshRemoteControlUi();
    }

    private void RemoteControlForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _videoStreaming.ControlState.Snapshot();
        _videoStreaming.ControlState.Seek(snapshot.PositionMs + 10_000);
        RefreshRemoteControlUi();
    }

    /// <summary>依 <see cref="VideoStreamingService.Manifest"/> 的原始順序切到上一部/下一部,跟一般
    /// 觀看端的上一部/下一部是同一種「在清單裡移動一格」的概念——遙控面板沒有排序下拉選單,
    /// 直接用 manifest 原始順序,不像觀看端會依使用者選的排序方式決定順序。</summary>
    private void RemoteControlSkip(int delta)
    {
        var manifest = _videoStreaming.Manifest;
        if (manifest.Count == 0)
        {
            return;
        }

        var currentPath = _videoStreaming.ControlState.Snapshot().VideoRelativePath;
        var currentIndex = manifest.ToList().FindIndex(m => m.RelativePath == currentPath);
        var nextIndex = (currentIndex >= 0 ? currentIndex : 0) + delta;
        if (nextIndex < 0 || nextIndex >= manifest.Count)
        {
            return;
        }

        _videoStreaming.ControlState.SetVideo(manifest[nextIndex].RelativePath);
        UpdateRemoteDurationAsync(manifest[nextIndex].RelativePath);
        RefreshRemoteControlUi();
    }

    private void RemoteControlPreviousButton_Click(object sender, RoutedEventArgs e) => RemoteControlSkip(-1);

    private void RemoteControlNextButton_Click(object sender, RoutedEventArgs e) => RemoteControlSkip(1);

    private void RemoteControlSpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteControlSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            _videoStreaming.ControlState.SetPlaybackRate(rate);
        }
    }

    private void RemoteControlMuteButton_Click(object sender, RoutedEventArgs e)
    {
        var muted = !_videoStreaming.ControlState.Snapshot().Muted;
        _videoStreaming.ControlState.SetMuted(muted);
        RemoteControlMuteIcon.Kind = muted ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
    }

    private void RemoteControlVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _videoStreaming.ControlState.SetVolume(e.NewValue);
        if (e.NewValue > 0 && _videoStreaming.ControlState.Snapshot().Muted)
        {
            _videoStreaming.ControlState.SetMuted(false);
            RemoteControlMuteIcon.Kind = PackIconKind.VolumeHigh;
        }
    }

    private void RemoteControlTimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRemoteTimeline = true;
    }

    private void RemoteControlTimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRemoteTimeline = false;
        if (RemoteControlTimelineSlider.IsEnabled)
        {
            _videoStreaming.ControlState.Seek((long)RemoteControlTimelineSlider.Value);
        }

        RefreshRemoteControlUi();
    }

    /// <summary>把 <see cref="PlaybackControlState"/> 目前的快照套用到遙控面板上——不管是使用者在這裡
    /// 按下按鈕之後,還是計時器定期重畫讓進度看起來會自己走動,都走這一個方法。</summary>
    private void RefreshRemoteControlUi()
    {
        if (!_videoStreaming.IsRunning || RemoteControlCard.Visibility != Visibility.Visible)
        {
            return;
        }

        var snapshot = _videoStreaming.ControlState.Snapshot();
        var entry = snapshot.VideoRelativePath is { } path
            ? _videoStreaming.Manifest.FirstOrDefault(m => m.RelativePath == path)
            : null;

        RemoteControlNowPlayingText.Text = entry?.Name ?? "尚未選擇影片";
        RemoteControlPlayPauseIcon.Kind = snapshot.IsPlaying ? PackIconKind.Pause : PackIconKind.Play;

        // 拖曳時間軸中就不要被這裡(定時器/其他指令觸發)的重畫蓋掉使用者正在拖曳的值,
        // 放開滑鼠後(見 PreviewMouseLeftButtonUp)才會送出 Seek、接著才會再次呼叫這裡刷新。
        if (!_isDraggingRemoteTimeline)
        {
            var durationMs = _remoteDurationMs;
            RemoteControlTimelineSlider.IsEnabled = durationMs is > 0;
            RemoteControlTimelineSlider.Maximum = durationMs is > 0 ? durationMs.Value : 1;
            RemoteControlTimelineSlider.Value = Math.Min(snapshot.PositionMs, RemoteControlTimelineSlider.Maximum);
            RemoteControlPositionText.Text = FormatDuration(snapshot.PositionMs) +
                (durationMs is > 0 ? " / " + FormatDuration(durationMs.Value) : string.Empty);
        }
    }

    private static string FormatDuration(long ms)
    {
        var totalSeconds = Math.Max(0, ms) / 1000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00}";
    }
}
