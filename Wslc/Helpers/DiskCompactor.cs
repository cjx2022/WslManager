using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 虚拟磁盘压缩（回收 wslc 会话的 storage.vhdx 空间）。
///
/// 背景（2026-10-10 实测）：删除镜像后，即使 wslc 输出里有「已删除」行，
/// storage.vhdx 的文件大小也**纹丝不动**（拉取 163MB 的 nginx 再删掉，磁盘 +0.0 MB）。
/// 原因是动态 VHDX 只增不减，必须手动压缩。
///
/// 为什么不能用 `wsl --manage &lt;发行版&gt; --compact`：`wsl -l -v` 里**看不到 wslc 的会话**
/// （它不是发行版），该命令管不到这个 vhdx。
///
/// 官方/通用做法（微软文档与社区一致）：
///   1) wsl --shutdown                    —— vhdx 被占用时无法压缩
///   2) diskpart:
///        select vdisk file="&lt;vhdx&gt;"
///        attach vdisk readonly
///        compact vdisk
///        detach vdisk
///   ⚠ diskpart 的 vdisk 操作**必须管理员权限**，所以要提权（UAC 由用户确认）。
/// </summary>
public static class DiskCompactor
{
    /// <summary>
    /// 全新空会话虚拟磁盘自身的底座大小（MB）。实测 2026-10-10：重建后 0 镜像 0 容器，
    /// storage.vhdx 仍占 588 MB —— ext4 日志区/块分配表等格式化结构（对 1TB 虚拟容量）
    /// + wslc 引擎目录（挂载实测内部实际数据仅约 350KB）。这部分重建/压缩都消不掉，
    /// 重建能释放的是「当前占用 − 底座」。
    /// </summary>
    public const long EmptySessionBaseMB = 588;

    /// <summary>要压缩的 vhdx 路径；取不到返回 null。</summary>
    public static string FindStorageVhdx()
    {
        var dir = WslManager.Wslc.Services.WslcService.GetStorageDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        string best = null;
        long bestSize = -1;
        FileInfo storageFi = null;
        var candidates = new List<FileInfo>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.vhdx", SearchOption.AllDirectories))
        {
            try { candidates.Add(new FileInfo(f)); } catch { }
        }

        // storage.vhdx 优先（swap.vhdx 不需要压）。
        // ⚠ 之前的写法 `length + long.MaxValue` 当优先加分时整数溢出为负，
        //   反而让 36MB 的 swap 赢了 825MB 的 storage —— 已改成两段式选择。
        storageFi = candidates.FirstOrDefault(f =>
            f.Name.StartsWith("storage", StringComparison.OrdinalIgnoreCase));
        if (storageFi != null) return storageFi.FullName;

        foreach (var fi in candidates)
        {
            if (fi.Length > bestSize) { bestSize = fi.Length; best = fi.FullName; }
        }
        return best;
    }

    /// <summary>当前 vhdx 大小（字节）；-1 表示取不到。</summary>
    public static long GetVhdxBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    /// <summary>
    /// 提权运行 diskpart 压缩。会弹 UAC，由用户确认。
    /// 返回 (成功, 输出文本, 错误信息)。**调用前必须已执行 wsl --shutdown**。
    /// </summary>
    public static (bool Ok, string Output, string Error) Compact(string vhdxPath)
    {
        if (string.IsNullOrEmpty(vhdxPath) || !File.Exists(vhdxPath))
            return (false, "", "找不到虚拟磁盘文件：" + vhdxPath);

        var work = Path.Combine(Path.GetTempPath(), "wslc_compact");
        Directory.CreateDirectory(work);
        var script = Path.Combine(work, "compact.txt");
        var log = Path.Combine(work, "compact.log");

        // diskpart 脚本：注意路径里有中文（张萌），脚本文件必须 UTF-16（Unicode）编码，
        // 否则 diskpart 按 ANSI 读会乱码导致 select vdisk 失败。
        var sb = new StringBuilder();
        sb.AppendLine("select vdisk file=\"" + vhdxPath + "\"");
        sb.AppendLine("attach vdisk readonly");
        sb.AppendLine("compact vdisk");
        sb.AppendLine("detach vdisk");
        sb.AppendLine("exit");
        File.WriteAllText(script, sb.ToString(), Encoding.Unicode);

        try { File.Delete(log); } catch { }

        // 用 cmd 包装以拿到输出（提权进程的 stdout 无法直接重定向）
        var cmd = "/c chcp 65001 >nul & diskpart /s \"" + script + "\" > \"" + log + "\" 2>&1";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            Verb = "runas",              // ← UAC 弹窗
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            return (false, "", ex.Message.Contains("被取消") || ex.Message.Contains("canceled")
                ? "UAC 授权被取消"
                : "提权失败：" + ex.Message);
        }

