using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WslManager.Wslc.Models;

/// <summary>本地镜像行模型。</summary>
public sealed class ImageInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private string _repository = "";
    private string _tag = "";
    private string _id = "";
    private string _size = "";
    private string _created = "";
    private ImageUsage _usage = new();

    public string Repository { get => _repository; set { if (_repository != value) { _repository = value ?? ""; Raise(); Raise(nameof(Reference)); } } }
    public string Tag { get => _tag; set { if (_tag != value) { _tag = value ?? ""; Raise(); Raise(nameof(Reference)); } } }
    public string Id { get => _id; set { if (_id != value) { _id = value ?? ""; Raise(); Raise(nameof(ShortId)); } } }
    public string Size { get => _size; set { if (_size != value) { _size = value ?? ""; Raise(); } } }
    public string Created { get => _created; set { if (_created != value) { _created = value ?? ""; Raise(); } } }

    public string Reference => string.IsNullOrEmpty(_tag) || _tag == "<none>"
        ? _repository
        : $"{_repository}:{_tag}";

    public string ShortId => _id.Length > 12 ? _id.Substring(0, 12) : _id;

    // ── 占用情况（由容器清单对账得到，见 WslcService.AttachImageUsage）──

    public ImageUsage Usage
    {
        get => _usage;
        set
        {
            _usage = value ?? new ImageUsage();
            Raise();
            Raise(nameof(InUse));
            Raise(nameof(UsageText));
            Raise(nameof(UsageBrushKey));
            Raise(nameof(UsageTip));
        }
    }

    public bool InUse => _usage.IsInUse;

    /// <summary>列表「占用」列的文案。</summary>
    public string UsageText
    {
        get
        {
            if (!_usage.IsInUse) return "—";
            if (_usage.RunningCount > 0 && _usage.StoppedCount > 0)
                return $"{_usage.RunningCount} 运行 / {_usage.StoppedCount} 停止";
            if (_usage.RunningCount > 0) return $"运行中 · {_usage.RunningCount}";
            return $"已停止容器 · {_usage.StoppedCount}";
        }
    }

    /// <summary>运行中 = 警告色（红/橙提示「删不掉」），仅已停止 = 信息色，无占用 = 灰。</summary>
    public string UsageBrushKey => !_usage.IsInUse
        ? "MutedBrush"
        : _usage.RunningCount > 0 ? "WarnBrush" : "InfoBrush";

    /// <summary>悬停解释：说清楚"删除会发生什么"，而不是只说"被占用"。</summary>
    public string UsageTip
    {
        get
        {
            if (!_usage.IsInUse)
                return "没有容器引用该镜像，删除会真正回收镜像层。";
            var s = $"被 {_usage.Total} 个容器引用：\n";
            if (_usage.RunningCount > 0) s += "  运行中：" + string.Join("、", _usage.RunningNames) + "\n";
            if (_usage.StoppedCount > 0) s += "  已停止：" + string.Join("、", _usage.StoppedNames) + "\n";
            s += "\nwslc 对这类镜像的删除只会「取消标记」——\n" +
                 "不报错、exit=0，但镜像层不删除、磁盘空间不释放，\n" +
                 "镜像只是从列表里消失。要真正回收，先把这些容器删掉。";
            return s;
        }
    }
}
