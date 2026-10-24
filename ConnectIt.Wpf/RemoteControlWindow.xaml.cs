using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 遙控器視窗(見 <see cref="RemoteControlController"/>),外觀做成一支實體遙控器,跟 Android 端的遙控器畫面同一套設計。
/// 每顆按鍵的 CommandParameter 是要送出的按鍵(組合鍵用「+」連接,例如「Alt+Tab」)。
/// 對方是電腦:方向盤 = 方向鍵、OK = Enter,右邊搖桿 = PageUp/PageDown(簡報),另有返回/開始/切換視窗與功能鍵;
/// 視窗有焦點時鍵盤的方向鍵、空白鍵、PageUp/PageDown 也會直接送給對方(可以當簡報翻頁器)。
/// 對方是 Android(只能送媒體鍵與音量):方向盤上下 = 音量、左右 = 上一首/下一首、OK = 播放/暫停,功能鍵隱藏。
/// 關閉視窗(或按電源鍵)就結束遙控。
/// </summary>
public partial class RemoteControlWindow : Window
{
    private readonly RemoteControlController _controller;
    private readonly bool _isWindows;
    private bool _closedByController;

    public RemoteControlWindow(RemoteControlController controller, string peerName, string? platform)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        _controller = controller;
        _isWindows = platform != "android";
        Title = $"遙控「{peerName}」 - ConnectIt";
        PeerNameText.Text = peerName;

        if (_isWindows)
        {
            HintText.Text = "視窗有焦點時,方向鍵、空白鍵、PageUp/PageDown 也會直接送給對方(可以當簡報翻頁器)。";
        }
        else
        {
            ConfigureForAndroid();
            HintText.Text = "Android 只支援媒體鍵與音量(系統不允許 App 模擬其他按鍵)。";
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

    /// <summary>Android 受控端只能送媒體鍵與音量:方向盤、圓鍵、右邊搖桿改成媒體用途,隱藏功能鍵。</summary>
    private void ConfigureForAndroid()
    {
        DPadUp.CommandParameter = RemoteKeys.VolumeUp;
        DPadDown.CommandParameter = RemoteKeys.VolumeDown;
        DPadLeft.CommandParameter = RemoteKeys.Previous;
        DPadRight.CommandParameter = RemoteKeys.Next;
        DPadOk.CommandParameter = RemoteKeys.PlayPause;
        DPadOkText.Visibility = Visibility.Collapsed;
        DPadOkIcon.Visibility = Visibility.Visible;
        DPadUpHint.Text = "音量+";
        DPadDownHint.Text = "音量−";
        DPadUpHint.Visibility = Visibility.Visible;
        DPadDownHint.Visibility = Visibility.Visible;

        SetNavKey(NavKey1, NavKey1Text, NavKey1Label, PackIconKind.SkipPrevious, "上一首", RemoteKeys.Previous);
        SetNavKey(NavKey2, NavKey2Text, NavKey2Label, PackIconKind.Stop, "停止", RemoteKeys.Stop);
        SetNavKey(NavKey3, NavKey3Text, NavKey3Label, PackIconKind.SkipNext, "下一首", RemoteKeys.Next);

        RightRockerUp.CommandParameter = RemoteKeys.Next;
        RightRockerDown.CommandParameter = RemoteKeys.Previous;
        RightRockerLabel.Text = "曲";
        FunctionKeysPanel.Visibility = Visibility.Collapsed;
        // 上一首/停止/下一首已經在方向盤下方那排,播放鍵下面的小鍵就不重複了。
        SmallTransportKeys.Visibility = Visibility.Collapsed;
    }

    private static void SetNavKey(Button button, TextBlock text, TextBlock label, PackIconKind icon, string caption, string key)
    {
        text.Visibility = Visibility.Collapsed;
        button.Content = new PackIcon { Kind = icon, Width = 24, Height = 24 };
        label.Text = caption;
        button.CommandParameter = key;
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string keys })
        {
            Press(keys.Split('+', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Press(params string[] keys)
    {
        _controller.SendShortcut(keys);
        FlashLed();
    }

    /// <summary>指示燈:每按一個鍵亮一下(就像實體遙控器送出紅外線時的那顆燈)。</summary>
    private void FlashLed()
    {
        var lit = (Color)ColorConverter.ConvertFromString("#22D3EE");
        var off = (Color)ColorConverter.ConvertFromString("#3A4152");
        var colorFlash = new ColorAnimationUsingKeyFrames();
        colorFlash.KeyFrames.Add(new DiscreteColorKeyFrame(lit, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        colorFlash.KeyFrames.Add(new LinearColorKeyFrame(lit, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        colorFlash.KeyFrames.Add(new LinearColorKeyFrame(off, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
        StatusLedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorFlash);

        var glow = new DoubleAnimationUsingKeyFrames();
        glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
        StatusLedGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glow);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isWindows)
        {
            return;
        }

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
            Press(key);
            e.Handled = true;
        }
    }
}
