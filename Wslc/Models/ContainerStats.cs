using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace WslManager.Wslc.Models;

/// <summary>单次性能采样（对应一行 wslc stats），带进度条用的数值化字段。</summary>
public sealed class ContainerStats : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private string _name = "";
    private string _cpu = "";
    private string _mem = "";
    private string _memPercent = "";
    private string _net = "";
    private string _block = "";
    private string _pids = "";

    public string Name { get => _name; set { if (_name != value) { _name = value ?? ""; Raise(); } } }
    public string Cpu { get => _cpu; set { if (_cpu != value) { _cpu = value ?? ""; Raise(); Raise(nameof(CpuValue)); } } }
    public string Mem { get => _mem; set { if (_mem != value) { _mem = value ?? ""; Raise(); } } }
    public string MemPercent { get => _memPercent; set { if (_memPercent != value) { _memPercent = value ?? ""; Raise(); Raise(nameof(MemValue)); } } }
    public string Net { get => _net; set { if (_net != value) { _net = value ?? ""; Raise(); } } }
    public string Block { get => _block; set { if (_block != value) { _block = value ?? ""; Raise(); } } }
    public string Pids { get => _pids; set { if (_pids != value) { _pids = value ?? ""; Raise(); } } }

    /// <summary>CPU 百分比数值（去掉 % 号），用于进度条。</summary>
    public double CpuValue => ParsePercent(_cpu);

    /// <summary>内存占用百分比数值。</summary>
    public double MemValue => ParsePercent(_memPercent);

    private static double ParsePercent(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.Trim().TrimEnd('%').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (v < 0 ? 0 : (v > 100 ? 100 : v))
            : 0;
    }
}
