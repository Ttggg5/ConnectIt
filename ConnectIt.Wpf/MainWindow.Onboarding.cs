using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ConnectIt.Wpf;

public partial class MainWindow
{
    // ===================== 首次啟動引導 =====================
    // 三個步驟:歡迎 → 替裝置取名 → 怎麼使用。第一次啟動時自動顯示,之後可以在設定頁「重新顯示使用引導」。

    private const int OnboardingStepCount = 3;
    private int _onboardingStep;

    private void ShowOnboarding()
    {
        OnboardingDeviceNameTextBox.Text = MyDeviceName;
        SetOnboardingStep(0);
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void SetOnboardingStep(int step)
    {
        _onboardingStep = Math.Clamp(step, 0, OnboardingStepCount - 1);

        var steps = new FrameworkElement[] { OnboardingStep1, OnboardingStep2, OnboardingStep3 };
        var dots = new Border[] { OnboardingDot1, OnboardingDot2, OnboardingDot3 };
        for (var i = 0; i < OnboardingStepCount; i++)
        {
            var isCurrent = i == _onboardingStep;
            steps[i].Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            dots[i].Width = isCurrent ? 22 : 8;
            dots[i].Background = (Brush)FindResource(i <= _onboardingStep ? "Brand.Brush.Gradient" : "Brand.Brush.Outline");
        }

        OnboardingBackButton.Content = _onboardingStep == 0 ? "略過" : "上一步";
        OnboardingNextButtonText.Text = _onboardingStep == OnboardingStepCount - 1 ? "開始使用" : "下一步";

        if (_onboardingStep == 1)
        {
            Dispatcher.BeginInvoke(() =>
            {
                OnboardingDeviceNameTextBox.Focus();
                OnboardingDeviceNameTextBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnboardingNextButton_Click(object sender, RoutedEventArgs e) => AdvanceOnboarding();

    private void AdvanceOnboarding()
    {
        if (_onboardingStep == 1)
        {
            ApplyDeviceName(OnboardingDeviceNameTextBox.Text);
        }

        if (_onboardingStep >= OnboardingStepCount - 1)
        {
            FinishOnboarding();
            return;
        }

        SetOnboardingStep(_onboardingStep + 1);
    }

    private void OnboardingBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep == 0)
        {
            FinishOnboarding();
            return;
        }

        SetOnboardingStep(_onboardingStep - 1);
    }

    private void OnboardingDeviceNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AdvanceOnboarding();
            e.Handled = true;
        }
    }

    private void FinishOnboarding()
    {
        _deviceSettings.SetOnboardingCompleted(true);
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        NavListBox.SelectedIndex = 0;
    }
}
