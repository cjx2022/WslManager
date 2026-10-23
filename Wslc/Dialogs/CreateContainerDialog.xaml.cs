using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WslManager.Wslc.Helpers;
using WslManager.Wslc.Models;

namespace WslManager.Wslc.Dialogs;

public partial class CreateContainerDialog : Window
{
    private readonly ObservableCollection<ContainerSpec.PortMap> _ports = new();
    private readonly ObservableCollection<ContainerSpec.VolumeMap> _volumes = new();
    private readonly ObservableCollection<ContainerSpec.EnvVar> _envs = new();
    private readonly DispatcherTimer _previewTimer = new();
    private readonly IEnumerable<ImageInfo> _localImages;

    private bool _ready;

    /// <summary>网络下拉的第一项：选它 = 不下发 <c>--network</c>（wslc 用自己的默认 bridge）。</summary>
    private const string DefaultNetworkLabel = "默认（bridge）";

    public ContainerSpec Result { get; private set; }

    public CreateContainerDialog(IEnumerable<ImageInfo> localImages, IEnumerable<string> networks)
    {
        InitializeComponent();

        _localImages = localImages ?? Enumerable.Empty<ImageInfo>();

        ListPorts.ItemsSource = _ports;
        ListVolumes.ItemsSource = _volumes;
        ListEnv.ItemsSource = _envs;

        ListLocalImages.ItemsSource = _localImages
            .Select(i => i.Reference)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Take(24)
            .ToList();

        FillNetworks(networks);

        _previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _previewTimer.Tick += (s, e) => UpdatePreview();
        _previewTimer.Start();

        Loaded += (s, e) =>
        {
            _ready = true;
            ChromeHelper.CenterOnOwner(this);
            // 默认给一行端口，降低上手成本
            if (_ports.Count == 0) _ports.Add(new ContainerSpec.PortMap());
            UpdatePreview();
        };

        Closed += (s, e) => _previewTimer.Stop();
    }

    /// <summary>
    /// 填网络下拉。第一项固定是「默认」，后面才是真实网络名 ——
    /// 默认项**必须存在**：容器不指定网络是完全正常的用法，不能逼用户选一个。
    /// </summary>
    private void FillNetworks(IEnumerable<string> networks)
    {
        var items = new List<string> { DefaultNetworkLabel };
        items.AddRange((networks ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

        CmbNetwork.ItemsSource = items;
        // 在构造函数里设选中项：此时 _ready 还是 false，OnAnyChanged 会被守卫挡掉
        CmbNetwork.SelectedIndex = 0;
    }

    private string SelectedNetwork()
    {
        var s = CmbNetwork.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s == DefaultNetworkLabel ? "" : s;
    }

    /// <summary>由「用此镜像新建容器」调用（ShowDialog 之前）。</summary>
    public void PresetImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        TxtImage.Text = reference;
        UpdatePreview();
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ── 行增删 ──
    private void OnAddPort(object sender, RoutedEventArgs e)
    {
        _ports.Add(new ContainerSpec.PortMap());
        UpdatePreview();
    }

    private void OnRemovePort(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.PortMap p) _ports.Remove(p);
        UpdatePreview();
    }

    private void OnAddVolume(object sender, RoutedEventArgs e)
    {
        _volumes.Add(new ContainerSpec.VolumeMap());
        UpdatePreview();
    }

    private void OnRemoveVolume(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.VolumeMap v) _volumes.Remove(v);
        UpdatePreview();
    }

    /// <summary>
    /// 「选择…」：调系统文件夹选择器填宿主路径，省得手打 ——
    /// 手打 Windows 路径最容易错在盘符与反斜杠上，错了要等容器起不来才发现。
    /// 用 .NET 8 新增的 <c>OpenFolderDialog</c>（WPF 自带），不引 WinForms 那套。
    /// </summary>
    private void OnPickHostFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ContainerSpec.VolumeMap v) return;

        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要挂载到容器里的宿主文件夹",
                Multiselect = false
            };

            // 已经有合法路径时就从那（或其父级）打开，省得每次从"此电脑"重新点起
            var current = (v.HostPath ?? "").Trim();
            if (current.Length > 0 && System.IO.Directory.Exists(current))
                dlg.InitialDirectory = current;

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                v.HostPath = dlg.FolderName;   // VolumeMap 实现了 INPC，输入框会跟着更新
                UpdatePreview();
            }
        }
        catch (Exception ex)
        {
            AppDialog.Info(this, "打开文件夹选择器失败", ex.Message);
        }
    }

    private void OnAddEnv(object sender, RoutedEventArgs e)
    {
        _envs.Add(new ContainerSpec.EnvVar());
        UpdatePreview();
    }

    private void OnRemoveEnv(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.EnvVar x) _envs.Remove(x);
        UpdatePreview();
    }

    private void OnPickLocalImage(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string s) TxtImage.Text = s;
    }

    private void OnAnyChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            if (TxtPreview == null) return;
            TxtPreview.Text = BuildSpec().PreviewCommandLine();
        }
        catch { }
    }

    private ContainerSpec BuildSpec()
    {
        var spec = new ContainerSpec
        {
            Image = TxtImage.Text?.Trim() ?? "",
            Name = TxtName.Text?.Trim() ?? "",
            Command = TxtCommand.Text?.Trim() ?? "",
            Detach = ChkDetach.IsChecked == true,
            AutoRemove = ChkAutoRemove.IsChecked == true,
            Tty = ChkTty.IsChecked == true,
            Interactive = ChkTty.IsChecked == true,
            RestartPolicy = TxtRestart.Text?.Trim() ?? "",
            WorkingDir = TxtWorkDir.Text?.Trim() ?? "",
            Network = SelectedNetwork()
        };

        foreach (var p in _ports)
        {
            if (string.IsNullOrWhiteSpace(p.ContainerPort)) continue;
            spec.Ports.Add(new ContainerSpec.PortMap
            {
                HostPort = (p.HostPort ?? "").Trim(),
                ContainerPort = (p.ContainerPort ?? "").Trim(),
                Protocol = (p.Protocol ?? "").Trim()
            });
        }
        foreach (var v in _volumes)
        {
            if (string.IsNullOrWhiteSpace(v.HostPath) && string.IsNullOrWhiteSpace(v.ContainerPath)) continue;
            spec.Volumes.Add(new ContainerSpec.VolumeMap
            {
                HostPath = (v.HostPath ?? "").Trim(),
                ContainerPath = (v.ContainerPath ?? "").Trim(),
                ReadOnly = v.ReadOnly
            });
        }
        foreach (var x in _envs)
        {
            if (string.IsNullOrWhiteSpace(x.Key)) continue;
            spec.Env.Add(new ContainerSpec.EnvVar { Key = x.Key.Trim(), Value = (x.Value ?? "").Trim() });
        }
        return spec;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var spec = BuildSpec();
        if (string.IsNullOrWhiteSpace(spec.Image))
        {
            AppDialog.Info(this, "缺少镜像", "请先填写镜像名称，例如 alpine:latest。");
            TxtImage.Focus();
            return;
        }
        Result = spec;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
