using System;
using System.Net;
using System.Net.Sockets;

namespace WslManager.Wslc.Services;

/// <summary>
/// 创建网络前的**本地**校验。
///
/// 定位：只拦「一眼就是错的」输入，目标是把 wslc 的报错翻译成人话，**不是**重新实现一个
/// CIDR 校验器 —— 与上游重复实现只会慢慢漂移，真正合法与否最终由 wslc 说了算。
///
/// 放在 Services 层而不是窗口类里，是为了让探针能直接跑（窗口类是 WPF，探针编不进去）。
/// </summary>
public static class NetworkValidator
{
    /// <summary>
    /// 不能用于容器网络的保留段（判定与 Docker 一致）。
    ///
    /// 为什么必须在这里拦：**`wslc network create` 对网段不做任何校验** ——
    /// 建 `127.11.0.0/24` 会 exit=0 并出现在网络列表里，看着一切正常，
    /// 直到启容器才炸：
    /// <code>
    /// failed to create shim task: OCI runtime create failed: ... error running prestart hook #0:
    /// failed to set gateway while updating gateway: route for the gateway 127.11.0.1
    /// could not be found: invalid argument
    /// </code>
    /// 回环段（127/8）在容器网络命名空间里没有对应路由，网关必然设不上。
    /// </summary>
    private static readonly (string Cidr, string Why)[] ReservedSubnets =
    {
        ("127.0.0.0/8",    "回环（loopback）段，容器里没有到它的路由，网关必然设不上"),
        ("0.0.0.0/8",      "保留段（0.x.x.x），不能作为容器网段"),
        ("169.254.0.0/16", "链路本地段，会被系统自动占用"),
        ("224.0.0.0/4",    "多播段，不能作为容器网段"),
        ("240.0.0.0/4",    "保留段（240.x.x.x），不能作为容器网段"),
    };

    /// <summary>
    /// 校验通过返回 true；否则 why 里是一句可直接展示给用户的中文。
    /// </summary>
    public static bool Validate(string name, string subnet, string gateway, out string why)
    {
        why = "";
        var nm = (name ?? "").Trim();
        var sub = (subnet ?? "").Trim();
        var gw = (gateway ?? "").Trim();

        if (nm.Length == 0) { why = "请先填网络名称"; return false; }

        foreach (var ch in nm)
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
            { why = "网络名称只能用字母、数字、下划线、连字符和点"; return false; }

        if (nm.Equals("bridge", StringComparison.OrdinalIgnoreCase) ||
            nm.Equals("host", StringComparison.OrdinalIgnoreCase) ||
            nm.Equals("none", StringComparison.OrdinalIgnoreCase))
        { why = $"「{nm}」是 wslc 自带网络，换个名字"; return false; }

        if (!TryParseCidr(sub, out var netIp, out var prefix, out var subWhy))
        {
            why = subWhy;
            return false;
        }

        // 网段的可用性检查（网络地址写法 + 保留段）与「列表里标记坏网段」共用同一个判定
        var subProblem = ExplainSubnet(sub);
        if (subProblem.Length > 0) { why = subProblem; return false; }

        if (sub.Length == 0 && gw.Length > 0)
        {
            // 只填网关不填网段：wslc 会自己挑网段，手填的网关几乎必然落在网段外
            why = "填了网关就要把网段一起填上，否则网关会落在 wslc 自动分配的网段之外";
            return false;
        }

        if (gw.Length > 0)
        {
            if (!IPAddress.TryParse(gw, out var gwIp) ||
                gwIp.AddressFamily != AddressFamily.InterNetwork)
            { why = "网关要写成 IPv4 地址，例如 172.30.0.1"; return false; }

            if (netIp != null && !InSameSubnet(netIp, gwIp, prefix))
            { why = $"网关 {gw} 不在网段 {sub} 内，容器会连不上"; return false; }
        }

        return true;
    }

    /// <summary>
    /// 检查一个 CIDR 网段能否用于容器网络：返回 "" 表示没问题，否则返回一句可直接展示的原因。
    ///
    /// 两处共用（判定只有一份，避免"创建时拦得住、列表里认不出来"）：
    ///   · 创建网络前的校验（<see cref="Validate"/>）
    ///   · 网络列表里给**已经存在的坏网段**打警告 —— `wslc network create` 不校验，
    ///     用户手上可能早就有一个用不了的网络，列表必须能认出来，
    ///     否则只有等到启容器报一长串 runc 错误时才发现。
    /// </summary>
    public static string ExplainSubnet(string subnet)
    {
        if (!TryParseCidr(subnet, out var ip, out var prefix, out var parseWhy)) return parseWhy;
        if (ip == null) return "";                 // 留空 = 交给 wslc 自动分配，无话可说

        var s = (subnet ?? "").Trim();

        var baseIp = Masked(ip, prefix);
        if (!baseIp.Equals(ip))
            return $"网段要写网络地址（主机位全 0），{s} 应写成 {baseIp}/{prefix}";

        foreach (var (cidr, reason) in ReservedSubnets)
        {
            if (!TryParseCidr(cidr, out var resIp, out var resPrefix, out _)) continue;
            if (InSameSubnet(ip, resIp, resPrefix))
                return $"网段 {s} 落在{reason} —— 容器起不来，换私网段（例如 172.30.0.0/24 或 192.168.99.0/24）";
        }
        return "";
    }

    /// <summary>解析 CIDR。subnet 留空算通过（= 让 wslc 自动分配），此时 ip 返回 null。</summary>
    public static bool TryParseCidr(string subnet, out IPAddress ip, out int prefix, out string why)
    {
        ip = null; prefix = 0; why = "";
        var s = (subnet ?? "").Trim();
        if (s.Length == 0) return true;

        var slash = s.IndexOf('/');
        if (slash <= 0 || slash == s.Length - 1)
        { why = "网段要写成 CIDR 形式，例如 172.30.0.0/24"; return false; }

        if (!IPAddress.TryParse(s.Substring(0, slash).Trim(), out ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        { why = "网段里的地址要是 IPv4，例如 172.30.0.0/24"; return false; }

        if (!int.TryParse(s.Substring(slash + 1).Trim(), out prefix) || prefix < 1 || prefix > 32)
        { why = "网段掩码要在 1~32 之间，例如 /24"; return false; }

        return true;
    }

    public static bool InSameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4) return false;

        uint mask = MaskOf(prefix);
        return (ToUInt(x) & mask) == (ToUInt(y) & mask);
    }

    /// <summary>把地址按掩码抹成网络地址（主机位清零）。</summary>
    public static IPAddress Masked(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return ip;

        uint v = ToUInt(b) & MaskOf(prefix);
        return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }

    /// <summary>
    /// 掩码常量。
    /// ⚠ prefix 为 0 时绝不能写 `0xFFFFFFFFu &lt;&lt; 32` —— C# 的移位量是按 32 取模的，
    ///   左移 32 位等于没移，会得到「全 1 掩码」这种反直觉结果（然后任何两个地址都判成同段）。
    /// </summary>
    private static uint MaskOf(int prefix) => prefix <= 0
        ? 0u
        : (prefix >= 32 ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - prefix)));

    private static uint ToUInt(byte[] b)
        => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
}
