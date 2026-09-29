using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WslManager.Install;

/// <summary>单项修复的执行结果。</summary>
public class FixResult
{
	public bool Success { get; set; }

	/// <summary>给用户看的一句话结论。</summary>
	public string Message { get; set; } = "";

	/// <summary>原始输出（可能较长），用于日志或详情。</summary>
	public string Raw { get; set; } = "";

	/// <summary>是否需要重启电脑才生效。</summary>
	public bool NeedReboot { get; set; }
}

/// <summary>
/// 按 <see cref="FixKind" /> 执行对应的修复动作。
/// 所有方法都不抛异常，失败信息通过 <see cref="FixResult" /> 返回。
/// </summary>
public static class FixService
{
	/// <summary>依据检查项的 Fix 类型执行修复。</summary>
	public static async Task<FixResult> ApplyAsync(CheckItem item, Action<string> progress = null)
	{
		if (item == null)
			return Fail("内部错误：检查项为空");

		switch (item.Fix)
		{
			case FixKind.InstallWsl:
				return await InstallWslAsync(progress).ConfigureAwait(false);

			case FixKind.StartService:
				return await StartServicesAsync(progress).ConfigureAwait(false);

			case FixKind.EnableFeature:
				return await EnableFeatureAsync(item, progress).ConfigureAwait(false);

			case FixKind.Elevate:
				return Fail("请关闭本程序，右键选择「以管理员身份运行」后重新检查。");

			default:
				return Fail("该项无法自动修复，请参考右侧文档手动处理。");
		}
	}

	// ---------------- 安装 / 更新 WSL ----------------

	private static async Task<FixResult> InstallWslAsync(Action<string> progress)
	{
		progress?.Invoke("正在安装 WSL 组件（可能需要几分钟）…");

		// 优先 wsl --install --no-distribution：只装组件不装发行版
		var r = await Task.Run(() => Run("wsl", "--install --no-distribution", 600)).ConfigureAwait(false);
		var text = (r.Out + "\n" + r.Err).Trim();

		if (r.Code == 0)
		{
			return new FixResult
			{
				Success = true,
				Message = "WSL 组件安装完成。若首次安装，需要重启电脑后生效。",
				Raw = text,
				NeedReboot = LooksLikeNeedReboot(text) || !WslWorks()
			};
		}

		// 回退：老版本 wsl.exe 没有 --no-distribution，用 --install 不带发行版
		progress?.Invoke("改用兼容方式安装…");
		var r2 = await Task.Run(() => Run("wsl", "--install", 600)).ConfigureAwait(false);
		var text2 = (r2.Out + "\n" + r2.Err).Trim();

		if (r2.Code == 0)
		{
			return new FixResult
			{
				Success = true,
				Message = "WSL 组件安装完成。若首次安装，需要重启电脑后生效。",
				Raw = text2,
				NeedReboot = LooksLikeNeedReboot(text2) || !WslWorks()
			};
		}

		// 再回退：启用 Windows 功能（虚拟机平台 + WSL）
		progress?.Invoke("安装命令不可用，改为启用 Windows 功能…");
		var feat = await EnableFeatureInternalAsync(progress).ConfigureAwait(false);
		if (feat.Success)
			return feat;

		return new FixResult
		{
			Success = false,
			Message = "自动安装失败，请在管理员终端里手动执行：wsl --install",
			Raw = "第一次尝试：\n" + text + "\n\n第二次尝试：\n" + text2 + "\n\n启用功能：\n" + feat.Raw
		};
	}

	// ---------------- 启动系统服务 ----------------

	private static async Task<FixResult> StartServicesAsync(Action<string> progress)
	{
		var names = new[] { "WslService", "LxssManager", "vmcompute" };
		var sb = new StringBuilder();
		var started = new List<string>();
		var failed = new List<string>();

		foreach (var n in names)
		{
			progress?.Invoke($"正在启动服务 {n}…");
			var msg = await Task.Run(() => StartOneService(n)).ConfigureAwait(false);
			sb.AppendLine(n + ": " + msg);
			if (msg.StartsWith("已启动") || msg.StartsWith("已在运行"))
				started.Add(n);
			else
				failed.Add(n + "（" + msg + "）");
		}

		if (started.Count > 0 && failed.Count == 0)
		{
			return new FixResult
			{
				Success = true,
				Message = "相关服务已全部启动：" + string.Join("、", started),
				Raw = sb.ToString()
			};
		}

		if (started.Count > 0)
		{
			return new FixResult
			{
				Success = true,
				Message = $"已启动 {string.Join("、", started)}；以下服务启动失败：{string.Join("；", failed)}",
				Raw = sb.ToString()
			};
		}

		return new FixResult
		{
			Success = false,
			Message = "服务启动失败。若提示「拒绝访问」，请以管理员身份重新运行本程序。" + string.Join("；", failed),
			Raw = sb.ToString()
		};
	}

