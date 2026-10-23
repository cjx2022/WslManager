using System.Windows;
using System.Windows.Input;
using WslManager.Wslc.Helpers;

namespace WslManager.Wslc.Dialogs;

/// <summary>
/// 环境检查弹窗卡片（v2.4：从「环境与安装」页的内联折叠面板改过来）。
///
/// 为什么只做壳不做逻辑：检查 / 修复 / 文档的全部状态机都在
/// <see cref="WslcView.EnvCheck"/>（要用它的 SetStatus / Log / _checks 集合），
/// 这里只承载控件，事件原样转发给 owner —— 同一动作只有一个实现，不会出现
/// 「卡片里点了和页面里点了行为不一样」。
/// </summary>
public partial class EnvCheckDialog : Window
{
    private readonly WslcView _owner;

    public EnvCheckDialog(WslcView owner)
    {
        InitializeComponent();
        _owner = owner;
        Loaded += (_, _) => ChromeHelper.CenterOnOwner(this);
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ── 事件转发：修复 / 文档的真正处理在 owner（WslcView.EnvCheck.cs） ──
    private void OnFixAllClick(object sender, RoutedEventArgs e) => _owner.OnEnvFixAllClick(sender, e);

    private void OnFixItemClick(object sender, RoutedEventArgs e) => _owner.OnEnvFixItemClick(sender, e);

    private void OnDocClick(object sender, MouseButtonEventArgs e) => _owner.OnEnvDocClick(sender, e);
}
