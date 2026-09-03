Imports System.ComponentModel
Imports System.Windows.Input

Namespace Helpers

    Public Class RelayCommand
        Implements ICommand

        Private ReadOnly _execute As Action(Of Object)
        Private ReadOnly _canExecute As Func(Of Object, Boolean)

        Public Sub New(execute As Action(Of Object), Optional canExecute As Func(Of Object, Boolean) = Nothing)
            _execute = execute
            _canExecute = canExecute
        End Sub

        Public Event CanExecuteChanged As EventHandler Implements ICommand.CanExecuteChanged

        Public Function CanExecute(parameter As Object) As Boolean Implements ICommand.CanExecute
            Return If(_canExecute Is Nothing, True, _canExecute(parameter))
        End Function

        Public Sub Execute(parameter As Object) Implements ICommand.Execute
            _execute?.Invoke(parameter)
        End Sub

        Public Sub RaiseCanExecuteChanged()
            RaiseEvent CanExecuteChanged(Me, EventArgs.Empty)
        End Sub
    End Class

    Public Class ViewModelBase
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Protected Sub OnPropertyChanged(Optional name As String = "")
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        Protected Function SetField(Of T)(ByRef field As T, value As T, Optional name As String = "") As Boolean
            If EqualityComparer(Of T).Default.Equals(field, value) Then Return False
            field = value
            OnPropertyChanged(name)
            Return True
        End Function
    End Class
End Namespace
