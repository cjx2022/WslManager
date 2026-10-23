using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WslManager.UI;
using WslManager.Wsl;

namespace WslManager.Wslc;

/// <summary>
/// 「发行版管理」页（v2.2 并入）。
///
/// 为什么用 partial 而不是另建 UserControl：本页要用的样式（NavItem / Card / ToolButton /
/// RowButton / Dot）、调色板替换（ThemeSwitcher 只认 WslcView 这一份）、状态栏全都在
/// <see cref="WslcView"/> 的资源与字段里。另建一个 UserControl 就得把那套样式复制一遍，
/// 换主题时还要多接管一处 —— 拆成 partial 文件只是「代码分文件」，视图仍是同一棵树。
///
/// 功能对齐 v2.1 的独立发行版模块：列表 / 开关机 / 重启 / 全部开关机 / 刷新 / 自动刷新 /
/// 终端 / 文件 / 设为默认 / 复制 IP / 复制名称 / 强制关机 / 卸载。命令参数一律走控件 Tag
/// （绑定发行版名），与容器页的写法保持一致。
/// </summary>
public partial class WslcView
{
    private readonly ObservableCollection<DistroViewModel> _distros = new();
    private readonly DispatcherTimer _distroTimer = new();

    private bool _distroRefreshing;
    private string _distroSignature = "";

    private InstallWindow _installWin;
    private UninstallWindow _uninstallWin;

    /// <summary>发行版运行数变化（供宿主更新托盘图标/文案）。参数 = 运行中的发行版个数。</summary>
    public event Action<int> DistroRunningChanged;

    /// <summary>当前运行中的发行版个数。</summary>
    public int RunningDistroCount => _distros.Count(d => d.IsRunning);

    /// <summary>构造函数里调用一次（在 InitializeComponent 之后）。</summary>
    private void InitDistroPage()
    {
        ListDistros.ItemsSource = _distros;

        _distroTimer.Interval = TimeSpan.FromSeconds(5);
        _distroTimer.Tick += async (_, _) => await RefreshDistrosAsync(silent: true);
    }

    /// <summary>切到本页时调用（导航、以及启动时直接落在本页的场合）。</summary>
    private async Task EnterDistroPageAsync()
    {
        UpdateDistroTimer();
        if (TxtDistroWslVersion.Text.Length == 0)
        {
            try { TxtDistroWslVersion.Text = await WslService.GetWslVersionAsync(); }
            catch { }
        }
        await RefreshDistrosAsync(silent: true);
    }

    private void UpdateDistroTimer()
    {
        bool on = NavDistro.IsChecked == true && ChkDistroAutoRefresh.IsChecked == true;
        if (on) _distroTimer.Start();
        else _distroTimer.Stop();
    }

    private void SetDistroBusy(bool busy)
        => Ui(() => BarDistroBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed);

    // ══════════════════════════ 刷新 ══════════════════════════

