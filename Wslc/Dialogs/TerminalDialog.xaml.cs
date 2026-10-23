using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WslManager.Wslc.Helpers;
using WslManager.Wslc.Models;
using WslManager.Wslc.Services;

namespace WslManager.Wslc.Dialogs;

/// <summary>
/// 「在终端中打开」：让用户挑容器里实际可用的 shell。
///
/// 为什么需要这个对话框：容器里有没有 bash 完全由镜像决定 ——
/// alpine 只有 busybox 提供的 sh，debian / nginx 才有 bash + dash，极小镜像甚至没有 shell。
/// 旧版把命令写死成 <c>sh</c>，遇到只有 bash 的镜像就只能开出一个报错的窗口。
/// 这里打开时探测一次，把真的存在的 shell 列出来；探不到时留「自定义…」兜底。
/// </summary>
public partial class TerminalDialog : Window
{
    private const string CustomTag = "自定义…";
    private static readonly string[] FallbackShells = { "bash", "sh" };

    private readonly ContainerInfo _c;
    private readonly bool _running;
    private readonly bool _demoCustom;
    /// <summary>可执行文件路径只解析一次 —— ResolveExecutable 会遍历 PATH，不能放在按键回调里。</summary>
    private readonly string _exe;
    private CancellationTokenSource _cts;

    /// <summary>用户最终确定的 shell 命令（仅当 ShowDialog() 返回 true 时有效）。</summary>
    public string SelectedShell { get; private set; } = "";

    /// <param name="demoCustom">仅验收用：强制切到「自定义…」分支，便于自动化截图核对。</param>
    public TerminalDialog(ContainerInfo c, bool demoCustom = false)
    {
        InitializeComponent();
        _c = c;
        _running = c.IsRunning;
        _demoCustom = demoCustom;
        _exe = WslcService.ExePath ?? "wslc.exe";

        // 动态资源引用：调色板在对话框自身的 Resources 里，且换主题时会整本替换 ——
        // 直接赋 Fill 的对象在换肤后不会更新，FindResource 找不到键还会抛异常。
        ElpState.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, c.StateBrushKey);
        TxtTitle.Text = string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name;
        TxtState.Text = c.StateText;

        // 先用常见项把对话框填满（探测是异步的，可能 1 秒才回来）——
        // 保证「打开即有可用选项」，而不是空白下拉。
        ApplyShells(FallbackShells, isProbeResult: false);

        Loaded += async (s, e) =>
        {
            UpdateLayout();
            ChromeHelper.CenterOnOwner(this);

            // 窗口高度随内容变化（选「自定义…」会多出一行输入框），
            // 尺寸一变就重新居中，免得长高之后偏到屏幕下方去。
            SizeChanged += (_, _) => ChromeHelper.CenterOnOwner(this);

            if (!_running)
            {
                TxtDetectHint.Text = "容器未运行 —— wslc exec 只能在运行中的容器里开终端。";
                BtnOpen.IsEnabled = false;
                CmbShell.IsEnabled = false;
                TxtCustom.IsEnabled = false;
                if (_demoCustom) SelectCustom("sh");   // 未运行时选中项也要能进自定义分支（验收用）
                return;
            }

            _cts = new CancellationTokenSource();
            TxtDetectHint.Text = "正在探测容器内可用的 shell…";
            var shells = await WslcService.DetectShellsAsync(_c.Name, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            ApplyShells(shells, isProbeResult: true);
            if (_demoCustom) SelectCustom("bash -l");
        };

        Closed += (s, e) => { try { _cts?.Cancel(); } catch { } };
    }

    // ── 列表填充 ───────────────────────────────────────────────
    /// <param name="shells">候选 shell（探测结果，或打开前的常见兜底项）</param>
    /// <param name="isProbeResult">true = 来自真实探测（探空即说明镜像没有 shell）；false = 打开前的兜底项</param>
    private void ApplyShells(IReadOnlyList<string> shells, bool isProbeResult)
    {
        var items = new List<string>();
        foreach (var s in shells)
            if (s.Length > 0 && !items.Contains(s)) items.Add(s);

        // 探测不到（或还没探测）时，至少给出最常见的两个 —— 不让下拉为空
        if (items.Count == 0)
            foreach (var s in FallbackShells)
                if (!items.Contains(s)) items.Add(s);

        CmbShell.Items.Clear();
        foreach (var s in items) CmbShell.Items.Add(s);
        CmbShell.Items.Add(CustomTag);

        // 优先 bash，其次 sh，再不然第一个；容器里一个都没有就落到「自定义…」
        int want = items.FindIndex(x => string.Equals(x, "bash", StringComparison.OrdinalIgnoreCase));
        if (want < 0) want = items.FindIndex(x => string.Equals(x, "sh", StringComparison.OrdinalIgnoreCase));
        if (want < 0) want = 0;
        bool noneDetected = isProbeResult && shells.Count == 0;
        CmbShell.SelectedIndex = noneDetected ? items.Count : want;

        if (noneDetected)
            TxtDetectHint.Text = "没有探测到常见 shell（该镜像可能不含 shell）—— 请填自定义命令。";
        else if (isProbeResult)
            TxtDetectHint.Text = $"容器内探测到 {items.Count} 个：{string.Join("、", items)}。以实际镜像为准。";
        else
            TxtDetectHint.Text = "先给出最常见项；打开时会实时探测容器内实际可用的 shell。";

        UpdatePreview();
    }

    // ── 交互 ───────────────────────────────────────────────────
    private void OnShellChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelCustom == null) return;   // 构造期 SelectedIndex 赋值可能早于后续字段就绪
        bool custom = (CmbShell.SelectedItem as string) == CustomTag;
        PanelCustom.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom) TxtCustom.Focus();
        UpdatePreview();
    }

    private void OnCustomTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    /// <summary>切到「自定义…」并预填命令（验收用，也可给「记住上次」之类的扩展复用）。</summary>
    private void SelectCustom(string command)
    {
        int idx = CmbShell.Items.IndexOf(CustomTag);
        if (idx < 0) return;
        CmbShell.SelectedIndex = idx;
        TxtCustom.Text = command;
        TxtCustom.Focus();
        TxtCustom.CaretIndex = TxtCustom.Text.Length;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var shell = CurrentShell();
        TxtPreview.Text = shell.Length == 0
            ? $"{_exe} exec -it {_c.Name} <未填写>"
            : $"{_exe} exec -it {_c.Name} {shell}";
        if (BtnOpen != null) BtnOpen.IsEnabled = _running && shell.Length > 0;
    }

    private string CurrentShell()
    {
        var s = CmbShell.SelectedItem as string ?? "";
        return s == CustomTag ? (TxtCustom.Text ?? "").Trim() : s.Trim();
    }

    // ── 窗口 ───────────────────────────────────────────────────
    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var shell = CurrentShell();
        if (shell.Length == 0) return;
        SelectedShell = shell;
        DialogResult = true;
    }
}
