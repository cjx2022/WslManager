using System;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WslManager.Helpers;

public class RelayCommand : ICommand
{
	private readonly Action<object> _execute;

	private readonly Func<object, bool> _canExecute;

	public event EventHandler CanExecuteChanged;

	public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
	{
		_execute = execute;
		_canExecute = canExecute;
	}

	public bool CanExecute(object parameter)
	{
		if (_canExecute != null)
		{
			return _canExecute(RuntimeHelpers.GetObjectValue(parameter));
		}
		return true;
	}

	bool ICommand.CanExecute(object parameter)
	{
		//ILSpy generated this explicit interface implementation from .override directive in CanExecute
		return this.CanExecute(parameter);
	}

	public void Execute(object parameter)
	{
		_execute?.Invoke(RuntimeHelpers.GetObjectValue(parameter));
	}

	void ICommand.Execute(object parameter)
	{
		//ILSpy generated this explicit interface implementation from .override directive in Execute
		this.Execute(parameter);
	}

	public void RaiseCanExecuteChanged()
	{
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}
