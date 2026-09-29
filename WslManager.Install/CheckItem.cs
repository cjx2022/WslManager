namespace WslManager.Install;

public enum CheckStatus
{
	Unknown,
	Ok,
	Warn,
	Fail
}

/// <summary>单项检查的可修复类型。</summary>
public enum FixKind
{
	/// <summary>不可自动修复，只能看文档。</summary>
	None,

	/// <summary>安装 / 更新 WSL 组件：wsl --install --no-distribution。</summary>
	InstallWsl,

	/// <summary>启动相关系统服务。</summary>
	StartService,

	/// <summary>用 DISM 启用 Windows 功能（虚拟机平台 / WSL）。</summary>
	EnableFeature,

	/// <summary>以管理员身份重启自身。</summary>
	Elevate
}

public class CheckItem
{
	public string Name { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Status { get; set; } = "Unknown";

	public string DocUrl { get; set; } = "";

	public string DocName { get; set; } = "";

	/// <summary>该项可用的修复动作；None 表示不可自动修复。</summary>
	public FixKind Fix { get; set; } = FixKind.None;

	public string Glyph => Status switch
	{
		"Ok" => "\uE73E",
		"Warn" => "\uE7BA",
		"Fail" => "\uE711",
		_ => "\uE946",
	};

	public string StatusText => Status switch
	{
		"Ok" => "通过",
		"Warn" => "注意",
		"Fail" => "未通过",
		_ => "未知",
	};

	/// <summary>是否需要修复（非通过且有可用修复动作）。</summary>
	public bool NeedsFix => !string.Equals(Status, "Ok", System.StringComparison.OrdinalIgnoreCase);

	/// <summary>是否显示「修复」按钮。</summary>
	public bool CanFix => NeedsFix && Fix != FixKind.None;

	/// <summary>修复按钮文案。</summary>
	public string FixText => Fix switch
	{
		FixKind.InstallWsl => "安装 WSL",
		FixKind.StartService => "启动服务",
		FixKind.EnableFeature => "启用功能",
		FixKind.Elevate => "提权重启",
		_ => ""
	};

	// ---- 以下为 UI 绑定用的通知属性（由 ViewModel 包装）----
	public string FixHint => CanFix
		? $"可自动修复：{FixText}"
		: (NeedsFix ? "需手动处理，请参考右侧文档" : "");
}
