using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WslManager.Helpers;
using WslManager.Install;
using WslManager.Wsl;

namespace WslManager.UI;

public class MirrorVm : ViewModelBase
{
	private string _speedText = "未测速";
	private bool _busy;

	public string Name { get; set; } = "";

	public string Location { get; set; } = "";

	public string Url { get; set; } = "";

	public bool CanSpeed => !Url.StartsWith("wsl ", StringComparison.OrdinalIgnoreCase);

	public PackageVm Owner { get; set; }

	public string SpeedText
	{
		get => _speedText;
		set => SetField(ref _speedText, value, nameof(SpeedText));
	}

	public bool Busy
	{
		get => _busy;
		set => SetField(ref _busy, value, nameof(Busy));
	}
}

public class PackageVm : ViewModelBase
{
	private string _installName = "";

	public DistroPackage Package { get; set; }

	public ObservableCollection<MirrorVm> OfficialSources { get; } = new ObservableCollection<MirrorVm>();

	public ObservableCollection<MirrorVm> MirrorSources { get; } = new ObservableCollection<MirrorVm>();

	public string DisplayName => string.IsNullOrWhiteSpace(Package.Version) ? Package.Name : $"{Package.Name} {Package.Version}";

	public string SizeText => Package.SizeBytes > 0 ? Package.SizeText : "";

	public string MethodText => Package.Sources.Any(s => !s.Url.StartsWith("wsl ", StringComparison.OrdinalIgnoreCase))
		? Package.MethodText
		: "";

	public string InstallName
	{
		get => _installName;
		set => SetField(ref _installName, value, nameof(InstallName));
	}

	public PackageVm(DistroPackage pkg, string onlineName = "")
	{
		Package = pkg;
		var on = onlineName ?? "";
		_installName = DefaultName(pkg);
		if (on.Length > 0)
		{
			OfficialSources.Add(new MirrorVm
			{
				Name = "官方商店",
				Location = "微软",
				Url = $"wsl --install -d {on}",
				SpeedText = "",
				Owner = this
			});
		}
		foreach (var s in pkg.Sources)
		{
			if (s.Url.StartsWith("wsl ", StringComparison.OrdinalIgnoreCase))
				continue;
			var m = new MirrorVm
			{
				Name = s.Name,
				Location = s.Location,
				Url = s.Url,
				Owner = this
			};
			if (s.IsOfficial)
				OfficialSources.Add(m);
			else
				MirrorSources.Add(m);
		}
	}

	private static string DefaultName(DistroPackage pkg)
	{
		if (string.IsNullOrWhiteSpace(pkg.Version))
			return pkg.Name;
		if (pkg.Name.Equals("Alpine", StringComparison.OrdinalIgnoreCase))
			return "Alpine";
		var first = pkg.Version.Split(' ')[0].Trim();
		return $"{pkg.Name}-{first}".Replace(".", "-");
	}
}

public partial class InstallWindow : Window
{
	private readonly ObservableCollection<PackageVm> _packages = new ObservableCollection<PackageVm>();
	private readonly List<OnlineDistro> _knownOnline = new List<OnlineDistro>();
	private string _statusText = "就绪";
	private string _progressText = "";
	private double _progressValue;
	private bool _working;
	private CancellationTokenSource _cts;
	private int _autoSpeedGen;
	private DownloadsWindow _dlWin;

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

	public string ProgressText
	{
		get => _progressText;
		set
		{
			_progressText = value;
			if (ProgressLabel != null)
				ProgressLabel.Text = value;
		}
	}

	public double ProgressValue
	{
		get => _progressValue;
		set
		{
			_progressValue = value;
			if (WorkProgress != null)
				WorkProgress.Value = value;
		}
	}

