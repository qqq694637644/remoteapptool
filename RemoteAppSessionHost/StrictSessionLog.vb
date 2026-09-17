Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text

Friend NotInheritable Class StrictSessionLog
    Private Shared ReadOnly SyncRoot As New Object()
    Private Shared LogFilePath As String

    Private Sub New()
    End Sub

    Public Shared Sub Initialize(ByVal sessionId As Integer)
        Try
            Dim root As String = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            Dim logDirectory As String = Path.Combine(root, "RemoteAppTool", "StrictSession", "logs")
            System.IO.Directory.CreateDirectory(logDirectory)

            Dim fileName As String = String.Format(
                "session-{0}-pid-{1}-{2}.log",
                sessionId,
                Process.GetCurrentProcess().Id,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))

            LogFilePath = Path.Combine(logDirectory, fileName)
        Catch
            LogFilePath = Nothing
        End Try
    End Sub

    Public Shared Sub Write(ByVal message As String)
        If String.IsNullOrEmpty(LogFilePath) Then Return

        Try
            SyncLock SyncRoot
                Dim line As String = DateTime.UtcNow.ToString("o") & " " & message & Environment.NewLine
                File.AppendAllText(LogFilePath, line, Encoding.UTF8)
            End SyncLock
        Catch
            ' Logging must never change session lifecycle behavior.
        End Try
    End Sub
End Class
