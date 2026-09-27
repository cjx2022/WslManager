using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WslManager.Helpers;
using WslManager.Install;
using WslManager.Wsl;

namespace WslManager.UI;

public partial class UninstallWindow : Window
{
	private readonly ObservableCollection<Distro> _distros = new ObservableCollection<Distro>();
	private bool _working;
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

	public string PreselectName { get; set; } = "";

	public void SelectDistro(string name)
	{
		if (string.IsNullOrEmpty(name))
			return;
		var d = _distros.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
		if (d != null)
			DistroCombo.SelectedItem = d;
	}

	private void SetProgress(string text, double value = 0, bool indeterminate = false)
	{
		if (ProgressLabel != null)
			ProgressLabel.Text = text;
		if (WorkProgress != null)
		{
			WorkProgress.IsIndeterminate = indeterminate;
			WorkProgress.Value = value;
		}
	}

	public UninstallWindow()
	{
		InitializeComponent();
		DataContext = this;
		SourceInitialized += (_, _) => WindowBackdrop.Apply(this, App.IsDark);
		Loaded += async (_, _) =>
		{
			WindowBackdrop.SetImmersiveDark(this, App.IsDark);
			DistroCombo.ItemsSource = _distros;
			await LoadDistrosAsync(selectFirst: true);
		};
	}

	private void Log(string msg)
	{
		var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
		SimpleLog.Write("卸载窗口 " + msg);
		Dispatcher.Invoke(() =>
		{
			LogBox.AppendText(line + Environment.NewLine);
			LogBox.ScrollToEnd();
		});
	}

	private Distro Selected => DistroCombo.SelectedItem as Distro;

	private async Task LoadDistrosAsync(bool selectFirst = false)
	{
		try
		{
			StatusText = "正在读取发行版列表…";
			var list = await WslService.ListDistrosAsync();
			var prev = (DistroCombo.SelectedItem as Distro)?.Name;
			if (!string.IsNullOrEmpty(PreselectName))
			{
				prev = PreselectName;
				PreselectName = "";
			}
			_distros.Clear();
			foreach (var d in list)
				_distros.Add(d);
			if (_distros.Count == 0)
			{
				StatusText = "未检测到已安装的发行版";
				PathValue.Text = "—";
				SizeValue.Text = "—";
				StateValue.Text = "—";
				return;
			}
			if (!string.IsNullOrEmpty(prev))
			{
				var back = _distros.FirstOrDefault(d => string.Equals(d.Name, prev, StringComparison.OrdinalIgnoreCase));
				if (back != null)
					DistroCombo.SelectedItem = back;
			}
			if (DistroCombo.SelectedIndex < 0)
				DistroCombo.SelectedIndex = 0;
			StatusText = selectFirst ? $"共 {_distros.Count} 个发行版" : $"已刷新，共 {_distros.Count} 个发行版";
			UpdateConfirmState();
		}
		catch (Exception ex)
		{
			StatusText = "读取发行版失败：" + ex.Message;
			Log("读取发行版异常：" + ex.Message);
		}
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e)
	{
		if (_working)
			return;
		await LoadDistrosAsync();
		Log("已刷新发行版列表");
	}

	private async void DistroCombo_Changed(object sender, SelectionChangedEventArgs e)
	{
		var d = Selected;
		if (d == null)
			return;
		ConfirmNameBox.Text = "";
		PathValue.Text = "读取中…";
		SizeValue.Text = "计算中…";
		StateValue.Text = $"{d.Name}　状态：{d.StateText}　WSL {d.Version}{(d.IsDefault ? "　（默认发行版）" : "")}";
		var defaultPath = ExportDefaultPath(d.Name);
		if (string.IsNullOrWhiteSpace(ExportBox.Text) || _exportAutoSet)
			ExportBox.Text = defaultPath;
		UpdateConfirmState();

		try
		{
			var (path, size) = await Task.Run(() => LookupDistro(d.Name));
			PathValue.Text = string.IsNullOrEmpty(path) ? "（未在注册表中找到）" : path;
			SizeValue.Text = size;
		}
		catch (Exception ex)
		{
			PathValue.Text = "—";
			SizeValue.Text = "查询失败";
			Log("查询安装路径失败：" + ex.Message);
		}
	}

