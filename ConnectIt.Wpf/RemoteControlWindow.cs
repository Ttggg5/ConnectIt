using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 遙控器視窗:不用開螢幕鏡像,直接按按鈕控制另一台裝置的媒體播放、音量、簡報與常用快捷鍵
/// (見 <see cref="RemoteControlController"/>)。視窗有焦點時,鍵盤的方向鍵、空白鍵、PageUp/PageDown 也會轉送給對方,
/// 拿來當簡報翻頁器很方便。關閉視窗就結束遙控。
/// </summary>
public sealed class RemoteControlWindow : Window
{
    private readonly RemoteControlController _controller;
    private bool _closedByController;

    public RemoteControlWindow(RemoteControlController controller, string peerName, string? platform)
    {
        _controller = controller;
        Title = $"遙控「{peerName}」 - ConnectIt";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brand.Font");
        SetResourceReference(BackgroundProperty, "Brand.Brush.Background");
        SetResourceReference(ForegroundProperty, "Brand.Brush.Text");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = $"正在遙控「{peerName}」",
            Style = (Style)FindResource("Brand.Text.Title"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var isWindows = platform != "android";
        root.Children.Add(new TextBlock
        {
            Text = isWindows
                ? "視窗有焦點時,方向鍵、空白鍵、PageUp/PageDown 也會直接送給對方(可以當簡報翻頁器)。"
                : "Android 只支援媒體鍵與音量(系統不允許 App 模擬其他按鍵)。",
            Style = (Style)FindResource("Brand.Text.Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        AddSection(root, "媒體與音量", RemoteShortcuts.Media);
        if (isWindows)
        {
            AddSection(root, "簡報", RemoteShortcuts.Presentation);
            AddSection(root, "快捷鍵", RemoteShortcuts.System);
        }

        var stop = new Button
        {
            Content = "結束遙控",
            Style = (Style)FindResource("Brand.Button.Danger"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
        };
        stop.Click += (_, _) => Close();
        root.Children.Add(stop);
        Content = root;

        if (isWindows)
        {
            PreviewKeyDown += OnPreviewKeyDown;
        }

        Closed += (_, _) =>
        {
            if (!_closedByController)
            {
                _controller.Stop();
            }
        };
    }

    /// <summary>遙控由控制器結束(對方結束、連線中斷)時關閉視窗,不再回頭呼叫 Stop。</summary>
    public void CloseFromController()
    {
        _closedByController = true;
        Close();
    }

    private void AddSection(StackPanel root, string title, IEnumerable<RemoteShortcut> shortcuts)
    {
        root.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Brand.Text.Overline"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var panel = new WrapPanel { Margin = new Thickness(-4, 0, -4, 12) };
        foreach (var shortcut in shortcuts)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new PackIcon { Kind = shortcut.Icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = shortcut.Label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = content,
                Style = (Style)FindResource("Brand.Button.Ghost"),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4),
                Focusable = false,
                ToolTip = string.Join(" + ", shortcut.Keys),
            };
            var keys = shortcut.Keys;
            button.Click += (_, _) => _controller.SendShortcut(keys);
            panel.Children.Add(button);
        }

        root.Children.Add(panel);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Space => " ",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => null,
        };
        if (key != null)
        {
            _controller.SendKey(key);
            e.Handled = true;
        }
    }
}
