Imports System
Imports System.IO
Imports Microsoft.Win32

Friend NotInheritable Class StrictAppConfig
    Private Const NativeRegistryPath As String = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications"
    Private Const WowRegistryPath As String = "SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications"

    Private Sub New()
    End Sub

    Public Shared Function TryLoadFromDeploymentPath(ByVal args() As String, ByRef options As SessionHostOptions) As Boolean
        If args.Length = 0 Then Return False

        Dim executablePath As String = args(0)
        Dim parentDirectory As String = Path.GetDirectoryName(executablePath)
        If String.IsNullOrEmpty(parentDirectory) Then Return False

        Dim strictIdText As String = Path.GetFileName(parentDirectory)
        Dim strictId As Guid
        If Not Guid.TryParse(strictIdText, strictId) Then Return False

        Dim registryPaths() As String = {NativeRegistryPath, WowRegistryPath}
        For Each registryPath As String In registryPaths
            Using applicationsKey As RegistryKey = Registry.LocalMachine.OpenSubKey(registryPath)
                If applicationsKey Is Nothing Then Continue For

                For Each aliasName As String In applicationsKey.GetSubKeyNames()
                    Using appKey As RegistryKey = applicationsKey.OpenSubKey(aliasName)
                        If appKey Is Nothing Then Continue For

                        Dim configuredId As String = CStr(appKey.GetValue("RemoteAppToolStrictId", ""))
                        If Not String.Equals(configuredId, strictId.ToString("D"), StringComparison.OrdinalIgnoreCase) Then Continue For

                        For Each obsoleteName As String In New String() {
                            "RemoteAppToolStrictSession",
                            "RemoteAppToolPersistentSession",
                            "RemoteAppToolTerminationMode",
                            "RemoteAppToolDisconnectBehavior"
                        }
                            If appKey.GetValue(obsoleteName, Nothing) IsNot Nothing Then
                                Throw New InvalidOperationException("RemoteApp " & aliasName & " contains obsolete " & obsoleteName & ". Recreate it using schema v2.")
                            End If
                        Next

                        Dim mode As Integer = CInt(appKey.GetValue("RemoteAppToolSessionMode", -1))
                        If mode <> 1 AndAlso mode <> 2 Then
                            Throw New InvalidOperationException("RemoteApp " & aliasName & " has no valid session mode. Recreate it using schema v2.")
                        End If

                        Dim schemaVersion As Integer = CInt(appKey.GetValue("RemoteAppToolSchemaVersion", 0))
                        If schemaVersion <> 2 Then
                            Throw New InvalidOperationException("Strict App Session configuration for alias " & aliasName & " uses unsupported schema version " & schemaVersion.ToString())
                        End If

                        Dim publishedPath As String = CStr(appKey.GetValue("Path", ""))
                        If String.IsNullOrWhiteSpace(publishedPath) Then
                            Throw New InvalidOperationException("Strict App Session configuration is missing the published launcher path for alias " & aliasName)
                        End If

                        If Not PathsEqual(publishedPath, executablePath) Then
                            Throw New InvalidOperationException("Strict App Session launcher path does not match the published path for alias " & aliasName)
                        End If

                        Dim targetPath As String = CStr(appKey.GetValue("RemoteAppToolTargetPath", ""))
                        If String.IsNullOrWhiteSpace(targetPath) Then
                            Throw New InvalidOperationException("Strict App Session configuration is missing RemoteAppToolTargetPath for alias " & aliasName)
                        End If

                        If PathsEqual(targetPath, executablePath) Then
                            Throw New InvalidOperationException("Strict App Session target path points back to the launcher for alias " & aliasName)
                        End If

                        Dim resolved As New SessionHostOptions()
                        resolved.TargetPath = targetPath
                        resolved.StartupGraceMs = ReadNonNegative(appKey, "RemoteAppToolStartupGraceMs", 10000)
                        resolved.CloseGraceMs = ReadNonNegative(appKey, "RemoteAppToolCloseGraceMs", 1500)
                        resolved.PollIntervalMs = 250
                        resolved.PersistentSession = mode = 2
                        resolved.ConfigurationSource = "registry:" & aliasName

                        ApplyMode(mode, resolved, ReadNonNegative(appKey, "RemoteAppToolDisconnectGraceMs", 30000))

                        For argumentIndex As Integer = 1 To args.Length - 1
                            resolved.TargetArguments.Add(args(argumentIndex))
                        Next

                        options = resolved
                        StrictSessionLog.Write("StrictConfigLoaded alias=" & aliasName & " id=" & strictId.ToString("D") & " mode=" & mode.ToString())
                        Return True
                    End Using
                Next
            End Using
        Next

        Throw New InvalidOperationException("No Strict App Session registry entry matches deployment id " & strictId.ToString("D"))
    End Function

    Friend Shared Sub ApplyMode(ByVal mode As Integer, ByVal options As SessionHostOptions, ByVal strictDisconnectGraceMs As Integer)
        Select Case mode
            Case 1
                ' Strict: close the last tracked window, then log off this session.
                options.PersistentSession = False
                options.TerminationMode = 0
                options.DisconnectGraceMs = strictDisconnectGraceMs
            Case 2
                ' Persistent: track the process tree; do not log off on disconnect.
                options.PersistentSession = True
                options.TerminationMode = 1
                options.DisconnectGraceMs = -1
            Case Else
                Throw New ArgumentOutOfRangeException("mode")
        End Select
    End Sub

    Private Shared Function PathsEqual(ByVal firstPath As String, ByVal secondPath As String) As Boolean
        Try
            Return String.Equals(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath), StringComparison.OrdinalIgnoreCase)
        Catch
            Return String.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase)
        End Try
    End Function

    Private Shared Function ReadNonNegative(ByVal key As RegistryKey, ByVal valueName As String, ByVal defaultValue As Integer) As Integer
        Dim value As Integer = CInt(key.GetValue(valueName, defaultValue))
        If value < 0 Then Return defaultValue
        Return value
    End Function
End Class
