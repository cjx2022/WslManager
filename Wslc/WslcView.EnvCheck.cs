using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WslManager.Install;
using WslManager.Wslc.Dialogs;
using WslManager.Wslc.Helpers;
using WslManager.Wslc.Models;

namespace WslManager.Wslc;

/// <summary>
/// 环境检查（检查 / 修复 / 文档）的状态机。
///
/// v2.2 从「安装发行版」窗口并入本视图；v2.3 曾改成页内折叠面板；v2.4 改为**弹出卡片**
/// （Wslc/Dialogs/EnvCheckDialog.xaml）—— 页面只留结论小字，明细不再占版面。
/// 弹窗只做壳，控件事件原样转发回这里；检查 / 修复复用
/// WslManager.Install 的 EnvChecker / FixService，没有第二套逻辑。
/// </summary>
public partial class WslcView
{
    private readonly ObservableCollection<CheckVm> _checks = new();

    /// <summary>本页首次进入才跑检查：EnvChecker 会执行 wsl/systeminfo 之类命令，别拖慢启动。</summary>
    private bool _envChecked;
    private bool _envChecking;

    /// <summary>检查轮次计数：每真正跑一轮 RunEnvChecksAsync 就 +1。</summary>
    private int _checkGen;

    /// <summary>环境检查弹窗（懒创建、单实例；关了置 null，下次点按钮重建）。</summary>
    private EnvCheckDialog _envDlg;

    private void InitEnvChecks()
    {
        // v2.4：明细列表在弹窗里，ItemsSource（=_checks）在弹窗首次打开时挂上。
        // _checks 集合本身常驻本视图 —— 启动时的静默预检不依赖弹窗存在。
    }

    /// <summary>首次切到「环境与安装」页时调用。</summary>
    private void EnsureEnvChecked()
    {
        if (_envChecked) return;
        _ = RunEnvChecksAsync();
    }

    /// <summary>打开「环境检查」弹窗卡片（懒创建、单实例）。</summary>
    private void OpenEnvCheckDialog()
    {
        // 静默探针下**一个窗口都不创建**。原先只是把弹窗挪到 -6000 离屏，实测仍会
        // 在任务栏/桌面上闪出来打扰用户 —— 验收不需要给人看，检查逻辑本身照跑即可。
        if (SilentProbe) return;

        if (_envDlg == null)
        {
            _envDlg = new EnvCheckDialog(this) { Owner = HostWindow };
            _envDlg.ListChecks.ItemsSource = _checks;
            _envDlg.Closed += (_, _) => _envDlg = null;
        }

        _envDlg.Show();
        _envDlg.Activate();
    }

    /// <summary>弹窗 / 主卡共用的结论小字。</summary>
    private static string EnvBriefText(int ok, int warn, int fail)
        => fail == 0
            ? $"{ok} 项通过{(warn > 0 ? " · " + warn + " 项注意" : "")}"
            : $"{fail} 项未通过{(warn > 0 ? " · " + warn + " 项注意" : "")}";

    private async Task RunEnvChecksAsync()
    {
        if (_envChecking) return;
        _envChecking = true;
        _envChecked = true;
        _checkGen++;   // 本次是新的一轮检查（验收探针靠它分辨「预检」和「点按钮后的本轮」）

        EnvSetBar(true);
        SetStatus("正在检查环境…", "InfoBrush");

        try
        {
            var items = await EnvChecker.RunAsync(msg => Ui(() => SetStatus("环境检查：" + msg, "InfoBrush")));

            _checks.Clear();
            foreach (var i in items)
            {
                _checks.Add(new CheckVm
                {
                    Name = i.Name,
                    Detail = i.Detail,
                    Status = i.Status,
                    DocUrl = i.DocUrl,
                    DocName = i.DocName,
                    Fix = i.Fix
                });
            }

            int fail = items.Count(i => i.Status == "Fail");
            int warn = items.Count(i => i.Status == "Warn");
            // 一键修复只处理「真正能自动改环境」的项，不含「提权重启」（那是换身份重开，得单独点）
            int fixable = items.Count(i => i.CanFix && i.Fix != FixKind.Elevate);

            string brief = EnvBriefText(items.Count - warn - fail, warn, fail);
            string summary = fail == 0
                ? $"环境检查完成：{items.Count - warn - fail} 项通过，{warn} 项注意"
                : $"环境检查发现 {fail} 项未通过，{warn} 项注意"
                  + (fixable > 0 ? $"，其中 {fixable} 项可一键修复" : "（请按“查看文档”手动处理）");

            Ui(() =>
            {
                TxtEnvCheckBrief.Text = brief;
                if (_envDlg != null)
                {
                    _envDlg.BtnFixAll.IsEnabled = fixable > 0;
                    _envDlg.BtnFixAll.Content = fixable > 0 ? $"一键修复（{fixable}）" : "一键修复";
                    _envDlg.TxtSub.Text = brief;
                    _envDlg.TxtHint.Text = summary;
                }
            });

            SetStatus(summary, fail == 0 ? "OkBrush" : "ErrBrush");
            Log(summary);
        }
        catch (Exception ex)
        {
            SetStatus("环境检查失败：" + ex.Message, "ErrBrush");
            Log("环境检查异常：" + ex.Message);
        }
        finally
        {
            _envChecking = false;
            EnvSetBar(false);
        }
    }

