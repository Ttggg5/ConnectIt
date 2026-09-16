using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            ThemeCard, SearchDurationCard, DownloadFolderCard, ConnectionCard, VideoServerSettingsCard,
            NotificationCard, AutoStartCard, TrustedDevicesCard,
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
            var column = new StackPanel { Margin = new Thickness(0, 0, SettingsCardGap, 0) };
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
        }
    }

    private void SearchDurationTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void SearchDurationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySearchDuration();
            Keyboard_ClearFocus();
        }
    }

    private void SearchDurationTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplySearchDuration();

    /// <summary>套用搜尋秒數設定,超出合理範圍會被夾住,並把夾住後的值顯示回輸入框。</summary>
    private void ApplySearchDuration()
    {
        if (!int.TryParse(SearchDurationTextBox.Text, out var seconds))
        {
            seconds = DiscoverySettingsService.DefaultSearchDurationSeconds;
        }

        SearchDurationTextBox.Text = _discoverySettings.SetSearchDurationSeconds(seconds).ToString();
    }

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

    private void PreferredPortTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void PreferredPortTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPreferredPort();
            Keyboard_ClearFocus();
        }
    }

    private void PreferredPortTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyPreferredPort();

    /// <summary>套用監聽連接埠設定,留空代表交由系統自動指派;變更只會在下次啟動 App 時生效。</summary>
    private void ApplyPreferredPort()
    {
        var port = int.TryParse(PreferredPortTextBox.Text, out var parsed) ? parsed : 0;
        var applied = _connectionSettings.SetPreferredPort(port);
        PreferredPortTextBox.Text = applied == 0 ? string.Empty : applied.ToString();
    }

    private void ConnectTimeoutTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void ConnectTimeoutTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyConnectTimeout();
            Keyboard_ClearFocus();
        }
    }

    private void ConnectTimeoutTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyConnectTimeout();

    /// <summary>套用主動連線的逾時秒數,超出合理範圍會被夾住,立即套用到目前的 ConnectionService。</summary>
    private void ApplyConnectTimeout()
    {
        if (!int.TryParse(ConnectTimeoutTextBox.Text, out var seconds))
        {
            seconds = ConnectionSettingsService.DefaultConnectTimeoutSeconds;
        }

        var applied = _connectionSettings.SetConnectTimeoutSeconds(seconds);
        ConnectTimeoutTextBox.Text = applied.ToString();
        _connection.ConnectTimeout = TimeSpan.FromSeconds(applied);
    }

    // ===================== 影片伺服器自訂選項(設定頁) =====================

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
        }
    }

    private void VideoAutoplayNextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _videoServerSettings.SetAutoplayNext(VideoAutoplayNextCheckBox.IsChecked == true);
    }

    private void VideoShuffleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _videoServerSettings.SetShuffle(VideoShuffleCheckBox.IsChecked == true);
    }

    private void VideoAutoplayCountdownTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void VideoAutoplayCountdownTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyVideoAutoplayCountdown();
            Keyboard_ClearFocus();
        }
    }

    private void VideoAutoplayCountdownTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyVideoAutoplayCountdown();

    /// <summary>套用自動播放下一部的倒數秒數,超出合理範圍會被夾住,並把夾住後的值顯示回輸入框。</summary>
    private void ApplyVideoAutoplayCountdown()
    {
        if (!int.TryParse(VideoAutoplayCountdownTextBox.Text, out var seconds))
        {
            seconds = VideoServerSettingsService.DefaultAutoplayCountdownSeconds;
        }

        VideoAutoplayCountdownTextBox.Text = _videoServerSettings.SetAutoplayCountdownSeconds(seconds).ToString();
    }

    private void VideoDefaultVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VideoDefaultVolumeText.Text = $"{(int)e.NewValue}%";

        // 拖曳中每個 tick 都會觸發這個事件,跟其他文字輸入框「LostFocus 才套用」不同——
        // 滑桿沒有「失焦」這種自然的「使用者確定好了」時機,所以就直接即時儲存,
        // 反正 SetDefaultVolume 內部本來就只有值真的變了才寫檔。
        _videoServerSettings.SetDefaultVolume((int)e.NewValue);
    }

    private void VideoDefaultSpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoDefaultSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _videoServerSettings.SetDefaultSpeed(speed);
        }
    }

    private void VideoExtraExtensionsTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var applied = _videoServerSettings.SetExtraExtensions(VideoExtraExtensionsTextBox.Text);
        VideoExtraExtensionsTextBox.Text = string.Join(", ", applied);
    }
}
