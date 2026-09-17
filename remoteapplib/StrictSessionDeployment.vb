Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Security.AccessControl
Imports System.Security.Cryptography
Imports System.Security.Principal

Public NotInheritable Class StrictSessionDeployment
    Public Const LauncherFileName As String = "RemoteAppSessionHost.exe"

    Private Sub New()
    End Sub

    Public Shared Function EnsureLauncher(ByVal strictId As String) As String
        Dim normalizedId As String = NormalizeStrictId(strictId)
        Dim sourcePath As String = GetLauncherSourcePath()

        If Not File.Exists(sourcePath) Then
            Throw New FileNotFoundException(
                "Strict App Session launcher was not found next to RemoteApp Tool. Rebuild or repair the installation before enabling Strict App Session.",
                sourcePath)
        End If

        Dim rootDirectory As String = GetStrictRootDirectory()
        Dim appDirectory As String = Path.Combine(rootDirectory, normalizedId)
        Dim targetPath As String = Path.Combine(appDirectory, LauncherFileName)

        Directory.CreateDirectory(rootDirectory)
        ApplyLauncherDirectoryAcl(rootDirectory)
        Directory.CreateDirectory(appDirectory)
        ApplyLauncherDirectoryAcl(appDirectory)

        If File.Exists(targetPath) AndAlso FilesMatch(sourcePath, targetPath) Then
            Return targetPath
        End If

        Dim temporaryPath As String = targetPath & ".new-" & Guid.NewGuid().ToString("N")
        File.Copy(sourcePath, temporaryPath, True)

        Try
            If File.Exists(targetPath) Then File.Delete(targetPath)
            File.Move(temporaryPath, targetPath)
        Finally
            If File.Exists(temporaryPath) Then
                Try
                    File.Delete(temporaryPath)
                Catch
                End Try
            End If
        End Try

        Return targetPath
    End Function

    Public Shared Function GetLauncherPath(ByVal strictId As String) As String
        Return Path.Combine(GetStrictRootDirectory(), NormalizeStrictId(strictId), LauncherFileName)
    End Function

    Public Shared Sub CleanupLauncher(ByVal strictId As String)
        Dim normalizedId As String
        Try
            normalizedId = NormalizeStrictId(strictId)
        Catch
            Return
        End Try

        Dim appDirectory As String = Path.Combine(GetStrictRootDirectory(), normalizedId)
        Try
            If Directory.Exists(appDirectory) Then Directory.Delete(appDirectory, True)
        Catch
            ' Registry state is authoritative. A locked/stale launcher can be repaired later.
        End Try
    End Sub

    Public Shared Function GetStrictRootDirectory() As String
        Dim programData As String = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        Return Path.Combine(programData, "RemoteAppTool", "StrictSession")
    End Function

    Private Shared Function GetLauncherSourcePath() As String
        Dim assemblyDirectory As String = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        Return Path.Combine(assemblyDirectory, LauncherFileName)
    End Function

    Private Shared Function NormalizeStrictId(ByVal strictId As String) As String
        Dim parsed As Guid
        If String.IsNullOrEmpty(strictId) OrElse Not Guid.TryParse(strictId, parsed) Then
            Throw New ArgumentException("Strict App Session ID must be a valid GUID.", "strictId")
        End If

        Return parsed.ToString("D")
    End Function

    Private Shared Function FilesMatch(ByVal firstPath As String, ByVal secondPath As String) As Boolean
        Dim firstInfo As New FileInfo(firstPath)
        Dim secondInfo As New FileInfo(secondPath)
        If firstInfo.Length <> secondInfo.Length Then Return False

        Using algorithm As SHA256 = SHA256.Create()
            Using firstStream As FileStream = File.OpenRead(firstPath)
                Using secondStream As FileStream = File.OpenRead(secondPath)
                    Dim firstHash() As Byte = algorithm.ComputeHash(firstStream)
                    Dim secondHash() As Byte = algorithm.ComputeHash(secondStream)
                    If firstHash.Length <> secondHash.Length Then Return False

                    For index As Integer = 0 To firstHash.Length - 1
                        If firstHash(index) <> secondHash(index) Then Return False
                    Next
                End Using
            End Using
        End Using

        Return True
    End Function

    Private Shared Sub ApplyLauncherDirectoryAcl(ByVal directoryPath As String)
        Dim directoryInfo As New DirectoryInfo(directoryPath)
        Dim security As New DirectorySecurity()
        security.SetAccessRuleProtection(True, False)

        Dim inheritance As InheritanceFlags = InheritanceFlags.ContainerInherit Or InheritanceFlags.ObjectInherit
        Dim propagation As PropagationFlags = PropagationFlags.None

        security.AddAccessRule(New FileSystemAccessRule(
            New SecurityIdentifier(WellKnownSidType.LocalSystemSid, Nothing),
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow))

        security.AddAccessRule(New FileSystemAccessRule(
            New SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, Nothing),
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow))

        security.AddAccessRule(New FileSystemAccessRule(
            New SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, Nothing),
            FileSystemRights.ReadAndExecute,
            inheritance,
            propagation,
            AccessControlType.Allow))

        directoryInfo.SetAccessControl(security)
    End Sub
End Class
