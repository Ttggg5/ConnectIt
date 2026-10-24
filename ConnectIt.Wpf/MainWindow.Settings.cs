using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;
using Microsoft.Win32;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // 設定頁卡片的「砌磚式」排版:欄數依可用寬度動態決定,每張卡片依目前最短的一欄放入,
    // 讓同一欄裡的卡片緊接排列、不會因為隔壁欄比較高而留白。
    private const double SettingsCardWidth = 320;
    private const double SettingsCardGap = 16;
    private FrameworkElement[]? _settingsCards;
    private int _settingsCardColumnCount = -1;

    // 數字輸入框「打完停一下就自動儲存」的延遲:每次輸入都重新計時,停止輸入這麼久之後才驗證/儲存,
    // 避免使用者打到一半(例如想輸入 30 先打了 3)就被當成無效值跳出錯誤。
    private static readonly TimeSpan SettingsSaveDebounce = TimeSpan.FromMilliseconds(600);
    private readonly Dictionary<TextBox, DispatcherTimer> _settingsDebounceTimers = new();
    private readonly Dictionary<TextBox, Action> _settingsDebounceActions = new();

    // 從程式碼把設定值填進輸入框時(啟動、套用後回填)不要觸發 TextChanged 的自動儲存。
    private bool _isLoadingSettings;

    /// <summary>建構時呼叫一次:把各設定服務目前的值填到設定頁的控制項上。</summary>
    private void LoadSettingsIntoUi()
    {
        _isLoadingSettings = true;
        try
        {
            DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;
            SearchDurationTextBox.Text = _discoverySettings.SearchDurationSeconds.ToString();

            NotificationsEnabledCheckBox.IsChecked = ((App)Application.Current).NotificationSettings.NotificationsEnabled;
            AutoStartCheckBox.IsChecked = _startupService.IsEnabled;
            PreferredPortTextBox.Text = _connectionSettings.PreferredPort == 0 ? string.Empty : _connectionSettings.PreferredPort.ToString();
            ConnectTimeoutTextBox.Text = _connectionSettings.ConnectTimeoutSeconds.ToString();
            RefreshTrustedDevicesList();

            SelectComboBoxItemByTag(VideoDefaultSortComboBox, _videoServerSettings.DefaultSort);
            VideoAutoplayNextCheckBox.IsChecked = _videoServerSettings.AutoplayNext;
            VideoShuffleCheckBox.IsChecked = _videoServerSettings.Shuffle;
            VideoDefaultVolumeSlider.Value = _videoServerSettings.DefaultVolume;
            VideoDefaultVolumeText.Text = $"{_videoServerSettings.DefaultVolume}%";
            SelectComboBoxItemByTag(VideoDefaultSpeedComboBox, _videoServerSettings.DefaultSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            VideoExtraExtensionsTextBox.Text = string.Join(", ", _videoServerSettings.ExtraExtensions);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SettingsViewRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            ArrangeSettingsCards();
        }
    }

    private void ArrangeSettingsCards()
    {
        _settingsCards ??=
        [
            ThemeCard, DownloadFolderCard, NotificationCard, AutoStartCard, TrustedDevicesCard,
            VideoServerSettingsCard, AdvancedCard, UpdateCard, DiagnosticsCard,
        ];

        // SettingsViewRoot 是根視窗 Grid 裡明確的 Star 欄,寬度一定等於實際可用內容寬度;
        // 不依賴內層 ScrollViewer/StackPanel/Grid 的量測結果(那條鏈路在沒有明確 Width 時
        // 會退化成「依內容縮小」,導致卡片區永遠量到一個很窄的寬度)。
        var availableWidth = SettingsViewRoot.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        if (availableWidth <= 0)
        {
            return;
        }

        SettingsCardsHost.Width = availableWidth;

        var columnCount = Math.Max(1, (int)((availableWidth + SettingsCardGap) / (SettingsCardWidth + SettingsCardGap)));
        if (columnCount == _settingsCardColumnCount)
        {
            return;
        }

        _settingsCardColumnCount = columnCount;

        SettingsCardsHost.ColumnDefinitions.Clear();
        var columns = new StackPanel[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            SettingsCardsHost.ColumnDefinitions.Add(new ColumnDefinition());
            var column = new StackPanel { Margin = new Thickness(0, 0, i == columnCount - 1 ? 0 : SettingsCardGap, 0) };
            Grid.SetColumn(column, i);
            columns[i] = column;
        }

        var columnWidth = (availableWidth - (SettingsCardGap * (columnCount - 1))) / columnCount;
        var columnHeights = new double[columnCount];
        SettingsCardsHost.Children.Clear();
        foreach (var card in _settingsCards)
        {
            card.Measure(new Size(columnWidth, double.PositiveInfinity));
            var shortestColumn = Array.IndexOf(columnHeights, columnHeights.Min());
            (card.Parent as Panel)?.Children.Remove(card);
            columns[shortestColumn].Children.Add(card);
            columnHeights[shortestColumn] += card.DesiredSize.Height + SettingsCardGap;
        }

        foreach (var column in columns)
        {
            SettingsCardsHost.Children.Add(column);
        }
    }

    /// <summary>「進階」展開/收合時卡片高度改變,重新排一次砌磚版面。</summary>
    private void AdvancedExpander_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settingsCardColumnCount = -1;
        Dispatcher.InvokeAsync(ArrangeSettingsCards, DispatcherPriority.Loaded);
    }

    /// <summary>讓卡片標題列右側的「已儲存 ✓」短暫出現後淡出,告訴使用者變更已經生效。</summary>
    private static void FlashSaved(UIElement badge)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100))));
        badge.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>每次輸入都重新計時,停止輸入 <see cref="SettingsSaveDebounce"/> 後才執行 <paramref name="apply"/>。</summary>
    private void DebounceSettingsSave(TextBox textBox, Action apply)
    {
        if (_isLoadingSettings || !IsLoaded)
        {
            return;
        }

        _settingsDebounceActions[textBox] = apply;
        if (!_settingsDebounceTimers.TryGetValue(textBox, out var timer))
        {
            timer = new DispatcherTimer { Interval = SettingsSaveDebounce };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_settingsDebounceActions.TryGetValue(textBox, out var pending))
                {
                    pending();
                }
            };
            _settingsDebounceTimers[textBox] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void FlushSettingsDebounce(TextBox textBox)
    {
        if (_settingsDebounceTimers.TryGetValue(textBox, out var timer))
        {
            timer.Stop();
        }
    }

    private static void ShowFieldError(TextBlock errorText, string? error)
    {
        errorText.Text = error ?? string.Empty;
        errorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 驗證並儲存一個數字欄位:合法就存檔、清掉錯誤訊息、閃一下「已儲存」;不合法就顯示原因、不存檔。
    /// <paramref name="commit"/> 為 true(按 Enter 或離開輸入框)時,不合法的輸入會被還原成目前生效的值,
    /// 讓輸入框不會一直停在錯誤狀態。
    /// </summary>
    private void ValidateAndSaveField(
        TextBox textBox, TextBlock errorText, SettingsValidation.Result result,
        Func<int, int> save, Func<string> currentValueText, UIElement savedBadge, bool commit)
    {
        if (result.IsValid)
        {
            save(result.Value);
            ShowFieldError(errorText, null);
            FlashSaved(savedBadge);

            if (commit)
            {
                SetTextWithoutAutoSave(textBox, currentValueText());
            }

            return;
        }

        if (commit)
        {
            ShowFieldError(errorText, null);
            SetTextWithoutAutoSave(textBox, currentValueText());
        }
        else
        {
            ShowFieldError(errorText, result.Error);
        }
    }

    private void SetTextWithoutAutoSave(TextBox textBox, string text)
    {
        if (textBox.Text == text)
        {
            return;
        }

        _isLoadingSettings = true;
        try
        {
            textBox.Text = text;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    // ===================== 外觀 =====================

    private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<AppThemeMode>(tag, out var mode))
        {
            _themeService.SetMode(mode);
        }
    }

    private void SetThemeRadioButtonForMode(AppThemeMode mode)
    {
        var radioButton = mode switch
        {
            AppThemeMode.Light => ThemeLightRadioButton,
            AppThemeMode.Dark => ThemeDarkRadioButton,
            _ => ThemeAutoRadioButton,
        };
        radioButton.IsChecked = true;
    }

    // ===================== 檔案接收 =====================

    private void BrowseDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇接收檔案要儲存的資料夾",
            InitialDirectory = _fileTransferSettings.DownloadFolder,
        };

        if (dialog.ShowDialog() == true && dialog.FolderName is { Length: > 0 } folder)
        {
            _fileTransferSettings.SetDownloadFolder(folder);
            DownloadFolderTextBox.Text = _fileTransferSettings.DownloadFolder;
            FlashSaved(DownloadFolderSavedBadge);
        }
    }

    private void OpenDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = _fileTransferSettings.DownloadFolder;
        if (!Directory.Exists(folder))
        {
            MainSnackbar.MessageQueue?.Enqueue("這個資料夾目前不存在,收到第一個檔案時會自動建立。");
            return;
        }

        TryStartShell(folder);
    }

    // ===================== 通知 / 開機 / 信任裝置 =====================

    private void NotificationsEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).NotificationSettings.SetNotificationsEnabled(NotificationsEnabledCheckBox.IsChecked == true);
    }

    private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _startupService.SetEnabled(AutoStartCheckBox.IsChecked == true);
    }

    private void RefreshTrustedDevicesList()
    {
        TrustedDevicesItemsControl.ItemsSource = _trustedDevices.TrustedDevices.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        TrustedDevicesEmptyText.Visibility = _trustedDevices.TrustedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UntrustDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
        {
            return;
        }

        _trustedDevices.Untrust(name);
        RefreshTrustedDevicesList();
    }

    // ===================== 進階:快速搜尋秒數 =====================

    private void SearchDurationTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void SearchDurationTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        DebounceSettingsSave(SearchDurationTextBox, () => ApplySearchDuration(commit: false));

    private void SearchDurationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySearchDuration(commit: true);
            Keyboard_ClearFocus();
        }
    }

    private void SearchDurationTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplySearchDuration(commit: true);

    private void ApplySearchDuration(bool commit)
    {
        FlushSettingsDebounce(SearchDurationTextBox);
        ValidateAndSaveField(
            SearchDurationTextBox, SearchDurationErrorText,
            SettingsValidation.ValidateSearchDuration(SearchDurationTextBox.Text),
            _discoverySettings.SetSearchDurationSeconds,
            () => _discoverySettings.SearchDurationSeconds.ToString(),
            AdvancedSavedBadge, commit);
    }

    // ===================== 進階:監聽連接埠 =====================

    private void PreferredPortTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void PreferredPortTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        DebounceSettingsSave(PreferredPortTextBox, () => ApplyPreferredPort(commit: false));

    private void PreferredPortTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPreferredPort(commit: true);
            Keyboard_ClearFocus();
        }
    }

    private void PreferredPortTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyPreferredPort(commit: true);

    /// <summary>套用監聽連接埠設定,留空代表交由系統自動指派;變更只會在下次啟動 App 時生效。</summary>
    private void ApplyPreferredPort(bool commit)
    {
        FlushSettingsDebounce(PreferredPortTextBox);
        ValidateAndSaveField(
            PreferredPortTextBox, PreferredPortErrorText,
            SettingsValidation.ValidatePort(PreferredPortTextBox.Text),
            _connectionSettings.SetPreferredPort,
            () => _connectionSettings.PreferredPort == 0 ? string.Empty : _connectionSettings.PreferredPort.ToString(),
            AdvancedSavedBadge, commit);
    }

    // ===================== 進階:連線逾時 =====================

    private void ConnectTimeoutTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void ConnectTimeoutTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        DebounceSettingsSave(ConnectTimeoutTextBox, () => ApplyConnectTimeout(commit: false));

    private void ConnectTimeoutTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyConnectTimeout(commit: true);
            Keyboard_ClearFocus();
        }
    }

    private void ConnectTimeoutTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyConnectTimeout(commit: true);

    /// <summary>套用主動連線的逾時秒數,立即套用到目前的 ConnectionService。</summary>
    private void ApplyConnectTimeout(bool commit)
    {
        FlushSettingsDebounce(ConnectTimeoutTextBox);
        ValidateAndSaveField(
            ConnectTimeoutTextBox, ConnectTimeoutErrorText,
            SettingsValidation.ValidateConnectTimeout(ConnectTimeoutTextBox.Text),
            seconds =>
            {
                var applied = _connectionSettings.SetConnectTimeoutSeconds(seconds);
                _connection.ConnectTimeout = TimeSpan.FromSeconds(applied);
                return applied;
            },
            () => _connectionSettings.ConnectTimeoutSeconds.ToString(),
            AdvancedSavedBadge, commit);
    }

    // ===================== 影片伺服器自訂選項 =====================

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void VideoDefaultSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoDefaultSortComboBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _videoServerSettings.SetDefaultSort(tag);
            FlashVideoSettingsSaved();
        }
    }

    private void VideoAutoplayNextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _videoServerSettings.SetAutoplayNext(VideoAutoplayNextCheckBox.IsChecked == true);
        FlashVideoSettingsSaved();
    }

    private void VideoShuffleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _videoServerSettings.SetShuffle(VideoShuffleCheckBox.IsChecked == true);
        FlashVideoSettingsSaved();
    }

    private void FlashVideoSettingsSaved()
    {
        if (!_isLoadingSettings && IsLoaded)
        {
            FlashSaved(VideoSettingsSavedBadge);
        }
    }

    private void VideoDefaultVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VideoDefaultVolumeText.Text = $"{(int)e.NewValue}%";

        // 拖曳中每個 tick 都會觸發這個事件:滑桿沒有「失焦」這種自然的「使用者確定好了」時機,
        // 所以就直接即時儲存,反正 SetDefaultVolume 內部本來就只有值真的變了才寫檔。
        _videoServerSettings.SetDefaultVolume((int)e.NewValue);
        FlashVideoSettingsSaved();
    }

    private void VideoDefaultSpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoDefaultSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _videoServerSettings.SetDefaultSpeed(speed);
            FlashVideoSettingsSaved();
        }
    }

    private void VideoExtraExtensionsTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        DebounceSettingsSave(VideoExtraExtensionsTextBox, () => ApplyVideoExtraExtensions(commit: false));

    private void VideoExtraExtensionsTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyVideoExtraExtensions(commit: true);

    /// <summary>副檔名清單是自由文字,沒有「不合法」的狀態(無法辨識的項目直接略過),
    /// 停止輸入後就儲存;離開輸入框時再把整理過的結果(去重、補上句點)回填。</summary>
    private void ApplyVideoExtraExtensions(bool commit)
    {
        FlushSettingsDebounce(VideoExtraExtensionsTextBox);
        var applied = _videoServerSettings.SetExtraExtensions(VideoExtraExtensionsTextBox.Text);
        FlashSaved(AdvancedSavedBadge);
        if (commit)
        {
            SetTextWithoutAutoSave(VideoExtraExtensionsTextBox, string.Join(", ", applied));
        }
    }

    // ===================== 說明與診斷 =====================

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrEmpty(LogTextBox.Text) ? "(沒有紀錄)" : LogTextBox.Text);
            MainSnackbar.MessageQueue?.Enqueue("已複製狀態紀錄。");
        }
        catch (Exception ex)
        {
            AppendLog($"複製到剪貼簿失敗:{ex.Message}");
        }
    }

    private void ShowOnboardingButton_Click(object sender, RoutedEventArgs e) => ShowOnboarding();
}
