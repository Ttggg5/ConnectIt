using System.Configuration;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using ConnectIt.Wpf.Services;
using Application = System.Windows.Application;

namespace ConnectIt.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // 固定的識別字串,確保每台機器上不管開幾次 exe,Mutex/EventWaitHandle 都指向同一個名字。
    private const string SingleInstanceMutexName = "ConnectIt-SingleInstance-9F2E6B7E-8C1A-4E1D-9D3B-2D6E1F9A7C44";
    private const string ActivateEventName = "ConnectIt-Activate-9F2E6B7E-8C1A-4E1D-9D3B-2D6E1F9A7C44";

    private NotifyIcon? _notifyIcon;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;

    private readonly NotificationSettingsService _notificationSettings = new();

    // 讓 MainWindow 判斷:目前是使用者按了系統匣的「結束」,還是只是想關閉視窗(此時要改成隱藏到系統匣)。
    public bool IsExiting { get; private set; }

    /// <summary>設定頁的「通知」開關讀寫這個,供 <see cref="ShowConnectionAlert"/> 判斷是否要顯示。</summary>
    public NotificationSettingsService NotificationSettings => _notificationSettings;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 同一台裝置只允許一個執行中的 ConnectIt——每個實例都會各自監聽一個 TCP 連接埠,
        // 開多個實例只會造成裝置探索/連線對到不確定是哪一個埠,而不是真的能同時用。
        // 用具名 Mutex 判斷是不是第一個實例;不是的話就叫醒既有的那個(顯示主視窗)然後直接結束自己。
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var existingActivateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                existingActivateEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 理論上不該發生(Mutex 都已經有人持有了),忽略即可。
            }

            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivateListener();

        base.OnStartup(e);
        InitializeTrayIcon();

        // 手動建立/顯示主視窗(App.xaml 不再用 StartupUri),這樣「開機自動啟動」
        // (見 StartupService)可以帶入 --minimized 參數,開機時直接留在系統匣,不彈出主視窗。
        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Any(arg => string.Equals(arg, StartupService.MinimizedStartupArg, StringComparison.OrdinalIgnoreCase)))
        {
            window.Show();
        }
    }

    /// <summary>背景執行緒等待其他(被擋下的)實例送來的訊號,收到就在 UI 執行緒把主視窗叫出來。</summary>
    private void StartActivateListener()
    {
        var activateEvent = _activateEvent!;
        var thread = new Thread(() =>
        {
            while (activateEvent.WaitOne())
            {
                Dispatcher.Invoke(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
    }

    private void InitializeTrayIcon()
    {
        var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

        var showMenuItem = new ToolStripMenuItem("開啟主視窗");
        showMenuItem.Click += (_, _) => ShowMainWindow();

        var exitMenuItem = new ToolStripMenuItem("結束");
        exitMenuItem.Click += (_, _) => RequestExit();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(showMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "ConnectIt",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>把「隱藏到系統匣」提示訊息告知使用者,之後真正結束時由這裡統一觸發視窗關閉。</summary>
    public void ShowBackgroundNotification()
    {
        _notifyIcon?.ShowBalloonTip(
            3000,
            "ConnectIt 仍在背景執行",
            "應用程式已隱藏到系統匣,裝置探索與連線功能會持續在背景運作。",
            ToolTipIcon.Info);
    }

    /// <summary>
    /// 通知使用者連線狀態的重要變化(對方傳來檔案/資料夾被自動接受、對方中斷連線),
    /// 不需要使用者手動確認的事件會靠這個系統匣氣泡提示曝光——尤其是視窗被隱藏到系統匣時。
    /// 可在設定頁關閉(見 NotificationSettingsService),關閉後這裡直接不顯示。
    /// </summary>
    public void ShowConnectionAlert(string title, string message)
    {
        if (!_notificationSettings.NotificationsEnabled)
        {
            return;
        }

        _notifyIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
    }

    public void RequestExit()
    {
        IsExiting = true;
        MainWindow?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;

        _activateEvent?.Dispose();
        _activateEvent = null;

        // 沒拿到 Mutex 的那個「第二實例」路徑不會走到這裡(它在 OnStartup 就直接 Shutdown 了),
        // 所以這裡一定是真正持有 Mutex 的那個實例,可以安全釋放。
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }
}
