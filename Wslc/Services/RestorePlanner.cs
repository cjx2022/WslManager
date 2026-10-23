using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using WslManager.Wslc.Models;

namespace WslManager.Wslc.Services;

/// <summary>
/// 把「备份时随附的配置元数据」（`wslc inspect -f json` 原文）翻译成一个可直接下发的
/// <see cref="ContainerSpec"/>。
///
/// 为什么单独抽成纯函数：这段解析原长在界面点击回调里，只能靠人手点一次弹窗才验得到，
/// 于是字段名猜错了两版都没人发现 —— **真实的 inspect 输出里端口映射在顶层 `Ports`**，
/// `HostConfig` 下根本没有 `PortBindings`（2026-10-12 用
/// `wslc inspect wslcm_bugcheck -f json` 实测确认）。结果是「按原配置恢复」恢复出来的容器
/// 永远没有端口映射，而界面还在日志里承诺「按原配置（名称/启动命令/端口/环境变量）重建」。
///
/// 抽出来后探针可以直接喂一份**真实 JSON** 断言，不用弹窗、不用人手点。
/// </summary>
public static class RestorePlanner
{
    /// <summary>解析结果：可直接下发的规格 + 需要提示给用户的说明。</summary>
    public sealed class Plan
    {
        public ContainerSpec Spec { get; } = new();

        /// <summary>备份里记录的原始容器名（可能为空）。</summary>
        public string OriginalName { get; set; } = "";

        /// <summary>备份里记录的重启策略（wslc run 不支持 --restart，只记录不下发）。</summary>
        public string RestartPolicy { get; set; } = "";

        /// <summary>解析过程中的提醒，界面按原样打进日志。</summary>
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// 解析元数据。
    /// </summary>
    /// <param name="json">inspect 原文（对象或只含一个对象的数组都接受）。</param>
    /// <param name="imageRef">重建时使用的镜像引用。</param>
    /// <param name="nameTaken">判断名字是否已被占用（占用则加 <c>-restored</c>）；可为 null。</param>
    /// <exception cref="JsonException">json 不是合法 JSON —— 由调用方兜住并降级为「只恢复镜像」。</exception>
    public static Plan FromInspectJson(string json, string imageRef, Func<string, bool> nameTaken = null)
    {
        var plan = new Plan();
        var spec = plan.Spec;
        spec.Image = imageRef ?? "";
        spec.Detach = true;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return plan;
            root = root[0];
        }

        // ── 名称 ──
        plan.OriginalName = Str(root, "Name").TrimStart('/');
        var name = string.IsNullOrWhiteSpace(plan.OriginalName) ? FallbackBase(imageRef) : plan.OriginalName;
        if (nameTaken != null && nameTaken(name)) name += "-restored";
        spec.Name = name;

        // ── 启动命令 / 环境变量（都在 Config 下，字段名已实测）──
        if (root.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            spec.Command = JoinCmd(cfg);
            foreach (var e in ReadEnv(cfg)) spec.Env.Add(e);
        }

        // ── 端口映射 ──
        // 真实位置：顶层 Ports（形如 {"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"18080"}]}）
        foreach (var p in ReadPorts(root, "Ports")) spec.Ports.Add(p);

        // 兜底：若上游哪天改回 Docker 那套（HostConfig.PortBindings），也能认
        if (spec.Ports.Count == 0 &&
            root.TryGetProperty("HostConfig", out var hc) && hc.ValueKind == JsonValueKind.Object)
            foreach (var p in ReadPorts(hc, "PortBindings")) spec.Ports.Add(p);

        // ── 重启策略（只记录） ──
        if (root.TryGetProperty("HostConfig", out var hcr) && hcr.ValueKind == JsonValueKind.Object &&
            hcr.TryGetProperty("RestartPolicy", out var rp) && rp.ValueKind == JsonValueKind.Object)
        {
            plan.RestartPolicy = Str(rp, "Name");
            if (plan.RestartPolicy.Length > 0 && plan.RestartPolicy != "no")
                plan.Notes.Add($"原容器重启策略为 {plan.RestartPolicy}，但 wslc run 暂不支持 --restart，恢复后未应用（容器不会开机自启）。");
        }

        if (spec.Command.Length == 0)
            plan.Notes.Add("备份里没有启动命令 —— 恢复后的容器可能一启动就退出（wslc 会把空命令当 main process 立即结束）。");

        return plan;
    }

    /// <summary>
    /// 从 <paramref name="scope"/> 下的端口对象读映射。
    /// </summary>
    /// <param name="propName">端口对象所在的属性名：
    /// <c>Ports</c>（顶层，当前真实格式）或 <c>PortBindings</c>（Docker 风格兜底）。</param>
    private static List<ContainerSpec.PortMap> ReadPorts(JsonElement scope, string propName)
    {
        var list = new List<ContainerSpec.PortMap>();
        if (!scope.TryGetProperty(propName, out var ports) || ports.ValueKind != JsonValueKind.Object) return list;

        foreach (var kp in ports.EnumerateObject())
        {
            // 键形如 "80/tcp"、"53/udp"
            var slash = kp.Name.IndexOf('/');
            var cp = slash > 0 ? kp.Name.Substring(0, slash) : kp.Name;
            var proto = slash > 0 && kp.Name.Substring(slash + 1).Equals("udp", StringComparison.OrdinalIgnoreCase)
                ? "udp" : "";
            if (cp.Length == 0 || kp.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var b in kp.Value.EnumerateArray())
            {
                var host = Str(b, "HostPort");
                if (host.Length == 0) continue;
                list.Add(new ContainerSpec.PortMap { HostPort = host, ContainerPort = cp, Protocol = proto });
                break;   // 同一个容器端口只取第一条宿主映射
            }
        }
        return list;
    }

    private static IEnumerable<ContainerSpec.EnvVar> ReadEnv(JsonElement cfg)
    {
        if (!cfg.TryGetProperty("Env", out var env) || env.ValueKind != JsonValueKind.Array) yield break;

        foreach (var e in env.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var kv = e.GetString() ?? "";
            var i = kv.IndexOf('=');
            if (i > 0)
                yield return new ContainerSpec.EnvVar { Key = kv.Substring(0, i), Value = kv.Substring(i + 1) };
        }
    }

    /// <summary>把 Config.Cmd 数组拼回一条命令行（带空格的 token 补引号）。</summary>
    private static string JoinCmd(JsonElement cfg)
    {
        if (!cfg.TryGetProperty("Cmd", out var cmd) || cmd.ValueKind != JsonValueKind.Array) return "";

        var sb = new StringBuilder();
        foreach (var t in cmd.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String) continue;
            var tok = t.GetString() ?? "";
            if (tok.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            if (tok.IndexOf(' ') >= 0 || tok.IndexOf('"') >= 0)
                sb.Append('"').Append(tok.Replace("\"", "\\\"")).Append('"');
            else sb.Append(tok);
        }
        return sb.ToString();
    }

    /// <summary>镜像名派生出的容器名（去掉 tag 与仓库前缀）。</summary>
    private static string FallbackBase(string imageRef)
    {
        var s = imageRef ?? "";
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        var colon = s.LastIndexOf(':');
        if (colon >= 0) s = s.Substring(0, colon);
        return s.Length == 0 ? "restored" : s;
    }

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";
}
