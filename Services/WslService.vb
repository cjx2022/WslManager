Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports WslManager.Helpers

Namespace Wsl

    ''' <summary>
    ''' WSL 发行版信息
    ''' </summary>
    Public Class Distro
        Public Property Name As String = ""
        Public Property State As String = "Unknown"
        Public Property Version As Integer = 2
        Public Property IsDefault As Boolean = False

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return String.Equals(State, "Running", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property StateText As String
            Get
                Select Case State.ToLowerInvariant()
                    Case "running" : Return "运行中"
                    Case "stopped" : Return "已停止"
                    Case "installing" : Return "安装中"
                    Case "uninstalling" : Return "卸载中"
                    Case "converting" : Return "转换中"
                    Case Else : Return State
                End Select
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 命令执行结果
    ''' </summary>
    Public Class WslResult
        Public Property ExitCode As Integer = -1
        Public Property StdOut As String = ""
        Public Property StdErr As String = ""
        Public Property Success As Boolean = False
        Public Property ErrorMessage As String = ""

        ''' <summary>优雅（软）关机成功：发行版内的 shutdown 正常完成</summary>
        Public Property Graceful As Boolean = False

        ''' <summary>优雅关机超时或失败，已回退为强制终止（wsl -t）</summary>
        Public Property Forced As Boolean = False
    End Class

    ''' <summary>
    ''' wsl.exe 调用封装
    ''' </summary>
    Public Module WslService

        ''' <summary>发行版启动时间记录（用于显示已运行时长）</summary>
        Private ReadOnly _startTimes As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        Private _wslVersionCache As String = ""

        ''' <summary>自动识别 UTF-16LE / UTF-8 输出（wsl -l -v 输出为 UTF-16LE）</summary>
        Private Function Decode(bytes As Byte()) As String
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return ""
            Try
                If bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
                    Return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
                End If
                If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then
                    Return New UTF8Encoding(False).GetString(bytes, 3, bytes.Length - 3)
                End If
                ' 无 BOM：若含大量 NUL 交错则判定为 UTF-16LE
                If bytes.Length >= 4 AndAlso bytes(1) = 0 AndAlso bytes(3) = 0 Then
                    Return Encoding.Unicode.GetString(bytes)
                End If
                Return New UTF8Encoding(False).GetString(bytes)
            Catch
                Return Encoding.Default.GetString(bytes)
            End Try
        End Function

        ' 重要经验：
        ' 1) 必须使用 ArgumentList 逐个传参。用 Arguments 字符串（即使带引号）传给 wsl.exe 时，
        '    启动发行版会稳定返回退出码 -1；改用 ArgumentList 后正常返回 0。
        ' 2) 必须重定向 stdin，且不能关闭它（关闭写端会让 wsl 读到 broken pipe 而失败）。
        ''' <param name="killOnTimeout">
        ''' 超时时是否杀掉进程树。
        ''' 发软关机指令时必须设为 False：wsl.exe 会一直挂到发行版真正停止，
        ''' 此时 Kill(True) 会连 WSL 会话一起杀掉，导致关机流程中断。
        ''' </param>
        Private Async Function RunAsync(args As String(),
                                        Optional timeoutMs As Integer = 30000,
                                        Optional ct As CancellationToken = Nothing,
                                        Optional killOnTimeout As Boolean = True) As Task(Of WslResult)
            Dim result As New WslResult()
            Dim psi As New ProcessStartInfo With {
                .FileName = "wsl.exe",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .RedirectStandardInput = True,
                .WindowStyle = ProcessWindowStyle.Hidden
            }
            For Each a In args
                psi.ArgumentList.Add(a)
            Next

            Using p As New Process()
                p.StartInfo = psi
                Try
                    If Not p.Start() Then
                        result.ErrorMessage = "无法启动 wsl.exe"
                        Return result
                    End If
                Catch ex As System.ComponentModel.Win32Exception
                    result.ErrorMessage = "未检测到 WSL（wsl.exe 不可用）：" & ex.Message
                    Return result
                Catch ex As Exception
                    result.ErrorMessage = ex.Message
                    Return result
                End Try

                ' 注意：此处绝对不能关闭 StandardInput。
                ' 关闭写端后子进程读 stdin 会得到 ERROR_BROKEN_PIPE，wsl.exe 启动发行版会直接返回 -1。
                ' 保持管道打开（无数据）等价于有效的空 stdin，冷启动才能成功。

                Dim soTask As Task(Of Byte()) = ReadAllAsync(p.StandardOutput.BaseStream, ct)
                Dim seTask As Task(Of Byte()) = ReadAllAsync(p.StandardError.BaseStream, ct)

                Dim exited As Boolean
                If timeoutMs > 0 Then
                    exited = Await Task.Run(Function() p.WaitForExit(timeoutMs))
                    If Not exited Then
                        If killOnTimeout Then
                            Try : p.Kill(True) : Catch : End Try
                        End If
                        result.ErrorMessage = "命令超时"
                        result.ExitCode = -2
                        Return result
                    End If
                Else
                    Await Task.Run(Sub() p.WaitForExit())
                End If

                result.StdOut = Decode(Await soTask)
                result.StdErr = Decode(Await seTask)
                result.ExitCode = p.ExitCode
                result.Success = p.ExitCode = 0
            End Using
            Return result
        End Function

        Private Async Function ReadAllAsync(stream As Stream, ct As CancellationToken) As Task(Of Byte())
            Using ms As New MemoryStream()
                Dim buffer(8191) As Byte
                Dim read As Integer
                Do
                    read = Await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                    If read <= 0 Then Exit Do
                    ms.Write(buffer, 0, read)
                Loop While read > 0
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>列出所有发行版及其状态</summary>
        Public Async Function ListDistrosAsync() As Task(Of List(Of Distro))
            Dim list As New List(Of Distro)()
            Dim r = Await RunAsync(New String() {"-l", "-v"}, 20000)
            If Not r.Success AndAlso String.IsNullOrWhiteSpace(r.StdOut) Then Return list

            For Each rawLine In r.StdOut.Replace(vbCrLf, vbLf).Split(vbLf(0))
                Dim line = rawLine.TrimEnd(vbCr(0)).TrimEnd()
                If String.IsNullOrWhiteSpace(line) Then Continue For

                ' 去掉 BOM / 表头
                line = line.TrimStart(ChrW(&HFEFF))
                If line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) Then Continue For
                If line.Contains("STATE") AndAlso line.Contains("VERSION") Then Continue For

                Dim isDefault = line.TrimStart().StartsWith("*")
                Dim body = line.TrimStart("*"c).Trim()
                If body.Length = 0 Then Continue For

                Dim parts = Regex.Split(body, "\s{2,}")
                If parts.Length = 1 Then parts = Regex.Split(body, "\s+")
                If parts.Length < 2 Then Continue For

                Dim d As New Distro With {
                    .Name = parts(0).Trim(),
                    .State = If(parts.Length > 1, parts(1).Trim(), "Unknown"),
                    .IsDefault = isDefault
                }
                If parts.Length > 2 Then
                    Dim v As Integer
                    If Integer.TryParse(parts(2).Trim(), v) Then d.Version = v
                End If
                If d.Name.Length > 0 Then list.Add(d)
            Next

            ' 同步启动时间
            For Each d In list
                SyncLock _startTimes
                    If d.IsRunning Then
                        If Not _startTimes.ContainsKey(d.Name) Then _startTimes(d.Name) = DateTime.Now
                    Else
                        _startTimes.Remove(d.Name)
                    End If
                End SyncLock
            Next

            Return list
        End Function

        ''' <summary>启动（开机）发行版</summary>
        Public Async Function StartAsync(name As String) As Task(Of WslResult)
            Dim r = Await RunAsync(New String() {"-d", name, "true"}, 60000)
            If r.Success Then
                SyncLock _startTimes
                    _startTimes(name) = DateTime.Now
                End SyncLock
                ' WSL2 在所有进程退出后会空闲自动停机，
                ' 因此再挂一个长期 sleep 进程，让发行版保持 Running。
                StartKeepAlive(name)
            End If
            Return r
        End Function

        ''' <summary>后台保活：启动一个不等待的 wsl.exe，在发行版内运行长睡眠</summary>
        Private Sub StartKeepAlive(name As String)
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = "wsl.exe",
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .WindowStyle = ProcessWindowStyle.Hidden,
                    .RedirectStandardInput = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True
                }
                psi.ArgumentList.Add("-d")
                psi.ArgumentList.Add(name)
                psi.ArgumentList.Add("--exec")
                psi.ArgumentList.Add("sleep")
                psi.ArgumentList.Add("8640000")

                Dim p As New Process With {.StartInfo = psi}
                p.Start()
                ' 不等待、不持有引用：该进程独立于本程序存活，发行版会保持运行。
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' 以 root 身份向发行版内发送关机指令，返回指令是否送达。
        '''
        ''' 关键点：必须用 -u root。用默认用户执行 shutdown 会被 polkit 拦下，
        ''' 报错 "Call to PowerOff failed: Interactive authentication required."，
        ''' 命令返回 rc=1 且什么都不做（表面看像关机了，其实只是 WSL 后来空闲自动停机）。
        ''' </summary>
        Private Async Function TryGracefulShutdownAsync(name As String) As Task(Of Boolean)
            For Each cmd In New String() {"/sbin/shutdown", "/usr/sbin/shutdown"}
                ' 等久一点（8s），尽量捕获真实的退出码 rc=0；超过则按 -2（命令正常挂着，会一直挂到 VM 停止）处理。
                ' killOnTimeout 必须为 False：wsl.exe 会一直挂到发行版真正停止，此时 Kill 会打断关机流程。
                Dim r = Await RunAsync(New String() {"-d", name, "-u", "root", "--", cmd, "-h", "now"},
                                       8000, Nothing, killOnTimeout:=False)
                If r.ExitCode = 0 Then
                    SimpleLog.Write($"软关机指令已送达（{cmd}，rc=0）：发行版将优雅停机")
                    Return True
                End If
                If r.ExitCode = -2 Then
                    ' 命令正常挂着，等待 VM 停止（后续轮询会确认）
                    SimpleLog.Write($"软关机指令已送出（{cmd}，等待 VM 停止）")
                    Return True
                End If
                SimpleLog.Write($"关机指令 {cmd} 失败（rc={r.ExitCode}）：{r.StdErr.Trim()}")
            Next
            Return False
        End Function

        ''' <summary>
        ''' 停止发行版。
        ''' graceful=True 时先走软关机：以 root 在发行版内执行 shutdown，等待其自行停机；
        ''' 若指令送不进去或超时未停，则先 sync 落盘，再回退到 wsl -t 强制终止。
        ''' </summary>
        Public Async Function StopAsync(name As String,
                                        Optional graceful As Boolean = True,
                                        Optional gracefulTimeoutMs As Integer = 20000,
                                        Optional ct As CancellationToken = Nothing) As Task(Of WslResult)

            ' 0) 先看当前状态。
            '    关键：对已停止的发行版绝不能执行 `wsl -d X ...`，
            '    wsl 会为了执行这条命令把发行版重新拉起来，反而变成开机。
            Dim cur = (Await ListDistrosAsync()).FirstOrDefault(
                Function(x) String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))

            If cur Is Nothing OrElse Not cur.IsRunning Then
                SyncLock _startTimes
                    _startTimes.Remove(name)
                End SyncLock
                Return New WslResult With {
                    .ExitCode = 0, .Success = True, .Graceful = True, .StdOut = "已经是停止状态"
                }
            End If

            If graceful Then
                ' 1) 发送软关机指令
                If Await TryGracefulShutdownAsync(name) Then
                    ' 2) 轮询等待状态变为非 Running
                    Dim waited As Integer = 0
                    While waited < gracefulTimeoutMs
                        Await Task.Delay(500)
                        waited += 500

                        Dim d = (Await ListDistrosAsync()).FirstOrDefault(
                            Function(x) String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        If d Is Nothing OrElse Not d.IsRunning Then
                            SyncLock _startTimes
                                _startTimes.Remove(name)
                            End SyncLock
                            SimpleLog.Write($"软关机完成：{name}（{waited / 1000.0:F1}s）")
                            Return New WslResult With {
                                .ExitCode = 0, .Success = True, .Graceful = True, .StdOut = "优雅关机完成"
                            }
                        End If
                    End While
                    SimpleLog.Write($"软关机超时（{gracefulTimeoutMs}ms），转为强制终止：{name}")
                Else
                    SimpleLog.Write($"软关机指令未送达，转为强制终止：{name}")
                End If

                ' 3) 强制终止前先把文件系统缓存刷盘，尽量降低数据风险
                Try
                    Await RunAsync(New String() {"-d", name, "-u", "root", "--", "sync"}, 8000, ct)
                Catch
                End Try
            End If

            ' 4) 强制终止（等价于直接掐掉 VM）
            Dim r = Await RunAsync(New String() {"-t", name}, 60000, ct)
            r.Forced = True
            SyncLock _startTimes
                _startTimes.Remove(name)
            End SyncLock
            Return r
        End Function

        ''' <summary>
        ''' 强制终止发行版（硬关机，不做软关机尝试）
        ''' </summary>
        Public Async Function TerminateAsync(name As String) As Task(Of WslResult)
            Dim r = Await RunAsync(New String() {"-t", name}, 60000)
            r.Forced = True
            SyncLock _startTimes
                _startTimes.Remove(name)
            End SyncLock
            Return r
        End Function

        ''' <summary>重启发行版：软关机 → 开机</summary>
        Public Async Function RestartAsync(name As String, Optional graceful As Boolean = True) As Task(Of WslResult)
            Await StopAsync(name, graceful)
            Await Task.Delay(600)
            Return Await StartAsync(name)
        End Function

        ''' <summary>
        ''' 关闭全部发行版：先对每个运行中的发行版发软关机请求，
        ''' 留出缓冲时间让它们自行落盘，最后用 wsl --shutdown 兜底。
        ''' </summary>
        Public Async Function ShutdownAllAsync(Optional graceful As Boolean = True) As Task(Of WslResult)
            If graceful Then
                Dim list = Await ListDistrosAsync()
                Dim running = list.Where(Function(d) d.IsRunning).ToList()
                If running.Count > 0 Then
                    For Each d In running
                        Await TryGracefulShutdownAsync(d.Name)
                    Next
                    ' 给各发行版一点落盘时间
                    Await Task.Delay(5000)
                End If
            End If

            Dim r = Await RunAsync(New String() {"--shutdown"}, 30000)
            SyncLock _startTimes
                _startTimes.Clear()
            End SyncLock
            Return r
        End Function

        ''' <summary>设为默认发行版</summary>
        Public Async Function SetDefaultAsync(name As String) As Task(Of WslResult)
            Return Await RunAsync(New String() {"-s", name}, 30000)
        End Function

        ''' <summary>查询发行版 IP（运行中才有意义）</summary>
        Public Async Function GetIpAsync(name As String) As Task(Of String)
            Dim r = Await RunAsync(New String() {"-d", name, "--", "hostname", "-I"}, 15000)
            If Not r.Success Then Return ""
            Dim ips = r.StdOut.Trim()
            If ips.Length = 0 Then Return ""
            Return ips.Split(" "c)(0).Trim()
        End Function

        ''' <summary>获取 WSL 平台版本信息</summary>
        Public Async Function GetWslVersionAsync() As Task(Of String)
            If _wslVersionCache.Length > 0 Then Return _wslVersionCache
            Dim r = Await RunAsync(New String() {"--version"}, 15000)
            If r.Success AndAlso r.StdOut.Length > 0 Then
                For Each line In r.StdOut.Replace(vbCrLf, vbLf).Split(vbLf(0))
                    Dim t = line.Trim()
                    If t.StartsWith("WSL version", StringComparison.OrdinalIgnoreCase) OrElse
                       t.StartsWith("WSL 版本", StringComparison.OrdinalIgnoreCase) Then
                        _wslVersionCache = t
                        Exit For
                    End If
                Next
                If _wslVersionCache.Length = 0 Then
                    _wslVersionCache = r.StdOut.Replace(vbCrLf, vbLf).Split(vbLf(0))(0).Trim()
                End If
            Else
                _wslVersionCache = "已安装"
            End If
            Return _wslVersionCache
        End Function

        ''' <summary>发行版累计运行起始时间</summary>
        Public Function GetStartTime(name As String) As DateTime?
            SyncLock _startTimes
                If _startTimes.ContainsKey(name) Then Return _startTimes(name)
            End SyncLock
            Return Nothing
        End Function

        ''' <summary>打开终端（优先 Windows Terminal，回退 wsl.exe）</summary>
        Public Sub OpenTerminal(name As String)
            ' 注意：不要传 `\\wsl$\X` 作为 wt.exe 的起始目录，否则 Windows Terminal 用 CMD profile
            ' 启动时工作目录落到 UNC 上，会弹"UNC 路径不受支持，默认值设为 Windows 目录"。
            ' 改为直接在终端里执行 `wsl -d X`，由发行版自己决定工作目录。
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = "wt.exe",
                    .Arguments = "wsl.exe -d """ & name & """",
                    .UseShellExecute = True
                }
                Process.Start(psi)
                Return
            Catch
            End Try
            Try
                Process.Start(New ProcessStartInfo With {
                    .FileName = "wsl.exe",
                    .Arguments = "-d """ & name & """",
                    .UseShellExecute = True
                })
            Catch
            End Try
        End Sub

        ''' <summary>在资源管理器中打开发行版文件系统</summary>
        Public Sub OpenFiles(name As String)
            Try
                Process.Start(New ProcessStartInfo With {
                    .FileName = "explorer.exe",
                    .Arguments = "\\wsl$\" & name,
                    .UseShellExecute = True
                })
            Catch
            End Try
        End Sub
    End Module
End Namespace
