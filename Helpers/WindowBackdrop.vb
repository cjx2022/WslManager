Imports System.Runtime.InteropServices
Imports System.Windows
Imports System.Windows.Interop
Imports System.Windows.Media
Imports Microsoft.Win32

Namespace Helpers

    ''' <summary>
    ''' Windows 11 视觉效果：Mica 背景、深色模式、圆角窗口、系统主题色
    ''' </summary>
    Public Module WindowBackdrop

        Private Const DWMWA_USE_IMMERSIVE_DARK_MODE As Integer = 20
        Private Const DWMWA_WINDOW_CORNER_PREFERENCE As Integer = 33
        Private Const DWMWA_SYSTEMBACKDROP_TYPE As Integer = 38

        Private Const DWMSBT_NONE As Integer = 1
        Private Const DWMSBT_MAINWINDOW As Integer = 2      ' Mica
        Private Const DWMSBT_TRANSIENTWINDOW As Integer = 3 ' Acrylic

        <DllImport("dwmapi.dll", PreserveSig:=True)>
        Private Function DwmSetWindowAttribute(hwnd As IntPtr, attr As Integer, ByRef attrValue As Integer, attrSize As Integer) As Integer
        End Function

        ''' <summary>是否为 Windows 11 及以上</summary>
        Public ReadOnly Property IsWindows11 As Boolean
            Get
                Return Environment.OSVersion.Version.Build >= 22000
            End Get
        End Property

        ''' <summary>系统是否为浅色主题</summary>
        Public Function SystemUsesLightTheme() As Boolean
            Try
                Using k = Registry.CurrentUser.OpenSubKey("Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                    Dim v = If(k?.GetValue("AppsUseLightTheme"), 1)
                    Return Convert.ToInt32(v) = 1
                End Using
            Catch
                Return True
            End Try
        End Function

        ''' <summary>系统主题色</summary>
        Public Function GetAccentColor() As Color
            Try
                Using k = Registry.CurrentUser.OpenSubKey("Software\Microsoft\Windows\DWM")
                    Dim v = If(k?.GetValue("ColorizationColor"), &HD77800)
                    Dim argb = CUInt(Convert.ToInt32(v))
                    Dim b As Byte = CByte(argb And &HFF)
                    Dim g As Byte = CByte((argb >> 8) And &HFF)
                    Dim r As Byte = CByte((argb >> 16) And &HFF)
                    If r + g + b < 90 Then Return Color.FromRgb(0, 120, 212)
                    Return Color.FromRgb(r, g, b)
                End Using
            Catch
                Return Color.FromRgb(0, 120, 212)
            End Try
        End Function

        Private Function HwndOf(window As Window) As IntPtr
            Return New WindowInteropHelper(window).Handle
        End Function

        ''' <summary>
        ''' 向 DWM 申请 Mica 背景材质。
        ''' 注意：WPF 的客户区是不透明渲染的，DWM 的材质无法透过 WPF 窗口显示出来，
        ''' 所以本项目不依赖它做背景（背景由 MicaBrush 自绘），
        ''' 这里保留实现仅用于将来切换到真正的透明承载方式。
        ''' </summary>
        Public Function SetMica(window As Window) As Boolean
            If Not IsWindows11 Then Return False
            Try
                Dim hwnd = HwndOf(window)
                If hwnd = IntPtr.Zero Then Return False
                Dim v As Integer = DWMSBT_MAINWINDOW
                Dim hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, v, Marshal.SizeOf(GetType(Integer)))
                Return hr >= 0
            Catch
                Return False
            End Try
        End Function

        ''' <summary>切换窗口深色 / 浅色模式（影响 Mica 与系统绘制区域）</summary>
        Public Sub SetImmersiveDark(window As Window, dark As Boolean)
            If Not IsWindows11 Then Return
            Try
                Dim hwnd = HwndOf(window)
                If hwnd = IntPtr.Zero Then Return
                Dim v As Integer = If(dark, 1, 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, v, Marshal.SizeOf(GetType(Integer)))
            Catch
            End Try
        End Sub

        ''' <summary>请求系统圆角</summary>
        Public Sub SetRoundedCorners(window As Window)
            If Not IsWindows11 Then Return
            Try
                Dim hwnd = HwndOf(window)
                If hwnd = IntPtr.Zero Then Return
                Dim v As Integer = 2 ' DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, v, Marshal.SizeOf(GetType(Integer)))
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' 一次性应用全部视觉效果：系统圆角 + 深色模式。
        ''' 背景不使用 DWM 材质（原因见 SetMica 注释），由主题中的 MicaBrush 自绘。
        ''' </summary>
        Public Sub Apply(window As Window, dark As Boolean)
            SetRoundedCorners(window)
            SetImmersiveDark(window, dark)
        End Sub
    End Module
End Namespace
