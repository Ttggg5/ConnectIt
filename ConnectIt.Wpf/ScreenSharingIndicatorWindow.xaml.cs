using System.Windows;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 分享螢幕期間浮在最上層的提示條(類似 TeamViewer/AnyDesk 被連線時的面板):顯示誰在看、能不能操控、
/// 是不是無人值守(免確認)進來的,並提供「中斷」。由 MainWindow 依螢幕鏡像狀態建立/更新/關閉。
/// </summary>
public partial class ScreenSharingIndicatorWindow : Window
{
    private readonly Action _onStop;
    private readonly Action _onOpenMain;
    private bool _positioned;

    public ScreenSharingIndicatorWindow(Action onStop, Action onOpenMain)
    {
        InitializeComponent();
        _onStop = onStop;
        _onOpenMain = onOpenMain;
        SizeChanged += (_, _) => PositionAtTopCenter();
    }

    public void Update(string peerName, bool allowControl, bool unattended)
    {
        ModeIcon.Kind = allowControl ? PackIconKind.CursorDefaultClick : PackIconKind.MonitorEye;
        TitleText.Text = allowControl ? $"「{peerName}」正在操控這台電腦" : $"「{peerName}」正在觀看這台電腦的螢幕";
        DetailText.Text = unattended ? "無人值守存取(免確認連入)· 隨時可以按「中斷」" : "隨時可以按「中斷」停止分享";
    }

    /// <summary>第一次量好大小後放到主螢幕工作區的正上方;之後使用者拖到哪就留在哪。</summary>
    private void PositionAtTopCenter()
    {
        if (_positioned || ActualWidth <= 0)
        {
            return;
        }

        _positioned = true;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Top;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _onStop();

    private void OpenMainButton_Click(object sender, RoutedEventArgs e) => _onOpenMain();
}
