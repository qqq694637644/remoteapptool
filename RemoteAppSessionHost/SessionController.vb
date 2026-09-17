Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Threading

Friend NotInheritable Class SessionController
    Private Const ControllerMutexName As String = "Local\RemoteAppTool.StrictSession.Controller"
    Private Const SharedSignalName As String = "Local\RemoteAppTool.StrictSession.SharedDetected"

    Private Sub New()
    End Sub

    Public Shared Function Run(ByVal options As SessionHostOptions, ByVal sessionId As Integer) As Integer
        If sessionId < 0 Then
            StrictSessionLog.Write("SessionIdUnavailable; refusing automatic logoff")
        End If

        Dim controllerMutex As Mutex = Nothing
        Dim sharedSignal As EventWaitHandle = Nothing
        Dim ownsController As Boolean = False
        Dim safeToLogoff As Boolean = sessionId >= 0
        Dim baselineVisibleProcesses As New HashSet(Of Integer)()

        Try
            controllerMutex = New Mutex(False, ControllerMutexName)
            Try
                ownsController = controllerMutex.WaitOne(0, False)
            Catch ex As AbandonedMutexException
                ownsController = True
                StrictSessionLog.Write("ControllerMutexAbandoned; ownership recovered")
            End Try

            Try
                sharedSignal = New EventWaitHandle(False, EventResetMode.ManualReset, SharedSignalName)
            Catch ex As Exception
                safeToLogoff = False
                StrictSessionLog.Write("SharedSignalUnavailable; automatic logoff disabled type=" & ex.GetType().Name)
            End Try

            If Not ownsController Then
                safeToLogoff = False
                StrictSessionLog.Write("SharedSessionDetected controller already exists; automatic logoff disabled")
                If sharedSignal IsNot Nothing Then sharedSignal.Set()
            End If

            If sessionId >= 0 Then
                baselineVisibleProcesses = WindowTracker.GetVisibleProcessIdsInSession(sessionId, Process.GetCurrentProcess().Id)
                If safeToLogoff AndAlso baselineVisibleProcesses.Count > 0 Then
                    safeToLogoff = False
                    StrictSessionLog.Write("SessionNotExclusive visibleProcessCount=" & baselineVisibleProcesses.Count.ToString() & "; automatic logoff disabled")
                End If
            End If

            Dim target As Process = StartTarget(options)
            If target Is Nothing Then
                StrictSessionLog.Write("TargetStartFailed no process returned")
                HostErrorDisplay.Show("The published application could not be started. Check the Strict App Session lifecycle log for details.")
                If safeToLogoff Then Return RequestLogoff(options, sessionId, safeToLogoff, "target-start-failed")
                Return 10
            End If

            Try
                Return MonitorTarget(options, sessionId, target.Id, safeToLogoff, sharedSignal, baselineVisibleProcesses)
            Finally
                target.Dispose()
            End Try
        Finally
            If ownsController AndAlso controllerMutex IsNot Nothing Then
                Try
                    controllerMutex.ReleaseMutex()
                Catch
                End Try
            End If

            If sharedSignal IsNot Nothing Then sharedSignal.Dispose()
            If controllerMutex IsNot Nothing Then controllerMutex.Dispose()
        End Try
    End Function

    Private Shared Function StartTarget(ByVal options As SessionHostOptions) As Process
        Dim currentExecutable As String = Environment.GetCommandLineArgs()(0)

        Try
            If File.Exists(options.TargetPath) AndAlso File.Exists(currentExecutable) Then
                Dim targetFullPath As String = Path.GetFullPath(options.TargetPath)
                Dim currentFullPath As String = Path.GetFullPath(currentExecutable)
                If String.Equals(targetFullPath, currentFullPath, StringComparison.OrdinalIgnoreCase) Then
                    Throw New InvalidOperationException("Refusing to launch RemoteAppSessionHost recursively")
                End If
            End If
        Catch ex As InvalidOperationException
            Throw
        Catch
            ' Path normalization is a guard only; Process.Start remains authoritative.
        End Try

        Dim startInfo As New ProcessStartInfo()
        startInfo.FileName = options.TargetPath
        startInfo.Arguments = CommandLineForwarder.BuildArguments(options.TargetArguments)
        startInfo.UseShellExecute = False

        Try
            If Path.IsPathRooted(options.TargetPath) Then
                Dim workingDirectory As String = Path.GetDirectoryName(options.TargetPath)
                If Not String.IsNullOrEmpty(workingDirectory) AndAlso Directory.Exists(workingDirectory) Then
                    startInfo.WorkingDirectory = workingDirectory
                End If
            End If
        Catch
            ' Working directory is optional.
        End Try

        StrictSessionLog.Write("StartingTarget path=" & options.TargetPath)
        Try
            Dim process As Process = Process.Start(startInfo)
            If process IsNot Nothing Then StrictSessionLog.Write("TargetStarted pid=" & process.Id.ToString())
            Return process
        Catch ex As Exception
            StrictSessionLog.Write("TargetStartError type=" & ex.GetType().Name & " message=" & ex.Message)
            Return Nothing
        End Try
    End Function

    Private Shared Function MonitorTarget(
        ByVal options As SessionHostOptions,
        ByVal sessionId As Integer,
        ByVal rootProcessId As Integer,
        ByVal initiallySafeToLogoff As Boolean,
        ByVal sharedSignal As EventWaitHandle,
        ByVal baselineVisibleProcesses As HashSet(Of Integer)) As Integer

        Dim tracker As New ProcessTracker(rootProcessId, sessionId)
        Dim startedAt As DateTime = DateTime.UtcNow
        Dim lifecycleArmed As Boolean = False
        Dim processTreeFallback As Boolean = False
        Dim closeStartedAt As Nullable(Of DateTime) = Nothing
        Dim disconnectStartedAt As Nullable(Of DateTime) = Nothing
        Dim lastConnectState As WtsNative.ConnectState = WtsNative.ConnectState.Unknown
        Dim connectStateErrorLogged As Boolean = False
        Dim safeToLogoff As Boolean = initiallySafeToLogoff

        While True
            If safeToLogoff AndAlso sharedSignal IsNot Nothing AndAlso sharedSignal.WaitOne(0) Then
                safeToLogoff = False
                StrictSessionLog.Write("SharedSessionDetected by peer; automatic logoff disabled")
            End If

            Dim nowUtc As DateTime = DateTime.UtcNow

            If sessionId >= 0 Then
                Dim sessionVisibleProcesses As HashSet(Of Integer) = WindowTracker.GetVisibleProcessIdsInSession(sessionId, Process.GetCurrentProcess().Id)
                For Each baselineProcessId As Integer In baselineVisibleProcesses
                    sessionVisibleProcesses.Remove(baselineProcessId)
                Next

                If sessionVisibleProcesses.Count > 0 Then
                    tracker.Adopt(sessionVisibleProcesses)
                End If
            End If

            Dim activeProcessIds As HashSet(Of Integer) = tracker.Refresh()
            Dim visibleWindowCount As Integer = WindowTracker.CountVisibleWindows(activeProcessIds)

            If HandleDisconnect(options, sessionId, nowUtc, disconnectStartedAt, lastConnectState, connectStateErrorLogged) Then
                Return RequestLogoff(options, sessionId, safeToLogoff, "disconnect-grace-elapsed")
            End If

            If options.TerminationMode = 1 Then
                If activeProcessIds.Count = 0 Then
                    Return RequestLogoff(options, sessionId, safeToLogoff, "process-tree-exited")
                End If

                Thread.Sleep(options.PollIntervalMs)
                Continue While
            ElseIf options.TerminationMode = 2 Then
                If Not activeProcessIds.Contains(rootProcessId) Then
                    Return RequestLogoff(options, sessionId, safeToLogoff, "primary-process-exited")
                End If

                Thread.Sleep(options.PollIntervalMs)
                Continue While
            ElseIf options.TerminationMode <> 0 Then
                StrictSessionLog.Write("TerminationModeInvalid mode=" & options.TerminationMode.ToString() & "; using LastTrackedWindowClosed")
                options.TerminationMode = 0
            End If

            If visibleWindowCount > 0 Then
                If Not lifecycleArmed Then
                    lifecycleArmed = True
                    StrictSessionLog.Write("LifecycleArmed visibleWindows=" & visibleWindowCount.ToString())
                End If

                If closeStartedAt.HasValue Then
                    StrictSessionLog.Write("CloseGraceCancelled window returned")
                    closeStartedAt = Nothing
                End If
            ElseIf lifecycleArmed Then
                If Not closeStartedAt.HasValue Then
                    closeStartedAt = nowUtc
                    StrictSessionLog.Write("CloseGraceStarted activeProcesses=" & activeProcessIds.Count.ToString())
                ElseIf (nowUtc - closeStartedAt.Value).TotalMilliseconds >= options.CloseGraceMs Then
                    Return RequestLogoff(options, sessionId, safeToLogoff, "last-tracked-window-closed")
                End If
            Else
                If activeProcessIds.Count = 0 Then
                    Return RequestLogoff(options, sessionId, safeToLogoff, "process-tree-exited-before-window")
                End If

                If Not processTreeFallback AndAlso (nowUtc - startedAt).TotalMilliseconds >= options.StartupGraceMs Then
                    processTreeFallback = True
                    StrictSessionLog.Write("StartupGraceElapsed; using process-tree fallback")
                End If
            End If

            If processTreeFallback AndAlso activeProcessIds.Count = 0 Then
                Return RequestLogoff(options, sessionId, safeToLogoff, "process-tree-exited")
            End If

            Thread.Sleep(options.PollIntervalMs)
        End While

        Return 0
    End Function

    Private Shared Function HandleDisconnect(
        ByVal options As SessionHostOptions,
        ByVal sessionId As Integer,
        ByVal nowUtc As DateTime,
        ByRef disconnectStartedAt As Nullable(Of DateTime),
        ByRef lastConnectState As WtsNative.ConnectState,
        ByRef connectStateErrorLogged As Boolean) As Boolean

        If options.DisconnectGraceMs < 0 OrElse sessionId < 0 Then Return False

        Dim state As WtsNative.ConnectState
        Dim errorCode As Integer
        If Not WtsNative.TryGetConnectState(sessionId, state, errorCode) Then
            If Not connectStateErrorLogged Then
                StrictSessionLog.Write("ConnectStateQueryFailed error=" & errorCode.ToString())
                connectStateErrorLogged = True
            End If
            Return False
        End If

        connectStateErrorLogged = False
        If state <> lastConnectState Then
            StrictSessionLog.Write("ConnectStateChanged state=" & state.ToString())
            lastConnectState = state
        End If

        If state = WtsNative.ConnectState.Disconnected Then
            If Not disconnectStartedAt.HasValue Then
                disconnectStartedAt = nowUtc
                StrictSessionLog.Write("DisconnectGraceStarted ms=" & options.DisconnectGraceMs.ToString())
            End If

            Return (nowUtc - disconnectStartedAt.Value).TotalMilliseconds >= options.DisconnectGraceMs
        End If

        If disconnectStartedAt.HasValue Then
            StrictSessionLog.Write("DisconnectGraceCancelled state=" & state.ToString())
            disconnectStartedAt = Nothing
        End If

        Return False
    End Function

    Private Shared Function RequestLogoff(
        ByVal options As SessionHostOptions,
        ByVal sessionId As Integer,
        ByVal safeToLogoff As Boolean,
        ByVal reason As String) As Integer

        If Not safeToLogoff Then
            StrictSessionLog.Write("LogoffSkipped reason=" & reason & " safety=shared-or-unknown-session")
            Return 20
        End If

        If options.NoLogoff Then
            StrictSessionLog.Write("LogoffDryRun reason=" & reason & " session=" & sessionId.ToString())
            Return 0
        End If

        Dim errorCode As Integer
        StrictSessionLog.Write("LogoffRequested reason=" & reason & " session=" & sessionId.ToString())
        If WtsNative.TryLogoff(sessionId, errorCode) Then
            StrictSessionLog.Write("LogoffAccepted session=" & sessionId.ToString())
            Return 0
        End If

        StrictSessionLog.Write("LogoffFailed session=" & sessionId.ToString() & " error=" & errorCode.ToString())
        Return 30
    End Function
End Class
