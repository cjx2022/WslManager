using System;

namespace WslManager.Wsl;

public class Distro
{
	public string Name { get; set; }

	public string State { get; set; }

	public int Version { get; set; }

	public bool IsDefault { get; set; }

	public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

	public string StateText => State.ToLowerInvariant() switch
	{
		"running" => "运行中", 
		"stopped" => "已停止", 
		"installing" => "安装中", 
		"uninstalling" => "卸载中", 
		"converting" => "转换中", 
		_ => State, 
	};

	public Distro()
	{
		Name = "";
		State = "Unknown";
		Version = 2;
		IsDefault = false;
	}
}
