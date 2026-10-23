using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WslManager.Wslc.Models;

/// <summary>
/// 一个 wslc 网络。<c>wslc network ls</c> 只给 ID/名称/驱动/范围，
/// **网段与关联容器只存在于 <c>network inspect</c>** —— 所以要两路数据合起来填。
/// </summary>
public sealed class NetworkInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Created { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string Gateway { get; set; } = "";
    public bool Internal { get; set; }

    /// <summary>挂在这个网络上的容器（含容器内 IP）。</summary>
    public List<NetworkEndpoint> Containers { get; } = new();

    /// <summary>inspect 是否成功补齐了详情（失败时列表仍可显示基本信息）。</summary>
    public bool DetailLoaded { get; set; }

    public string ShortId => Id.Length > 12 ? Id.Substring(0, 12) : Id;

    /// <summary>wslc 会话自带的三个网络（bridge / host / none），不可删除。</summary>
    public bool IsBuiltIn =>
        Name.Equals("bridge", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>列表「操作」列按钮的可用性。</summary>
    public bool CanRemove => !IsBuiltIn;

    public string RemoveTip => IsBuiltIn
        ? "wslc 自带网络，不能删除"
        : (ContainerCount > 0
            ? $"网络上还挂着 {ContainerCount} 个容器，删除会失败 —— 先断开或删掉这些容器"
            : "删除这个网络");

    /// <summary>驱动名 + 中文注解（"null" 是 none 网络的驱动名）。</summary>
    public string DriverText => Driver.ToLowerInvariant() switch
    {
        "bridge" => "bridge 桥接",
        "host" => "host 主机",
        "null" => "none 无网络",
        "" => "—",
        _ => Driver
    };

    /// <summary>
    /// 网段：没配（或 inspect 失败）时给一个中性占位，不用空白。
    /// 内置的 host / none 本来就没有网段，说「自动分配」会误导 —— 它们只能是「—」。
    /// </summary>
    public string SubnetText => Subnet.Length > 0
        ? Subnet
        : (IsBuiltIn || !DetailLoaded ? "—" : "自动分配");

    public string GatewayText => Gateway.Length > 0 ? Gateway : "—";

    /// <summary>
    /// 网段不可用的原因（空串 = 没问题）。刷新时由 <c>NetworkValidator.ExplainSubnet</c> 填入 ——
    /// **不在 Model 里自己判**，判定只保留 Services 层那一份。
    /// 存在的意义：`wslc network create` 不校验网段，用户手上可能早就有个永远起不了容器的网络，
    /// 列表得能把它指出来。
    /// </summary>
    public string SubnetWarning { get; set; } = "";

    public bool HasSubnetWarning => SubnetWarning.Length > 0;

    public int ContainerCount => Containers.Count;

    /// <summary>列表「关联容器」列：名称逗号分隔，空则 "—"。</summary>
    public string ContainersText => Containers.Count == 0
        ? "—"
        : string.Join("、", Containers.Select(c => c.Name));

    /// <summary>悬停提示：每个容器一行「名称 + 网络内 IP」。</summary>
    public string ContainersTip => Containers.Count == 0
        ? "没有容器连接到这个网络"
        : string.Join("\n", Containers.Select(c =>
                c.Ip.Length > 0 ? $"{c.Name}    {c.Ip}" : c.Name));

    public string CountText => Containers.Count == 0 ? "" : $"{Containers.Count} 个";

    /// <summary>基础信息行的悬停提示（ID / 范围 / 创建时间）。</summary>
    public string BasicTip =>
        $"网络 ID：{Id}\n驱动：{DriverText}\n范围：{Scope}\n创建：{Created}" +
        (Internal ? "\n外部访问：已限制（--internal）" : "");

    /// <summary>把 UTC / 带偏移的时间串归一成本地 "yyyy-MM-dd HH:mm"。</summary>
    public static string NormCreated(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        // wslc network ls 风格："2026-10-13 16:26:40.233599799 +0000 UTC" —— 尾部 " UTC" 解析不了
        if (t.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(0, t.Length - 4).Trim();
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return s.Trim();
    }
}

/// <summary>网络里的一个容器端点。</summary>
public sealed class NetworkEndpoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>形如 172.30.0.2/24；取不到就留空。</summary>
    public string Ip { get; set; } = "";
}
