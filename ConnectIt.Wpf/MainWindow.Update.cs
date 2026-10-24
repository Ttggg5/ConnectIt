using System.Diagnostics;
using System.Windows;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Wpf;

// 設定頁「軟體更新」卡片:啟動後自動檢查一次 GitHub 上有沒有新版本,有的話跳系統匣通知;
// 使用者按「下載並更新」才真的下載,下載完交給 UpdateService 的外部腳本覆蓋檔案並重新啟動。
public partial class MainWindow
{
    // 啟動後等一下再檢查,不跟裝置探索/監聽搶開機時的網路與 CPU。
    private static readonly TimeSpan StartupUpdateCheckDelay = TimeSpan.FromSeconds(8);

    private readonly UpdateService _updateService = new();
    private AvailableUpdate? _availableUpdate;
    private bool _isUpdateBusy;

    private void InitializeUpdateUi()
    {
        CurrentVersionText.Text = $"目前版本:{UpdateService.FormatVersion(UpdateService.CurrentVersion)}";
        Closed += (_, _) => _updateService.Dispose();
    }

    private async void StartStartupUpdateCheck()
    {
        await Task.Delay(StartupUpdateCheckDelay);
        await CheckForUpdateAsync(userInitiated: false);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(userInitiated: true);

    private async Task CheckForUpdateAsync(bool userInitiated)
    {
        if (_isUpdateBusy)
        {
            return;
        }

        SetUpdateBusy(true);
        UpdateStatusText.Text = "正在檢查更新…";
        try
        {
            _availableUpdate = await _updateService.CheckForUpdateAsync();
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = $"已經是最新版本。(上次檢查:{DateTime.Now:HH:mm})";
                return;
            }

            var versionText = UpdateService.FormatVersion(_availableUpdate.Version);
            UpdateStatusText.Text = $"有新版本 {versionText} 可以更新。更新時 App 會自動重新啟動,目前的連線會中斷。";
            AppendLog($"發現新版本 {versionText}。");
            if (!userInitiated)
            {
                ((App)Application.Current).ShowConnectionAlert("ConnectIt 有新版本", $"版本 {versionText} 已推出,到「設定 → 軟體更新」即可安裝。");
            }
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            UpdateStatusText.Text = "檢查更新失敗,請確認網路連線後再試一次。";
            AppendLog($"檢查更新失敗:{ex.Message}");
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } update || _isUpdateBusy)
        {
            return;
        }

        if (!UpdateService.CanUpdateInPlace())
        {
            // 裝在沒有寫入權限的資料夾(例如 Program Files)時無法自動覆蓋,改開下載頁讓使用者手動更新。
            UpdateStatusText.Text = "程式所在的資料夾無法寫入,請從開啟的網頁手動下載新版本。";
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPageUrl) { UseShellExecute = true });
            return;
        }

        SetUpdateBusy(true);
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下載 {UpdateService.FormatVersion(update.Version)}…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = value;
                UpdateStatusText.Text = $"正在下載 {UpdateService.FormatVersion(update.Version)}… {value:P0}";
            });
            var extractedDirectory = await _updateService.DownloadAsync(update, progress);

            UpdateStatusText.Text = "下載完成,正在重新啟動以完成更新…";
            AppendLog($"開始安裝版本 {UpdateService.FormatVersion(update.Version)}。");
            UpdateService.LaunchInstaller(extractedDirectory);
            ((App)Application.Current).RequestExit();
        }
        catch (Exception ex)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "下載更新失敗,請稍後再試一次。";
            AppendLog($"下載更新失敗:{ex.Message}");
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _isUpdateBusy = busy;
        CheckUpdateButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        InstallUpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded && SettingsViewRoot.Visibility == Visibility.Visible)
        {
            // 按鈕/進度條出現或消失會改變卡片高度,重新排一次砌磚版面。
            _settingsCardColumnCount = -1;
            Dispatcher.InvokeAsync(ArrangeSettingsCards, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
