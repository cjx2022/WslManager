using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WslManager.Wslc.Services;

/// <summary>一条镜像检索结果。</summary>
public sealed class ImageSearchItem
{
    public string Repository { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Official { get; set; }
    public long Stars { get; set; }
    /// <summary>true = 来自远程仓库；false = 来自内置索引。</summary>
    public bool FromRemote { get; set; }

    /// <summary>填进拉取框用的名字。</summary>
    public string PullName => string.IsNullOrWhiteSpace(Name) ? Repository : Name;

    public string StarsText => Stars > 0 ? "★ " + Stars.ToString("N0") : "";
    public string SourceText => FromRemote ? "远程" : "内置索引";
}

public sealed class ImageSearchResult
{
    public List<ImageSearchItem> Items { get; } = new();
    public bool RemoteOk { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 镜像检索。
///
/// 关键事实（2026-10 实测）：Docker Hub 的检索接口在国内**彻底不可达**
/// —— hub.docker.com / registry-1.docker.io / docker.io 全部连接挂起（8~10s 超时），
/// 且 wslc 自身没有 `search` 子命令。所以检索的主数据源只能是**本地内置索引**，
/// 并且必须在界面层**瞬时返回**，不能等远程超时（否则表现就是"点了没反应"）。
///
/// 因此接口拆成两半：
///   · SearchLocal   —— 同步、瞬时、零网络依赖（**界面用的就是这一个**）
///   · SearchRemoteAsync —— 限时 3s 的尝试，失败返回空表（能通就补充真实仓库数据）
/// 兼容入口 SearchAsync = 两者合并。
///
/// ⚠ **当前 UI 不调用远程那两半**（MainWindow 只调 SearchLocal）——
///   这是刻意的：在目标网络里那 3 秒等不到任何东西，只会让用户以为界面卡住。
///   远程实现保留下来是因为它是可选增强、且 `_probe` 有对应的限时/冷却自检；
///   将来若要做"代理环境下补充真实仓库数据"，把 MainWindow 改回调 SearchAsync 即可。
/// </summary>
public static class ImageSearchService
{
    /// <summary>
    /// 远程查询超时。国内到 hub.docker.com 是"连接挂起"而非快速失败，
    /// 给太长会让界面卡住，给太短则代理环境下拿不到结果 —— 3 秒是折中。
    /// </summary>
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// "远程已知不可达"的冷却截止时间。国内 hub.docker.com 基本永远连不上，
    /// 若每次检索都干等 3 秒纯属浪费 —— 失败后 5 分钟内直接跳过远程尝试。
    /// （超过窗口会再试一次，这样用户挂了代理后能自动恢复。）
    /// </summary>
    private static DateTime _remoteDownUntilUtc = DateTime.MinValue;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // 超时交给 CancellationToken 按请求控制（HttpClient.Timeout 是全局的，无法按次调整）
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.Add("User-Agent", "WSLCManager/1.0");
        return h;
    }

    // ── 索引版本与外置索引（索引是"会过时的数据"，必须能更新、能自证版本）────
    /// <summary>
    /// 内置索引的版本号。**每次改动下面的 Index 数组都必须同步改这里** ——
    /// 否则用户没法判断"搜不到"是索引太旧还是确实没有这个镜像。
    /// </summary>
    public const string BuiltInIndexVersion = "2026.10.10";

    /// <summary>外置索引（可更新的那份）的落盘位置。</summary>
    public static string IndexFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WSLCManager", "image-index.json");

    private static string[] _externalIndex;   // null = 尚未加载；空数组 = 已加载但不可用
    private static string _externalVersion = "";
    private static string _externalUpdated = "";

    /// <summary>当前生效的条目：外置索引优先，缺失或损坏时回落内置索引。</summary>
    private static string[] EffectiveIndex
    {
        get
        {
            if (_externalIndex == null) LoadExternalIndex();
            return _externalIndex != null && _externalIndex.Length > 0 ? _externalIndex : Index;
        }
    }

    private static bool UsingExternal => _externalIndex != null && _externalIndex.Length > 0;

    public static string IndexSourceText => UsingExternal ? "外置索引" : "内置索引";
    public static string IndexVersionText => UsingExternal && _externalVersion.Length > 0
        ? _externalVersion : BuiltInIndexVersion;
    public static int IndexCount => EffectiveIndex.Length;

    /// <summary>索引状态一行摘要，用于设置页与检索提示。</summary>
    public static string IndexSummary
    {
        get
        {
            var tail = UsingExternal && _externalUpdated.Length > 0 ? $" · 更新于 {_externalUpdated}" : "";
            return $"{IndexSourceText} v{IndexVersionText} · {IndexCount} 条{tail}";
        }
    }

    /// <summary>重新读取外置索引（导入/删除后调用）。</summary>
    public static void ReloadIndex()
    {
        _externalIndex = null;
        LoadExternalIndex();
    }

    private static void LoadExternalIndex()
    {
        _externalIndex = Array.Empty<string>();
        _externalVersion = "";
        _externalUpdated = "";
        try
        {
            if (!File.Exists(IndexFilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(IndexFilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                _externalVersion = v.GetString() ?? "";
            if (root.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String)
                _externalUpdated = u.GetString() ?? "";

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                _externalIndex = items.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains('|'))
                    .ToArray();
            }
        }
        catch
        {
            // 外置索引损坏时当它不存在，静默回落内置索引（不能因为一个坏文件让检索整个不可用）
            _externalIndex = Array.Empty<string>();
        }
    }

    /// <summary>把当前生效的索引导出成 JSON（可作为"更新索引"的模板）。</summary>
    public static (bool ok, string msg) ExportIndex(string path)
    {
        try
        {
            File.WriteAllText(path, BuildIndexJson(IndexVersionText, EffectiveIndex), new UTF8Encoding(false));
            return (true, $"已导出 {EffectiveIndex.Length} 条 → {path}");
        }
        catch (Exception ex) { return (false, "导出失败：" + ex.Message); }
    }

    /// <summary>从 JSON 导入索引并落盘为外置索引，立即生效。</summary>
    public static (bool ok, string msg) ImportIndex(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var raw = new List<string>();
            if (root.ValueKind == JsonValueKind.Array) raw.AddRange(Flatten(root));
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var arr))
                raw.AddRange(Flatten(arr));

            var items = raw.Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains('|')).ToArray();
            if (items.Length == 0)
                return (false, "文件里没有可用条目（每条需要「名称[:tag]|描述|别名」格式）");

