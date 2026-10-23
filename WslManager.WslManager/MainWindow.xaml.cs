using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using System.Windows.Threading;
using Microsoft.VisualBasic.CompilerServices;
using WslManager.Helpers;
using WslManager.Wsl;

namespace WslManager.UI;

[DesignerGenerated]
public partial class MainWindow : Window, INotifyPropertyChanged, IComponentConnector
{
	[CompilerGenerated]
	private bool _reallyExit;

	private NotifyIcon _tray;
	public event PropertyChangedEventHandler PropertyChanged;

	private void RaiseProp(string name = "")
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public MainWindow()
	{
		// 开机自启：必须**在第一次容器刷新之前**取走「待恢复名单」——
		// 刷新会把「当前运行中的容器」写回名单文件，而开机那一刻必然一个都没跑，
		// 上次关机留下的名单会被刷成空（0.9.3 实测踩过：--autostart 跑完一个都没拉起）。
		Wslc.Helpers.AutoStart.CapturePendingOnStartup();

		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Expected Obj, but got Unknown
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Expected Obj, but got Unknown
		((FrameworkElement)this).Loaded += MainWindow_Loaded;
		((Window)this).SourceInitialized += MainWindow_SourceInitialized;
		((Window)this).StateChanged += MainWindow_StateChanged;
		((Window)this).Closing += MainWindow_Closing;
		((UIElement)this).KeyDown += MainWindow_KeyDown;
		_reallyExit = false;
		InitializeComponent();
		((FrameworkElement)this).DataContext = this;
		TitleIcon.Source = AppIcon.MakeImageSource(running: false, 96);
		try
		{
			VersionText.Text = "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.1");
		}
		catch
		{
		}
		((Window)this).Icon = AppIcon.MakeImageSource();
		SetupTray();

