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
