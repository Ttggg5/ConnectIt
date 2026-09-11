using System.Configuration;
using System.Data;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace ConnectIt.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;

    // 讓 MainWindow 判斷:目前是使用者按了系統匣的「結束」,還是只是想關閉視窗(此時要改成隱藏到系統匣)。
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InitializeTrayIcon();
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

    public void RequestExit()
    {
        IsExiting = true;
        MainWindow?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        base.OnExit(e);
    }
}
