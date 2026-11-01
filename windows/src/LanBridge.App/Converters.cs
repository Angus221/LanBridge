using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LanBridge.App;

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is string flag && flag == "hide") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class ThemeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = parameter as string == "origin"
            ? (value as string == "来自手机" ? "PhoneBrush" : "AccentBrush")
            : (value is true ? "AccentBrush" : "LineBrush");
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
