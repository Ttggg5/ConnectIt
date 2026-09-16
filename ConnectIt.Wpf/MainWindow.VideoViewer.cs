using System.Windows;
using System.Windows.Controls;
using ConnectIt.Wpf.Models;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // ===================== 影片伺服器(觀看端:發現與瀏覽) =====================

    private DiscoveredDevice? _activeVideoServer;

    // 網頁內(例如影片播放器)按下全螢幕時,WebView2 預設只會讓該元素填滿 WebView2 控制項本身的範圍,
    // 不會真的變成視窗全螢幕,所以旁邊的導覽列、返回列還是看得到。這裡監聽 ContainsFullScreenElementChanged,
    // 手動把導覽列/返回列收起來並讓視窗真正全螢幕,離開全螢幕時再還原。
    private bool _isWebViewFullScreen;
    private WindowState _preFullScreenWindowState;
    private WindowStyle _preFullScreenWindowStyle;
    private ResizeMode _preFullScreenResizeMode;

    private void OnVideoServerDiscovered(object? sender, DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _videoServers.FirstOrDefault(d => d.Key == device.Key);
            if (existing != null)
            {
                var index = _videoServers.IndexOf(existing);
                _videoServers[index] = device;
            }
            else
            {
                _videoServers.Add(device);
                AppendLog($"找到影片伺服器:{device.DisplayName} ({device.Address}:{device.Port})");
            }
        });
    }

    private void OnVideoServerRemoved(object? sender, string instanceName)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _videoServers.FirstOrDefault(d => d.Key == instanceName);
            if (existing != null)
            {
                _videoServers.Remove(existing);
                AppendLog($"影片伺服器已離線:{existing.DisplayName}");
            }
        });
    }

    private void UpdateVideoServersEmptyState()
    {
        VideoServersEmptyStateStackPanel.Visibility = _videoServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>點「觀看」直接用內嵌瀏覽器開啟對方影片伺服器的網站(首頁清單/單片直接跳轉、觀看頁
    /// 都是伺服器端組好的 HTML,見 <see cref="VideoStreamingService"/>),App 這邊不用自己解析
    /// manifest 或實作播放器 UI。</summary>
    private void WatchVideoServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredDevice server })
        {
            return;
        }

        _activeVideoServer = server;
        VideoWebViewTitleText.Text = server.DisplayName;
        VideoWebView.Source = new Uri($"http://{server.Address}:{server.Port}/");

        SetActivePage(AppPage.VideoWebsite);
    }

    private void VideoWebViewBackButton_Click(object sender, RoutedEventArgs e)
    {
        SetActivePage(AppPage.Video);
    }

    private void VideoWebView_CoreWebView2InitializationCompleted(object sender,
        Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess && VideoWebView.CoreWebView2 != null)
        {
            VideoWebView.CoreWebView2.ContainsFullScreenElementChanged += VideoWebView_ContainsFullScreenElementChanged;
        }
    }

    /// <summary>網頁(播放器)按下全螢幕/離開全螢幕時觸發,負責把 App 本身的 UI(導覽列、返回列、
    /// 視窗邊框)一併收起來/還原,讓 WebView2 裡的內容真正填滿整個螢幕。</summary>
    private void VideoWebView_ContainsFullScreenElementChanged(object? sender, object e)
    {
        if (VideoWebView.CoreWebView2 == null)
        {
            return;
        }

        if (VideoWebView.CoreWebView2.ContainsFullScreenElement)
        {
            EnterWebViewFullScreen();
        }
        else
        {
            RestoreFromWebViewFullScreen();
        }
    }

    private void EnterWebViewFullScreen()
    {
        if (_isWebViewFullScreen)
        {
            return;
        }

        _isWebViewFullScreen = true;
        _preFullScreenWindowState = WindowState;
        _preFullScreenWindowStyle = WindowStyle;
        _preFullScreenResizeMode = ResizeMode;

        NavColorZone.Visibility = Visibility.Collapsed;
        VideoWebViewHeader.Visibility = Visibility.Collapsed;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
    }

    private void RestoreFromWebViewFullScreen()
    {
        if (!_isWebViewFullScreen)
        {
            return;
        }

        _isWebViewFullScreen = false;

        NavColorZone.Visibility = Visibility.Visible;
        VideoWebViewHeader.Visibility = Visibility.Visible;

        WindowState = _preFullScreenWindowState;
        WindowStyle = _preFullScreenWindowStyle;
        ResizeMode = _preFullScreenResizeMode;
    }
}
