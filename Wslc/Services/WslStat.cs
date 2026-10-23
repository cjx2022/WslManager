using System;
using System.Globalization;

namespace WslManager.Wslc.Services;

/// <summary>一次虚拟机利用率采样结果。</summary>
public sealed class WslVmUsage
{
    /// <summary>本轮是否读到数据。</summary>
    public bool Ok;

    /// <summary>CPU 占用百分比（整台虚拟机、全部 vCPU 合计）。NaN = 还没有上一次采样，先不显示。</summary>
    public double CpuPercent = double.NaN;

    /// <summary>内存占用百分比。</summary>
    public double MemPercent;

    public double UsedMb;
    public double TotalMb;

    public static readonly WslVmUsage Empty = new() { Ok = false };
}

/// <summary>
/// WSL 虚拟机的 CPU / 内存利用率采样（v2.5）。
///
/// 为什么不再读 Vmmem / VmmemWSL 进程：
/// Windows 对这两个系统级进程做了保护 —— WorkingSet64 能读到，但 TotalProcessorTime
/// 访问会抛 AccessDenied（实测：ws=2260.6 MB 可读、cpu 抛异常）。v2.4 把整段逻辑包在
/// 一个 try 里，CPU 一抛连内存一起去掉，卡片正中于是什么都不显示。
///
/// 现在改为在发行版内部读 /proc，一次 exec 拿两项：
///   • /proc/stat 的 cpu 行 = 内核全局计数（所有发行版共享同一内核），两次采样做差 → VM CPU%
///   • MemTotal / MemAvailable → VM 内存占用
/// 开销约 40~80 ms，由 5 秒刷新循环在后台线程调用。
///
/// ⚠ 仍然是**虚拟机级**读数：多个发行版共享同一台 VM，Windows/WSL 都不提供按发行版的拆分。
/// </summary>
public static class WslStat
{
    private static readonly object Gate = new();
    private static double _lastBusy;
    private static double _lastTotal;
    private static DateTime _lastAt = DateTime.MinValue;

    /// <summary>
    /// 采样一次。<paramref name="distro"/> 传任一**正在运行**的发行版名 ——
    /// 不传 -d 的话 wsl.exe 会去启动默认发行版，等于偷偷开机。
    /// </summary>
    public static WslVmUsage Read(string distro)
    {
        if (string.IsNullOrWhiteSpace(distro)) return WslVmUsage.Empty;

        var exe = ProcRunner.ResolveExecutable("wsl.exe");
        if (exe == null) return WslVmUsage.Empty;

        var r = ProcRunner.Run(exe, new[]
        {
            "-d", distro, "--exec", "sh", "-c",
            "head -1 /proc/stat; grep -E '^(MemTotal|MemAvailable)' /proc/meminfo"
        }, 6000);

        if (!r.Success || string.IsNullOrWhiteSpace(r.StdOut)) return WslVmUsage.Empty;
        return Parse(r.StdOut);
    }

    private static WslVmUsage Parse(string text)
    {
        var u = new WslVmUsage();
        double? busy = null, totalTicks = null, memTotal = null, memAvail = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                // cpu  user nice system idle iowait irq softirq steal guest guest_nice
                var f = line.Substring(4).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 4) continue;

                double sum = 0;
                int n = Math.Min(f.Length, 8);          // guest/guest_nice 已含在 user/nice 里
                for (int i = 0; i < n; i++)
                    if (double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        sum += v;

                double idle = Num(f, 3) + Num(f, 4);     // idle + iowait 算空闲
                totalTicks = sum;
                busy = sum - idle;
            }
            else if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                memTotal = Kb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                memAvail = Kb(line);
        }

        if (memTotal.HasValue && memTotal.Value > 0 && memAvail.HasValue)
        {
            u.TotalMb = memTotal.Value;
            u.UsedMb = Math.Max(0, memTotal.Value - memAvail.Value);
            u.MemPercent = u.UsedMb / u.TotalMb * 100.0;
            u.Ok = true;
        }

        if (busy.HasValue && totalTicks.HasValue && totalTicks.Value > 0)
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (_lastAt != DateTime.MinValue && _lastTotal > 0)
                {
                    double dt = (now - _lastAt).TotalSeconds;
                    double dtick = totalTicks.Value - _lastTotal;
                    if (dt >= 0.5 && dtick > 0)
                        u.CpuPercent = Math.Clamp((busy.Value - _lastBusy) / dtick * 100.0, 0, 100);
                }
                _lastBusy = busy.Value;
                _lastTotal = totalTicks.Value;
                _lastAt = now;
            }
        }

        return u;
    }

    private static double Num(string[] f, int i)
        => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Kb(string line)
    {
        // "MemTotal:        8094512 kB"
        var s = line[(line.IndexOf(':') + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (s.Length == 0) return 0;
        return double.TryParse(s[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 1024.0 : 0;
    }
}