	public InstallWindow()
	{
		InitializeComponent();
		DataContext = this;
		SourceInitialized += (_, _) => WindowBackdrop.Apply(this, App.IsDark);
		Loaded += async (_, _) =>
		{
			WindowBackdrop.SetImmersiveDark(this, App.IsDark);
			DistroCombo.ItemsSource = _packages;
			DistroCombo.DisplayMemberPath = "DisplayName";
			await LoadOptionsAsync();
		};
	}

	private void SetWorking(bool value)
	{
		_working = value;
		WorkProgress.IsIndeterminate = value && ProgressValue <= 0;
		if (CancelBtn != null)
			CancelBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
		if (value)
		{
			_cts?.Dispose();
			_cts = new CancellationTokenSource();
		}
	}

	private CancellationToken Tok => _cts != null ? _cts.Token : CancellationToken.None;

	private void Log(string msg)
	{
		var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
		SimpleLog.Write("安装窗口 " + msg);
		Dispatcher.Invoke(() =>
		{
			LogBox.AppendText(line + Environment.NewLine);
			LogBox.ScrollToEnd();
		});
	}

	private static string OnlineCachePath => System.IO.Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"WslManager", "onlinelist.cache");

	private static List<OnlineDistro> ReadOnlineCache()
	{
		var list = new List<OnlineDistro>();
		try
		{
			foreach (var line in System.IO.File.ReadAllLines(OnlineCachePath))
			{
				var parts = line.Split('\t');
				if (parts.Length > 0 && parts[0].Trim().Length > 0)
					list.Add(new OnlineDistro
					{
						Name = parts[0].Trim(),
						FriendlyName = parts.Length > 1 ? parts[1] : ""
					});
			}
		}
		catch
		{
		}
		return list;
	}

	private static void WriteOnlineCache(List<OnlineDistro> list)
	{
		try
		{
			System.IO.File.WriteAllLines(OnlineCachePath, list.Select(d => d.Name + "\t" + d.FriendlyName));
		}
		catch
		{
		}
	}

	private int MergeOnline(List<OnlineDistro> items)
	{
		var added = 0;
		foreach (var d in items)
		{
			if (!_knownOnline.Any(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
			{
				_knownOnline.Add(d);
				added++;
			}
		}
		return added;
	}

	private async Task LoadOptionsAsync(bool forceRefresh = false)
	{
		try
		{
			var added = 0;
			var fetchedFresh = false;
			var fromCache = false;
			var refreshFailed = false;
			if (forceRefresh)
			{
				StatusText = "正在刷新官方发行版列表…";
				var got = await WslInstallerService.ListOnlineAsync(Tok);
				if (got.Count > 0)
				{
					added = MergeOnline(got);
					WriteOnlineCache(_knownOnline);
					fetchedFresh = true;
				}
				else
				{
					refreshFailed = true;
				}
			}
			else if (_knownOnline.Count == 0)
			{
				if (System.IO.File.Exists(OnlineCachePath))
				{
					_knownOnline.AddRange(ReadOnlineCache());
					fromCache = _knownOnline.Count > 0;
				}
				if (_knownOnline.Count == 0)
				{
					StatusText = "正在读取官方发行版列表…";
					var got = await WslInstallerService.ListOnlineAsync(Tok);
					if (got.Count > 0)
					{
						added = MergeOnline(got);
						WriteOnlineCache(_knownOnline);
						fetchedFresh = true;
					}
				}
			}
			var online = _knownOnline;
			var prev = (DistroCombo.SelectedItem as PackageVm)?.Package.Key;
			_packages.Clear();
			var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var d in online)
			{
				var pkg = MirrorCatalog.Packages.FirstOrDefault(p =>
					p.OnlineName.Length > 0 && p.OnlineName.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
				if (pkg != null && matched.Add(pkg.Key))
					_packages.Add(new PackageVm(pkg, d.Name));
				else
					_packages.Add(new PackageVm(new DistroPackage
					{
						Key = "online:" + d.Name,
						Name = d.Name,
						Version = ""
					}, d.Name));
			}
			foreach (var p in MirrorCatalog.Packages)
			{
				if (p.OnlineName.Length == 0 && !matched.Contains(p.Key))
					_packages.Add(new PackageVm(p, ""));
			}
			StatusText = refreshFailed
				? $"官方列表获取失败，已保留现有 {online.Count} 个条目"
				: forceRefresh
					? $"可用发行版 {_packages.Count} 个（官方 {online.Count} 个，新增 {added} 个，已缓存）"
					: fromCache
						? $"可用发行版 {_packages.Count} 个（缓存列表，点「刷新列表」更新）"
						: $"可用发行版 {_packages.Count} 个（官方列表 {online.Count} 个，已缓存）";
			Log($"发行版列表 → {_packages.Count} 个（官方 {online.Count} 个，本次新增 {added} 个{(fromCache ? "，缓存" : fetchedFresh ? "，已写入缓存" : "")}）");
			if (_packages.Count > 0)
			{
				var back = _packages.FirstOrDefault(x => x.Package.Key == prev);
				DistroCombo.SelectedItem = back ?? _packages[0];
			}
		}
		catch (Exception ex)
		{
			StatusText = "读取发行版列表失败：" + ex.Message;
			Log("读取列表异常：" + ex.Message);
		}
	}

	private async void RefreshOnline_Click(object sender, RoutedEventArgs e)
	{
		await LoadOptionsAsync(true);
	}

	private void InstallDoc_Click(object sender, RoutedEventArgs e) => DocLinks.Open(DocLinks.Install);

	private void MirrorDoc_Click(object sender, RoutedEventArgs e) => DocLinks.Open(DocLinks.CustomDistro);

	private async void SpeedOne_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button b || b.Tag is not MirrorVm m)
			return;
		await SpeedOneAsync(m);
	}

	private async Task SpeedOneAsync(MirrorVm m)
	{
		if (m.Busy || !m.CanSpeed)
			return;
		m.Busy = true;
		m.SpeedText = "测速中…";
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(Tok);
			cts.CancelAfter(TimeSpan.FromSeconds(12));
			var r = await DownloadService.MeasureAsync(m.Url, cts.Token);
			m.SpeedText = r.Ok
				? $"{r.SpeedText}（HTTP {r.StatusCode}{(r.UsedFallback ? "，回退下载" : "")}）"
				: $"不可用：{r.Error}";
			Log($"测速 {m.Name}：{m.SpeedText} {m.Url}");
		}
		catch (OperationCanceledException) when (Tok.IsCancellationRequested)
		{
			m.SpeedText = "已取消";
			Log($"测速取消 {m.Name}");
		}
		catch (OperationCanceledException)
		{
			m.SpeedText = "测速超时（12 秒）";
			Log($"测速超时 {m.Name}");
		}
		catch (Exception ex)
		{
			m.SpeedText = "测速失败：" + ex.Message;
			Log("测速异常：" + ex.Message);
		}
		finally
		{
			m.Busy = false;
		}
	}

	private async void DistroCombo_Changed(object sender, SelectionChangedEventArgs e)
	{
		var pkg = DistroCombo.SelectedItem as PackageVm;
		if (pkg == null)
			return;
		OfficialList.ItemsSource = pkg.OfficialSources;
		MirrorList.ItemsSource = pkg.MirrorSources;
		OfficialGroupTitle.Visibility = pkg.OfficialSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		MirrorGroupTitle.Visibility = pkg.MirrorSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		var size = pkg.SizeText;
		SizeBadgeBox.Visibility = string.IsNullOrEmpty(size) ? Visibility.Collapsed : Visibility.Visible;
		SizeBadge.Text = size;
		var method = pkg.MethodText;
		MethodBadgeBox.Visibility = string.IsNullOrEmpty(method) ? Visibility.Collapsed : Visibility.Visible;
		MethodBadge.Text = method;
		InstallNameBox.DataContext = pkg;
		StatusText = $"已选择 {pkg.DisplayName}（官方 {pkg.OfficialSources.Count} 个 + 国内 {pkg.MirrorSources.Count} 个下载源）";
		await AutoSpeedAsync(pkg);
	}

	private async Task AutoSpeedAsync(PackageVm pkg)
	{
		var gen = ++_autoSpeedGen;
		if (_working)
			return;
		var targets = pkg.OfficialSources.Concat(pkg.MirrorSources).Where(s => s.CanSpeed).ToList();
		if (targets.Count == 0)
			return;
		StatusText = $"正在自动测速 {pkg.DisplayName}…";
		foreach (var m in targets)
		{
			if (gen != _autoSpeedGen || Tok.IsCancellationRequested || _working)
				return;
			await SpeedOneAsync(m);
		}
		if (gen != _autoSpeedGen || _working)
			return;
		var best = targets.Where(s => s.SpeedText.Contains("/s"))
			.OrderByDescending(s => ParseKbps(s.SpeedText)).FirstOrDefault();
		StatusText = best != null
			? $"{pkg.DisplayName} 测速完成，最快 {best.Name}（{best.SpeedText}）"
			: $"{pkg.DisplayName} 测速完成";
	}

	private async void SpeedAll_Click(object sender, RoutedEventArgs e)
	{
		if (_working)
			return;
		SetWorking(true);
		try
		{
			var pkg = DistroCombo.SelectedItem as PackageVm;
			if (pkg == null)
			{
				StatusText = "请先选择发行版";
				return;
			}
			var all = pkg.OfficialSources.Concat(pkg.MirrorSources).Where(s => s.CanSpeed).ToList();
			if (all.Count == 0)
			{
				StatusText = $"{pkg.DisplayName} 没有可测速的下载源";
				return;
			}
			StatusText = $"正在测速 {pkg.DisplayName} 的下载源…";
			int done = 0;
			foreach (var m in all)
			{
				if (Tok.IsCancellationRequested)
					break;
				await SpeedOneAsync(m);
				done++;
				ProgressValue = done * 100.0 / all.Count;
				ProgressText = $"{done}/{all.Count}";
			}
			if (Tok.IsCancellationRequested)
			{
				StatusText = "测速已终止";
				return;
			}
			var best = all.Where(s => s.SpeedText.Contains("/s")).OrderByDescending(s => ParseKbps(s.SpeedText)).FirstOrDefault();
			StatusText = best != null ? $"测速完成，最快：{best.Name}（{best.SpeedText}）" : "测速完成";
			Log($"测速完成：{pkg.DisplayName}（{all.Count} 个源）");
		}
		finally
		{
			SetWorking(false);
			ProgressValue = 0;
			ProgressText = "";
		}
	}

	private static double ParseKbps(string text)
	{
		var idxMb = text.IndexOf("MB/s", StringComparison.Ordinal);
		var idxKb = text.IndexOf("KB/s", StringComparison.Ordinal);
		if (idxMb <= 0 && idxKb <= 0)
			return -1;
		var idx = idxMb > 0 ? idxMb : idxKb;
		var start = text.LastIndexOf(' ', idx);
		var num = start >= 0 ? text.Substring(start + 1, idx - start - 1) : text.Substring(0, idx);
		if (!double.TryParse(num, out var v))
			return -1;
		return idxMb > 0 ? v * 1024 : v;
	}

	private async void InstallOne_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button b || b.Tag is not MirrorVm m)
			return;
		await InstallAsync(m.Owner, m);
	}

	private async Task InstallAsync(PackageVm pkg, MirrorVm src)
	{
		if (_working)
		{
			StatusText = "正在执行其他任务，请稍候";
			return;
		}
		SetWorking(true);
		ProgressValue = 0;
		try
		{
			if (src.Url.StartsWith("wsl ", StringComparison.OrdinalIgnoreCase))
			{
				var parts = src.Url.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				var name = parts.Last();
				StatusText = $"正在安装 {name}…";
				Log($"执行：{src.Url}");
				var r = await WslInstallerService.InstallOnlineAsync(name, Tok);
				Log($"退出码={r.ExitCode} {(r.StdErr + r.StdOut).Trim()}");
				StatusText = r.Success ? $"{name} 安装成功" : $"安装失败（退出码 {r.ExitCode}）";
				return;
			}

			StatusText = $"正在下载 {src.Name}：{pkg.DisplayName}…";
			Log($"开始下载 {src.Url}");
			var progress = new Progress<DownloadProgress>(p =>
			{
				ProgressValue = p.Percent;
				ProgressText = $"{p.SizeText} {p.SpeedText}";
			});
			var file = await DownloadService.DownloadCachedAsync(src.Url, progress, Tok);
			Log($"下载完成：{file}");

			if (src.Url.EndsWith(".wsl", StringComparison.OrdinalIgnoreCase))
			{
				ProgressText = "安装中…";
				StatusText = $"正在安装 {System.IO.Path.GetFileName(file)}…";
				var r = await WslInstallerService.InstallFromFileAsync(file, Tok);
				Log($"退出码={r.ExitCode} {(r.StdErr + r.StdOut).Trim()}");
				StatusText = r.Success
					? $"安装完成（{pkg.DisplayName}），返回主界面按 F5 刷新"
					: $"安装失败（退出码 {r.ExitCode}）：{Truncate((r.StdErr + r.StdOut).Trim(), 160)}";
			}
			else if (src.Url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || src.Url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
			{
				var name = WslInstallerService.SafeName(string.IsNullOrWhiteSpace(pkg.InstallName) ? pkg.Package.Name : pkg.InstallName);
				var dir = System.IO.Path.Combine(WslInstallerService.DistroRootDir, name);
				ProgressText = "导入中…";
				StatusText = $"正在导入 {name}…（首次导入可能需要数分钟）";
				Log($"执行：wsl --import {name} {dir} {file} --version 2");
				var r = await WslInstallerService.ImportAsync(name, dir, file, Tok);
				Log($"退出码={r.ExitCode} {(r.StdErr + r.StdOut).Trim()}");
				StatusText = r.Success
					? $"{name} 导入完成（WSL 2），返回主界面按 F5 刷新"
					: $"导入失败（退出码 {r.ExitCode}）：{Truncate((r.StdErr + r.StdOut).Trim(), 160)}";
			}
			else
			{
				StatusText = "无法识别的包类型，请参考「自定义发行版文档」";
			}
		}
		catch (OperationCanceledException)
		{
			StatusText = "已取消（安装/下载已终止）";
			Log("安装流程已取消");
		}
		catch (Exception ex)
		{
			StatusText = "安装异常：" + ex.Message;
			Log("安装异常：" + ex.Message);
		}
		finally
		{
			SetWorking(false);
			ProgressValue = 0;
			ProgressText = "";
		}
	}

	private static string Truncate(string s, int len)
	{
		s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
		return s.Length <= len ? s : s.Substring(0, len) + "…";
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		if (!_working)
			return;
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
		StatusText = "正在终止当前任务…";
		Log("请求终止当前任务");
	}

	private void Downloads_Click(object sender, RoutedEventArgs e)
	{
		if (_dlWin != null && _dlWin.IsVisible)
		{
			_dlWin.Activate();
			return;
		}
		_dlWin = new DownloadsWindow
		{
			Owner = this
		};
		_dlWin.Closed += (_, _) => _dlWin = null;
		_dlWin.Show();
	}

	private EnvCheckWindow _envWin;

	private void OpenEnvCheck_Click(object sender, RoutedEventArgs e)
	{
		if (_envWin != null && _envWin.IsVisible)
		{
			_envWin.Activate();
			return;
		}
		_envWin = new EnvCheckWindow
		{
			Owner = this
		};
		_envWin.Closed += (_, _) => _envWin = null;
		_envWin.Show();
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
