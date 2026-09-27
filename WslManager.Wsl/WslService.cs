using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.CompilerServices;
using WslManager.Helpers;

namespace WslManager.Wsl;

[StandardModule]
public sealed class WslService
{
	private static readonly Dictionary<string, DateTime> _startTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private static string _wslVersionCache = "";

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

	private static string Decode(byte[] bytes)
	{
		checked
		{
			string result;
			if (bytes == null || bytes.Length == 0)
			{
				result = "";
			}
			else
			{
				try
				{
					if (bytes.Length >= 2 && bytes[0] == byte.MaxValue && bytes[1] == 254)
					{
						result = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
					}
					else if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191)
					{
						result = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, 3, bytes.Length - 3);
					}
					else if (bytes.Length >= 4 && (bytes.Length & 1) == 0 && LooksLikeUtf16LeCjk(bytes))
					{
						result = Encoding.Unicode.GetString(bytes);
					}
					else
					{
						result = ((bytes.Length < 4 || bytes[1] != 0 || bytes[3] != 0) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes) : Encoding.Unicode.GetString(bytes));
					}
				}
				catch (Exception projectError)
				{
					ProjectData.SetProjectError(projectError);
					result = Encoding.Default.GetString(bytes);
					ProjectData.ClearProjectError();
				}
			}
			return result;
		}
	}

	private static async Task<WslResult> RunAsync(string[] args, int timeoutMs = 30000, CancellationToken ct = default(CancellationToken), bool killOnTimeout = true)
	{
		WslResult wslResult = new WslResult();
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = "wsl.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (string item in args)
		{
			processStartInfo.ArgumentList.Add(item);
		}
		Process p = new Process();
		try
		{
			p.StartInfo = processStartInfo;
			try
			{
				if (!p.Start())
				{
					wslResult.ErrorMessage = "无法启动 wsl.exe";
					return wslResult;
				}
			}
			catch (Win32Exception ex)
			{
				wslResult.ErrorMessage = "未检测到 WSL（wsl.exe 不可用）：" + ex.Message;
				return wslResult;
			}
			catch (Exception ex2)
			{
				wslResult.ErrorMessage = ex2.Message;
				return wslResult;
			}
			Task<byte[]> outTask = ReadAllAsync(p.StandardOutput.BaseStream, ct);
			Task<byte[]> errTask = ReadAllAsync(p.StandardError.BaseStream, ct);
			if (timeoutMs <= 0)
			{
				await Task.Run(() => p.WaitForExit());
			}
			else if (!await Task.Run(() => p.WaitForExit(timeoutMs)))
			{
				if (killOnTimeout)
				{
					try
					{
						p.Kill(entireProcessTree: true);
					}
					catch
					{
					}
				}
				wslResult.ErrorMessage = "命令超时";
				wslResult.ExitCode = -2;
				return wslResult;
			}
			wslResult.StdOut = Decode(await outTask);
			wslResult.StdErr = Decode(await errTask);
			wslResult.ExitCode = p.ExitCode;
			wslResult.Success = p.ExitCode == 0;
		}
		finally
		{
			p.Dispose();
		}
		return wslResult;
	}
