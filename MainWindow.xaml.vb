Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Linq
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Threading
Imports WslManager.Helpers
Imports WF = System.Windows.Forms

Namespace WslManager

    Partial Public Class MainWindow
        Inherits Window
        Implements INotifyPropertyChanged

        '================ 字段 =================
        Private ReadOnly _distros As New ObservableCollection(Of DistroViewModel)()
        Private _isBusy As Boolean = False
        Private _busyCount As Integer = 0
        Private _refreshing As Boolean = False
        Private _reallyExit As Boolean = False
        ''' <summary>上次刷新得到的状态摘要，用于判断状态是否真的变了</summary>
        Private _lastSignature As String = ""

        Private _refreshTimer As DispatcherTimer
        Private WithEvents _tray As WF.NotifyIcon

        '================ 属性 =================
        Public ReadOnly Property Distros As ObservableCollection(Of DistroViewModel)
            Get
                Return _distros
            End Get
        End Property

        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                If _isBusy = value Then Return
                _isBusy = value
                RaiseProp(NameOf(IsBusy))
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub RaiseProp(Optional name As String = "")
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        '================ 命令 =================
        Private _startCmd, _stopCmd, _forceStopCmd, _restartCmd, _startAllCmd, _stopAllCmd, _refreshCmd As RelayCommand

        Public ReadOnly Property StartCommand As ICommand
            Get
                If _startCmd Is Nothing Then _startCmd = New RelayCommand(AddressOf DoStart)
                Return _startCmd
            End Get
        End Property

        Public ReadOnly Property StopCommand As ICommand
            Get
                If _stopCmd Is Nothing Then _stopCmd = New RelayCommand(AddressOf DoStop)
                Return _stopCmd
            End Get
        End Property

        ''' <summary>强制关机（跳过软关机，直接 wsl -t）</summary>
        Public ReadOnly Property ForceStopCommand As ICommand
            Get
                If _forceStopCmd Is Nothing Then _forceStopCmd = New RelayCommand(AddressOf DoForceStop)
                Return _forceStopCmd
            End Get
        End Property

        Public ReadOnly Property RestartCommand As ICommand
            Get
                If _restartCmd Is Nothing Then _restartCmd = New RelayCommand(AddressOf DoRestart)
                Return _restartCmd
            End Get
        End Property

        Public ReadOnly Property StartAllCommand As ICommand
            Get
                If _startAllCmd Is Nothing Then _startAllCmd = New RelayCommand(AddressOf DoStartAll)
                Return _startAllCmd
            End Get
        End Property

        Public ReadOnly Property StopAllCommand As ICommand
            Get
                If _stopAllCmd Is Nothing Then _stopAllCmd = New RelayCommand(AddressOf DoStopAll)
                Return _stopAllCmd
            End Get
        End Property

        Public ReadOnly Property RefreshCommand As ICommand
            Get
                If _refreshCmd Is Nothing Then _refreshCmd = New RelayCommand(AddressOf DoRefresh)
                Return _refreshCmd
            End Get
        End Property

        '================ 构造 =================
        Public Sub New()
            InitializeComponent()
            DataContext = Me

            TitleIcon.Source = AppIcon.MakeImageSource(False, 32)
            Me.Icon = AppIcon.MakeImageSource(False, 64)

            SetupTray()
            SetupTimers()

            ' 注意：XAML 中已默认勾选，此处再赋相同值不会触发 Checked 事件，
            ' 必须显式启动计时器，否则自动刷新永远不会生效。
            Dim autoRefresh = (AppSettings.GetValue("AutoRefresh", "1") = "1")
            AutoRefreshCheck.IsChecked = autoRefresh
            _refreshTimer.IsEnabled = autoRefresh
        End Sub

        Private Sub SetupTimers()
            ' 定时刷新走静默模式：不显示进度条、不打扰界面，只在状态真变化时留痕
            _refreshTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(5)}
            AddHandler _refreshTimer.Tick, Sub() RefreshAsyncWrapper()
            ' 注意：不要再用一个独立的 1 秒计时器去刷新"已运行时长"，
            ' 那会让卡片上的时间文本每秒跳动，看着就是"前台刷新抖动"。
            ' 运行时长改为在每次状态刷新里顺带计算（见 UpdateUptimes）。
        End Sub

        Private Async Sub RefreshAsyncWrapper()
            Await RefreshAsync(silent:=True)
        End Sub

        '================ 窗口生命周期 =================
        Private Sub MainWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            OnThemeChanged(App.IsDark)
            AwaitFirstRun()
            Me.Focus()
        End Sub

        ''' <summary>后台常驻模式：不显示主窗口，只在托盘运行</summary>
        Public Sub StartBackground()
            SimpleLog.Write("以托盘常驻模式启动")
            ShowInTaskbar = False
            AwaitFirstRun()
        End Sub

        Private Async Sub AwaitFirstRun()
            Await RefreshAsync()
            Dim v = Await Wsl.WslService.GetWslVersionAsync()
            WslVersionText.Text = v
        End Sub

        Private Sub MainWindow_SourceInitialized(sender As Object, e As EventArgs) Handles Me.SourceInitialized
            ' 只申请系统级效果（圆角 + 深色模式下的系统绘制区域）。
            ' 背景一律自绘：WPF 的客户区是不透明渲染的，DWM 材质透不出来，
            ' 若把 RootBorder 背景改成 Transparent 会直接变成纯黑（浅色主题下尤其明显）。
            WindowBackdrop.Apply(Me, App.IsDark)
        End Sub

        Private Sub MainWindow_StateChanged(sender As Object, e As EventArgs) Handles Me.StateChanged
            If WindowState = WindowState.Maximized Then
                RootBorder.Margin = New Thickness(7)
                RootBorder.CornerRadius = New CornerRadius(0)
                MaxGlyph.Text = ChrW(&HE923).ToString()
            Else
                RootBorder.Margin = New Thickness(0)
                RootBorder.CornerRadius = New CornerRadius(9)
                MaxGlyph.Text = ChrW(&HE922).ToString()
            End If
        End Sub

        Private Sub MainWindow_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
            If _reallyExit Then
                If _tray IsNot Nothing Then
                    _tray.Visible = False
                    _tray.Dispose()
                    _tray = Nothing
                End If
                Return
            End If
            e.Cancel = True
            Hide()
            SetStatus("已最小化到系统托盘，双击托盘图标可重新打开")
        End Sub

        Private Async Sub MainWindow_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
            If e.Key = Key.F5 Then
                ' 手动刷新：给出完整反馈（与定时器触发的静默刷新区分开）
                Await RefreshAsync()
                SetStatus("已手动刷新")
            ElseIf e.Key = Key.Escape Then
                Hide()
            End If
        End Sub

        '================ 刷新 =================
        ''' <summary>
        ''' 刷新发行版状态。
        ''' silent=True（定时刷新）：不显示忙碌进度条、不弹状态栏提示、状态没变就不写日志也不动时间戳，
        ''' 让自动刷新在界面上完全无感，只在后台进行。
        ''' silent=False（手动点刷新）：给出进度条与更新时间等完整反馈。
        ''' </summary>
        Public Async Function RefreshAsync(Optional silent As Boolean = False) As Task
            If _refreshing Then Return
            _refreshing = True
            If Not silent Then BusyEnter()
            Try
                Dim list = Await Wsl.WslService.ListDistrosAsync()

                ' 移除已不存在的
                For i = _distros.Count - 1 To 0 Step -1
                    Dim n = _distros(i).Name
                    If Not list.Any(Function(d) String.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)) Then
                        _distros.RemoveAt(i)
                    End If
                Next

                ' 新增或更新
                For Each d In list
                    Dim exist = _distros.FirstOrDefault(
                        Function(x) String.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))
                    If exist Is Nothing Then
                        Dim vm As New DistroViewModel()
                        vm.UpdateFrom(d)
                        _distros.Add(vm)
                    Else
                        exist.UpdateFrom(d)
                    End If
                Next

                SortDistros()
                UpdateSummary()

                ' 只有状态真的发生变化时才留痕（日志 + 更新时间）。
                ' 否则每 5 秒写一行日志、跳一次时间，就是在界面和日志里"看见刷新"了。
                Dim signature = String.Join(" | ", list.Select(Function(d) d.Name & "=" & d.State).ToArray())
                Dim changed = (signature <> _lastSignature)
                _lastSignature = signature
                If changed Then
                    SimpleLog.Write("状态变化：" & signature)
                    LastUpdateText.Text = "更新于 " & DateTime.Now.ToString("HH:mm:ss")
                ElseIf Not silent Then
                    LastUpdateText.Text = "更新于 " & DateTime.Now.ToString("HH:mm:ss")
                End If

                EmptyState.Visibility = If(_distros.Count = 0, Visibility.Visible, Visibility.Collapsed)

                ' 后台刷新 IP、运行时长与图标
                Dim ignored = UpdateIpsAsync()
                UpdateUptimes()
                UpdateTrayIcon()
            Catch ex As Exception
                If silent Then
                    SimpleLog.Write("后台刷新失败：" & ex.Message)
                Else
                    SetStatus("读取状态失败：" & ex.Message)
                End If
            Finally
                _refreshing = False
                If Not silent Then BusyLeave()
            End Try
        End Function

        Private Sub SortDistros()
            For i = 0 To _distros.Count - 1
                Dim min = i
                For j = i + 1 To _distros.Count - 1
                    If CompareDistro(_distros(j), _distros(min)) < 0 Then min = j
                Next
                If min <> i Then _distros.Move(min, i)
            Next
        End Sub

        Private Function CompareDistro(a As DistroViewModel, b As DistroViewModel) As Integer
            If a.IsDefault <> b.IsDefault Then Return If(a.IsDefault, -1, 1)
            Return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Async Function UpdateIpsAsync() As Task
            For Each vm In _distros.ToList()
                Try
                    If vm.IsRunning Then
                        Dim ip = Await Wsl.WslService.GetIpAsync(vm.Name)
                        vm.Ip = If(ip.Length > 0, ip, "未获取到 IP")
                    Else
                        vm.Ip = ""
                    End If
                Catch
                End Try
            Next
        End Function

        ''' <summary>随状态刷新一起计算运行时长，避免单独的秒级计时器造成界面每秒抖动。</summary>
        Private Sub UpdateUptimes()
            For Each vm In _distros
                Dim st = Wsl.WslService.GetStartTime(vm.Name)
                If st.HasValue AndAlso vm.IsRunning Then
                    vm.Uptime = FormatUptime(DateTime.Now - st.Value)
                Else
                    vm.Uptime = ""
                End If
            Next
        End Sub

        Private Function FormatUptime(ts As TimeSpan) As String
            If ts.TotalHours >= 1 Then
                Return Math.Floor(ts.TotalHours) & " 小时 " & ts.Minutes & " 分"
            ElseIf ts.TotalMinutes >= 1 Then
                Return Math.Floor(ts.TotalMinutes) & " 分 " & ts.Seconds & " 秒"
            Else
                Return ts.Seconds & " 秒"
            End If
        End Function

        Private Sub UpdateSummary()
            Dim total = _distros.Count
            Dim running = _distros.AsEnumerable().Count(Function(d) d.IsRunning)
            SummaryText.Text = $"共 {total} 个发行版  ·  {running} 个运行中  ·  {total - running} 个已停止"
        End Sub

        Private Sub BusyEnter()
            _busyCount += 1
            If _busyCount = 1 Then IsBusy = True
        End Sub

        Private Sub BusyLeave()
            _busyCount -= 1
            If _busyCount <= 0 Then
                _busyCount = 0
                IsBusy = False
            End If
        End Sub

        Private Sub SetStatus(msg As String)
            StatusMessage.Text = msg
            SimpleLog.Write(msg)
        End Sub

        Private Function Find(name As String) As DistroViewModel
            Return _distros.FirstOrDefault(Function(x) String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        End Function

        '================ 发行版操作 =================
        Private Async Sub DoStart(param As Object)
            Await RunAction(CStr(param), "开机", Function() Wsl.WslService.StartAsync(CStr(param)))
        End Sub

        ''' <summary>软关机：在发行版内执行 shutdown，超时（默认 15s）后自动转强制终止</summary>
        Private Async Sub DoStop(param As Object)
            Dim n = CStr(param)
            Await RunAction(n, "关机",
                            Function() Wsl.WslService.StopAsync(n, True),
                            Function(r) If(r.Graceful,
                                           $"{n} 已正常关机",
                                           $"{n} 已强制终止（软关机超时，未落盘的数据可能丢失）"))
        End Sub

        ''' <summary>强制关机：跳过软关机，直接 wsl -t</summary>
        Private Async Sub DoForceStop(param As Object)
            Dim n = CStr(param)
            Await RunAction(n, "强制关机", Function() Wsl.WslService.TerminateAsync(n))
        End Sub

        Private Async Sub DoRestart(param As Object)
            Dim n = CStr(param)
            Await RunAction(n, "重启", Function() Wsl.WslService.RestartAsync(n, True))
        End Sub

        Private Async Sub DoStartAll(param As Object)
            Dim targets = _distros.Where(Function(d) Not d.IsRunning).Select(Function(d) d.Name).ToList()
            If targets.Count = 0 Then
                SetStatus("所有发行版都已在运行中")
                Return
            End If
            BusyEnter()
            SetStatus($"正在开机 {targets.Count} 个发行版…")
            Dim ok As Integer = 0
            For Each n In targets
                Dim r = Await Wsl.WslService.StartAsync(n)
                If r.Success Then ok += 1
            Next
            BusyLeave()
            SetStatus($"已开机 {ok}/{targets.Count} 个发行版")
            Await RefreshAsync()
        End Sub

        Private Async Sub DoStopAll(param As Object)
            BusyEnter()
            SetStatus("正在软关机所有发行版，未响应者将强制关闭…")
            Dim r = Await Wsl.WslService.ShutdownAllAsync(True)
            BusyLeave()
            SetStatus(If(r.Success, "已关闭所有发行版（先软关机，再 wsl --shutdown 兜底）", "关闭失败：" & r.ErrorMessage))
            Await RefreshAsync()
        End Sub

        Private Async Sub DoRefresh(param As Object)
            Await RefreshAsync()
        End Sub

        Private Async Function RunAction(name As String, verb As String, action As Func(Of Task(Of Wsl.WslResult)),
                                          Optional describe As Func(Of Wsl.WslResult, String) = Nothing) As Task
            Dim vm = Find(name)
            If vm Is Nothing Then Return
            vm.IsBusy = True
            BusyEnter()
            SetStatus($"正在{verb} {name}…")
            Try
                Dim r = Await action()
                If r.Success Then
                    SetStatus(If(describe IsNot Nothing, describe(r), $"{name} 已{verb}"))
                Else
                    Dim err = If(r.ErrorMessage.Length > 0, r.ErrorMessage, r.StdErr.Trim())
                    If err.Length = 0 Then err = "退出码 " & r.ExitCode
                    SetStatus($"{name} {verb}失败：{err}")
                End If
            Catch ex As Exception
                SetStatus($"{name} {verb}异常：{ex.Message}")
            Finally
                vm.IsBusy = False
                BusyLeave()
            End Try
            Await RefreshAsync()
        End Function

        '================ 卡片按钮 =================
        Private Sub TerminalButton_Click(sender As Object, e As RoutedEventArgs)
            Dim name = CStr(CType(sender, Button).Tag)
            SetStatus("正在打开 " & name & " 终端…")
            Wsl.WslService.OpenTerminal(name)
        End Sub

        Private Sub FilesButton_Click(sender As Object, e As RoutedEventArgs)
            Dim name = CStr(CType(sender, Button).Tag)
            SetStatus("正在打开 " & name & " 文件系统…")
            Wsl.WslService.OpenFiles(name)
        End Sub

        Private Sub MoreButton_Click(sender As Object, e As RoutedEventArgs)
            Dim btn = CType(sender, Button)
            If btn.ContextMenu IsNot Nothing Then
                btn.ContextMenu.PlacementTarget = btn
                btn.ContextMenu.IsOpen = True
            End If
            e.Handled = True
        End Sub

        Private Function OwnerName(item As Object) As String
            Dim mi = TryCast(item, MenuItem)
            If mi Is Nothing Then Return ""
            Dim cm = TryCast(mi.Parent, ContextMenu)
            If cm Is Nothing Then Return ""
            Dim btn = TryCast(cm.PlacementTarget, Button)
            If btn Is Nothing Then Return ""
            Return CStr(btn.Tag)
        End Function

        Private Async Sub SetDefault_Click(sender As Object, e As RoutedEventArgs)
            Dim name = OwnerName(sender)
            If name.Length = 0 Then Return
            BusyEnter()
            Dim r = Await Wsl.WslService.SetDefaultAsync(name)
            BusyLeave()
            SetStatus(If(r.Success, $"已将 {name} 设为默认发行版", "设置失败：" & r.ErrorMessage))
            Await RefreshAsync()
        End Sub

        Private Sub CopyIp_Click(sender As Object, e As RoutedEventArgs)
            Dim name = OwnerName(sender)
            Dim vm = Find(name)
            If vm Is Nothing Then Return
            If vm.Ip.Length > 0 AndAlso vm.Ip <> "未获取到 IP" Then
                Clipboard.SetText(vm.Ip)
                SetStatus("已复制 IP：" & vm.Ip)
            Else
                SetStatus("该发行版当前没有可用 IP")
            End If
        End Sub

        Private Sub CopyName_Click(sender As Object, e As RoutedEventArgs)
            Dim name = OwnerName(sender)
            If name.Length > 0 Then
                Clipboard.SetText(name)
                SetStatus("已复制发行版名称：" & name)
            End If
        End Sub

        Private Async Sub ForceStop_Click(sender As Object, e As RoutedEventArgs)
            Dim name = OwnerName(sender)
            If name.Length = 0 Then Return
            Await RunAction(name, "强制关机", Function() Wsl.WslService.TerminateAsync(name))
        End Sub

        '================ 标题栏 =================
        Private Sub TitleBar_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
            If e.ClickCount = 2 Then
                WindowState = If(WindowState = WindowState.Maximized, WindowState.Normal, WindowState.Maximized)
            Else
                Try
                    DragMove()
                Catch
                End Try
            End If
        End Sub

        Private Sub MinButton_Click(sender As Object, e As RoutedEventArgs)
            WindowState = WindowState.Minimized
        End Sub

        Private Sub MaxButton_Click(sender As Object, e As RoutedEventArgs)
            WindowState = If(WindowState = WindowState.Maximized, WindowState.Normal, WindowState.Maximized)
        End Sub

        Private Sub HideButton_Click(sender As Object, e As RoutedEventArgs)
            Hide()
            SetStatus("已最小化到系统托盘，双击托盘图标可重新打开")
        End Sub

        Private Sub ThemeButton_Click(sender As Object, e As RoutedEventArgs)
            App.ThemeMode = If(App.IsDark, "Light", "Dark")
            AppSettings.SetValue("Theme", App.ThemeMode)
            App.ApplyTheme()
        End Sub

        Private Sub AutoRefresh_Changed(sender As Object, e As RoutedEventArgs)
            ' XAML 初始化阶段会触发一次，此时定时器尚未创建
            If _refreshTimer Is Nothing Then Return
            Dim enabled = AutoRefreshCheck.IsChecked.GetValueOrDefault()
            AppSettings.SetValue("AutoRefresh", If(enabled, "1", "0"))
            _refreshTimer.IsEnabled = enabled
            SetStatus(If(enabled, "已开启自动刷新（每 3 秒）", "已关闭自动刷新"))
        End Sub

        Public Sub OnThemeChanged(dark As Boolean)
            ThemeGlyph.Text = If(dark, ChrW(&HE706).ToString(), ChrW(&HE708).ToString())
            WindowBackdrop.SetImmersiveDark(Me, dark)
        End Sub

        '================ 系统托盘 =================
        Private Sub SetupTray()
            _tray = New WF.NotifyIcon With {
                .Icon = AppIcon.MakeIcon(False, 64),
                .Text = "WSL 状态管理器",
                .Visible = True
            }

            Dim menu As New WF.ContextMenuStrip()
            Dim miOpen As New WF.ToolStripMenuItem("打开主界面")
            Dim miStart As New WF.ToolStripMenuItem("全部开机")
            Dim miStop As New WF.ToolStripMenuItem("全部关机")
            Dim miExit As New WF.ToolStripMenuItem("退出")
            miOpen.Font = New System.Drawing.Font(miOpen.Font, System.Drawing.FontStyle.Bold)

            AddHandler miOpen.Click, Sub() ShowMainWindow()
            AddHandler miStart.Click, Sub() DoStartAll(Nothing)
            AddHandler miStop.Click, Sub() DoStopAll(Nothing)
            AddHandler miExit.Click, Sub() ReallyExit()

            menu.Items.Add(miOpen)
            menu.Items.Add(New WF.ToolStripSeparator())
            menu.Items.Add(miStart)
            menu.Items.Add(miStop)
            menu.Items.Add(New WF.ToolStripSeparator())
            menu.Items.Add(miExit)
            _tray.ContextMenuStrip = menu
        End Sub

        Private Sub UpdateTrayIcon()
            If _tray Is Nothing Then Return
            Dim running = _distros.Any(Function(d) d.IsRunning)
            _tray.Icon = AppIcon.MakeIcon(running, 64)
            _tray.Text = If(running,
                            "WSL 状态管理器 — " & _distros.AsEnumerable().Count(Function(d) d.IsRunning) & " 个运行中",
                            "WSL 状态管理器 — 全部已停止")
        End Sub

        Private Sub ShowMainWindow()
            Show()
            If WindowState = WindowState.Minimized Then WindowState = WindowState.Normal
            Activate()
            Topmost = True
            Topmost = False
        End Sub

        Private Sub ReallyExit()
            _reallyExit = True
            Application.Current.Shutdown()
        End Sub

        Private Sub Tray_MouseDoubleClick(sender As Object, e As WF.MouseEventArgs) Handles _tray.MouseDoubleClick
            If e.Button = WF.MouseButtons.Left Then ShowMainWindow()
        End Sub
    End Class
End Namespace
