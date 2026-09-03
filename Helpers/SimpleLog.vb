Imports System.IO

Namespace Helpers

    ''' <summary>轻量运行日志，便于排障（%LOCALAPPDATA%\WslManager\run.log）</summary>
    Public Module SimpleLog

        Private ReadOnly Property LogPath As String
            Get
                Return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WslManager", "run.log")
            End Get
        End Property

        Public Sub Write(msg As String)
            Try
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath))
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}")
                TrimIfNeeded()
            Catch
            End Try
        End Sub

        ''' <summary>超过 400 行时保留末尾 200 行</summary>
        Private Sub TrimIfNeeded()
            Try
                If Not File.Exists(LogPath) Then Return
                Dim lines = File.ReadAllLines(LogPath)
                If lines.Length > 400 Then
                    File.WriteAllLines(LogPath, lines.Skip(lines.Length - 200).ToArray())
                End If
            Catch
            End Try
        End Sub
    End Module
End Namespace