            var ver = root.ValueKind == JsonValueKind.Object
                      && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "") : "";

            Directory.CreateDirectory(Path.GetDirectoryName(IndexFilePath));
            File.WriteAllText(IndexFilePath,
                BuildIndexJson(ver.Length > 0 ? ver : DateTime.Now.ToString("yyyy.MM.dd"), items),
                new UTF8Encoding(false));
            ReloadIndex();
            return (true, $"已导入 {items.Length} 条，当前生效：{IndexSummary}");
        }
        catch (Exception ex) { return (false, "导入失败：" + ex.Message); }

        static IEnumerable<string> Flatten(JsonElement arr)
        {
            foreach (var x in arr.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) yield return x.GetString();
        }
    }

    /// <summary>删除外置索引，回到程序内置的那份。</summary>
    public static void ResetToBuiltIn()
    {
        try { if (File.Exists(IndexFilePath)) File.Delete(IndexFilePath); } catch { }
        ReloadIndex();
    }

    private static string BuildIndexJson(string version, IEnumerable<string> items)
    {
        var body = string.Join(",\r\n    ", items.Select(s => JsonSerializer.Serialize(s)));
        return "{\r\n" +
               $"  \"version\": {JsonSerializer.Serialize(version)},\r\n" +
               $"  \"updated\": {JsonSerializer.Serialize(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))},\r\n" +
               "  \"note\": \"WSLC 管理器镜像索引；每条格式：名称[:tag]|描述|别名(逗号分隔)\",\r\n" +
               "  \"items\": [\r\n    " + body + "\r\n  ]\r\n}\r\n";
    }

    // ── 本地索引（主数据源）────────────────────────────────────
    /// <summary>本地索引检索：同步、瞬时、零网络依赖。界面应立即显示它。</summary>
    public static ImageSearchResult SearchLocal(string query)
    {
        var res = new ImageSearchResult();
        var q = (query ?? "").Trim();
        if (q.Length == 0)
        {
            res.Message = "请输入要检索的镜像关键字，例如 nginx、redis、数据库、向量";
            return res;
        }

        res.Items.AddRange(SearchBuiltIn(q));

        // 口径：wslc 根本没有 search 子命令，能查的只有本地索引 —— 这点必须说清楚，
        // 否则用户会把"搜不到"理解成工具坏了。
        var src = $"{IndexSourceText} v{IndexVersionText}（共 {IndexCount} 条）";
        res.Message = res.Items.Count > 0
            ? $"wslc 无官方 search 功能，以下 {res.Items.Count} 条来自{src}"
            : $"wslc 无官方 search 功能；{src} 中没有匹配「{q}」的条目";
        return res;
    }

    // ── 远程（可选增强）──────────────────────────────────────────
    /// <summary>
    /// Docker Hub 公开检索接口。国内通常超时 —— 失败一律返回空表，不抛异常、不阻塞界面。
    /// </summary>
    public static async Task<List<ImageSearchItem>> SearchRemoteAsync(string query, CancellationToken ct = default)
    {
        var outList = new List<ImageSearchItem>();
        var q = (query ?? "").Trim();
        if (q.Length == 0) return outList;

        // 冷却期内直接跳过（避免每次检索都白等 3 秒）
        if (DateTime.UtcNow < _remoteDownUntilUtc) return outList;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RemoteTimeout);
        try
        {
            var url = "https://hub.docker.com/v2/search/repositories/?query=" +
                      Uri.EscapeDataString(q) + "&page_size=40";
            using var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return outList;

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var item = new ImageSearchItem { FromRemote = true };
                    item.Repository = GetStr(el, "repo_name");
                    if (string.IsNullOrWhiteSpace(item.Repository)) item.Repository = GetStr(el, "name");
                    item.Name = item.Repository;
                    item.Description = GetStr(el, "short_description");
                    item.Official = GetBool(el, "is_official");
                    item.Stars = GetLong(el, "star_count");
                    if (!string.IsNullOrWhiteSpace(item.Repository)) outList.Add(item);
                }
            }
        }
        catch
        {
            // 不可达 / 超时 / 解析失败 —— 一律当作"没有远程结果"，由调用侧回落到内置索引；
            // 同时进入 5 分钟冷却，避免连续检索反复白等 3 秒
            _remoteDownUntilUtc = DateTime.UtcNow.AddMinutes(5);
        }
        return outList;
    }

    /// <summary>兼容入口：本地索引结果为主，远程有结果时并入（并标注来源）。</summary>
    public static async Task<ImageSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        var res = SearchLocal(query);
        var remote = await SearchRemoteAsync(query, ct).ConfigureAwait(false);

        if (remote.Count > 0)
        {
            var seen = new HashSet<string>(res.Items.Select(i => i.Repository), StringComparer.OrdinalIgnoreCase);
            var merged = new List<ImageSearchItem>(remote);
            merged.AddRange(res.Items.Where(i => !seen.Contains(i.Repository)));
            res.Items.Clear();
            res.Items.AddRange(merged);
            res.RemoteOk = true;
            res.Message = $"来自镜像仓库（Docker Hub）{remote.Count} 条" +
                          (merged.Count > remote.Count ? $"，另有 {merged.Count - remote.Count} 条来自内置索引" : "");
        }
        else
        {
            var src = $"{IndexSourceText} v{IndexVersionText}（共 {IndexCount} 条）";
            res.Message = res.Items.Count > 0
                ? $"wslc 无官方 search 功能，以下 {res.Items.Count} 条来自{src}；索引可在「设置与加速」页更新。"
                : $"wslc 无官方 search 功能，{src} 中没有匹配「{query}」的条目。" +
                  $"可更新索引，或点「浏览器搜索」、直接在拉取框输入完整镜像名（例如 {query}:latest）。";
        }
        return res;
    }

    /// <summary>
    /// 「浏览器搜索」的默认地址模板，`{q}` 为检索词占位。
    /// 调用方（界面）可传入自己的模板（见 Helpers/AppSettings.WebSearchUrlTemplate）；
    /// 这里只做兜底 —— 兜底就是 Docker Hub 的官方检索页，**不预置任何第三方检索站**。
    /// （注意与「镜像加速前缀」区分：那是拉取通道，这是搜索引擎。）
    /// </summary>
    public const string DefaultWebSearchUrlTemplate = "https://hub.docker.com/search?q={q}";

    /// <summary>
    /// 在系统浏览器里打开一个镜像搜索页。地址模板由调用方给出（可空 → 用内置兜底）；
    /// 模板里必须含 `{q}`，否则也回落到内置兜底，避免打开一个没有检索词的页面。
    /// </summary>
    public static void OpenWebSearch(string query, string urlTemplate = null)
    {
        var q = string.IsNullOrWhiteSpace(query) ? "nginx" : query.Trim();

        var tpl = urlTemplate;
        if (string.IsNullOrWhiteSpace(tpl) || !tpl.Contains("{q}", StringComparison.Ordinal))
            tpl = DefaultWebSearchUrlTemplate;

        var url = tpl.Replace("{q}", Uri.EscapeDataString(q));
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    // ── 内置索引匹配 ─────────────────────────────────────────────
    /// <summary>
    /// 索引条目格式：name[:tag]|描述|别名（别名用逗号分隔，可留空）。
    /// 命中判定在「名字 + 描述 + 别名」三者拼接的串上做子串匹配 ——
    /// 所以中文关键词（如"数据库""向量""消息队列"）也能搜到。
    /// </summary>
    private static List<ImageSearchItem> SearchBuiltIn(string q)
    {
        var lower = q.ToLowerInvariant();
        var terms = lower.Split(new[] { ' ', ',', '，', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var res = new List<ImageSearchItem>();

        foreach (var raw in EffectiveIndex)
        {
            var parts = raw.Split('|');
            var name = parts[0];
            var desc = parts.Length > 1 ? parts[1] : "";
            var alias = parts.Length > 2 ? parts[2] : "";
            var hay = (name + " " + desc + " " + alias).ToLowerInvariant();

            int hit = terms.Count(t => hay.Contains(t));
            if (hit == 0) continue;

            res.Add(new ImageSearchItem
            {
                Repository = name,
                Name = name,
                Description = desc,
                Official = true,
                FromRemote = false,
            });
        }

        // 排序：整体前缀命中 > 仓库名命中词数 > 名字短；:latest 优先于同名的其他 tag
        return res
            .OrderByDescending(i => i.Repository.ToLowerInvariant().StartsWith(lower) ? 1 : 0)
            .ThenByDescending(i => terms.Count(t => i.Repository.ToLowerInvariant().Contains(t)))
            .ThenByDescending(i => i.Repository.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(i => i.Repository.Length)
            .Take(80)
            .ToList();
    }

    // ── 内置索引数据（离线可用，覆盖主流场景）────────────────────
    private static readonly string[] Index =
    {
        // ── 基础系统 ──
        "alpine:latest|极小体积的 Linux 发行版，容器基础镜像首选|基础,系统,linux,轻量,最小",
        "alpine:3.20|Alpine 3.20|基础,系统,linux",
        "ubuntu:latest|Ubuntu 基础镜像|基础,系统,linux",
        "ubuntu:24.04|Ubuntu 24.04 LTS 基础镜像|基础,系统,linux",
        "ubuntu:22.04|Ubuntu 22.04 LTS 基础镜像|基础,系统,linux",
        "ubuntu:20.04|Ubuntu 20.04 LTS 基础镜像|基础,系统,linux",
        "debian:latest|Debian 基础镜像|基础,系统,linux",
        "debian:12|Debian 12 基础镜像|基础,系统,linux",
        "debian:11|Debian 11 基础镜像|基础,系统,linux",
        "rockylinux:9|RHEL 兼容发行版|基础,系统,linux,rhel",
        "almalinux:9|RHEL 兼容发行版|基础,系统,linux,rhel",
        "centos:7|CentOS 7 基础镜像（已停止维护）|基础,系统,linux,rhel",
        "fedora:latest|Fedora 基础镜像|基础,系统,linux",
        "oraclelinux:9|Oracle Linux 基础镜像|基础,系统,linux",
        "opensuse/leap:latest|openSUSE Leap 基础镜像|基础,系统,linux,suse",
        "archlinux:latest|Arch Linux 基础镜像|基础,系统,linux",
        "busybox:latest|集成常用 Unix 工具的极简镜像|基础,系统,工具,轻量",
        "photon:latest|VMware Photon OS|基础,系统,linux",
        "amazonlinux:2023|Amazon Linux 2023|基础,系统,linux,aws",

        // ── Web 服务器 / 反向代理 ──
        "nginx:latest|高性能 Web 服务器与反向代理|web,服务器,反向代理,代理,http",
        "nginx:stable|Nginx 稳定分支|web,服务器,反向代理",
        "nginx:1.27|Nginx 1.27 稳定版|web,服务器,反向代理",
        "nginx:1.26|Nginx 1.26|web,服务器,反向代理",
        "nginx:alpine|Nginx 的 Alpine 精简版（体积小）|web,服务器,反向代理,轻量",
        "nginx:stable-alpine|Nginx 稳定分支的 Alpine 版|web,服务器,反向代理,轻量",
        "openresty/openresty:latest|基于 Nginx 与 Lua 的 Web 平台|web,服务器,反向代理,lua",
        "httpd:2.4|Apache HTTP Server|web,服务器,apache,http",
        "httpd:2.4-alpine|Apache 的 Alpine 版|web,服务器,apache,轻量",
        "caddy:latest|自动 HTTPS 的 Web 服务器|web,服务器,反向代理,https",
        "traefik:latest|云原生反向代理与负载均衡|web,反向代理,负载均衡,网关",
        "tomcat:10|Apache Tomcat 应用服务器|web,java,应用服务器,servlet",
        "tomcat:9|Apache Tomcat 9|web,java,应用服务器",
        "jetty:latest|Jetty 应用服务器|web,java,应用服务器",
        "haproxy:latest|高可用负载均衡器|web,负载均衡,代理,高可用",
        "varnish:latest|HTTP 加速缓存|web,缓存,加速",
        "jc21/nginx-proxy-manager:latest|带图形界面的 Nginx 反代管理|web,反向代理,管理,面板",

        // ── 关系型 / 文档数据库 ──
        "mysql:latest|MySQL 关系型数据库|数据库,关系型,sql,db",
        "mysql:8.0|MySQL 8.0 数据库|数据库,关系型,sql,db",
        "mysql:8.4|MySQL 8.4 LTS 数据库|数据库,关系型,sql,db",
        "mysql:5.7|MySQL 5.7 数据库|数据库,关系型,sql,db",
        "mariadb:latest|MySQL 的开源分支|数据库,关系型,sql,db,mysql",
        "mariadb:11|MariaDB 11|数据库,关系型,sql,db",
        "postgres:latest|PostgreSQL 关系型数据库|数据库,关系型,sql,db,pg",
        "postgres:16|PostgreSQL 16|数据库,关系型,sql,db,pg",
        "postgres:16-alpine|PostgreSQL 16 的 Alpine 版|数据库,关系型,sql,pg,轻量",
        "postgres:15|PostgreSQL 15|数据库,关系型,sql,db,pg",
        "percona:8.0|Percona Server for MySQL|数据库,关系型,sql,mysql",
        "mongo:latest|MongoDB 文档数据库|数据库,文档,nosql,db",
        "mongo:7|MongoDB 7|数据库,文档,nosql,db",
        "mongo:6|MongoDB 6|数据库,文档,nosql,db",
        "mongo:4.4|MongoDB 4.4|数据库,文档,nosql,db",
        "microsoft/mssql-server-linux:latest|SQL Server on Linux|数据库,关系型,sql,微软,mssql",
        "adminer:latest|轻量数据库管理界面|数据库,管理,界面,工具",
        "phpmyadmin:latest|MySQL 图形化管理工具|数据库,管理,界面,mysql,工具",
        "cloudbeaver/cloudbeaver:latest|多数据库 Web 管理工具|数据库,管理,界面,工具",

        // ── 时序 / 分析 / 搜索 / 向量数据库 ──
        "clickhouse/clickhouse-server:latest|列式分析数据库 ClickHouse|数据库,列式,分析,olap,大数据",
        "influxdb:2|时序数据库 InfluxDB|数据库,时序,监控,tsdb",
        "cassandra:latest|Apache Cassandra 分布式数据库|数据库,分布式,nosql,大数据",
        "neo4j:5|图数据库 Neo4j|数据库,图,graph,neo4j",
        "cockroachdb/cockroach:latest|分布式 SQL 数据库|数据库,分布式,sql,db",
        "elasticsearch:8|Elasticsearch 搜索与分析引擎|搜索,分析,es,日志,大数据",
        "opensearchproject/opensearch:latest|OpenSearch 搜索引擎|搜索,分析,日志,大数据",
        "pingcap/tidb:latest|TiDB 分布式 SQL 数据库|数据库,分布式,sql,mysql,国产",
        "trinodb/trino:latest|Trino 分布式 SQL 查询引擎|数据库,查询,引擎,大数据,olap",
        "prestodb/presto:latest|Presto 分布式查询引擎|数据库,查询,引擎,大数据,olap",
        "victoriametrics/victoria-metrics:latest|高性能时序数据库|数据库,时序,监控,prometheus",
        "qdrant/qdrant:latest|向量数据库 Qdrant|数据库,向量,vector,ai,检索",
        "milvusdb/milvus:latest|向量数据库 Milvus|数据库,向量,vector,ai,检索,国产",
        "weaviate:latest|向量数据库 Weaviate|数据库,向量,vector,ai,检索",
        "chromadb/chroma:latest|向量数据库 Chroma|数据库,向量,vector,ai,检索",
        "pgvector/pgvector:pg16|支持向量检索的 PostgreSQL|数据库,向量,vector,pg,ai",
        "redis/redis-stack:latest|Redis 官方栈（含搜索与 JSON）|数据库,缓存,redis,搜索",
        "valkey/valkey:latest|Valkey（Redis 开源分支）|数据库,缓存,redis,valkey",

        // ── 缓存 ──
        "redis:latest|Redis 内存数据库与缓存|数据库,缓存,kv,内存",
        "redis:7|Redis 7|数据库,缓存,kv,内存",
        "redis:7-alpine|Redis 的 Alpine 精简版|数据库,缓存,kv,轻量",
        "redis:6-alpine|Redis 6 的 Alpine 版|数据库,缓存,kv,轻量",
        "redis:alpine|Redis 最新版 Alpine 镜像|数据库,缓存,kv,轻量",
        "memcached:latest|内存对象缓存系统|缓存,内存,memcached",
        "memcached:alpine|Memcached 的 Alpine 版|缓存,内存,轻量",

        // ── 消息队列 / 流处理 ──
        "rabbitmq:3-management|RabbitMQ 消息队列（带管理界面）|消息队列,mq,rabbitmq,amqp",
        "rabbitmq:3|RabbitMQ 消息队列|消息队列,mq,rabbitmq,amqp",
        "rabbitmq:4|RabbitMQ 4|消息队列,mq,rabbitmq,amqp",
        "apache/kafka:latest|Apache Kafka 消息与流平台|消息队列,mq,kafka,流,大数据",
        "bitnami/kafka:latest|Bitnami 版 Kafka|消息队列,mq,kafka,流",
        "zookeeper:latest|Apache ZooKeeper 协调服务|协调,kafka,分布式",
        "nats:latest|NATS 轻量消息系统|消息队列,mq,nats,轻量",
        "emqx/emqx:latest|MQTT 物联网消息服务器|消息队列,mqtt,物联网,iot",
        "eclipse-mosquitto:latest|轻量 MQTT Broker|消息队列,mqtt,物联网,轻量",
        "activemq:latest|Apache ActiveMQ|消息队列,mq,jms,java",
        "apachepulsar/pulsar:latest|Apache Pulsar 消息平台|消息队列,mq,流,pulsar",
        "apache/rocketmq:latest|Apache RocketMQ 消息队列|消息队列,mq,国产,阿里",
        "redpandadata/redpanda:latest|Kafka 兼容的高性能流平台|消息队列,mq,kafka,流",
        "provectuslabs/kafka-ui:latest|Kafka 图形管理界面|消息队列,kafka,管理,界面",

        // ── 语言运行时 ──
        "node:latest|Node.js 运行时|语言,nodejs,js,javascript,运行时",
        "node:22|Node.js 22|语言,nodejs,js,运行时",
        "node:22-alpine|Node.js 22 的 Alpine 精简版|语言,nodejs,js,轻量",
        "node:20|Node.js 20 LTS|语言,nodejs,js,运行时",
        "node:18|Node.js 18 LTS|语言,nodejs,js,运行时",
        "python:latest|Python 运行时|语言,python,运行时",
        "python:3.12|Python 3.12|语言,python,运行时",
        "python:3.12-slim|Python 3.12 精简版|语言,python,轻量",
        "python:3.11|Python 3.11|语言,python,运行时",
        "python:3.11-alpine|Python 3.11 Alpine 版|语言,python,轻量",
        "openjdk:21|OpenJDK 21 运行时|语言,java,jdk,运行时",
        "openjdk:17|OpenJDK 17|语言,java,jdk,运行时",
        "eclipse-temurin:21-jre|Eclipse Temurin JRE 21|语言,java,jdk,运行时,temurin",
        "eclipse-temurin:17-jre|Eclipse Temurin JRE 17|语言,java,jdk,运行时,temurin",
        "amazoncorretto:21|Amazon Corretto JDK 21|语言,java,jdk,运行时,aws",
        "golang:latest|Go 语言编译环境|语言,go,golang,编译",
        "golang:1.23|Go 1.23|语言,go,golang,编译",
        "golang:1.23-alpine|Go 1.23 的 Alpine 版|语言,go,golang,轻量",
        "ruby:3.3|Ruby 运行时|语言,ruby,运行时",
        "php:8.3-fpm|PHP-FPM 运行时|语言,php,运行时,fpm",
        "php:8.2-apache|Apache + PHP|语言,php,web,apache",
        "perl:latest|Perl 运行时|语言,perl,运行时",
        "erlang:latest|Erlang 运行时|语言,erlang,运行时",
        "elixir:latest|Elixir 运行时|语言,elixir,运行时",
        "rust:latest|Rust 编译环境|语言,rust,编译",
        "swift:latest|Swift 编译环境|语言,swift,编译",
        "dart:stable|Dart 运行时|语言,dart,flutter",
        "deno:latest|Deno 运行时|语言,deno,js,ts",
        "oven/bun:latest|Bun 运行时（极快的 JS 运行时）|语言,bun,js,ts",
        "julia:latest|Julia 科学计算语言|语言,julia,科学计算",
        "mcr.microsoft.com/dotnet/aspnet:8.0|.NET 8 ASP.NET 运行时|语言,dotnet,csharp,aspnet,运行时",
        "mcr.microsoft.com/dotnet/sdk:8.0|.NET 8 SDK|语言,dotnet,csharp,sdk,编译",
        "mcr.microsoft.com/dotnet/runtime:8.0|.NET 8 运行时|语言,dotnet,csharp,运行时",

        // ── 构建 / CI / 制品 ──
        "maven:3.9-eclipse-temurin-21|Maven 构建工具|构建,maven,java,编译",
        "gradle:8-jdk21|Gradle 构建工具|构建,gradle,java,编译",
        "jenkins/jenkins:lts|Jenkins 持续集成|ci,cd,jenkins,持续集成",
        "sonarqube:lts|代码质量分析平台|ci,质量,代码扫描,sonar",
        "sonatype/nexus3:latest|Sonatype Nexus 制品仓库|制品,仓库,maven,npm",
        "registry:2|Docker Registry 私有镜像仓库|镜像仓库,registry,私有",
        "goharbor/harbor-core:latest|Harbor 企业级镜像仓库|镜像仓库,harbor,私有,企业",
        "gitlab/gitlab-ce:latest|GitLab 社区版|git,代码,仓库,ci,devops",
        "gitea/gitea:latest|轻量 Git 服务|git,代码,仓库,轻量",
        "drone/drone:latest|Drone 持续集成|ci,持续集成,drone",
        "argoproj/argocd:latest|Argo CD 声明式 GitOps 部署|cd,gitops,k8s,部署",
        "bitnami/kubectl:latest|kubectl 命令行工具|k8s,kubectl,工具",
        "alpine/helm:latest|Helm 包管理|k8s,helm,工具",

        // ── 监控 / 日志 / 可观测 ──
        "prom/prometheus:latest|Prometheus 监控系统|监控,指标,prometheus",
        "prom/alertmanager:latest|Prometheus 告警管理|监控,告警,alertmanager",
        "prom/node-exporter:latest|主机指标采集器|监控,指标,采集",
        "grafana/grafana:latest|Grafana 可视化面板|监控,可视化,面板,仪表盘",
        "grafana/loki:latest|Loki 日志聚合系统|日志,监控,loki",
        "grafana/tempo:latest|Tempo 分布式链路追踪|链路追踪,监控,tempo",
        "elastic/kibana:8|Kibana 日志可视化|日志,可视化,elk",
        "logstash:8|Logstash 日志采集|日志,采集,elk",
        "jaegertracing/all-in-one:latest|Jaeger 链路追踪|链路追踪,监控,jaeger",
        "openzipkin/zipkin:latest|Zipkin 链路追踪|链路追踪,监控,zipkin",
        "telegraf:latest|Telegraf 指标采集|监控,采集,telegraf",
        "gcr.io/cadvisor/cadvisor:latest|cAdvisor 容器监控|监控,容器,cadvisor",
        "zabbix/zabbix-server-mysql:latest|Zabbix 服务端|监控,zabbix,告警",
        "netdata/netdata:latest|实时性能监控|监控,性能,实时",
        "louislam/uptime-kuma:latest|Uptime Kuma 站点监控|监控,可用性,站点",
        "apache/skywalking-oap-server:latest|SkyWalking 服务端|监控,链路追踪,国产,apm",
        "apache/hertzbeat:latest|HertzBeat 开源监控告警|监控,告警,国产,apm",
        "sigp/signoz:latest|SigNoz 可观测平台|监控,日志,链路,可观测",

        // ── 对象存储 / 文件 / 网盘 ──
        "minio/minio:latest|MinIO 对象存储|存储,对象存储,s3,minio",
        "minio/mc:latest|MinIO 客户端|存储,客户端,s3",
        "nextcloud:latest|Nextcloud 私有云盘|云盘,文件,协作,网盘",
        "seafileltd/seafile-mc:latest|Seafile 私有网盘|云盘,文件,网盘",
        "owncloud:latest|ownCloud 私有云盘|云盘,文件,网盘",
        "filebrowser/filebrowser:latest|轻量 Web 文件管理器|文件,管理,web",
        "tsl0922/ttyd:latest|浏览器内终端|工具,终端,web",
        "xhofe/alist:latest|AList 多网盘聚合|网盘,聚合,文件,国产",

        // ── 媒体 / 娱乐 ──
        "jellyfin/jellyfin:latest|Jellyfin 媒体服务器|媒体,影音,服务器",
        "linuxserver/plex:latest|Plex 媒体服务器|媒体,影音,服务器",
        "navidrome/navidrome:latest|Navidrome 音乐服务器|媒体,音乐,服务器",
        "photoprism/photoprism:latest|PhotoPrism 照片管理|媒体,照片,相册",
        "fallenbagel/jellyseerr:latest|Jellyseerr 媒体请求管理|媒体,请求,管理",
        "jgraph/drawio:latest|Draw.io 在线绘图|工具,绘图,流程图",
        "excalidraw/excalidraw:latest|Excalidraw 手绘风白板|工具,绘图,白板",

        // ── 工具 / 运维 ──
        "git:latest|Git 命令行工具|工具,git,版本控制",
        "curl:latest|curl 命令行工具|工具,curl,http",
        "openssh-server:latest|SSH 服务端|工具,ssh,远程",
        "alpine/git:latest|极小体积的 Git 镜像|工具,git,轻量",
        "docker:latest|Docker CLI|工具,docker,容器",
        "alpine/curl:latest|Alpine + curl|工具,curl,轻量",
        "ansible/ansible-runner:latest|Ansible 运行环境|运维,ansible,自动化",
        "hashicorp/terraform:latest|Terraform 基础设施即代码|运维,terraform,iac",
        "hashicorp/vault:latest|HashiCorp Vault 密钥管理|安全,密钥,vault",
        "hashicorp/consul:latest|Consul 服务发现与配置|运维,服务发现,consul",
        "amazon/aws-cli:latest|AWS 命令行工具|工具,aws,云",
        "etcd:latest|etcd 分布式键值存储|运维,etcd,k8s,协调",
        "portainer/portainer-ce:latest|Portainer 容器管理界面|容器,管理,界面,面板",
        "containrrr/watchtower:latest|镜像自动更新|运维,自动更新,容器",
        "codercom/code-server:latest|浏览器版 VS Code|工具,ide,代码,web",
        "n8nio/n8n:latest|n8n 工作流自动化|自动化,工作流,低代码",
        "gethomepage/homepage:latest|Homepage 仪表盘导航页|工具,导航,仪表盘",

        // ── 安全 / 认证 ──
        "quay.io/keycloak/keycloak:latest|Keycloak 身份认证与 SSO|安全,认证,sso,oauth",
        "authelia/authelia:latest|Authelia 认证门户|安全,认证,单点登录",
        "osixia/openldap:latest|OpenLDAP 目录服务|安全,ldap,目录",
        "clamav/clamav:latest|ClamAV 病毒扫描|安全,杀毒,扫描",
        "aquasec/trivy:latest|Trivy 镜像漏洞扫描|安全,漏洞,扫描",
        "falcosecurity/falco:latest|Falco 运行时安全监控|安全,运行时,监控",
        "wazuh/wazuh-manager:latest|Wazuh 主机入侵检测|安全,入侵检测,soc",

        // ── 网络 / 代理 / 网关 ──
        "nginx:1.27-alpine|Nginx 1.27 的 Alpine 版|web,反向代理,轻量",
        "linuxserver/wireguard:latest|WireGuard VPN|网络,vpn,wireguard",
        "kylemanna/openvpn:latest|OpenVPN 服务端|网络,vpn,openvpn",
        "cloudflare/cloudflared:latest|Cloudflare Tunnel 内网穿透|网络,穿透,隧道,cloudflare",
        "adguard/adguardhome:latest|AdGuard Home DNS 去广告|网络,dns,去广告",
        "pihole/pihole:latest|Pi-hole DNS 广告拦截|网络,dns,去广告",
        "snowdreamtech/frps:latest|FRP 内网穿透服务端|网络,穿透,frp,国产",
        "snowdreamtech/frpc:latest|FRP 内网穿透客户端|网络,穿透,frp,国产",

        // ── 大数据 ──
        "apache/spark:latest|Apache Spark 计算引擎|大数据,spark,计算",
        "apache/flink:latest|Apache Flink 流计算|大数据,flink,流计算",
        "apache/hadoop:3|Apache Hadoop|大数据,hadoop,hdfs",
        "apache/hive:latest|Apache Hive 数据仓库|大数据,hive,数仓",
        "apache/airflow:latest|Airflow 工作流调度|大数据,调度,airflow",
        "apache/dolphinscheduler:latest|DolphinScheduler 调度平台|大数据,调度,国产",
        "apache/superset:latest|Superset 数据可视化|大数据,bi,可视化",
        "metabase/metabase:latest|Metabase 商业智能|大数据,bi,报表",
        "apache/druid:latest|Apache Druid 实时分析|大数据,olap,分析",

        // ── AI / 机器学习 ──
        "pytorch/pytorch:latest|PyTorch 深度学习框架|ai,机器学习,深度学习,pytorch",
        "tensorflow/tensorflow:latest|TensorFlow 深度学习框架|ai,机器学习,深度学习,tensorflow",
        "ollama/ollama:latest|Ollama 本地大模型运行|ai,大模型,llm,ollama",
        "vllm/vllm-openai:latest|vLLM 大模型推理服务|ai,大模型,llm,推理,vllm",
        "open-webui/open-webui:main|Open WebUI 大模型对话界面|ai,大模型,界面,llm",
        "nvidia/cuda:latest|NVIDIA CUDA 运行环境|ai,cuda,gpu,nvidia",
        "jupyter/base-notebook:latest|Jupyter Notebook 基础环境|ai,jupyter,notebook",
        "jupyter/datascience-notebook:latest|Jupyter 数据科学环境|ai,jupyter,数据科学",
        "huggingface/text-embeddings-inference:latest|HuggingFace 向量推理|ai,向量,embedding",

        // ── 国内常用中间件 ──
        "nacos/nacos-server:latest|Nacos 配置与注册中心|国产,配置,注册中心,nacos,微服务",
        "apolloconfig/apollo-configservice:latest|Apollo 配置中心|国产,配置中心,apollo,微服务",
        "xuxueli/xxl-job-admin:latest|XXL-JOB 分布式任务调度|国产,调度,任务,xxl-job",
        "bladex/sentinel-dashboard:latest|Sentinel 流量控制台|国产,限流,熔断,sentinel",
        "seataio/seata-server:latest|Seata 分布式事务|国产,分布式事务,seata",
        "apache/dubbo-admin:latest|Dubbo 服务治理控制台|国产,dubbo,微服务",
        "apache/shardingsphere-proxy:latest|ShardingSphere 分库分表代理|国产,分库分表,数据库",
        "canal/canal-server:latest|Canal 数据库增量订阅|国产,binlog,同步,canal",
        "mysql:8.0.36|MySQL 8.0.36|数据库,关系型,sql",
    };
}
