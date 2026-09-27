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
		if (array.Length > 0 && !IsResidentMode(array))
		{
			RunCommandAsync(array);
			return;
		}
		bool createdNew = false;
		_mutex = new Mutex(initiallyOwned: true, "Global\\WslManager.SingleInstance.V2.9A3D", out createdNew);
		if (!createdNew)
		{
			MessageBox.Show("WSL 状态管理器 v2 已经在运行了。", "WSL 状态管理器", (MessageBoxButton)0, (MessageBoxImage)64);
			((Application)this).Shutdown();
			return;
		}
		ThemeMode = AppSettings.GetValue("Theme", "Auto");
		if (Operators.CompareString(ThemeMode, "Light", TextCompare: false) != 0 && Operators.CompareString(ThemeMode, "Dark", TextCompare: false) != 0)
		{
			ThemeMode = "Auto";
		}
		ApplyTheme();
		MainWindow mainWindow = new MainWindow();
		if (array.Length > 0 && (Operators.CompareString(array[0], "--tray", TextCompare: false) == 0 || Operators.CompareString(array[0], "-b", TextCompare: false) == 0))
		{
			mainWindow.StartBackground();
		}
		else
		{
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
