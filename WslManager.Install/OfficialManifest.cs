using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WslManager.Helpers;

namespace WslManager.Install;

/// <summary>微软官方发行版清单（DistributionInfo.json）里的一条。</summary>
public class OfficialDistro
{
	public string Group { get; set; } = "";

	public string Name { get; set; } = "";

	public string FriendlyName { get; set; } = "";

	/// <summary>安装包直链（.wsl），由微软清单给出。</summary>
	public string Url { get; set; } = "";

	/// <summary>微软给出的官方 sha256，用于校验下载到的包。</summary>
	public string Sha256 { get; set; } = "";

	public bool Default { get; set; }

	public string Display => string.IsNullOrWhiteSpace(FriendlyName) ? Name : FriendlyName;
}

/// <summary>
/// 微软官方发行版清单的获取与缓存。
///
/// 为什么要自己取：<c>wsl --install -d &lt;名字&gt;</c> 和 <c>wsl --list --online</c> 都依赖
/// <c>raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json</c>，
/// 该域名在境内**直连不通**（实测 HTTP 000 / 20 秒超时），于是命令要么报错要么挂死。
/// 本类改为自己取这份清单、自己下载 .wsl、再交给 <c>wsl --install --from-file</c>，
/// 从而完全绕开 wsl.exe 对那个域名的依赖。
///
/// 三级数据来源，从可靠到兜底：
///   1. 内置快照（编译进 exe，零网络依赖）
///   2. 磁盘缓存（上次刷新成功的结果）
///   3. 网络多源刷新（直连 + 若干境内可达的 CDN/代理）
/// </summary>
public static class OfficialManifest
{
	/// <summary>取清单的候选源。直连排第一（通了就完全不需要第三方）。</summary>
	public static readonly string[] Sources =
	{
		"https://raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json",
		"https://cdn.jsdelivr.net/gh/microsoft/WSL@master/distributions/DistributionInfo.json",
		"https://gh-proxy.com/https://raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json",
		"https://ghfast.top/https://raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json",
		"https://api.gitproxy.dev/raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json"
	};

	/// <summary>下载 github.com 上的安装包时套用的加速器（前缀 + 完整 URL）。</summary>
	public static readonly (string Label, string Prefix)[] GithubAccelerators =
	{
		("gh-proxy 加速", "https://gh-proxy.com/"),
		("ghfast 加速", "https://ghfast.top/"),
		("gitproxy 加速", "https://api.gitproxy.dev/")
	};

	private const string ResourceName = "official-manifest.snapshot.json";

	private static List<OfficialDistro> _cache;

	/// <summary>本次数据来自哪里（内置快照 / 磁盘缓存 / 网络源名）。</summary>
	public static string LastSource { get; private set; } = "（未加载）";

	/// <summary>清单内容自身的 sha256（用于多源一致性校验与展示）。</summary>
	public static string LastContentSha { get; private set; } = "";