	private static string StartOneService(string name)
	{
		try
		{
			using var sc = new ServiceController(name);

			if (sc.Status == ServiceControllerStatus.Running)
				return "已在运行";

			// 服务被禁用时先恢复为手动启动
			try
			{
				var startMode = sc.StartType;
				if (startMode == ServiceStartMode.Disabled)
				{
					Run("sc", $"config {name} start= demand", 20);
				}
			}
			catch
			{
			}

			sc.Start();
			string waited;
			try
			{
				sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
				waited = sc.Status == ServiceControllerStatus.Running
					? "已启动"
					: $"已发出启动请求，当前状态 {sc.Status}";
			}
			catch (System.ServiceProcess.TimeoutException)
			{
				waited = $"已发出启动请求，15 秒内未就绪（当前 {sc.Status}）";
			}
			return waited;
		}
		catch (InvalidOperationException ex)
		{
			return "服务不存在或不可用：" + ex.Message;
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return "拒绝访问（需要管理员权限）：" + ex.Message;
		}
		catch (Exception ex)
		{
			return "启动失败：" + ex.Message;
		}
	}

	// ---------------- 启用 Windows 功能 ----------------

	private static Task<FixResult> EnableFeatureAsync(CheckItem item, Action<string> progress)
		=> EnableFeatureInternalAsync(progress);

	private static async Task<FixResult> EnableFeatureInternalAsync(Action<string> progress)
	{
		var features = new[]
		{
			"Microsoft-Windows-Subsystem-Linux",
			"VirtualMachinePlatform"
		};

		var sb = new StringBuilder();
		bool anyOk = false;
		bool anyFail = false;

		foreach (var f in features)
		{
			progress?.Invoke($"正在启用 {f}…");
			var r = await Task.Run(() => Run("dism",
				$"/online /enable-feature /featurename:{f} /all /norestart", 900)).ConfigureAwait(false);
			var text = (r.Out + "\n" + r.Err).Trim();
			sb.AppendLine($"== {f} (exit={r.Code}) ==");
			sb.AppendLine(text);

			// 3010 = 成功但需重启
			if (r.Code == 0 || r.Code == 3010)
				anyOk = true;
			else if (text.IndexOf("已启用", StringComparison.Ordinal) >= 0
				|| text.IndexOf("The operation completed successfully", StringComparison.OrdinalIgnoreCase) >= 0)
				anyOk = true;
			else
				anyFail = true;
		}

		if (anyOk && !anyFail)
		{
			return new FixResult
			{
				Success = true,
				Message = "Windows 功能已启用。必须重启电脑后生效。",
				Raw = sb.ToString(),
				NeedReboot = true
			};
		}

		if (anyOk)
		{
			return new FixResult
			{
				Success = true,
				Message = "部分功能已启用，部分失败。建议重启后重新检查；若仍失败请以管理员身份运行。",
				Raw = sb.ToString(),
				NeedReboot = true
			};
		}

		return new FixResult
		{
			Success = false,
			Message = "启用 Windows 功能失败。请以管理员身份运行本程序；若仍失败，可在「启用或关闭 Windows 功能」中手动勾选「适用于 Linux 的 Windows 子系统」和「虚拟机平台」。",
			Raw = sb.ToString()
		};
	}

	// ---------------- 工具方法 ----------------

	/// <summary>wsl.exe 是否可用（用于判断是否需要重启）。</summary>
	public static bool WslWorks()
	{
		try
		{
			var r = Run("wsl", "--version", 15);
			return r.Code == 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool LooksLikeNeedReboot(string text)
	{
		if (string.IsNullOrEmpty(text))
			return false;
		return text.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0
			|| text.IndexOf("重启", StringComparison.Ordinal) >= 0;
	}

	private static FixResult Fail(string msg) => new FixResult { Success = false, Message = msg };

	private static (int Code, string Out, string Err) Run(string exe, string args, int timeoutSec)
	{
		var psi = new ProcessStartInfo
		{
			FileName = exe,
			Arguments = args,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		try
		{
			using var p = Process.Start(psi);
			if (p == null)
				return (-1, "", "无法启动进程");

			var outSb = new StringBuilder();
			var errSb = new StringBuilder();

			var tOut = Task.Run(() => outSb.Append(p.StandardOutput.ReadToEnd()));
			var tErr = Task.Run(() => errSb.Append(p.StandardError.ReadToEnd()));

			if (!p.WaitForExit(timeoutSec * 1000))
			{
				try { p.Kill(true); } catch { }
				var partial = "（超时 " + timeoutSec + " 秒后已终止）";
				return (-2, outSb.ToString() + partial, errSb.ToString());
			}

			try { Task.WaitAll(new[] { tOut, tErr }, 5000); } catch { }
			return (p.ExitCode, outSb.ToString(), errSb.ToString());
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return (-1, "", "无法执行 " + exe + "：" + ex.Message);
		}
		catch (Exception ex)
		{
			return (-1, "", "执行异常：" + ex.Message);
		}
	}
}
