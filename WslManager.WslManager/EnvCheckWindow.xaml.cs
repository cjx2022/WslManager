using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WslManager.Helpers;
using WslManager.Install;

namespace WslManager.UI;

public class CheckVm : ViewModelBase
{
	private string _name = "";
	private string _detail = "";
	private string _status = "Unknown";
	private string _docUrl = "";
	private bool _fixing;
	private string _fixMessage = "";

	public string Name
	{
		get => _name;
		set => SetField(ref _name, value, nameof(Name));
	}

	public string Detail
	{
		get => _detail;
		set => SetField(ref _detail, value, nameof(Detail));
	}

	public string Status
	{
		get => _status;
		set
		{
			if (SetField(ref _status, value, nameof(Status)))
			{
				OnPropertyChanged(nameof(Glyph));
				OnPropertyChanged(nameof(StatusText));
				OnPropertyChanged(nameof(StatusBrush));
				OnPropertyChanged(nameof(CanFix));
				OnPropertyChanged(nameof(FixHint));
			}
		}
	}

	/// <summary>该项目可用的修复动作（来自 CheckItem.Fix）。</summary>
	public FixKind Fix { get; set; } = FixKind.None;

	/// <summary>修复进行中的标志（按钮转圈 / 禁用）。</summary>
	public bool IsFixing
	{
		get => _fixing;
		set
		{
			if (SetField(ref _fixing, value, nameof(IsFixing)))
			{
				OnPropertyChanged(nameof(CanFix));
				OnPropertyChanged(nameof(FixButtonText));
			}
		}
	}

	/// <summary>上一次修复的结果文案（一行，展示在详情下方）。</summary>
	public string FixMessage
	{
		get => _fixMessage;
		set
		{
			if (SetField(ref _fixMessage, value, nameof(FixMessage)))
				OnPropertyChanged(nameof(HasFixMessage));
		}
	}

	public bool HasFixMessage => !string.IsNullOrWhiteSpace(_fixMessage);

	public bool NeedsFix => !string.Equals(_status, "Ok", StringComparison.OrdinalIgnoreCase);

	/// <summary>是否显示「修复」按钮：未通过且该项有修复动作，且当前没有正在修复。</summary>
	public bool CanFix => NeedsFix && Fix != FixKind.None && !_fixing;

	public string FixButtonText => _fixing ? "修复中…" : FixText;

	public string FixText => Fix switch
	{
		FixKind.InstallWsl => "安装 WSL",
		FixKind.StartService => "启动服务",
		FixKind.EnableFeature => "启用功能",
		FixKind.Elevate => "提权重启",
		_ => ""
	};

	public string FixHint => NeedsFix
		? (Fix != FixKind.None ? FixText : "需手动处理，请参考文档")
		: "";

	public string DocUrl
	{
		get => _docUrl;
		set
		{
			if (SetField(ref _docUrl, value, nameof(DocUrl)))
			{
				OnPropertyChanged(nameof(HasDoc));
			}
		}
	}

	public string DocName { get; set; } = "";

	public bool HasDoc => !string.IsNullOrWhiteSpace(_docUrl);

	public string Glyph => _status switch
	{
		"Ok" => "\uE73E",
		"Warn" => "\uE7BA",
		"Fail" => "\uE711",
		_ => "\uE946"
	};

	public string StatusText => _status switch
	{
		"Ok" => "通过",
		"Warn" => "注意",
		"Fail" => "未通过",
		_ => "未知"
	};

	public System.Windows.Media.Brush StatusBrush => _status switch
	{
		"Ok" => UiRes.Get("SuccessBrush", System.Windows.Media.Brushes.Green),
		"Warn" => UiRes.Get("WarningBrush", System.Windows.Media.Brushes.DarkGoldenrod),
		"Fail" => UiRes.Get("DangerBrush", System.Windows.Media.Brushes.IndianRed),
		_ => UiRes.Get("TextTertiaryBrush", System.Windows.Media.Brushes.Gray)
	};
}

