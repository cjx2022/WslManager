using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WslManager.Wslc.Models;

namespace WslManager.Wslc.Services;

/// <summary>
/// 容器的网络信息（详情页展示用）。
///
/// ⚠ 为什么不从 `wslc ps` 拿：没有端口映射时 ps 的「端口」列是空的，界面上看起来
///   像「没有地址」；实际上容器**一直有** bridge 网段的私有 IP，只是 Windows 侧不能直达。
///   所以 IP / 网关只能从 `wslc inspect` 的 NetworkSettings 里取。
/// </summary>
public sealed class ContainerNetInfo
{
    public bool Ok { get; set; }
    /// <summary>网络名（bridge，或用户自建的网络名）。</summary>
    public string NetworkName { get; set; } = "";
    /// <summary>容器在该网络里的 IPv4 地址。</summary>
    public string Ip { get; set; } = "";
    /// <summary>该网络的网关（WSL 侧宿主地址）。</summary>
    public string Gateway { get; set; } = "";
    /// <summary>端口映射文本（inspect 视角，形如 127.0.0.1:18080 -&gt; 80/tcp）。</summary>
    public string PortsText { get; set; } = "";
    /// <summary>解析失败时的原因（正常为空）。用于自检定位，不参与界面展示。</summary>
    public string ParseError { get; set; } = "";

    public bool HasIp => Ip.Length > 0;
}

/// <summary>
/// 容器的**真实运行态**（不是「命令退出码」）。
///
/// 为什么非要有它：`wslc container start` / `wslc run -d` 的 exit=0 只代表
/// **指令被接受**，不代表容器还在跑 —— 容器的主进程一结束它就停止，而命令照样返回 0。
/// 实测（2026-10-10，WSL 3.0.1.0）：一个 Cmd 为 `/bin/sh`、没加 `-i -t` 的容器
/// 被 start 后只活了 **2 毫秒**（StartedAt 19:03:45.630847 → FinishedAt 19:03:45.632935），
/// 而 `wslc container start` 依然 exit=0 并把容器名打印出来。
/// 所以「启动成功」这种结论**只能靠复查得出**，不能靠退出码 ——
/// 否则界面就会出现「提示成功、状态还是已停止」这种自相矛盾的反馈。
/// </summary>
public sealed class ContainerRuntimeState
{
    /// <summary>
    /// 是否真的问到了。false = wslc 不可用 / 输出没法解析 ——
    /// 调用侧必须据此**放弃判断**（宁可不提示，也不能误报「起后即退」）。
    /// </summary>
    public bool Queried { get; set; }

    public bool Running { get; set; }

    /// <summary>退出码（只在已退出时有意义；runtime 没给就是 null）。</summary>
    public int? ExitCode { get; set; }

    /// <summary>runtime 的原始状态串：running / exited / created …</summary>
    public string Status { get; set; } = "";

    /// <summary>容器的启动命令（Config.Cmd），用来给出「为什么立刻退出」的针对性解释。</summary>
    public string Cmd { get; set; } = "";

    /// <summary>解析失败原因（正常为空）。仅用于自检定位，不上界面。</summary>
    public string ParseError { get; set; } = "";
}

/// <summary>
/// wslc 业务封装。
/// CLI 语义与 docker 对齐，但子命令拼写可能随版本微调，因此调用侧做了回退。
/// </summary>
public static class WslcService
{
    public static string ExePath => ProcRunner.ResolveExecutable("wslc.exe");
    public static bool Available => ExePath != null;

    // ── 容器列表 ───────────────────────────────────────────────
    public static Task<RunResult> RawListContainersAsync(bool all, CancellationToken ct = default)
        => Task.Run(() => RawListContainers(all, ct), ct);

    private static RunResult RawListContainers(bool all, CancellationToken ct)
    {
        if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };

        // ① 优先 JSON：结构化、不受列宽/截断/本地化表头影响
        foreach (var baseArgs in new[]
                 {
                     new[] { "container", "ls" }, new[] { "container", "list" },
                     new[] { "container", "ps" }, new[] { "ps" }, new[] { "ls" }
                 })
        {
            var a = baseArgs.ToList();
            if (all) a.Add("-a");
            a.Add("--format"); a.Add("json");
            var rj = ProcRunner.Run(ExePath, a, 25000, ct);
            if (rj.Success) return rj;
        }

