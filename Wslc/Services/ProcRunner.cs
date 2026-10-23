using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WslManager.Wslc.Services;

/// <summary>一次外部命令执行的结果。</summary>
public sealed class RunResult
{
    public int ExitCode { get; set; } = -1;
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public bool TimedOut { get; set; }
    /// <summary>被调用方主动取消（例如用户点「停止」中断拉取）。</summary>
    public bool Canceled { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int ElapsedMs { get; set; }

    public bool Success => ExitCode == 0 && !TimedOut && !Canceled;

    /// <summary>stdout + stderr 合并，便于展示与解析。</summary>
    public string All
    {
        get
        {
            var a = (StdOut ?? "").TrimEnd();
            var b = (StdErr ?? "").TrimEnd();
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            return a + "\n" + b;
        }
    }

    public string Display => All.Length == 0 ? "(无输出)" : All;
}

/// <summary>
/// 统一的进程调用封装。
/// 关键点：wslc.exe / wsl.exe 的输出编码不稳定（UTF-8 / UTF-16LE / 带 BOM），
/// 所以一律按原始字节读取后再自适应解码，避免中文乱码。
/// </summary>
public static class ProcRunner
{
    /// <summary>wslc.exe 的查找顺序：Program Files\WSL → PATH → WindowsApps 别名。</summary>
    public static string ResolveExecutable(string exeName)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p1 = Path.Combine(pf, "WSL", exeName);
        if (File.Exists(p1)) return p1;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var p2 = Path.Combine(local, "Microsoft", "WindowsApps", exeName);
        if (File.Exists(p2)) return p2;

        // PATH 探测
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var cand = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }

    public static Task<RunResult> RunAsync(string exePath, IEnumerable<string> args,
        int timeoutMs = 30000, CancellationToken ct = default,
        Action<string> onStdOutLine = null, Action<string> onStdErrLine = null,
        string workingDir = null, IDictionary<string, string> env = null)
    {
        return Task.Run(() => Run(exePath, args, timeoutMs, ct, onStdOutLine, onStdErrLine,
            workingDir, env), ct);
    }

    public static RunResult Run(string exePath, IEnumerable<string> args,
        int timeoutMs = 30000, CancellationToken ct = default,
        Action<string> onStdOutLine = null, Action<string> onStdErrLine = null,
        string workingDir = null, IDictionary<string, string> env = null)
    {
        var r = new RunResult();
        if (string.IsNullOrEmpty(exePath))
        {
            r.ErrorMessage = "可执行文件不存在";
            return r;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = null,
            StandardErrorEncoding = null,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var sw = Stopwatch.StartNew();
        Process p = new Process { StartInfo = psi };
        try
        {
            try
            {
                if (!p.Start())
                {
                    r.ErrorMessage = "进程启动失败";
                    return r;
                }
            }
            catch (Exception ex)
            {
                r.ErrorMessage = ex.Message;
                return r;
            }

            // 输入流立即关闭：wslc 在无 tty 时不会等待输入，避免 -i 阻塞
            try { p.StandardInput.Close(); } catch { }

            // 取消支持：之前在 UI 上点"停止"没法中断正在跑的命令，
            // 因为这里从未注册过 ct。注册后 Kill 会让下面的 WaitForExit 立刻返回。
            using var reg = ct.CanBeCanceled
                ? ct.Register(() =>
                {
                    try
                    {
                        r.Canceled = true;
                        if (!p.HasExited) p.Kill(true);
                    }
                    catch { }
                })
                : default;

            var outTask = ReadAllAsync(p.StandardOutput.BaseStream, onStdOutLine);
            var errTask = ReadAllAsync(p.StandardError.BaseStream, onStdErrLine);

            bool exited;
            if (timeoutMs <= 0)
            {
                p.WaitForExit();
                exited = true;
            }
            else
            {
                exited = p.WaitForExit(timeoutMs);
            }

            if (!exited)
            {
                r.TimedOut = true;
                try { p.Kill(true); } catch { }
                r.ErrorMessage = $"命令超时（{timeoutMs} ms）";
                try { p.WaitForExit(3000); } catch { }
            }

            var ob = outTask.GetAwaiter().GetResult();
            var eb = errTask.GetAwaiter().GetResult();
            r.StdOut = Decode(ob);
            r.StdErr = Decode(eb);
            try { r.ExitCode = p.ExitCode; } catch { r.ExitCode = r.TimedOut ? -2 : -1; }
        }
        catch (Exception ex)
        {
            r.ErrorMessage = ex.Message;
        }
        finally
        {
            sw.Stop();
            r.ElapsedMs = (int)sw.ElapsedMilliseconds;
            try { p.Dispose(); } catch { }
        }
        return r;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, Action<string> onLine)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        var pending = new List<byte>();

        // ⚠ UTF-16LE 的换行是「0A 00」两个字节。若只按 0x0A 切分，紧随其后的 0x00 会落到
        //   下一行开头，导致该行之后每一行都错位一个字节 → 整段解码全乱。
        //   这里把行尾的高字节 0x00 一并归入本行；跨缓冲区时用 pendingEndsWithLf 挂起等待确认。
        bool pendingEndsWithLf = false;

        int n;
        while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (onLine == null) continue;

            int i = 0;

            // 上一块以 0A 结尾：先结算那一行（若这块开头是 0x00，说明是 UTF-16LE 行尾）
            if (pendingEndsWithLf)
            {
                pendingEndsWithLf = false;
                if (buf[0] == 0) { pending.Add(0); i = 1; }
                FlushLine(pending, onLine);
            }

            for (; i < n; i++)
            {
                var b = buf[i];
                if (b == (byte)'\n')
                {
                    pending.Add(b);
                    if (i + 1 < n)
                    {
                        if (buf[i + 1] == 0) { pending.Add(0); i++; }   // UTF-16LE 行尾（0A 00）
                        FlushLine(pending, onLine);
                    }
                    else
                    {
                        pendingEndsWithLf = true;                      // 跨块，留给下一块确认
                    }
                }
                else if (pending.Count < 65536)
                {
                    pending.Add(b);
                }
            }
        }

