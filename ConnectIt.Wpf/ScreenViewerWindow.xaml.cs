using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf;

/// <summary>
/// 觀看對方螢幕的獨立視窗(可以自由縮放、全螢幕,不佔用主視窗)。對方允許操控時,
/// 在畫面上的滑鼠/滾輪/鍵盤操作會轉送給對方:
/// - 對方是電腦:原樣轉送滑鼠左/右/中鍵、移動、滾輪與按鍵(含 Ctrl/Alt/Shift 組合鍵),文字以 Unicode 送出。
/// - 對方是 Android:按住左鍵拖曳 = 手指滑動、點一下 = 點擊、右鍵 = 返回;工具列另有主畫面/最近使用。
/// F11 切換全螢幕(不會轉送給對方)。
/// </summary>
public partial class ScreenViewerWindow : Window
{
    /// <summary>沒按著按鈕的滑鼠移動送出的最小間隔,太密集只會塞爆連線。</summary>
    private const long HoverMoveIntervalMs = 25;

    private readonly ScreenMirrorController _controller;
    private ScreenMirrorStatus _status;
    private WriteableBitmap? _bitmap;
    private DecodedScreenFrame? _pendingFrame;
    private int _renderScheduled;
    private bool _closedByController;
    private long _lastMoveSentAt;
    private readonly HashSet<string> _heldKeys = [];
    private WindowState _restoreState = WindowState.Normal;

    public ScreenViewerWindow(ScreenMirrorController controller, string peerName, ScreenMirrorStatus status)
    {
        InitializeComponent();
        _controller = controller;
        _status = status;
        TitleText.Text = $"「{peerName}」的螢幕";
        Title = $"{peerName} 的螢幕 - ConnectIt";
        WaitingText.Text = $"正在等待「{peerName}」的畫面…";
        ApplyStatus(status);

        _controller.FrameDecoded += OnFrameDecoded;
        _controller.FpsChanged += OnFpsChanged;
        Loaded += (_, _) => ScreenHost.Focus();
        Closed += (_, _) =>
        {
            _controller.FrameDecoded -= OnFrameDecoded;
            _controller.FpsChanged -= OnFpsChanged;
            Interlocked.Exchange(ref _pendingFrame, null)?.Release();
        };
    }

    private bool CanControl => _status.AllowControl;

    private bool IsRemoteAndroid => _status.Platform == "android";

    /// <summary>對方送來新的畫面資訊(尺寸、能不能操控、平台)。必須在 UI 執行緒呼叫。</summary>
    public void ApplyStatus(ScreenMirrorStatus status)
    {
        var lostControl = _status.AllowControl && !status.AllowControl;
        _status = status;
        if (lostControl)
        {
            ReleaseHeldKeys();
        }

        ControlStateIcon.Kind = CanControl ? PackIconKind.CursorDefaultClick : PackIconKind.Eye;
        ControlStateText.Text = CanControl ? "可以操控" : "僅觀看";
        AndroidNavPanel.Visibility = CanControl && IsRemoteAndroid ? Visibility.Visible : Visibility.Collapsed;
        ScreenImage.Cursor = CanControl ? Cursors.Arrow : Cursors.No;
    }

    /// <summary>工作階段由控制器結束(對方停止、連線中斷)時關閉視窗,不再回頭呼叫 Stop。</summary>
    public void CloseFromController()
    {
        _closedByController = true;
        Close();
    }

    // ===================== 畫面 =====================

    /// <summary>背景執行緒觸發。只保留最新一張,UI 執行緒來不及畫時中間的直接丟掉。</summary>
    private void OnFrameDecoded(object? sender, DecodedScreenFrame frame)
    {
        Interlocked.Exchange(ref _pendingFrame, frame)?.Release();
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(RenderPendingFrame, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void RenderPendingFrame()
    {
        Volatile.Write(ref _renderScheduled, 0);
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame == null)
        {
            return;
        }

        try
        {
            if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                ScreenImage.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            WaitingPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            frame.Release();
        }
    }

    private void OnFpsChanged(object? sender, int fps) =>
        Dispatcher.BeginInvoke(() => FpsText.Text = $"{fps} fps");

    // ===================== 滑鼠 =====================

    /// <summary>滑鼠在畫面上的位置 → 0~1 相對座標(拖曳到畫面外時夾到邊緣)。</summary>
    private (double X, double Y)? Normalize(MouseEventArgs e)
    {
        if (ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0)
        {
            return null;
        }

        var position = e.GetPosition(ScreenImage);
        return (Math.Clamp(position.X / ScreenImage.ActualWidth, 0, 1), Math.Clamp(position.Y / ScreenImage.ActualHeight, 0, 1));
    }

    private static int? ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => null,
    };

    private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenHost.Focus();
        if (!CanControl || ButtonIndex(e.ChangedButton) is not { } button || Normalize(e) is not { } point)
        {
            return;
        }