    /// <summary>检查进度条只存在于弹窗里；没开弹窗时跑静默预检，静默跳过。</summary>
    private void EnvSetBar(bool on)
        => Ui(() =>
        {
            if (_envDlg != null)
                _envDlg.BarCheck.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        });

    /// <summary>MessageBox 的属主：弹窗开着就挂弹窗下（否则弹窗会压住提示框）。</summary>
    private Window DlgOwner => _envDlg != null && _envDlg.IsVisible ? _envDlg : HostWindow;

    // ══════════════════════════ 交互（弹窗事件转发到这里） ══════════════════════════
    // 重新检查的入口在主卡的「环境检查」按钮上（OnEnvCheckClick），弹窗里不放第二个
    // 「重新检查」——同一动作两个入口，用户不知道该点哪个。

    internal async void OnEnvFixItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CheckVm vm) return;
        await FixOneAsync(vm);
    }

    internal async void OnEnvFixAllClick(object sender, RoutedEventArgs e)
    {
        var targets = _checks.Where(c => c.CanFix && c.Fix != FixKind.Elevate).ToList();
        if (targets.Count == 0)
        {
            SetStatus("没有可自动修复的项目", "InfoBrush");
            return;
        }

        Ui(() =>
        {
            if (_envDlg != null) _envDlg.BtnFixAll.IsEnabled = false;
            EnvSetBar(true);
        });

        int ok = 0, bad = 0;
        bool needReboot = false;

        for (int i = 0; i < targets.Count; i++)
        {
            SetStatus($"一键修复（{i + 1}/{targets.Count}）：{targets[i].Name}…", "InfoBrush");
            var r = await FixOneAsync(targets[i], silent: true);
            if (r != null && r.Success) ok++; else bad++;
            if (r != null && r.NeedReboot) needReboot = true;
        }

        SetStatus($"一键修复完成：{ok} 项成功，{bad} 项失败", bad == 0 ? "OkBrush" : "WarnBrush");
        Log($"一键修复完成：成功 {ok}，失败 {bad}，需重启={needReboot}");

        await RunEnvChecksAsync();

        if (needReboot)
        {
            MessageBox.Show(DlgOwner,
                "部分修复（启用 Windows 功能 / 安装 WSL 组件）需要重启电脑后才会生效。\n\n请重启后再点「环境检查」确认。",
                "需要重启", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (bad == 0 && ok > 0)
        {
            MessageBox.Show(DlgOwner, $"已修复 {ok} 项，状态已刷新。", "修复完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>执行单项修复。silent=true 时不弹单条结果框（供一键修复复用）。</summary>
    private async Task<FixResult> FixOneAsync(CheckVm vm, bool silent = false)
    {
        if (vm == null || vm.Fix == FixKind.None) return null;

        // 提权重启：不是修复动作，而是换个身份重开自己
        if (vm.Fix == FixKind.Elevate)
        {
            SetStatus("正在以管理员身份重新启动…", "InfoBrush");
            Log("用户触发提权重启");

            // 先让本进程退出，再拉起提权实例：直接 Start 会撞上本进程持有的单实例互斥量。
            ElevationHost.PendingElevate = true;
            // 放行主窗口的「关闭 → 最小化到托盘」拦截，否则 Shutdown 会被 Cancel 掉
            ElevationHost.IsElevateShutdown = true;
            Application.Current?.Shutdown();
            return new FixResult { Success = false, Message = "正在提权重启" };
        }

        vm.IsFixing = true;
        vm.FixMessage = "";
        EnvSetBar(true);
        SetStatus($"正在修复：{vm.Name}…", "InfoBrush");

        FixResult result;
        try
        {
            var item = new CheckItem
            {
                Name = vm.Name,
                Detail = vm.Detail,
                Status = vm.Status,
                Fix = vm.Fix
            };
            result = await FixService.ApplyAsync(item, msg => Ui(() => SetStatus(msg, "InfoBrush")));
        }
        catch (Exception ex)
        {
            result = new FixResult { Success = false, Message = "修复异常：" + ex.Message };
        }
        finally
        {
            vm.IsFixing = false;
            EnvSetBar(false);
        }

        vm.FixMessage = (result.Success ? "✔ " : "✖ ") + result.Message;
        SetStatus(result.Message, result.Success ? "OkBrush" : "ErrBrush");
        Log($"修复[{vm.Name}/{vm.Fix}] 成功={result.Success} 需重启={result.NeedReboot}：{result.Message}");

        if (!silent && result.NeedReboot)
        {
            MessageBox.Show(DlgOwner, result.Message + "\n\n需要重启电脑后才会生效。", "需要重启",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (!silent && result.Success) await RunEnvChecksAsync();

        return result;
    }

    internal void OnEnvDocClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string url && url.Length > 0)
            DocLinks.Open(url);
    }
}