	private static string CachePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"WslManager", "official-manifest.json");

	/// <summary>
	/// 同步取清单：内存 → 磁盘缓存 → 内置快照。**不联网**。
	/// </summary>
	public static List<OfficialDistro> Load()
	{
		if (_cache != null)
			return _cache;

		var disk = TryReadFile(CachePath, "磁盘缓存");
		if (disk != null && disk.Count > 0)
		{
			_cache = disk;
			LastSource = "磁盘缓存";
			return _cache;
		}

		var builtin = LoadBuiltin();
		_cache = builtin;
		LastSource = "内置快照";
		return _cache;
	}

	private static List<OfficialDistro> LoadBuiltin()
	{
		try
		{
			var asm = Assembly.GetExecutingAssembly();
			using Stream s = asm.GetManifestResourceStream(ResourceName);
			if (s == null)
				return new List<OfficialDistro>();
			using var sr = new StreamReader(s);
			return Parse(sr.ReadToEnd(), out string sha);
		}
		catch (Exception ex)
		{
			SimpleLogSafe("读取内置清单快照失败：" + ex.Message);
			return new List<OfficialDistro>();
		}
	}

	private static List<OfficialDistro> TryReadFile(string path, string tag)
	{
		try
		{
			if (!File.Exists(path))
				return null;
			string text = File.ReadAllText(path);
			var list = Parse(text, out string sha);
			if (list.Count > 0)
				LastContentSha = sha;
			return list;
		}
		catch (Exception ex)
		{
			SimpleLogSafe($"读取{tag}失败：{ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// 联网刷新：按顺序试 <see cref="Sources" />，**收集全部成功结果并做多源一致性校验**。
	/// 至少两个来源给出相同内容才落盘（防单点投毒）；只有一个来源成功时也接受，
	/// 但会在日志里标注"单源未交叉校验"。
	/// </summary>
	public static async Task<(List<OfficialDistro> List, string Message)> RefreshAsync(
		CancellationToken ct = default)
	{
		var results = new List<(string Name, string Sha, List<OfficialDistro> List)>();
		var errors = new List<string>();

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WslManager/1.1");

		foreach (string src in Sources)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				string body = await http.GetStringAsync(src, ct).ConfigureAwait(false);
				var list = Parse(body, out string sha);
				if (list.Count == 0)
				{
					errors.Add(ShortName(src) + ":解析为空");
					continue;
				}
				results.Add((ShortName(src), sha, list));
			}
			catch (Exception ex)
			{
				errors.Add(ShortName(src) + ":" + ex.GetType().Name);
			}
		}

		if (results.Count == 0)
			return (Load(), "全部来源均失败（" + string.Join("，", errors) + "）");

		// 多源一致性：取出现次数最多的那个哈希
		var best = results.GroupBy(r => r.Sha)
			.OrderByDescending(g => g.Count())
			.ThenByDescending(g => g.First().List.Count)
			.First();

		int agree = best.Count();
		string sha2 = best.Key;
		var list2 = best.First().List;
		string srcName = string.Join("+", best.Select(x => x.Name));

		LastContentSha = sha2;
		LastSource = srcName;
		_cache = list2;

		string note = agree >= 2
			? $"多源一致（{agree} 个来源）"
			: "单源未交叉校验";
		SaveCache(list2, sha2);

		return (list2, $"清单已更新：{list2.Count} 个发行版，来自 {srcName}（{note}）");
	}

	private static void SaveCache(List<OfficialDistro> list, string sha)
	{
		try
		{
			string dir = Path.GetDirectoryName(CachePath);
			Directory.CreateDirectory(dir);
			File.WriteAllText(CachePath, Serialize(list, sha));
		}
		catch (Exception ex)
		{
			SimpleLogSafe("写清单缓存失败：" + ex.Message);
		}
	}

	private static string ShortName(string url)
	{
		try
		{
			string host = new Uri(url).Host;
			if (host.Contains("jsdelivr"))
				return "jsDelivr";
			if (host.Contains("gh-proxy"))
				return "gh-proxy";
			if (host.Contains("ghfast"))
				return "ghfast";
			if (host.Contains("gitproxy"))
				return "gitproxy";
			if (host.Contains("githubusercontent"))
				return "直连";
			return host;
		}
		catch
		{
			return "未知源";
		}
	}

	// ---------------------------------------------------------------- 解析 / 序列化

	/// <summary>
	/// 解析微软清单或本项目的精简快照。两种格式都认得：
	///   - 微软原版：{ ModernDistributions: { 分组: [ {Name, FriendlyName, Amd64Url:{Url,Sha256}} ] } }
	///   - 本项目快照：{ Distributions: [ {Group, Name, FriendlyName, Url, Sha256, Default} ] }
	/// </summary>
	public static List<OfficialDistro> Parse(string json, out string contentSha)
	{
		contentSha = Sha256Hex(json);
		var list = new List<OfficialDistro>();
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			// 本项目精简快照格式
			if (root.TryGetProperty("Distributions", out var simple)
				&& simple.ValueKind == JsonValueKind.Array
				&& simple.GetArrayLength() > 0
				&& simple[0].TryGetProperty("Url", out _))
			{
				foreach (var it in simple.EnumerateArray())
				{
					var d = new OfficialDistro
					{
						Group = Str(it, "Group"),
						Name = Str(it, "Name"),
						FriendlyName = Str(it, "FriendlyName"),
						Url = Str(it, "Url"),
						Sha256 = Str(it, "Sha256")
					};
					d.Default = it.TryGetProperty("Default", out var dv)
						&& dv.ValueKind == JsonValueKind.True;
					if (d.Name.Length > 0 && d.Url.Length > 0)
						list.Add(d);
				}
				return list;
			}

			// 微软原版格式
			if (!root.TryGetProperty("ModernDistributions", out var md)
				|| md.ValueKind != JsonValueKind.Object)
				return list;

			foreach (var grp in md.EnumerateObject())
			{
				if (grp.Value.ValueKind != JsonValueKind.Array)
					continue;
				foreach (var it in grp.Value.EnumerateArray())
				{
					string url = "", sha = "";
					if (it.TryGetProperty("Amd64Url", out var amd)
						&& amd.ValueKind == JsonValueKind.Object)
					{
						url = Str(amd, "Url");
						sha = Str(amd, "Sha256");
					}
					var d = new OfficialDistro
					{
						Group = grp.Name,
						Name = Str(it, "Name"),
						FriendlyName = Str(it, "FriendlyName"),
						Url = url,
						Sha256 = sha
					};
					d.Default = it.TryGetProperty("Default", out var dv2)
						&& dv2.ValueKind == JsonValueKind.True;
					if (d.Name.Length > 0 && d.Url.Length > 0)
						list.Add(d);
				}
			}
		}
		catch (Exception ex)
		{
			SimpleLogSafe("解析发行版清单失败：" + ex.Message);
		}
		return list;
	}

	private static string Str(JsonElement e, string name)
		=> e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
			? v.GetString() ?? ""
			: "";

	public static string Serialize(List<OfficialDistro> list, string sha)
	{
		var payload = new
		{
			Source = "microsoft/WSL distributions/DistributionInfo.json",
			FetchedVia = LastSource,
			ContentSha256 = sha ?? "",
			Distributions = list.Select(d => new
			{
				d.Group,
				d.Name,
				d.FriendlyName,
				d.Default,
				d.Url,
				d.Sha256
			}).ToList()
		};
		return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
	}

	public static string Sha256Hex(string text)
	{
		try
		{
			using var sha = System.Security.Cryptography.SHA256.Create();
			byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
			return string.Concat(h.Select(b => b.ToString("x2")));
		}
		catch
		{
			return "";
		}
	}

	// ---------------------------------------------------------------- 下载源

	public static bool IsGithubUrl(string url)
		=> !string.IsNullOrEmpty(url)
			&& url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 把一个官方直链展开成候选下载源列表：直连优先，github.com 的再加几个境内加速器。
	/// 交给现有的「测速 / 下载安装」流程去挑最快的。
	/// </summary>
	public static List<MirrorSource> BuildSources(OfficialDistro d)
	{
		var list = new List<MirrorSource>
		{
			new MirrorSource { Name = "微软官方直链", Location = "海外", Url = d.Url, IsOfficial = true }
		};
		if (IsGithubUrl(d.Url))
		{
			foreach (var acc in GithubAccelerators)
				list.Add(new MirrorSource { Name = acc.Label, Location = "境内", Url = acc.Prefix + d.Url });
		}
		return list;
	}

	private static void SimpleLogSafe(string msg)
	{
		try { SimpleLog.Write("[OfficialManifest] " + msg); } catch { }
	}
}
