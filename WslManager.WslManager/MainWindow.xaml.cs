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
public partial class MainWindow : Window, INotifyPropertyChanged, IComponentConnector, IStyleConnector
{
	[CompilerGenerated]
	internal sealed class _Closure_0024__52_002D0
	{
		public string _0024VB_0024Local_n;

		public _Closure_0024__52_002D0(_Closure_0024__52_002D0 arg0)
		{
			if (arg0 != null)
			{
				_0024VB_0024Local_n = arg0._0024VB_0024Local_n;
			}
		}

		[SpecialName]
		internal bool _Lambda_0024__0(Distro d)
		{
			return string.Equals(d.Name, _0024VB_0024Local_n, StringComparison.OrdinalIgnoreCase);
		}
	}

	[CompilerGenerated]
	internal sealed class _Closure_0024__52_002D1
	{
		public Distro _0024VB_0024Local_d;

		public _Closure_0024__52_002D1(_Closure_0024__52_002D1 arg0)
		{
			if (arg0 != null)
			{
				_0024VB_0024Local_d = arg0._0024VB_0024Local_d;
			}
		}

		[SpecialName]
		internal bool _Lambda_0024__1(DistroViewModel x)
		{
			return string.Equals(x.Name, _0024VB_0024Local_d.Name, StringComparison.OrdinalIgnoreCase);
		}
	}

	private readonly ObservableCollection<DistroViewModel> _distros;

	private bool _isBusy;

	private int _busyCount;

	private bool _refreshing;

	private bool _reallyExit;

	private string _lastSignature;

	private DispatcherTimer _refreshTimer;

	private RelayCommand _startCmd;

	private RelayCommand _stopCmd;

	private RelayCommand _forceStopCmd;

	private RelayCommand _restartCmd;

	private RelayCommand _startAllCmd;

	private RelayCommand _stopAllCmd;

	private RelayCommand _refreshCmd;

	private NotifyIcon _tray;
	public ObservableCollection<DistroViewModel> Distros => _distros;

	public bool IsBusy
	{
		get
		{
			return _isBusy;
		}
		set
		{
			if (_isBusy != value)
			{
				_isBusy = value;
				RaiseProp("IsBusy");
			}
		}
	}

	public ICommand StartCommand
	{
		get
		{
			if (_startCmd == null)
			{
				_startCmd = new RelayCommand(DoStart);
			}
			return _startCmd;
		}
	}

	public ICommand StopCommand
	{
		get
		{
			if (_stopCmd == null)
			{
				_stopCmd = new RelayCommand(DoStop);
			}
			return _stopCmd;
		}
	}

	public ICommand ForceStopCommand
	{
		get
		{
			if (_forceStopCmd == null)
			{
				_forceStopCmd = new RelayCommand(DoForceStop);
			}
			return _forceStopCmd;
		}
	}

	public ICommand RestartCommand
	{
		get
		{
			if (_restartCmd == null)
			{
				_restartCmd = new RelayCommand(DoRestart);
			}
			return _restartCmd;
		}
	}

	public ICommand StartAllCommand
	{
		get
		{
			if (_startAllCmd == null)
			{
				_startAllCmd = new RelayCommand(DoStartAll);
			}
			return _startAllCmd;
		}
	}

	public ICommand StopAllCommand
	{
		get
		{
			if (_stopAllCmd == null)
			{
				_stopAllCmd = new RelayCommand(DoStopAll);
			}
			return _stopAllCmd;
		}
	}

