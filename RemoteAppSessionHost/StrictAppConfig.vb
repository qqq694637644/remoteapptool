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

                        If CInt(appKey.GetValue("RemoteAppToolStrictSession", 0)) <> 1 Then Continue For

                        Dim configuredId As String = CStr(appKey.GetValue("RemoteAppToolStrictId", ""))
                        If Not String.Equals(configuredId, strictId.ToString("D"), StringComparison.OrdinalIgnoreCase) Then Continue For

                        Dim schemaVersion As Integer = CInt(appKey.GetValue("RemoteAppToolSchemaVersion", 0))
                        If schemaVersion <> 1 Then
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
                        resolved.TerminationMode = CInt(appKey.GetValue("RemoteAppToolTerminationMode", 0))
                        resolved.ConfigurationSource = "registry:" & aliasName

                        Dim disconnectBehavior As Integer = CInt(appKey.GetValue("RemoteAppToolDisconnectBehavior", 1))
                        Select Case disconnectBehavior
                            Case 0
                                resolved.DisconnectGraceMs = -1
                            Case 2
                                resolved.DisconnectGraceMs = 0
                            Case Else
                                resolved.DisconnectGraceMs = ReadNonNegative(appKey, "RemoteAppToolDisconnectGraceMs", 30000)
                        End Select

                        For argumentIndex As Integer = 1 To args.Length - 1
                            resolved.TargetArguments.Add(args(argumentIndex))
                        Next

                        options = resolved
                        StrictSessionLog.Write("StrictConfigLoaded alias=" & aliasName & " id=" & strictId.ToString("D"))
                        Return True
                    End Using
                Next
            End Using
        Next

        Throw New InvalidOperationException("No Strict App Session registry entry matches deployment id " & strictId.ToString("D"))
    End Function

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