    public async Task RefreshDistrosAsync(bool silent = false)
    {
        if (_distroRefreshing) return;
        _distroRefreshing = true;
        if (!silent) SetDistroBusy(true);

        try
        {
            var list = await WslService.ListDistrosAsync();

            // 先删掉已经不在列表里的（卸载后），再按名字就地更新，避免整表重建导致闪烁
            for (int i = _distros.Count - 1; i >= 0; i--)
            {
                var name = _distros[i].Name;
                if (!list.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                    _distros.RemoveAt(i);
            }

            foreach (var d in list)
            {
                var vm = _distros.FirstOrDefault(x =>
                    string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                if (vm == null)
                {
                    vm = new DistroViewModel();
                    vm.UpdateFrom(d);
                    _distros.Add(vm);
                }
                else
                {
                    vm.UpdateFrom(d);
                }
            }

            SortDistros();
            UpdateDistroSummary();

            var sig = string.Join("|", list.Select(d => d.Name + "=" + d.State));
            if (sig != _distroSignature)
            {
                _distroSignature = sig;
                Log("发行版状态变化：" + sig);
            }

            TxtDistroUpdated.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            DistroEmpty.Visibility = _distros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            await UpdateDistroIpsAsync();
            UpdateDistroUptimes();
            await UpdateDistroUsageAsync();

            DistroRunningChanged?.Invoke(_distros.Count(d => d.IsRunning));
        }
        catch (Exception ex)
        {
            Log("发行版刷新失败：" + ex.Message);
            if (!silent) SetStatus("读取发行版状态失败：" + ex.Message, "ErrBrush");
        }
        finally
        {
            _distroRefreshing = false;
            if (!silent) SetDistroBusy(false);
        }
    }

    /// <summary>默认发行版排在最前，其余按名称排序（与 v2.1 一致）。</summary>
    private void SortDistros()
    {
        for (int i = 0; i < _distros.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < _distros.Count; j++)
                if (CompareDistro(_distros[j], _distros[best]) < 0)
                    best = j;
            if (best != i) _distros.Move(best, i);
        }
    }

    private static int CompareDistro(DistroViewModel a, DistroViewModel b)
    {
        if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdateDistroIpsAsync()
    {
        foreach (var item in _distros.ToList())
        {
            try
            {
                if (item.IsRunning)
                {
                    var ip = await WslService.GetIpAsync(item.Name);
                    item.Ip = ip.Length > 0 ? ip : "未获取到 IP";
                }
                else
                {
                    item.Ip = "";
                }
            }
            catch
            {
                // 单条取 IP 失败不影响整表刷新
            }
        }
    }

    private void UpdateDistroUptimes()
    {
        foreach (var d in _distros)
        {
            var start = WslService.GetStartTime(d.Name);
            d.Uptime = start.HasValue && d.IsRunning ? FormatUptime(System.DateTime.Now - start.Value) : "";
        }
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} 分 {ts.Seconds} 秒";
        return ts.Seconds + " 秒";
    }

    // ── WSL 虚拟机利用率（卡片正中，v2.4 加、v2.5 修）────────────────────────
    // v2.4 读 Vmmem/VmmemWSL 的 TotalProcessorTime，但这两个是受保护的系统进程，
    // 一访问就抛 AccessDenied；整段又被一个 try 兜住，于是连内存也一起丢掉 ——
    // 卡片正中什么都不显示。v2.5 改由 WslStat 在发行版里读 /proc/stat 与
    // /proc/meminfo（见 Wslc/Services/WslStat.cs 的注释），一次 exec 拿两项。
    // ⚠ 仍是虚拟机级读数：多个发行版共享同一台 VM，Windows/WSL 都不提供按发行版拆分。

    private async System.Threading.Tasks.Task UpdateDistroUsageAsync()
    {
        string running = _distros.FirstOrDefault(d => d.IsRunning)?.Name;
        if (string.IsNullOrWhiteSpace(running)) { ClearDistroUsage(); return; }

        WslManager.Wslc.Services.WslVmUsage u;
        try { u = await System.Threading.Tasks.Task.Run(() => WslManager.Wslc.Services.WslStat.Read(running)); }
        catch { u = WslManager.Wslc.Services.WslVmUsage.Empty; }

        // CPU% 是两次采样做差，刚进页面时只有一次采样 → 会显示「CPU —」等 5 秒。
        // 这里补一次 0.7 秒后的采样，让数字立刻出来（/proc/stat 粒度是 10ms，够用）。
        if (u.Ok && double.IsNaN(u.CpuPercent))
        {
            await System.Threading.Tasks.Task.Delay(700);
            try { u = await System.Threading.Tasks.Task.Run(() => WslManager.Wslc.Services.WslStat.Read(running)); }
            catch { }
        }

        string cpu = !u.Ok ? ""
                   : double.IsNaN(u.CpuPercent) ? "CPU —"        // 首轮还没有上次采样
                   : $"CPU {u.CpuPercent:0}%";
        string mem = u.Ok ? $"内存 {u.MemPercent:0}%" : "";

        // 数值段（只有这一段变色）：CPU 首轮没上次采样时显示「—」，保持默认色。
        string cpuValue = !u.Ok ? ""
                        : double.IsNaN(u.CpuPercent) ? "—"
                        : $"{u.CpuPercent:0}%";
        string memValue = u.Ok ? $"{u.MemPercent:0}%" : "";

        // 颜色档位（v2.5.1）：<60% 绿 · 60~85% 黄 · ≥85% 红。
        // 边界取 >=：正好 60 归黄、正好 85 归红 —— 运维上「到线即预警」比「超线才预警」更合理。
        string cpuLevel = u.Ok && !double.IsNaN(u.CpuPercent) ? UsageLevel(u.CpuPercent) : "";
        string memLevel = u.Ok ? UsageLevel(u.MemPercent) : "";

        string tip = u.Ok
            ? "WSL 虚拟机整体利用率：CPU "
              + (double.IsNaN(u.CpuPercent) ? "采样中" : u.CpuPercent.ToString("0") + "%")
              + $" · 内存 {u.UsedMb:0} MB / {u.TotalMb:0} MB（{u.MemPercent:0}%）\r\n"
              + "所有发行版共享同一台虚拟机，这是虚拟机级别的读数\r\n"
              + "颜色：<60% 绿 · 60~85% 黄 · ≥85% 红"
            : "暂时读不到虚拟机利用率（发行版可能正在启动或停止）";

        Ui(() =>
        {
            foreach (var d in _distros)
            {
                if (d.IsRunning)
                {
                    d.UsageCpu = cpu; d.UsageMem = mem; d.UsageTip = tip;
                    d.UsageCpuValue = cpuValue; d.UsageMemValue = memValue;
                    d.UsageCpuLevel = cpuLevel; d.UsageMemLevel = memLevel;
                }
                else
                {
                    d.UsageCpu = ""; d.UsageMem = ""; d.UsageTip = "";
                    d.UsageCpuValue = ""; d.UsageMemValue = "";
                    d.UsageCpuLevel = ""; d.UsageMemLevel = "";
                }
            }
        });
    }

    /// <summary>利用率档位 → XAML 里的 DataTrigger 值："ok" / "warn" / "danger"。</summary>
    private static string UsageLevel(double percent)
    {
        if (double.IsNaN(percent)) return "";
        if (percent >= 85) return "danger";
        if (percent >= 60) return "warn";
        return "ok";
    }

    private void ClearDistroUsage()
    {
        foreach (var d in _distros)
        {
            d.UsageCpu = ""; d.UsageMem = ""; d.UsageTip = "";
            d.UsageCpuValue = ""; d.UsageMemValue = "";
            d.UsageCpuLevel = ""; d.UsageMemLevel = "";
        }
    }

    private void UpdateDistroSummary()
    {
        int total = _distros.Count;
        int running = _distros.Count(d => d.IsRunning);
        TxtDistroSummary.Text = $"共 {total} 个发行版  ·  {running} 个运行中  ·  {total - running} 个已停止";
    }

    // ══════════════════════════ 操作 ══════════════════════════

    private async void OnDistroRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshDistrosAsync();
        SetStatus("发行版状态已刷新", "OkBrush");
    }

