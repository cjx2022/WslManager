using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WslManager.Install;

public static class EnvChecker
{
	static EnvChecker()
	{
		try
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		}
		catch
		{
		}
	}

	public static async Task<List<CheckItem>> RunAsync(Action<string> status = null)
	{
		var items = new List<CheckItem>();

		status?.Invoke("检查 Windows 版本...");
		items.Add(await CheckWindowsVersionAsync().ConfigureAwait(false));

		status?.Invoke("检查 WSL 是否已安装...");
		items.Add(await CheckWslVersionAsync().ConfigureAwait(false));

		status?.Invoke("检查 WSL 状态...");
		items.Add(await CheckWslStatusAsync().ConfigureAwait(false));

		status?.Invoke("检查系统服务...");
		items.Add(CheckServices());

		status?.Invoke("检查虚拟化功能...");
		items.AddRange(await CheckVirtualizationAsync().ConfigureAwait(false));

		status?.Invoke("检查磁盘空间...");
		items.Add(CheckDiskSpace());

		status?.Invoke("检查管理员权限...");
		items.Add(CheckAdmin());

		status?.Invoke("检查网络连通性...");
		items.Add(await CheckNetworkAsync().ConfigureAwait(false));

		return items;
	}

	private static CheckItem Make(string name, string detail, CheckStatus status, string url = "", string docName = "")
		=> new CheckItem
		{
			Name = name,
			Detail = detail,
			Status = status.ToString(),
			DocUrl = url,
			DocName = docName
		};

	private static Task<CheckItem> CheckWindowsVersionAsync()
	{
		var ver = Environment.OSVersion.Version;
		var build = ver.Build;
		string url = DocLinks.ManualInstall + "#step-1---enable-the-windows-subsystem-for-linux";
		CheckStatus st;
		string detail;
		if (build >= 22000)
		{
			st = CheckStatus.Ok;
			detail = $"Windows 11（build {build}，支持 WSL 2）";
		}
		else if (build >= 19041)
		{
			st = CheckStatus.Warn;
			detail = $"Windows build {build}（支持 WSL 2，建议升级到 Windows 11）";
		}
		else
		{
			st = CheckStatus.Fail;
			detail = $"Windows build {build} 过旧，不满足 WSL 2 最低要求（需 19041+）";
		}
		return Task.FromResult(Make("Windows 版本", detail, st, url, "WSL 安装要求"));
	}

	private static async Task<CheckItem> CheckWslVersionAsync()
	{
		string url = DocLinks.BasicCommands + "#check-wsl-version";
		try
		{
			var r = await Task.Run(() => RunCapture("wsl", "--version")).ConfigureAwait(false);
			if (r.Code != 0 || string.IsNullOrWhiteSpace(r.Out))
			{
				return Make("WSL 组件", "未检测到 WSL（wsl --version 无输出）", CheckStatus.Fail,
					DocLinks.Install + "#how-to-install-linux-on-windows-with-wsl", "安装 WSL");
			}
			var text = Decode(r.Raw);
			var m = Regex.Match(text, @"WSL\s*(?:version|版本)\s*[:：]?\s*([0-9]+\.[0-9.]+)", RegexOptions.IgnoreCase);
			var ver = m.Success ? m.Groups[1].Value : FirstNumber(text);
			return Make("WSL 组件", ver.Length > 0 ? $"WSL 版本 {ver}" : $"WSL 已安装（版本解析失败：{Trunc(text)}）", CheckStatus.Ok, url, "检查 WSL 版本");
		}
		catch (Exception ex)
		{
			return Make("WSL 组件", "执行 wsl --version 失败: " + ex.Message, CheckStatus.Fail, url, "检查 WSL 版本");
		}
	}

	private static async Task<CheckItem> CheckWslStatusAsync()
	{
		string url = DocLinks.BasicCommands + "#check-wsl-status";
		try
		{
			var r = await Task.Run(() => RunCapture("wsl", "--status")).ConfigureAwait(false);
			var text = Decode(r.Raw);
			if (r.Code != 0 && string.IsNullOrWhiteSpace(text))
			{
				return Make("WSL 状态", "wsl --status 无输出（WSL 可能未安装）", CheckStatus.Warn, url, "检查 WSL 状态");
			}
			var def = Regex.Match(text, @"(?:默认版本|Default Version)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
			var ver = def.Success ? def.Groups[1].Value : "未知";
			var defDistro = Regex.Match(text, @"(?:默认发行版|Default Distribution)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
			var distro = defDistro.Success ? defDistro.Groups[1].Value : "";
			var ok = r.Code == 0;
			var detail = ok
				? $"默认 WSL 版本 {ver}{(distro.Length > 0 ? $"，默认发行版 {distro}" : "")}"
				: $"wsl --status 返回非零：{Trunc(text)}";
			return Make("WSL 状态", detail, ok ? CheckStatus.Ok : CheckStatus.Warn, url, "检查 WSL 状态");
		}
		catch (Exception ex)
		{
			return Make("WSL 状态", "执行 wsl --status 失败: " + ex.Message, CheckStatus.Warn, url, "检查 WSL 状态");
		}
	}

	private static CheckItem CheckServices()
	{
		string url = DocLinks.Troubleshooting + "#installation-issues";
		try
		{
			var names = new[] { "WslService", "LxssManager", "vmcompute" };
			var found = new List<string>();
			foreach (var n in names)
			{
				try
				{
					using var sc = new System.ServiceProcess.ServiceController(n);
					found.Add($"{n}={sc.Status}");
				}
				catch
				{
					found.Add($"{n}=缺失");
				}
			}
			bool coreOk = found.Any(x => x.StartsWith("WslService=Running") || x.StartsWith("LxssManager=Running"));
			bool hvOk = found.Any(x => x.StartsWith("vmcompute=Running"));
			var st = coreOk ? (hvOk ? CheckStatus.Ok : CheckStatus.Warn) : CheckStatus.Fail;
			return Make("系统服务", string.Join("，", found), st, url, "安装问题排查");
		}
		catch (Exception ex)
		{
			return Make("系统服务", "查询服务失败: " + ex.Message, CheckStatus.Warn, url, "安装问题排查");
		}
	}

	private static async Task<IEnumerable<CheckItem>> CheckVirtualizationAsync()
	{
		var list = new List<CheckItem>();
		string url = DocLinks.EnableVirtualization;

		// Windows 功能：虚拟机平台 / Hypervisor（需要管理员）
		if (!IsElevated())
		{
			list.Add(Make("Windows 功能", "需要管理员权限才能查询 Windows 功能（当前进程未提权）；请以管理员身份重新运行本程序后点「重新检查」",
				CheckStatus.Warn, DocLinks.ManualInstall + "#step-3---enable-virtual-machine-feature", "启用虚拟机平台"));
		}
		else
		{
			try
			{
				var feat = await Task.Run(() => RunCapture("dism", "/online /get-features /format:table")).ConfigureAwait(false);
				var text = Decode(feat.Raw);
				string Find(string key)
				{
					var m = Regex.Match(text, key + @"\s*\|\s*(\S+)", RegexOptions.IgnoreCase);
					return m.Success ? m.Groups[1].Value.Trim() : "未知";
				}
				bool IsOn(string v) => v.Equals("Enabled", StringComparison.OrdinalIgnoreCase) || v.Contains("启用") || v.Contains("已装");
				var vm = Find("VirtualMachinePlatform");
				var hl = Find("Microsoft-Windows-Subsystem-Linux");
				var st = IsOn(vm) && IsOn(hl) ? CheckStatus.Ok : (vm == "未知" && hl == "未知" ? CheckStatus.Warn : CheckStatus.Fail);
				list.Add(Make("Windows 功能",
					$"VirtualMachinePlatform={vm}，Microsoft-Windows-Subsystem-Linux={hl}",
					st, DocLinks.ManualInstall + "#step-3---enable-virtual-machine-feature", "启用虚拟机平台"));
			}
			catch (Exception ex)
			{
				list.Add(Make("Windows 功能", "DISM 查询失败: " + ex.Message, CheckStatus.Warn,
					DocLinks.ManualInstall + "#step-3---enable-virtual-machine-feature", "启用虚拟机平台"));
			}
		}

		// CPU 虚拟化（固件已启用 / 已被 hypervisor 占用）
		try
		{
			var info = await Task.Run(() =>
			{
				try
				{
					using var mc = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled, HypervisorPresent FROM Win32_Processor");
					foreach (ManagementObject o in mc.Get())
					{
						var v = SafeProp(o, "VirtualizationFirmwareEnabled");
						var h = SafeProp(o, "HypervisorPresent");
						return $"VirtualizationFirmwareEnabled={v}，HypervisorPresent={h}";
					}
					return "WMI 未返回 CPU 数据";
				}
				catch (Exception ex2)
				{
					try
					{
						var r = RunCapture("powershell",
							"-NoProfile -Command \"Get-WmiObject Win32_Processor | ForEach-Object { 'VF=' + $_.VirtualizationFirmwareEnabled + ';HP=' + $_.HypervisorPresent }\"");
						var t = (r.Out ?? "").Trim();
						if (t.Length > 0)
							return t;
						return "WMI 查询失败：" + ex2.Message;
					}
					catch (Exception ex3)
					{
						return "WMI 查询失败：" + ex2.Message + "；备用查询失败：" + ex3.Message;
					}
				}
			}).ConfigureAwait(false);
			CheckStatus st = info.IndexOf("HP=True", StringComparison.OrdinalIgnoreCase) >= 0
					|| info.IndexOf("HypervisorPresent=True", StringComparison.OrdinalIgnoreCase) >= 0
				? CheckStatus.Ok
				: (info.IndexOf("VF=True", StringComparison.OrdinalIgnoreCase) >= 0
					|| info.IndexOf("VirtualizationFirmwareEnabled=True", StringComparison.OrdinalIgnoreCase) >= 0
					? CheckStatus.Ok
					: CheckStatus.Warn);
			list.Add(Make("CPU 虚拟化", info, st, url, "启用硬件虚拟化"));
		}
		catch (Exception ex)
		{
			list.Add(Make("CPU 虚拟化", "查询失败: " + ex.Message, CheckStatus.Warn, url, "启用硬件虚拟化"));
		}

		return list;
	}

	private static CheckItem CheckDiskSpace()
	{
		string url = DocLinks.ManualInstall + "#step-2---check-requirements-for-running-wsl-2";
		try
		{
			var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
			var di = new DriveInfo(drive);
			var freeGb = di.AvailableFreeSpace / (1024.0 * 1024 * 1024);
			var st = freeGb >= 10 ? CheckStatus.Ok : (freeGb >= 4 ? CheckStatus.Warn : CheckStatus.Fail);
			return Make("磁盘空间", $"{drive} 可用 {freeGb:F1} GB（建议 ≥ 10 GB）", st, url, "WSL 2 要求");
		}
		catch (Exception ex)
		{
			return Make("磁盘空间", "查询失败: " + ex.Message, CheckStatus.Warn, url, "WSL 2 要求");
		}
	}

	private static bool IsElevated()
	{
		try
		{
			using var id = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	private static CheckItem CheckAdmin()
	{
		string url = DocLinks.ManualInstall;
		try
		{
			bool admin = IsElevated();
			return Make("管理员权限", admin ? "当前进程已提权" : "当前进程未提权（安装 WSL 需要管理员）",
				admin ? CheckStatus.Ok : CheckStatus.Warn, url, "手动安装 WSL");
		}
		catch (Exception ex)
		{
			return Make("管理员权限", "检查失败: " + ex.Message, CheckStatus.Warn, url, "手动安装 WSL");
		}
	}

	private static async Task<CheckItem> CheckNetworkAsync()
	{
		string url = DocLinks.Install + "#how-to-install-linux-on-windows-with-wsl";
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("WslManager/1.0");
			var sw = Stopwatch.StartNew();
			var resp = await http.GetAsync("https://mirrors.aliyun.com/alpine/", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
			sw.Stop();
			var st = (int)resp.StatusCode < 500 ? CheckStatus.Ok : CheckStatus.Fail;
			return Make("网络连通性", $"镜像源可达 HTTP {(int)resp.StatusCode}，耗时 {sw.ElapsedMilliseconds} ms", st, url, "安装 WSL");
		}
		catch (Exception ex)
		{
			return Make("网络连通性", "无法访问镜像源: " + ex.Message, CheckStatus.Fail, url, "安装 WSL");
		}
	}

	private static string Decode(byte[] raw)
	{
		if (raw == null || raw.Length == 0)
			return "";
		if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
			return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
		if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
			return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
		if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
			return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
		int n = Math.Min(raw.Length, 8192);
		int even = 0;
		int odd = 0;
		for (int i = 0; i < n; i++)
		{
			if (raw[i] == 0)
			{
				if ((i & 1) == 0)
					even++;
				else
					odd++;
			}
		}
		if (odd > n * 0.2 && odd > even * 2)
			return Encoding.Unicode.GetString(raw);
		if (even > n * 0.2 && even > odd * 2)
			return Encoding.BigEndianUnicode.GetString(raw);
		if (LooksLikeUtf16LeCjk(raw))
			return Encoding.Unicode.GetString(raw);
		try
		{
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(raw);
		}
		catch
		{
			try
			{
				return Encoding.GetEncoding(936).GetString(raw);
			}
			catch
			{
				return Encoding.Default.GetString(raw);
			}
		}
	}

	private static bool LooksLikeUtf16LeCjk(byte[] b)
	{
		if (b == null || b.Length < 4 || (b.Length & 1) != 0)
			return false;
		int high = 0;
		for (int i = 0; i < b.Length; i++)
		{
			if (b[i] >= 0x80)
				high++;
		}
		if (high * 8 <= b.Length)
			return false;
		int pairs = b.Length / 2;
		int ok = 0;
		for (int i = 0; i < pairs; i++)
		{
			int hi = b[i * 2 + 1];
			if (hi == 0 || hi == 0x30 || hi == 0xFF || (hi >= 0x4E && hi <= 0x9F) || (hi >= 0xD8 && hi <= 0xDF))
				ok++;
		}
		return ok > pairs * 0.7;
	}

	private static string SafeProp(ManagementObject o, string name)
	{
		try
		{
			var v = o[name];
			return v == null ? "未知" : v.ToString();
		}
		catch (Exception ex)
		{
			return "读取失败(" + ex.GetType().Name + ")";
		}
	}

	private static string Trunc(string s, int len = 60)
	{
		s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
		return s.Length <= len ? s : s.Substring(0, len) + "…";
	}

	private static string FirstNumber(string text)
	{
		var m = Regex.Match(text ?? "", @"\d+\.\d+(\.\d+)*");
		return m.Success ? m.Value : "";
	}

	private static (int Code, string Out, byte[] Raw) RunCapture(string exe, string args)
	{
		var psi = new ProcessStartInfo
		{
			FileName = exe,
			Arguments = args,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		using var p = Process.Start(psi);
		if (p == null)
			return (-1, "", Array.Empty<byte>());
		var stdout = new MemoryStream();
		p.StandardOutput.BaseStream.CopyTo(stdout);
		var err = p.StandardError.ReadToEnd();
		p.WaitForExit(30000);
		var raw = stdout.ToArray();
		var text = Decode(raw);
		if (string.IsNullOrEmpty(text))
			text = err;
		return (p.ExitCode, text, raw);
	}
}