	private bool _exportAutoSet = true;

	private static string ExportDefaultPath(string name)
	{
		var dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"WslManager", "backup");
		Directory.CreateDirectory(dir);
		var safe = WslInstallerService.SafeName(name);
		return Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmm}.tar");
	}

	private static (string Path, string Size) LookupDistro(string name)
	{
		string basePath = "";
		try
		{
			using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
			if (root != null)
			{
				foreach (var sub in root.GetSubKeyNames())
				{
					using var k = root.OpenSubKey(sub);
					if (k == null)
						continue;
					var distName = k.GetValue("DistributionName") as string;
					if (string.Equals(distName, name, StringComparison.OrdinalIgnoreCase))
					{
						basePath = k.GetValue("BasePath") as string ?? "";
						break;
					}
				}
			}
		}
		catch
		{
		}
		if (basePath.StartsWith(@"\\?\", StringComparison.Ordinal))
			basePath = basePath.Substring(4);
		if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
			return (basePath, "未知");

		long total = 0;
		var files = new Stack<string>();
		files.Push(basePath);
		while (files.Count > 0)
		{
			var dir = files.Pop();
			try
			{
				foreach (var f in Directory.EnumerateFiles(dir))
				{
					try
					{
						total += new FileInfo(f).Length;
					}
					catch
					{
					}
				}
				foreach (var d in Directory.EnumerateDirectories(dir))
					files.Push(d);
			}
			catch
			{
			}
		}
		var text = total >= 1073741824 ? $"{total / 1073741824.0:F2} GB" : $"{total / 1048576.0:F1} MB";
		return (basePath, text);
	}

	private async void Browse_Click(object sender, RoutedEventArgs e)
	{
		var d = Selected;
		var dlg = new SaveFileDialog
		{
			Filter = "WSL 导出包 (*.tar)|*.tar|所有文件 (*.*)|*.*",
			FileName = Path.GetFileName(ExportBox.Text),
			InitialDirectory = Path.GetDirectoryName(ExportBox.Text)
		};
		if (dlg.ShowDialog(this) == true)
		{
			ExportBox.Text = dlg.FileName;
			_exportAutoSet = false;
			StatusText = "已选择备份路径：" + dlg.FileName;
		}
		await Task.CompletedTask;
	}

	private async void Export_Click(object sender, RoutedEventArgs e)
	{
		await ExportAsync();
	}

	private async Task<bool> ExportAsync()
	{
		var d = Selected;
		if (d == null)
		{
			StatusText = "请先选择发行版";
			return false;
		}
		if (_working)
		{
			StatusText = "正在执行其他任务，请稍候";
			return false;
		}
		var target = (ExportBox.Text ?? "").Trim();
		if (string.IsNullOrEmpty(target))
		{
			StatusText = "请先指定备份文件路径";
			return false;
		}
		_working = true;
		try
		{
			StatusText = $"正在导出 {d.Name} 到 {target}…";
			SetProgress("导出中…", 0, true);
			Log($"执行：wsl --export {d.Name} {target}");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var r = await WslInstallerService.ExportAsync(d.Name, target, default);
			sw.Stop();
			Log($"退出码={r.ExitCode} 耗时 {sw.Elapsed.TotalSeconds:F1}s {(r.StdErr + r.StdOut).Trim()}");
			if (r.Success)
			{
				var size = File.Exists(target) ? new FileInfo(target).Length : 0;
				StatusText = $"导出成功：{Path.GetFileName(target)}（{size / 1048576.0:F1} MB，{sw.Elapsed.TotalSeconds:F0} 秒）";
				SetProgress("完成", 100);
				return true;
			}
			StatusText = $"导出失败（退出码 {r.ExitCode}）：{(r.StdErr + r.StdOut).Trim()}";
			SetProgress("", 0);
			return false;
		}
		catch (Exception ex)
		{
			StatusText = "导出异常：" + ex.Message;
			Log("导出异常：" + ex.Message);
			SetProgress("", 0);
			return false;
		}
		finally
		{
			_working = false;
		}
	}

	private void Confirm_Changed(object sender, RoutedEventArgs e)
	{
		UpdateConfirmState();
	}

	private void UpdateConfirmState()
	{
		if (UninstallBtn == null || ConfirmCheck == null || ConfirmNameBox == null)
			return;
		var d = Selected;
		var ok = d != null
			&& ConfirmCheck.IsChecked == true
			&& string.Equals((ConfirmNameBox.Text ?? "").Trim(), d.Name, StringComparison.Ordinal);
		UninstallBtn.IsEnabled = ok && !_working;
		ConfirmHint.Text = d == null
			? "请先选择发行版。"
			: (ok
				? $"确认无误，点击「卸载」将对 {d.Name} 执行导出（若勾选）+ wsl --unregister。"
				: $"请勾选确认，并输入完全一致的名称：{d.Name}");
	}

	private async void Uninstall_Click(object sender, RoutedEventArgs e)
	{
		var d = Selected;
		if (d == null)
		{
			StatusText = "请先选择发行版";
			return;
		}
		if (ConfirmCheck.IsChecked != true || !string.Equals((ConfirmNameBox.Text ?? "").Trim(), d.Name, StringComparison.Ordinal))
		{
			StatusText = "确认信息不完整，已取消卸载";
			return;
		}
		if (_working)
			return;
		_working = true;
		UpdateConfirmState();
		try
		{
			if (AutoExportCheck.IsChecked == true)
			{
				_working = false;
				var ok = await ExportAsync();
				if (!ok)
				{
					var cont = MessageBox.Show(this,
						$"导出备份未成功。\n\n仍要继续卸载 {d.Name} 吗？\n（继续将永久删除该发行版及其全部数据）",
						"导出未完成", MessageBoxButton.YesNo, MessageBoxImage.Warning);
					if (cont != MessageBoxResult.Yes)
					{
						StatusText = "已取消卸载（导出失败）";
						return;
					}
				}
				_working = true;
			}

			StatusText = $"正在卸载 {d.Name}（wsl --unregister）…";
			SetProgress("卸载中…", 0, true);
			Log($"执行：wsl --unregister {d.Name}");
			var r = await WslInstallerService.UnregisterAsync(d.Name);
			Log($"退出码={r.ExitCode} {(r.StdErr + r.StdOut).Trim()}");
			if (r.Success)
			{
				StatusText = $"{d.Name} 已卸载（数据已删除）";
				SetProgress("完成", 100);
				ConfirmNameBox.Text = "";
				ConfirmCheck.IsChecked = false;
				await LoadDistrosAsync(selectFirst: true);
			}
			else
			{
				StatusText = $"卸载失败（退出码 {r.ExitCode}）：{(r.StdErr + r.StdOut).Trim()}";
				SetProgress("", 0);
			}
		}
		catch (Exception ex)
		{
			StatusText = "卸载异常：" + ex.Message;
			Log("卸载异常：" + ex.Message);
			SetProgress("", 0);
		}
		finally
		{
			_working = false;
			UpdateConfirmState();
		}
	}

	private void UnregisterDoc_Click(object sender, RoutedEventArgs e) => DocLinks.Open(DocLinks.UnregisterCmd);

	private void ExportDoc_Click(object sender, RoutedEventArgs e) => DocLinks.Open(DocLinks.ExportCmd);

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
