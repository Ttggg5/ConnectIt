using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ConnectIt.Wpf.Converters;

/// <summary>
/// 比較兩個值的字串表示是否相同(第二個值為 null 時一律視為不相同)。
/// 用在清單項目的 DataTrigger:例如裝置卡片的位址等於 ItemsControl.Tag 裡記的「目前連線對象位址」時
/// 顯示「已連線」標籤,影片卡片的相對路徑等於「目前播放中」時加上高亮。
/// </summary>
public sealed class StringEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is null || values[1] == DependencyProperty.UnsetValue)
        {
            return false;
        }

        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>值不是 null(也不是空字串)時回傳 true。</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value is not string { Length: 0 };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility;ConverterParameter 為 "Invert" 時反過來(true → Collapsed)。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is "Invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