internal static class UiRes
{
	public static System.Windows.Media.Brush Get(string key, System.Windows.Media.Brush fallback)
	{
		try
		{
			return Application.Current?.Resources[key] as System.Windows.Media.Brush ?? fallback;
		}
		catch
		{
			return fallback;
		}
	}
}

public partial class EnvCheckWindow : Window
{
	private readonly ObservableCollection<CheckVm> _checks = new ObservableCollection<CheckVm>();
	private string _statusText = "就绪";

	public string StatusText
	{
		get => _statusText;
		set
		{
			_statusText = value;
			if (StatusMessage != null)
				StatusMessage.Text = value;
		}
	}

	public EnvCheckWindow()
	{
		InitializeComponent();
		DataContext = this;
		SourceInitialized += (_, _) => WindowBackdrop.Apply(this, App.IsDark);
		Loaded += async (_, _) =>
		{
			WindowBackdrop.SetImmersiveDark(this, App.IsDark);
			CheckList.ItemsSource = _checks;
			await RunChecksAsync();
		};
	}

	private async Task RunChecksAsync()
	{
		CheckProgress.Visibility = Visibility.Visible;
		StatusText = "正在检查环境…";
		try
		{
			var items = await Task.Run(() => EnvChecker.RunAsync(msg => Dispatcher.Invoke(() => StatusText = msg)));
			_checks.Clear();
			foreach (var i in items)
			{
				_checks.Add(new CheckVm
				{
					Name = i.Name,
					Detail = i.Detail,
					Status = i.Status,
					DocUrl = i.DocUrl,
					DocName = i.DocName,
					Fix = i.Fix
				});
			}
			var fail = items.Count(i => i.Status == "Fail");
			var warn = items.Count(i => i.Status == "Warn");
			var fixable = items.Count(i => i.CanFix);
			StatusText = fail == 0
				? $"环境检查完成：{items.Count - warn - fail} 项通过，{warn} 项注意"
				: $"环境检查发现 {fail} 项未通过，{warn} 项注意"
					+ (fixable > 0 ? $"，其中 {fixable} 项可一键修复" : "（请点击文档手动处理）");
			FixAllButton.IsEnabled = fixable > 0;
			FixAllButton.Content = fixable > 0 ? $"一键修复（{fixable}）" : "一键修复";
			SimpleLog.Write($"环境检查完成：通过 {items.Count - warn - fail}，注意 {warn}，未通过 {fail}，可修复 {fixable}");
		}
		catch (Exception ex)
		{
			StatusText = "环境检查失败：" + ex.Message;
			SimpleLog.Write("环境检查异常：" + ex.Message);
		}
		finally
		{
			CheckProgress.Visibility = Visibility.Collapsed;
		}
	}

	private async void Recheck_Click(object sender, RoutedEventArgs e)
	{
		await RunChecksAsync();
	}

	// ------------------------- 修复 -------------------------

