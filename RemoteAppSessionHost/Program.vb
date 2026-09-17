Imports System
Imports System.Diagnostics

Friend Module Program
    Public Sub Main()
        Dim sessionId As Integer = -1

        Try
            sessionId = Process.GetCurrentProcess().SessionId
        Catch
            sessionId = -1
        End Try

        StrictSessionLog.Initialize(sessionId)

        Try
            Dim options As SessionHostOptions = SessionHostOptions.Resolve(Environment.GetCommandLineArgs())
            StrictSessionLog.Write("HostStart session=" & sessionId.ToString() & " noLogoff=" & options.NoLogoff.ToString())
            Environment.ExitCode = SessionController.Run(options, sessionId)
        Catch ex As ArgumentException
            StrictSessionLog.Write("ArgumentError type=" & ex.GetType().Name & " message=" & ex.Message)
            HostErrorDisplay.Show("The Strict App Session launcher configuration is invalid." & vbCrLf & vbCrLf & ex.Message)
            Environment.ExitCode = 2
        Catch ex As Exception
            StrictSessionLog.Write("UnhandledError type=" & ex.GetType().FullName & " message=" & ex.Message)
            HostErrorDisplay.Show("The Strict App Session launcher could not start the published application." & vbCrLf & vbCrLf & ex.Message)
            Environment.ExitCode = 99
        End Try
    End Sub
End Module
