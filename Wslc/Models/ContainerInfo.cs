using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WslManager.Wslc.Models;

/// <summary>
/// 容器运行状态。
/// 语义按约定：绿 = 运行中，黄 = 已停止/等待，红色感叹号 = 异常故障。
/// </summary>
public enum ContainerState
{
    Unknown,
    Running,
    Exited,
    Created,
    Paused,
    Restarting,
    /// <summary>用户刚点了停止、操作尚未返回（本地临时状态）。</summary>
    Stopping,
    /// <summary>异常退出（退出码非 0）或 Dead。</summary>
    Failed
}

/// <summary>容器行模型。状态 → 文本/图标/画刷键的映射写在模型里，XAML 只绑定。</summary>
public sealed class ContainerInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private string _id = "";
    private string _name = "";
    private string _image = "";
    private string _status = "";
    private string _ports = "";
    private ContainerState _state = ContainerState.Unknown;
    private string _command = "";
    private string _created = "";
    private bool _isSelected;
    private int _exitCode = int.MinValue;

    public string Id { get => _id; set { if (_id != value) { _id = value ?? ""; Raise(); Raise(nameof(ShortId)); } } }
    public string Name { get => _name; set { if (_name != value) { _name = value ?? ""; Raise(); } } }
    public string Status { get => _status; set { if (_status != value) { _status = value ?? ""; Raise(); } } }
    public string Ports { get => _ports; set { if (_ports != value) { _ports = value ?? ""; Raise(); Raise(nameof(PortsShort)); Raise(nameof(PortsFull)); } } }

    /// <summary>端口映射全量文本（悬浮提示 / 详情用）。</summary>
    public string PortsFull => string.IsNullOrWhiteSpace(_ports) ? "（未映射端口）" : _ports;

    /// <summary>列表里精简显示：只列第一项，多项时加数量。</summary>
    public string PortsShort
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_ports)) return "—";
            var parts = _ports.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return _ports.Trim();
            return parts[0].Trim() + $" 等 {parts.Length} 项";
        }
    }
    public string Command { get => _command; set { if (_command != value) { _command = value ?? ""; Raise(); } } }
    public string Created { get => _created; set { if (_created != value) { _created = value ?? ""; Raise(); } } }

    private string _networks = "";
    /// <summary>所属网络（wslc 的 Networks 字段，默认 bridge）。</summary>
    public string Networks
    {
        get => _networks;
        set { if (_networks != value) { _networks = value ?? ""; Raise(); } }
    }

    public string Image
    {
        get => _image;
        set { if (_image != value) { _image = value ?? ""; Raise(); Raise(nameof(ImageShort)); } }
    }

    /// <summary>镜像短名：只取最后一个 '/' 之后的部分（docker.io/library/alpine:latest → alpine:latest）。</summary>
    public string ImageShort
    {
        get
        {
            if (string.IsNullOrEmpty(_image)) return "";
            var i = _image.LastIndexOf('/');
            var tail = i >= 0 ? _image.Substring(i + 1) : _image;
            return string.IsNullOrEmpty(tail) ? _image : tail;
        }
    }

    /// <summary>退出码（int.MinValue 表示未取到）。</summary>
    public int ExitCode
    {
        get => _exitCode;
        set { if (_exitCode != value) { _exitCode = value; Raise(); } }
    }

    /// <summary>批量操作的多选标记。</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    public ContainerState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise();
            Raise(nameof(StateText));
            Raise(nameof(StateGlyph));
            Raise(nameof(StateBrushKey));
            Raise(nameof(IsRunning));
        }
    }

    public string ShortId => _id.Length > 12 ? _id.Substring(0, 12) : _id;
    public bool IsRunning => _state == ContainerState.Running;

    public string StateText => _state switch
    {
        ContainerState.Running => "运行中",
        ContainerState.Exited => "已停止",
        ContainerState.Created => "待启动",
        ContainerState.Paused => "已暂停",
        ContainerState.Restarting => "重启中",
        ContainerState.Stopping => "停止中",
        ContainerState.Failed => "异常故障",
        _ => "未知"
    };

    /// <summary>状态图标字形：● 圆点（正常/停止/过渡）/ ⚠ 感叹号（异常故障）。</summary>
    public string StateGlyph => _state == ContainerState.Failed || _state == ContainerState.Unknown
        ? "\u26A0"
        : "\u25CF";

    public string StateBrushKey => _state switch
    {
        ContainerState.Running => "OkBrush",
        ContainerState.Restarting => "WarnBrush",
        ContainerState.Stopping => "WarnBrush",
        ContainerState.Paused => "WarnBrush",
        ContainerState.Created => "WarnBrush",
        ContainerState.Exited => "WarnBrush",
        ContainerState.Failed => "ErrBrush",
        ContainerState.Unknown => "ErrBrush",
        _ => "SubTextBrush"
    };

    // ── 性能 ──
    private string _cpu = "—";
    private string _memPct = "—";
    private string _memDetail = "";

    public string Cpu { get => _cpu; set { if (_cpu != value) { _cpu = value ?? "—"; Raise(); Raise(nameof(CpuValue)); } } }
    public string MemPct { get => _memPct; set { if (_memPct != value) { _memPct = value ?? "—"; Raise(); Raise(nameof(MemValue)); } } }
    public string MemDetail { get => _memDetail; set { if (_memDetail != value) { _memDetail = value ?? ""; Raise(); } } }

    public double CpuValue => ParsePercent(_cpu);
    public double MemValue => ParsePercent(_memPct);

    private static double ParsePercent(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.Trim().TrimEnd('%').Trim();
        return double.TryParse(t, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100) : 0;
    }

    public void ApplyStats(ContainerStats st)
    {
        if (st == null) return;
        Cpu = string.IsNullOrWhiteSpace(st.Cpu) ? "—" : st.Cpu;
        MemPct = string.IsNullOrWhiteSpace(st.MemPercent) ? "—" : st.MemPercent;
        MemDetail = string.IsNullOrWhiteSpace(st.Mem) ? "" : "内存用量：" + st.Mem;
        if (!string.IsNullOrWhiteSpace(st.Net)) MemDetail += "　网络：" + st.Net;
        if (!string.IsNullOrWhiteSpace(st.Pids)) MemDetail += "　进程：" + st.Pids;
    }

    /// <summary>
    /// 判定容器状态。
    /// ⚠ 实测（wslc 3.0.1）：`container stop` 正常停止后退出码也是 **137**（因为容器内进程不响应
    /// SIGTERM，被 SIGKILL 强杀），与 `container kill` 完全一样 —— 所以**不能只看退出码**。
    /// 因此优先用容器 JSON 里的 State 字段，退出码只用来把「真正的异常」挑出来。
    /// </summary>
    /// <param name="state">JSON 的 State 字段：running / exited / created / paused / dead …</param>
    /// <param name="status">显示用状态串："Exited (137) 2 seconds ago"</param>
    public static ContainerState ParseState(string state, string status)
    {
        var st = (state ?? "").Trim().ToLowerInvariant();

        switch (st)
        {
            case "running": return ContainerState.Running;
            case "paused": return ContainerState.Paused;
            case "created": return ContainerState.Created;
            case "restarting": return ContainerState.Restarting;
            case "dead": return ContainerState.Failed;
            case "removing": return ContainerState.Exited;
            case "exited": return ClassifyExit(status);
        }

        // State 缺失或未知时，退回按状态串推断
        return ParseStateFromText(status);
    }

    private static ContainerState ParseStateFromText(string raw)
    {
        var s = (raw ?? "").ToLowerInvariant();
        if (s.Length == 0) return ContainerState.Unknown;

        if (s.Contains("pause")) return ContainerState.Paused;
        if (s.Contains("restart")) return ContainerState.Restarting;
        if (s.Contains("dead")) return ContainerState.Failed;
        if (s.Contains("exited") || s.Contains("stopped")) return ClassifyExit(s);
        if (s.Contains("created")) return ContainerState.Created;
        if (s.Contains("up") || s.Contains("running")) return ContainerState.Running;
        return ContainerState.Unknown;
    }

    /// <summary>
    /// 按退出码细分已停止 / 异常。
    /// 130/137/143 分别对应 SIGINT / SIGKILL / SIGTERM，都是「被信号结束」，
    /// 绝大多数情况是用户主动 stop/kill 或系统正常回收，**不应算故障**。
    /// 其余非 0 码（应用自己报错退出）才算异常故障。
    /// </summary>
    private static ContainerState ClassifyExit(string status)
    {
        var code = ParseExitCode(status);
        if (!code.HasValue) return ContainerState.Exited;

        return code.Value switch
        {
            0 => ContainerState.Exited,
            130 => ContainerState.Exited,   // SIGINT
            137 => ContainerState.Exited,   // SIGKILL（stop 超时强杀也走这里）
            143 => ContainerState.Exited,   // SIGTERM
            _ => ContainerState.Failed,
        };
    }

    /// <summary>从 "exited (137) ..." 里取退出码。</summary>
    public static int? ParseExitCode(string raw)
    {
        var s = (raw ?? "").ToLowerInvariant();
        var i = s.IndexOf("exited");
        if (i < 0) return null;
        var open = s.IndexOf('(', i);
        if (open < 0) return null;
        var close = s.IndexOf(')', open);
        if (close < 0) return null;
        var inner = s.Substring(open + 1, close - open - 1).Trim();
        return int.TryParse(inner, out var code) ? code : (int?)null;
    }

    /// <summary>用状态串一次性刷新状态（含退出码）。state 为容器 JSON 里的 State 字段，可空。</summary>
    public void ApplyStatus(string state, string status)
    {
        Status = status;
        var code = ParseExitCode(status);
        if (code.HasValue) ExitCode = code.Value;

        // 用户刚点过停止：在服务端状态跟上来之前，保持「停止中」而不是其它判定
        if (_state == ContainerState.Stopping && !IsRunningState(state, status))
        {
            State = ContainerState.Stopping;
            return;
        }
        State = ParseState(state, status);
    }

    private static bool IsRunningState(string state, string status)
        => (state ?? "").ToLowerInvariant().Contains("running")
           || (status ?? "").ToLowerInvariant().Contains("up");
}