    private void OnDistroAutoRefreshChanged(object sender, RoutedEventArgs e) => UpdateDistroTimer();

    private async void OnDistroStartClick(object sender, RoutedEventArgs e)
    {
        var name = TagOf(sender);
        if (name.Length == 0) return;
        await RunDistroAction(name, "开机", () => WslService.StartAsync(name));
    }

    private async void OnDistroStopClick(object sender, RoutedEventArgs e)
    {
        var name = TagOf(sender);
        if (name.Length == 0) return;
        await RunDistroAction(name, "关机", () => WslService.StopAsync(name),
            r => r.Graceful ? $"{name} 已正常关机" : $"{name} 已强制终止（软关机超时，未落盘的数据可能丢失）");
    }

    private async void OnDistroRestartClick(object sender, RoutedEventArgs e)
    {
        var name = TagOf(sender);
        if (name.Length == 0) return;
        await RunDistroAction(name, "重启", () => WslService.RestartAsync(name));
    }

    private async void OnDistroStartAllClick(object sender, RoutedEventArgs e) => await StartAllDistrosAsync();

    private async void OnDistroStopAllClick(object sender, RoutedEventArgs e) => await StopAllDistrosAsync();

    /// <summary>全部开机（工具栏与宿主托盘菜单共用同一入口）。</summary>
    public async Task StartAllDistrosAsync()
    {
        var names = _distros.Where(d => !d.IsRunning).Select(d => d.Name).ToList();
        if (names.Count == 0)
        {
            SetStatus("所有发行版都已在运行中", "InfoBrush");
            return;
        }

        BusyEnterDistro();
        SetStatus($"正在开机 {names.Count} 个发行版…", "InfoBrush");
        int ok = 0;
        foreach (var n in names)
            if ((await WslService.StartAsync(n)).Success) ok++;
        BusyLeaveDistro();

        SetStatus($"已开机 {ok}/{names.Count} 个发行版", ok == names.Count ? "OkBrush" : "WarnBrush");
        await RefreshDistrosAsync(silent: true);
    }

