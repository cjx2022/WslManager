using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WslManager.Helpers;
using WslManager.Wsl;

namespace WslManager.Install;

public class OnlineDistro
{
	public string Name { get; set; } = "";

	public string FriendlyName { get; set; } = "";

	public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? Name : $"{Name}（{FriendlyName}）";
}

public static class WslInstallerService
{
	public static string DownloadDir => DownloadService.DownloadDir;

	public static string DistroRootDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"WslManager", "distros");

	private static string Decode(byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
			return "";
		try
		{
			if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
				return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
			if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
				return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
			if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
			int n = Math.Min(bytes.Length, 8192);
			int even = 0;
			int odd = 0;
			for (int i = 0; i < n; i++)
			{
				if (bytes[i] == 0)
				{
					if ((i & 1) == 0)
						even++;
					else
						odd++;
				}
			}
			if (odd > n * 0.2 && odd > even * 2)
				return Encoding.Unicode.GetString(bytes);
			if (even > n * 0.2 && even > odd * 2)
				return Encoding.BigEndianUnicode.GetString(bytes);
			if (LooksLikeUtf16LeCjk(bytes))
				return Encoding.Unicode.GetString(bytes);
			return new UTF8Encoding(false, true).GetString(bytes);
		}
		catch
		{
			try
			{
				return Encoding.GetEncoding(936).GetString(bytes);
			}
			catch
			{
				return Encoding.Default.GetString(bytes);
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

	private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
	{
		using var ms = new MemoryStream();
		var buf = new byte[8192];
		int n;
		while ((n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
			ms.Write(buf, 0, n);
		return ms.ToArray();
	}

	public static async Task<WslResult> RunAsync(string[] args, int timeoutMs = 30000, CancellationToken ct = default)
	{
		var result = new WslResult();
		var psi = new ProcessStartInfo
		{
			FileName = "wsl.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var a in args)
			psi.ArgumentList.Add(a);

		using var p = new Process { StartInfo = psi };
		try
		{
			if (!p.Start())
			{
				result.ErrorMessage = "无法启动 wsl.exe";
				return result;
			}
		}
		catch (Exception ex)
		{
			result.ErrorMessage = "未检测到 WSL（wsl.exe 不可用）：" + ex.Message;
			return result;
		}

		using var killReg = ct.Register(() =>
		{
			try
			{
				if (!p.HasExited)
					p.Kill(entireProcessTree: true);
			}
			catch
			{
			}
		});
		var outTask = ReadAllAsync(p.StandardOutput.BaseStream, ct);
		var errTask = ReadAllAsync(p.StandardError.BaseStream, ct);
		if (timeoutMs <= 0)
		{
			await Task.Run(() => p.WaitForExit(), ct).ConfigureAwait(false);
		}
		else if (!await Task.Run(() => p.WaitForExit(timeoutMs), ct).ConfigureAwait(false))
		{
			try
			{
				p.Kill(entireProcessTree: true);
			}
			catch
			{
			}
			result.ErrorMessage = "命令超时";
			result.ExitCode = -2;
			return result;
		}
		result.StdOut = Decode(await outTask.ConfigureAwait(false));
		result.StdErr = Decode(await errTask.ConfigureAwait(false));
		result.ExitCode = p.ExitCode;
		result.Success = p.ExitCode == 0;
		SimpleLog.Write($"wsl {string.Join(" ", args)} → 退出码={result.ExitCode}");
		return result;
	}

	public static async Task<List<OnlineDistro>> ListOnlineAsync(CancellationToken ct = default)
	{
		var list = new List<OnlineDistro>();
		var r = await RunAsync(new[] { "--list", "--online" }, 60000, ct).ConfigureAwait(false);
		var text = string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
		foreach (var raw in (text ?? "").Split('\n'))
		{
			var line = raw.TrimEnd('\r', ' ').TrimStart(' ');
			if (line.Length == 0)
				continue;
			var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
				continue;
			var name = parts[0].Trim();
			if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9._-]*$"))
				continue;
			if (name.Equals("NAME", StringComparison.OrdinalIgnoreCase))
				continue;
			list.Add(new OnlineDistro
			{
				Name = name,
				FriendlyName = parts.Length > 1 ? parts[1].Trim() : ""
			});
		}
		return list;
	}

	public static async Task<WslResult> InstallOnlineAsync(string distroName, CancellationToken ct = default)
	{
		return await RunAsync(new[] { "--install", "-d", distroName, "--no-launch" }, 1800000, ct).ConfigureAwait(false);
	}

	public static async Task<WslResult> InstallFromFileAsync(string filePath, CancellationToken ct = default)
	{
		return await RunAsync(new[] { "--install", "--from-file", filePath }, 1800000, ct).ConfigureAwait(false);
	}

	public static async Task<WslResult> ImportAsync(string name, string installDir, string filePath, CancellationToken ct = default)
	{
		Directory.CreateDirectory(installDir);
		return await RunAsync(new[] { "--import", name, installDir, filePath, "--version", "2" }, 1800000, ct).ConfigureAwait(false);
	}

	public static async Task<WslResult> ExportAsync(string name, string filePath, CancellationToken ct = default)
	{
		var dir = Path.GetDirectoryName(filePath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);
		return await RunAsync(new[] { "--export", name, filePath }, 3600000, ct).ConfigureAwait(false);
	}

	public static async Task<WslResult> UnregisterAsync(string name, CancellationToken ct = default)
	{
		return await RunAsync(new[] { "--unregister", name }, 600000, ct).ConfigureAwait(false);
	}

	public static async Task<WslResult> SetDefaultVersionAsync(int version, CancellationToken ct = default)
	{
		return await RunAsync(new[] { "--set-default-version", version.ToString() }, 60000, ct).ConfigureAwait(false);
	}

	public static string SafeName(string input)
	{
		var sb = new StringBuilder();
		foreach (var c in input ?? "")
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
				sb.Append(c);
		}
		return sb.Length > 0 ? sb.ToString() : "NewDistro";
	}
}
