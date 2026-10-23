using System;
using System.Windows.Input;

namespace WslManager.Wslc.Helpers;

/// <summary>极简 ICommand 实现，避免引入 MVVM 框架。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object> _exec;
    private readonly Func<object, bool> _canExec;

    public RelayCommand(Action<object> exec, Func<object, bool> canExec = null)
    {
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
        _canExec = canExec;
    }

    public RelayCommand(Action exec, Func<bool> canExec = null)
    {
        _exec = _ => exec();
        if (canExec != null) _canExec = _ => canExec();
    }

    public bool CanExecute(object parameter) => _canExec == null || _canExec(parameter);

    public void Execute(object parameter) => _exec(parameter);

    public event EventHandler CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
}