		// 注销 / 关机时窗口不走 Closing（进程被直接结束）—— 想留下「关机时谁在跑」，
		// 只能挂在这个事件上。写的是一个几行的文本文件，同步做完来得及。
		try
		{
			Application.Current.SessionEnding += (s, e) => _wslcView?.SaveAutoStartSnapshotNow();
		}
		catch
		{
		}
	}

	private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		OnThemeChanged(App.IsDark);
		// v2.2：内容区只有容器模块，启动就创建（开机自启的容器恢复逻辑在它的 Loaded 里）
		EnsureWslcView();
		DumpTheme();
		ApplyAutoStartHide();
		TrimAfterFirstRender();
		((UIElement)this).Focus();
	}

	/// <summary>
	/// `--dump-theme=&lt;路径&gt;`：导出主题状态与「对齐类」控件的**真实矩形**。
	///
	/// 为什么需要它：「左下角按钮与右下角提示是否同一基线」「按钮与输入框是否同一中轴」
	/// 这类问题差几个像素，截图缩略图看不出来，只能量。0.9.3 起就有这个验收参数，
	/// 并入 v2.x 时漏了 —— 这里补回，并适配成「宿主标题栏 + 容器页」两段。
	/// 只在显式带参数时生效，正常运行完全不受影响。
	/// </summary>
	private void DumpTheme()
	{
		var arg = Environment.GetCommandLineArgs()
			.FirstOrDefault(a => a.StartsWith("--dump-theme=", StringComparison.OrdinalIgnoreCase));
		if (arg == null) return;

		var path = arg.Substring("--dump-theme=".Length).Trim().Trim('"');
		if (path.Length == 0) return;

		try
		{
			((FrameworkElement)this).UpdateLayout();
			WslcHost?.UpdateLayout();

			var sb = new System.Text.StringBuilder();
			sb.AppendLine("== 主题状态 ==");
			sb.AppendLine("  App.IsDark = " + App.IsDark);
			var glyph = ThemeGlyph?.Text ?? "";
			sb.AppendLine("  主题图标  = U+" + (glyph.Length > 0 ? ((int)glyph[0]).ToString("X4") : "----") +
			              "   (E706 太阳 / E708 月亮)");

			sb.AppendLine();
			sb.AppendLine("== 标题栏（42px 行）：各控件中心 y 应在同一基线 ==");
			var items = new List<(string Label, FrameworkElement El)>
			{
				("logo", TitleIcon),
				("版本号", VersionText),
				("主题按钮", ThemeGlyph?.Parent as FrameworkElement),
			};
			double maxDelta = 0;
			double baseline = double.NaN;
			foreach (var it in items)
			{
				var r = RectOf(it.El);
				if (r.IsEmpty) { sb.AppendLine($"  {it.Label,-14} !! 未布局"); continue; }
				double cy = r.Y + r.Height / 2;
				if (double.IsNaN(baseline)) baseline = cy;
				maxDelta = Math.Max(maxDelta, Math.Abs(cy - baseline));
				sb.AppendLine($"  {it.Label,-14} x={r.X:0.#}  y={r.Y:0.#}  {r.Width:0.#}×{r.Height:0.#}   中心 y={cy:0.#}");
			}
			if (!double.IsNaN(baseline))
				sb.AppendLine($"  最大偏差 |Δcy| = {maxDelta:0.#} px  →  {(maxDelta <= 2 ? "同基线 PASS" : "不在同一条线 !!")}");

			if (_wslcView != null)
			{
				sb.AppendLine();
				sb.Append(_wslcView.BuildThemeAlignmentReport());
			}
			else sb.AppendLine("\r\n== 容器模块 ==（未创建：本次启动没进容器页）");

			System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
		}
		catch
		{
		}
	}

	private System.Windows.Rect RectOf(FrameworkElement el)
	{
		if (el == null || el.ActualWidth <= 0 || el.ActualHeight <= 0) return System.Windows.Rect.Empty;
		try
		{
			var p = el.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
			return new System.Windows.Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
		}
		catch { return System.Windows.Rect.Empty; }
	}

	/// <summary>
	/// 开机自启 + 「自启时最小化到托盘」：等容器视图首次加载（含恢复）完成后再 Hide，
	/// 否则窗口先隐藏、视图的 Loaded 可能不触发，容器一个都不会被拉起。
	/// </summary>
	private async void ApplyAutoStartHide()
	{
		try
		{
			if (!Wslc.Helpers.AutoStart.LaunchedByAutoStart) return;
			if (!Wslc.Helpers.AppSettings.AutoStartMinimized) return;

			var view = _wslcView;
			if (view == null) return;

			await view.StartupDone;
			((Window)this).ShowInTaskbar = false;
			((Window)this).Hide();
			TrimWorkingSetIfEnabled();
			SimpleLog.Write("开机自启：后台恢复完成，已最小化到托盘");
		}
		catch
		{
		}
	}

	// ══════════════════════════ 内容区（v2.2） ══════════════════════════
	// 只剩容器模块一个内容区；发行版管理是它左侧导航里的一页，
	// 因此不再有「模块切换」这一步，创建时机与原先「切到容器模块」一致（启动即创建）。

	private Wslc.WslcView _wslcView;

	private Wslc.WslcView EnsureWslcView()
	{
		if (_wslcView != null) return _wslcView;

		_wslcView = new Wslc.WslcView();
		WslcHost.Content = _wslcView;
		// 创建时按当前宿主主题套一次配色（此时可能还没进视觉树，上面的 OnThemeChanged 找不到它）
		Wslc.Helpers.ThemeSwitcher.ApplyTo(_wslcView, App.IsDark);

		// 托盘图标/文案跟随发行版运行数（v2.2：数据由发行版页提供，宿主只负责画托盘）
		_wslcView.DistroRunningChanged += n =>
			Dispatcher.BeginInvoke(new Action(() => UpdateTrayIcon(n)), DispatcherPriority.Background);

		// 容器页原设计 1120x660 起，窗口太小时先撑一下
		if (((Window)this).Width < 1180)
		{
			((Window)this).Width = 1180;
		}
		if (((Window)this).Height < 720)
		{
			((Window)this).Height = 720;
		}
		TrimAfterModuleSwitch();
		return _wslcView;
	}

	public void StartBackground()
	{
		SimpleLog.Write("以托盘常驻模式启动");
		((Window)this).ShowInTaskbar = false;
		// 不显示窗口 → 视图的 Loaded 不会触发，得主动把启动流程推起来
		EnsureWslcView().EnsureStarted();
	}

	private void MainWindow_SourceInitialized(object sender, EventArgs e)
	{
		WindowBackdrop.Apply((Window)(object)this, App.IsDark);
	}

	private void MainWindow_StateChanged(object sender, EventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Invalid comparison between Unknown and I4
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		if ((int)((Window)this).WindowState == 2)
		{
			((FrameworkElement)RootBorder).Margin = new Thickness(7.0);
			RootBorder.CornerRadius = new CornerRadius(0.0);
			MaxGlyph.Text = '\ue923'.ToString();
		}
		else
		{
			((FrameworkElement)RootBorder).Margin = new Thickness(0.0);
			RootBorder.CornerRadius = new CornerRadius(9.0);
			MaxGlyph.Text = '\ue922'.ToString();
		}
	}

	private void MainWindow_Closing(object sender, CancelEventArgs e)
	{
		// 退出前留一份「谁在跑」的名单（自启恢复靠它）—— 两个分支都要写，
		// 因为 _reallyExit 才是真正退出，而 Hide() 分支也可能紧跟着被托盘退出。
		try { _wslcView?.SaveAutoStartSnapshotNow(); }
		catch { }

		if (_reallyExit)
		{
			if (_tray != null)
			{
				_tray.Visible = false;
				((Component)(object)_tray).Dispose();
				_tray = null;
			}
		}
		else if (!Wslc.Helpers.ElevationHost.IsElevateShutdown)
		{
			e.Cancel = true;
			((Window)this).Hide();
			SimpleLog.Write("已最小化到系统托盘，双击托盘图标可重新打开");
			TrimWorkingSetIfEnabled();
		}
	}

	private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
	{
		// F5：刷新当前页（v2.2 起各页自己的刷新逻辑都在容器视图里）
		if (e.Key == Key.F5)
		{
			if (_wslcView != null) await _wslcView.RefreshActivePageAsync();
		}
		else if (e.Key == Key.Escape)
		{
			((Window)this).Hide();
		}
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Invalid comparison between Unknown and I4
		if (e.ClickCount == 2)
		{
			((Window)this).WindowState = (WindowState)(((int)((Window)this).WindowState != 2) ? 2 : 0);
			return;
		}
		try
		{
			((Window)this).DragMove();
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}

	private void MinButton_Click(object sender, RoutedEventArgs e)
	{
		((Window)this).WindowState = (WindowState)1;
	}

	private void MaxButton_Click(object sender, RoutedEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		((Window)this).WindowState = (WindowState)(((int)((Window)this).WindowState != 2) ? 2 : 0);
	}

	private void HideButton_Click(object sender, RoutedEventArgs e)
	{
		((Window)this).Hide();
		SimpleLog.Write("已最小化到系统托盘，双击托盘图标可重新打开");
		TrimWorkingSetIfEnabled();
	}

	[System.Runtime.InteropServices.DllImport("psapi.dll")]
	private static extern bool EmptyWorkingSet(IntPtr hProcess);

	/// <summary>
	/// 隐藏到托盘后，把本进程的工作集交还系统。默认开启（TrimOnHide 缺省为 1）。
	///
	/// 实测（本机 Intel Iris Xe / i5-12500H，窗口 940x640）：
	///   不回收：可见 282 MB → 进托盘仍 284 MB（几乎不降）
	///   回收　：可见 277 MB → 进托盘 32.6 MB（降 244.8 MB）
	///
	/// 原理：WPF 窗口即使 Hide 了，用户态渲染栈（内含显卡驱动的已驻留页）依旧被引用，
	/// 系统不会主动换出；先 GC 掉已无引用的托管对象，再 EmptyWorkingSet 让系统
	/// 把这些页移出物理内存，任务管理器上的数字立刻下来。
	/// 代价：下次从托盘唤起窗口时，这些页需要重新 fault in，首次显示略慢（毫秒级）。
	///
	/// 关闭方法：settings.ini 里写 TrimOnHide=0。
	/// </summary>
	internal static void TrimWorkingSetIfEnabled()
	{
		string flag = null;
		try { flag = Environment.GetEnvironmentVariable("WSLMGR_TRIM"); } catch { }
		if (string.IsNullOrWhiteSpace(flag))
			flag = AppSettings.GetValue("TrimOnHide", "1");

		if (Operators.CompareString(flag, "0", TextCompare: false) == 0
			|| Operators.CompareString(flag, "false", TextCompare: false) == 0)
			return;

		TrimWorkingSet("隐藏到托盘");
	}

	/// <summary>
	/// 真正做回收：GC → EmptyWorkingSet。调用点是「界面上暂时不会再看的东西刚结束」
	/// （进托盘 / 首次显示稳定后 / 切换模块后），不要在用户正在操作时随手调。
	/// </summary>
	internal static void TrimWorkingSet(string reason)
	{
		try
		{
			long before = CurrentWorkingSetMB();

			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

			using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
			{
				EmptyWorkingSet(proc.Handle);
			}

			long after = CurrentWorkingSetMB();
			SimpleLog.Write($"内存回收（{reason}）：工作集 {before} MB → {after} MB");
		}
		catch (Exception ex)
		{
			try { SimpleLog.Write("TrimWorkingSet 失败：" + ex.Message); } catch { }
		}
	}

	/// <summary>
	/// 首次显示稳定后回收一次（TrimOnStartup，缺省开）。
	///
	/// 为什么需要：硬件渲染下 WPF 首帧会把 D3D 设备 + 显卡用户态驱动（本机实测
	/// igd12dxva64 / igc64 等约 150 MB）连同渲染栈整片 fault in —— 进程构造完才 100 MB，
	/// 一显示就跳到 291 MB（工作集）/ 217 MB（私有）。这些页并没有被代码一直引用，
	/// 只是"刚用过、还在工作集里"，所以进托盘回收能掉到 32 MB。
	/// 既然机制一样，就没必要等用户去关一次窗口：首帧稳定 1.5 秒后主动做一次。
	///
	/// 代价与边界：之后只要重绘（切模块、状态刷新）这些页会重新 fault in，工作集会回到
	/// 200~300 MB —— 那是 WPF + 显卡驱动的固有成本，回收不改变。想真正压下来只能不加载
	/// 驱动，即设置 RenderMode=Software（托盘菜单也有一项）。
	/// </summary>
	private async void TrimAfterFirstRender()
	{
		try
		{
			await Task.Delay(1500);
			if (!TrimOnStartupEnabled())
			{
				return;
			}
			await Task.Run(() => TrimWorkingSet("首次显示后"));
		}
		catch
		{
		}
	}

	private static bool TrimOnStartupEnabled()
	{
		string flag = AppSettings.GetValue("TrimOnStartup", "1");
		return Operators.CompareString(flag, "0", TextCompare: false) != 0
			&& Operators.CompareString(flag, "false", TextCompare: false) != 0;
	}

	/// <summary>切模块会重建一批可视化树，隔一会儿再回收一次（同一开关）。</summary>
	private async void TrimAfterModuleSwitch()
	{
		try
		{
			await Task.Delay(2500);
			if (!TrimOnStartupEnabled())
			{
				return;
			}
			await Task.Run(() => TrimWorkingSet("切换模块后"));
		}
		catch
		{
		}
	}

	/// <summary>当前进程的工作集（MB）—— 任务管理器「内存」列看的就是它。</summary>
	internal static long CurrentWorkingSetMB()
	{
		try
		{
			using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
			{
				proc.Refresh();
				return proc.WorkingSet64 / (1024 * 1024);
			}
		}
		catch { return -1; }
	}

	/// <summary>内存探针（--memprobe 用）：把工作集 / 私有字节写进 run.log。</summary>
	internal static void LogMem(string tag)
	{
		try
		{
			using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
			{
				proc.Refresh();
				SimpleLog.Write($"内存[{tag}] 工作集={proc.WorkingSet64 / (1024 * 1024)} MB" +
					$" 私有={proc.PrivateMemorySize64 / (1024 * 1024)} MB" +
					$" 托管={(GC.GetTotalMemory(false) / (1024 * 1024))} MB");
			}
		}
		catch { }
	}

	private void ThemeButton_Click(object sender, RoutedEventArgs e)
	{
		App.ThemeMode = (App.IsDark ? "Light" : "Dark");
		AppSettings.SetValue("Theme", App.ThemeMode);
		App.ApplyTheme();
	}

	// ══════════════════════════ 离屏自检（--shot=<目录>） ══════════════════════════
	// 把窗口摆到屏幕外再 Show()，用 WPF 渲染管线截图：不抢焦点、不动鼠标、不遮挡用户，
	// 因此可以在用户正常使用电脑时跑。用于验收「两个模块的界面都正常」。

	/// <summary>
	/// 静默探针（--dump-layout / --dump-theme）：把主窗口摆到屏幕外、不激活、不占任务栏。
	/// 与 --shot 的离屏自检同一套位置，区别只是产出的是文本报告而不是 PNG。
	/// </summary>
	public void ParkOffscreenForProbe()
	{
		Wslc.WslcView.ParkWindowOffscreen(this);
	}

	public async void RunOffscreenSelfShot(string dir)
	{
		try
		{
			Directory.CreateDirectory(dir);
			ShowInTaskbar = false;
			ShowActivated = false;   // 不抢前台焦点（验收期间用户可正常使用电脑）
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = -6000;
			Top = -6000;
			LogMem("构造完成");
			Show();
			await Task.Delay(1200);
			LogMem("首次显示(1.2s)");

			await Task.Delay(600);
			SaveShot(Path.Combine(dir, "01-env.png"));
			LogMem("环境与安装页(1.8s)");

			if (_wslcView != null)
			{
				_wslcView.SelectPageForTest("distro");
				// 3 秒：发行版列表要跑 wsl -l -v，利用率还要再等一次 /proc 采样，
				// 1.4 秒截出来的卡片正中常常还是空的（v2.5 实测）
				await Task.Delay(3000);
				SaveShot(Path.Combine(dir, "02-distro.png"));

				_wslcView.SelectPageForTest("container");
				await Task.Delay(2600);
				SaveShot(Path.Combine(dir, "03-container.png"));

				_wslcView.SelectPageForTest("settings");
				await Task.Delay(1200);
				SaveShot(Path.Combine(dir, "04-settings.png"));

				_wslcView.SelectPageForTest("env");
				await Task.Delay(800);
				SaveShot(Path.Combine(dir, "05-back-to-env.png"));
			}
			LogMem("各页走完");

			// 内存实验：可见状态下回收，看能压到多少
			TrimWorkingSet("自检·可见状态");
			LogMem("自检·回收后");
			await Task.Delay(1200);
			LogMem("自检·回收后 1.2s");

			// 对话框自检：只构造、不显示。StaticResource 在解析期就要能解析，
			// 所以「构造成功」= 对话框的样式表接得对（并入 Wslc/Themes/Styles.xaml）。
			var report = new List<string>();
			void TryDlg(string name, Func<object> make)
			{
				try
				{
					object o = make();
					report.Add(name + " → OK");
					if (o is Window w2)
					{
						w2.Close();
					}
				}
				catch (Exception ex)
				{
					report.Add(name + " → 失败：" + ex.GetType().Name + " " + ex.Message);
				}
			}
			var sample = new Wslc.Models.ContainerInfo { Name = "selftest" };
			var noImages = new List<Wslc.Models.ImageInfo>();
			TryDlg("CreateContainerDialog", () => new Wslc.Dialogs.CreateContainerDialog(noImages, new[] { "bridge", "app-net" }));
			TryDlg("ContainerDetailDialog", () => new Wslc.Dialogs.ContainerDetailDialog(sample));
			TryDlg("ContainerLogsDialog", () => new Wslc.Dialogs.ContainerLogsDialog(sample));
			TryDlg("TerminalDialog", () => new Wslc.Dialogs.TerminalDialog(sample));
			TryDlg("AppDialog", () => Wslc.Dialogs.AppDialog.Build("自检", "对话框资源自检", "知道了", danger: false));
			report.Insert(0, "版本 " + (GetType().Assembly.GetName().Version?.ToString(3) ?? "?"));
			File.WriteAllText(Path.Combine(dir, "dialog-check.txt"), string.Join(Environment.NewLine, report), new UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			try { SimpleLog.Write("离屏自检失败：" + ex); } catch { }
		}
		finally
		{
			try { _reallyExit = true; } catch { }
			Application.Current?.Shutdown();
		}
	}

	private void SaveShot(string path)
	{
		var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
		var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
			(int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
			(int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
			96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
		rtb.Render(this);
		var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
		enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
		using (var fs = File.Create(path))
		{
			enc.Save(fs);
		}
		SimpleLog.Write("离屏截图：" + path);
	}

	// 关于 / 卸载入口已随发行版页一起搬进容器视图：
	// 「关于」在容器模块的设置与加速页，卸载在发行版管理页。

	public void OnThemeChanged(bool dark)
	{
		ThemeGlyph.Text = (dark ? '\ue706'.ToString() : '\ue708'.ToString());
		WindowBackdrop.SetImmersiveDark((Window)(object)this, dark);
		// v2.1：容器模块自带调色板，换浅/深两份字典（DynamicResource 会自己刷新，
		// 不需要重建 WslcView；未创建的容器页这里会跳过，创建时再补一次）。
		Wslc.Helpers.ThemeSwitcher.Apply(dark);
	}

	private void SetupTray()
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected Obj, but got Unknown
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected Obj, but got Unknown
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Expected Obj, but got Unknown
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected Obj, but got Unknown
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Expected Obj, but got Unknown
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Expected Obj, but got Unknown
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Expected Obj, but got Unknown
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Expected Obj, but got Unknown
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Expected Obj, but got Unknown
		_tray = new NotifyIcon
		{
			Icon = AppIcon.MakeIcon(),
			Text = "WSL 管理器",
			Visible = true
		};
		ContextMenuStrip val = new ContextMenuStrip();
		ToolStripMenuItem val2 = new ToolStripMenuItem("打开主界面");
		ToolStripMenuItem val5 = new ToolStripMenuItem("退出");
		((ToolStripItem)val2).Font = new Font(((ToolStripItem)val2).Font, (FontStyle)1);
		((ToolStripItem)val2).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			ShowMainWindow();
		};
		((ToolStripItem)val5).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			ReallyExit();
		};
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val2);

		// ── v2.4 菜单瘦身 ─────────────────────────────────────────────
		// 删掉：全部开机 / 全部关机（与发行版页的「全部开机·关机」重复）、
		// 内存占用显示、立即释放内存、低内存模式开关。
		// 低内存默认化：TrimWorkingSetIfEnabled 的自动回收（首帧后 / 切模块 / 隐藏到托盘）
		// 是默认行为，不受菜单删除影响；软件渲染的手动开关不再提供（渲染模式仍可由
		// settings.ini 的 RenderMode=Software 或环境变量 WSLMGR_RENDER 指定，文档路径保留）。
		((ToolStrip)val).Items.Add((ToolStripItem)new ToolStripSeparator());
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val5);
		_tray.ContextMenuStrip = val;
		_tray.MouseDoubleClick += Tray_MouseDoubleClick;
	}

	/// <summary>
	/// 托盘图标/文案跟随发行版运行状态（v2.2：数据源从宿主搬到容器视图的发行版页）。
	/// 由 <see cref="EnsureWslcView"/> 订阅 DistroRunningChanged 驱动，只在状态真变时更新。
	/// </summary>
	private void UpdateTrayIcon(int runningDistros)
	{
		if (_tray != null)
		{
			bool running = runningDistros > 0;
			_tray.Icon = AppIcon.MakeIcon(running);
			_tray.Text = running
				? "WSL 管理器 — " + runningDistros + " 个运行中"
				: "WSL 管理器 — 全部已停止";
		}
	}

	private void ShowMainWindow()
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Invalid comparison between Unknown and I4
		((Window)this).Show();
		if ((int)((Window)this).WindowState == 1)
		{
			((Window)this).WindowState = (WindowState)0;
		}
		((Window)this).Activate();
		((Window)this).Topmost = true;
		((Window)this).Topmost = false;
	}

	private void ReallyExit()
	{
		_reallyExit = true;
		Application.Current.Shutdown();
	}

	/// <summary>
	/// 内存实验专用：真正销毁窗口（进程靠 App.xaml 的 OnExplicitShutdown 继续存活）。
	/// 用来验证「隐藏 ≠ 销毁」这个假设——若销毁后工作集掉回首启水平，
	/// 就说明一直挂着的 180MB 是渲染栈（D3D device + 显卡驱动工作集）。
	/// </summary>
	internal void DestroyWindowForTest(bool trim = false)
	{
		try
		{
			_reallyExit = true;
			if (_tray != null)
			{
				_tray.Visible = false;
				((Component)(object)_tray).Dispose();
				_tray = null;
			}
			((Window)this).Close();
			SimpleLog.Write("内存实验：窗口已 Close()，trim=" + trim);

			if (trim)
			{
				GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
				GC.WaitForPendingFinalizers();
				GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
				using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
				{
					EmptyWorkingSet(proc.Handle);
				}
				SimpleLog.Write("内存实验：已 GC + EmptyWorkingSet");
			}
		}
		catch (Exception ex)
		{
			try { SimpleLog.Write("内存实验销毁窗口失败：" + ex.Message); } catch { }
		}
	}

	private void Tray_MouseDoubleClick(object sender, MouseEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Invalid comparison between Unknown and I4
		if ((int)e.Button == 1048576)
		{
			ShowMainWindow();
		}
	}
}
