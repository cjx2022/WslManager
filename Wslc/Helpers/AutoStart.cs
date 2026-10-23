using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 开机自启：把本程序挂到 <c>HKCU\...\CurrentVersion\Run</c>，并带 <c>--autostart</c> 启动；
/// 程序启动时读「上次运行中的容器名单」逐个拉起，这样 WSL 容器能跟着 Windows 一起就绪。
///
/// 为什么用 HKCU 的 Run 键而不是任务计划 / 启动文件夹：
///   · 不需要管理员（写 HKLM 或建计划任务都要提权，一个容器管理器不该要求这个）
///   · 值是透明可查的命令行，用户随时能在「任务管理器 → 启动」里看到并禁用
///   · 不落任何 exe / 快捷方式到用户目录，卸载程序即彻底消失
///
/// 名单快照单独存一个文本文件（与 settings.ini 同目录），**不写进 settings.ini**：
///   它是程序每次刷新都会覆盖的高频数据，混进人工编辑的配置里会把注释冲掉。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 注册表启动项的值名。用新品牌名（不是旧版 WSLCManager）—— 两者若同时存在，
    /// 「任务管理器 → 启动」里会看到两条，用户以为重复。
    /// </summary>
    private const string ValueName = "WslManager";

    /// <summary>自启启动时带的参数，程序据此走「恢复容器」分支。</summary>
    public const string AutoStartArgument = "--autostart";

    public static string RegistryKeyPath => RunKeyPath;
    public static string EntryName => ValueName;

    /// <summary>运行中容器名单快照的落盘位置。</summary>
    public static string SnapshotPath
    {
        get
        {
            var dir = Path.GetDirectoryName(AppSettings.Location);
            if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "autostart-containers.txt");
        }
    }

    // ── 启动期的「待恢复名单」──────────────────────────────────
    //
    // ⚠ 必须**在任何一次容器刷新之前**取走并存到内存：
    //   刷新结束会把「当前运行中的容器」写回名单文件，而开机那一刻必然一个都没跑
    //   （WSL 还没起来），于是上次关机留下的名单会被刷成空 —— 等恢复时再读就晚了。
    //   0.9.3 实测踩过：--autostart 跑完，一个容器都没被拉起。

    private static bool _pendingCaptured;

    /// <summary>本次进程是否由开机自启（--autostart）拉起。</summary>
    public static bool LaunchedByAutoStart { get; private set; }

    /// <summary>启动早期取走的待恢复名单（非自启启动时为空表）。</summary>
    public static List<string> PendingOnStartup { get; private set; } = new List<string>();

    /// <summary>程序一启动就调用一次（构造函数最合适）：锁定「本次是不是自启」并取走名单。</summary>
    public static void CapturePendingOnStartup()
    {
        if (_pendingCaptured) return;
        _pendingCaptured = true;

        try
        {
            LaunchedByAutoStart = Environment.GetCommandLineArgs()
                .Any(a => a.Equals(AutoStartArgument, StringComparison.OrdinalIgnoreCase));
        }
        catch { LaunchedByAutoStart = false; }

        PendingOnStartup = LaunchedByAutoStart ? LoadSnapshot() : new List<string>();
    }

    // ── 注册表侧 ──────────────────────────────────────────────

    /// <summary>写进注册表的命令行值。带引号，路径含空格也不会被拆断。</summary>
    public static string BuildCommandLine(string exePath)
    {
        var exe = (exePath ?? "").Trim();
        if (exe.Length == 0) return "";
        return "\"" + exe + "\" " + AutoStartArgument;
    }

    /// <summary>当前注册表里那条启动项的命令行；没有则空串。</summary>
    public static string CurrentCommandLine()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string ?? "";
        }
        catch { return ""; }
    }

    public static bool IsEnabled() => CurrentCommandLine().Trim().Length > 0;

    /// <summary>开启自启。用当前进程的 exe 路径写注册表（不是安装路径 —— 本工具是绿色单文件）。</summary>
    public static (bool Ok, string Message) Enable(string exePath)
    {
        try
        {
            var cmd = BuildCommandLine(exePath);
            if (cmd.Length == 0) return (false, "无法定位程序自身的路径");

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return (false, "打不开注册表启动项（HKCU\\...\\Run）");

            key.SetValue(ValueName, cmd, RegistryValueKind.String);
            return (true, cmd);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return (false, "打不开注册表启动项（HKCU\\...\\Run）");

            // throwOnMissingValue: false —— 本来就没有这项时，关掉也该算成功
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return (true, "已关闭开机自启");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── 快照侧（纯函数单独抽出，便于离线自检）──────────────────

    /// <summary>序列化名单：注释头 + 每行一个容器名。带时间戳只为日后排查，解析时一律忽略。</summary>
    public static string SerializeSnapshot(IEnumerable<string> names, DateTime stamp)
    {
        var sb = new StringBuilder();
        sb.Append("# WSL 管理器 —— 开机自启恢复名单\r\n");
        sb.Append("# 由程序自动维护：记录「最近一次已知处于运行中」的容器，启动时逐个 start。\r\n");
        sb.Append("# 请勿手工编辑；本文件每次刷新都会被覆盖。\r\n");
        sb.Append("# 时间：").Append(stamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");

        foreach (var n in CleanNames(names))
            sb.Append(n).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>解析名单：跳过空行与 # 注释，去重且保持顺序。</summary>
    public static List<string> ParseSnapshot(string text)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return res;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            res.Add(line);
        }
        return Dedup(res);
    }

    private static IEnumerable<string> CleanNames(IEnumerable<string> names)
        => Dedup((names ?? Enumerable.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0));

    private static List<string> Dedup(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<string>();
        foreach (var n in names)
            if (seen.Add(n)) res.Add(n);
        return res;
    }

    /// <summary>覆盖写入快照。名单为空也会写（头部注释在，表示「上次没有运行中的容器」）。</summary>
    public static bool SaveSnapshot(IEnumerable<string> names)
    {
        try
        {
            var path = SnapshotPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, SerializeSnapshot(names, DateTime.Now), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    public static List<string> LoadSnapshot()
    {
        try
        {
            var path = SnapshotPath;
            if (!File.Exists(path)) return new List<string>();
            return ParseSnapshot(File.ReadAllText(path, Encoding.UTF8));
        }
        catch { return new List<string>(); }
    }
}
