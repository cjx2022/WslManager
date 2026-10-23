using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualBasic.CompilerServices;
using WslManager.Helpers;
using WslManager.Wsl;

namespace WslManager.UI;

public partial class App : Application
{
	private static Mutex _mutex = null;

	public static string ThemeMode { get; set; } = "Auto";

	public static bool IsDark { get; set; } = false;

	protected override void OnStartup(StartupEventArgs e)
	{
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		AppDomain.CurrentDomain.UnhandledException += (a0, a1) =>
		{
			try
			{
				SimpleLog.Write("未处理异常: " + a1.ExceptionObject);
			}
			catch
			{
			}
		};
		TaskScheduler.UnobservedTaskException += (a0, a1) =>
		{
			try
			{
				SimpleLog.Write("未观察任务异常: " + a1.Exception);
			}
			catch
			{
			}
		};
		try
		{
			System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
		}
		catch
		{
		}
		base.OnStartup(e);
		ApplyRenderMode();
		DispatcherUnhandledException += (a0, a1) =>
		{
			try
			{
				SimpleLog.Write("调度器异常: " + a1.Exception);
			}
			catch
			{
			}
			a1.Handled = true;
		};
		string[] array = Environment.GetCommandLineArgs().Skip(1).ToArray();
		SimpleLog.Write("启动参数：" + ((array.Length == 0) ? "(无)" : string.Join(" ", array)));
		if (array.Length > 0 && !IsResidentMode(array) && !IsGuiExtraMode(array) && !IsElevatedRelaunch(array) && !IsMemTest(array) && !IsShotMode(array))
		{
			RunCommandAsync(array);
			return;
		}
		bool createdNew = false;
		// --elevated：由旧实例提权重启而来，旧实例已在退出流程中（可能还没释放互斥量），
		// 此时不抢单实例锁，否则会被自己的旧进程判定为"已在运行"而直接退出。
		if (!IsElevatedRelaunch(array))
		{
			_mutex = new Mutex(initiallyOwned: true, "Global\\WslManager.SingleInstance.V2.9A3D", out createdNew);
			if (!createdNew)
			{
				MessageBox.Show("WSL 管理器已经在运行了。", "WSL 管理器", (MessageBoxButton)0, (MessageBoxImage)64);
				((Application)this).Shutdown();
				return;
			}
		}
		ThemeMode = AppSettings.GetValue("Theme", "Auto");
		if (Operators.CompareString(ThemeMode, "Light", TextCompare: false) != 0 && Operators.CompareString(ThemeMode, "Dark", TextCompare: false) != 0)
		{
			ThemeMode = "Auto";
		}
		ApplyTheme();
		MainWindow mainWindow = new MainWindow();
		// 静默探针（--dump-layout= / --dump-theme= 等）：只写报告文件，全程不弹窗、不抢焦点。
		// 必须在 Show 之前置位，MainWindow / 弹窗在显示时才会把自己摆到屏幕外。
		WslManager.Wslc.WslcView.SilentProbe = IsSilentProbeMode(array);
		// 离屏自检（--shot=<目录>）：窗口摆到屏幕外渲染两个模块并存 PNG，不抢焦点、不动鼠标。
		string shotDir = null;
		foreach (string shotArg in array)
		{
			if (shotArg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
			{
				shotDir = shotArg.Substring("--shot=".Length).Trim('"');
			}
		}
		if (!string.IsNullOrWhiteSpace(shotDir))
		{
			mainWindow.RunOffscreenSelfShot(shotDir);
			return;
		}
		// ---- 内存实验钩子：显示窗口后真正销毁它（进程靠 OnExplicitShutdown 继续活着），
		//      用来验证「销毁窗口能否释放渲染栈」。跑完记得删。----
		if (array.Length > 0 && Operators.CompareString(array[0], "--memtest", TextCompare: false) == 0)
		{
			double delay = 8;
			if (array.Length > 1) double.TryParse(array[1], out delay);
			bool trim = array.Length > 2 && Operators.CompareString(array[2], "trim", TextCompare: false) == 0;
			SimpleLog.Write($"内存实验：显示窗口，{delay} 秒后真正销毁窗口，trim={trim}");
			((Window)mainWindow).Show();
			var timer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(delay)
			};
			timer.Tick += (a0, a1) =>
			{
				timer.Stop();
				SimpleLog.Write("内存实验：正在销毁窗口");
				mainWindow.DestroyWindowForTest(trim);
			};
			timer.Start();
			return;
		}
		if (array.Length > 0 && (Operators.CompareString(array[0], "--tray", TextCompare: false) == 0 || Operators.CompareString(array[0], "-b", TextCompare: false) == 0))
		{
			mainWindow.StartBackground();
		}
		else
		{
			// 静默探针：窗口摆到屏幕外再显示 —— 验收不该在用户桌面上弹窗
			if (WslManager.Wslc.WslcView.SilentProbe) mainWindow.ParkOffscreenForProbe();
			((Window)mainWindow).Show();
		}
	}

