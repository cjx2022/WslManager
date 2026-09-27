namespace WslManager.Install;

public enum CheckStatus
{
	Unknown,
	Ok,
	Warn,
	Fail
}

public class CheckItem
{
	public string Name { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Status { get; set; } = "Unknown";

	public string DocUrl { get; set; } = "";

	public string DocName { get; set; } = "";

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
}