        // ② 回退：表格输出
        foreach (var v in new[]
                 {
                     new[] { "container", "ls" }, new[] { "container", "list" },
                     new[] { "container", "ps" }, new[] { "ps" }, new[] { "ls" }
                 })
        {
            var args = v.ToList();
            if (all) args.Add("-a");
            var r = ProcRunner.Run(ExePath, args, 25000, ct);
            if (r.Success && r.All.Trim().Length > 0) return r;
        }
        return new RunResult();
    }

    public static async Task<(List<ContainerInfo> Items, RunResult Raw)> ListContainersAsync(
        bool all, CancellationToken ct = default)
    {
        var raw = await RawListContainersAsync(all, ct);
        var items = new List<ContainerInfo>();
        foreach (var d in ParseContainerRows(raw.All))
        {
            var c = new ContainerInfo
            {
                // 表头可能中英混排（实测：容器列表为中文，镜像列表为英文），两种都要认
                Id = TableParser.Pick(d, "CONTAINER ID", "CONTAINERID", "容器 ID", "容器ID", "ID", "Id"),
                Image = TableParser.Pick(d, "IMAGE", "Image", "映像", "镜像"),
                Command = TableParser.Pick(d, "COMMAND", "Command", "命令"),
                Created = TableParser.Pick(d, "CREATED", "CREATEDAT", "Created", "已创建", "创建时间"),
                Status = TableParser.Pick(d, "STATUS", "Status", "STATE", "State", "状态"),
                Ports = TableParser.Pick(d, "PORTS", "Ports", "端口"),
                Name = TableParser.Pick(d, "NAMES", "NAME", "Name", "名称"),
                Networks = TableParser.Pick(d, "NETWORKS", "NETWORK", "Networks", "网络")
            };
            if (string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Id))
                c.Name = c.ShortId;
            if (string.IsNullOrWhiteSpace(c.Id) && string.IsNullOrWhiteSpace(c.Name)) continue;

            // State 字段比 Status 文本更权威；退出码只用来挑真正的异常（见 ContainerInfo.ClassifyExit）
            d.TryGetValue("state", out var rawState);
            c.ApplyStatus(rawState, c.Status);
            items.Add(c);
        }
        return (items, raw);
    }

    private static List<Dictionary<string, string>> ParseContainerRows(string text)
    {
        // 优先 JSON（wslc --format json 输出 NDJSON，字段名是 Docker 风格）
        var json = TableParser.ParseJsonLines(text);
        if (json.Count > 0) return json;

        var res = new List<Dictionary<string, string>>();
        var (header, rows) = TableParser.ParseAligned(text);
        var normHeader = header.Select(TableParser.Normalize).ToArray();
        foreach (var row in rows)
        {
            if (row.Length == 0) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < normHeader.Length; i++)
                d[normHeader[i]] = i < row.Length ? row[i] : "";
            res.Add(d);
        }
        return res;
    }

    // ── 镜像 ───────────────────────────────────────────────────
    public static Task<RunResult> RawListImagesAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var variants = new List<string[]>
            {
                new[] { "image", "ls" },
                new[] { "image", "list" },
                new[] { "images" },
            };

            // ① 优先 JSON
            foreach (var v in variants)
            {
                var a = v.ToList();
                a.Add("--format"); a.Add("json");
                var rj = ProcRunner.Run(ExePath, a, 25000, ct);
                if (rj.Success) return rj;
            }
            // ② 回退表格
            RunResult last = null;
            foreach (var v in variants)
            {
                var r = ProcRunner.Run(ExePath, v, 25000, ct);
                last = r;
                if (r.Success && r.All.Trim().Length > 0) return r;
            }
            return last ?? new RunResult();
        }, ct);

    public static async Task<(List<ImageInfo> Items, RunResult Raw)> ListImagesAsync(CancellationToken ct = default)
    {
        var raw = await RawListImagesAsync(ct);
        var items = new List<ImageInfo>();

        var json = TableParser.ParseJsonLines(raw.All);
        if (json.Count > 0)
        {
            foreach (var d in json)
            {
                var img = MapImage(d);
                if (string.IsNullOrWhiteSpace(img.Repository) && string.IsNullOrWhiteSpace(img.Id)) continue;
                items.Add(img);
            }
            return (items, raw);
        }

        var (header, rows) = TableParser.ParseAligned(raw.All);
        var normHeader = header.Select(TableParser.Normalize).ToArray();
        foreach (var row in rows)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < normHeader.Length; i++)
                d[normHeader[i]] = i < row.Length ? row[i] : "";
            var img = MapImage(d);
            if (string.IsNullOrWhiteSpace(img.Repository) && string.IsNullOrWhiteSpace(img.Id)) continue;
            items.Add(img);
        }
        return (items, raw);
    }

    private static ImageInfo MapImage(Dictionary<string, string> d) => new()
    {
        Repository = TableParser.Pick(d, "REPOSITORY", "Repository", "NAME", "IMAGE", "仓库", "映像", "镜像"),
        Tag = TableParser.Pick(d, "TAG", "Tag", "标签"),
        Id = TableParser.Pick(d, "IMAGE ID", "IMAGEID", "映像 ID", "映像ID", "镜像 ID", "镜像ID", "ID", "Id"),
        Size = TableParser.Pick(d, "SIZE", "Size", "大小"),
        // wslc 给的是 "2026-09-18 04:37:20 +0800 GMT+8"，列表里只需日期时间
        Created = TrimCreated(TableParser.Pick(d, "CREATED", "CREATEDAT", "Created", "已创建", "创建时间"))
    };

    private static string TrimCreated(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length > 19 ? s.Substring(0, 19).Trim() : s;
    }

    // ── 拉取镜像 ───────────────────────────────────────────────
    /// <summary>
    /// 归一化加速前缀：去掉 http(s):// 与结尾斜杠。
    /// ⚠ wslc 只认「纯 host[:port]」前缀，带 scheme 会直接报「映像无效」(E_INVALIDARG)。
    ///   检测（TestMirrorAsync / 界面预览）与拉取必须共用这一套归一化，否则会出现
    ///   「检测说可用、真拉取却失败」的假象。
    /// </summary>
    public static string NormalizeMirrorPrefix(string prefix)
    {
        var p = (prefix ?? "").Trim();
        if (p.Length == 0) return "";
        if (p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) p = p.Substring(8);
        else if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) p = p.Substring(7);
        return p.TrimEnd('/');
    }

    /// <summary>
    /// 镜像名是否已显式带 registry（形如 host[:port]/...）。
    /// ⚠ `nginx:latest` 里的冒号是 **tag 分隔符**、不是 registry 端口 —— 必须先切出第一个 '/'
    ///   之前的部分再判断，否则最常见的带 tag 写法会被误判为「已有 registry」而跳过加速前缀
    ///   （这是下载直连官方源、最终超时的另一个原因）。
    /// </summary>
    public static bool HasExplicitRegistry(string image)
    {
        var img = (image ?? "").Trim();
        int slash = img.IndexOf('/');
        if (slash <= 0) return false;                    // 无 '/'：第一段就是镜像名（可能带 tag）
        var first = img.Substring(0, slash);
        return first.Contains('.') || first.Contains(':') ||
               first.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否是 Docker Hub **官方镜像**（库命名空间下的镜像，如 alpine、nginx、redis）。
    ///
    /// ⚠ 这类镜像的**规范全名是 `library/&lt;名&gt;`**：`docker pull alpine` 之所以能成功，
    ///   是客户端替你补了 `library/`。一旦换成第三方加速前缀，**补不补要由我们决定** ——
    ///   而**部分镜像站对裸名直接返回 `denied`**（2026-10 多站实测），只有带 `library/`
    ///   才肯转发。这正是「检测说地址可用、直接点拉取却报 denied」的根因：
    ///   检测路径写的是 `&lt;host&gt;/library/alpine:latest`，而拉取路径此前拼的是 `&lt;host&gt;/alpine:latest`。
    ///
    /// 判据：既没有显式 registry、名字里也没有 '/'（`nginx:latest` 里的冒号是 tag，不算）。
    /// </summary>
    public static bool IsOfficialImage(string image)
    {
        var img = (image ?? "").Trim();
        if (HasExplicitRegistry(img)) return false;
        return img.IndexOf('/') < 0;
    }

    /// <summary>
    /// 组合出真正交给 `wslc pull` 的引用（界面预览也走这里，保证「所见即所执行」）。
    /// 官方镜像补 `library/`，与「检测加速地址可用性」走同一条路径 ——
    /// 两边写法必须一致，否则又会出现「检测说可用、拉取却失败」的假象。
    /// </summary>
    public static string ComposePullTarget(string image, string mirrorPrefix)
        => PullCandidates(image, mirrorPrefix)[0];

    /// <summary>
    /// 加速源下的候选目标，**按优先级排列**：
    ///   官方镜像 → [`&lt;前缀&gt;/library/&lt;名&gt;`（规范写法）, `&lt;前缀&gt;/&lt;名&gt;`（少数站点只认裸名）]
    ///   其他镜像 → [`&lt;前缀&gt;/&lt;名&gt;`]
    ///   已带 registry 或未配前缀 → 原样一个。
    /// 拉取逻辑逐个尝试，任何一种写法通了即算成功。
    /// </summary>
    public static IReadOnlyList<string> PullCandidates(string image, string mirrorPrefix)
    {
        var t = (image ?? "").Trim();
        var pre = NormalizeMirrorPrefix(mirrorPrefix);

        if (pre.Length == 0 || HasExplicitRegistry(t)) return new[] { t };

        if (IsOfficialImage(t) && !t.StartsWith("library/", StringComparison.OrdinalIgnoreCase))
            return new[] { $"{pre}/library/{t}", $"{pre}/{t}" };

        return new[] { $"{pre}/{t}" };
    }

    /// <summary>
    /// 拉取镜像。mirror 非空时先按 <see cref="PullCandidates"/> 逐个尝试加速地址
    /// （官方镜像先试规范的 `library/` 写法、再试裸名），全失败才回退官方名 ——
    /// 保证不依赖任何内置的第三方通道。
    /// </summary>
    public static Task<RunResult> PullAsync(string image, string mirrorPrefix,
        Action<string> onLine, int timeoutMs = 0, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var target = (image ?? "").Trim();
            var pre = NormalizeMirrorPrefix(mirrorPrefix);

            if (pre.Length > 0)
            {
                var candidates = PullCandidates(target, pre);
                if (!string.Equals(candidates[0], target, StringComparison.Ordinal))
                {
                    RunResult last = null;
                    foreach (var cand in candidates)
                    {
                        // 用户点了「停止」就彻底收手，不要再换写法试 —— 那不是失败
                        if (ct.IsCancellationRequested) return Stopped(last);

                        onLine?.Invoke($"[加速] wslc pull {cand}");
                        var rm = ProcRunner.Run(ExePath, new[] { "pull", cand }, timeoutMs, ct, onLine);
                        if (rm.Success) return rm;
                        if (rm.Canceled || ct.IsCancellationRequested) return Stopped(rm);

                        last = rm;
                        onLine?.Invoke($"   ← 退出码 {rm.ExitCode}，换下一种写法重试");
                    }

                    if (ct.IsCancellationRequested) return Stopped(last);

                    // 回退官方源：国内 registry-1.docker.io 是"连接挂起"而非快速失败，
                    // 若不设上限，用户会在"正在拉取"上干等几分钟 —— 给 90 秒封顶。
                    var fallbackTimeout = timeoutMs > 0 ? timeoutMs : 90000;
                    onLine?.Invoke($"[加速失败] 退出码 {last?.ExitCode}，回退官方源：{target}" +
                                   $"（国内通常不可达，最多等 {fallbackTimeout / 1000} 秒）");
                    var rf = ProcRunner.Run(ExePath, new[] { "pull", target }, fallbackTimeout, ct, onLine);
                    return rf.Canceled || ct.IsCancellationRequested ? Stopped(rf) : rf;
                }
            }

            var r = ProcRunner.Run(ExePath, new[] { "pull", target }, timeoutMs, ct, onLine);
            return r.Canceled || ct.IsCancellationRequested ? Stopped(r) : r;
        });

    /// <summary>把一次「被用户停止」包装成结果（复用已捕获的输出，便于界面展示）。</summary>
    private static RunResult Stopped(RunResult src)
    {
        var r = src ?? new RunResult();
        r.Canceled = true;
        return r;
    }

    // ── 运行容器 ───────────────────────────────────────────────
    public static Task<RunResult> RunAsync(ContainerSpec spec, Action<string> onLine,
        int timeoutMs = 0, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var args = new List<string> { "run" };
            args.AddRange(spec.ToArgs());
            onLine?.Invoke("> wslc " + string.Join(" ", args.Select(ContainerSpec.QuoteIfNeeded)));
            return ProcRunner.Run(ExePath, args, timeoutMs, ct, onLine);
        }, ct);

    // ── 生命周期 ───────────────────────────────────────────────
    public static Task<RunResult> ContainerActionAsync(string action, string container,
        CancellationToken ct = default, int timeoutMs = 60000)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            return ProcRunner.Run(ExePath, new[] { "container", action, container }, timeoutMs, ct);
        }, ct);

    public static Task<RunResult> StartAsync(string c, CancellationToken ct = default) => ContainerActionAsync("start", c, ct);

    /// <summary>
    /// 优雅停止：发 SIGTERM 并等待应用退出。
    /// ⚠ 实测默认要等 10 秒（应用不响应则超时强杀），这里给 8 秒 —— 既留出清理时间，
    /// 又不至于让用户干等。注意：无论优雅停止还是强制终止，最终退出码都是 137。
    /// </summary>
    public static Task<RunResult> StopAsync(string c, int timeoutSeconds = 8, CancellationToken ct = default)
        => Task.Run(() => ProcRunner.Run(ExePath,
            new[] { "container", "stop", "-t", timeoutSeconds.ToString(), c }, 120000, ct), ct);

    public static Task<RunResult> RestartAsync(string c, CancellationToken ct = default) => ContainerActionAsync("restart", c, ct);

    /// <summary>强制终止（wslc container kill）：立即 SIGKILL，实测 0.4 秒返回。注意：wslc 没有 pause/unpause。</summary>
    public static Task<RunResult> KillAsync(string c, CancellationToken ct = default) => ContainerActionAsync("kill", c, ct);

    public static Task<RunResult> RemoveAsync(string c, bool force, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var args = new List<string> { "container", "rm" };
            if (force) args.Add("-f");
            args.Add(c);
            return ProcRunner.Run(ExePath, args, 60000, ct);
        }, ct);

    public static Task<RunResult> RemoveImageAsync(string image, bool force, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var args = new List<string> { "image", "rm" };
            if (force) args.Add("-f");
            args.Add(image);
            return ProcRunner.Run(ExePath, args, 60000, ct);
        }, ct);

    // ── 备份 / 恢复 ─────────────────────────────────────────────

    /// <summary>
    /// 导出容器文件系统为 tar（wslc container export -o &lt;file&gt; &lt;container&gt;）。
    /// 运行中的容器也能导出。大容器耗时可能较长，timeout 给足。
    /// </summary>
    public static Task<RunResult> ExportContainerAsync(string container, string outputFile,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            return ProcRunner.Run(ExePath,
                new[] { "container", "export", "-o", outputFile, container },
                0, ct);   // 0 = 不限超时，导出大容器可能要几分钟
        }, ct);

    /// <summary>
    /// 导入 tar 为镜像（wslc image import &lt;file&gt; &lt;name&gt;）。
    /// 与 ExportContainerAsync 的产物配对使用。
    /// </summary>
    public static Task<RunResult> ImportImageAsync(string tarFile, string imageName,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var args = new List<string> { "image", "import", tarFile };
            if (!string.IsNullOrWhiteSpace(imageName)) args.Add(imageName);
            return ProcRunner.Run(ExePath, args, 0, ct);
        }, ct);

    // ── 镜像存档（导出 / 导入 tar）────────────────────────────────
    //
    // 与上面「备份 / 恢复」两条链**不是一回事**（名字像、产物完全不同）：
    //   · 这里：`image save -o` / `image load -i` —— 仓库格式的 tar，**保留标签**，
    //     导回来还是原镜像，可以直接 run。
    //   · 上面：`container export` / `image import` —— 导的是容器文件系统，**不保留标签**，
    //     导入后的镜像没有默认启动命令，新建容器时必须手填常驻命令。

    /// <summary>
    /// 导出镜像为 tar：`wslc image save -o &lt;路径&gt; &lt;镜像&gt;`。
    ///
    /// ⚠ 一次只导一个镜像。wslc 的 save 接受多个镜像名，但产物是**单个 tar**，
    ///   多镜像会让里面出现多个 manifest，再导入时行为不保证 —— 界面按「选中一个导出」设计。
    /// ⚠ 超时传 0（不超时）：镜像动辄几百 MB ~ 数 GB，固定超时会在慢盘上误杀。
    /// </summary>
    public static Task<RunResult> ImageSaveAsync(string image, string outputFile,
        CancellationToken ct = default, Action<string> onLine = null)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            if (string.IsNullOrWhiteSpace(image)) return new RunResult { ErrorMessage = "没有指定要导出的镜像" };
            if (string.IsNullOrWhiteSpace(outputFile)) return new RunResult { ErrorMessage = "没有指定输出文件" };

            return ProcRunner.Run(ExePath, BuildImageSaveArgs(image, outputFile), 0, ct, onLine, onLine);
        }, ct);

    /// <summary>导出参数拼装（抽成纯函数，好让探针断言，不必真跑一次几百 MB 的导出）。</summary>
    internal static List<string> BuildImageSaveArgs(string image, string outputFile)
        => new List<string> { "image", "save", "-o", (outputFile ?? "").Trim(), (image ?? "").Trim() };

    /// <summary>从 tar 导入镜像：`wslc image load -i &lt;路径&gt;`（超时理由同 save）。</summary>
    public static Task<RunResult> ImageLoadAsync(string tarFile,
        CancellationToken ct = default, Action<string> onLine = null)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            if (string.IsNullOrWhiteSpace(tarFile)) return new RunResult { ErrorMessage = "没有指定 tar 文件" };
            if (!File.Exists(tarFile)) return new RunResult { ErrorMessage = "文件不存在：" + tarFile };

            return ProcRunner.Run(ExePath, BuildImageLoadArgs(tarFile), 0, ct, onLine, onLine);
        }, ct);

    /// <summary>导入参数拼装（同 <see cref="BuildImageSaveArgs"/>，纯函数便于自检）。</summary>
    internal static List<string> BuildImageLoadArgs(string tarFile)
        => new List<string> { "image", "load", "-i", (tarFile ?? "").Trim() };

    /// <summary>
    /// 终止活动会话（wslc system session terminate）。
    /// ⚠ 实测 terminate **不接受位置参数**（传会话 ID 会报"未预期的位置参数"），
    /// 不带参数时终止默认会话；指定会话用全局选项 --session。
    /// ⚠ 终止会话后 WSL2 VM 仍可能挂着 vhdx，删除会话目录前必须再执行 wsl --shutdown。
    /// </summary>
    public static Task<RunResult> TerminateSessionAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            return ProcRunner.Run(ExePath, new[] { "system", "session", "terminate" }, 60000, ct);
        }, ct);

    /// <summary>列出会话（wslc system session list，表格输出）。</summary>
    public static RunResult ListSessions(int timeoutMs = 30000)
    {
        if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
        return ProcRunner.Run(ExePath, new[] { "system", "session", "list" }, timeoutMs);
    }

    /// <summary>
    /// 导出容器文件系统为 tar，并随附配置元数据（inspect 的 JSON 原文）。
    /// tar 只有文件系统（容器名/命令/端口/环境变量都不在里面），恢复时需要这份
    /// 元数据才能按原配置重建容器 —— 没有它就只能恢复成镜像、让用户手动填命令。
    /// 元数据写到 &lt;tarPath&gt;.json。
    /// </summary>
    public static async Task<RunResult> ExportContainerWithMetaAsync(string container,
        string outputFile, CancellationToken ct = default)
    {
        var tar = await ExportContainerAsync(container, outputFile, ct);
        if (!tar.Success) return tar;

        try
        {
            var ins = await InspectAsync(container, ct);
            var txt = ins.All ?? "";
            var s = txt.IndexOf('{');
            var e = txt.LastIndexOf('}');
            if (s >= 0 && e > s)
            {
                var json = txt.Substring(s, e - s + 1);
                // 验证是合法 JSON 再落盘，避免把表格输出误存进去
                System.Text.Json.JsonDocument.Parse(json);
                File.WriteAllText(outputFile + ".json", json, new System.Text.UTF8Encoding(false));
            }
        }
        catch { /* inspect/写元数据失败不阻塞导出本身，恢复时按无元数据处理 */ }

        return tar;
    }

    // ── 镜像占用对账 / 删除结果解读 ───────────────────────────────

    /// <summary>
    /// `wslc image rm` 的真实结果。**exit=0 不等于删掉了**，必须看输出里有没有「已删除」行。
    ///
    /// 实测（2026-10-10，WSL 3.0.1.0）：
    ///   A. 没有任何容器引用：
    ///        已取消标记: &lt;tag&gt;
    ///        已取消标记: &lt;digest&gt;
    ///        已删除: sha256:aaef90e0…        ← 镜像层真的删了（重拉要从网络下 47 秒）
    ///   B. 有容器（运行中**或**已停止）引用：
    ///        已取消标记: &lt;tag&gt;
    ///        已取消标记: &lt;digest&gt;            ← 就这两行，exit=0，没有任何报错
    ///      镜像层不删、空间不释放；镜像从 image ls 消失（看着像成功了），
    ///      重拉只要 0.7 秒（说明层还在本地）。
    /// </summary>
    public enum ImageRemoveOutcome
    {
        /// <summary>镜像层被真正删除，空间回收。</summary>
        Deleted,
        /// <summary>只摘掉了标签；镜像层仍在（被容器按住），空间未回收。</summary>
        UntaggedOnly,
        /// <summary>命令失败（如镜像不存在）。</summary>
        Failed
    }

    /// <summary>解读 `wslc image rm` 的输出（纯函数，便于离线自检）。</summary>
    public static ImageRemoveOutcome InterpretImageRemove(bool success, string output)
    {
        if (!success) return ImageRemoveOutcome.Failed;
        var t = output ?? "";
        if (t.IndexOf("已删除", StringComparison.Ordinal) >= 0 ||
            t.IndexOf("Deleted", StringComparison.OrdinalIgnoreCase) >= 0)
            return ImageRemoveOutcome.Deleted;
        if (t.IndexOf("取消标记", StringComparison.Ordinal) >= 0 ||
            t.IndexOf("Untagged", StringComparison.OrdinalIgnoreCase) >= 0)
            return ImageRemoveOutcome.UntaggedOnly;
        // exit=0 且没有任何可识别关键词 → 引擎接受了请求，按已删除处理（输出会原样打进日志）
        return ImageRemoveOutcome.Deleted;
    }

    /// <summary>
    /// 把「容器清单」对账到「镜像清单」上，填出每个镜像被谁占用。
    ///
    /// 纯函数（输入两份清单、输出无副作用），便于用夹具离线自检。
    /// 匹配要放宽：容器可能按 tag 全名引用、也可能只写仓库名 / 短 ID；
    /// 表格视图（无 JSON 时的回退）还会把镜像名截断成 "registry.example.com/libr…"。
    /// </summary>
    public static void AttachImageUsage(IEnumerable<ImageInfo> images, IEnumerable<ContainerInfo> containers)
    {
        if (images == null) return;
        var list = images as IList<ImageInfo> ?? images.ToList();
        foreach (var img in list) img.Usage = new ImageUsage();
        if (containers == null) return;

        var cs = containers as IList<ContainerInfo> ?? containers.ToList();
        foreach (var c in cs)
        {
            var used = c.Image ?? "";
            if (used.Trim().Length == 0) continue;
            foreach (var img in list)
            {
                if (!MatchesImage(used, img)) continue;
                img.Usage.Add(string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name, c.IsRunning);
            }
        }
    }

    /// <summary>容器上记的镜像引用是否指向这个镜像条目。</summary>
    private static bool MatchesImage(string usedByContainer, ImageInfo img)
    {
        var a = usedByContainer.Trim();
        var b = (img.Reference ?? "").Trim();
        if (b.Length == 0) return false;

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // 仓库名（不带 tag）
        var repo = (img.Repository ?? "").Trim();
        if (repo.Length > 0 && string.Equals(a, repo, StringComparison.OrdinalIgnoreCase)) return true;

        // 镜像 ID（完整或短）：容器可以用 ID 建
        var id = (img.Id ?? "").Trim();
        if (id.Length >= 12 && a.Length >= 12 &&
            (id.StartsWith(a, StringComparison.OrdinalIgnoreCase) ||
             a.StartsWith(id, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 表格回退路径下 wslc 会把长名字截断成 "xxx…"，做前缀匹配
        var ta = TrimEllipsis(a);
        var tb = TrimEllipsis(b);
        if (ta.Length >= 4 && tb.Length >= 4 &&
            (tb.StartsWith(ta, StringComparison.OrdinalIgnoreCase) ||
             ta.StartsWith(tb, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static string TrimEllipsis(string s)
    {
        int i = s.IndexOf('…');
        return (i >= 0 ? s.Substring(0, i) : s).TrimEnd('.', ' ');
    }

    // ── 日志 / 详情 / 统计 ─────────────────────────────────────
    public static Task<RunResult> LogsAsync(string c, int tail, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var args = new List<string> { "container", "logs" };
            if (tail > 0) { args.Add("--tail"); args.Add(tail.ToString()); }
            args.Add(c);
            return ProcRunner.Run(ExePath, args, 30000, ct);
        }, ct);

    public static Task<RunResult> InspectAsync(string c, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var variants = new[] { new[] { "container", "inspect", c }, new[] { "inspect", c } };
            RunResult last = null;
            foreach (var v in variants)
            {
                var r = ProcRunner.Run(ExePath, v, 30000, ct);
                last = r;
                if (r.Success && r.All.Trim().Length > 0) return r;
            }
            return last ?? new RunResult();
        }, ct);

    // ── 容器网络详情（IP / 网关）─────────────────────────────────
    /// <summary>
    /// 取容器的网络信息（IP / 网关 / 网络名 / 端口映射）。
    ///
    /// ⚠ 实测（WSL 3.0.1.0）：`wslc inspect -f` **只认 `json`**，不支持 Go 模板 ——
    ///   传 `{{.NetworkSettings.IPAddress}}` 会报「不是可识别的格式类型」。
    ///   所以这里取 `-f json` 的**单行 JSON 数组**自行解析（缩进 JSON 也能解析，作为回退）。
    /// </summary>
    public static Task<ContainerNetInfo> InspectNetworkAsync(string container, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available || string.IsNullOrWhiteSpace(container)) return new ContainerNetInfo();

            string text = null;
            foreach (var args in new[]
                     {
                         new[] { "inspect", "-f", "json", container },
                         new[] { "container", "inspect", "-f", "json", container },
                         new[] { "inspect", container },   // 回退：缩进 JSON 同样能解析
                     })
            {
                var r = ProcRunner.Run(ExePath, args, 30000, ct);
                if (r.Success && r.All.Trim().Length > 0) { text = r.All; break; }
            }
            return string.IsNullOrWhiteSpace(text) ? new ContainerNetInfo() : ParseNetInfo(text);
        }, ct);

    /// <summary>从 `wslc inspect` 的 JSON 里解析网络信息（独立方法，便于离线自检）。</summary>
    internal static ContainerNetInfo ParseNetInfo(string text)
    {
        var info = new ContainerNetInfo();
        var t = (text ?? "").Trim();
        if (t.Length == 0) return info;

        // 取出 JSON 片段：从第一个 '[' 或 '{'（取更靠前者）截到与之配对的结尾 ']' / '}'。
        //
        // ⚠ 不能只从第一个 '{' 开始截 —— 那会把数组开头的 '[' 切掉，留下一个孤立的 ']'，
        //   JsonDocument 直接报 “']' is invalid after a single JSON value”（实测踩过）。
        //   而 `RunResult.All` 是 stdout + stderr 拼起来的，尾部可能还挂着错误行，
        //   所以要用 LastIndexOf 把尾巴一起甩掉。
        int start = FirstIndexOf(t, '[', '{');
        if (start < 0) return info;
        int end = t.LastIndexOf(t[start] == '[' ? ']' : '}');
        if (end < start) return info;

        try
        {
            using var doc = JsonDocument.Parse(t.Substring(start, end - start + 1));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return info;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return info;

            FillNetInfo(root, info);
            info.Ok = info.Ip.Length > 0 || info.NetworkName.Length > 0;
        }
        catch (Exception ex)
        {
            info.ParseError = ex.GetType().Name + ": " + ex.Message;
        }
        return info;
    }

    private static void FillNetInfo(JsonElement root, ContainerNetInfo info)
    {
        if (root.TryGetProperty("HostConfig", out var hc) &&
            hc.TryGetProperty("NetworkMode", out var nm) && nm.ValueKind == JsonValueKind.String)
            info.NetworkName = nm.GetString() ?? "";

        if (root.TryGetProperty("NetworkSettings", out var ns) &&
            ns.TryGetProperty("Networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
        {
            foreach (var net in nets.EnumerateObject())
            {
                if (info.NetworkName.Length == 0) info.NetworkName = net.Name;
                if (net.Value.ValueKind != JsonValueKind.Object) continue;

                var ip = JsonStr(net.Value, "IPAddress");
                if (ip.Length == 0) continue;
                info.Ip = ip;
                info.Gateway = JsonStr(net.Value, "Gateway");
                break;
            }
        }

        if (root.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Object)
        {
            var list = new List<string>();
            foreach (var p in ports.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in p.Value.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object) continue;
                    var host = JsonStr(b, "HostIp");
                    var port = JsonStr(b, "HostPort");
                    if (port.Length > 0) list.Add($"{host}:{port} -> {p.Name}");
                }
            }
            info.PortsText = string.Join("，", list);
        }
    }

    /// <summary>返回 a、b 中先出现的那一个的下标（都不存在返回 -1）。</summary>
    private static int FirstIndexOf(string s, char a, char b)
    {
        int ia = s.IndexOf(a), ib = s.IndexOf(b);
        if (ia < 0) return ib;
        if (ib < 0) return ia;
        return Math.Min(ia, ib);
    }

    // ── 容器真实运行态（复查「启动成功」到底成没成）──────────────────
    /// <summary>
    /// 查容器的真实运行态。回退链与 <see cref="InspectNetworkAsync"/> 一致 ——
    /// `wslc inspect -f` **只认 `json`**，不支持 Go 模板。
    /// </summary>
    public static Task<ContainerRuntimeState> InspectStateAsync(string container, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available || string.IsNullOrWhiteSpace(container)) return new ContainerRuntimeState();

            string text = null;
            foreach (var args in new[]
                     {
                         new[] { "inspect", "-f", "json", container },
                         new[] { "container", "inspect", "-f", "json", container },
                         new[] { "inspect", container },   // 回退：缩进 JSON 同样能解析
                     })
            {
                var r = ProcRunner.Run(ExePath, args, 30000, ct);
                if (r.Success && r.All.Trim().Length > 0) { text = r.All; break; }
            }
            return ParseStateInfo(text);
        }, ct);

    /// <summary>
    /// 从 `wslc inspect` 的 JSON 里解析运行态（独立方法，便于离线自检）。
    /// 截取片段的方式与 <see cref="ParseNetInfo"/> 相同：**必须从第一个 `[` 或 `{` 里更靠前的那个开始**，
    /// 只从 `{` 截会留下孤立的 `]`（踩过）。
    /// </summary>
    internal static ContainerRuntimeState ParseStateInfo(string text)
    {
        var st = new ContainerRuntimeState();
        var t = (text ?? "").Trim();
        if (t.Length == 0) return st;

        int start = FirstIndexOf(t, '[', '{');
        if (start < 0) return st;
        int end = t.LastIndexOf(t[start] == '[' ? ']' : '}');
        if (end < start) return st;

        try
        {
            using var doc = JsonDocument.Parse(t.Substring(start, end - start + 1));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return st;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return st;

            if (root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                // Running 必须是**真布尔**才算问到了；字符串 "false" 不当布尔用。
                if (s.TryGetProperty("Running", out var run) &&
                    (run.ValueKind == JsonValueKind.True || run.ValueKind == JsonValueKind.False))
                {
                    st.Running = run.GetBoolean();
                    st.Queried = true;
                }

                if (s.TryGetProperty("ExitCode", out var ec) && ec.ValueKind == JsonValueKind.Number &&
                    ec.TryGetInt32(out var code))
                    st.ExitCode = code;

                st.Status = JsonStr(s, "Status");
            }

            if (root.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Object &&
                cfg.TryGetProperty("Cmd", out var cmd))
            {
                if (cmd.ValueKind == JsonValueKind.Array)
                    st.Cmd = string.Join(" ", cmd.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()));
                else if (cmd.ValueKind == JsonValueKind.String)
                    st.Cmd = cmd.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            st.ParseError = ex.GetType().Name + ": " + ex.Message;
        }
        return st;
    }

    /// <summary>
    /// `wslc run -d` 的输出里是容器 ID（实测：64 位十六进制，单独占一行）。
    /// 用户没给 `--name` 时用它来复查新容器到底跑起来没有。
    /// 取「最后一行看起来像 ID 的输出」，避免被前面的告警行干扰。
    /// </summary>
    internal static string ParseCreatedId(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        string found = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().Trim('\r');
            if (line.Length < 12 || line.Length > 64) continue;
            if (!line.All(Uri.IsHexDigit)) continue;
            found = line;
        }
        return found;
    }

    /// <summary>
    /// 命令是不是「需要终端才活得下去」的交互式 shell。
    /// 用于把「起后即退」解释得具体一点，而不是丢一句「容器退出了」。
    /// </summary>
    internal static bool LooksLikeInteractiveShell(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var first = cmd.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                       .FirstOrDefault() ?? "";
        var leaf = first.Contains('/') ? first.Substring(first.LastIndexOf('/') + 1) : first;
        return ShellCandidates.Contains(leaf, StringComparer.OrdinalIgnoreCase);
    }

    private static string JsonStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    // ── 终端 shell 探测 ────────────────────────────────────────
    /// <summary>候选 shell，**顺序即优先级**（bash 优于 sh，busybox 最低）。</summary>
    public static readonly string[] ShellCandidates =
        { "bash", "sh", "ash", "zsh", "dash", "fish", "busybox" };

    /// <summary>
    /// 探测容器内可用的 shell —— 不同镜像差别很大（alpine 只有 busybox 的 sh，
    /// debian/nginx 有 bash + dash，某些极小镜像干脆没有 shell）。
    ///
    /// 做法：`exec <容器> sh -c 'for s in …; do command -v $s && echo $s; done'`；
    /// 容器连 sh 都没有时，换 bash 再试一次；都失败就返回空表 ——
    /// 调用侧必须留「自定义」入口兜底，别把用户堵死。
    /// </summary>
    public static Task<List<string>> DetectShellsAsync(string container, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var found = new List<string>();
            if (!Available || string.IsNullOrWhiteSpace(container)) return found;

            // 结尾补 `: `（shell 内建真值命令）：循环体是 `command -v $s && echo $s`，
            // 最后一个候选不存在时整条命令的退出码会是 1，补一个真正的空语句让退出码稳定为 0。
            var probe = "for s in " + string.Join(" ", ShellCandidates) +
                        "; do command -v $s >/dev/null 2>&1 && echo $s; done; :";
            var args = new[] { "exec", container, "", "-c", probe };

            foreach (var shell in new[] { "sh", "bash" })
            {
                args[2] = shell;
                var r = ProcRunner.Run(ExePath, args, 20000, ct);

                // ⚠ 判据只看「输出里有没有候选 shell」，**不能**要求 exit=0 ——
                //   壳内退出码会随最后一个候选是否存在而变，容器里没有 busybox 时就是 1，
                //   但输出（可用 shell 清单）完全正常。（这里踩过一个假失败。）
                foreach (var raw in TableParser.Lines(r.All))
                {
                    var s = raw.Trim();
                    if (s.Length == 0) continue;
                    if (!ShellCandidates.Contains(s, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!found.Contains(s, StringComparer.OrdinalIgnoreCase)) found.Add(s);
                }
                if (found.Count > 0) break;   // 探测成功就不再用 bash 重试
            }

            // 按候选表优先级排序（exec 回显顺序不保证）
            found.Sort((a, b) => ShellRank(a).CompareTo(ShellRank(b)));
            return found;
        }, ct);

    private static int ShellRank(string s)
    {
        for (int i = 0; i < ShellCandidates.Length; i++)
            if (string.Equals(ShellCandidates[i], s, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>
    /// 单次性能采样。
    /// 实测：wslc stats 本身就是「快照」语义（输出一次即退出），没有 --no-stream 选项；
    /// 因此直接调用即可，并优先取 JSON 输出。
    /// </summary>
    public static Task<(List<ContainerStats> Items, RunResult Raw)> StatsOnceAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return (new List<ContainerStats>(), new RunResult { ErrorMessage = "未找到 wslc.exe" });

            RunResult r = ProcRunner.Run(ExePath, new[] { "stats", "--format", "json" }, 40000, ct);
            if (!r.Success)
                r = ProcRunner.Run(ExePath, new[] { "stats" }, 40000, ct);

            var items = new List<ContainerStats>();

            var json = TableParser.ParseJsonLines(r.All);
            if (json.Count > 0)
            {
                foreach (var d in json) items.Add(MapStats(d));
                return (items, r);
            }

            var (header, rows) = TableParser.ParseAligned(r.All);
            var nh = header.Select(TableParser.Normalize).ToArray();
            foreach (var row in rows)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < nh.Length; i++) d[nh[i]] = i < row.Length ? row[i] : "";
                var s = MapStats(d);
                if (string.IsNullOrWhiteSpace(s.Name) && string.IsNullOrWhiteSpace(s.Cpu)) continue;
                items.Add(s);
            }
            return (items, r);
        }, ct);

    /// <summary>采集单个容器的性能快照。</summary>
    public static Task<(ContainerStats Item, RunResult Raw)> StatsForAsync(
        string container, CancellationToken ct = default)
        => Task.Run<(ContainerStats, RunResult)>(() =>
        {
            if (!Available) return (null, new RunResult { ErrorMessage = "未找到 wslc.exe" });

            var r = ProcRunner.Run(ExePath, new[] { "stats", container, "--format", "json" }, 40000, ct);
            if (!r.Success)
                r = ProcRunner.Run(ExePath, new[] { "stats", container }, 40000, ct);

            var json = TableParser.ParseJsonLines(r.All);
            if (json.Count > 0) return (MapStats(json[0]), r);

            var (header, rows) = TableParser.ParseAligned(r.All);
            if (rows.Count > 0)
            {
                var nh = header.Select(TableParser.Normalize).ToArray();
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < nh.Length; i++) d[nh[i]] = i < rows[0].Length ? rows[0][i] : "";
                return (MapStats(d), r);
            }
            return (null, r);
        }, ct);

    private static ContainerStats MapStats(Dictionary<string, string> d) => new()
    {
        Name = TableParser.Pick(d, "名称", "NAME", "CONTAINER", "容器 ID", "容器ID", "CONTAINERID", "ID"),
        Cpu = TableParser.Pick(d, "CPU 百分比", "CPU百分比", "CPU %", "CPUPERC", "CPU"),
        Mem = TableParser.Pick(d, "最大用量/限制", "最大用量限制", "内存用量/上限", "MEM USAGE / LIMIT", "MEMUSAGELIMIT", "MEM USAGE", "MEMUSAGE"),
        MemPercent = TableParser.Pick(d, "内存百分比", "MEM %", "MEMPERC", "MEMPERCENT"),
        Net = TableParser.Pick(d, "网络 I/O", "网络IO", "NET I/O", "NETIO"),
        Block = TableParser.Pick(d, "块 I/O", "块IO", "BLOCK I/O", "BLOCKIO"),
        Pids = TableParser.Pick(d, "PIDS", "PIDSCOUNT", "进程数")
    };

    /// <summary>
    /// 实测某个镜像加速前缀是否可用（真的拉一次极小的 alpine 试探）。
    /// 判断依据是退出码，比"能不能访问首页"可靠得多 ——
    /// 「首页打得开」只说明 Web 服务在，不代表 registry 端能转发镜像。
    ///
    /// ⚠ 副作用：这会把 `alpine:latest` 真的拉进本地镜像列表 —— 调用侧应提示用户。
    /// </summary>
    public static Task<bool> TestMirrorAsync(string prefix, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return false;
            if (NormalizeMirrorPrefix(prefix).Length == 0) return false;

            // 探测目标 = 拉取主路径的**全部候选**（alpine:latest 是官方镜像 →
            // 先试 `&lt;前缀&gt;/library/alpine:latest`，再退裸名）。
            // ⚠ 一律走 PullCandidates，**不许写死字符串**：一旦探测与拉取各写一份，
            //   就会重现「检测说可用、直接拉取却 denied」的假象（2026-10-10 实测踩过）。
            foreach (var probe in PullCandidates("alpine:latest", prefix))
            {
                if (ProcRunner.Run(ExePath, new[] { "pull", probe }, 60000, ct).Success)
                    return true;
            }
            return false;
        }, ct);

    /// <summary>打开 wslc 用户设置文件。</summary>
    public static void OpenSettingsFile()
    {
        if (!Available) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "settings",
            UseShellExecute = true
        });
    }

    /// <summary>
    /// wslc 的数据目录（镜像 / 容器 / 设置存放处）。
    /// 优先从 `wslc info` 的「设置文件:」行解析，失败则回退 %LOCALAPPDATA%\wslc。
    /// </summary>
    /// <summary>
    /// 取 wslc 会话数据目录下虚拟磁盘的总大小（字节）。
    /// 用途：`wslc pull` 在非终端环境下**完全不输出进度**（实测 stdout / stderr 均为 0 字节，
    /// 进度条只在真实 tty 里渲染），所以只能靠 vhdx 的增长量来估算下载速度。返回 -1 表示取不到。
    /// </summary>
    public static long GetStorageBytes()
    {
        try
        {
            var dir = GetStorageDir();
            if (!Directory.Exists(dir)) return -1;

            long total = 0;
            bool any = false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.vhdx", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; any = true; } catch { }
            }
            return any ? total : -1;
        }
        catch { return -1; }
    }

    private static string _storageDirCache;

    public static string GetStorageDir()
    {
        // 缓存：内部会同步调用 `wslc info`，绝不能每次采样都跑一遍（UI 线程会卡死）
        if (_storageDirCache != null) return _storageDirCache;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslc");

        try
        {
            if (!Available) return fallback;
            var r = ProcRunner.Run(ExePath, new[] { "info" }, 20000);
            foreach (var raw in TableParser.Lines(r.All))
            {
                var line = raw.Trim();
                if (line.StartsWith("设置文件") || line.StartsWith("Settings file", StringComparison.OrdinalIgnoreCase))
                {
                    var i = line.IndexOf(':');
                    if (i < 0) i = line.IndexOf('：');
                    if (i >= 0)
                    {
                        var p = line.Substring(i + 1).Trim().Trim('"');
                        var dir = Path.GetDirectoryName(p);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            _storageDirCache = dir;
                            return dir;
                        }
                    }
                }
            }
        }
        catch { }

        _storageDirCache = fallback;
        return fallback;
    }

    // ══════════════════ 网络管理（wslc network） ══════════════════
    //
    // 2026-10-13 实测（WSL 3.0.1.0）：
    //   `wslc network create -d bridge --subnet 172.30.0.0/24 --gateway 172.30.0.1 mynet` → exit=0，
    //   随后 `network inspect mynet -f json` 里 IPAM.Config[0].Subnet/Gateway 与传入值一致。
    //   —— 网段**是可指定的**。（0.9.1 之前删掉网络模块的理由是「网段不可改」，那条结论不再成立。）
    //
    // 两个数据源分工，缺一不可：
    //   · `network ls --format json`  → NDJSON，只有 ID / 名称 / 驱动 / 范围
    //   · `network inspect <名...> -f json` → **一个 JSON 数组**，含 IPAM 网段与 Containers
    // 所以列表必须「先 ls 出名单，再一次性 inspect 补齐详情」。
    // 顺带一条：三个内置网络（bridge / host / none）的**ID 每次会话重建都会变**，别当稳定主键。

    public static Task<RunResult> RawListNetworksAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var variants = new List<string[]>
            {
                new[] { "network", "ls" },
                new[] { "network", "list" },
            };

            // ① 优先 JSON
            foreach (var v in variants)
            {
                var a = v.ToList();
                a.Add("--format"); a.Add("json");
                var rj = ProcRunner.Run(ExePath, a, 25000, ct);
                if (rj.Success && rj.All.Trim().Length > 0) return rj;
            }
            // ② 回退表格
            RunResult last = null;
            foreach (var v in variants)
            {
                var r = ProcRunner.Run(ExePath, v, 25000, ct);
                last = r;
                if (r.Success && r.All.Trim().Length > 0) return r;
            }
            return last ?? new RunResult();
        }, ct);

    /// <summary>批量 inspect 网络（一次可传多个名字，实测返回 JSON 数组）。</summary>
    public static Task<RunResult> RawInspectNetworksAsync(IEnumerable<string> names,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var list = (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
            if (list.Count == 0) return new RunResult();

            foreach (var withFormat in new[] { true, false })
            {
                var a = new List<string> { "network", "inspect" };
                a.AddRange(list);
                if (withFormat) { a.Add("-f"); a.Add("json"); }
                var r = ProcRunner.Run(ExePath, a, 30000, ct);
                if (r.Success && r.All.Trim().Length > 0) return r;
            }
            return new RunResult();
        }, ct);

    /// <summary>
    /// 创建网络。driver 空则交给 wslc 用默认（bridge）；
    /// subnet / gateway 空则不下发该参数（wslc 自行分配网段）。
    /// </summary>
    public static Task<RunResult> NetworkCreateAsync(string name, string driver, string subnet,
        string gateway, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };

            var a = new List<string> { "network", "create" };
            var d = (driver ?? "").Trim();
            if (d.Length > 0) { a.Add("-d"); a.Add(d); }
            var sub = (subnet ?? "").Trim();
            if (sub.Length > 0) { a.Add("--subnet"); a.Add(sub); }
            var gw = (gateway ?? "").Trim();
            if (gw.Length > 0) { a.Add("--gateway"); a.Add(gw); }
            a.Add((name ?? "").Trim());

            return ProcRunner.Run(ExePath, a, 120000, ct);
        }, ct);

    /// <summary>删除网络。force 对应 wslc 的 <c>-f</c>（网络不存在时不报错）。</summary>
    public static Task<RunResult> NetworkRemoveAsync(string name, bool force = true,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Available) return new RunResult { ErrorMessage = "未找到 wslc.exe" };
            var a = new List<string> { "network", "rm" };
            if (force) a.Add("-f");
            a.Add((name ?? "").Trim());
            return ProcRunner.Run(ExePath, a, 60000, ct);
        }, ct);

    public static async Task<(List<NetworkInfo> Items, RunResult Raw)> ListNetworksAsync(
        CancellationToken ct = default)
    {
        var raw = await RawListNetworksAsync(ct);
        var items = ParseNetworkList(raw.All);

        // 详情（网段 / 网关 / 关联容器）只能靠 inspect 补 —— 一次把它们全传进去
        if (items.Count > 0)
        {
            var insp = await RawInspectNetworksAsync(items.Select(i => i.Name), ct);
            if (insp.Success && insp.All.Trim().Length > 0)
                ApplyNetworkDetails(items, insp.All);
        }
        return (items, raw);
    }

    /// <summary>解析 `network ls` 输出（NDJSON 优先，回退列对齐表格）。独立方法便于离线自检。</summary>
    internal static List<NetworkInfo> ParseNetworkList(string text)
    {
        var res = new List<NetworkInfo>();

        var json = TableParser.ParseJsonLines(text);
        if (json.Count > 0)
        {
            foreach (var d in json)
            {
                var n = new NetworkInfo
                {
                    Id = TableParser.Pick(d, "ID", "Id"),
                    Name = TableParser.Pick(d, "NAME", "Name", "名称"),
                    Driver = TableParser.Pick(d, "DRIVER", "Driver", "驱动程序"),
                    Scope = TableParser.Pick(d, "SCOPE", "Scope", "范围"),
                    Created = NetworkInfo.NormCreated(
                        TableParser.Pick(d, "CREATEDAT", "CREATED", "CreatedAt", "Created", "已创建")),
                };
                if (string.IsNullOrWhiteSpace(n.Name) && string.IsNullOrWhiteSpace(n.Id)) continue;
                if (string.IsNullOrWhiteSpace(n.Name)) n.Name = n.ShortId;
                res.Add(n);
            }
            SortNetworks(res);
            return res;
        }

        var (header, rows) = TableParser.ParseAligned(text);
        var norm = header.Select(TableParser.Normalize).ToArray();
        foreach (var row in rows)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < norm.Length; i++) d[norm[i]] = i < row.Length ? row[i] : "";

            var n = new NetworkInfo
            {
                Id = TableParser.Pick(d, "ID", "Id", "网络id"),
                Name = TableParser.Pick(d, "NAME", "Name", "名称"),
                Driver = TableParser.Pick(d, "DRIVER", "Driver", "驱动程序"),
                Scope = TableParser.Pick(d, "SCOPE", "Scope", "范围"),
            };
            if (string.IsNullOrWhiteSpace(n.Name) && string.IsNullOrWhiteSpace(n.Id)) continue;
            if (string.IsNullOrWhiteSpace(n.Name)) n.Name = n.ShortId;
            res.Add(n);
        }
        SortNetworks(res);
        return res;
    }

    /// <summary>内置三网络固定排在前面，其余按名称；避免用户网络把 bridge/host/none 挤到看不见的地方。</summary>
    private static void SortNetworks(List<NetworkInfo> list)
        => list.Sort((a, b) =>
        {
            int ra = NetRank(a.Name), rb = NetRank(b.Name);
            if (ra != rb) return ra.CompareTo(rb);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

    private static int NetRank(string name) => (name ?? "").ToLowerInvariant() switch
    {
        "bridge" => 0,
        "host" => 1,
        "none" => 2,
        _ => 3
    };

    /// <summary>把 inspect 的详情合并进 ls 出来的列表（按名称匹配，名称缺失时按 ID 前缀兜底）。</summary>
    internal static void ApplyNetworkDetails(List<NetworkInfo> items, string inspectText)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in ParseJsonArrayElements(inspectText))
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var nm = JsonStr(el, "Name");
            if (nm.Length > 0) byName[nm] = el;
            var id = JsonStr(el, "Id");
            if (id.Length > 0) byId[id] = el;
        }

        foreach (var n in items)
        {
            JsonElement el;
            bool found = byName.TryGetValue(n.Name, out el);
            if (!found && n.Id.Length >= 8)
            {
                foreach (var kv in byId)
                    if (kv.Key.StartsWith(n.Id, StringComparison.OrdinalIgnoreCase)) { el = kv.Value; found = true; break; }
            }
            if (!found) continue;

            FillNetworkDetail(el, n);
            n.DetailLoaded = true;
        }
    }

    private static void FillNetworkDetail(JsonElement el, NetworkInfo n)
    {
        var id = JsonStr(el, "Id");
        if (id.Length > 0) n.Id = id;
        var drv = JsonStr(el, "Driver");
        if (drv.Length > 0) n.Driver = drv;
        var scope = JsonStr(el, "Scope");
        if (scope.Length > 0) n.Scope = scope;
        var created = JsonStr(el, "Created");
        if (created.Length > 0) n.Created = NetworkInfo.NormCreated(created);
        if (el.TryGetProperty("Internal", out var it) && it.ValueKind == JsonValueKind.True)
            n.Internal = true;

        // IPAM.Config 是数组（可能为 null）—— 取第一条带 Subnet 的
        if (el.TryGetProperty("IPAM", out var ipam) && ipam.ValueKind == JsonValueKind.Object &&
            ipam.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cfg.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var sub = JsonStr(c, "Subnet");
                if (sub.Length == 0) continue;
                n.Subnet = sub;
                n.Gateway = JsonStr(c, "Gateway");
                break;
            }
        }

        n.Containers.Clear();
        if (el.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in cs.EnumerateObject())
            {
                var ep = new NetworkEndpoint { Id = kv.Name };
                if (kv.Value.ValueKind == JsonValueKind.Object)
                {
                    ep.Name = JsonStr(kv.Value, "Name");
                    ep.Ip = JsonStr(kv.Value, "IPv4Address");
                }
                else if (kv.Value.ValueKind == JsonValueKind.String)
                {
                    ep.Name = kv.Value.GetString() ?? "";
                }
                if (ep.Name.Length == 0)
                    ep.Name = ep.Id.Length > 12 ? ep.Id.Substring(0, 12) : ep.Id;
                n.Containers.Add(ep);
            }
        }
    }


    /// 从可能夹带提示文本的输出里取出 JSON 片段，解析成元素列表（数组 → 逐个元素，对象 → 单个）。
    ///
    /// ⚠ 必须 <c>Clone()</c>：JsonDocument 一旦 Dispose，它枚举出来的 JsonElement 全部失效
    ///   （访问会抛 ObjectDisposedException）—— 这里要跨出 using 作用域返回，所以逐个克隆。
    /// </summary>
    internal static List<JsonElement> ParseJsonArrayElements(string text)
    {
        var res = new List<JsonElement>();
        var t = (text ?? "").Trim();
        if (t.Length == 0) return res;

        // 与 ParseNetInfo 同一套截取规则：从更靠前的 '[' / '{' 起，到最后一个配对结尾
        int start = FirstIndexOf(t, '[', '{');
        if (start < 0) return res;
        int end = t.LastIndexOf(t[start] == '[' ? ']' : '}');
        if (end < start) return res;

        try
        {
            using var doc = JsonDocument.Parse(t.Substring(start, end - start + 1));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) res.Add(el.Clone());
            else if (root.ValueKind == JsonValueKind.Object)
                res.Add(root.Clone());
        }
        catch { /* 解析不了就当没有详情，列表仍可显示基本信息 */ }
        return res;
    }
}
