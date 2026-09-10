namespace ConnectIt.Wpf.Models;

/// <summary>
/// 應用程式的顏色主題模式。
/// 命名為 AppThemeMode(而非 ThemeMode)是為了避免和 WPF 內建的 <see cref="System.Windows.ThemeMode"/>
/// (Window.ThemeMode 屬性用的型別)撞名。
/// </summary>
public enum AppThemeMode
{
    Light,
    Dark,

    /// <summary>跟隨 Windows 系統目前的淺色/深色設定。</summary>
    Auto,
}