	public ICommand RefreshCommand
	{
		get
		{
			if (_refreshCmd == null)
			{
				_refreshCmd = new RelayCommand(DoRefresh);
			}
			return _refreshCmd;
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	private void RaiseProp(string name = "")
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public MainWindow()
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Expected Obj, but got Unknown
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Expected Obj, but got Unknown
		((FrameworkElement)this).Loaded += MainWindow_Loaded;
		((Window)this).SourceInitialized += MainWindow_SourceInitialized;
		((Window)this).StateChanged += MainWindow_StateChanged;
		((Window)this).Closing += MainWindow_Closing;
		((UIElement)this).KeyDown += MainWindow_KeyDown;
		_distros = new ObservableCollection<DistroViewModel>();
		_isBusy = false;
		_busyCount = 0;
		_refreshing = false;
		_reallyExit = false;
		_lastSignature = "";
		InitializeComponent();
		((FrameworkElement)this).DataContext = this;
		TitleIcon.Source = AppIcon.MakeImageSource(running: false, 32);
		((Window)this).Icon = AppIcon.MakeImageSource();
		SetupTray();
		SetupTimers();
		bool flag = Operators.CompareString(AppSettings.GetValue("AutoRefresh", "1"), "1", TextCompare: false) == 0;
		((ToggleButton)AutoRefreshCheck).IsChecked = flag;
		_refreshTimer.IsEnabled = flag;
	}

	private void SetupTimers()
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Expected Obj, but got Unknown
		_refreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(5.0)
		};
		_refreshTimer.Tick += [SpecialName] (object a0, EventArgs a1) =>
		{
			RefreshAsyncWrapper();
		};
	}

	private async void RefreshAsyncWrapper()
	{
		await RefreshAsync(silent: true);
	}

	private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		OnThemeChanged(App.IsDark);
		AwaitFirstRun();
		((UIElement)this).Focus();
	}

	public void StartBackground()
	{
		SimpleLog.Write("以托盘常驻模式启动");
		((Window)this).ShowInTaskbar = false;
		AwaitFirstRun();
	}

	private async void AwaitFirstRun()
	{
		await RefreshAsync();
		string text = await WslService.GetWslVersionAsync();
		WslVersionText.Text = text;
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
		if (_reallyExit)
		{
			if (_tray != null)
			{
				_tray.Visible = false;
				((Component)(object)_tray).Dispose();
				_tray = null;
			}
		}
		else
		{
			e.Cancel = true;
			((Window)this).Hide();
			SetStatus("已最小化到系统托盘，双击托盘图标可重新打开");
		}
	}

	private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
	{
		if ((int)e.Key == 94)
		{
			await RefreshAsync();
			SetStatus("已手动刷新");
		}
		else if ((int)e.Key == 13)
		{
			((Window)this).Hide();
		}
	}

	public async Task RefreshAsync(bool silent = false)
	{
		if (_refreshing)
		{
			return;
		}
		_refreshing = true;
		if (!silent)
		{
			BusyEnter();
		}
		checked
		{
			try
			{
				List<Distro> list = await WslService.ListDistrosAsync();
				_Closure_0024__52_002D0 obj = default;
				for (int i = _distros.Count - 1; i >= 0; i += -1)
				{
					obj = new _Closure_0024__52_002D0(obj);
					obj._0024VB_0024Local_n = _distros[i].Name;
					if (!list.Any(obj._Lambda_0024__0))
					{
						_distros.RemoveAt(i);
					}
				}
				using (List<Distro>.Enumerator enumerator = list.GetEnumerator())
				{
					_Closure_0024__52_002D1 obj2 = default;
					while (enumerator.MoveNext())
					{
						obj2 = new _Closure_0024__52_002D1(obj2);
						obj2._0024VB_0024Local_d = enumerator.Current;
						DistroViewModel distroViewModel = _distros.FirstOrDefault(obj2._Lambda_0024__1);
						if (distroViewModel == null)
						{
							DistroViewModel distroViewModel2 = new DistroViewModel();
							distroViewModel2.UpdateFrom(obj2._0024VB_0024Local_d);
							_distros.Add(distroViewModel2);
						}
						else
						{
							distroViewModel.UpdateFrom(obj2._0024VB_0024Local_d);
						}
					}
				}
				SortDistros();
				UpdateSummary();
				string text = string.Join(" | ", list.Select([SpecialName] (Distro d) => d.Name + "=" + d.State).ToArray());
				bool flag = Operators.CompareString(text, _lastSignature, TextCompare: false) != 0;
				_lastSignature = text;
				if (flag)
				{
					SimpleLog.Write("状态变化：" + text);
					LastUpdateText.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
				}
				else if (!silent)
				{
					LastUpdateText.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
				}
				((UIElement)EmptyState).Visibility = (Visibility)((_distros.Count != 0) ? 2 : 0);
				_ = UpdateIpsAsync();
				UpdateUptimes();
				UpdateTrayIcon();
			}
			catch (Exception ex)
			{
				ProjectData.SetProjectError(ex);
				Exception ex2 = ex;
				if (silent)
				{
					SimpleLog.Write("后台刷新失败：" + ex2.Message);
				}
				else
				{
					SetStatus("读取状态失败：" + ex2.Message);
				}
				ProjectData.ClearProjectError();
			}
			finally
			{
				_refreshing = false;
				if (!silent)
				{
					BusyLeave();
				}
			}
		}
	}

	private void SortDistros()
	{
		checked
		{
			int num = _distros.Count - 1;
			for (int i = 0; i <= num; i++)
			{
				int num2 = i;
				int num3 = i + 1;
				int num4 = _distros.Count - 1;
				for (int j = num3; j <= num4; j++)
				{
					if (CompareDistro(_distros[j], _distros[num2]) < 0)
					{
						num2 = j;
					}
				}
				if (num2 != i)
				{
					_distros.Move(num2, i);
				}
			}
		}
	}

	private int CompareDistro(DistroViewModel a, DistroViewModel b)
	{
		if (a.IsDefault != b.IsDefault)
		{
			return (!a.IsDefault) ? 1 : (-1);
		}
		return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
	}

	private async Task UpdateIpsAsync()
	{
		foreach (DistroViewModel item in _distros.ToList())
		{
			try
			{
				if (item.IsRunning)
				{
					string text = await WslService.GetIpAsync(item.Name);
					item.Ip = ((text.Length > 0) ? text : "未获取到 IP");
				}
				else
				{
					item.Ip = "";
				}
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				ProjectData.ClearProjectError();
			}
		}
	}

	private void UpdateUptimes()
	{
		foreach (DistroViewModel distro in _distros)
		{
			DateTime? startTime = WslService.GetStartTime(distro.Name);
			if (startTime.HasValue && distro.IsRunning)
			{
				distro.Uptime = FormatUptime(DateTime.Now - startTime.Value);
			}
			else
			{
				distro.Uptime = "";
			}
		}
	}

	private string FormatUptime(TimeSpan ts)
	{
		if (ts.TotalHours >= 1.0)
		{
			return Conversions.ToString(Math.Floor(ts.TotalHours)) + " 小时 " + Conversions.ToString(ts.Minutes) + " 分";
		}
		if (ts.TotalMinutes >= 1.0)
		{
			return Conversions.ToString(Math.Floor(ts.TotalMinutes)) + " 分 " + Conversions.ToString(ts.Seconds) + " 秒";
		}
		return Conversions.ToString(ts.Seconds) + " 秒";
	}

	private void UpdateSummary()
	{
		int count = _distros.Count;
		int num = _distros.AsEnumerable().Count([SpecialName] (DistroViewModel d) => d.IsRunning);
		SummaryText.Text = $"共 {count} 个发行版  ·  {num} 个运行中  ·  {checked(count - num)} 个已停止";
	}

	private void BusyEnter()
	{
		checked
		{
			_busyCount++;
			if (_busyCount == 1)
			{
				IsBusy = true;
			}
		}
	}

	private void BusyLeave()
	{
		checked
		{
			_busyCount--;
			if (_busyCount <= 0)
			{
				_busyCount = 0;
				IsBusy = false;
			}
		}
	}

	private void SetStatus(string msg)
	{
		StatusMessage.Text = msg;
		SimpleLog.Write(msg);
	}

	private DistroViewModel Find(string name)
	{
		return _distros.FirstOrDefault([SpecialName] (DistroViewModel x) => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	private async void DoStart(object param)
	{
		await RunAction(Conversions.ToString(RuntimeHelpers.GetObjectValue(param)), "开机", [SpecialName] () => WslService.StartAsync(Conversions.ToString(param)));
	}

	private async void DoStop(object param)
	{
		string text = Conversions.ToString(RuntimeHelpers.GetObjectValue(param));
		await RunAction(text, "关机", [SpecialName] () => WslService.StopAsync(text), [SpecialName] (WslResult r) => (!r.Graceful) ? $"{text} 已强制终止（软关机超时，未落盘的数据可能丢失）" : $"{text} 已正常关机");
	}

	private async void DoForceStop(object param)
	{
		string name = Conversions.ToString(RuntimeHelpers.GetObjectValue(param));
		await RunAction(name, "强制关机", [SpecialName] () => WslService.TerminateAsync(name));
	}

	private async void DoRestart(object param)
	{
		string name = Conversions.ToString(RuntimeHelpers.GetObjectValue(param));
		await RunAction(name, "重启", [SpecialName] () => WslService.RestartAsync(name));
	}

	private async void DoStartAll(object param)
	{
		List<string> list = (from d in _distros
			where !d.IsRunning
			select d.Name).ToList();
		if (list.Count == 0)
		{
			SetStatus("所有发行版都已在运行中");
			return;
		}
		BusyEnter();
		SetStatus($"正在开机 {list.Count} 个发行版…");
		int num = 0;
		foreach (string item in list)
		{
			if ((await WslService.StartAsync(item)).Success)
			{
				num = checked(num + 1);
			}
		}
		BusyLeave();
		SetStatus($"已开机 {num}/{list.Count} 个发行版");
		await RefreshAsync();
	}

	private async void DoStopAll(object param)
	{
		BusyEnter();
		SetStatus("正在软关机所有发行版，未响应者将强制关闭…");
		WslResult wslResult = await WslService.ShutdownAllAsync();
		BusyLeave();
		SetStatus(wslResult.Success ? "已关闭所有发行版（先软关机，再 wsl --shutdown 兜底）" : ("关闭失败：" + wslResult.ErrorMessage));
		await RefreshAsync();
	}

	private async void DoRefresh(object param)
	{
		await RefreshAsync();
	}

	private async Task RunAction(string name, string verb, Func<Task<WslResult>> action, Func<WslResult, string> describe = null)
	{
		DistroViewModel distroViewModel = Find(name);
		if (distroViewModel == null)
		{
			return;
		}
		distroViewModel.IsBusy = true;
		BusyEnter();
		SetStatus($"正在{verb} {name}…");
		try
		{
			WslResult wslResult = await action();
			if (wslResult.Success)
			{
				SetStatus((describe != null) ? describe(wslResult) : $"{name} 已{verb}");
			}
			else
			{
				string text = ((wslResult.ErrorMessage.Length > 0) ? wslResult.ErrorMessage : wslResult.StdErr.Trim());
				if (text.Length == 0)
				{
					text = "退出码 " + Conversions.ToString(wslResult.ExitCode);
				}
				SetStatus($"{name} {verb}失败：{text}");
			}
		}
		catch (Exception ex)
		{
			ProjectData.SetProjectError(ex);
			Exception ex2 = ex;
			SetStatus($"{name} {verb}异常：{ex2.Message}");
			ProjectData.ClearProjectError();
		}
		finally
		{
			distroViewModel.IsBusy = false;
			BusyLeave();
		}
		await RefreshAsync();
	}

	private void TerminalButton_Click(object sender, RoutedEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		string text = Conversions.ToString(((FrameworkElement)(Button)sender).Tag);
		SetStatus("正在打开 " + text + " 终端…");
		WslService.OpenTerminal(text);
	}

	private void FilesButton_Click(object sender, RoutedEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		string text = Conversions.ToString(((FrameworkElement)(Button)sender).Tag);
		SetStatus("正在打开 " + text + " 文件系统…");
		WslService.OpenFiles(text);
	}

	private void MoreButton_Click(object sender, RoutedEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Expected Obj, but got Unknown
		Button val = (Button)sender;
		if (((FrameworkElement)val).ContextMenu != null)
		{
			((FrameworkElement)val).ContextMenu.PlacementTarget = (UIElement)(object)val;
			((FrameworkElement)val).ContextMenu.IsOpen = true;
		}
		e.Handled = true;
	}

	private string OwnerName(object item)
	{
		MenuItem val = (MenuItem)((item is MenuItem) ? item : null);
		if (val == null)
		{
			return "";
		}
		DependencyObject parent = ((FrameworkElement)val).Parent;
		ContextMenu val2 = (ContextMenu)(object)((parent is ContextMenu) ? parent : null);
		if (val2 == null)
		{
			return "";
		}
		UIElement placementTarget = val2.PlacementTarget;
		Button val3 = (Button)(object)((placementTarget is Button) ? placementTarget : null);
		if (val3 == null)
		{
			return "";
		}
		return Conversions.ToString(((FrameworkElement)val3).Tag);
	}

	private async void SetDefault_Click(object sender, RoutedEventArgs e)
	{
		string text = OwnerName(RuntimeHelpers.GetObjectValue(sender));
		if (text.Length != 0)
		{
			BusyEnter();
			WslResult wslResult = await WslService.SetDefaultAsync(text);
			BusyLeave();
			SetStatus(wslResult.Success ? $"已将 {text} 设为默认发行版" : ("设置失败：" + wslResult.ErrorMessage));
			await RefreshAsync();
		}
	}

	private void CopyIp_Click(object sender, RoutedEventArgs e)
	{
		string name = OwnerName(RuntimeHelpers.GetObjectValue(sender));
		DistroViewModel distroViewModel = Find(name);
		if (distroViewModel != null)
		{
			if (distroViewModel.Ip.Length > 0 && Operators.CompareString(distroViewModel.Ip, "未获取到 IP", TextCompare: false) != 0)
			{
				Clipboard.SetText(distroViewModel.Ip);
				SetStatus("已复制 IP：" + distroViewModel.Ip);
			}
			else
			{
				SetStatus("该发行版当前没有可用 IP");
			}
		}
	}

	private void CopyName_Click(object sender, RoutedEventArgs e)
	{
		string text = OwnerName(RuntimeHelpers.GetObjectValue(sender));
		if (text.Length > 0)
		{
			Clipboard.SetText(text);
			SetStatus("已复制发行版名称：" + text);
		}
	}

	private async void ForceStop_Click(object sender, RoutedEventArgs e)
	{
		string text = OwnerName(RuntimeHelpers.GetObjectValue(sender));
		if (text.Length != 0)
		{
			await RunAction(text, "强制关机", [SpecialName] () => WslService.TerminateAsync(text));
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
		SetStatus("已最小化到系统托盘，双击托盘图标可重新打开");
	}

	private void ThemeButton_Click(object sender, RoutedEventArgs e)
	{
		App.ThemeMode = (App.IsDark ? "Light" : "Dark");
		AppSettings.SetValue("Theme", App.ThemeMode);
		App.ApplyTheme();
	}

	private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
	{
		if (_refreshTimer != null)
		{
			bool valueOrDefault = ((ToggleButton)AutoRefreshCheck).IsChecked == true;
			AppSettings.SetValue("AutoRefresh", valueOrDefault ? "1" : "0");
			_refreshTimer.IsEnabled = valueOrDefault;
			SetStatus(valueOrDefault ? "已开启自动刷新（每 3 秒）" : "已关闭自动刷新");
		}
	}

	private InstallWindow _installWin;

	private UninstallWindow _uninstallWin;

	private void InstallWindow_Click(object sender, RoutedEventArgs e)
	{
		if (_installWin != null && _installWin.IsVisible)
		{
			_installWin.Activate();
			return;
		}
		_installWin = new InstallWindow
		{
			Owner = (Window)(object)this
		};
		_installWin.Closed += async (a0, a1) =>
		{
			await RefreshAsync(silent: true);
			SetStatus("安装窗口已关闭，列表已刷新");
		};
		_installWin.Show();
		SetStatus("已打开安装窗口（环境检查 / 镜像测速 / 下载安装）");
	}

	private void About_Click(object sender, RoutedEventArgs e)
	{
		System.Windows.MessageBox.Show(this,
			"WSL Manager 1.0\n\n开发者：BillChen\n© 2026 BillChen 版权所有\n\n本软件为个人开发作品，仅供学习与个人使用；软件按现状提供，作者不对使用本软件产生的任何损失承担责任。WSL、Windows 等名称归其各自所有者所有。",
			"关于 WSL Manager",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private void UninstallWindow_Click(object sender, RoutedEventArgs e)
	{
		OpenUninstallWindow(null);
	}

	private void UninstallDistro_Click(object sender, RoutedEventArgs e)
	{
		string name = OwnerName(RuntimeHelpers.GetObjectValue(sender));
		if (name.Length != 0)
		{
			OpenUninstallWindow(name);
		}
	}

	private void OpenUninstallWindow(string preselect)
	{
		if (_uninstallWin != null && _uninstallWin.IsVisible)
		{
			if (!string.IsNullOrEmpty(preselect))
			{
				_uninstallWin.SelectDistro(preselect);
			}
			_uninstallWin.Activate();
			return;
		}
		_uninstallWin = new UninstallWindow
		{
			Owner = (Window)(object)this,
			PreselectName = preselect ?? ""
		};
		_uninstallWin.Closed += async (a0, a1) =>
		{
			await RefreshAsync(silent: true);
			SetStatus("卸载窗口已关闭，列表已刷新");
		};
		_uninstallWin.Show();
		SetStatus("已打开卸载窗口（导出备份 + 输入确认卸载）");
	}

	public void OnThemeChanged(bool dark)
	{
		ThemeGlyph.Text = (dark ? '\ue706'.ToString() : '\ue708'.ToString());
		WindowBackdrop.SetImmersiveDark((Window)(object)this, dark);
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
			Text = "WSL 状态管理器",
			Visible = true
		};
		ContextMenuStrip val = new ContextMenuStrip();
		ToolStripMenuItem val2 = new ToolStripMenuItem("打开主界面");
		ToolStripMenuItem val3 = new ToolStripMenuItem("全部开机");
		ToolStripMenuItem val4 = new ToolStripMenuItem("全部关机");
		ToolStripMenuItem val5 = new ToolStripMenuItem("退出");
		((ToolStripItem)val2).Font = new Font(((ToolStripItem)val2).Font, (FontStyle)1);
		((ToolStripItem)val2).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			ShowMainWindow();
		};
		((ToolStripItem)val3).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			_Lambda_0024__86_002D1();
		};
		((ToolStripItem)val4).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			_Lambda_0024__86_002D2();
		};
		((ToolStripItem)val5).Click += [SpecialName] (object a0, EventArgs a1) =>
		{
			ReallyExit();
		};
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val2);
		((ToolStrip)val).Items.Add((ToolStripItem)new ToolStripSeparator());
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val3);
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val4);
		((ToolStrip)val).Items.Add((ToolStripItem)new ToolStripSeparator());
		((ToolStrip)val).Items.Add((ToolStripItem)(object)val5);
		_tray.ContextMenuStrip = val;
		_tray.MouseDoubleClick += Tray_MouseDoubleClick;
	}

	private void UpdateTrayIcon()
	{
		if (_tray != null)
		{
			bool flag = _distros.Any([SpecialName] (DistroViewModel d) => d.IsRunning);
			_tray.Icon = AppIcon.MakeIcon(flag);
			_tray.Text = (flag ? ("WSL 状态管理器 — " + Conversions.ToString(_distros.AsEnumerable().Count([SpecialName] (DistroViewModel d) => d.IsRunning)) + " 个运行中") : "WSL 状态管理器 — 全部已停止");
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

	private void Tray_MouseDoubleClick(object sender, MouseEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Invalid comparison between Unknown and I4
		if ((int)e.Button == 1048576)
		{
			ShowMainWindow();
		}
	}

	[SpecialName]
	[CompilerGenerated]
	private void _Lambda_0024__86_002D1()
	{
		DoStartAll(null);
	}

	[SpecialName]
	[CompilerGenerated]
	private void _Lambda_0024__86_002D2()
	{
		DoStopAll(null);
	}
}
