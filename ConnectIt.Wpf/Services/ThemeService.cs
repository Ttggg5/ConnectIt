using System.IO;
using System.Text.Json;
using System.Windows;
using ConnectIt.Wpf.Models;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 管理應用程式的顏色主題(淺色/深色/跟隨系統),並把使用者的選擇儲存在本機供下次啟動使用。
/// 「跟隨系統」模式會讀取 Windows「設定 > 個人化 > 色彩」的淺色/深色設定,
/// 並在使用者於系統設定中切換主題時即時跟著更新。
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly PaletteHelper _paletteHelper = new();
    private readonly string _settingsFilePath;

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.Auto;

    public ThemeService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>載入先前儲存的主題設定並套用。應在應用程式啟動、UI 建立完成後呼叫一次。</summary>
    public void Initialize()
    {
        CurrentMode = LoadSavedMode();
        Apply();
    }

    /// <summary>切換主題模式,套用到目前的 UI 並儲存供下次啟動使用。</summary>
    public void SetMode(AppThemeMode mode)
    {
        if (CurrentMode == mode)
        {
            return;
        }

        CurrentMode = mode;
        SaveMode(mode);
        Apply();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || CurrentMode != AppThemeMode.Auto)
        {
            return;
        }

        // 這個事件從系統背景執行緒觸發,必須排回 UI 執行緒才能安全更新資源字典。
        Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        var effectiveTheme = CurrentMode == AppThemeMode.Auto ? GetSystemBaseTheme() : CurrentMode;

        var theme = _paletteHelper.GetTheme();
        theme.SetBaseTheme(effectiveTheme == AppThemeMode.Dark ? BaseTheme.Dark : BaseTheme.Light);
        _paletteHelper.SetTheme(theme);
    }

    private static AppThemeMode GetSystemBaseTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(AppsUseLightThemeValueName) is int value)
            {
                return value == 0 ? AppThemeMode.Dark : AppThemeMode.Light;
            }
        }
        catch
        {
            // 讀不到登錄檔(例如權限問題)時,退回淺色主題。
        }

        return AppThemeMode.Light;
    }

    private AppThemeMode LoadSavedMode()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<ThemeSettingsData>(json);
                if (data != null && Enum.TryParse<AppThemeMode>(data.Theme, ignoreCase: true, out var mode))
                {
                    return mode;
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return AppThemeMode.Auto;
    }

    private void SaveMode(AppThemeMode mode)
    {
        try
        {
            var json = JsonSerializer.Serialize(new ThemeSettingsData { Theme = mode.ToString() });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的主題切換,只是下次啟動會退回預設值。
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private sealed class ThemeSettingsData
    {
        public string Theme { get; set; } = nameof(AppThemeMode.Auto);
    }
}
