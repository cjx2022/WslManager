Imports WslManager.Helpers

Namespace WslManager

    ''' <summary>发行版卡片视图模型</summary>
    Public Class DistroViewModel
        Inherits ViewModelBase

        Private _name As String = ""
        Private _state As String = "Unknown"
        Private _version As Integer = 2
        Private _isDefault As Boolean = False
        Private _ip As String = ""
        Private _isBusy As Boolean = False
        Private _uptime As String = ""

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                SetField(_name, value, NameOf(Name))
            End Set
        End Property

        Public Property State As String
            Get
                Return _state
            End Get
            Set(value As String)
                If SetField(_state, value, NameOf(State)) Then
                    OnPropertyChanged(NameOf(IsRunning))
                    OnPropertyChanged(NameOf(StatusText))
                    OnPropertyChanged(NameOf(StateText))
                    OnPropertyChanged(NameOf(CanStart))
                    OnPropertyChanged(NameOf(CanStop))
                End If
            End Set
        End Property

        Public Property Version As Integer
            Get
                Return _version
            End Get
            Set(value As Integer)
                If SetField(_version, value, NameOf(Version)) Then
                    OnPropertyChanged(NameOf(VersionText))
                End If
            End Set
        End Property

        Public Property IsDefault As Boolean
            Get
                Return _isDefault
            End Get
            Set(value As Boolean)
                SetField(_isDefault, value, NameOf(IsDefault))
            End Set
        End Property

        Public Property Ip As String
            Get
                Return _ip
            End Get
            Set(value As String)
                If SetField(_ip, value, NameOf(Ip)) Then
                    OnPropertyChanged(NameOf(StatusText))
                End If
            End Set
        End Property

        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                If SetField(_isBusy, value, NameOf(IsBusy)) Then
                    OnPropertyChanged(NameOf(CanStart))
                    OnPropertyChanged(NameOf(CanStop))
                End If
            End Set
        End Property

        Public Property Uptime As String
            Get
                Return _uptime
            End Get
            Set(value As String)
                If SetField(_uptime, value, NameOf(Uptime)) Then
                    OnPropertyChanged(NameOf(StatusText))
                End If
            End Set
        End Property

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return String.Equals(_state, "Running", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>可开机：当前已停止且未在处理中</summary>
        Public ReadOnly Property CanStart As Boolean
            Get
                Return Not IsRunning AndAlso Not _isBusy
            End Get
        End Property

        ''' <summary>可关机 / 重启：当前运行中且未在处理中</summary>
        Public ReadOnly Property CanStop As Boolean
            Get
                Return IsRunning AndAlso Not _isBusy
            End Get
        End Property

        Public ReadOnly Property StateText As String
            Get
                Select Case _state.ToLowerInvariant()
                    Case "running" : Return "运行中"
                    Case "stopped" : Return "已停止"
                    Case "installing" : Return "安装中"
                    Case "uninstalling" : Return "卸载中"
                    Case "converting" : Return "转换中"
                    Case Else : Return _state
                End Select
            End Get
        End Property

        Public ReadOnly Property VersionText As String
            Get
                Return "WSL " & _version.ToString()
            End Get
        End Property

        ''' <summary>第二行摘要：状态 · IP · 运行时长</summary>
        Public ReadOnly Property StatusText As String
            Get
                Dim parts As New List(Of String) From {StateText}
                If _ip.Length > 0 Then parts.Add(_ip)
                If _uptime.Length > 0 Then parts.Add("已运行 " & _uptime)
                Return String.Join("  ·  ", parts)
            End Get
        End Property

        Public Sub UpdateFrom(d As Wsl.Distro)
            Name = d.Name
            State = d.State
            Version = d.Version
            IsDefault = d.IsDefault
        End Sub
    End Class
End Namespace