        if (onLine != null && pending.Count > 0) FlushLine(pending, onLine);
        return ms.ToArray();
    }

    /// <summary>把 pending 中累积的一整行解码后回调，并清空缓冲。</summary>
    private static void FlushLine(List<byte> pending, Action<string> onLine)
    {
        if (pending.Count == 0) return;
        var lineBytes = pending.ToArray();
        pending.Clear();
        var s = Decode(lineBytes).TrimEnd('\r', '\n', '\0');
        if (s.Length > 0) { try { onLine(s); } catch { } }
    }

    /// <summary>
    /// 自适应解码：BOM → UTF-16；否则按 NUL 的奇偶分布判断是否为 UTF-16LE；
    /// 最后按 UTF-8 严格解码，失败再在 GBK / 宽松 UTF-8 之间用可读性打分选优。
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

        if ((bytes.Length & 1) == 0 && LooksLikeUtf16Le(bytes))
            return Encoding.Unicode.GetString(bytes);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch { }

        return DecodeFallback(bytes);
    }

    /// <summary>严格 UTF-8 解码失败时的兜底：GBK 与「宽松 UTF-8」二选一，取可读性高者。</summary>
    private static string DecodeFallback(byte[] bytes)
    {
        string gbk = null, loose = null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            gbk = Encoding.GetEncoding(936).GetString(bytes);
        }
        catch { }
        try { loose = new UTF8Encoding(false).GetString(bytes); } catch { }

        if (gbk == null) return loose ?? "";
        if (loose == null) return gbk;
        return Score(loose) > Score(gbk) ? loose : gbk;
    }

    /// <summary>文本可读性打分：控制字符 / 替换字符重罚，ASCII 与汉字加分。</summary>
    private static int Score(string s)
    {
        if (string.IsNullOrEmpty(s)) return int.MinValue;
        int score = 0;
        foreach (var ch in s)
        {
            if (ch == '\uFFFD') score -= 60;                       // 替换字符：解码失败的铁证
            else if (ch == '\r' || ch == '\n' || ch == '\t') score += 1;
            else if (ch < 0x20) score -= 40;                       // 其它控制字符
            else if (ch < 0x7F) score += 3;                        // ASCII 可打印
            else if (ch >= 0x4E00 && ch <= 0x9FFF) score += 6;     // 常用汉字
            else if (ch >= 0x3000 && ch <= 0x303F) score += 5;     // 中文标点
            else if (ch >= 0xFF00 && ch <= 0xFFEF) score += 5;     // 全角字符
            else if (ch >= 0x2000 && ch <= 0x206F) score += 2;     // 通用标点
            else if (ch >= 0xE000 && ch <= 0xF8FF) score -= 20;    // 私用区
            else score += 1;
        }
        return score;
    }

    /// <summary>
    /// 判断是否是无 BOM 的 UTF-16LE。
    /// 依据是 NUL 字节的**奇偶分布**：UTF-16LE 里 0x00 只会出现在高字节（奇数位）——
    /// ASCII 字符与 CR/LF 都会贡献；而 UTF-8 / GBK 的文本几乎不含 0x00。
    /// （旧实现要求「高字节几乎全为 0」，一旦出现中文就失效，这正是 wsl --update
    ///   中文提示乱码的根因：CJK 的高字节非 0，判据过不了 → 退化成 GBK。）
    /// </summary>
    private static bool LooksLikeUtf16Le(byte[] b)
    {
        if (b.Length < 2) return false;

        int zeros = 0, zerosAtOdd = 0;
        for (int i = 0; i < b.Length; i++)
            if (b[i] == 0) { zeros++; if ((i & 1) == 1) zerosAtOdd++; }

        return zeros >= 1 && zerosAtOdd >= zeros * 0.8;
    }
}