private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
	{
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array = new byte[8192];
		int num;
		do
		{
			num = await stream.ReadAsync(array, 0, array.Length, ct);
			if (num <= 0)
			{
				break;
			}
			memoryStream.Write(array, 0, num);
		}
		while (num > 0);
		return memoryStream.ToArray();
	}

	public static async Task<List<Distro>> ListDistrosAsync()
	{
		List<Distro> list = new List<Distro>();
		WslResult wslResult = await RunAsync(new string[2] { "-l", "-v" }, 20000);
		if (!wslResult.Success && string.IsNullOrWhiteSpace(wslResult.StdOut))
		{
			return list;
		}
		string[] array = wslResult.StdOut.Replace("\r\n", "\n").Split("\n"[0]);
		for (int i = 0; i < array.Length; i = checked(i + 1))
		{
			string text = array[i].TrimEnd("\r"[0]).TrimEnd();
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			text = text.TrimStart('\ufeff');
			if (text.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) || (text.Contains("STATE") && text.Contains("VERSION")))
			{
				continue;
			}
			bool isDefault = text.TrimStart().StartsWith("*");
			string text2 = text.TrimStart('*').Trim();
			if (text2.Length == 0)
			{
				continue;
			}
			string[] array2 = Regex.Split(text2, "\\s{2,}");
			if (array2.Length == 1)
			{
				array2 = Regex.Split(text2, "\\s+");
			}
			if (array2.Length >= 2)
			{
				Distro distro = new Distro
				{
					Name = array2[0].Trim(),
					State = ((array2.Length > 1) ? array2[1].Trim() : "Unknown"),
					IsDefault = isDefault
				};
				if (array2.Length > 2 && int.TryParse(array2[2].Trim(), out var result))
				{
					distro.Version = result;
				}
				if (distro.Name.Length > 0)
				{
					list.Add(distro);
				}
			}
		}
		foreach (Distro item in list)
		{
			lock (_startTimes)
			{
				if (item.IsRunning)
				{
					if (!_startTimes.ContainsKey(item.Name))
					{
						_startTimes[item.Name] = DateTime.Now;
					}
				}
				else
				{
					_startTimes.Remove(item.Name);
				}
			}
		}
		return list;
	}

	public static async Task<WslResult> StartAsync(string name)
	{
		WslResult wslResult = await RunAsync(new string[3] { "-d", name, "true" }, 60000);
		if (wslResult.Success)
		{
			lock (_startTimes)
			{
				_startTimes[name] = DateTime.Now;
			}
			StartKeepAlive(name);
		}
		return wslResult;
	}

	private static void StartKeepAlive(string name)
	{
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = "wsl.exe",
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			processStartInfo.ArgumentList.Add("-d");
			processStartInfo.ArgumentList.Add(name);
			processStartInfo.ArgumentList.Add("--exec");
			processStartInfo.ArgumentList.Add("sleep");
			processStartInfo.ArgumentList.Add("8640000");
			Process process = new Process();
			process.StartInfo = processStartInfo;
			process.Start();
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}

	private static async Task<bool> TryGracefulShutdownAsync(string name)
	{
		string[] array = new string[2] { "/sbin/shutdown", "/usr/sbin/shutdown" };
		foreach (string text in array)
		{
			WslResult wslResult = await RunAsync(new string[8] { "-d", name, "-u", "root", "--", text, "-h", "now" }, 8000, default, killOnTimeout: false);
			if (wslResult.ExitCode == 0)
			{
				SimpleLog.Write($"软关机指令已送达（{text}，rc=0）：发行版将优雅停机");
				return true;
			}
			if (wslResult.ExitCode == -2)
			{
				SimpleLog.Write($"软关机指令已送出（{text}，等待 VM 停止）");
				return true;
			}
			SimpleLog.Write($"关机指令 {text} 失败（rc={wslResult.ExitCode}）：{wslResult.StdErr.Trim()}");
		}
		return false;
	}

	public static async Task<WslResult> StopAsync(string name, bool graceful = true, int gracefulTimeoutMs = 20000, CancellationToken ct = default(CancellationToken))
	{
		Distro distro = (await ListDistrosAsync()).FirstOrDefault([SpecialName] (Distro x) => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
		if (distro == null || !distro.IsRunning)
		{
			lock (_startTimes)
			{
				_startTimes.Remove(name);
			}
			return new WslResult
			{
				ExitCode = 0,
				Success = true,
				Graceful = true,
				StdOut = "已经是停止状态"
			};
		}
		if (graceful)
		{
			if (await TryGracefulShutdownAsync(name))
			{
				int num = 0;
				while (num < gracefulTimeoutMs)
				{
					await Task.Delay(500);
					num = checked(num + 500);
					Distro distro2 = (await ListDistrosAsync()).FirstOrDefault([SpecialName] (Distro x) => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
					if (distro2 == null || !distro2.IsRunning)
					{
						lock (_startTimes)
						{
							_startTimes.Remove(name);
						}
						SimpleLog.Write($"软关机完成：{name}（{(double)num / 1000.0:F1}s）");
						return new WslResult
						{
							ExitCode = 0,
							Success = true,
							Graceful = true,
							StdOut = "优雅关机完成"
						};
					}
				}
				SimpleLog.Write($"软关机超时（{gracefulTimeoutMs}ms），转为强制终止：{name}");
			}
			else
			{
				SimpleLog.Write($"软关机指令未送达，转为强制终止：{name}");
			}
			try
			{
				await RunAsync(new string[6] { "-d", name, "-u", "root", "--", "sync" }, 8000, ct);
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				ProjectData.ClearProjectError();
			}
		}
		WslResult wslResult = await RunAsync(new string[2] { "-t", name }, 60000, ct);
		wslResult.Forced = true;
		lock (_startTimes)
		{
			_startTimes.Remove(name);
		}
		return wslResult;
	}

	public static async Task<WslResult> TerminateAsync(string name)
	{
		WslResult wslResult = await RunAsync(new string[2] { "-t", name }, 60000);
		wslResult.Forced = true;
		lock (_startTimes)
		{
			_startTimes.Remove(name);
			return wslResult;
		}
	}

	public static async Task<WslResult> RestartAsync(string name, bool graceful = true)
	{
		await StopAsync(name, graceful);
		await Task.Delay(600);
		return await StartAsync(name);
	}

	public static async Task<WslResult> ShutdownAllAsync(bool graceful = true)
	{
		if (graceful)
		{
			List<Distro> list = (await ListDistrosAsync()).Where([SpecialName] (Distro d) => d.IsRunning).ToList();
			if (list.Count > 0)
			{
				foreach (Distro item in list)
				{
					await TryGracefulShutdownAsync(item.Name);
				}
				await Task.Delay(5000);
			}
		}
		WslResult result = await RunAsync(new string[1] { "--shutdown" });
		lock (_startTimes)
		{
			_startTimes.Clear();
			return result;
		}
	}

	public static async Task<WslResult> SetDefaultAsync(string name)
	{
		return await RunAsync(new string[2] { "-s", name });
	}

	public static async Task<string> GetIpAsync(string name)
	{
		WslResult wslResult = await RunAsync(new string[5] { "-d", name, "--", "hostname", "-I" }, 15000);
		if (!wslResult.Success)
		{
			return "";
		}
		string text = wslResult.StdOut.Trim();
		if (text.Length == 0)
		{
			return "";
		}
		return text.Split(' ')[0].Trim();
	}

	public static async Task<string> GetWslVersionAsync()
	{
		if (_wslVersionCache.Length > 0)
		{
			return _wslVersionCache;
		}
		WslResult wslResult = await RunAsync(new string[1] { "--version" }, 15000);
		if (wslResult.Success && wslResult.StdOut.Length > 0)
		{
			string[] array = wslResult.StdOut.Replace("\r\n", "\n").Split("\n"[0]);
			for (int i = 0; i < array.Length; i = checked(i + 1))
			{
				string text = array[i].Trim();
				if (text.StartsWith("WSL version", StringComparison.OrdinalIgnoreCase) || text.StartsWith("WSL 版本", StringComparison.OrdinalIgnoreCase))
				{
					_wslVersionCache = text;
					break;
				}
			}
			if (_wslVersionCache.Length == 0)
			{
				_wslVersionCache = wslResult.StdOut.Replace("\r\n", "\n").Split("\n"[0])[0].Trim();
			}
		}
		else
		{
			_wslVersionCache = "已安装";
		}
		return _wslVersionCache;
	}

	public static DateTime? GetStartTime(string name)
	{
		lock (_startTimes)
		{
			if (_startTimes.ContainsKey(name))
			{
				return _startTimes[name];
			}
		}
		return null;
	}

	public static void OpenTerminal(string name)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "wt.exe",
				Arguments = "wsl.exe -d " + name,
				UseShellExecute = true
			});
			return;
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "wsl.exe",
				Arguments = "-d " + name,
				UseShellExecute = true
			});
		}
		catch (Exception projectError2)
		{
			ProjectData.SetProjectError(projectError2);
			ProjectData.ClearProjectError();
		}
	}

	public static void OpenFiles(string name)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "explorer.exe",
				Arguments = "\\\\wsl$\\" + name,
				UseShellExecute = true
			});
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}
}
