Imports System.IO

Namespace Helpers

    ''' <summary>简单键值设置持久化</summary>
    Public Module AppSettings

        Private ReadOnly Property FilePath As String
            Get
                Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslManager")
                Directory.CreateDirectory(dir)
                Return Path.Combine(dir, "settings.ini")
            End Get
        End Property

        Private _values As Dictionary(Of String, String)

        Private Function Values() As Dictionary(Of String, String)
            If _values Is Nothing Then
                _values = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                Try
                    If File.Exists(FilePath) Then
                        For Each line In File.ReadAllLines(FilePath)
                            Dim i = line.IndexOf("="c)
                            If i <= 0 Then Continue For
                            _values(line.Substring(0, i).Trim()) = line.Substring(i + 1).Trim()
                        Next
                    End If
                Catch
                End Try
            End If
            Return _values
        End Function

        Public Function GetValue(key As String, Optional def As String = "") As String
            Dim v As String = Nothing
            If Values().TryGetValue(key, v) Then Return v
            Return def
        End Function

        Public Sub SetValue(key As String, value As String)
            Values()(key) = value
            Save()
        End Sub

        Private Sub Save()
            Try
                Dim sb As New Text.StringBuilder()
                For Each kv In Values()
                    sb.AppendLine(kv.Key & "=" & kv.Value)
                Next
                File.WriteAllText(FilePath, sb.ToString())
            Catch
            End Try
        End Sub
    End Module
End Namespace
