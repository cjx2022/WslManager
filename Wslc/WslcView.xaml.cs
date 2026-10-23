using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WslManager.Wslc.Dialogs;
using WslManager.Wslc.Helpers;
using WslManager.Wslc.Models;
using WslManager.Wslc.Services;

namespace WslManager.Wslc;

public partial class WslcView : UserControl
{
    /// <summary>
    /// 静默探针模式（v2.0）：带 --dump-layout / --dump-theme 等验收参数启动时为 true。
    /// 验收只写报告文件、不需要给人看 —— 所有窗口（含弹窗）都要摆到屏幕外且不激活，
    /// 否则跑一次验收就在用户桌面上弹窗、抢焦点（bill 明确要求"静默验证"）。
    /// </summary>
    public static bool SilentProbe { get; set; }

    /// <summary>把一个窗口挪到屏幕外并禁止激活 —— 静默探针模式下所有 Show 之前调用。</summary>
    public static void ParkWindowOffscreen(Window w)
    {
        if (w == null) return;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000;
        w.Top = -6000;
    }

    /// <summary>
    /// 承载本视图的宿主窗口。窗口相关的动作（对话框属主、屏幕抓取、DPI 判定）
    /// 仍然必须落在真正的 Window 上 —— UserControl 不是 Window，故统一走这里。
    /// </summary>
    private Window HostWindow => Window.GetWindow(this) ?? Application.Current?.MainWindow;

    private readonly ObservableCollection<ContainerInfo> _containers = new();
    private readonly ObservableCollection<ImageInfo> _images = new();
    private readonly ObservableCollection<NetworkInfo> _networks = new();
    private readonly ObservableCollection<ImageSearchItem> _searchResults = new();
    private readonly DispatcherTimer _realtime = new();

    private WslEnvStatus _env;
    private bool _ready;
    private bool _busy;
    /// <summary>镜像清单是否已至少成功刷新过一次（环境信息卡显示「读取中…」还是真实数量）。</summary>
    private bool _imagesLoaded;

    /// <summary>会话虚拟磁盘的原始路径（供「复制」用；TxtSessionDisk 里是「路径 + 占用」的复合文本）。</summary>
    private string _sessionDiskPath = "";
    private CancellationTokenSource _statsCts;

    /// <summary>--demo 演示数据模式。演示数据绝不能写进真实的自启名单。</summary>
    private bool _demoMode;

    /// <summary>LoadSettingsIntoUi 期间的「静默」标记：避免程序回填勾选框时触发真实写注册表。</summary>
    private bool _settingsLoading;

    /// <summary>
    /// 首次加载（含开机自启恢复）完成的信号。宿主「自启时最小化到托盘」要等它 ——
    /// 窗口先 Hide() 的话，视图的 Loaded 可能根本不触发，恢复逻辑就永远不跑了。
    /// </summary>
    private readonly TaskCompletionSource<bool> _startupTcs =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartupDone => _startupTcs.Task;

    private ContainerInfo _selContainer;
    private ImageInfo _selImage;

