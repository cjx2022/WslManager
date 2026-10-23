using WslManager.Helpers;
using WslManager.Install;

namespace WslManager.Wslc.Models;

/// <summary>
/// 环境检查清单里的一行（从原「环境检查」窗口搬来，v2.2 起内嵌到「环境与安装」页）。
///
/// 只保留「绑定需要」的派生属性：颜色不走 Brush 对象（主题切换后会留下旧画刷），
/// 而是暴露 <see cref="Status"/>，由 XAML 里的 DataTrigger 取 DynamicResource。
/// </summary>
public class CheckVm : ViewModelBase
{
    private string _name = "";
    private string _detail = "";
    private string _status = "Unknown";
    private string _docUrl = "";
    private bool _fixing;
    private string _fixMessage = "";
    private FixKind _fix = FixKind.None;

    private void RaiseFixDependents()
    {
        OnPropertyChanged(nameof(NeedsFix));
        OnPropertyChanged(nameof(CanFix));
        OnPropertyChanged(nameof(FixText));
        OnPropertyChanged(nameof(FixButtonText));
        OnPropertyChanged(nameof(FixHint));
    }

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
                RaiseFixDependents();
            }
        }
    }

    public FixKind Fix
    {
        get => _fix;
        set
        {
            if (SetField(ref _fix, value, nameof(Fix))) RaiseFixDependents();
        }
    }

    public bool IsFixing
    {
        get => _fixing;
        set
        {
            if (SetField(ref _fixing, value, nameof(IsFixing))) RaiseFixDependents();
        }
    }

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

    /// <summary>是否显示「修复」按钮：未通过、该项有修复动作、且当前没有正在修复。</summary>
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
            if (SetField(ref _docUrl, value, nameof(DocUrl))) OnPropertyChanged(nameof(HasDoc));
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
}