	private bool IsResidentMode(string[] args)
	{
		if (Operators.CompareString(args[0], "--tray", TextCompare: false) != 0)
		{
			return Operators.CompareString(args[0], "-b", TextCompare: false) == 0;
		}
		return true;
	}

	/// <summary>
	/// 只影响 GUI 行为、不构成 CLI 命令的启动参数。带这些参数时**必须进窗口**，
	/// 否则会被 RunCommandAsync 当成「未知命令」直接 Shutdown。
	///
	/// ⚠ 踩过的坑：--autostart 是开机自启写进注册表的参数，一旦被误判成 CLI 命令，
	///   自启进程启动即退出、一个容器都拉不起来，且日志里只有一句「未知命令」。
	///   README 里写的 --module=wslc、验收用的 --dump-theme= 同理。
	/// </summary>
	private static bool IsGuiExtraMode(string[] args)
	{
		foreach (string a in args)
		{
			if (a.StartsWith("--autostart", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--module=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--dump-theme=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// 静默探针参数：只产出报告 / 截图文件，不需要给人看窗口。
	/// 这类启动必须离屏（主窗口 + 自己点开的弹窗）—— 否则跑一次验收就在用户桌面上弹窗、抢焦点。
	/// </summary>
	private static bool IsSilentProbeMode(string[] args)
	{
		foreach (string a in args)
		{
			if (a.StartsWith("--dump-layout=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--dump-theme=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--click-start=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase)
				|| a.StartsWith("--textmode", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>是否为「旧实例提权重启」拉起来的新进程。</summary>
	private static bool IsElevatedRelaunch(string[] args)
		=> args.Length > 0 && Operators.CompareString(args[0], "--elevated", TextCompare: false) == 0;

	/// <summary>离屏自检（--shot=&lt;目录&gt;，仅验收用）。</summary>
	private static bool IsShotMode(string[] args)
		=> args.Any(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));

	/// <summary>内存实验钩子（仅调试用）。</summary>
	private static bool IsMemTest(string[] args)
		=> args.Length > 0 && Operators.CompareString(args[0], "--memtest", TextCompare: false) == 0;

	/// <summary>
	/// 渲染模式。默认走 WPF 硬件加速（Direct3D），代价是会加载显卡厂商的用户态驱动
	/// （本机 Intel 核显实测 igd12dxva64 + igc64 + igd12um64xel 合计约 150 MB 工作集）。
	/// 设为 Software 则改用 WPF 内置软件光栅器，完全不碰 Direct3D 与显卡驱动。
	/// 取值来源：环境变量 WSLMGR_RENDER（优先，便于压测）→ settings.ini 的 RenderMode → Auto。
	/// </summary>
	private static void ApplyRenderMode()
	{
		string mode = null;
		try { mode = Environment.GetEnvironmentVariable("WSLMGR_RENDER"); } catch { }
		if (string.IsNullOrWhiteSpace(mode))
			mode = AppSettings.GetValue("RenderMode", "Auto");

		if (!string.IsNullOrWhiteSpace(mode)
			&& (Operators.CompareString(mode, "Software", TextCompare: false) == 0
				|| Operators.CompareString(mode, "software", TextCompare: false) == 0
				|| Operators.CompareString(mode, "1", TextCompare: false) == 0))
		{
			RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
			SimpleLog.Write("渲染模式：软件渲染（SoftwareOnly，不加载显卡驱动）");
		}
		else
		{
			SimpleLog.Write("渲染模式：默认（WPF 硬件加速）");
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		// 先释放单实例互斥量，确保即将拉起的提权实例不会撞上"已在运行"。
		try
		{
			if (_mutex != null)
			{
				try
				{
					_mutex.ReleaseMutex();
				}
				catch (ApplicationException)
				{
					// 本线程不持有该互斥量（或已被释放），忽略
				}
				_mutex.Dispose();
				_mutex = null;
			}
		}
		catch (Exception ex)
		{
			try
			{
				SimpleLog.Write("释放单实例互斥量失败：" + ex.Message);
			}
			catch
			{
			}
		}

		// 用户点了「提权重启」：现在才拉起管理员实例，避免与上面的互斥量冲突。
		if (Wslc.Helpers.ElevationHost.PendingElevate)
		{
			Wslc.Helpers.ElevationHost.PendingElevate = false;
			Wslc.Helpers.ElevationHost.TryRelaunchElevated();
		}
		// 本次退出就是为提权重启服务的，清掉标志，避免影响后续同进程内的其他退出流程。
		Wslc.Helpers.ElevationHost.IsElevateShutdown = false;

		base.OnExit(e);
	}

	private async void RunCommandAsync(string[] args)
	{
		try
		{
			string text = args[0].ToLowerInvariant();
			SimpleLog.Write("命令行：" + string.Join(" ", args));
			switch (text)
			{
			case "--status":
			case "-q":
				await DumpStatusAsync();
				break;
			case "--start":
			case "-s":
				if (args.Length > 1)
				{
					WslResult wslResult2 = await WslService.StartAsync(args[1]);
					SimpleLog.Write(string.Format("启动 {0} → 退出码={1} 错误={2} stderr={3}", new object[4]
					{
						args[1],
						wslResult2.ExitCode,
						wslResult2.ErrorMessage,
						wslResult2.StdErr.Trim()
					}));
					await Task.Delay(1500);
					await DumpStatusAsync();
				}
				else
				{
					SimpleLog.Write("缺少发行版名称");
				}
				break;
			case "--stop":
			case "-t":
				if (args.Length > 1)
				{
					WslResult wslResult3 = await WslService.StopAsync(args[1]);
					SimpleLog.Write(string.Format("停止 {0} → 退出码={1} {2}", args[1], wslResult3.ExitCode, (wslResult3.ErrorMessage.Length > 0) ? ("错误：" + wslResult3.ErrorMessage) : "成功"));
					await Task.Delay(800);
					await DumpStatusAsync();
				}
				else
				{
					SimpleLog.Write("缺少发行版名称");
				}
				break;
			case "--force-stop":
			case "-f":
				if (args.Length > 1)
				{
					WslResult wslResult4 = await WslService.TerminateAsync(args[1]);
					SimpleLog.Write($"强制终止 {args[1]} → 退出码={wslResult4.ExitCode}");
					await Task.Delay(800);
					await DumpStatusAsync();
				}
				else
				{
					SimpleLog.Write("缺少发行版名称");
				}
				break;
			case "--restart":
			case "-r":
				if (args.Length > 1)
				{
					WslResult wslResult = await WslService.RestartAsync(args[1]);
					SimpleLog.Write($"重启 {args[1]} → 退出码={wslResult.ExitCode} 错误={wslResult.ErrorMessage}");
					await Task.Delay(2000);
					await DumpStatusAsync();
				}
				else
				{
					SimpleLog.Write("缺少发行版名称");
				}
				break;
			case "--shutdown":
			case "-k":
				SimpleLog.Write($"全部关机 → 退出码={(await WslService.ShutdownAllAsync()).ExitCode}");
				await Task.Delay(800);
				await DumpStatusAsync();
				break;
			default:
				SimpleLog.Write("未知命令：" + text);
				break;
			}
		}
		catch (Exception ex)
		{
			ProjectData.SetProjectError(ex);
			Exception ex2 = ex;
			SimpleLog.Write("命令执行失败：" + ex2.Message);
			ProjectData.ClearProjectError();
		}
		finally
		{
			((Application)this).Shutdown();
		}
	}

	private async Task DumpStatusAsync()
	{
		List<Distro> list = await WslService.ListDistrosAsync();
		if (list.Count == 0)
		{
			SimpleLog.Write("未检测到 WSL 发行版");
			return;
		}
		foreach (Distro item in list)
		{
			string text = string.Format("  {0}  {1}  {2}  WSL{3}", new object[4]
			{
				item.Name,
				item.StateText,
				item.IsDefault ? "默认" : "",
				item.Version
			});
			if (item.IsRunning)
			{
				string text2 = await WslService.GetIpAsync(item.Name);
				if (text2.Length > 0)
				{
					text = text + "  IP=" + text2;
				}
			}
			SimpleLog.Write(text);
		}
	}

	public static void ApplyTheme()
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Expected Obj, but got Unknown
		string themeMode = ThemeMode;
		bool flag = (IsDark = Operators.CompareString(themeMode, "Light", TextCompare: false) != 0 && (Operators.CompareString(themeMode, "Dark", TextCompare: false) == 0 || !WindowBackdrop.SystemUsesLightTheme()));
		Collection<ResourceDictionary> mergedDictionaries = Application.Current.Resources.MergedDictionaries;
		if (mergedDictionaries.Count >= 2)
		{
			mergedDictionaries[1] = new ResourceDictionary
			{
				Source = new Uri(flag ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
			};
		}
		ApplyAccent(flag);
		foreach (object window in Application.Current.Windows)
		{
			if (RuntimeHelpers.GetObjectValue(window) is MainWindow mainWindow)
			{
				mainWindow.OnThemeChanged(flag);
			}
		}
	}

	private static void ApplyAccent(bool dark)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Expected Obj, but got Unknown
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Expected Obj, but got Unknown
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Expected Obj, but got Unknown
		Color val = WindowBackdrop.GetAccentColor();
		double num = (0.299 * (double)(int)val.R + 0.587 * (double)(int)val.G + 0.114 * (double)(int)val.B) / 255.0;
		if (dark && num < 0.2)
		{
			val = LiftColor(val, 0.55);
		}
		else if (num > 0.72)
		{
			val = DarkenColor(val, 0.72);
		}
		Color val2 = (dark ? LiftColor(val, 0.18) : DarkenColor(val, 0.12));
		Application.Current.Resources[(object)"AccentBrush"] = (object)new SolidColorBrush(val);
		Application.Current.Resources[(object)"AccentHoverBrush"] = (object)new SolidColorBrush(val2);
		Application.Current.Resources[(object)"AccentSoftBrush"] = (object)new SolidColorBrush(Color.FromArgb((byte)(dark ? 60 : 38), val.R, val.G, val.B));
	}

	private static Color LiftColor(Color c, double amount)
	{
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		checked
		{
			return Color.FromRgb((byte)Math.Round(Math.Min(255.0, (double)unchecked((int)c.R) + (double)(255 - c.R) * amount)), (byte)Math.Round(Math.Min(255.0, (double)unchecked((int)c.G) + (double)(255 - c.G) * amount)), (byte)Math.Round(Math.Min(255.0, (double)unchecked((int)c.B) + (double)(255 - c.B) * amount)));
		}
	}

	private static Color DarkenColor(Color c, double amount)
	{
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		checked
		{
			return Color.FromRgb((byte)Math.Round((double)unchecked((int)c.R) * (1.0 - amount)), (byte)Math.Round((double)unchecked((int)c.G) * (1.0 - amount)), (byte)Math.Round((double)unchecked((int)c.B) * (1.0 - amount)));
		}
	}
}
