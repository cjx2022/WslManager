using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WslManager.Wslc.Helpers;
using WslManager.Wslc.Models;
using WslManager.Wslc.Services;

namespace WslManager.Wslc.Dialogs;

/// <summary>单个容器的卡片详情：实时性能 + 基本信息 + 配置/日志。</summary>
public partial class ContainerDetailDialog : Window
{
    private readonly ContainerInfo _c;
    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource _cts;
    private CancellationTokenSource _netCts;
    private bool _ready;
    private bool _logsLoaded;

    public ContainerDetailDialog(ContainerInfo c)
    {
        InitializeComponent();
        _c = c;

        ElpState.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, c.StateBrushKey);
        TxtTitle.Text = string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name;
        TxtState.Text = c.StateText;
        TxtStatus.Text = string.IsNullOrWhiteSpace(c.Status) ? c.StateText : c.Status;
        TxtStatusDetail.Text = string.IsNullOrWhiteSpace(c.Status) ? "—" : c.Status;
        if (c.ExitCode != int.MinValue) TxtStatusDetail.Text += $"　（退出码 {c.ExitCode}）";

        TxtId.Text = c.Id;
        TxtImage.Text = c.Image;
        TxtCreated.Text = c.Created;
        TxtCommand.Text = c.Command;
        TxtPorts.Text = string.IsNullOrWhiteSpace(c.Ports) ? "未映射（Windows 不可直达）" : c.Ports;
        if (string.IsNullOrWhiteSpace(c.Ports))
            TxtPorts.ToolTip = "没有端口映射：容器仍然有 IP，但那只是 WSL 内部私有地址，Windows 侧不能直达。\n" +
                               "要让 Windows 访问容器内的服务，需在创建容器时映射端口（-p 宿主端口:容器端口）。";
        TxtNetwork.Text = string.IsNullOrWhiteSpace(c.Networks) ? "bridge（默认）" : c.Networks;
        TxtIp.Text = "读取中…";
        TxtGateway.Text = "读取中…";

        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (s, e) => await RefreshStatsAsync();

        Loaded += async (s, e) =>
        {
            _ready = true;
            ChromeHelper.CenterOnOwner(this);
            await RefreshStatsAsync();
            await RefreshContentAsync();
            await LoadNetInfoAsync();
            if (_c.IsRunning) _timer.Start();
        };

        Closed += (s, e) =>
        {
            _timer.Stop();
            try { _cts?.Cancel(); } catch { }
            try { _netCts?.Cancel(); } catch { }
        };
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnSegChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool cfg = SegConfig.IsChecked == true;
        TxtConfig.Visibility = cfg ? Visibility.Visible : Visibility.Collapsed;
        TxtLogs.Visibility = cfg ? Visibility.Collapsed : Visibility.Visible;
        if (!cfg && !_logsLoaded) _ = LoadLogsAsync();
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        BtnReload.IsEnabled = false;
        try
        {
            await RefreshStatsAsync();
            _logsLoaded = false;
            await RefreshContentAsync();
            _netCts?.Cancel();          // 上一次还没回来的 inspector 结果会覆盖新值
            await LoadNetInfoAsync();
        }
        finally
        {
            BtnReload.IsEnabled = true;
        }
    }

    /// <summary>
    /// 取容器 IP / 网关。
    /// 只在 `wslc inspect` 里才有 —— `wslc ps` 的「端口」列在未映射时是空的，
    /// 单看列表会以为容器"没有地址"。
    /// </summary>
    private async System.Threading.Tasks.Task LoadNetInfoAsync()
    {
        if (!WslcService.Available)
        {
            TxtIp.Text = "—";
            TxtGateway.Text = "—";
            return;
        }

        var cts = new CancellationTokenSource();
        _netCts = cts;

        var n = await WslcService.InspectNetworkAsync(_c.Name, cts.Token);
        if (cts.IsCancellationRequested) return;

        if (n.NetworkName.Length > 0) TxtNetwork.Text = n.NetworkName;

        if (n.HasIp)
        {
            TxtIp.Text = n.Ip;
            TxtGateway.Text = string.IsNullOrWhiteSpace(n.Gateway) ? "—" : n.Gateway;
            return;
        }

        TxtIp.Text = _c.IsRunning ? "—" : "—（容器未运行）";
        TxtGateway.Text = _c.IsRunning ? "—" : "—（容器未运行）";
    }

    private async System.Threading.Tasks.Task RefreshStatsAsync()
    {
        if (!_ready || !WslcService.Available) return;

        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();

        var (st, raw) = await WslcService.StatsForAsync(_c.Name, _cts.Token);
        if (st == null)
        {
            TxtHint.Text = string.IsNullOrWhiteSpace(raw.ErrorMessage)
                ? "未取到性能数据（容器可能未运行）"
                : "性能采集失败：" + raw.ErrorMessage;
            return;
        }

        TxtCpu.Text = string.IsNullOrWhiteSpace(st.Cpu) ? "—" : st.Cpu;
        BarCpu.Value = st.CpuValue;
        TxtMemPct.Text = string.IsNullOrWhiteSpace(st.MemPercent) ? "—" : st.MemPercent;
        BarMem.Value = st.MemValue;
        TxtMem.Text = string.IsNullOrWhiteSpace(st.Mem) ? "—" : st.Mem;
        TxtNet.Text = string.IsNullOrWhiteSpace(st.Net) ? "—" : st.Net;
        TxtBlock.Text = string.IsNullOrWhiteSpace(st.Block) ? "—" : st.Block;
        TxtPids.Text = string.IsNullOrWhiteSpace(st.Pids) ? "—" : st.Pids;

        TxtHint.Text = "性能更新于 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private async System.Threading.Tasks.Task RefreshContentAsync()
    {
        if (SegLogs.IsChecked == true) await LoadLogsAsync();
        else await LoadConfigAsync();
    }

    private async System.Threading.Tasks.Task LoadConfigAsync()
    {
        if (!WslcService.Available) { TxtConfig.Text = "wslc 未安装。"; return; }
        var r = await WslcService.InspectAsync(_c.Name);
        TxtConfig.Text = string.IsNullOrWhiteSpace(r.All) ? "(无配置输出)" : r.All;
        TxtConfig.ScrollToHome();
    }

    private async System.Threading.Tasks.Task LoadLogsAsync()
    {
        if (!WslcService.Available) { TxtLogs.Text = "wslc 未安装。"; return; }
        var r = await WslcService.LogsAsync(_c.Name, 500);
        TxtLogs.Text = string.IsNullOrWhiteSpace(r.All)
            ? "(暂无日志输出)"
            : string.Join(Environment.NewLine, TableParser.Lines(r.All));
        TxtLogs.ScrollToEnd();
        _logsLoaded = true;
    }
}
