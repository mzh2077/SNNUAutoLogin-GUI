using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace SNNUAutoLogin;

/// <summary>bool → Visibility。true 显示，false 折叠。</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