    /// <summary>
    /// 「常用镜像源」预设（设置页的可点按钮）。
    ///
    /// 默认值是**第三方公开**加速地址（见 `AppSettings.DefaultMirrorPresets`）——
    /// 本项目只是替用户填个地址，不提供、不运营、也不背书这些站点，可用性/速度不保证。
    /// 用户可在 `settings.ini` 里改 `MirrorPresets=`（写 `none` 表示一个都不用）。
    /// </summary>
    private static string[] MirrorPresets() =>
        (AppSettings.MirrorPresets ?? "")
            .Split(new[] { ',', '，', ';', '；', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public WslcView()
    {
        InitializeComponent();

        // 让 BrushKeyConverter 能查到本视图 Resources 里的调色板（OkBrush/ErrBrush…），
        // 否则状态点与状态文字取不到颜色、渲染成透明
        BrushKeyConverter.Scope = this;

        GridContainers.ItemsSource = _containers;
        GridImages.ItemsSource = _images;
        GridNetworks.ItemsSource = _networks;
        ListSearchResults.ItemsSource = _searchResults;

        _realtime.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(AppSettings.StatsIntervalMs, 1000, 60000));
        _realtime.Tick += async (s, e) => await RefreshRealtimeStatsAsync();

        ListMirrors.ItemsSource = MirrorPresets();

        InitDistroPage();
        InitEnvChecks();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureStarted();

    /// <summary>0/1 标志：启动流程只跑一次（Loaded 与「托盘常驻」可能都来叫它）。</summary>
    private int _coreStarted;

    /// <summary>
    /// 启动初始化（检测环境 / 首次刷新 / 自启恢复）。
    ///
    /// 正常情况下由 <see cref="OnLoaded"/> 触发；但 <c>--tray</c> 常驻模式**不显示窗口**，
    /// Loaded 永远不会来 —— 那种情况下宿主主动调这个方法，否则容器/发行版列表一直空着。
    /// </summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _coreStarted, 1) != 0) return;
        _ = RunStartupCoreAsync();
    }

    private async Task RunStartupCoreAsync()
    {
        try { await OnLoadedCoreAsync(); }
        catch (Exception ex) { try { Log("初始化失败：" + ex.Message, TxtLog); } catch { } }
        finally { _startupTcs.TrySetResult(true); }
    }

    private async System.Threading.Tasks.Task OnLoadedCoreAsync()
    {
        _ready = true;
        LoadSettingsIntoUi();

        // 宿主是 UserControl，装不到窗口级类处理器上，这里对本视图单独按 DPI 设排版模式。
        TextQuality.ApplyTo(this);

        TxtAbout.Text =
            "WSL 管理器 v2.0 —— 发行版（wsl）与容器（wslc）的统一可视化工具，仅调用官方 wsl / wslc 命令。\r\n" +
            "容器部分依赖 WSL 2.9.3 及以上版本（wslc 随 WSL 提供）。\r\n" +
            "设置里预置的镜像加速地址为第三方公开站点，与本工具作者无关，可用性不保证。";

        await DetectAsync();
        // 默认落在「环境与安装」页，环境检查随首屏跑一次（下次进入不重复跑）
        EnsureEnvChecked();
        await RefreshContainersAsync(silent: true);
        await RefreshImagesAsync(silent: true);

        // 开机自启：按启动早期取走的名单恢复容器（名单已在 AutoStart.CapturePendingOnStartup
        // 里取走，这里再读文件会被上面那次刷新刷成空）
        if (AutoStart.LaunchedByAutoStart && AutoStart.PendingOnStartup.Count > 0)
            await RunAutoStartRestoreAsync(AutoStart.PendingOnStartup, fromTray: true);

        ApplyStartupPage();
        ApplyStartupSearch();
        _ = Dispatcher.BeginInvoke(new Action(ApplyStartupDialog), DispatcherPriority.Background);

        ApplyStartupDiagnostics();

        // --click-start=<容器名>：走真实 UI 路径点一次「启动」，导出界面文案（见方法注释）
        await ClickStartAsync();

        await DumpLayoutAsync();
        await HandleScreenshotAsync();

        // 兜底：只带 --dump-layout / --textmode 而不带 --screenshot 时，
        // HandleScreenshotAsync 会直接 return，没人退进程 → 命令行永远挂着（实测踩过）。
        ExitIfRequested();
    }

    // ────────────────────────── 验收用启动参数 ──────────────────────────
    // 只在显式带参数时生效，正常运行完全不受影响。
    // 存在的理由：「按钮有没有被裁掉」「列表有没有滚动条」这类结论靠截图定不了案
    // ——截图是像素，差几个 px 肉眼分不出来，必须打印真实布局矩形来量。

    private void ApplyStartupDiagnostics()
    {
        var argv = Environment.GetCommandLineArgs();

        // --size=WxH：指定窗口尺寸。WPF 里 Width 小于 MinWidth 时 MinWidth 生效，
        // 所以传一个小值就等于"按最小宽度跑"，正好用来验证窄窗口下的表现。
        var sizeArg = argv.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase));
        if (sizeArg != null)
        {
            var p = sizeArg.Substring("--size=".Length).Split('x', 'X');
            if (p.Length == 2
                && double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var w)
                && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var h))
            {
                Width = w;
                Height = h;
            }
        }

        ApplyStressData();
        ApplyDemoMode();
        ApplyFillDemo();
    }

    /// <summary>
    /// --demo：**纯演示数据**模式，用于对外截图 / 文档配图。
    /// 清掉本机真实的容器、镜像与状态栏版本信息，只留构造出来的样例，
    /// 避免把本机环境（容器名、版本号等）带进公开材料。
    /// </summary>
    private void ApplyDemoMode()
    {
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            return;

        _demoMode = true;

        _containers.Clear();
        _images.Clear();

        // 关键：停掉实时刷新定时器，否则它会在截图前把演示数据换成真实数据
        try { _realtime.Stop(); } catch { }

        var demo = new (string Name, string Image, string Ports, bool Running, double Cpu, double Mem)[]
        {
            ("web-frontend",  "library/nginx:1.27-alpine",  "8080:80",   true,  0.42, 3.10),
            ("api-server",    "library/python:3.12-slim",   "5000:5000", true,  1.85, 12.40),
            ("redis-cache",   "library/redis:7-alpine",     "6379:6379", true,  0.15, 1.20),
            ("postgres-main", "library/postgres:16",        "5432:5432", true,  0.90, 8.65),
            ("worker-queue",  "library/python:3.12-slim",   "",          false, 0.00, 0.00),
            ("nginx-gateway", "library/nginx:stable",       "80:80,443:443", false, 0.00, 0.00),
        };

        for (int i = 0; i < demo.Length; i++)
        {
            var d = demo[i];
            var c = new ContainerInfo
            {
                Id = $"demo{i:D12}",
                Name = d.Name,
                Image = d.Image,
                Ports = d.Ports,
                Status = d.Running ? "Up 12 minutes" : "Exited (0) 3 minutes ago",
            };
            c.ApplyStatus(d.Running ? "running" : "exited", c.Status);
            if (d.Running)
                c.ApplyStats(new ContainerStats
                {
                    Cpu = $"{d.Cpu:F2}%",
                    MemPercent = $"{d.Mem:F2}%",           // 列表内存列的进度条读这个
                    Mem = $"{d.Mem * 25:F0}MB / 2GB",
                    Net = "1.2MB / 0.8MB",
                    Pids = "12"
                });
            _containers.Add(c);
        }

        // 状态栏不带本机版本号
        Ui(() => TxtStatus.Text = "就绪");

        // 若正停在备份页，把演示容器同步进下拉（页面切换只在进入时刷新一次）
        if (NavEnv.IsChecked == true) EnsureEnvChecked();
        if (NavBackup.IsChecked == true) { RefreshBackupContainers(); RefreshSessionDiskInfo(); }
    }

    /// <summary>--fill-demo：往镜像页两个输入框填演示文本，便于截图核对排版（对齐 / 内边距）。</summary>
    private void ApplyFillDemo()
    {
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--fill-demo", StringComparison.OrdinalIgnoreCase)))
            return;

        TxtPullImage.Text = "docker.1ms.run/library/nginx:latest";
        TxtRepoQuery.Text = "nginx-stable-alpine-very-long-keyword";
    }

    /// <summary>
    /// --stress=N：往两个列表灌 N 行假数据（纯内存，不碰 wslc、不写盘），把列表撑出滚动条。
    /// 做成幂等：刷新定时器会把列表清空重填，量测前需要能再灌一次而不叠加。
    /// </summary>
    private void ApplyStressData()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--stress=", StringComparison.OrdinalIgnoreCase));
        if (arg == null || !int.TryParse(arg.Substring("--stress=".Length), out var n) || n <= 0) return;
        n = Math.Min(n, 80);

        for (int i = _containers.Count - 1; i >= 0; i--)
            if ((_containers[i].Name ?? "").StartsWith("demo-container-", StringComparison.Ordinal))
                _containers.RemoveAt(i);
        for (int i = _images.Count - 1; i >= 0; i--)
            if ((_images[i].Repository ?? "").Contains("/library/demo", StringComparison.Ordinal))
                _images.RemoveAt(i);

        for (int i = 1; i <= n; i++)
        {
            _containers.Add(new ContainerInfo
            {
                Id = string.Format("stress{0:D12}", i),
                Name = $"demo-container-{i:00}",
                Image = "registry.example.com/library/alpine:latest",
                State = i % 3 == 0 ? ContainerState.Exited : ContainerState.Running,
                Status = i % 3 == 0 ? "Exited (0)" : "Up 2 minutes"
            });
            _images.Add(new ImageInfo
            {
                Repository = "registry.example.com/library/demo" + i,
                Tag = "latest",
                Id = string.Format("stress{0:D12}abcdef", i),
                Size = "12.3MB",
                Created = "2026-10-10 00:00:00 +0800 CST"
            });
        }
    }

    /// <summary>
    /// --dump-layout=&lt;路径&gt;：把关键控件的真实布局矩形写成文本。
    /// 必须等布局稳定后再量：DataGrid 的列宽与 ScrollViewer 视口是渲染阶段才算出来的，
    /// 紧接着 UpdateLayout() 去读会拿到中间值（列宽全 20、ViewportHeight=13 这种），
    /// 拿中间值下结论等于没量。
    /// </summary>
    private async System.Threading.Tasks.Task DumpLayoutAsync()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var path = arg.Substring("--dump-layout=".Length).Trim().Trim('"');
        if (path.Length == 0) return;

        await System.Threading.Tasks.Task.Delay(1500);
        ApplyStressData();   // 刷新定时器可能已把列表清空重填，量测前补灌一次
        UpdateLayout();
        var sb = new StringBuilder();
        sb.Append(BuildLayoutReport());
        await AppendEnvReportAsync(sb);
        await AppendDistroReportAsync(sb);
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    /// <summary>
    /// 「环境与安装」页（v2.4）的量测：默认态量按钮与信息网格的位置，
    /// 再**真实调用一次**「环境检查」按钮的处理器，量弹出卡片是否正常打开与填充。
    /// 为什么要在 dump 里点一次：弹窗只在点按钮时创建，不真点就量不到 ——
    /// 量完即退进程，不留任何状态。
    /// </summary>
    private async System.Threading.Tasks.Task AppendEnvReportAsync(StringBuilder sb)
    {
        Rect R(FrameworkElement el)
        {
            if (el == null || !el.IsLoaded) return Rect.Empty;
            var p = el.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(p, new Size(el.ActualWidth, el.ActualHeight));
        }
        string F(Rect r) => r.IsEmpty ? "未布局" : $"x={r.X:0.#} y={r.Y:0.#} {r.Width:0.#}×{r.Height:0.#}";

        sb.AppendLine();
        sb.AppendLine("== 环境与安装（v2.0：检查改弹窗） ==");
        sb.AppendLine($"  环境检查按钮   {F(R(BtnEnvCheck))}");
        sb.AppendLine($"  信息卡         {F(R(PanelEnv))}");

        // v2.0 加：八行信息（版本 / 数量 / 五条路径）逐行量 —— 新增的行有没有挤到卡片外、
        // 路径有没有真的填上值，都要用矩形和文本说话。
        var envRows = new (string Label, TextBlock Tb)[]
        {
            ("WSL 版本", TxtVerWsl), ("内核版本", TxtVerKernel), ("默认发行版", TxtVerDistro),
            ("容器数量", TxtVerContainers), ("镜像数量", TxtVerImages), ("wslc 版本", TxtVerWslcVer),
            ("WSL 虚拟磁盘", TxtVerWslDisk), ("wslc 数据目录", TxtVerWslcDataDir),
            ("wslc 虚拟磁盘", TxtVerWslcDisk), ("wslc 路径", TxtVerWslcPath),
            ("运行日志", TxtVerLogPath), ("发行版共享路径", TxtVerSharePath),
        };
        var envRect = R(PanelEnv);
        foreach (var row in envRows)
        {
            var rr = R(row.Tb);
            bool inside = !rr.IsEmpty && !envRect.IsEmpty && rr.Bottom <= envRect.Bottom + 0.5;
            string txt = (row.Tb.Text ?? "");
            if (txt.Length > 62) txt = txt.Substring(0, 62) + "…";
            sb.AppendLine($"  {row.Label,-14} {F(rr)} {(inside ? "卡内" : "超出卡片 !!")} \"{txt}\"");
        }

        // 模拟点「环境检查」：开弹窗 + 重新检测
        OnEnvCheckClick(BtnEnvCheck, new RoutedEventArgs());
        UpdateLayout();
        // 一直等到「点按钮后的那一轮检查」跑完为止（_checkGen 变了且不在检查中）；
        // 只看 !_envChecking 会落在「预检刚结束、本轮还没开始」的空档，量到旧值。
        int gen0 = _checkGen;
        for (int i = 0; i < 80; i++)   // 上限 20 秒
        {
            await System.Threading.Tasks.Task.Delay(250);
            UpdateLayout();
            if (_checkGen > gen0 && !_envChecking) break;
        }
        sb.AppendLine("  —— 点「环境检查」后 ——");
        sb.AppendLine(_envDlg != null
            ? $"  检查弹窗       {_envDlg.ActualWidth:0.#}×{_envDlg.ActualHeight:0.#} 可见={_envDlg.IsVisible}"
            : "  检查弹窗       未创建（静默探针不弹窗）");
        sb.AppendLine($"  明细行数       {_checks.Count}");
        if (_envDlg != null)
        {
            sb.AppendLine($"  一键修复       文本=\"{_envDlg.BtnFixAll.Content ?? "-"}\" 可用={_envDlg.BtnFixAll.IsEnabled}");
            sb.AppendLine($"  弹窗副标题     \"{_envDlg.TxtSub.Text}\"");
        }
        sb.AppendLine($"  摘要小字       \"{TxtEnvCheckBrief.Text}\"");

        // 复制自检（v2.0 修的就是这条报警）：跑一次真实的「复制」，把状态栏回执和剪贴板里的
        // 实际内容都打出来。判据是「剪贴板里有没有这段文本」—— 回执说成功但剪贴板是空的，同样是 bug。
        // 探针会占用剪贴板，验完原样还原，不能把用户正复制着的东西冲掉。
        sb.AppendLine("  —— 复制自检 ——");
        string copyTarget = (TxtVerLogPath.Text ?? "").Trim();
        string clipBackup = null;
        bool hadClip = false;
        try { hadClip = Clipboard.ContainsText(); if (hadClip) clipBackup = Clipboard.GetText(); } catch { }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CopyPath(copyTarget, "运行日志路径");
        sw.Stop();
        await System.Threading.Tasks.Task.Delay(150);
        string clip = "";
        try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { }
        sb.AppendLine($"      目标     \"{copyTarget}\"");
        sb.AppendLine($"      回执     \"{TxtStatus.Text}\"");
        sb.AppendLine($"      耗时     {sw.ElapsedMilliseconds} ms");
        sb.AppendLine($"      剪贴板    {(clip == copyTarget ? "与目标一致" : "不一致 !!")}");
        if (hadClip) { try { Clipboard.SetDataObject(clipBackup, true); } catch { } }
    }

    /// <summary>
    /// 发行版页（v2.5）的量测：切过去等两轮刷新（CPU% 要两次 /proc/stat 采样做差），
    /// 然后把卡片几何与利用率读数逐一打出来 —— 「居中没有」和「有没有数字」都用数说话。
    /// </summary>
    private async System.Threading.Tasks.Task AppendDistroReportAsync(StringBuilder sb)
    {
        Rect R(FrameworkElement el)
        {
            if (el == null || !el.IsLoaded) return Rect.Empty;
            var p = el.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(p, new Size(el.ActualWidth, el.ActualHeight));
        }
        string F(Rect r) => r.IsEmpty ? "未布局" : $"x={r.X:0.#} y={r.Y:0.#} {r.Width:0.#}×{r.Height:0.#}";

        SelectPageForTest("distro");
        UpdateLayout();

        // 等到至少一张运行中的卡片拿到了真实 CPU%（首轮只有内存）
        bool got = false;
        for (int i = 0; i < 40 && !got; i++)     // 上限 12 秒
        {
            await System.Threading.Tasks.Task.Delay(300);
            UpdateLayout();
            got = _distros.Any(d => d.IsRunning && d.UsageCpu.StartsWith("CPU ", StringComparison.Ordinal)
                                                   && !d.UsageCpu.Contains('—'));
        }

        sb.AppendLine();
        sb.AppendLine("== 发行版页（v2.0：卡片 CPU / 内存） ==");
        sb.AppendLine($"  卡片 {_distros.Count} 张 · 运行中 {_distros.Count(d => d.IsRunning)} · "
                      + $"利用率{(got ? "已出现" : "未读到 !!（无运行中发行版或采样超时）")}");
        sb.AppendLine($"  列表区         {F(R(ListDistros))}");

        var panels = Descendants(ListDistros).OfType<FrameworkElement>()
                                            .Where(e => e.Name == "UsagePanel").ToList();
        sb.AppendLine($"  利用率面板     找到 {panels.Count} 个");
        for (int i = 0; i < panels.Count && i < _distros.Count; i++)
        {
            var panel = panels[i];
            var card = Ancestor<Border>(panel);
            var rp = R(panel);
            var rc = R(card);
            double dc = rp.IsEmpty || rc.IsEmpty ? double.NaN : Math.Abs((rp.X + rp.Width / 2) - (rc.X + rc.Width / 2));
            sb.AppendLine($"   #{i + 1} {_distros[i].Name}  卡片 {F(rc)}");
            sb.AppendLine($"       面板 {F(rp)}  |Δ中心x|={dc:0.#} px");
            sb.AppendLine($"       文本 \"{_distros[i].UsageCpu}\" / \"{_distros[i].UsageMem}\"");

            // v2.5.1：一行拆成「标签 + 数值」，4 段必须同字号同字重；只有数值段跟档位换色。
            var tbs = Descendants(panel).OfType<TextBlock>()
                                       .Where(t => t.Name.StartsWith("Usage", StringComparison.Ordinal))
                                       .ToList();
            foreach (var tb in tbs)
            {
                string lv = tb.Name == "UsageCpuValue" ? _distros[i].UsageCpuLevel
                          : tb.Name == "UsageMemValue" ? _distros[i].UsageMemLevel
                          : "（标签·固定次要色）";
                sb.AppendLine($"       {tb.Name,-15} \"{tb.Text}\" 字号 {tb.FontSize:0.#} 字重 {tb.FontWeight} "
                              + $"颜色 {Hex(tb.Foreground)} 档位 \"{lv}\"");
            }
            bool sameFont = tbs.Count == 4
                            && tbs.All(t => Math.Abs(t.FontSize - 12.5) < 0.01 && t.FontWeight == FontWeights.SemiBold);
            sb.AppendLine($"       同字体（4 段一致）={(sameFont ? "是" : "否 !!")}");

            // 对齐：两行的数值必须同一左边缘（早先两个独立 StackPanel 各自居中，数字错位）
            var named = new Dictionary<string, TextBlock>();
            foreach (var tb in tbs) if (!named.ContainsKey(tb.Name)) named[tb.Name] = tb;
            if (named.ContainsKey("UsageCpuValue") && named.ContainsKey("UsageMemValue"))
            {
                var r1 = R(named["UsageCpuValue"]);
                var r2 = R(named["UsageMemValue"]);
                var l1 = R(named["UsageCpuLabel"]);
                double dx = Math.Abs(r1.X - r2.X);
                sb.AppendLine($"       数值左缘 x={r1.X:0.##} / {r2.X:0.##}  |Δx|={dx:0.##} px "
                              + $"{(dx <= 0.5 ? "对齐" : "错位 !!")}");
                sb.AppendLine($"       标签右缘 x={l1.X + l1.Width:0.##} 宽 {l1.Width:0.##}");
            }
        }

        // 阈值映射（纯函数）：边界正好 60 / 85 时归哪一档，用数说话
        sb.AppendLine("  —— 阈值映射 ——");
        foreach (var p in new[] { 0.0, 59.9, 60.0, 84.9, 85.0, 100.0 })
            sb.AppendLine($"       {p:0.#}% → \"{UsageLevel(p)}\"");

        // 档位 → 颜色：真实负载一般读不到 60%+，所以只改 ViewModel 的档位、不动读数，
        // 验证三条 DataTrigger 都真的接上了（否则黄/红永远没人看见过就等于没做）。
        var first = _distros.FirstOrDefault(d => d.IsRunning);
        var firstTb = panels.Count > 0
            ? Descendants(panels[0]).OfType<TextBlock>().FirstOrDefault(t => t.Name == "UsageCpuValue")
            : null;
        var labelTb = panels.Count > 0
            ? Descendants(panels[0]).OfType<TextBlock>().FirstOrDefault(t => t.Name == "UsageCpuLabel")
            : null;
        if (first != null && firstTb != null)
        {
            string lv0 = first.UsageCpuLevel;
            sb.AppendLine("  —— 档位→颜色（只改档位，读数不变）——");
            foreach (var lv in new[] { "ok", "warn", "danger" })
            {
                first.UsageCpuLevel = lv;
                await System.Threading.Tasks.Task.Delay(160);
                UpdateLayout();
                sb.AppendLine($"       档位 \"{lv}\" → 数值 {Hex(firstTb.Foreground)}  标签 {Hex(labelTb?.Foreground)}");
            }
            first.UsageCpuLevel = lv0;
        }

        SelectPageForTest("env");
        UpdateLayout();
    }

    /// <summary>Brush → #RRGGBBAA，用于 dump 里比对颜色档位。</summary>
    private static string Hex(Brush b)
        => b is SolidColorBrush s ? s.Color.ToString() : (b == null ? "null" : b.GetType().Name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    private static T Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(node); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private string BuildLayoutReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 窗口 ==");
        var win = HostWindow;
        sb.AppendLine($"  窗口 {win?.ActualWidth:0.#} x {win?.ActualHeight:0.#}   MinWidth={win?.MinWidth} MinHeight={win?.MinHeight}");
        sb.AppendLine();
        sb.AppendLine("== 文字渲染 ==");
        sb.AppendLine("  " + TextQuality.Describe(win));
        sb.AppendLine($"  [调试] TxtSessionDisk 文本 = \"{TxtSessionDisk?.Text}\"  可见性={TxtSessionDisk?.Visibility}");
        var realMode = TextOptions.GetTextFormattingMode(this);
        var realAa = TextOptions.GetTextRenderingMode(this);
        sb.AppendLine($"  实际生效 TextFormattingMode={realMode}  TextRenderingMode={realAa}");
        sb.AppendLine($"  窗口是否分层（AllowsTransparency）={win?.AllowsTransparency}" +
                      "   （分层窗口 WPF 一律关 ClearType，弹窗靠 RenderOptions.ClearTypeHint 找回来）");
        // 抽查一个弹窗式分层窗口卡片上有没有开 ClearTypeHint
        sb.AppendLine();
        AppendGridReport(sb, GridContainers, "容器列表");
        AppendGridReport(sb, GridImages, "镜像列表");
        return sb.ToString();
    }

    private void AppendGridReport(StringBuilder sb, DataGrid dg, string label)
    {
        var origin = dg.TransformToAncestor(this).Transform(new Point(0, 0));
        var sv = FindDescendant<ScrollViewer>(dg);

        sb.AppendLine($"== {label} ==");
        sb.AppendLine($"  位置 x={origin.X:0.#} y={origin.Y:0.#}   尺寸 {dg.ActualWidth:0.#} x {dg.ActualHeight:0.#}");
        sb.AppendLine($"  行数 {dg.Items.Count}");
        if (sv != null)
        {
            sb.AppendLine($"  垂直滚动条 {sv.ComputedVerticalScrollBarVisibility}   水平滚动条 {sv.ComputedHorizontalScrollBarVisibility}");
            sb.AppendLine($"  视口 {sv.ViewportWidth:0.#} x {sv.ViewportHeight:0.#}   可滚动 {sv.ScrollableWidth:0.#} x {sv.ScrollableHeight:0.#}");
        }
        var cols = new List<string>();
        foreach (var c in dg.Columns) cols.Add($"{c.Header}={c.ActualWidth:0.#}");
        sb.AppendLine("  列宽 " + string.Join("  ", cols));

        // 行内按钮：必须按「行」分组量 —— 运行中的行比已停止的行多一个按钮（强制），
        // 只抽第一行会漏掉这一行，正是它才可能被裁。
        // 关键：判定边界要用「内容视口右边缘」，而不是 DataGrid 自身右边缘 ——
        // 垂直滚动条约占 17.6px，用 DataGrid 宽度当边界会得出假绿的「在界内」。
        var svOrigin = sv != null ? sv.TransformToAncestor(this).Transform(new Point(0, 0)) : origin;
        double contentLeft = svOrigin.X;
        double contentRight = sv != null ? svOrigin.X + sv.ViewportWidth : origin.X + dg.ActualWidth;

        sb.AppendLine($"  内容视口 x={contentLeft:0.#}..{contentRight:0.#}（列宽合计 {dg.Columns.Sum(c => c.ActualWidth):0.#}）");

        var perRow = new Dictionary<int, List<(string name, double l, double r, double w)>>();
        int idx = 0;
        foreach (var b in Descendants<Button>(dg))
        {
            var row = FindAncestor<DataGridRow>(b);
            if (row == null) continue;
            int ri = row.GetIndex();
            var p = b.TransformToAncestor(this).Transform(new Point(0, 0));
            if (!perRow.TryGetValue(ri, out var list)) perRow[ri] = list = new List<(string, double, double, double)>();
            list.Add((b.Content?.ToString() ?? "", p.X, p.X + b.ActualWidth, b.ActualWidth));
            idx++;
        }

        sb.AppendLine($"  行内按钮 共 {idx} 个，覆盖 {perRow.Count} 行");
        foreach (var kv in perRow.OrderBy(k => k.Key).Take(6))
        {
            var visible = kv.Value.Where(b => b.w > 0.5).ToList();
            if (visible.Count == 0) continue;
            double maxRight = visible.Max(b => b.r);
            double need = maxRight - contentLeft;
            sb.AppendLine($"    行#{kv.Key} {visible.Count} 个可见按钮  {visible[0].l:0.#}..{maxRight:0.#}  需 {need:0.#}px"
                        + $"  {(maxRight > contentRight + 0.5 ? $"越界 {(maxRight - contentRight):0.#}px!" : "在界内")}");
            sb.AppendLine($"       " + string.Join(" ", visible.Select(b => $"[{b.name}]")));
        }
        sb.AppendLine();
    }

    private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T t) return t;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindDescendant<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>
    /// --screenshot=&lt;路径&gt;：把窗口用 WPF 渲染管线导出为 PNG（不依赖 DWM/窗口是否可见），
    /// 供自动化验收使用；配合 --exit-after-shot 截完即退出。
    /// </summary>
    private async System.Threading.Tasks.Task HandleScreenshotAsync()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var path = arg.Substring("--screenshot=".Length).Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return;

        await System.Threading.Tasks.Task.Delay(2500);

        string err = null;
        try
        {
            // --grab：连 DWM 绘制的非客户区一起抓（用于验证窗口外框）。
            // 普通模式用 RenderTargetBitmap，只覆盖 WPF 自己的可视化树。
            bool grab = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--grab", StringComparison.OrdinalIgnoreCase));

            if (grab)
            {
                await GrabFromScreenAsync(path);
            }
            else
            {
                // --dialog 模式下渲染的是被打开的对话框，而不是主窗口
                var target = _shotDialog != null ? (FrameworkElement)_shotDialog : this;
                var dpi = VisualTreeHelper.GetDpi(target);
                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(target);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = File.Create(path);
                enc.Save(fs);
            }
        }
        catch (Exception ex) { err = ex.ToString(); }

        if (err != null)
        {
            try { File.WriteAllText(path + ".err.txt", err); } catch { }
        }

        ExitIfRequested();
    }

    /// <summary>
    /// --click-start=&lt;容器名&gt; [--dump-ui=&lt;路径&gt;]：在**真实界面上按下**该行的「启动」按钮，
    /// 然后把状态栏、操作日志、列表状态列的**真实文本**导出。
    ///
    /// 为什么必须真的去点：本次修的正是「提示成功、状态还是已停止」——
    /// 对错取决于三处文本的组合（状态栏 / 操作日志 / 列表状态），
    /// 只调服务层（`WslcService.StartAsync` + `InspectStateAsync`）证明不了**界面文案**对不对。
    /// 默认输出到程序目录下的 click_start_result.txt。
    /// </summary>
    private async System.Threading.Tasks.Task ClickStartAsync()
    {
        var argv = Environment.GetCommandLineArgs();
        var arg = argv.FirstOrDefault(a => a.StartsWith("--click-start=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;

        var name = arg.Substring("--click-start=".Length).Trim().Trim('"');
        var outArg = argv.FirstOrDefault(a => a.StartsWith("--dump-ui=", StringComparison.OrdinalIgnoreCase));
        var outPath = outArg?.Substring("--dump-ui=".Length).Trim().Trim('"') ?? "";
        if (outPath.Length == 0) outPath = Path.Combine(AppContext.BaseDirectory, "click_start_result.txt");

        var lines = new List<string> { "== 真实 UI 路径：点击「启动」 ==", "目标容器：" + name, "" };
        try
        {
            // 必须先切到容器页：DataGrid 是虚拟化的，而页面切走时容器页整块是 Collapsed
            // —— 折叠状态下不会生成行容器，视觉树里一个 DataGridRow 都找不到（踩过）。
            NavContainer.IsChecked = true;

            await System.Threading.Tasks.Task.Delay(2500);   // 与截图同一节奏：等列表落地
            UpdateLayout();
            GridContainers.UpdateLayout();

            var row = Descendants<DataGridRow>(GridContainers)
                .FirstOrDefault(r => (r.Item as ContainerInfo)?.Name == name);

            if (row == null)
            {
                lines.Add("!! 列表里找不到该容器行（可能没刷新出来）");
            }
            else
            {
                lines.Add("点前列表状态 : " + (row.Item as ContainerInfo)?.StateText);
                var btn = Descendants<Button>(row).FirstOrDefault(b => (b.Content as string) == "启动");

                if (btn == null)
                {
                    lines.Add("!! 该行没有「启动」按钮（运行中的容器只显示 停止/强制）");
                }
                else
                {
                    lines.Add($"按钮         : Content=\"启动\" IsEnabled={btn.IsEnabled}");
                    // 真的走一遍 Click —— 与用户手点完全同一条路径（OnRowStartClick → RowActionAsync）
                    btn.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));

                    await System.Threading.Tasks.Task.Delay(4500);   // 操作 + 复查(~1.1s) + 刷新

                    // ⚠ 刷新后**不能再用刚才那个 DataGridRow**：列表重建过，行容器已被回收，
                    //   row.Item 会变成 null（第一次量就踩了这个坑）。改成按名字去数据源里找。
                    var after = _containers.FirstOrDefault(x => x.Name == name);

                    lines.Add("");
                    lines.Add("状态栏文本   : " + TxtStatus.Text);
                    // ⚠ 状态色在**状态圆点**上（SetStatus 只改 ElpStatus.Fill），
                    //   文字色是固定的次要色，量它永远是灰的 —— 别量错对象。
                    lines.Add("状态栏圆点色 : " + (ElpStatus.Fill as SolidColorBrush)?.Color
                              + "   (OkBrush=#FF12A150  WarnBrush=#FFD9820A  ErrBrush=#FFE0484D)");
                    lines.Add("");
                    lines.Add("操作日志：");
                    foreach (var l in TableParser.Lines(TxtContainerLog.Text)) lines.Add("  " + l);
                    lines.Add("");
                    lines.Add("点后列表状态 : " + (after?.StateText ?? "(没找到)")
                              + "   （原始: " + (after?.Status ?? "") + "）");
                }
            }
        }
        catch (Exception ex) { lines.Add("!! 异常: " + ex); }

        try { File.WriteAllText(outPath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false)); } catch { }
    }

    private bool _exitRequested;

    /// <summary>
    /// 带了 --exit-after-shot 就退出（幂等）。抽出来的原因：
    /// 只在截图分支里判会把「只导布局报告、不截图」的场景漏掉，那条路径没人退进程。
    /// </summary>
    private void ExitIfRequested()
    {
        if (_exitRequested) return;
        if (!Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--exit-after-shot", StringComparison.OrdinalIgnoreCase))) return;
        _exitRequested = true;
        ExitApp();
    }

    /// <summary>把窗口置顶后用屏幕抓取保存（含 DWM 非客户区），用于验证窗口外框。</summary>
    private async System.Threading.Tasks.Task GrabFromScreenAsync(string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(HostWindow).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0040);
        NativeMethods.SetForegroundWindow(hwnd);
        await System.Threading.Tasks.Task.Delay(700);

        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// F5：刷新当前所在子页。宿主窗口只负责转发，具体刷哪一页由这里判断 ——
    /// 页的状态只有本视图知道，放到宿主里就会变成「宿主猜当前页」。
    /// </summary>
    public async Task RefreshActivePageAsync()
    {
        if (NavDistro.IsChecked == true) { await RefreshDistrosAsync(); return; }
        if (NavContainer.IsChecked == true) { await RefreshContainersAsync(); return; }
        if (NavImage.IsChecked == true) { await RefreshImagesAsync(); return; }
        if (NavNetwork.IsChecked == true) { await RefreshNetworksAsync(); return; }
        if (NavBackup.IsChecked == true) { RefreshBackupContainers(); RefreshSessionDiskInfo(); return; }
        if (NavSettings.IsChecked == true) { LoadSettingsIntoUi(); return; }
        await DetectAsync();
    }

    /// <summary>
    /// 宿主切换到别的模块时调用：离开时不后台空转容器状态轮询，回来时按当前子页恢复。
    /// （v2.2 只剩一个模块，宿主不再切模块；保留是为了自检与将来的复用。）
    /// </summary>
    public void SetModuleActive(bool active)
    {
        try
        {
            if (!active)
            {
                _realtime.Stop();
                return;
            }
            if (NavContainer.IsChecked == true)
            {
                _realtime.Start();
                _ = RefreshContainersAsync(silent: true);
            }
        }
        catch { }
    }

    /// <summary>自检用：按名字切到某个子页（env / container / image / backup / settings）。</summary>
    public void SelectPageForTest(string page)
    {
        switch ((page ?? "").ToLowerInvariant())
        {
            case "env": NavEnv.IsChecked = true; break;
            case "distro": NavDistro.IsChecked = true; break;
            case "container": NavContainer.IsChecked = true; break;
            case "image": NavImage.IsChecked = true; break;
            case "backup": NavBackup.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
        }
    }

    /// <summary>退出整个应用（宿主窗口与托盘由 WSL 管理器主窗口负责收尾）。</summary>
    private void ExitApp()
    {
        Ui(() =>
        {
            Application.Current?.Shutdown();
        });
    }

    private void ApplyStartupPage()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            switch (arg.Substring("--page=".Length).Trim().ToLowerInvariant())
            {
                case "container": NavContainer.IsChecked = true; break;
                case "image": NavImage.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
                case "env": NavEnv.IsChecked = true; break;
                case "distro": NavDistro.IsChecked = true; break;
                case "backup": NavBackup.IsChecked = true; break;
            }
        }
        catch { }
    }

    /// <summary>
    /// --search=关键字：启动后自动填入检索框并触发一次检索。
    /// 仅用于自动化验收截图，正常使用不受影响。
    /// </summary>
    private void ApplyStartupSearch()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--search=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            var q = arg.Substring("--search=".Length).Trim().Trim('"');
            if (q.Length == 0) return;

            TxtRepoQuery.Text = q;
            OnRepoSearchClick(this, null);
        }
        catch { }
    }

    private void ApplyStartupDialog()
    {
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--dialog=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;

            var v = arg.Substring("--dialog=".Length).Trim().ToLowerInvariant();

            // 截图/布局量测模式下改用非模态 Show()：ShowDialog 会阻塞 UI 线程，
            // 让随后的截图任务永远排不上队（表现为「截出来没有对话框」）。
            bool shotMode = Environment.GetCommandLineArgs()
                .Any(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase));

            Window dlg = null;
            if (v == "create")
                dlg = new CreateContainerDialog(_images, _networks.Select(n => n.Name)) { Owner = HostWindow };
            else if (v == "detail" && _containers.Count > 0)
                dlg = new ContainerDetailDialog(_containers[0]) { Owner = HostWindow };
            else if (v == "logs" && _containers.Count > 0)
                dlg = new ContainerLogsDialog(_containers[0]) { Owner = HostWindow };
            else if (v == "terminal" && _containers.Count > 0)
            {
                // --pick-custom：验收用，强制切到「自定义命令」分支后再截图
                bool demo = Environment.GetCommandLineArgs()
                    .Any(a => a.Equals("--pick-custom", StringComparison.OrdinalIgnoreCase));
                dlg = new TerminalDialog(_containers[0], demo) { Owner = HostWindow };
            }
            else if (v == "confirm")
            {
                var ad = AppDialog.Build("删除容器",
                    "确定要删除容器「wslctest」吗？\n该操作不可撤销。",
                    "删除", danger: true);
                ad.Owner = HostWindow;
                dlg = ad;
            }
            else if (v == "info")
            {
                var ad = AppDialog.Build("容器完整名称", "wslctest\n\n（已复制到剪贴板）",
                    "知道了", danger: false, showCancel: false);
                ad.Owner = HostWindow;
                dlg = ad;
            }

            if (dlg == null) return;

            if (shotMode)
            {
                _shotDialog = dlg;
                dlg.Show();
            }
            else
            {
                dlg.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            SetStatus("打开对话框失败：" + ex.Message, "ErrBrush");
        }
    }

    /// <summary>截图模式下被显式打开的对话框（非模态），截图时作为渲染目标。</summary>
    private Window _shotDialog;

    // ══════════════════════════ 通用 ══════════════════════════
    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.Invoke(a);
    }

    private void Log(string text, TextBox target = null)
    {
        Ui(() =>
        {
            var box = target ?? TxtLog;
            box.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
            box.ScrollToEnd();
        });
    }

    /// <summary>
    /// 忙状态只更新右下角提示，**不锁鼠标指针** —— 启停/检索等操作期间界面仍可用。
    /// </summary>
    private void SetBusy(bool busy, string what = "")
        => Ui(() =>
        {
            _busy = busy;
            TxtBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtBusy.Text = busy ? "● " + what : "";
        });

    private void SetStatus(string text, string brushKey)
        => Ui(() =>
        {
            TxtStatus.Text = text;
            // ⚠ 必须用元素级 + 动态资源引用：
            //   ① 容器调色板挂在**本视图**的 Resources 里，Application.Current.FindResource
            //      找不到这些键 —— 以前就是这么写的，结果状态圆点根本没有填充色
            //      （bill 反馈的"wslc 就绪的绿点没有"），FindResource 还会直接抛异常。
            //   ② 用 SetResourceReference 而不是直接赋 Fill 的对象：主题切换时调色板字典
            //      被整本替换，动态引用会自己重新解析，圆点颜色跟着变。
            ElpStatus.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        });

    private static string One(string s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Replace("\r\n", " ").Replace('\n', ' ').Trim();

    private static ContainerInfo CtxC(object sender)
        => (sender as FrameworkElement)?.DataContext as ContainerInfo;

    private static ImageInfo CtxI(object sender)
        => (sender as FrameworkElement)?.DataContext as ImageInfo;

    // ══════════════════════════ 导航 ══════════════════════════
    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        PanelEnv.Visibility = NavEnv.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDistro.Visibility = NavDistro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelContainer.Visibility = NavContainer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelImage.Visibility = NavImage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = NavNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelBackup.Visibility = NavBackup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavEnv.IsChecked == true) EnsureEnvChecked();
        if (NavBackup.IsChecked == true) { RefreshBackupContainers(); RefreshSessionDiskInfo(); }
        if (NavNetwork.IsChecked == true) _ = RefreshNetworksAsync();

        // 发行版页：进来先刷一次，并按「自动刷新」开关起停 5 秒轮询
        if (NavDistro.IsChecked == true) _ = EnterDistroPageAsync();
        else UpdateDistroTimer();

        if (NavContainer.IsChecked == true)
        {
            _realtime.Start();
            _ = RefreshContainersAsync();
        }
        else
        {
            _realtime.Stop();
            if (NavImage.IsChecked == true) _ = RefreshImagesAsync();
        }
    }

    // ══════════════════════════ 环境 ══════════════════════════

    /// <summary>
    /// 「环境检查」按钮（v2.4）：打开弹出卡片跑逐项检查，同时刷新版本类信息。
    /// 明细不再铺在页面上 —— 主卡只留状态摘要与一行结论小字。
    /// </summary>
    private async void OnEnvCheckClick(object sender, RoutedEventArgs e)
    {
        OpenEnvCheckDialog();
        await DetectAsync();
        // 启动时的静默预检可能还没跑完：等它结束，保证弹窗里出的是本轮结果
        while (_envChecking) await System.Threading.Tasks.Task.Delay(200);
        await RunEnvChecksAsync();
    }

    private async System.Threading.Tasks.Task DetectAsync()
    {
        SetBusy(true, "正在检测环境…");
        Log("检测 WSL / wslc 环境…");

        WslEnvStatus st;
        try { st = await InstallService.DetectAsync(); }
        catch (Exception ex)
        {
            Log("检测失败：" + ex.Message);
            SetBusy(false);
            return;
        }

        _env = st;
        ApplyEnvToUi(st);
        SetBusy(false);
    }

    private void ApplyEnvToUi(WslEnvStatus st)
    {
        Ui(() =>
        {
            TxtVerWsl.Text = string.IsNullOrEmpty(st.WslVersion) ? "—" : st.WslVersion;
            TxtVerKernel.Text = string.IsNullOrEmpty(st.KernelVersion) ? "—" : st.KernelVersion;
            TxtVerWslcPath.Text = st.WslcFound ? st.WslcPath : "未安装";
            TxtVerWslcVer.Text = string.IsNullOrEmpty(st.WslcVersion) ? "—" : st.WslcVersion;
            TxtVerDistro.Text = string.IsNullOrEmpty(st.DefaultDistro) ? "—" : st.DefaultDistro;

            // 两条虚拟磁盘路径：长路径一律省略号 + 悬停看全（卡片宽度不够铺开）
            SetPathText(TxtVerWslDisk, st.WslDiskPath, "未检测到发行版磁盘");
            SetPathText(TxtVerWslcDataDir, st.WslcDataDir, "—");
            SetPathText(TxtVerWslcDisk, st.WslcDiskPath, "—");
            SetPathText(TxtVerLogPath, st.LogPath, "—");
            SetPathText(TxtVerSharePath, st.DefaultSharePath, "未设置默认发行版");
            TxtVerWslcPath.ToolTip = st.WslcFound ? st.WslcPath : null;

            TxtEnvSummary.Text = st.Summary;

            // 小字只留「需要用户做什么」。正常状态下 WSL 版本已经出现在上面的大字（Summary）
            // 和右边的信息网格里，再复述一遍就是噪音 —— 同一屏出现两遍同样的版本号，
            // 用户看不出哪句有用。正常时整块收起，不留空档。
            var hint = "";
            if (!st.WslFound)
                hint = "未检测到 WSL，请在左下角「WSL 升级」里更新到最新稳定版。";
            else if (!st.WslcFound && !st.SupportsWslc)
                hint = $"当前 WSL 版本低于 wslc 的最低要求（{WslEnvStatus.MinWslForWslc}），请先升级。";
            else if (!st.WslcFound)
                hint = "wslc.exe 未在预期路径中找到，可点「环境检查」重试或重启终端。";

            TxtEnvDetail.Text = hint;
            TxtEnvDetail.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 环境圆点同理：键在本视图的调色板里，只能走元素级查找（见 SetStatus 的注释）。
            ElpEnv.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                st.Ready ? "OkBrush" : (st.SupportsWslc ? "WarnBrush" : "ErrBrush"));
        });

        UpdateEnvCounts();

        SetStatus(One(st.Summary),
            st.Ready ? "OkBrush" : (st.WslFound ? "WarnBrush" : "ErrBrush"));

        Log($"检测完成：WSL={st.WslVersion} wslc={(st.WslcFound ? st.WslcPath : "未安装")}");
    }

    private static void SetPathText(System.Windows.Controls.TextBlock tb, string path, string empty)
    {
        tb.Text = string.IsNullOrWhiteSpace(path) ? empty : path;
        tb.ToolTip = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    // ══════════════════════════ 路径复制（v2.5） ══════════════════════════
    // 路径一律按省略号显示（卡片宽度不够铺开），全值只在 ToolTip 里能看 ——
    // 那不够用：用户拿到路径是要粘到资源管理器 / 终端里去的，必须能一键复制。

    private void OnCopyWslDiskPathClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerWslDisk.Text, "WSL 虚拟磁盘路径");

    private void OnCopyWslcDiskPathClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerWslcDisk.Text, "wslc 虚拟磁盘路径");

    private void OnCopyWslcPathClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerWslcPath.Text, "wslc 路径");

    private void OnCopyConfigPathClick(object sender, RoutedEventArgs e) => CopyPath(AppSettings.Location, "配置文件路径");

    private void OnCopyIndexPathClick(object sender, RoutedEventArgs e)
        => CopyPath(ImageSearchService.IndexFilePath, "索引文件路径");

    private void OnCopySessionDiskClick(object sender, RoutedEventArgs e) => CopyPath(_sessionDiskPath, "会话虚拟磁盘路径");

    private void OnCopyWslcDataDirClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerWslcDataDir.Text, "wslc 数据目录");
    private void OnCopyLogPathClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerLogPath.Text, "运行日志路径");
    private void OnCopySharePathClick(object sender, RoutedEventArgs e) => CopyPath(TxtVerSharePath.Text, "发行版共享路径");

    /// <summary>复制路径到剪贴板。空的 / 占位文案（—、未安装…）不算可复制内容。</summary>
    private void CopyPath(string path, string label)
    {
        string v = (path ?? "").Trim();
        if (v.Length == 0 || v == "—" || v == "未安装" || v == "未检测到发行版磁盘" || v == "未检测到")
        {
            SetStatus(label + "当前为空，没有可复制的内容", "WarnBrush");
            return;
        }

        // 判据是「能不能粘出来」，不是「有没有抛异常」—— 见 TrySetClipboard 的注释
        if (TrySetClipboard(v))
            SetStatus("已复制" + label, "OkBrush");
        else
            SetStatus("复制" + label + "失败（剪贴板被别的程序占用，稍后再点一次）", "ErrBrush");
    }

    /// <summary>容器 / 镜像数量（v2.3 并入 WSL 环境信息）。列表刷新后调用；线程上可能来自后台，统一走 Ui()。</summary>
    private void UpdateEnvCounts()
    {
        Ui(() =>
        {
            if (TxtVerContainers == null) return;
            int running = _containers.Count(c => c.IsRunning);
            TxtVerContainers.Text = $"{_containers.Count} 个（运行 {running}）";
            TxtVerImages.Text = _imagesLoaded ? $"{_images.Count} 个" : "读取中…";
        });
    }

    private async void OnWslUpdateStableClick(object sender, RoutedEventArgs e)
        => await RunWslCommandAsync(InstallService.OfficialUpdateArgs(false), "更新");

    private async void OnWslUpdatePreClick(object sender, RoutedEventArgs e)
        => await RunWslCommandAsync(InstallService.OfficialUpdateArgs(true), "更新到预览版");

    private async System.Threading.Tasks.Task RunWslCommandAsync(string[] args, string verb)
    {
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        var wsl = ProcRunner.ResolveExecutable("wsl.exe");
        if (wsl == null) { Log("未找到 wsl.exe，无法执行。"); return; }

        Log($"执行：wsl {string.Join(" ", args)}（官方渠道，下载耗时取决于网络）");
        SetBusy(true, $"正在{verb} WSL…");

        // wsl --update 在下载安装包期间**不输出任何进度**（实测要下 350 MB 左右），
        // 只靠它的 stdout 会让人以为卡死；这里另开一条旁路，盯 %TEMP% 里的安装包报速度。
        using var watchCts = new CancellationTokenSource();
        var watchTask = WatchWslDownloadAsync(watchCts.Token);

        var r = await ProcRunner.RunAsync(wsl, args, 0, default, s => Log(s));

        watchCts.Cancel();
        try { await watchTask; } catch { }

        SetBusy(false);
        Log("退出码：" + r.ExitCode);
        foreach (var line in TableParser.Lines(r.All)) Log("  " + line);

        await DetectAsync();
        await RefreshContainersAsync();
        await RefreshImagesAsync();
    }

    /// <summary>
    /// 旁路监视 `wsl --update` 的下载进度。
    /// 原理：wsl 把安装包下到 %TEMP%\wsl.&lt;版本&gt;.x64.msi，但自身不打印进度；
    /// 于是每秒采样该文件大小，算实时速度并周期性写入日志。
    /// </summary>
    private async System.Threading.Tasks.Task WatchWslDownloadAsync(CancellationToken ct)
    {
        var started = DateTime.Now;
        FileInfo seen = null;
        long lastSize = 0;
        var lastAt = DateTime.Now;
        double speed = 0;                       // bytes/s
        var lastReport = DateTime.MinValue;
        bool announced = false;

        while (!ct.IsCancellationRequested)
        {
            try { await System.Threading.Tasks.Task.Delay(1000, ct); }
            catch (System.Threading.Tasks.TaskCanceledException) { break; }
            if (ct.IsCancellationRequested) break;

            var f = DownloadProbe.FindActivePackage(started.AddSeconds(-3));
            if (f == null) continue;
            try { f.Refresh(); } catch { continue; }

            if (seen == null || seen.FullName != f.FullName)
            {
                // 换了个文件（新的下载开始）
                seen = f;
                lastSize = 0;
                lastAt = DateTime.Now;
                announced = false;
            }

            var now = DateTime.Now;
            var dt = (now - lastAt).TotalSeconds;
            if (dt >= 0.9)
            {
                var delta = f.Length - lastSize;
                if (delta > 0) speed = delta / dt;
                else if (delta < 0) speed = 0;
                lastSize = f.Length;
                lastAt = now;
            }

            if (!announced)
            {
                announced = true;
                lastReport = now;
                Log($"[下载] 检测到更新包 {f.Name}，正在下载…");
            }
            else if ((now - lastReport).TotalSeconds >= 5)
            {
                lastReport = now;
                Log($"[下载] 已获取 {DownloadProbe.Human(f.Length)}" +
                    (speed > 0 ? $" · {DownloadProbe.Human((long)speed)}/s（官方源，速度取决于网络）" : ""));
            }
        }

        if (seen != null)
        {
            try
            {
                seen.Refresh();
                Log($"[下载] 结束：{seen.Name} 共 {DownloadProbe.Human(seen.Length)}" +
                    (seen.Length > 1048576 ? "，随后交给系统安装" : ""));
            }
            catch { }
        }
    }

    private void OnInstallFromFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 WSL 官方安装包",
            Filter = "WSL 安装包|*.msi;*.msix;*.msixbundle;*.appx;*.appxbundle|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(HostWindow) != true) return;

        Log("准备安装：" + dlg.FileName);
        if (InstallService.StartElevatedInstall(dlg.FileName, out var msg))
        {
            Log(msg);
            Log("安装完成后请点击「环境检查」。");
        }
        else Log("启动安装失败：" + msg);
    }

    private void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        InstallService.OpenStorePage();
        Log("已尝试打开 Microsoft Store 的 WSL 页面。");
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        InstallService.OpenReleasePage();
        Log("已尝试打开 WSL 官方发布页（GitHub Releases）。");
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        string log = TxtLog.Text ?? "";
        if (log.Length == 0) { SetStatus("日志为空，没有可复制的内容", "WarnBrush"); return; }
        if (TrySetClipboard(log)) SetStatus("日志已复制到剪贴板", "OkBrush");
        else SetStatus("复制失败（剪贴板被别的程序占用，稍后再点一次）", "ErrBrush");
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => TxtLog.Clear();

    // ══════════════════════════ 容器 ══════════════════════════
    private async void OnRefreshContainersClick(object sender, RoutedEventArgs e) => await RefreshContainersAsync();

    private async System.Threading.Tasks.Task RefreshContainersAsync(bool silent = false)
    {
        if (!_ready) return;

        if (!WslcService.Available)
        {
            if (!silent) SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }

        // 与 WSLC 管理器 v0.9.1 起的行为一致：容器列表**恒含已停止容器**（等价 `wslc ps -a`）。
        // 早先这里挂过一个「显示已停止」勾选框，实际驱动的是 ps 的 all 开关；
        // 一旦取消勾选，已停止的容器连"启动"按钮都找不到 —— 所以直接去掉这个开关。
        var (items, raw) = await WslcService.ListContainersAsync(true);
        var (stats, _) = await WslcService.StatsOnceAsync();

        var statMap = new Dictionary<string, ContainerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stats)
            if (!string.IsNullOrWhiteSpace(s.Name)) statMap[s.Name] = s;

        foreach (var c in items)
        {
            if (statMap.TryGetValue(c.Name, out var st)) c.ApplyStats(st);
            // 状态已在 ListContainersAsync 里按 JSON 的 State 字段判定过了
        }

        Ui(() =>
        {
            var selectedNames = new HashSet<string>(
                _containers.Where(c => c.IsSelected).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var keep = _selContainer?.Name;

            _containers.Clear();
            foreach (var i in items)
            {
                i.IsSelected = selectedNames.Contains(i.Name);
                _containers.Add(i);
            }

            if (keep != null)
            {
                var again = _containers.FirstOrDefault(c => c.Name == keep);
                if (again != null) GridContainers.SelectedItem = again;
            }
            UpdateSelectedCount();
        });

        if (!raw.Success && raw.ErrorMessage.Length > 0)
            Log("列举容器失败：" + One(raw.ErrorMessage));

        // 刷新后顺手更新开机自启名单（三个时机之一：列表刷新 / 退出 / 系统关机）
        SaveAutoStartSnapshotNow();
        UpdateEnvCounts();
    }

    private async System.Threading.Tasks.Task RefreshRealtimeStatsAsync()
    {
        if (!_ready || _busy || NavContainer.IsChecked != true) return;
        if (!WslcService.Available || _containers.Count == 0) return;

        try { _statsCts?.Cancel(); } catch { }
        _statsCts = new CancellationTokenSource();

        var (stats, _) = await WslcService.StatsOnceAsync(_statsCts.Token);
        if (stats.Count == 0) return;

        var map = new Dictionary<string, ContainerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stats)
            if (!string.IsNullOrWhiteSpace(s.Name)) map[s.Name] = s;

        Ui(() =>
        {
            foreach (var c in _containers)
            {
                if (map.TryGetValue(c.Name, out var st)) c.ApplyStats(st);
                else { c.Cpu = "—"; c.MemPct = "—"; c.MemDetail = ""; }
            }
        });
    }

    private void OnContainerSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _selContainer = GridContainers.SelectedItem as ContainerInfo;

    // ── 多选 ──
    private void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = (sender as CheckBox)?.IsChecked == true;
        foreach (var c in _containers) c.IsSelected = on;
        UpdateSelectedCount();
    }

    private void OnRowCheckChanged(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateSelectedCount), DispatcherPriority.Background);

    private void UpdateSelectedCount()
    {
        int n = _containers.Count(c => c.IsSelected);
        TxtSelectedCount.Text = n == 0 ? "未选择容器" : $"已选择 {n} 个容器";
        TxtSelectedCount.SetResourceReference(TextBlock.ForegroundProperty,
            n > 0 ? "AccentBrush" : "MutedBrush");
    }

    private List<ContainerInfo> SelectedContainers() => _containers.Where(c => c.IsSelected).ToList();

    // ── 批量 ──
    private async void OnBatchStartClick(object sender, RoutedEventArgs e) => await BatchAsync("start", "启动");
    private async void OnBatchStopClick(object sender, RoutedEventArgs e) => await BatchAsync("stop", "停止");
    private async void OnBatchKillClick(object sender, RoutedEventArgs e) => await BatchAsync("kill", "强制终止");
    private async void OnBatchRestartClick(object sender, RoutedEventArgs e) => await BatchAsync("restart", "重启");

    private async System.Threading.Tasks.Task BatchAsync(string action, string verb)
    {
        var sel = SelectedContainers();
        if (sel.Count == 0) { SetStatus("请先勾选要操作的容器", "WarnBrush"); return; }

        bool stopping = action == "stop" || action == "kill";
        bool starting = IsStartingAction(action);

        SetBusy(true, $"正在批量{verb} {sel.Count} 个容器…");
        Ui(() => TxtContainerLog.Clear());

        if (stopping)
            foreach (var c0 in sel) c0.State = ContainerState.Stopping;

        int ok = 0, died = 0;
        foreach (var c in sel)
        {
            var r = action switch
            {
                "stop" => await WslcService.StopAsync(c.Name),
                "kill" => await WslcService.KillAsync(c.Name),
                _ => await WslcService.ContainerActionAsync(action, c.Name),
            };

            // 同单容器路径：start / restart 的 exit=0 不代表容器在跑，必须逐一复查
            var st = r.Success && starting ? await VerifyRunningAsync(c.Name) : null;

            if (r.Success && DiedRightAfterStart(st))
            {
                died++;
                Log(ExplainImmediateExit(c.Name, st), TxtContainerLog);
            }
            else
            {
                Log($"{(r.Success ? "✓" : "✗")} {verb} {c.Name}" +
                    (r.Success ? "" : $"　{One(r.All)}"), TxtContainerLog);
                if (r.Success) ok++;
            }
        }

        SetBusy(false);
        SetStatus(died > 0
                ? $"批量{verb}完成：真正运行 {ok}、起后即退 {died}（共 {sel.Count}）"
                : $"批量{verb}完成：成功 {ok} / {sel.Count}",
            ok == sel.Count ? "OkBrush" : "WarnBrush");
        await RefreshContainersAsync();
    }

    private async void OnBatchRemoveClick(object sender, RoutedEventArgs e)
    {
        var sel = SelectedContainers();
        if (sel.Count == 0) { SetStatus("请先勾选要删除的容器", "WarnBrush"); return; }

        if (!AppDialog.Confirm(HostWindow, "批量删除容器",
                $"确定要删除选中的 {sel.Count} 个容器吗？\n该操作不可撤销。",
                okText: "删除", danger: true))
            return;

        SetBusy(true, $"正在删除 {sel.Count} 个容器…");
        Ui(() => TxtContainerLog.Clear());

        int ok = 0;
        foreach (var c in sel)
        {
            var r = await WslcService.RemoveAsync(c.Name, force: true);
            Log($"{(r.Success ? "✓" : "✗")} 删除 {c.Name}" +
                (r.Success ? "" : $"　{One(r.All)}"), TxtContainerLog);
            if (r.Success) ok++;
        }

        SetBusy(false);
        SetStatus($"批量删除完成：成功 {ok} / {sel.Count}", ok == sel.Count ? "OkBrush" : "WarnBrush");
        await RefreshContainersAsync();
    }

    // ── 单容器行内操作 ──
    private async void OnRowStartClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "start", "启动");
    private async void OnRowStopClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "stop", "停止");
    private async void OnRowKillClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "kill", "强制终止");
    private async void OnRowRestartClick(object sender, RoutedEventArgs e) => await RowActionAsync(sender, "restart", "重启");

    private async System.Threading.Tasks.Task RowActionAsync(object sender, string action, string verb)
    {
        var c = CtxC(sender);
        if (c == null) return;

        if (action == "stop" || action == "kill")
        {
            // 立刻给出「停止中」反馈，避免操作期间状态看着没变化
            c.State = ContainerState.Stopping;
            SetStatus($"正在{verb} {c.Name}…", "WarnBrush");
        }

        SetBusy(true, $"{verb} {c.Name}…");
        Ui(() => TxtContainerLog.Clear());
        Log($"{verb}容器：{c.Name}");

        var r = action switch
        {
            "stop" => await WslcService.StopAsync(c.Name),
            "kill" => await WslcService.KillAsync(c.Name),
            _ => await WslcService.ContainerActionAsync(action, c.Name),
        };

        // start / restart 必须复查：exit=0 只代表「启动请求被接受」，
        // 不代表容器在跑（主进程一结束容器就停，命令照样返回 0）。
        var st = r.Success && IsStartingAction(action) ? await VerifyRunningAsync(c.Name) : null;
        SetBusy(false);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(c.Name, st), TxtContainerLog);
            SetStatus($"{verb} {c.Name}：容器随即退出（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else
        {
            Log(r.Success ? $"完成：{verb} {c.Name}" : $"失败：{One(r.All)}", TxtContainerLog);
            SetStatus(r.Success ? $"{verb} {c.Name} 成功" : $"{verb} {c.Name} 失败",
                r.Success ? "OkBrush" : "ErrBrush");
        }

        await RefreshContainersAsync();
    }

    // ── 「启动成功」到底成没成 ────────────────────────────────────
    private static bool IsStartingAction(string action) => action == "start" || action == "restart";

    /// <summary>指令报成功、但复查发现容器并没在跑 —— 这才是「提示成功却还是已停止」的真身。</summary>
    private static bool DiedRightAfterStart(Services.ContainerRuntimeState st)
        => st != null && st.Queried && !st.Running;

    private static string ExitCodeText(Services.ContainerRuntimeState st)
        => st?.ExitCode.HasValue == true ? st.ExitCode.Value.ToString() : "未知";

    /// <summary>
    /// 启动 / 重启后**复查真实运行态**。
    ///
    /// 先给 350ms 沉降（避开 `start` 刚返回、状态还没落到 inspect 的窗口），
    /// 不通过再补两次、每次间隔 250ms —— 总上限约 1.1 秒。
    /// 之所以敢这么短：容器进入 running 是在**主进程一启动**时就发生的，
    /// 与「应用是否已经就绪」无关，所以正常启动第一轮就能查到。
    /// </summary>
    private static async Task<Services.ContainerRuntimeState> VerifyRunningAsync(string name)
    {
        await Task.Delay(350);
        var st = await WslcService.InspectStateAsync(name);
        for (int i = 0; i < 2 && st.Queried && !st.Running; i++)
        {
            await Task.Delay(250);
            st = await WslcService.InspectStateAsync(name);
        }
        return st;
    }

    /// <summary>
    /// 创建容器后复查 —— `wslc run -d` 的 exit=0 同样只是「指令被接受」。
    /// 容器身份：用户起的名字优先，没起名就从输出里抓它打印的容器 ID。
    /// 前台运行（未勾 -d）与 `--rm` 不复查：前者会阻塞、后者容器已自动销毁，都问不到。
    /// </summary>
    private static async Task<Services.ContainerRuntimeState> VerifyCreatedAsync(ContainerSpec spec, RunResult r)
    {
        if (!spec.Detach || spec.AutoRemove) return null;
        var id = !string.IsNullOrWhiteSpace(spec.Name) ? spec.Name.Trim() : WslcService.ParseCreatedId(r.All);
        return string.IsNullOrWhiteSpace(id) ? null : await VerifyRunningAsync(id);
    }

    /// <summary>容器「起后即退」时给出针对性解释（命令 / 退出码 / 怎么改）。</summary>
    private static string ExplainImmediateExit(string name, Services.ContainerRuntimeState st)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"✗ {name}：启动了但随即停止（退出码 {ExitCodeText(st)}）—— ");
        sb.Append("容器在主进程结束时就会停止，这条指令本身是成功的。");

        if (!string.IsNullOrWhiteSpace(st.Cmd)) sb.Append($"当前命令：{st.Cmd}。");

        sb.Append(WslcService.LooksLikeInteractiveShell(st.Cmd)
            ? "交互式 shell 没有终端会立刻读到 EOF 退出；新建容器时勾选「分配终端（-i -t）」即可常驻。"
            : "要让容器常驻，把命令换成不会自己结束的（例如 sleep infinity）。");

        sb.Append("详情见该行「日志」。");
        return sb.ToString();
    }

    /// <summary>名称列缩窄后，点一下弹完整名称并顺手复制，方便辨认长名字。</summary>
    private void OnNameClick(object sender, MouseButtonEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null || string.IsNullOrWhiteSpace(c.Name)) return;

        // 弹窗文案要跟事实一致：复制没成功就别写「已复制」
        bool copied = TrySetClipboard(c.Name);

        AppDialog.Info(HostWindow, "容器完整名称",
            c.Name + (copied ? "\n\n（已复制到剪贴板）" : "\n\n（复制失败：剪贴板被占用，请手动选中复制）"));
    }

    private void OnOpenDetailClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender) ?? _selContainer;
        if (c == null) return;
        new ContainerDetailDialog(c) { Owner = HostWindow }.ShowDialog();
    }

    private void OnMetricClick(object sender, MouseButtonEventArgs e) => OnOpenDetailClick(sender, e);

    /// <summary>日志改为弹出卡片。</summary>
    private void OnRowLogsClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;
        new ContainerLogsDialog(c) { Owner = HostWindow }.ShowDialog();
    }

    /// <summary>
    /// 打开容器终端。
    /// 命令不写死 —— 容器里是 bash 还是 sh（或什么都没有）由镜像决定，
    /// 由 TerminalDialog 打开时实时探测后再让用户确认。
    /// </summary>
    private void OnRowShellClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;

        var dlg = new TerminalDialog(c) { Owner = HostWindow };
        if (dlg.ShowDialog() != true) return;

        var shell = dlg.SelectedShell;
        if (string.IsNullOrWhiteSpace(shell)) return;

        if (TryStartTerminal(c.Name, shell))
            SetStatus($"已在终端中打开 {c.Name}（{shell}）", "OkBrush");
        else
            SetStatus("打开终端失败：未找到可用的终端程序（wt.exe / powershell.exe）", "ErrBrush");
    }

    /// <summary>
    /// 起一个外部终端跑 `wslc exec -it &lt;容器&gt; &lt;shell&gt;`。
    /// 优先 Windows Terminal（wt.exe）；Win10 或未装 Store 版时它不存在，
    /// 回退到 PowerShell（-NoExit 让窗口留在那里，不是一闪而过）。
    /// </summary>
    private static bool TryStartTerminal(string container, string shell)
    {
        var exe = WslcService.ExePath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        var inner = $"\"{exe}\" exec -it {container} {shell}";

        foreach (var (file, args) in new[]
                 {
                     ("wt.exe", inner),
                     ("powershell.exe", $"-NoExit -Command \"& '{exe}' exec -it {container} {shell}\""),
                 })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = true
                });
                return true;
            }
            catch { /* 换下一个 */ }
        }
        return false;
    }

    private async void OnRowRemoveClick(object sender, RoutedEventArgs e)
    {
        var c = CtxC(sender);
        if (c == null) return;

        if (!AppDialog.Confirm(HostWindow, "删除容器",
                $"确定要删除容器「{c.Name}」吗？\n该操作不可撤销。",
                okText: "删除", danger: true))
            return;

        SetBusy(true, "正在删除…");
        var r = await WslcService.RemoveAsync(c.Name, force: true);
        SetBusy(false);
        SetStatus(r.Success ? $"已删除 {c.Name}" : "删除失败：" + One(r.All),
            r.Success ? "OkBrush" : "ErrBrush");
        await RefreshContainersAsync();
    }

    private void OnClearContainerLogClick(object sender, RoutedEventArgs e) => TxtContainerLog.Clear();

    /// <summary>
    /// 表格「右键复制」：按点到的**列**决定复制什么（容器名称 / 镜像 / 端口映射 / 网络名称）。
    ///
    /// 处理器挂在**单元格**上（XAML 里 `CopyCell` 样式上的 EventSetter），所以 `sender`
    /// 就是 DataGridCell —— 不用沿视觉树往上找，也就不依赖 `e.Source` / `e.OriginalSource`
    /// 这类只在真实输入下才可靠的字段。
    ///
    /// ⚠ 别改挂到祖先 DataGrid 上：`MouseRightButtonUp` 是 **Direct 路由事件**（不是冒泡），
    ///   它由框架在处理底层 `Mouse.MouseUp`（这个才冒泡）时，在路由上的**每个 UIElement
    ///   上逐个重发**。挂在单元格上才与真实点击的路径一致（单元格正是路由上的一环）。
    ///
    /// 取的是**全量值**（Name / Image / Ports），不是列表里被 TextTrimming 截断的显示值 ——
    /// 复制一串带省略号的文本没有意义。
    /// </summary>
    private void OnContainerCellRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell) return;
        var header = cell.Column?.Header as string;

        if (cell.DataContext is ContainerInfo c)
        {
            switch (header)
            {
                case "容器名称": CopyToClipboard(c.Name, "容器名称"); break;
                case "镜像": CopyToClipboard(c.Image, "镜像"); break;
                case "端口映射": CopyToClipboard(c.Ports, "端口映射"); break;
                // 其余列（状态 / CPU / 内存 / 操作）没挂这个样式，右键保持无操作
            }
            return;
        }

        if (cell.DataContext is NetworkInfo n && header == "名称")
            CopyToClipboard(n.Name, "网络名称");
    }

    /// <summary>
    /// 写剪贴板，返回是否**真的**写进去了。
    ///
    /// ⚠ 关键点（v2.0 修）：<c>Clipboard.SetText</c> / <c>SetDataObject</c> 抛异常**不等于没写进去** ——
    /// OleSetClipboard 已成功、而后续阶段被别的进程抢占时同样会抛，剪贴板里其实已经有内容了。
    /// 旧实现只看异常就报「复制失败」，于是出现「提示失败、实际能粘出来」这种自相矛盾的提示（bill 实测报的就是这条）。
    /// 判据只有一个：**能不能粘出来** —— 所以异常之后必须回读一次剪贴板，读回一致就算成功。
    /// </summary>
    private static bool TrySetClipboard(string text)
    {
        // 实测数据（--dump-layout 的「复制自检」打出耗时）：走 copy:true 时 SetDataObject 会**阻塞
        // 十几秒再抛异常**（OleFlushClipboard 被别的进程卡住），随后的回读每次又要吃满 OLE 的 1 秒重试
        // —— 一次「复制」25 秒，界面直接假死，最后还报失败。而剪贴板里其实早就有内容了。
        //
        // 所以顺序反过来：先走不 flush 的写法（只把数据对象挂上去，瞬间返回，程序活着期间粘贴照常可用），
        // 回读一致即成功；只有它读不到时才退到 copy:true（用 25 秒换「退出后仍可粘贴」）。
        if (WriteAndVerify(text, flush: false)) return true;

        // 兜底同样要限时：OleFlushClipboard 能卡 25 秒，绝不能让它拽住 UI 线程。
        // 超时后按「已交给剪贴板」处理 —— 数据对象已经递出去了，此时报「失败」正是 bill 遇到的那条假警报。
        var t = new System.Threading.Thread(() => { try { WriteAndVerify(text, flush: true); } catch { } });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(1000)) return true;          // 还在 flush：别等，也别谎报失败
        return ClipboardHas(text);               // 已写完：以回读为准
    }

    /// <summary>写一次 + 回读校验（抛异常不判失败，一律以回读为准）。</summary>
    private static bool WriteAndVerify(string text, bool flush)
    {
        try { Clipboard.SetDataObject(text, flush); } catch { /* 见 TrySetClipboard 的注释 */ }
        for (int i = 0; i < 2; i++)
        {
            if (ClipboardHas(text)) return true;
            System.Threading.Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>回读剪贴板并比对（CRLF 归一化，避免多行文本因换行写法不同误判失败）。</summary>
    private static bool ClipboardHas(string text)
    {
        try
        {
            if (!Clipboard.ContainsText()) return false;
            string got = Clipboard.GetText();
            return string.Equals(got, text, StringComparison.Ordinal)
                   || string.Equals(got.Replace("\r\n", "\n"), text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写剪贴板 + 状态栏回执。
    ///
    /// 三件事不能省：① 空值不写剪贴板（否则会把「—」「（未映射端口）」这类占位符复制出去）；
    /// ② 剪贴板被别的进程占住时会抛异常，重试几次再放弃；
    /// ③ 失败必须说出来，不能 <c>catch { }</c> 吞掉 —— 用户会以为复制成功了。
    /// </summary>
    private void CopyToClipboard(string value, string label)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            SetStatus($"这一行没有{label}可复制", "WarnBrush");
            return;
        }

        if (TrySetClipboard(text))
        {
            SetStatus($"已复制{label}：" + ClipForStatus(text), "OkBrush");
        }
        else
        {
            // 走到这里才能断言失败：重试 4 次、且回读剪贴板仍不是这段文本
            SetStatus($"复制{label}失败（剪贴板被别的程序占用，稍后再点一次）", "ErrBrush");
        }
    }

    /// <summary>状态栏回执里把超长值截成单行（完整值已经在剪贴板里了）。</summary>
    private static string ClipForStatus(string s)
    {
        s = One(s);
        return s.Length <= 48 ? s : s.Substring(0, 48) + "…";
    }

    /// <summary>
    /// 打开「新建容器」前预取网络名。用户可能没进过「容器网络管理」页 ——
    /// 那时 _networks 是空的，下拉里只剩「默认」，看着像不支持自定义网络。
    /// </summary>
    private async System.Threading.Tasks.Task EnsureNetworksAsync()
    {
        if (_networks.Count > 0 || !WslcService.Available) return;
        await RefreshNetworksAsync(silent: true);
    }

    private async void OnCreateContainerClick(object sender, RoutedEventArgs e)
    {
        await EnsureNetworksAsync();
        var dlg = new CreateContainerDialog(_images, _networks.Select(n => n.Name)) { Owner = HostWindow };
        if (dlg.ShowDialog() != true) return;

        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        var spec = dlg.Result;
        Ui(() => TxtContainerLog.Clear());
        Log("新建容器：" + spec.PreviewCommandLine(), TxtContainerLog);

        SetBusy(true, "正在创建容器…");
        var r = await WslcService.RunAsync(spec, s => Log(s, TxtContainerLog));
        var st = r.Success ? await VerifyCreatedAsync(spec, r) : null;
        SetBusy(false);

        Log(r.Success ? "容器创建指令已执行（退出码 0）。" : $"创建失败（退出码 {r.ExitCode}）。", TxtContainerLog);
        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtContainerLog);

        // ⚠ 导入的备份镜像（image import）没有默认启动命令，不带命令 run 会报 no command specified
        if (!r.Success && (r.All ?? "").Contains("no command specified", StringComparison.OrdinalIgnoreCase))
        {
            Log("  诊断：该镜像没有默认启动命令（常见于「备份与清理」导入的镜像 —— 导出只含文件系统）。", TxtContainerLog);
            Log("  解决：重新新建容器，并在「启动命令」里填一个常驻命令，例如 sleep infinity 或 sh。", TxtContainerLog);
        }

        // 网络不可用导致的启动失败（原始报错里没有「网段」两个字，必须补一句人话）
        if (!r.Success) ExplainNetworkStartFailure(spec.Network, r.All, TxtContainerLog);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(!string.IsNullOrWhiteSpace(spec.Name) ? spec.Name : "新容器", st), TxtContainerLog);
            SetStatus($"容器已创建，但随即停止（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else SetStatus(r.Success ? "容器已创建" : "容器创建失败", r.Success ? "OkBrush" : "ErrBrush");

        await RefreshContainersAsync();
    }

    // ══════════════════════════ 网络 ══════════════════════════
    private async void OnRefreshNetworksClick(object sender, RoutedEventArgs e) => await RefreshNetworksAsync();

    private async System.Threading.Tasks.Task RefreshNetworksAsync(bool silent = false)
    {
        if (!_ready) return;

        if (!WslcService.Available)
        {
            if (!silent) SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }

        var (items, raw) = await WslcService.ListNetworksAsync();

        // 标记「网段不可用」的网络。wslc network create 不校验网段，所以列表里出现的网络
        // 未必能用 —— 必须在这里认出来，否则用户只有等到启容器报 runc 错误才知道。
        foreach (var n in items)
            n.SubnetWarning = NetworkValidator.ExplainSubnet(n.Subnet);

        var badCount = items.Count(n => n.HasSubnetWarning);

        Ui(() =>
        {
            _networks.Clear();
            foreach (var n in items) _networks.Add(n);
            TxtNetSummary.Text = items.Count == 0
                ? ""
                : $"{items.Count} 个网络" + (badCount > 0 ? $"（{badCount} 个网段不可用）" : "");
        });

        if (badCount > 0)
            Log($"注意：{badCount} 个网络的网段不可用（列表里有 ⚠）—— 这些网络能建容器，但容器起不来。", TxtNetworkLog);

        if (!raw.Success && raw.ErrorMessage.Length > 0)
        {
            if (!silent) SetStatus("列举网络失败：" + One(raw.ErrorMessage), "ErrBrush");
            Log("列举网络失败：" + One(raw.ErrorMessage), TxtNetworkLog);
        }
    }

    private void OnFillNetworkSampleClick(object sender, RoutedEventArgs e)
    {
        TxtNetName.Text = "app-net";
        TxtNetSubnet.Text = "172.30.0.0/24";
        TxtNetGateway.Text = "172.30.0.1";
        SetStatus("已填入示例，确认无误后点「创建网络」", "InfoBrush");
    }

    private async void OnCreateNetworkClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }

        var name = (TxtNetName.Text ?? "").Trim();
        var subnet = (TxtNetSubnet.Text ?? "").Trim();
        var gateway = (TxtNetGateway.Text ?? "").Trim();

        if (!NetworkValidator.Validate(name, subnet, gateway, out var why))
        {
            SetStatus(why, "WarnBrush");
            Log("创建网络被拦下：" + why, TxtNetworkLog);
            return;
        }

        // 本地先查重：wslc 对同名网络会报错，但错误信息不如这里一句人话清楚
        if (_networks.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            var msg = $"已存在同名网络「{name}」，换个名字";
            SetStatus(msg, "WarnBrush");
            Log("创建网络被拦下：" + msg, TxtNetworkLog);
            return;
        }

        var cmd = "wslc network create -d bridge"
                  + (subnet.Length > 0 ? $" --subnet {subnet}" : "")
                  + (gateway.Length > 0 ? $" --gateway {gateway}" : "")
                  + " " + name;

        Ui(() => TxtNetworkLog.Clear());
        Log("创建网络：" + cmd, TxtNetworkLog);

        SetBusy(true, "正在创建网络…");
        var r = await WslcService.NetworkCreateAsync(name, "bridge", subnet, gateway);
        SetBusy(false);

        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtNetworkLog);

        // 判据看退出码：wslc network create 成功时只在 stdout 打印网络名（实测 exit=0、
        // 输出就是那一个名字），没有「成功」字样可匹配。
        if (r.Success)
        {
            Log($"已创建网络 {name}。", TxtNetworkLog);
            SetStatus($"网络 {name} 已创建", "OkBrush");
            Ui(() => { TxtNetName.Clear(); TxtNetSubnet.Clear(); TxtNetGateway.Clear(); });
        }
        else
        {
            Log($"创建失败（退出码 {r.ExitCode}）。", TxtNetworkLog);
            SetStatus("网络创建失败：" + One(r.All), "ErrBrush");
        }

        await RefreshNetworksAsync();
    }

    private async void OnNetworkRowRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NetworkInfo net) return;

        if (net.IsBuiltIn)
        {
            SetStatus($"「{net.Name}」是 wslc 自带网络，不能删除", "WarnBrush");
            return;
        }

        if (net.ContainerCount > 0)
        {
            AppDialog.Info(HostWindow, "网络还在使用中",
                $"网络「{net.Name}」上还连着 {net.ContainerCount} 个容器：\n\n" +
                string.Join("、", net.Containers.Select(c => c.Name)) +
                "\n\n先断开或删除这些容器，再删网络 —— 直接删 wslc 也会拒绝。");
            return;
        }

        if (!AppDialog.Confirm(HostWindow, "删除网络",
                $"确定删除网络「{net.Name}」吗？\n\n" +
                $"网段：{net.SubnetText}　网关：{net.GatewayText}\n" +
                "删除后容器要重新创建才能接回这个网络。",
                okText: "删除", danger: true))
            return;

        SetBusy(true, "正在删除网络…");
        var r = await WslcService.NetworkRemoveAsync(net.Name);
        SetBusy(false);

        Log($"删除网络 {net.Name}：" + (r.Success ? "完成" : $"失败（退出码 {r.ExitCode}）"), TxtNetworkLog);
        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtNetworkLog);

        SetStatus(r.Success ? $"网络 {net.Name} 已删除" : "删除失败：" + One(r.All),
            r.Success ? "OkBrush" : "ErrBrush");

        await RefreshNetworksAsync();
    }

    private void OnClearNetworkLogClick(object sender, RoutedEventArgs e) => TxtNetworkLog.Clear();

    /// <summary>
    /// 容器启动失败若是「网关/网段不可用」，补一段人话诊断。返回是否命中。
    ///
    /// 为什么需要：`wslc network create` 对网段**零校验** —— `127.11.0.0/24` 这种
    /// 回环段的网络建得出来、列表里看着也正常，直到启容器才报：
    /// <code>
    /// failed to set gateway while updating gateway: route for the gateway 127.11.0.1
    /// could not be found: invalid argument
    /// </code>
    /// 原始报错里**没有出现「网段」两个字**，用户只能看到一长串 runc/shim 术语，
    /// 根本猜不到是自己填的 CIDR 有问题。
    /// </summary>
    private bool ExplainNetworkStartFailure(string network, string output, TextBox target)
    {
        var text = output ?? "";
        bool looksLikeGateway = text.Contains("route for the gateway", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("failed to set gateway", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeGateway) return false;

        var net = string.IsNullOrWhiteSpace(network) ? "（默认网络）" : network;
        Log("  诊断：容器的网关设不上 —— 这个网络的网段不可用。", target);
        Log($"  原因：网络「{net}」的网段在容器内没有对应路由，网关地址不可达" +
            "（回环 127.0.0.0/8、0.0.0.0/8、169.254.0.0/16、224.0.0.0/4、240.0.0.0/4 都会这样）。", target);
        Log("  解决：到「容器网络管理」页删掉这个网络，用私网段重建（例如 172.30.0.0/24 或 192.168.99.0/24），再新建容器。", target);
        Log("  注：wslc network create 不校验网段，所以这种网络建得出来、却永远起不了容器。", target);
        return true;
    }

    // ══════════════════════════ 镜像 ══════════════════════════
    private async void OnRefreshImagesClick(object sender, RoutedEventArgs e) => await RefreshImagesAsync();

    private async System.Threading.Tasks.Task RefreshImagesAsync(bool silent = false)
    {
        if (!_ready) return;
        if (!WslcService.Available)
        {
            if (!silent) SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        // 镜像清单与容器清单一起取：镜像列表要标出「被哪些容器占用」。
        // 必要性：wslc 删除被容器引用的镜像时**不报错、exit=0**，但只摘标签、
        // 镜像层不删、空间不释放（实测见 WslcService.InterpretImageRemove 注释）。
        var imgTask = WslcService.ListImagesAsync();
        var ctrTask = WslcService.ListContainersAsync(all: true);
        await System.Threading.Tasks.Task.WhenAll(imgTask, ctrTask);

        var (items, raw) = imgTask.Result;
        WslcService.AttachImageUsage(items, ctrTask.Result.Items);

        Ui(() =>
        {
            _images.Clear();
            foreach (var i in items) _images.Add(i);
        });
        _imagesLoaded = true;
        UpdateEnvCounts();

        if (!raw.Success && raw.ErrorMessage.Length > 0)
            Log("列举镜像失败：" + One(raw.ErrorMessage));
    }

    // ── 镜像检索（本地索引）──
    private void OnRepoQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRepoSearchClick(sender, e);
    }

    /// <summary>
    /// 检索 = 纯本地索引，同步瞬时返回。
    /// 不再"先干等远程 3 秒再回落"：wslc 没有官方 search 子命令，Docker Hub 的检索接口
    /// 在国内也不可达，那 3 秒等不到任何东西，只会让用户以为点了没反应。
    /// </summary>
    private void OnRepoSearchClick(object sender, RoutedEventArgs e)
    {
        var q = (TxtRepoQuery.Text ?? "").Trim();
        if (q.Length == 0) { SetStatus("请输入要检索的镜像关键字", "WarnBrush"); return; }

        ListSearchResults.ItemsSource = null;
        _searchResults.Clear();

        var local = ImageSearchService.SearchLocal(q);
        foreach (var it in local.Items) _searchResults.Add(it);
        ListSearchResults.ItemsSource = _searchResults;

        TxtSearchStatus.Text = local.Message;
        SetStatus(local.Items.Count > 0
                ? $"已列出 {local.Items.Count} 条（{ImageSearchService.IndexSourceText} v{ImageSearchService.IndexVersionText}）"
                : "未找到匹配项",
            local.Items.Count > 0 ? "OkBrush" : "WarnBrush");
    }

    private void OnRepoWebSearchClick(object sender, RoutedEventArgs e)
    {
        var q = (TxtRepoQuery.Text ?? "").Trim();
        ImageSearchService.OpenWebSearch(q, AppSettings.WebSearchUrlTemplate);
        SetStatus("已在浏览器中打开镜像搜索页", "InfoBrush");
    }

    private void OnSearchResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ImageSearchItem it)
        {
            TxtPullImage.Text = it.PullName;
            TxtPullImage.CaretIndex = it.PullName.Length;
            SetStatus("已填入镜像名：" + it.PullName, "InfoBrush");
        }
    }

    private void OnImageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selImage = GridImages.SelectedItem as ImageInfo;
        // 被容器占用的镜像没有删除资格（按钮禁用 + 提示说明原因）
        if (BtnImgRemove == null) return;
        var img = _selImage;
        BtnImgRemove.IsEnabled = img != null && !img.InUse;
        BtnImgRemove.ToolTip = img == null ? "先在列表中选择一个镜像"
            : img.InUse ? $"被 {img.Usage.Total} 个容器使用，不能删除（先删容器）"
            : "删除该镜像（虚拟磁盘内删除，列表同步移除）";
    }

    private void OnImageRowRemoveClick(object sender, RoutedEventArgs e)
    {
        var img = CtxI(sender) ?? _selImage;
        if (img == null) return;
        ConfirmRemoveImage(img);
    }

    /// <summary>输出区里的「删除选中镜像」按钮（走当前选中项）。</summary>
    private void OnImageRemoveClick(object sender, RoutedEventArgs e)
    {
        var img = _selImage;
        if (img == null) { SetStatus("请先在列表中选择一个镜像", "WarnBrush"); return; }
        if (img.InUse)
        {
            // 被容器占用的镜像不提供删除（按钮本身也会禁用，这里是键盘路径的兜底）
            var u = img.Usage;
            SetStatus($"镜像 {img.Reference} 正被 {u.Total} 个容器使用，不能删除", "WarnBrush");
            return;
        }
        ConfirmRemoveImage(img);
    }

    private void ConfirmRemoveImage(ImageInfo img)
    {
        string body;
        if (img.InUse)
        {
            var u = img.Usage;
            var who = new List<string>();
            if (u.RunningCount > 0) who.Add("运行中：" + string.Join("、", u.RunningNames));
            if (u.StoppedCount > 0) who.Add("已停止：" + string.Join("、", u.StoppedNames));

            body = $"镜像「{img.Reference}」正被 {u.Total} 个容器引用。\n\n" +
                   string.Join("\n", who) + "\n\n" +
                   "注意：这种情况下 wslc 只会「取消标记」——\n" +
                   "  · 不报错（退出码 0），看上去像删成功了\n" +
                   "  · 但镜像层不会删除，磁盘空间不会释放\n" +
                   "  · 镜像只是从列表里消失，这些容器仍能照常运行\n\n" +
                   "要真正回收空间，请先删除上面列出的容器。\n\n仍要继续吗？";
        }
        else
        {
            body = $"确定要删除镜像「{img.Reference}」吗？\n\n" +
                   "该镜像没有被任何容器引用，删除将在虚拟磁盘内真正删除镜像层，\n" +
                   "删除成功后列表会同步移除。\n\n" +
                   "⚠ 注意：宿主的 storage.vhdx 文件大小不会自动缩小（虚拟磁盘只增不减）。\n" +
                   "如需回收宿主空间，请到「备份与清理」页重建会话。";
        }

        if (!AppDialog.Confirm(HostWindow, "删除镜像", body, okText: "删除", danger: true)) return;
        _ = RemoveImageAsync(img);
    }

    private async void OnTestMirrorsClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available) { SetStatus("wslc 尚未安装", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        // 待测清单 = 当前输入框里的地址（有就排在前面）∪ 预设清单。
        // 这样即使没有配置任何预设，也能直接测手上正在填的那个地址。
        var typed = WslcService.NormalizeMirrorPrefix(TxtMirror.Text);
        var targets = new List<string>();
        if (typed.Length > 0) targets.Add(typed);
        targets.AddRange(MirrorPresets().Select(WslcService.NormalizeMirrorPrefix));
        targets = targets.Where(x => x.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        if (targets.Count == 0)
        {
            TxtMirrorTest.Text = "没有可检测的地址：请先在上面填写「加速前缀」。";
            SetStatus("请先填写一个加速前缀再检测", "WarnBrush");
            return;
        }

        SetBusy(true, "正在逐个实测加速地址…");
        var lines = new List<string>
        {
            "说明：检测方式是**真的拉取一次 alpine:latest**，可用即意味着该站能转发镜像；",
            "      测完 alpine:latest 会留在本地镜像列表里，不需要时可在镜像页删掉。"
        };
        int okCount = 0;
        string firstOk = null;

        foreach (var m in targets)
        {
            TxtMirrorTest.Text = string.Join("\r\n", lines) +
                                 $"\r\n… 正在检测 {m}";
            bool ok = await WslcService.TestMirrorAsync(m);
            lines.Add($"{(ok ? "✓ 可用  " : "✗ 不可用")}  {m}");
            TxtMirrorTest.Text = string.Join("\r\n", lines);
            if (ok) { okCount++; firstOk ??= m; }
        }

        lines.Add("");
        lines.Add(okCount > 0
            ? $"共 {okCount} / {targets.Count} 个可用。推荐：{firstOk}"
            : "全部不可用，可直接留空使用官方源（可能较慢）。");
        TxtMirrorTest.Text = string.Join("\r\n", lines);

        SetBusy(false);
        SetStatus($"加速地址检测完成：{okCount} / {targets.Count} 可用",
            okCount > 0 ? "OkBrush" : "WarnBrush");
    }

    /// <summary>
    /// 压缩 wslc 会话的虚拟磁盘（storage.vhdx）。
    /// 背景：删除镜像后 vhdx 只增不减（实测删 163MB 的 nginx 后磁盘 +0.0 MB），
    /// 需要手动 compact 才能真正回收。流程：警告 → 关 WSL → 提权 diskpart → 报告前后大小。
    /// </summary>
    private async void OnCompactDiskClick(object sender, RoutedEventArgs e)
    {
        var vhdx = await System.Threading.Tasks.Task.Run(
            () => WslManager.Wslc.Helpers.DiskCompactor.FindStorageVhdx());
        if (vhdx == null) { SetStatus("未找到 wslc 会话的虚拟磁盘", "WarnBrush"); return; }

        var before = WslManager.Wslc.Helpers.DiskCompactor.GetVhdxBytes(vhdx);
        var msg = "将对 wslc 会话的虚拟磁盘执行 diskpart compact：\n\n" + vhdx +
                  $"\n\n当前大小：{before / 1048576.0:N1} MB" +
                  "\n\n⚠ 流程：wsl --shutdown（所有 WSL / wslc 容器会停止）→ UAC 提权 → diskpart compact。" +
                  "\n\n⚠ 实测提醒（2026-10-10）：ext4 删除文件不清零，compact 只回收全零块，" +
                  "**对 wslc 会话的磁盘通常回收 0 字节**（fstrim 的 TRIM 也不传导）。" +
                  "此功能保留给确有需要的场景（如磁盘内做过全盘写零）。\n\n确定继续吗？";

        if (!AppDialog.Confirm(HostWindow, "压缩虚拟磁盘", msg, okText: "开始压缩", danger: false)) return;

        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }
        SetBusy(true, "正在压缩虚拟磁盘…");
        BtnCompact.IsEnabled = false;

        // 1) 关闭 WSL（含 wslc 会话），否则 vhdx 被占用无法压缩
        SetStatus("正在关闭 WSL…", "WarnBrush");
        var (ok, outText) = await System.Threading.Tasks.Task.Run(
            () => WslManager.Wslc.Helpers.DiskCompactor.ShutdownWsl());
        if (!ok)
        {
            SetBusy(false); BtnCompact.IsEnabled = true;
            SetStatus("关闭 WSL 失败：" + outText, "ErrBrush");
            return;
        }
        await System.Threading.Tasks.Task.Delay(1500);   // 留时间让句柄完全释放

        // 2) 提权 diskpart（UAC 由用户确认）
        SetStatus("等待 UAC 授权（如未弹出请检查任务栏）…", "WarnBrush");
        var r = await System.Threading.Tasks.Task.Run(
            () => WslManager.Wslc.Helpers.DiskCompactor.Compact(vhdx));

        // 3) 报告
        var after = WslManager.Wslc.Helpers.DiskCompactor.GetVhdxBytes(vhdx);
        var saved = (before - after) / 1048576.0;
        Log($"── 压缩虚拟磁盘 ──", TxtImageLog);
        Log($"磁盘：{vhdx}", TxtImageLog);
        Log($"压缩前：{before / 1048576.0:N1} MB　压缩后：{after / 1048576.0:N1} MB　" +
            $"释放：{(saved > 0 ? saved.ToString("N1") + " MB" : "0 MB（无碎片可回收）")}", TxtImageLog);
        if (!string.IsNullOrWhiteSpace(r.Output))
            foreach (var l in r.Output.Split('\n'))
                if (l.Trim().Length > 0 && !l.Contains("Microsoft") && !l.Contains("版权"))
                    Log("  " + l.TrimEnd('\r'), TxtImageLog);

        SetBusy(false);
        BtnCompact.IsEnabled = true;

        if (r.Ok && saved > 0.05)
            SetStatus($"压缩完成，释放 {saved:N1} MB", "OkBrush");
        else if (r.Ok)
            SetStatus("压缩完成，但本磁盘没有可回收的碎片空间", "InfoBrush");
        else
            SetStatus("压缩失败：" + r.Error, "ErrBrush");
    }

    // ── 备份与清理 ─────────────────────────────────────────────

    /// <summary>进入备份页时刷新容器下拉（按名称排序）。</summary>
    private void RefreshBackupContainers()
    {
        var old = CmbBackupContainer.SelectedItem as string;
        CmbBackupContainer.Items.Clear();
        foreach (var n in _containers.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n))
            CmbBackupContainer.Items.Add(n);
        if (old != null && CmbBackupContainer.Items.Contains(old))
            CmbBackupContainer.SelectedItem = old;
        else if (CmbBackupContainer.Items.Count > 0)
            CmbBackupContainer.SelectedIndex = 0;
    }

    /// <summary>刷新「当前虚拟磁盘占用」显示（重建会话能腾出多少空间，一目了然）。</summary>
    private void RefreshSessionDiskInfo()
    {
        if (TxtSessionDisk == null) return;
        // ⚠ FindStorageVhdx → GetStorageDir 首次会同步跑 wslc info（最长 20s 超时），
        // 绝不能在 UI 线程直接调 —— 会卡死界面（2026-10-10 二次踩同一坑，截图/测试全被拖死）。
        TxtSessionDisk.Text = "正在读取虚拟磁盘信息…";
        // vhdx 是原始路径，单独存一份供「复制」按钮用（TextBlock 里是「路径 + 占用」的复合文本）
        _sessionDiskPath = "";
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var vhdx = WslManager.Wslc.Helpers.DiskCompactor.FindStorageVhdx();
                _sessionDiskPath = vhdx ?? "";
                if (vhdx == null) return "当前虚拟磁盘：未找到（会话可能尚未初始化）";
                var bytes = WslManager.Wslc.Helpers.DiskCompactor.GetVhdxBytes(vhdx);
                if (bytes < 0) return $"当前虚拟磁盘：{vhdx}";

                // 实测（2026-10-10）：全新空会话自身约占 588 MB —— ext4 日志区/块分配表
                // 等格式化结构 + wslc 引擎目录（挂载实测内部数据仅约 350KB）。
                // 这部分是"底座"，重建/压缩都消不掉，能释放的是超出它的部分。
                var mb = bytes / 1048576.0;
                var reclaim = mb - DiskCompactor.EmptySessionBaseMB;
                var tip = reclaim > 16
                    ? $"重建后约降至 {DiskCompactor.EmptySessionBaseMB} MB，可释放约 {reclaim:N0} MB"
                    : "已接近空会话的运行时底座，重建收益有限";
                return $"当前虚拟磁盘：{vhdx}\n占用 {mb:N1} MB —— {tip}";
            }
            catch (Exception ex) { return "读取虚拟磁盘信息失败：" + ex.Message; }
        })
        .ContinueWith(t =>
        {
            if (TxtSessionDisk != null) TxtSessionDisk.Text = t.Result;
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>备份：把选中容器导出为 tar 存档（wslc container export -o）。</summary>
    private async void OnBackupExportClick(object sender, RoutedEventArgs e)
    {
        var name = CmbBackupContainer.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(name)) { SetStatus("请先选择要备份的容器", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        using var dlg = new System.Windows.Forms.SaveFileDialog
        {
            Title = "选择备份保存位置",
            FileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.tar",
            Filter = "tar 存档 (*.tar)|*.tar|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(GetIWin32WindowOwner()) != System.Windows.Forms.DialogResult.OK) return;

        // ⚠ 实测：wslc 对运行中的容器导出会报 WSLC_E_CONTAINER_IS_RUNNING，
        // 必须先停止。这里自动处理：停止 → 导出 → 恢复原来的运行状态。
        var target = _containers.FirstOrDefault(x => x.Name == name);
        var wasRunning = target != null && target.State == ContainerState.Running;
        if (wasRunning)
        {
            if (!AppDialog.Confirm(HostWindow, "备份容器",
                $"容器「{name}」正在运行。\n\n导出要求容器处于停止状态：工具会先停止它，" +
                "导出完成后自动重新启动。\n\n继续吗？", okText: "停止并导出")) return;
        }

        SetBusy(true, wasRunning ? $"正在停止容器 {name}…" : $"正在导出容器 {name}…（容器越大越久）");

        if (wasRunning)
        {
            var st = await WslcService.StopAsync(name);
            if (!st.Success)
            {
                SetBusy(false);
                SetStatus("停止容器失败：" + One(st.StdErr), "ErrBrush");
                return;
            }
            Log($"已停止容器 {name}（导出前置条件）", TxtImageLog);
        }

        Log($"执行：wslc container export -o \"{dlg.FileName}\" {name}", TxtImageLog);

        var r = await WslcService.ExportContainerWithMetaAsync(name, dlg.FileName);

        // 导出完把原来运行中的容器拉起来
        if (wasRunning && r.Success)
        {
            SetStatus("导出完成，正在恢复容器运行…", "InfoBrush");
            var sr = await WslcService.StartAsync(name);
            Log(sr.Success ? $"容器 {name} 已重新启动" : $"容器 {name} 重新启动失败（可手动启动）",
                TxtImageLog);
        }
        SetBusy(false);

        if (r.Success)
        {
            var size = File.Exists(dlg.FileName) ? new FileInfo(dlg.FileName).Length : 0;
            var hasMeta = File.Exists(dlg.FileName + ".json");
            Log($"备份完成：{dlg.FileName}（{size / 1048576.0:N1} MB）" +
                (hasMeta ? " + 配置元数据（恢复时将按原配置自动重建容器）" : "（⚠ 未取得配置元数据，恢复时只能恢复为镜像）"),
                TxtImageLog);
            SetStatus($"已备份容器 {name}", "OkBrush");
        }
        else
        {
            Log("备份失败：" + One(r.StdErr), TxtImageLog);
            // 导出前把容器停了；失败时它**还停在已停止**，必须说清楚怎么恢复 ——
            // 否则用户以为"失败了应该没影响"，回去发现容器不跑了。
            if (wasRunning)
            {
                Log($"  ⚠ 容器 {name} 目前处于**已停止**状态（导出前被停掉了，失败后没有自动拉起）。", TxtImageLog);
                Log("  恢复方式：在容器列表点它的「启动」，或右键复制名称后执行 wslc start。", TxtImageLog);
                SetStatus($"备份失败，容器 {name} 仍处于已停止（请手动启动）", "ErrBrush");
            }
            else SetStatus("备份失败", "ErrBrush");
        }
    }

    private void OnRestoreBrowseClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择备份 tar 文件",
            Filter = "tar 存档 (*.tar)|*.tar|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(GetIWin32WindowOwner()) == System.Windows.Forms.DialogResult.OK)
            TxtRestoreFile.Text = dlg.FileName;
    }

    /// <summary>恢复：把备份 tar 导入为镜像（wslc image import），之后可用它新建容器。</summary>
    private async void OnRestoreImportClick(object sender, RoutedEventArgs e)
    {
        var file = TxtRestoreFile.Text?.Trim();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        { SetStatus("请先选择有效的备份 tar 文件", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        // 镜像名：取文件名小写、去掉扩展名与非法字符
        var baseName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in baseName) sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '/' ? ch : '-');
        var imageName = sb.ToString().Trim('-');
        if (imageName.Length == 0) imageName = "restored";
        imageName += ":restored";

        SetBusy(true, $"正在导入为镜像 {imageName}…");
        Log($"执行：wslc image import \"{file}\" {imageName}", TxtImageLog);

        var r = await WslcService.ImportImageAsync(file, imageName);
        if (!r.Success)
        {
            SetBusy(false);
            Log("导入失败：" + One(r.StdErr), TxtImageLog);
            SetStatus("导入失败", "ErrBrush");
            return;
        }

        Log($"导入完成：镜像 {imageName} 已就绪。", TxtImageLog);

        // 备份时随附的配置元数据（<tar>.json）：有它就能按原配置直接重建容器
        string meta = null;
        var jsonPath = file + ".json";
        if (File.Exists(jsonPath))
        {
            try { meta = File.ReadAllText(jsonPath); }
            catch { Log("读取配置元数据失败（" + jsonPath + "），只能恢复为镜像。", TxtImageLog); }
        }

        if (meta == null)
        {
            SetBusy(false);
            Log("  ⚠ 未找到配置元数据（" + jsonPath + "）—— 仅恢复为镜像。", TxtImageLog);
            Log("  新建容器时必须在「启动命令」里填一个常驻命令（例如 sleep infinity 或 sh），" +
                "否则会报 no command specified。", TxtImageLog);
            SetStatus($"已导入镜像 {imageName}（未按原配置重建）", "WarnBrush");
            await RefreshImagesAsync(silent: true);
            return;
        }

        // 按原配置重建容器：名称 / 启动命令 / 端口映射 / 环境变量 / 重启策略
        // ⚠ 解析统一交给 RestorePlanner：端口映射在 inspect 的**顶层 Ports**，
        //   HostConfig 下没有 PortBindings —— 这段以前写在这里、读错了位置，
        //   导致「按原配置恢复」丢掉全部 -p 映射（v0.9.1 修过，并入时又丢了一次）。
        try
        {
            var plan = WslManager.Wslc.Services.RestorePlanner.FromInspectJson(
                meta, imageName, n => _containers.Any(x => x.Name == n));
            var spec = plan.Spec;

            foreach (var note in plan.Notes)
                Log("  ⚠ " + note, TxtImageLog);

            Log($"按原配置重建容器 {spec.Name}：命令「{spec.Command}」、" +
                $"端口 {spec.Ports.Count} 条、环境变量 {spec.Env.Count} 条" +
                (spec.RestartPolicy.Length > 0 ? $"、重启策略 {spec.RestartPolicy}" : ""), TxtImageLog);

            var run = await WslcService.RunAsync(spec, s2 => Log(s2, TxtImageLog));
            if (run.Success)
            {
                Log($"✓ 容器 {spec.Name} 已按原配置恢复。", TxtImageLog);
                SetStatus($"已恢复容器 {spec.Name}", "OkBrush");
            }
            else
            {
                Log("按原配置重建容器失败：" + One(run.StdErr), TxtImageLog);
                Log("镜像已导入，可到「镜像管理」页手动新建（记得填启动命令）。", TxtImageLog);
                SetStatus("镜像已导入，但容器重建失败", "WarnBrush");
            }
        }
        catch (Exception ex)
        {
            Log("解析备份元数据失败，仅恢复为镜像：" + ex.Message, TxtImageLog);
            SetStatus($"已导入镜像 {imageName}（未按原配置重建）", "WarnBrush");
        }

        SetBusy(false);
        await RefreshImagesAsync(silent: true);
        RefreshBackupContainers();
    }

    /// <summary>
    /// 清理：终止 wslc 会话并删除其虚拟磁盘，下次使用时自动重建空会话。
    /// ⚠ 该会话下全部容器与镜像都会丢失 —— 这就是为什么它放在红色卡片里。
    /// </summary>
    private async void OnRebuildSessionClick(object sender, RoutedEventArgs e)
    {
        // 查 vhdx 会触发 GetStorageDir（可能同步跑 wslc info），放后台
        var (vhdx, diskBytes) = await System.Threading.Tasks.Task.Run(() =>
        {
            var v = WslManager.Wslc.Helpers.DiskCompactor.FindStorageVhdx();
            return (v, v == null ? -1 : WslManager.Wslc.Helpers.DiskCompactor.GetVhdxBytes(v));
        });
        string diskLine;
        if (diskBytes >= 0)
        {
            var mb = diskBytes / 1048576.0;
            var reclaim = mb - WslManager.Wslc.Helpers.DiskCompactor.EmptySessionBaseMB;
            diskLine = reclaim > 16
                ? $"\n重建后将释放约 {reclaim:N0} MB（新磁盘自身约 {WslManager.Wslc.Helpers.DiskCompactor.EmptySessionBaseMB} MB 为运行时底座，仍会存在）\n"
                : $"\n当前占用（{mb:N0} MB）已接近空会话的运行时底座（约 {WslManager.Wslc.Helpers.DiskCompactor.EmptySessionBaseMB} MB），重建收益有限。\n";
        }
        else diskLine = "";

        var msg = "将终止 wslc 会话并删除其虚拟磁盘（storage.vhdx），\n" +
                  "下次使用时自动重建一个全新的空会话。\n\n" +
                  $"当前会话下有 {_containers.Count} 个容器、{_images.Count} 个镜像，**全部会被清空**。\n" +
                  "虚拟磁盘内的数据删除后不可恢复 —— 如需保留请先「备份容器」。\n" +
                  diskLine +
                  "\n确定重建吗？";
        if (!AppDialog.Confirm(HostWindow, "重建会话", msg, okText: "重建", danger: true)) return;
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        SetBusy(true, "正在重建会话…");

        // 1) 终止会话（terminate 不接受位置参数，不带参数即终止默认会话）
        var tr = await System.Threading.Tasks.Task.Run(() => WslcService.TerminateSessionAsync());
        Log($"已终止会话（exit={tr.ExitCode}）", TxtImageLog);

        // 2) ⚠ 必须关闭 WSL：wslc 的 vhdx 由 WSL2 VM 挂载，只 terminate 会话的话
        //    VM 仍占着 storage.vhdx / swap.vhdx 的句柄，下一步删目录必然失败
        //    （2026-10-10 实测：terminate 后删除报"会话目录删除失败"，补 shutdown 后成功）。
        var sd = await System.Threading.Tasks.Task.Run(
            () => WslManager.Wslc.Helpers.DiskCompactor.ShutdownWsl());
        Log($"已执行 wsl --shutdown（exit={(sd.Ok ? "0" : "失败")}）", TxtImageLog);
        await System.Threading.Tasks.Task.Delay(1500);   // 留时间让句柄完全释放

        // 3) 删除会话目录（虚拟磁盘与全部数据），带重试 —— 句柄释放可能有延迟
        var dir = WslcService.GetStorageDir();
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            bool deleted = false;
            string lastErr = "";
            for (int attempt = 1; attempt <= 3 && !deleted; attempt++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                    Log($"删除会话目录第 {attempt} 次失败（{ex.GetType().Name}），2 秒后重试…", TxtImageLog);
                    await System.Threading.Tasks.Task.Delay(2000);
                }
            }
            if (!deleted)
            {
                Log("删除会话目录失败：" + lastErr, TxtImageLog);
                SetBusy(false);
                SetStatus("重建失败：会话目录删除失败（可手动删除后重试：" + dir + "）", "ErrBrush");
                return;
            }
            Log($"已删除会话目录：{dir}", TxtImageLog);
        }

        // 4) 触发 wslc 重建空会话（任意命令都会自动建）
        await System.Threading.Tasks.Task.Run(() => WslcService.ListSessions());

        _containers.Clear();
        _images.Clear();
        await RefreshContainersAsync(silent: true);
        await RefreshImagesAsync(silent: true);
        RefreshBackupContainers();
        RefreshSessionDiskInfo();   // 重建后 vhdx 是全新的，刷新占用显示

        SetBusy(false);
        SetStatus("会话已重建（全部容器与镜像已清空）", "OkBrush");
    }

    /// <summary>WinForms 文件对话框的属主窗口包装（WPF Window 不是 IWin32Window）。</summary>
    private System.Windows.Forms.IWin32Window GetIWin32WindowOwner()
        => new Win32WindowHandle(new System.Windows.Interop.WindowInteropHelper(HostWindow).Handle);

    private sealed class Win32WindowHandle(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private void OnOpenWslcSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            WslcService.OpenSettingsFile();
            SetStatus("已用系统默认编辑器打开 wslc 设置文件", "InfoBrush");
        }
        catch (Exception ex)
        {
            SetStatus("打开设置文件失败：" + ex.Message, "ErrBrush");
        }
    }

    private async void OnImageRowOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 放到后台线程取目录，避免同步调 wslc 卡住 UI
            var dir = await System.Threading.Tasks.Task.Run(() => WslcService.GetStorageDir());
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultWslcDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            SetStatus("已打开镜像存储目录：" + dir, "InfoBrush");
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message, "ErrBrush");
        }
    }

    private async System.Threading.Tasks.Task RemoveImageAsync(ImageInfo img)
    {
        SetBusy(true, "正在删除镜像…");
        Log($"执行：wslc image rm -f {img.Reference}", TxtImageLog);
        var r = await WslcService.RemoveImageAsync(img.Reference, force: true);
        SetBusy(false);

        foreach (var line in (r.All ?? "").Split('\n'))
            if (line.Trim().Length > 0) Log("  " + line.TrimEnd('\r'), TxtImageLog);

        // exit=0 不代表删掉了：wslc 可能只「取消标记」，必须看输出里有没有「已删除」行。
        switch (WslcService.InterpretImageRemove(r.Success, r.All))
        {
            case WslcService.ImageRemoveOutcome.Deleted:
                SetStatus($"已删除镜像 {img.Reference}（镜像层已删除）", "OkBrush");
                break;

            case WslcService.ImageRemoveOutcome.UntaggedOnly:
            {
                // ⚠ 不能一口咬定"被容器按住" —— 得实际查引用关系再说。
                // （2026-10-10 用户实测：当时一个容器都没有，却提示"被容器按住"）
                var holders = _containers
                    .Where(x => ImageRefMatches(x.Image, img.Reference))
                    .Select(x => x.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .ToList();

                if (holders.Count > 0)
                {
                    SetStatus($"只移除了标签：{img.Reference} 仍被 {holders.Count} 个容器引用", "WarnBrush");
                    Log($"  注意：没有「已删除」行，该镜像仍被容器引用 —— " +
                        $"先删掉这些容器才能回收：{string.Join("、", holders.Take(6))}" +
                        (holders.Count > 6 ? $" 等 {holders.Count} 个" : ""), TxtImageLog);
                }
                else
                {
                    SetStatus($"只移除了标签：{img.Reference} 的镜像层仍在", "WarnBrush");
                    Log("  注意：没有「已删除」行，但当前**没有容器引用它**。可能是该层仍被其它" +
                        "镜像当作父层共享，或 wslc 这次只做了取消标记。", TxtImageLog);
                }
                break;
            }

            default:
                SetStatus("删除失败：" + One(r.All), "ErrBrush");
                break;
        }

        // 无论层删没删，虚拟磁盘都不会自动缩小 —— 这才是"空间没回收"的常见原因，必须讲清楚。
        Log("  说明：镜像层删掉后，虚拟磁盘 storage.vhdx **不会自动缩小**，" +
            "在「资源管理器」里看文件大小仍不变（实测：删掉 163MB 的 nginx 后磁盘占用 +0.0 MB）。" +
            "要真正释放，需让 WSL 压缩该虚拟磁盘。", TxtImageLog);

        await RefreshImagesAsync(silent: true);
    }

    /// <summary>容器记录的镜像引用与待删镜像是否为同一个（容忍有无 tag、前缀差异）。</summary>
    private static bool ImageRefMatches(string containerImage, string reference)
    {
        if (string.IsNullOrWhiteSpace(containerImage) || string.IsNullOrWhiteSpace(reference)) return false;

        var a = containerImage.Trim();
        var b = reference.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        static string NoTag(string s)
        {
            var i = s.LastIndexOf(':');
            var j = s.LastIndexOf('/');
            return i > j ? s.Substring(0, i) : s;   // 只去掉 tag，保留 registry 端口里的冒号
        }
        return string.Equals(NoTag(a), NoTag(b), StringComparison.OrdinalIgnoreCase);
    }

    private async void OnImageRunClick(object sender, RoutedEventArgs e)
    {
        var img = _selImage;
        if (img == null) { SetStatus("请先在列表中选择一个镜像", "WarnBrush"); return; }

        await EnsureNetworksAsync();
        var dlg = new CreateContainerDialog(_images, _networks.Select(n => n.Name)) { Owner = HostWindow };
        dlg.PresetImage(img.Reference);
        if (dlg.ShowDialog() != true) return;

        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        _ = RunCreatedAsync(dlg.Result);
    }

    private async System.Threading.Tasks.Task RunCreatedAsync(ContainerSpec spec)
    {
        Ui(() => TxtContainerLog.Clear());
        Log("新建容器：" + spec.PreviewCommandLine(), TxtContainerLog);

        SetBusy(true, "正在创建容器…");
        var r = await WslcService.RunAsync(spec, s => Log(s, TxtContainerLog));
        var st = r.Success ? await VerifyCreatedAsync(spec, r) : null;
        SetBusy(false);

        Log(r.Success ? "容器已创建。" : $"创建失败（退出码 {r.ExitCode}）。", TxtContainerLog);
        foreach (var line in TableParser.Lines(r.All)) Log(line, TxtContainerLog);

        if (r.Success && DiedRightAfterStart(st))
        {
            Log(ExplainImmediateExit(!string.IsNullOrWhiteSpace(spec.Name) ? spec.Name : "新容器", st), TxtContainerLog);
            SetStatus($"容器已创建，但随即停止（退出码 {ExitCodeText(st)}）", "WarnBrush");
        }
        else SetStatus(r.Success ? "容器已创建" : "容器创建失败", r.Success ? "OkBrush" : "ErrBrush");

        await RefreshContainersAsync();
    }

    private async void OnPullImageClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先安装或升级 WSL", "WarnBrush");
            return;
        }

        var image = TxtPullImage.Text?.Trim();
        if (string.IsNullOrWhiteSpace(image)) { SetStatus("请先输入镜像名，例如 alpine:latest", "WarnBrush"); return; }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        Ui(() => TxtImageLog.Clear());
        var mirror = AppSettings.RegistryMirror?.Trim() ?? "";
        var actual = WslcService.ComposePullTarget(image, mirror);
        Log($"开始拉取镜像：{image}" +
            (string.Equals(actual, image, StringComparison.Ordinal)
                ? "（未配置加速前缀，直连官方源）"
                : $"　→　{actual}（加速源）"), TxtImageLog);

        SetBusy(true, "正在拉取镜像…");

        // wslc pull 在非终端环境不输出任何进度（实测 0 字节），
        // 所以自己在外面按网卡接收字节数采样，给用户一个实时速度。
        _pullCts = new CancellationTokenSource();
        StartPullMeter();
        Ui(() =>
        {
            BtnPullStop.Visibility = Visibility.Visible;
            BtnPullImage.IsEnabled = false;
        });

        var r = await WslcService.PullAsync(image, mirror, s => Log(s, TxtImageLog), 0, _pullCts.Token);

        StopPullMeter();
        Ui(() =>
        {
            BtnPullStop.Visibility = Visibility.Collapsed;
            BtnPullImage.IsEnabled = true;
        });
        SetBusy(false);

        if (r.Canceled)
        {
            var el = (DateTime.Now - _pullStart).TotalSeconds;
            Log($"已按你的操作停止拉取（已接收 {NetMeter.Human(_pullTotal)}，用时 {el:F0}s）。" +
                "这不是拉取失败，可以随时重新拉取。", TxtImageLog);
            SetStatus($"已停止拉取：{image}", "WarnBrush");
            TxtPullProgress.Text = "";
            await RefreshImagesAsync();
            return;
        }

        Log(r.Success ? $"拉取完成：{image}" : $"拉取失败（退出码 {r.ExitCode}）：{One(r.StdErr)}", TxtImageLog);
        if (!r.Success)
        {
            var tip = DiagnosePullFailure(r, mirror);
            if (tip.Length > 0) Log(tip, TxtImageLog);
        }

        SetStatus(r.Success ? $"镜像拉取完成：{image}" : "镜像拉取失败",
            r.Success ? "OkBrush" : "ErrBrush");

        await RefreshImagesAsync();
    }

    // ── 拉取进度采样（wslc 自己不给进度，只能外部量网速）──
    private CancellationTokenSource _pullCts;
    private DispatcherTimer _pullTimer;
    private long _pullPrevBytes;
    private long _pullStartBytes;
    private long _pullTotal;
    private double _pullSpeed;
    private DateTime _pullStart;

    private void StartPullMeter()
    {
        _pullStart = DateTime.Now;
        _pullStartBytes = NetMeter.TotalBytesReceived();
        _pullPrevBytes = _pullStartBytes;
        _pullTotal = 0;
        _pullSpeed = 0;

        Ui(() => TxtPullProgress.Text = "连接中…");

        _pullTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pullTimer.Tick -= OnPullTick;
        _pullTimer.Tick += OnPullTick;
        _pullTimer.Start();
    }

    private void StopPullMeter()
    {
        _pullTimer?.Stop();
        if (_pullTimer != null) _pullTimer.Tick -= OnPullTick;
    }

    private async void OnPullTick(object sender, EventArgs e)
    {
        long now = await System.Threading.Tasks.Task.Run(() => NetMeter.TotalBytesReceived());
        if (now < 0 || _pullPrevBytes < 0) return;

        long delta = now - _pullPrevBytes;
        if (delta < 0) delta = 0;                    // 计数器回绕
        _pullPrevBytes = now;
        _pullSpeed = delta;                          // 1 秒间隔 ≈ 字节/秒
        _pullTotal = now - _pullStartBytes;

        var el = (DateTime.Now - _pullStart).TotalSeconds;
        var s = _pullSpeed > 0
            ? $"↓ {NetMeter.Human((long)_pullSpeed)}/s"
            : "↓ 等待数据…";
        Ui(() => TxtPullProgress.Text = $"{s}　已接收 {NetMeter.Human(_pullTotal)}　用时 {el:F0}s");
    }

    private void OnStopPullClick(object sender, RoutedEventArgs e)
    {
        if (_pullCts == null || _pullCts.IsCancellationRequested) return;
        try { _pullCts.Cancel(); } catch { }
        SetStatus("正在中断拉取…", "WarnBrush");
    }

    /// <summary>把常见的 wslc pull 失败翻译成「怎么办」，而不是只甩一个退出码。</summary>
    private static string DiagnosePullFailure(RunResult r, string mirror)
    {
        var msg = ((r.StdErr ?? "") + " " + (r.StdOut ?? "")).ToLowerInvariant();

        if (msg.Contains("e_invalidarg") || msg.Contains("映像无效") || msg.Contains("invalid"))
            return "提示：镜像名格式不被接受。若已填加速前缀，请只填纯主机名（例如 mirror.example.com），不要带 https:// 。";

        if (msg.Contains("timeout") || msg.Contains("超时") || msg.Contains("deadline") ||
            msg.Contains("no such host") || msg.Contains("connection") || msg.Contains("exceeded"))
            return string.IsNullOrWhiteSpace(mirror)
                ? "提示：连接超时。官方源 registry-1.docker.io 在国内通常不可达 —— 建议到「设置与加速」填一个加速前缀后重试。"
                : "提示：连接超时。当前加速前缀可能已失效，可在「设置与加速」页点「检测加速地址可用性」换一个。";

        if (msg.Contains("not found") || msg.Contains("manifest unknown") || msg.Contains("不存在"))
            return "提示：仓库里没有这个镜像或标签，检查名字与 tag（例如 alpine:latest）。";

        return "";
    }

    private void OnClearImageLogClick(object sender, RoutedEventArgs e) => TxtImageLog.Clear();

    // ══════════════════════════ 镜像存档：导出 / 导入 tar ══════════════════════════

    private void OnExportImageClick(object sender, RoutedEventArgs e)
    {
        var img = _selImage;
        if (img == null) { SetStatus("请先在列表中选择一个镜像", "WarnBrush"); return; }
        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出镜像为 tar",
            FileName = SafeFileName(img.Reference) + ".tar",
            DefaultExt = ".tar",
            Filter = "tar 存档 (*.tar)|*.tar|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(HostWindow) != true) return;

        var target = dlg.FileName;

        // ⚠ 覆盖已有的 tar 是不可逆的（旧的导出会被直接截断），先说清楚再写
        if (File.Exists(target) &&
            !AppDialog.Confirm(HostWindow, "覆盖现有文件",
                $"这个位置已经有文件了：\n{target}\n\n继续导出会覆盖它，确定吗？",
                okText: "覆盖", danger: true))
            return;

        _ = ExportImageAsync(img, target);
    }

    private async System.Threading.Tasks.Task ExportImageAsync(ImageInfo img, string target)
    {
        TxtImageLog.Clear();
        Log($"导出镜像：{img.Reference}", TxtImageLog);
        Log($"  输出到：{target}", TxtImageLog);

        SetBusy(true, "正在导出镜像…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await WslcService.ImageSaveAsync(img.Reference, target,
            onLine: line => Log(line, TxtImageLog));
        sw.Stop();
        SetBusy(false);

        long size = -1;
        try { if (File.Exists(target)) size = new FileInfo(target).Length; } catch { }

        if (r.Success && size > 0)
        {
            Log($"导出完成：{HumanSize(size)}，耗时 {sw.Elapsed.TotalSeconds:F1} 秒。", TxtImageLog);
            SetStatus($"镜像已导出（{HumanSize(size)}）", "OkBrush");
        }
        else
        {
            foreach (var line in TableParser.Lines(r.All)) Log(line, TxtImageLog);
            Log(r.Canceled
                ? "导出已取消。"
                : $"导出失败（退出码 {r.ExitCode}）。产物可能只有半截，建议删掉后重试。", TxtImageLog);
            SetStatus(r.Canceled ? "已取消导出" : "镜像导出失败：" + One(r.All),
                r.Canceled ? "WarnBrush" : "ErrBrush");
        }
    }

    private async void OnImportImageClick(object sender, RoutedEventArgs e)
    {
        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，请先在「环境与安装」页安装或升级 WSL", "WarnBrush");
            return;
        }
        if (_busy) { SetStatus("已有任务在执行，请稍候", "WarnBrush"); return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的镜像 tar",
            CheckFileExists = true,
            Filter = "tar 存档 (*.tar)|*.tar|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(HostWindow) != true) return;

        var file = dlg.FileName;
        long size = -1;
        try { size = new FileInfo(file).Length; } catch { }

        TxtImageLog.Clear();
        Log($"导入镜像：{file}" + (size >= 0 ? $"（{HumanSize(size)}）" : ""), TxtImageLog);
        Log("  说明：这是仓库格式的 tar，标签会一起恢复（与「备份与清理」导出的容器文件系统不同）。", TxtImageLog);

        SetBusy(true, "正在导入镜像…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await WslcService.ImageLoadAsync(file, onLine: line => Log(line, TxtImageLog));
        sw.Stop();
        SetBusy(false);

        if (r.Success)
        {
            // wslc 的成功输出就一行（实测「已加载映像: xxx:tag」），全量打印即可 ——
            // 顺带避免"只认英文 Loaded"这种本地化坑。
            foreach (var line in TableParser.Lines(r.All)) Log(line, TxtImageLog);
            Log($"导入完成，耗时 {sw.Elapsed.TotalSeconds:F1} 秒。", TxtImageLog);
            SetStatus("镜像已导入", "OkBrush");
        }
        else
        {
            foreach (var line in TableParser.Lines(r.All)) Log(line, TxtImageLog);
            Log($"导入失败（退出码 {r.ExitCode}）。", TxtImageLog);
            SetStatus("镜像导入失败：" + One(r.All), "ErrBrush");
        }

        await RefreshImagesAsync();
    }

    /// <summary>字节 → 人类可读。</summary>
    private static string HumanSize(long bytes)
    {
        if (bytes < 0) return "未知";
        if (bytes < 1024) return bytes + " B";
        double v = bytes / 1024.0;
        if (v < 1024) return v.ToString("N1") + " KB";
        v /= 1024.0;
        if (v < 1024) return v.ToString("N1") + " MB";
        return (v / 1024.0).ToString("N2") + " GB";
    }

    /// <summary>把镜像引用名收成一个可作文件名的字符串（tag 里的 / 与 : 在 Windows 上非法）。</summary>
    private static string SafeFileName(string reference)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (reference ?? "").Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        var name = sb.ToString().Trim('_', '.');
        return name.Length == 0 ? "image" : name;
    }

    // ══════════════════════════ 设置 ══════════════════════════
    private void LoadSettingsIntoUi()
    {
        TxtMirror.Text = AppSettings.RegistryMirror ?? "";
        // 注意：这里绝不能同步调用 wslc（会在 UI 线程阻塞）；
        // v2.5：只留配置文件路径，wslc 数据目录那行删掉 —— 真正的存储目录由
        // 「镜像 → 打开存储目录」（同步 wslc 查询）给出，这里不重复写一个推断值。
        TxtConfigPath.Text = "配置文件：" + AppSettings.Location;
        UpdateIndexInfo();
        UpdateMirrorPreview();
        UpdateSelectedCount();

        // 自启：勾选框直接反映注册表的真实状态（注册表才是唯一事实）
        _settingsLoading = true;
        try
        {
            ChkAutoStart.IsChecked = AutoStart.IsEnabled();
            ChkAutoStartMin.IsChecked = AppSettings.AutoStartMinimized;
        }
        finally { _settingsLoading = false; }
        UpdateAutoStartState();
    }

    // ── 开机自启 ──────────────────────────────────────────────

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _settingsLoading) return;

        bool want = ChkAutoStart.IsChecked == true;
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

        var (ok, msg) = want ? AutoStart.Enable(exe) : AutoStart.Disable();
        if (!ok)
        {
            // 写不进去就把勾选框拨回真实状态 —— 界面不能停留在一个没生效的"已开启"上
            _settingsLoading = true;
            try { ChkAutoStart.IsChecked = AutoStart.IsEnabled(); }
            finally { _settingsLoading = false; }

            AppDialog.Info(HostWindow, "设置失败", "写注册表启动项失败：\n" + msg);
            SetStatus("开机自启设置失败", "ErrBrush");
            return;
        }

        UpdateAutoStartState();
        Log(want ? "已开启开机自启：" + msg : "已关闭开机自启");
        SetStatus(want ? "已开启开机自启（登录后自动拉起 wslc 与上次运行中的容器）" : "已关闭开机自启",
            want ? "OkBrush" : "InfoBrush");
    }

    private void UpdateAutoStartState()
    {
        if (TxtAutoStartState == null) return;

        var cur = AutoStart.CurrentCommandLine();
        if (cur.Length == 0)
        {
            TxtAutoStartState.Text =
                "当前状态：未启用\r\n" +
                "注册表项：HKCU\\" + AutoStart.RegistryKeyPath + "（无本程序）";
            return;
        }

        var names = AutoStart.LoadSnapshot();
        TxtAutoStartState.Text =
            "当前状态：已启用\r\n" +
            "启动项：" + cur + "\r\n" +
            "名单文件：" + AutoStart.SnapshotPath + $"（{names.Count} 个容器）";
    }

    /// <summary>「立即试跑一次恢复」：不重启电脑，就按名单拉起一遍，用来验证配置对不对。</summary>
    private async void OnTryAutoStartNowClick(object sender, RoutedEventArgs e)
    {
        var names = AutoStart.LoadSnapshot();
        if (names.Count == 0)
        {
            SetStatus("名单里没有容器 —— 到容器页刷新一次列表，或先启动几个容器", "WarnBrush");
            Log("试跑自启恢复：名单为空，什么都没做。", TxtLog);
            return;
        }

        await RunAutoStartRestoreAsync(names, fromTray: false);
    }

    /// <summary>
    /// 按名单恢复容器。开机自启与「立即试跑」共用这一条路径（同一份代码，
    /// 免得「试跑通过、真自启却不成」）。
    /// </summary>
    private async System.Threading.Tasks.Task RunAutoStartRestoreAsync(
        IReadOnlyList<string> names, bool fromTray)
    {
        if (names == null || names.Count == 0) return;

        if (!WslcService.Available)
        {
            SetStatus("wslc 尚未安装，没法恢复容器", "WarnBrush");
            return;
        }

        SetBusy(true, "正在恢复容器…");
        Log($"开始恢复容器：共 {names.Count} 个（{string.Join("、", names)}）", TxtLog);

        // ① 先预热会话：WSL 刚开机时 wslc 会话还没起来，第一条 start 会顺带拉起它 ——
        //   但那会把整个会话启动时间算进第一条的超时预算。用最轻的一条命令先敲门。
        var warm = await WslcService.RawListNetworksAsync();
        Log("wslc 会话预热：" + (warm.Success ? "就绪" : "失败（" + One(warm.All) + "）"), TxtLog);

        int ok = 0, failed = 0;
        foreach (var n in names)
        {
            var r = await WslcService.StartAsync(n);

            if (r.Success) { ok++; Log($"  {n}：已启动", TxtLog); }
            else if ((r.All ?? "").Contains("already running", StringComparison.OrdinalIgnoreCase) ||
                     (r.All ?? "").Contains("已在运行", StringComparison.Ordinal))
            {
                // 本来就在跑 —— 这是成功态，不能算失败（否则每次自启都会虚报一堆失败）
                ok++; Log($"  {n}：本来就在运行", TxtLog);
            }
            else { failed++; Log($"  {n}：启动失败（{One(r.All)}）", TxtLog); }
        }

        SetBusy(false);
        Log($"恢复完成：成功 {ok} 个" + (failed > 0 ? $"，失败 {failed} 个" : "") + "。", TxtLog);
        SetStatus($"自启恢复：成功 {ok} 个" + (failed > 0 ? $"，失败 {failed} 个" : ""),
            failed > 0 ? "WarnBrush" : "OkBrush");

        await RefreshContainersAsync(silent: true);
    }

    /// <summary>
    /// 把「当前处于运行中」的容器名写进自启名单。
    ///
    /// 三个时机都要，缺一不可：列表刷新后（名单新鲜）／真正退出时（用户主动关）／
    /// 系统关机（<c>SessionEnding</c> —— 那时窗口不走 Closing，不挂这个钩子就会丢最后一次状态）。
    /// </summary>
    public void SaveAutoStartSnapshotNow()
    {
        if (_demoMode) return;   // 演示数据绝不能写进真实名单
        try
        {
            var running = _containers
                .Where(c => c.IsRunning && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => c.Name)
                .ToList();
            AutoStart.SaveSnapshot(running);
        }
        catch { }
    }

    // ── 镜像索引（wslc 无 search 子命令，检索靠本地索引，索引要能更新）──

    private void UpdateIndexInfo()
    {
        TxtIndexInfo.Text = $"当前索引：{ImageSearchService.IndexSummary}\r\n" +
                            $"索引文件：{ImageSearchService.IndexFilePath}" +
                            (File.Exists(ImageSearchService.IndexFilePath) ? "（已存在）" : "（尚未生成）");
    }

    private void OnExportIndexClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出镜像索引",
            FileName = "image-index.json",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(HostWindow) != true) return;

        var (ok, msg) = ImageSearchService.ExportIndex(dlg.FileName);
        TxtIndexMsg.Text = msg;
        SetStatus(msg, ok ? "OkBrush" : "ErrBrush");
    }

    private void OnImportIndexClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择镜像索引 JSON",
            CheckFileExists = true,
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(HostWindow) != true) return;

        var (ok, msg) = ImageSearchService.ImportIndex(dlg.FileName);
        TxtIndexMsg.Text = msg;
        SetStatus(msg, ok ? "OkBrush" : "ErrBrush");
        if (ok) UpdateIndexInfo();
    }

    private void OnResetIndexClick(object sender, RoutedEventArgs e)
    {
        ImageSearchService.ResetToBuiltIn();
        var msg = $"已恢复内置索引：{ImageSearchService.IndexSummary}";
        TxtIndexMsg.Text = msg;
        UpdateIndexInfo();
        SetStatus(msg, "OkBrush");
    }

    private static string DefaultWslcDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslc");

    private void OnMirrorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateMirrorPreview();
    }

    private void UpdateMirrorPreview()
    {
        // 预览走与 PullAsync 相同的组合逻辑，保证「所见即所执行」
        // （也顺带体现两件事：填 https:// 前缀会被自动剥离成纯 host；
        //   官方镜像会自动补 library/ —— 部分镜像站对裸名直接回 denied）
        var pre = WslcService.NormalizeMirrorPrefix(TxtMirror.Text);
        var sample = WslcService.ComposePullTarget("alpine:latest", TxtMirror.Text);
        TxtMirrorPreview.Text = pre.Length == 0
            ? $"预览：拉取 alpine:latest  →  wslc pull {sample}（未配置加速，直连官方源）"
            : $"预览：拉取 alpine:latest  →  wslc pull {sample}（官方镜像自动补 library/）";

        // 填了带 scheme 的地址时给一句提醒（wslc 只认纯 host）
        var raw = (TxtMirror.Text ?? "").Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            TxtMirrorPreview.Text += "\n（https:// 会自动去掉，wslc 只接受纯主机名）";
    }

    private void OnMirrorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string s)
        {
            TxtMirror.Text = s;
            SetStatus("已填入加速前缀：" + s + "（记得保存）", "InfoBrush");
        }
    }

    /// <summary>
    /// `--dump-theme` 的报告正文（容器模块部分）：把「靠肉眼看不出几像素」的对齐关系
    /// 量成数字 —— 状态栏圆点与状态文字是否同基线、导航项宽度是否一致。
    /// </summary>
    public string BuildThemeAlignmentReport()
    {
        var sb = new StringBuilder();

        sb.AppendLine("== 容器模块：状态栏圆点 vs 状态文字（应在同一条基线上）==");
        var dot = RectInView(ElpStatus);
        var txt = RectInView(TxtStatus);
        if (!dot.IsEmpty && !txt.IsEmpty)
        {
            double dc = dot.Y + dot.Height / 2, tc = txt.Y + txt.Height / 2;
            sb.AppendLine($"  状态圆点  x={dot.X:0.#}  y={dot.Y:0.#}  {dot.Width:0.#}×{dot.Height:0.#}   中心 y={dc:0.#}");
            sb.AppendLine($"  状态文字  x={txt.X:0.#}  y={txt.Y:0.#}  {txt.Width:0.#}×{txt.Height:0.#}   中心 y={tc:0.#}");
            sb.AppendLine($"  中心差    |Δcy| = {Math.Abs(dc - tc):0.#} px  →  " +
                          $"{(Math.Abs(dc - tc) <= 2 ? "同基线 PASS" : "不在同一条线 !!")}");
        }
        else sb.AppendLine("  !! 量不到（控件未布局）");

        sb.AppendLine();
        sb.AppendLine("== 左侧导航（各导航项宽度应一致）==");
        var navs = new (string Name, RadioButton El)[]
        {
            ("环境与安装", NavEnv), ("发行版管理", NavDistro), ("容器管理", NavContainer),
            ("镜像管理", NavImage), ("容器网络管理", NavNetwork), ("备份与清理", NavBackup),
            ("设置与加速", NavSettings),
        };
        foreach (var n in navs)
        {
            var r = RectInView(n.El);
            sb.AppendLine(r.IsEmpty
                ? $"  {n.Name,-10} !! 未布局"
                : $"  {n.Name,-10} x={r.X:0.#}  y={r.Y:0.#}  {r.Width:0.#}×{r.Height:0.#}");
        }

        sb.AppendLine();
        sb.AppendLine("== 容器表 ==");
        var grid = RectInView(GridContainers);
        sb.AppendLine($"  x={grid.X:0.#}  y={grid.Y:0.#}  {grid.Width:0.#}×{grid.Height:0.#}  行数 {GridContainers.Items.Count}");

        return sb.ToString();
    }

    private Rect RectInView(FrameworkElement el)
    {
        if (el == null || el.ActualWidth <= 0 || el.ActualHeight <= 0) return Rect.Empty;
        try
        {
            var p = el.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
        }
        catch { return Rect.Empty; }
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        AppSettings.RegistryMirror = (TxtMirror.Text ?? "").Trim();
        AppSettings.AutoStartMinimized = ChkAutoStartMin.IsChecked == true;
        AppSettings.Save();

        _realtime.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(AppSettings.StatsIntervalMs, 1000, 60000));

        SetStatus("设置已保存（立即生效，无需重启）", "OkBrush");
        Log("设置已保存：" + AppSettings.Location);
        Log($"  加速前缀 = {(AppSettings.RegistryMirror.Length == 0 ? "(空，直连官方源)" : AppSettings.RegistryMirror)}");
        Log($"  镜像索引 = {ImageSearchService.IndexSummary}");
        Log($"  自启时最小化到托盘 = {(AppSettings.AutoStartMinimized ? "是" : "否")}" +
            $"（是否开机自启见注册表，当前：{(AutoStart.IsEnabled() ? "已启用" : "未启用")}）");
    }

    private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppSettings.Location);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("打开目录失败：" + ex.Message, "ErrBrush"); }
    }

    /// <summary>重启应用：拉起新实例并退出当前进程。</summary>
    private void OnRestartAppClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) { SetStatus("无法定位程序路径", "ErrBrush"); return; }

            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus("重启失败：" + ex.Message, "ErrBrush");
        }
    }
}