        // 等待完成（最长 20 分钟；compact 大盘可能较慢）
        if (!p.WaitForExit(20 * 60 * 1000))
        {
            try { p.Kill(); } catch { }
            return (false, "", "压缩超时（20 分钟）");
        }

        var output = "";
        try { output = File.ReadAllText(log, Encoding.UTF8); } catch { }
        var (ok, why) = JudgeCompact(p.ExitCode, output);
        return (ok, output, ok ? "" : why);
    }

    /// <summary>
    /// 判定一次 diskpart compact 是否**真的**成功。
    ///
    /// 为什么不能只看「返回码 + 出现成功字样」：diskpart 脚本是顺序执行的，
    /// `compact vdisk` 失败**不会**中断脚本 —— 后面的 `detach vdisk` 照旧执行成功，
    /// 并在日志里写下「已成功分离虚拟磁盘文件」。于是
    /// <c>exit==0 &amp;&amp; 日志含"successfully" || 日志含"成功"</c>
    /// 这种写法会把 detach 的成功当成 compact 的成功（典型的假绿）。
    ///
    /// 现在的判据：先排除否定证据（出现任何错误字样即失败），
    /// 再要求成功证据出现在 `compact vdisk` 那一段之后。判不出来时**判失败**
    /// （宁可让用户看到原始日志，也不假报成功）。
    ///
    /// public 是为了让探针（第 17b 节）直接喂样本断言 —— 这个判定只能靠样本验，不能靠手点。
    /// </summary>
    public static (bool Ok, string Why) JudgeCompact(int exitCode, string output)
    {
        var text = output ?? "";
        if (text.Length == 0) return (false, "diskpart 没有产生任何输出（返回码 " + exitCode + "）");

        foreach (var bad in new[] { "错误", "失败", "error", "failed", "invalid", "denied" })
            if (text.Contains(bad, StringComparison.OrdinalIgnoreCase))
                return (false, "diskpart 输出中出现「" + bad + "」，压缩未成功");

        var i = text.IndexOf("compact vdisk", StringComparison.OrdinalIgnoreCase);
        var tail = i >= 0 ? text.Substring(i) : text;
        var saidOk = tail.Contains("成功", StringComparison.Ordinal) ||
                     tail.Contains("successfully", StringComparison.OrdinalIgnoreCase);

        if (!saidOk) return (false, "未在 diskpart 输出中找到压缩成功的证据（返回码 " + exitCode + "）");
        return exitCode == 0 ? (true, "") : (false, "diskpart 返回码 " + exitCode);
    }

    /// <summary>关闭 WSL（压缩前必须）。wslc 的会话也由它停掉。</summary>
    public static (bool Ok, string Output) ShutdownWsl()
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return (false, "找不到 wsl.exe");

        var psi = new ProcessStartInfo
        {
            FileName = wsl,
            Arguments = "--shutdown",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
        };
        try
        {
            using var p = Process.Start(psi);
            var outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode == 0, outText);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
