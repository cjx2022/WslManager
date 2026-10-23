using System;
using System.Collections.Generic;
using WslManager.Helpers;
using WslManager.Wsl;

namespace WslManager.UI;

public class DistroViewModel : ViewModelBase
{
	private string _name;

	private string _state;

	private int _version;

	private bool _isDefault;

	private string _ip;

	private bool _isBusy;

	private string _uptime;

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			SetField(ref _name, value, "Name");
		}
	}

	public string State
	{
		get
		{
			return _state;
		}
		set
		{
			if (SetField(ref _state, value, "State"))
			{
				OnPropertyChanged("IsRunning");
				OnPropertyChanged("StatusText");
				OnPropertyChanged("StateText");
				OnPropertyChanged("CanStart");
				OnPropertyChanged("CanStop");
			}
		}
	}

	public int Version
	{
		get
		{
			return _version;
		}
		set
		{
			if (SetField(ref _version, value, "Version"))
			{
				OnPropertyChanged("VersionText");
			}
		}
	}

	public bool IsDefault
	{
		get
		{
			return _isDefault;
		}
		set
		{
			SetField(ref _isDefault, value, "IsDefault");
		}
	}

	public string Ip
	{
		get
		{
			return _ip;
		}
		set
		{
			if (SetField(ref _ip, value, "Ip"))
			{
				OnPropertyChanged("StatusText");
			}
		}
	}

	public bool IsBusy
	{
		get
		{
			return _isBusy;
		}
		set
		{
			if (SetField(ref _isBusy, value, "IsBusy"))
			{
				OnPropertyChanged("CanStart");
				OnPropertyChanged("CanStop");
			}
		}
	}

	public string Uptime
	{
		get
		{
			return _uptime;
		}
		set
		{
			if (SetField(ref _uptime, value, "Uptime"))
			{
				OnPropertyChanged("StatusText");
			}
		}
	}

	private string _usageCpu = "";
	private string _usageMem = "";
	private string _usageTip = "";

	/// <summary>
	/// 卡片正中的利用率（v2.4 加、v2.5 修）：上行「CPU 12%」、下行「内存 9%」。
	/// 数据来自 WslStat 在发行版内读 /proc —— 旧实现读 Vmmem 进程的 TotalProcessorTime
	/// 会被系统拒绝（AccessDenied），整段静默失败，界面上一片空白。
	/// ⚠ 所有发行版共享同一台虚拟机，这是虚拟机级读数；已停止的发行版为空。
	/// </summary>
	public string UsageCpu
	{
		get { return _usageCpu; }
		set { SetField(ref _usageCpu, value, "UsageCpu"); }
	}

	public string UsageMem
	{
		get { return _usageMem; }
		set { SetField(ref _usageMem, value, "UsageMem"); }
	}

	// ── 只有百分比数字变色（v2.5.1）──
	// 界面把一行拆成「标签 + 数值」两个 TextBlock：标签（CPU / 内存）固定用次要色，
	// 数值（9% / —）按档位换色。这里只放数值那一段，标签写死在 XAML 里。
	private string _usageCpuValue = "";
	private string _usageMemValue = "";

	public string UsageCpuValue
	{
		get { return _usageCpuValue; }
		set { SetField(ref _usageCpuValue, value, "UsageCpuValue"); }
	}

	public string UsageMemValue
	{
		get { return _usageMemValue; }
		set { SetField(ref _usageMemValue, value, "UsageMemValue"); }
	}

	public string UsageTip
	{
		get { return _usageTip; }
		set { SetField(ref _usageTip, value, "UsageTip"); }
	}

	// ── 颜色档位（v2.5.1）──
	// "ok" / "warn" / "danger" / ""（无读数）。
	// 只存档位、不存 Brush：颜色由 XAML 的 DataTrigger 取 DynamicResource，
	// 切浅/深色主题时能立刻跟着换（Brush 存进 ViewModel 就会卡在旧主题色上）。
	private string _usageCpuLevel = "";
	private string _usageMemLevel = "";

	public string UsageCpuLevel
	{
		get { return _usageCpuLevel; }
		set { SetField(ref _usageCpuLevel, value, "UsageCpuLevel"); }
	}

	public string UsageMemLevel
	{
		get { return _usageMemLevel; }
		set { SetField(ref _usageMemLevel, value, "UsageMemLevel"); }
	}

	public bool IsRunning => string.Equals(_state, "Running", StringComparison.OrdinalIgnoreCase);

	public bool CanStart
	{
		get
		{
			if (!IsRunning)
			{
				return !_isBusy;
			}
			return false;
		}
	}

	public bool CanStop
	{
		get
		{
			if (IsRunning)
			{
				return !_isBusy;
			}
			return false;
		}
	}

	public string StateText => _state.ToLowerInvariant() switch
	{
		"running" => "运行中", 
		"stopped" => "已停止", 
		"installing" => "安装中", 
		"uninstalling" => "卸载中", 
		"converting" => "转换中", 
		_ => _state, 
	};

	public string VersionText => "WSL " + _version;

	public string StatusText
	{
		get
		{
			List<string> list = new List<string> { StateText };
			if (_ip.Length > 0)
			{
				list.Add(_ip);
			}
			if (_uptime.Length > 0)
			{
				list.Add("已运行 " + _uptime);
			}
			return string.Join("  ·  ", list);
		}
	}

	public DistroViewModel()
	{
		_name = "";
		_state = "Unknown";
		_version = 2;
		_isDefault = false;
		_ip = "";
		_isBusy = false;
		_uptime = "";
	}

	public void UpdateFrom(Distro d)
	{
		Name = d.Name;
		State = d.State;
		Version = d.Version;
		IsDefault = d.IsDefault;
	}
}
