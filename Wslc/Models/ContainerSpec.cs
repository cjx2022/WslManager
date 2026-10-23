using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WslManager.Wslc.Models;

/// <summary>「可视化新建容器」的完整规格。UI 收集 → ToArgs() 生成命令行。</summary>
public sealed class ContainerSpec
{
    public string Image { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Detach { get; set; } = true;
    public bool AutoRemove { get; set; }
    public bool Tty { get; set; }
    public bool Interactive { get; set; }
    public bool PublishAll { get; set; }
    public string RestartPolicy { get; set; } = "";      // "" / always / on-failure / unless-stopped
    public string WorkingDir { get; set; } = "";         // -w
    public string EntrypointOverride { get; set; } = "";
    public string HostName { get; set; } = "";

    /// <summary>
    /// 要连接的网络名（对应 <c>--network</c>）。
    /// 空 = 不下发该参数，走 wslc 默认网络（bridge）—— 与旧版本行为完全一致。
    /// </summary>
    public string Network { get; set; } = "";

    public List<PortMap> Ports { get; } = new();
    public List<VolumeMap> Volumes { get; } = new();
    public List<EnvVar> Env { get; } = new();
    public List<string> ExtraArgs { get; } = new();

    public sealed class PortMap
    {
        public string HostPort { get; set; } = "";
        public string ContainerPort { get; set; } = "";
        public string Protocol { get; set; } = ""; // "" = tcp
        public string Text => string.IsNullOrWhiteSpace(Protocol)
            ? $"{HostPort}:{ContainerPort}"
            : $"{HostPort}:{ContainerPort}/{Protocol}";
    }

    /// <summary>
    /// 一条目录挂载。
    /// ⚠ 这个类必须实现 INotifyPropertyChanged：界面上的「选择…」按钮会**由代码**写 HostPath，
    ///   而绑定是 OneWay 到 UI 的 —— 没有变更通知的话，选完目录输入框还是空的
    ///   （看起来像"选了没用"，实际是值写进去了、界面不知道）。
    /// </summary>
    public sealed class VolumeMap : INotifyPropertyChanged
    {
        private string _hostPath = "";
        private string _containerPath = "";
        private bool _readOnly;

        public string HostPath
        {
            get => _hostPath;
            set { if (_hostPath != value) { _hostPath = value ?? ""; Raise(nameof(HostPath)); Raise(nameof(Text)); } }
        }

        public string ContainerPath
        {
            get => _containerPath;
            set { if (_containerPath != value) { _containerPath = value ?? ""; Raise(nameof(ContainerPath)); Raise(nameof(Text)); } }
        }

        public bool ReadOnly
        {
            get => _readOnly;
            set { if (_readOnly != value) { _readOnly = value; Raise(nameof(ReadOnly)); Raise(nameof(Text)); } }
        }

        public string Text => ReadOnly ? $"{HostPath}:{ContainerPath}:ro" : $"{HostPath}:{ContainerPath}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class EnvVar
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string Text => $"{Key}={Value}";
    }

    /// <summary>生成 wslc run 的参数列表（不含可执行文件与 "run"）。</summary>
    public List<string> ToArgs()
    {
        var a = new List<string>();
        if (Detach) a.Add("-d");
        if (AutoRemove) a.Add("--rm");
        if (Interactive) a.Add("-i");
        if (Tty) a.Add("-t");
        if (PublishAll) a.Add("-P");
        if (!string.IsNullOrWhiteSpace(Name)) { a.Add("--name"); a.Add(Name.Trim()); }
        // ⚠ 不输出 --restart：wslc run 没有该选项（2026-10-10 实测报
        //   「选项名称未被识别：'--restart'」），填了会导致创建失败。
        //   字段保留用于界面展示与备份元数据，但不下发。
        if (!string.IsNullOrWhiteSpace(WorkingDir)) { a.Add("-w"); a.Add(WorkingDir.Trim()); }
        if (!string.IsNullOrWhiteSpace(HostName)) { a.Add("-h"); a.Add(HostName.Trim()); }
        if (!string.IsNullOrWhiteSpace(EntrypointOverride)) { a.Add("--entrypoint"); a.Add(EntrypointOverride.Trim()); }
        // 网络：留空不下发（= wslc 默认网络），这样「不选」与旧版本行为一致
        if (!string.IsNullOrWhiteSpace(Network)) { a.Add("--network"); a.Add(Network.Trim()); }

        foreach (var p in Ports)
        {
            if (string.IsNullOrWhiteSpace(p.ContainerPort)) continue;
            a.Add("-p"); a.Add(p.Text);
        }
        foreach (var v in Volumes)
        {
            if (string.IsNullOrWhiteSpace(v.HostPath) || string.IsNullOrWhiteSpace(v.ContainerPath)) continue;
            a.Add("-v"); a.Add(v.Text);
        }
        foreach (var e in Env)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            a.Add("-e"); a.Add(e.Text);
        }
        foreach (var x in ExtraArgs)
        {
            if (string.IsNullOrWhiteSpace(x)) continue;
            a.Add(x);
        }

        if (!string.IsNullOrWhiteSpace(Image)) a.Add(Image.Trim());

        if (!string.IsNullOrWhiteSpace(Command))
        {
            foreach (var tok in SplitCommand(Command)) a.Add(tok);
        }
        return a;
    }

    /// <summary>把命令字符串按 shell 习惯切成 token（支持单/双引号）。</summary>
    public static List<string> SplitCommand(string s)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return res;
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        bool has = false;
        foreach (var c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                else cur.Append(c);
                has = true;
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; has = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (has) { res.Add(cur.ToString()); cur.Clear(); has = false; }
                continue;
            }
            cur.Append(c);
            has = true;
        }
        if (has) res.Add(cur.ToString());
        return res;
    }

    /// <summary>生成可读的命令行预览（用于 UI 展示与复制）。</summary>
    public string PreviewCommandLine()
    {
        var parts = new List<string> { "wslc", "run" };
        parts.AddRange(ToArgs());
        return string.Join(" ", parts.Select(QuoteIfNeeded));
    }

    public static string QuoteIfNeeded(string s)
        => string.IsNullOrEmpty(s) ? "\"\"" : (s.Contains(' ') ? $"\"{s}\"" : s);
}