	/// <summary>单项修复：点某一行的「修复」按钮。</summary>
	private async void FixItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button b || b.Tag is not CheckVm vm)
			return;
		await FixOneAsync(vm);
	}

	/// <summary>一键修复：按顺序修复所有可修复项。</summary>
	private async void FixAll_Click(object sender, RoutedEventArgs e)
	{
		var targets = _checks.Where(c => c.CanFix).ToList();
		if (targets.Count == 0)
		{
			StatusText = "没有可自动修复的项目。";
			return;
		}

		FixAllButton.IsEnabled = false;
		RecheckButton.IsEnabled = false;

		int ok = 0;
		int bad = 0;
		bool needReboot = false;

		try
		{
			for (int i = 0; i < targets.Count; i++)
			{
				StatusText = $"一键修复（{i + 1}/{targets.Count}）：{targets[i].Name}…";
				var r = await FixOneAsync(targets[i], silent: true);
				if (r != null && r.Success)
					ok++;
				else
					bad++;
				if (r != null && r.NeedReboot)
					needReboot = true;
			}

			StatusText = $"一键修复完成：{ok} 项成功，{bad} 项失败";
			SimpleLog.Write($"一键修复完成：成功 {ok}，失败 {bad}，需重启={needReboot}");
		}
		finally
		{
			RecheckButton.IsEnabled = true;
		}

		// 修复后自动复查一次，让状态即时刷新
		await RunChecksAsync();

		if (needReboot)
		{
			MessageBox.Show(this,
				"部分修复（启用 Windows 功能 / 安装 WSL 组件）需要重启电脑后才会生效。\n\n请重启后再点「重新检查」确认。",
				"需要重启", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		else if (bad == 0 && ok > 0)
		{
			MessageBox.Show(this, $"已修复 {ok} 项，状态已刷新。", "修复完成",
				MessageBoxButton.OK, MessageBoxImage.Information);
		}
	}

	/// <summary>
	/// 执行单项修复。silent=true 时不弹单条结果框（供一键修复复用）。
	/// </summary>
	private async Task<FixResult> FixOneAsync(CheckVm vm, bool silent = false)
	{
		if (vm == null || vm.Fix == FixKind.None)
			return null;

		// 提权重启：不是修复动作，而是换个身份重开自己
		if (vm.Fix == FixKind.Elevate)
		{
			if (TryRelaunchElevated())
			{
				StatusText = "已请求以管理员身份重新启动，本窗口即将关闭…";
				SimpleLog.Write("用户触发提权重启");
				await Task.Delay(600);
				Application.Current?.Shutdown();
			}
			else
			{
				MessageBox.Show(this,
					"提权启动失败或被取消。\n\n你也可以关闭本程序，手动右键 →「以管理员身份运行」。",
					"提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
			return new FixResult { Success = false, Message = "已请求提权" };
		}

		vm.IsFixing = true;
		vm.FixMessage = "";
		CheckProgress.Visibility = Visibility.Visible;
		StatusText = $"正在修复：{vm.Name}…";

		FixResult result;
		try
		{
			var item = new CheckItem
			{
				Name = vm.Name,
				Detail = vm.Detail,
				Status = vm.Status,
				Fix = vm.Fix
			};
			result = await FixService.ApplyAsync(item, msg => Dispatcher.Invoke(() => StatusText = msg));
		}
		catch (Exception ex)
		{
			result = new FixResult { Success = false, Message = "修复异常：" + ex.Message };
		}
		finally
		{
			vm.IsFixing = false;
			CheckProgress.Visibility = Visibility.Collapsed;
		}

		vm.FixMessage = (result.Success ? "✔ " : "✖ ") + result.Message;
		StatusText = result.Message;
		SimpleLog.Write($"修复[{vm.Name}/{vm.Fix}] 成功={result.Success} 需重启={result.NeedReboot}：{result.Message}");

		// 单项修复时，有需要重启的提示就直接弹出来
		if (!silent && result.NeedReboot)
		{
			MessageBox.Show(this, result.Message + "\n\n需要重启电脑后才会生效。", "需要重启",
				MessageBoxButton.OK, MessageBoxImage.Information);
		}

		// 单项修复：成功后立刻复查该项所在整表，让用户看到状态变化
		if (!silent && result.Success)
		{
			await RunChecksAsync();
		}

		return result;
	}

	/// <summary>以管理员身份重新启动当前程序。</summary>
	private bool TryRelaunchElevated()
	{
		try
		{
			var exe = Environment.ProcessPath;
			if (string.IsNullOrEmpty(exe))
				return false;

			var psi = new System.Diagnostics.ProcessStartInfo
			{
				FileName = exe,
				UseShellExecute = true,
				Verb = "runas",
				WorkingDirectory = AppContext.BaseDirectory
			};
			System.Diagnostics.Process.Start(psi);
			return true;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			// 用户在 UAC 弹窗点了「否」
			return false;
		}
		catch (Exception ex)
		{
			SimpleLog.Write("提权失败：" + ex.Message);
			return false;
		}
	}

	private void InstallDoc_Click(object sender, RoutedEventArgs e) => DocLinks.Open(DocLinks.Install);

	private void DocLink_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button b && b.Tag is string url)
			DocLinks.Open(url);
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
			return;
		}
		try
		{
			DragMove();
		}
		catch
		{
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
