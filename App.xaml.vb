Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Media
Imports WslManager.Helpers

Namespace WslManager

    Partial Public Class App
        Inherits Application

        ''' <summary>主题模式：Auto / Light / Dark</summary>
        Public Shared Property ThemeMode As String = "Auto"
        ''' <summary>当前是否深色（Auto 模式下由系统决定）</summary>
        Public Shared Property IsDark As Boolean = False

        Private Shared _mutex As Mutex = Nothing

        Protected Overrides Sub OnStartup(e As StartupEventArgs)
            MyBase.OnStartup(e)

            Dim args = Environment.GetCommandLineArgs().Skip(1).ToArray()
            SimpleLog.Write("启动参数：" & If(args.Length = 0, "(无)", String.Join(" ", args)))

            ' 一次性命令（--status / --start / --stop / --shutdown）：不需要单实例检查
            If args.Length > 0 AndAlso Not IsResidentMode(args) Then
                RunCommandAsync(args)
                Return
            End If

            ' 单实例
            Dim createdNew As Boolean = False
            _mutex = New Mutex(True, "Global\WslManager.SingleInstance.7C1F", createdNew)
            If Not createdNew Then
                MessageBox.Show("WSL 状态管理器已经在运行了。", "WSL 状态管理器",
                                MessageBoxButton.OK, MessageBoxImage.Information)
                Shutdown()
                Return
            End If

            ThemeMode = AppSettings.GetValue("Theme", "Auto")
            If ThemeMode <> "Light" AndAlso ThemeMode <> "Dark" Then ThemeMode = "Auto"

            ApplyTheme()

            Dim win As New MainWindow()
            If args.Length > 0 AndAlso (args(0) = "--tray" OrElse args(0) = "-b") Then
                ' 后台常驻：只显示托盘图标
                win.StartBackground()
            Else
                win.Show()
            End If
        End Sub

        Private Function IsResidentMode(args As String()) As Boolean
            Return args(0) = "--tray" OrElse args(0) = "-b"
        End Function

        ''' <summary>命令行模式：状态查询 / 开机 / 关机 / 全部关机</summary>
        Private Async Sub RunCommandAsync(args As String())
            Try
                Dim cmd = args(0).ToLowerInvariant()
                SimpleLog.Write("命令行：" & String.Join(" ", args))

                Select Case cmd
                    Case "--status", "-q"
                        Await DumpStatusAsync()

                    Case "--start", "-s"
                        If args.Length > 1 Then
                            Dim r = Await Wsl.WslService.StartAsync(args(1))
                            SimpleLog.Write($"启动 {args(1)} → 退出码={r.ExitCode} 错误={r.ErrorMessage} stderr={r.StdErr.Trim()}")
                            Await Task.Delay(1500)
                            Await DumpStatusAsync()
                        Else
                            SimpleLog.Write("缺少发行版名称")
                        End If

                    Case "--stop", "-t"
                        If args.Length > 1 Then
                            Dim r = Await Wsl.WslService.StopAsync(args(1))
                            SimpleLog.Write($"停止 {args(1)} → 退出码={r.ExitCode} {If(r.ErrorMessage.Length > 0, "错误：" & r.ErrorMessage, "成功")}")
                            Await Task.Delay(800)
                            Await DumpStatusAsync()
                        Else
                            SimpleLog.Write("缺少发行版名称")
                        End If

                    Case "--force-stop", "-f"
                        If args.Length > 1 Then
                            Dim r = Await Wsl.WslService.TerminateAsync(args(1))
                            SimpleLog.Write($"强制终止 {args(1)} → 退出码={r.ExitCode}")
                            Await Task.Delay(800)
                            Await DumpStatusAsync()
                        Else
                            SimpleLog.Write("缺少发行版名称")
                        End If

                    Case "--restart", "-r"
                        If args.Length > 1 Then
                            Dim r = Await Wsl.WslService.RestartAsync(args(1))
                            SimpleLog.Write($"重启 {args(1)} → 退出码={r.ExitCode} 错误={r.ErrorMessage}")
                            Await Task.Delay(2000)
                            Await DumpStatusAsync()
                        Else
                            SimpleLog.Write("缺少发行版名称")
                        End If

                    Case "--shutdown", "-k"
                        Dim r = Await Wsl.WslService.ShutdownAllAsync()
                        SimpleLog.Write($"全部关机 → 退出码={r.ExitCode}")
                        Await Task.Delay(800)
                        Await DumpStatusAsync()

                    Case Else
                        SimpleLog.Write("未知命令：" & cmd)
                End Select
            Catch ex As Exception
                SimpleLog.Write("命令执行失败：" & ex.Message)
            Finally
                Shutdown()
            End Try
        End Sub

        Private Async Function DumpStatusAsync() As Task
            Dim list = Await Wsl.WslService.ListDistrosAsync()
            If list.Count = 0 Then
                SimpleLog.Write("未检测到 WSL 发行版")
                Return
            End If
            For Each d In list
                Dim line = $"  {d.Name}  {d.StateText}  {If(d.IsDefault, "默认", "")}  WSL{d.Version}"
                If d.IsRunning Then
                    Dim ip = Await Wsl.WslService.GetIpAsync(d.Name)
                    If ip.Length > 0 Then line &= "  IP=" & ip
                End If
                SimpleLog.Write(line)
            Next
        End Function

        ''' <summary>应用主题（Auto 时跟随系统）</summary>
        Public Shared Sub ApplyTheme()
            Dim dark As Boolean
            Select Case ThemeMode
                Case "Light" : dark = False
                Case "Dark" : dark = True
                Case Else : dark = Not WindowBackdrop.SystemUsesLightTheme()
            End Select
            IsDark = dark

            Dim dicts = Current.Resources.MergedDictionaries
            ' 索引 0 = Controls.xaml，索引 1 = 主题
            If dicts.Count >= 2 Then
                dicts(1) = New ResourceDictionary With {
                    .Source = New Uri(If(dark, "Themes/Dark.xaml", "Themes/Light.xaml"), UriKind.Relative)
                }
            End If

            ApplyAccent(dark)

            For Each w In Current.Windows
                Dim mw = TryCast(w, MainWindow)
                If mw IsNot Nothing Then mw.OnThemeChanged(dark)
            Next
        End Sub

        ''' <summary>把系统主题色写入资源，并按明暗做对比度调整</summary>
        Private Shared Sub ApplyAccent(dark As Boolean)
            Dim c = WindowBackdrop.GetAccentColor()
            Dim lum As Double = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0

            If dark AndAlso lum < 0.45 Then
                c = LiftColor(c, 0.55)
            ElseIf Not dark AndAlso lum > 0.72 Then
                c = DarkenColor(c, 0.72)
            End If

            Dim hover As Color = If(dark, LiftColor(c, 0.18), DarkenColor(c, 0.12))

            Current.Resources("AccentBrush") = New SolidColorBrush(c)
            Current.Resources("AccentHoverBrush") = New SolidColorBrush(hover)
            Current.Resources("AccentSoftBrush") = New SolidColorBrush(
                Color.FromArgb(If(dark, CByte(60), CByte(38)), c.R, c.G, c.B))
        End Sub

        Private Shared Function LiftColor(c As Color, amount As Double) As Color
            Return Color.FromRgb(
                CByte(Math.Min(255, c.R + (255 - c.R) * amount)),
                CByte(Math.Min(255, c.G + (255 - c.G) * amount)),
                CByte(Math.Min(255, c.B + (255 - c.B) * amount)))
        End Function

        Private Shared Function DarkenColor(c As Color, amount As Double) As Color
            Return Color.FromRgb(
                CByte(c.R * (1 - amount)),
                CByte(c.G * (1 - amount)),
                CByte(c.B * (1 - amount)))
        End Function
    End Class
End Namespace
