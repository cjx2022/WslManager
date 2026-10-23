using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WslManager.Wslc.Helpers;

namespace WslManager.Wslc.Dialogs;

/// <summary>
/// 自绘的提示 / 确认对话框。
/// 替代系统自带 MessageBox —— 后者是 Windows 原生样式（方角、系统字、蓝色按钮），
/// 跟本程序的圆角浅色卡片风格完全对不上。
/// </summary>
public partial class AppDialog : Window
{
    private AppDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => ChromeHelper.CenterOnOwner(this);
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancelClick(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Setup(string title, string message, string okText,
                       bool danger, bool showCancel, string cancelText)
    {
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnOk.Content = okText;
        BtnCancel.Content = cancelText;
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        if (danger)
        {
            IconBadge.Background = (Brush)FindResource("ErrSoftBrush");
            IconGlyph.Foreground = (Brush)FindResource("ErrBrush");
            IconGlyph.Text = "\uE7BA";              // 警告三角
            BtnOk.Style = (Style)FindResource("DangerPrimaryButton");
            BtnOk.Padding = new Thickness(20, 8, 20, 8);
            BtnOk.MinWidth = 88;
        }
        else
        {
            IconBadge.Background = (Brush)FindResource("AccentSoftBrush");
            IconGlyph.Foreground = (Brush)FindResource("AccentBrush");
            IconGlyph.Text = "\uE946";              // 信息
        }
    }

    /// <summary>构造但不显示（供 --dialog=confirm 这类验收入口使用）。</summary>
    internal static AppDialog Build(string title, string message, string okText,
        bool danger, bool showCancel = true, string cancelText = "取消")
    {
        var d = new AppDialog();
        d.Setup(title, message, okText, danger, showCancel, cancelText);
        return d;
    }

    /// <summary>纯提示（只有「知道了」）。</summary>
    public static void Info(Window owner, string title, string message, string okText = "知道了")
    {
        var d = new AppDialog { Owner = owner };
        d.Setup(title, message, okText, danger: false, showCancel: false, cancelText: "");
        d.ShowDialog();
    }

    /// <summary>确认框；danger=true 时确定按钮为红色（删除类操作）。</summary>
    public static bool Confirm(Window owner, string title, string message,
        string okText = "确定", bool danger = false, string cancelText = "取消")
    {
        var d = new AppDialog { Owner = owner };
        d.Setup(title, message, okText, danger, showCancel: true, cancelText);
        return d.ShowDialog() == true;
    }
}