        ScreenImage.CaptureMouse();
        SendPointer(ScreenInputAction.PointerDown, point, button);
        e.Handled = true;
    }

    private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanControl || ButtonIndex(e.ChangedButton) is not { } button || Normalize(e) is not { } point)
        {
            return;
        }

        SendPointer(ScreenInputAction.PointerUp, point, button);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
        {
            ScreenImage.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CanControl || Normalize(e) is not { } point)
        {
            return;
        }

        var anyPressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed;
        // Android 端只在「按著」時才有意義(觸控螢幕沒有游標);電腦端則要讓對方的游標跟著移動。
        if (!anyPressed && IsRemoteAndroid)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastMoveSentAt < (anyPressed ? 10 : HoverMoveIntervalMs))
        {
            return;
        }

        _lastMoveSentAt = now;
        SendPointer(ScreenInputAction.PointerMove, point, null);
    }

    private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanControl || Normalize(e) is not { } point)
        {
            return;
        }

        _controller.SendInput(new ScreenMessage
        {
            Type = ScreenMessage.Input,
            Action = ScreenInputAction.Wheel,
            X = point.X,
            Y = point.Y,
            DeltaY = e.Delta / 120.0,
        });
        e.Handled = true;
    }

    private void SendPointer(string action, (double X, double Y) point, int? button) =>
        _controller.SendInput(new ScreenMessage
        {
            Type = ScreenMessage.Input,
            Action = action,
            X = point.X,
            Y = point.Y,
            Button = button,
        });

    // ===================== 鍵盤 =====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (!CanControl)
        {
            return;
        }

        // 一般字元(字母、數字、標點、空白)交給 PreviewTextInput 以文字送出,才能正確處理大小寫、
        // 輸入法與各種鍵盤配置;只有搭配 Ctrl/Alt 的組合鍵(Ctrl+C 之類)才以按鍵送出。
        var modifierHeld = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        var name = MapKey(key, includeCharacters: modifierHeld);
        if (name == null)
        {
            return;
        }

        _heldKeys.Add(name);
        SendKey(ScreenInputAction.KeyDown, name);
        // 吃掉事件,避免 Tab 移動焦點、Alt 叫出系統選單、方向鍵捲動之類的本機行為。
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!CanControl)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = MapKey(key, includeCharacters: true);
        if (name == null || !_heldKeys.Remove(name))
        {
            return;
        }

        SendKey(ScreenInputAction.KeyUp, name);
        e.Handled = true;
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!CanControl || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        // Enter/Backspace/Tab/Esc 已經在 KeyDown 以按鍵送出,這裡收到的控制字元要濾掉,不然會重複。
        var text = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0)
        {
            return;
        }

        _controller.SendInput(new ScreenMessage { Type = ScreenMessage.Input, Action = ScreenInputAction.Text, Text = text });
        e.Handled = true;
    }

    /// <summary>視窗失去焦點時(Alt+Tab 切走)放開所有按住的鍵,避免對方的 Alt/Ctrl 卡住。</summary>
    private void Window_Deactivated(object? sender, EventArgs e) => ReleaseHeldKeys();

    private void ReleaseHeldKeys()
    {
        foreach (var name in _heldKeys.ToArray())
        {
            SendKey(ScreenInputAction.KeyUp, name);
        }

        _heldKeys.Clear();
    }

    private void SendKey(string action, string key) =>
        _controller.SendInput(new ScreenMessage { Type = ScreenMessage.Input, Action = action, Key = key });

    /// <summary>
    /// WPF 的 Key → 網頁 KeyboardEvent.key 風格的名稱(兩端共用的命名)。<paramref name="includeCharacters"/>
    /// 為 false 時字母/數字/空白回傳 null(交給文字輸入處理)。
    /// </summary>
    public static string? MapKey(Key key, bool includeCharacters)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return includeCharacters ? ((char)('a' + (key - Key.A))).ToString() : null;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return includeCharacters ? ((char)('0' + (key - Key.D0))).ToString() : null;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            return $"F{key - Key.F1 + 1}";
        }

        return key switch
        {
            Key.Space => includeCharacters ? " " : null,
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.Tab => "Tab",
            Key.Escape => "Escape",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "ArrowLeft",
            Key.Up => "ArrowUp",
            Key.Right => "ArrowRight",
            Key.Down => "ArrowDown",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Control",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LWin or Key.RWin => "Meta",
            Key.Apps => "ContextMenu",
            _ => null,
        };
    }

    // ===================== 工具列 =====================

    private void SendNav(string key) =>
        _controller.SendInput(new ScreenMessage { Type = ScreenMessage.Input, Action = ScreenInputAction.Nav, Key = key });

    private void NavBackButton_Click(object sender, RoutedEventArgs e) => SendNav("back");

    private void NavHomeButton_Click(object sender, RoutedEventArgs e) => SendNav("home");

    private void NavRecentsButton_Click(object sender, RoutedEventArgs e) => SendNav("recents");

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _restoreState;
            FullScreenIcon.Kind = PackIconKind.Fullscreen;
        }
        else
        {
            _restoreState = WindowState;
            // 先回到 Normal 再設 Maximized,無邊框視窗才會真的蓋住工作列。
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullScreenIcon.Kind = PackIconKind.FullscreenExit;
        }

        ScreenHost.Focus();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ReleaseHeldKeys();
        if (!_closedByController)
        {
            _controller.Stop();
        }
    }
}
