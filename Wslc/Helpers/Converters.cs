using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WslManager.Wslc.Helpers;

/// <summary>把资源键字符串（如 "OkBrush"）转成画刷。让行模型只暴露 key，XAML 不写状态逻辑。</summary>
public sealed class BrushKeyConverter : IValueConverter
{
    /// <summary>
    /// 视图级资源宿主。容器调色板挂在 WslcView.Resources 上，而宿主主窗口用的是另一套
    /// 键名（SuccessBrush / DangerBrush…），Application 级查不到 "OkBrush" 这类键 ——
    /// 只查应用级会一直拿到 Transparent（状态点、状态文字直接看不见）。
    /// 由 WslcView 构造时注册，查不到时再退回应用级。
    /// </summary>
    public static FrameworkElement Scope { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) return Brushes.Transparent;
        var res = Scope != null ? Scope.TryFindResource(key) : null;
        if (res == null) res = Application.Current?.TryFindResource(key);
        return res as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → Visible，false → Collapsed。参数 "invert" 取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>数量为 0 → Visible（用于列表空状态提示）。参数 "invert" 取反。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int n = 0;
        try { n = System.Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { }
        bool b = n == 0;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串非空 → Visible。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = !string.IsNullOrWhiteSpace(value as string);
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
