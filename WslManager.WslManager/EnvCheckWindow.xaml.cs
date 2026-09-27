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
			}
		}
	}

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
					DocName = i.DocName
				});
			}
			var fail = items.Count(i => i.Status == "Fail");
			var warn = items.Count(i => i.Status == "Warn");
			StatusText = fail == 0
				? $"环境检查完成：{items.Count - warn - fail} 项通过，{warn} 项注意"
				: $"环境检查发现 {fail} 项未通过，{warn} 项注意（点击查看文档）";
			SimpleLog.Write($"环境检查完成：通过 {items.Count - warn - fail}，注意 {warn}，未通过 {fail}");
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
