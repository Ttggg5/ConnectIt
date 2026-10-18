using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConnectIt.Wpf.Models;
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

    // 隨機播放的洗牌種子:跟觀看頁網址帶的 seed 同一個道理,同一輪隨機播放期間清單/上一部/下一部
    // 順序要固定,只有重新勾選「隨機播放」或重新開始分享時才換一組。
    private int _remoteShuffleSeed;

    // 自動播放下一部:主機端不一定真的在播放影片,所以「播完了」是用碼表外插的位置跟探測到的總長度
    // 比較出來的(見 HandleRemoteAutoplay),不是播放器的 ended 事件;判斷播完後直接切到下一部。

    // 影片庫目前顯示的卡片(已套用排序/隨機播放與搜尋關鍵字),以及正在顯示縮圖的「正在播放」影片。
    private List<RemoteVideoItem> _remoteVideoItems = [];
    private string? _remoteNowPlayingImagePath;

    private void RemoteControlModeToggle_Click(object sender, RoutedEventArgs e) =>
        SetRemoteControlEnabled(RemoteControlModeToggle.IsChecked == true);

    private void StartRemoteControlButton_Click(object sender, RoutedEventArgs e)
    {
        RemoteControlModeToggle.IsChecked = true;
        SetRemoteControlEnabled(true);
    }

    private void SetRemoteControlEnabled(bool enabled)
    {
        _videoStreaming.ControlState.SetEnabled(enabled);
        UpdateRemoteControlModeUi(enabled);
        RefreshRemoteControlUi();
    }

    /// <summary>遙控面板子頁:未啟用時顯示介紹與「開始遙控」,啟用後才顯示影片庫與控制面板。</summary>
    private void UpdateRemoteControlModeUi(bool enabled)
    {
        RemoteControlDetailPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RemoteControlIntroPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        RemoteControlModeLabel.Text = enabled ? "遙控中" : "遙控未啟用";
        RemoteControlCardStatusText.Text = enabled
            ? "遙控中:觀看端正跟著你的操作同步播放。"
            : "讓所有觀看端同步播放你選的影片,適合一起看片或簡報。";
    }

    private void RemoteControlBackButton_Click(object sender, RoutedEventArgs e)
    {
        VideoShareTabButton.IsChecked = true;
        SetActivePage(AppPage.Video);
    }

    private void RemoteControlSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_videoStreaming.IsRunning)
        {
            RefreshRemoteControlPlaylist();
        }
    }

    /// <summary>本機影片伺服器上某部影片的縮圖網址(跟觀看端網頁用的是同一個 /thumbnail/ 端點)。</summary>
    private string BuildLocalThumbnailUrl(string relativePath) =>
        (VideoServerBaseUrl ?? string.Empty) + "thumbnail/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    private void RemoteControlVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoManifestEntry entry })
        {
            return;
        }

        SelectRemoteVideo(entry.RelativePath);
    }

    private void SelectRemoteVideo(string relativePath)
    {
        _videoStreaming.ControlState.SetVideo(relativePath);
        UpdateRemoteDurationAsync(relativePath);
        RefreshRemoteControlUi();
    }

    /// <summary>遙控面板目前的播放清單順序:依面板上選的排序方式,勾了「隨機播放」就再用
    /// <see cref="_remoteShuffleSeed"/> 洗牌一次,跟觀看頁共用 <see cref="VideoStreamingService.GetDisplayOrder"/>。</summary>
    private List<VideoManifestEntry> GetRemoteControlPlaylist()
    {
        var manifest = _videoStreaming.Manifest;
        var sort = RemoteControlSortComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? tag
            : VideoServerSettingsService.DefaultSortValue;
        return _videoStreaming
            .GetDisplayOrder(sort, RemoteControlShuffleCheckBox.IsChecked == true, _remoteShuffleSeed)
            .Select(i => manifest[i])
            .ToList();
    }

    /// <summary>重建影片庫卡片:依目前的排序/隨機播放順序,再套用搜尋關鍵字過濾(只影響顯示,
    /// 上一部/下一部/自動播放仍依完整的播放清單順序)。</summary>
    private void RefreshRemoteControlPlaylist()
    {
        var keyword = RemoteControlSearchTextBox.Text?.Trim() ?? string.Empty;
        _remoteVideoItems = GetRemoteControlPlaylist()
            .Where(entry => keyword.Length == 0 || entry.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new RemoteVideoItem { Entry = entry, ThumbnailUrl = BuildLocalThumbnailUrl(entry.RelativePath) })
            .ToList();
        RemoteControlVideoListItemsControl.ItemsSource = _remoteVideoItems;
    }

    /// <summary>目前選片在播放清單裡往前/往後 <paramref name="delta"/> 格的影片;還沒選片時從第一部開始,
    /// 超出清單範圍回傳 null。</summary>
    private string? GetRemoteNeighborPath(int delta)
    {
        var playlist = GetRemoteControlPlaylist();
        if (playlist.Count == 0)
        {
            return null;
        }

        var currentPath = _videoStreaming.ControlState.Snapshot().VideoRelativePath;
        var currentIndex = playlist.FindIndex(m => m.RelativePath == currentPath);
        var nextIndex = currentIndex < 0 ? 0 : currentIndex + delta;
        return nextIndex >= 0 && nextIndex < playlist.Count ? playlist[nextIndex].RelativePath : null;
    }

    private void RemoteControlSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_videoStreaming.IsRunning)
        {
            RefreshRemoteControlPlaylist();
        }
    }

    private void RemoteControlShuffleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // 每次重新勾選都換一組種子,不然關掉再打開會得到跟上次一模一樣的「隨機」順序。
        if (RemoteControlShuffleCheckBox.IsChecked == true)
        {
            _remoteShuffleSeed = Random.Shared.Next();
        }

        RefreshRemoteControlPlaylist();
    }

    /// <summary>
    /// 由 <see cref="RefreshRemoteControlUi"/> 定期呼叫:目前選片(外插後的)位置走到探測出的總長度時,
    /// 勾了「自動播放下一部」而且清單還有下一部就直接切過去;否則把狀態定在片尾、改成暫停
    /// (不然碼表會一直往後算,超過影片長度)。總長度探測失敗(null)時無從判斷播完了沒有,
    /// 這種影片就不會自動接下一部。
    /// </summary>
    private void HandleRemoteAutoplay()
    {
        var snapshot = _videoStreaming.ControlState.Snapshot();
        if (!snapshot.Enabled ||
            !snapshot.IsPlaying ||
            _remoteDurationMs is not { } durationMs ||
            durationMs <= 0 ||
            _remoteDurationProbedForPath != snapshot.VideoRelativePath ||
            snapshot.PositionMs < durationMs)
        {
            return;
        }

        if (RemoteControlAutoplayCheckBox.IsChecked == true && GetRemoteNeighborPath(1) is { } next)
        {
            SelectRemoteVideo(next);
            return;
        }

        _videoStreaming.ControlState.SetPlaying(false);
        _videoStreaming.ControlState.Seek(durationMs);
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

        // 已經停在片尾時按播放就從頭開始,不然一按下去馬上又被判定成「播完了」。
        if (!snapshot.IsPlaying && _remoteDurationMs is > 0 && snapshot.PositionMs >= _remoteDurationMs.Value)
        {
            _videoStreaming.ControlState.Seek(0);
        }

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

    /// <summary>依遙控面板目前的播放清單順序(排序方式 + 隨機播放,見 <see cref="GetRemoteControlPlaylist"/>)
    /// 切到上一部/下一部,跟觀看端的上一部/下一部是同一種「在清單裡移動一格」的概念。</summary>
    private void RemoteControlSkip(int delta)
    {
        if (GetRemoteNeighborPath(delta) is { } path)
        {
            SelectRemoteVideo(path);
        }
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
        if (!_videoStreaming.IsRunning)
        {
            return;
        }

        // 自動播放下一部要在背景持續判斷(使用者離開遙控面板子頁也照樣會接下一部),
        // 畫面本身則只有子頁顯示中才需要重畫。
        HandleRemoteAutoplay();

        if (_activePage != AppPage.RemoteControl)
        {
            return;
        }

        var snapshot = _videoStreaming.ControlState.Snapshot();
        var entry = snapshot.VideoRelativePath is { } path
            ? _videoStreaming.Manifest.FirstOrDefault(m => m.RelativePath == path)
            : null;

        RemoteControlNowPlayingText.Text = entry?.Name ?? "尚未選擇影片";
        if (!Equals(RemoteControlVideoListItemsControl.Tag, snapshot.VideoRelativePath))
        {
            RemoteControlVideoListItemsControl.Tag = snapshot.VideoRelativePath;
        }

        if (_remoteNowPlayingImagePath != entry?.RelativePath)
        {
            _remoteNowPlayingImagePath = entry?.RelativePath;
            RemoteControlNowPlayingImage.Source = entry is null
                ? null
                : new BitmapImage(new Uri(BuildLocalThumbnailUrl(entry.RelativePath)));
        }
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
