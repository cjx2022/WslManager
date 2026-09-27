using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WslManager.Helpers;
using WslManager.Install;

namespace WslManager.UI;

public class DownloadFileVm
{
	public string FullPath { get; set; } = "";

	public string Name { get; set; } = "";

	public long Bytes { get; set; }

	public string SizeText => $"{Bytes / 1048576.0:F1} MB";

	public string DateText { get; set; } = "";
}

public partial class DownloadsWindow : Window
{
	private List<DownloadFileVm> _files = new List<DownloadFileVm>();
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

	public DownloadsWindow()
	{
		InitializeComponent();
		DataContext = this;
		SourceInitialized += (_, _) => WindowBackdrop.Apply(this, App.IsDark);
		Loaded += (_, _) =>
		{
			WindowBackdrop.SetImmersiveDark(this, App.IsDark);
			LoadFiles();
		};
	}

	private void LoadFiles()
	{
		_files = new List<DownloadFileVm>();
		var dir = DownloadService.DownloadDir;
		DirText.Text = dir;
		if (Directory.Exists(dir))
		{
			foreach (var f in Directory.EnumerateFiles(dir).OrderByDescending(x => File.GetLastWriteTime(x)))
			{
				var fi = new FileInfo(f);
				_files.Add(new DownloadFileVm
				{
					FullPath = f,
					Name = fi.Name,
					Bytes = fi.Length,
					DateText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
				});
			}
		}
		FileList.ItemsSource = _files;
		EmptyHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		var total = _files.Sum(x => x.Bytes);
		StatusText = _files.Count > 0
			? $"缓存 {_files.Count} 个文件，共 {total / 1048576.0:F1} MB"
			: "缓存为空";
		SimpleLog.Write($"下载管理：{dir} → {_files.Count} 个文件 {total / 1048576.0:F1} MB");
	}

	private void OpenDir_Click(object sender, RoutedEventArgs e)
	{
		var dir = DownloadService.DownloadDir;
		try
		{
			Directory.CreateDirectory(dir);
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			StatusText = "打开目录失败：" + ex.Message;
		}
	}

	private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFiles();

	private void DeleteFile_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button b || b.Tag is not string path)
			return;
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
				SimpleLog.Write("下载管理 删除：" + Path.GetFileName(path));
				StatusText = $"已删除 {Path.GetFileName(path)}";
			}
		}
		catch (Exception ex)
		{
			StatusText = "删除失败：" + ex.Message;
		}
		LoadFiles();
	}

	private void ClearAll_Click(object sender, RoutedEventArgs e)
	{
		if (_files.Count == 0)
		{
			StatusText = "缓存为空";
			return;
		}
		var r = MessageBox.Show(this, $"确定清空全部已下载的安装包缓存吗？（共 {_files.Count} 个文件）", "清空缓存",
			MessageBoxButton.OKCancel, MessageBoxImage.Warning);
		if (r != MessageBoxResult.OK)
			return;
		var dir = DownloadService.DownloadDir;
		int removed = 0;
		try
		{
			foreach (var f in Directory.EnumerateFiles(dir))
			{
				try
				{
					File.Delete(f);
					removed++;
				}
				catch
				{
				}
			}
			SimpleLog.Write($"下载管理 清空缓存：删除 {removed} 个文件");
			StatusText = $"已清空缓存（{removed} 个文件）";
		}
		catch (Exception ex)
		{
			StatusText = "清空失败：" + ex.Message;
		}
		LoadFiles();
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
