Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Text
Imports System.Runtime.InteropServices

Namespace Helpers

    ''' <summary>
    ''' 动态生成应用图标（无需外部 ico 文件，并按运行状态改变颜色）
    ''' </summary>
    Public Module AppIcon

        <DllImport("user32.dll", SetLastError:=True)>
        Private Function DestroyIcon(hIcon As IntPtr) As Boolean
        End Function

        ''' <summary>绘制一个圆形终端图标</summary>
        Public Function MakeIcon(Optional running As Boolean = False, Optional size As Integer = 64) As Drawing.Icon
            Dim bmp As New Bitmap(size, size)
            Using g As Graphics = Graphics.FromImage(bmp)
                g.SmoothingMode = SmoothingMode.AntiAlias
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit
                g.InterpolationMode = InterpolationMode.HighQualityBicubic
                g.Clear(Drawing.Color.Transparent)

                Dim pad As Integer = CInt(size * 0.05)
                Dim bg As Drawing.Color = If(running,
                                             Drawing.Color.FromArgb(16, 124, 16),
                                             Drawing.Color.FromArgb(108, 113, 118))

                Using br As New SolidBrush(bg)
                    Using path As New GraphicsPath()
                        path.AddEllipse(pad, pad, size - pad * 2, size - pad * 2)
                        g.FillPath(br, path)
                    End Using
                End Using

                Using f As New Font("Consolas", CSng(size * 0.40), FontStyle.Bold)
                    Using br As New SolidBrush(Drawing.Color.White)
                        Dim sf As New StringFormat With {
                            .Alignment = StringAlignment.Center,
                            .LineAlignment = StringAlignment.Center
                        }
                        Dim rect As New RectangleF(0, size * 0.06F, size, size)
                        g.DrawString(">_", f, br, rect, sf)
                    End Using
                End Using
            End Using

            Dim h As IntPtr = bmp.GetHicon()
            Return Drawing.Icon.FromHandle(h)
        End Function

        ''' <summary>生成 WPF 窗口图标</summary>
        Public Function MakeImageSource(Optional running As Boolean = False, Optional size As Integer = 64) As System.Windows.Media.ImageSource
            Dim ic = MakeIcon(running, size)
            Try
                Return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    ic.Handle, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions())
            Finally
                Try : DestroyIcon(ic.Handle) : Catch : End Try
            End Try
        End Function
    End Module
End Namespace
