using Microsoft.Win32;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 管理「開機時自動啟動」設定,透過使用者層級的登錄機碼(不需要系統管理員權限)。
/// 啟用時會加上 <c>--minimized</c> 參數,讓 App 開機時直接隱藏到系統匣,不彈出主視窗
/// (對應 <see cref="App.OnStartup"/> 對這個參數的處理)。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ConnectIt";
    public const string MinimizedStartupArg = "--minimized";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{exePath}\" {MinimizedStartupArg}");
        }
        catch
        {
            // 沒有登錄檔寫入權限等異常情況下,安靜地放棄——使用者可以之後再試一次。
        }
    }
}