    /// <summary>全部关机（同上）。</summary>
    public async Task StopAllDistrosAsync()
    {
        BusyEnterDistro();
        SetStatus("正在软关机所有发行版，未响应者将强制关闭…", "InfoBrush");
        var r = await WslService.ShutdownAllAsync();
        BusyLeaveDistro();

        SetStatus(r.Success
                ? "已关闭所有发行版（先软关机，再 wsl --shutdown 兜底）"
                : "关闭失败：" + r.ErrorMessage,
            r.Success ? "OkBrush" : "ErrBrush");
        await RefreshDistrosAsync(silent: true);
    }

    private void OnDistroTerminalClick(object sender, RoutedEventArgs e)
    {
        var name = TagOf(sender);
        if (name.Length == 0) return;
        SetStatus("正在打开 " + name + " 终端…", "InfoBrush");
        WslService.OpenTerminal(name);
    }

    private void OnDistroFilesClick(object sender, RoutedEventArgs e)
    {
        var name = TagOf(sender);
        if (name.Length == 0) return;
        SetStatus("正在打开 " + name + " 文件系统…", "InfoBrush");
        WslService.OpenFiles(name);
    }

    private void OnDistroMoreClick(object sender, RoutedEventArgs e)
    {
        // ContextMenu 默认不给 PlacementTarget，得自己挂（否则 OwnerName 取不到发行版名）
        if (sender is Button b && b.ContextMenu != null)
        {
            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    private async void OnDistroSetDefaultClick(object sender, RoutedEventArgs e)
    {
        var name = DistroNameFromMenu(sender);
        if (name.Length == 0) return;

        BusyEnterDistro();
        var r = await WslService.SetDefaultAsync(name);
        BusyLeaveDistro();

        SetStatus(r.Success ? $"已将 {name} 设为默认发行版" : "设置失败：" + r.ErrorMessage,
            r.Success ? "OkBrush" : "ErrBrush");
        await RefreshDistrosAsync(silent: true);
    }

    private void OnDistroCopyIpClick(object sender, RoutedEventArgs e)
    {
        var name = DistroNameFromMenu(sender);
        var vm = _distros.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (vm == null) return;

        if (vm.Ip.Length > 0 && vm.Ip != "未获取到 IP")
        {
            // 走统一的 TrySetClipboard：抛异常不等于没写进去，以回读结果为准
            if (TrySetClipboard(vm.Ip)) SetStatus("已复制 IP：" + vm.Ip, "OkBrush");
            else SetStatus("复制 IP 失败（剪贴板被别的程序占用，稍后再点一次）", "ErrBrush");
        }
        else
        {
            SetStatus("该发行版当前没有可用 IP", "WarnBrush");
        }
    }

    private void OnDistroCopyNameClick(object sender, RoutedEventArgs e)
    {
        var name = DistroNameFromMenu(sender);
        if (name.Length == 0) return;
        if (TrySetClipboard(name)) SetStatus("已复制发行版名称：" + name, "OkBrush");
        else SetStatus("复制发行版名称失败（剪贴板被别的程序占用，稍后再点一次）", "ErrBrush");
    }

    private async void OnDistroForceStopClick(object sender, RoutedEventArgs e)
    {
        var name = DistroNameFromMenu(sender);
        if (name.Length == 0) return;
        await RunDistroAction(name, "强制关机", () => WslService.TerminateAsync(name));
    }

    private void OnDistroUninstallItemClick(object sender, RoutedEventArgs e)
        => OpenUninstallWindow(DistroNameFromMenu(sender));

    private void OnUninstallDistroClick(object sender, RoutedEventArgs e) => OpenUninstallWindow(null);

    private async Task RunDistroAction(string name, string verb, Func<Task<WslResult>> action,
        Func<WslResult, string> describe = null)
    {
        var vm = _distros.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (vm == null) return;

        vm.IsBusy = true;
        BusyEnterDistro();
        SetStatus($"正在{verb} {name}…", "InfoBrush");

        try
        {
            var r = await action();
            if (r.Success)
            {
                SetStatus(describe != null ? describe(r) : $"{name} 已{verb}", "OkBrush");
            }
            else
            {
                var msg = r.ErrorMessage.Length > 0 ? r.ErrorMessage : r.StdErr.Trim();
                if (msg.Length == 0) msg = "退出码 " + r.ExitCode;
                SetStatus($"{name} {verb}失败：{msg}", "ErrBrush");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"{name} {verb}异常：{ex.Message}", "ErrBrush");
        }
        finally
        {
            vm.IsBusy = false;
            BusyLeaveDistro();
        }

        await RefreshDistrosAsync(silent: true);
    }

    private void BusyEnterDistro() => SetDistroBusy(true);

    private void BusyLeaveDistro() => SetDistroBusy(false);

    // ══════════════════════════ 安装 / 卸载 ══════════════════════════

    private void OnInstallDistroClick(object sender, RoutedEventArgs e)
    {
        if (_installWin != null && _installWin.IsVisible)
        {
            _installWin.Activate();
            return;
        }

        _installWin = new InstallWindow { Owner = HostWindow };
        _installWin.Closed += async (_, _) =>
        {
            await RefreshDistrosAsync(silent: true);
            SetStatus("安装窗口已关闭，列表已刷新", "InfoBrush");
        };
        _installWin.Show();
        SetStatus("已打开安装窗口（官方源 / 镜像源测速下载安装）", "InfoBrush");
    }

    private void OpenUninstallWindow(string preselect)
    {
        if (_uninstallWin != null && _uninstallWin.IsVisible)
        {
            if (!string.IsNullOrEmpty(preselect)) _uninstallWin.SelectDistro(preselect);
            _uninstallWin.Activate();
            return;
        }

        _uninstallWin = new UninstallWindow
        {
            Owner = HostWindow,
            PreselectName = preselect ?? ""
        };
        _uninstallWin.Closed += async (_, _) =>
        {
            await RefreshDistrosAsync(silent: true);
            SetStatus("卸载窗口已关闭，列表已刷新", "InfoBrush");
        };
        _uninstallWin.Show();
        SetStatus("已打开卸载窗口（导出备份 + 输入确认卸载）", "InfoBrush");
    }

    // ══════════════════════════ 小工具 ══════════════════════════

    private static string TagOf(object sender)
        => sender is FrameworkElement fe ? (fe.Tag as string ?? "") : "";

    /// <summary>从右键菜单反查发行版名：菜单的 PlacementTarget 是「更多」按钮，名字挂在它的 Tag 上。</summary>
    private static string DistroNameFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return "";
        if (mi.Parent is not ContextMenu cm) return "";
        return cm.PlacementTarget is FrameworkElement fe ? (fe.Tag as string ?? "") : "";
    }
}
