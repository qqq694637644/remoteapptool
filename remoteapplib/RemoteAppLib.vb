Public Class RemoteAppCollection

    Inherits System.Collections.CollectionBase

    Public Sub Add(RemoteApp As RemoteApp)
        List.Add(RemoteApp)
    End Sub

    Public Sub Remove(RemoteApp As RemoteApp)
        List.Remove(RemoteApp)
    End Sub
End Class

Public Class RemoteApp

    Public Name As String
    Public FullName As String
    Public Path As String
    Public VPath As String
    Public IconPath As String
    Public IconIndex As Integer = 0
    Public CommandLine As String = ""
    Public CommandLineOption As Integer = 1
    Public TSWA As Boolean = False
    Public FileTypeAssociations As FileTypeAssociationCollection
    Public StrictSessionEnabled As Boolean = False
    Public StrictSessionId As String = ""
    Public StrictTerminationMode As Integer = 0
    Public StrictStartupGraceMs As Integer = 10000
    Public StrictCloseGraceMs As Integer = 1500
    Public StrictDisconnectBehavior As Integer = 1
    Public StrictDisconnectGraceMs As Integer = 30000
    Public StrictSchemaVersion As Integer = 1
    Public StrictSessionLauncherPath As String = ""

End Class

Public Class FileTypeAssociation
    Public Extension As String
    Public IconPath As String
    Public IconIndex As String
End Class

Public Class FileTypeAssociationCollection
    Inherits System.Collections.CollectionBase

    Public Sub Add(FileTypeAssociation As FileTypeAssociation)
        List.Add(FileTypeAssociation)
    End Sub

    Public Sub Remove(FileTypeAssociation As FileTypeAssociation)
        List.Remove(FileTypeAssociation)
    End Sub

    Public Function GetFlatFileTypes() As String
        GetFlatFileTypes = ""
        If List.Count > 0 Then
            For Each listItem As FileTypeAssociation In List
                GetFlatFileTypes += ",." & listItem.Extension
            Next
            GetFlatFileTypes = GetFlatFileTypes.Substring(1)
        End If
    End Function
End Class

Public Class IconSelection
    Public IconPath As String
    Public IconIndex As String
End Class


Public Class SystemRemoteApps
    Private Legacy32bit As Boolean = False
    Private RegistryPath As String = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications"
    Private BaseKey As Microsoft.Win32.RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryPath)
    Private BaseKeyWrite As Microsoft.Win32.RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryPath, True)

    Public Sub Init()
        Dim RegistryPathCV As String = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\"
        Dim cvKey As Microsoft.Win32.RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryPathCV, True)
        Dim tsKey As Microsoft.Win32.RegistryKey = cvKey.CreateSubKey("Terminal Server")
        Dim tsaaKey As Microsoft.Win32.RegistryKey = tsKey.CreateSubKey("TSAppAllowList")
        Dim appKey As Microsoft.Win32.RegistryKey = tsaaKey.CreateSubKey("Applications")
    End Sub

    Public Function GetAll() As RemoteAppCollection

        Dim SystemAppCollection As New RemoteAppCollection

        For Each App As String In BaseKey.GetSubKeyNames
            Dim RemoteApp As New RemoteApp
            RemoteApp = GetApp(App)
            SystemAppCollection.Add(RemoteApp)
        Next

        BaseKey.Close()

        Return SystemAppCollection

    End Function

    Function GetApp(Name As String) As RemoteApp
        Dim App As New RemoteApp

        Dim AppKey As Microsoft.Win32.RegistryKey = BaseKey.OpenSubKey(Name)

        If AppKey Is Nothing Then Return Nothing

        App.Name = Name
        App.FullName = AppKey.GetValue("Name", "")

        App.StrictSessionEnabled = CInt(AppKey.GetValue("RemoteAppToolStrictSession", 0)) = 1
        App.StrictSessionId = CStr(AppKey.GetValue("RemoteAppToolStrictId", ""))
        App.StrictTerminationMode = CInt(AppKey.GetValue("RemoteAppToolTerminationMode", 0))
        App.StrictStartupGraceMs = CInt(AppKey.GetValue("RemoteAppToolStartupGraceMs", 10000))
        App.StrictCloseGraceMs = CInt(AppKey.GetValue("RemoteAppToolCloseGraceMs", 1500))
        App.StrictDisconnectBehavior = CInt(AppKey.GetValue("RemoteAppToolDisconnectBehavior", 1))
        App.StrictDisconnectGraceMs = CInt(AppKey.GetValue("RemoteAppToolDisconnectGraceMs", 30000))
        App.StrictSchemaVersion = CInt(AppKey.GetValue("RemoteAppToolSchemaVersion", 1))

        If App.StrictSessionEnabled Then
            App.StrictSessionLauncherPath = CStr(AppKey.GetValue("Path", ""))
            App.Path = CStr(AppKey.GetValue("RemoteAppToolTargetPath", ""))
            App.VPath = CStr(AppKey.GetValue("RemoteAppToolTargetVPath", App.Path))

            ' Fail readable for an incomplete legacy/partial strict configuration.
            If App.Path = "" Then App.Path = App.StrictSessionLauncherPath
            If App.VPath = "" Then App.VPath = App.Path
        Else
            App.Path = CStr(AppKey.GetValue("Path", ""))
            App.VPath = CStr(AppKey.GetValue("VPath", ""))
        End If
        App.CommandLine = AppKey.GetValue("RequiredCommandLine", "")
        App.CommandLineOption = AppKey.GetValue("CommandLineSetting", "1")
        App.IconPath = AppKey.GetValue("IconPath", "")
        App.IconIndex = AppKey.GetValue("IconIndex", 0)
        App.TSWA = AppKey.GetValue("ShowInTSWA", 0)

        Dim FTAKey As Microsoft.Win32.RegistryKey = AppKey.OpenSubKey("Filetypes")
        If Not FTAKey Is Nothing Then
            Dim FTACol As New FileTypeAssociationCollection

            For Each FTAValueName As String In FTAKey.GetValueNames
                Dim FTA As New FileTypeAssociation
                Dim FTAValue = FTAKey.GetValue(FTAValueName).ToString.Split(",")
                FTA.Extension = FTAValueName
                FTA.IconPath = FTAValue(0)
                FTA.IconIndex = Val(FTAValue(1))

                FTACol.Add(FTA)

            Next

            App.FileTypeAssociations = FTACol
        End If

        AppKey.Close()
        Return App

    End Function

    Public Sub SaveApp(RemoteApp As RemoteApp)
        Dim ExistingStrictId As String = ""
        Dim ExistingKey As Microsoft.Win32.RegistryKey = BaseKey.OpenSubKey(RemoteApp.Name)
        If Not ExistingKey Is Nothing Then
            ExistingStrictId = CStr(ExistingKey.GetValue("RemoteAppToolStrictId", ""))
            ExistingKey.Close()
        End If

        Dim StrictLauncherPath As String = ""
        If RemoteApp.StrictSessionEnabled Then
            If String.IsNullOrEmpty(RemoteApp.StrictSessionId) Then
                RemoteApp.StrictSessionId = System.Guid.NewGuid().ToString("D")
            End If

            ' Deploy first. Registry Path/VPath are not switched until the launcher is ready.
            StrictLauncherPath = StrictSessionDeployment.EnsureLauncher(RemoteApp.StrictSessionId)
            RemoteApp.StrictSessionLauncherPath = StrictLauncherPath
        End If

        BaseKeyWrite.CreateSubKey(RemoteApp.Name)

        Dim AppKey As Microsoft.Win32.RegistryKey = BaseKey.OpenSubKey(RemoteApp.Name, True)

        AppKey.SetValue("Name", RemoteApp.FullName, Microsoft.Win32.RegistryValueKind.String)

        AppKey.SetValue("RequiredCommandLine", RemoteApp.CommandLine, Microsoft.Win32.RegistryValueKind.String)
        AppKey.SetValue("CommandLineSetting", RemoteApp.CommandLineOption, Microsoft.Win32.RegistryValueKind.DWord)
        AppKey.SetValue("IconPath", RemoteApp.IconPath, Microsoft.Win32.RegistryValueKind.String)
        AppKey.SetValue("IconIndex", RemoteApp.IconIndex, Microsoft.Win32.RegistryValueKind.DWord)
        AppKey.SetValue("ShowInTSWA", RemoteApp.TSWA, Microsoft.Win32.RegistryValueKind.DWord)

        If Not RemoteApp.FileTypeAssociations Is Nothing Then
            If Not AppKey.OpenSubKey("Filetypes") Is Nothing Then AppKey.DeleteSubKeyTree("Filetypes")
            AppKey.CreateSubKey("Filetypes")
            Dim FTAKey = AppKey.OpenSubKey("Filetypes", True)
            For Each fta As FileTypeAssociation In RemoteApp.FileTypeAssociations
                FTAKey.SetValue(fta.Extension, fta.IconPath & "," & fta.IconIndex.ToString, Microsoft.Win32.RegistryValueKind.String)
            Next
            FTAKey.Close()
        End If

        If RemoteApp.StrictSessionEnabled Then
            AppKey.SetValue("RemoteAppToolStrictSession", 1, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolStrictId", RemoteApp.StrictSessionId, Microsoft.Win32.RegistryValueKind.String)
            AppKey.SetValue("RemoteAppToolTargetPath", RemoteApp.Path, Microsoft.Win32.RegistryValueKind.String)
            AppKey.SetValue("RemoteAppToolTargetVPath", RemoteApp.VPath, Microsoft.Win32.RegistryValueKind.String)
            AppKey.SetValue("RemoteAppToolTerminationMode", RemoteApp.StrictTerminationMode, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolStartupGraceMs", RemoteApp.StrictStartupGraceMs, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolCloseGraceMs", RemoteApp.StrictCloseGraceMs, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolDisconnectBehavior", RemoteApp.StrictDisconnectBehavior, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolDisconnectGraceMs", RemoteApp.StrictDisconnectGraceMs, Microsoft.Win32.RegistryValueKind.DWord)
            AppKey.SetValue("RemoteAppToolSchemaVersion", RemoteApp.StrictSchemaVersion, Microsoft.Win32.RegistryValueKind.DWord)

            ' Switch the published executable last, after all strict metadata is durable.
            AppKey.SetValue("Path", StrictLauncherPath, Microsoft.Win32.RegistryValueKind.String)
            AppKey.SetValue("VPath", StrictLauncherPath, Microsoft.Win32.RegistryValueKind.String)
        Else
            AppKey.SetValue("Path", RemoteApp.Path, Microsoft.Win32.RegistryValueKind.String)
            AppKey.SetValue("VPath", RemoteApp.VPath, Microsoft.Win32.RegistryValueKind.String)
            DeleteStrictValues(AppKey)
        End If

        AppKey.Close()

        If Not RemoteApp.StrictSessionEnabled AndAlso ExistingStrictId <> "" Then
            StrictSessionDeployment.CleanupLauncher(ExistingStrictId)
        ElseIf RemoteApp.StrictSessionEnabled AndAlso ExistingStrictId <> "" AndAlso Not String.Equals(ExistingStrictId, RemoteApp.StrictSessionId, StringComparison.OrdinalIgnoreCase) Then
            StrictSessionDeployment.CleanupLauncher(ExistingStrictId)
        End If

    End Sub

    Private Shared Sub DeleteStrictValues(AppKey As Microsoft.Win32.RegistryKey)
        Dim Values() As String = {
            "RemoteAppToolStrictSession",
            "RemoteAppToolStrictId",
            "RemoteAppToolTargetPath",
            "RemoteAppToolTargetVPath",
            "RemoteAppToolTerminationMode",
            "RemoteAppToolStartupGraceMs",
            "RemoteAppToolCloseGraceMs",
            "RemoteAppToolDisconnectBehavior",
            "RemoteAppToolDisconnectGraceMs",
            "RemoteAppToolSchemaVersion"
        }

        For Each ValueName As String In Values
            AppKey.DeleteValue(ValueName, False)
        Next
    End Sub

    Public Sub DuplicateApp(Name As String)
        Dim NewApp = GetApp(Name)

        Dim NewName = NewApp.Name

        While GetApp(NewName) IsNot Nothing
            NewName = NewName & " copy"
        End While

        NewApp.Name = NewName

        If NewApp.StrictSessionEnabled Then
            NewApp.StrictSessionId = System.Guid.NewGuid().ToString("D")
            NewApp.StrictSessionLauncherPath = ""
        End If

        SaveApp(NewApp)
    End Sub

    Public Sub RenameApp(RemoteAppOldName As String, RemoteAppNewName As String)
        Dim App As New RemoteApp
        Dim SystemApps As New SystemRemoteApps

        App = SystemApps.GetApp(RemoteAppOldName)
        DeleteApp(RemoteAppOldName)
        App.Name = RemoteAppNewName
        SaveApp(App)
    End Sub

    Public Sub DeleteApp(Name As String)
        Dim StrictId As String = ""
        Dim AppKey As Microsoft.Win32.RegistryKey = BaseKey.OpenSubKey(Name)
        If Not AppKey Is Nothing Then
            StrictId = CStr(AppKey.GetValue("RemoteAppToolStrictId", ""))
            AppKey.Close()
        End If

        BaseKeyWrite.DeleteSubKeyTree(Name)

        If StrictId <> "" Then StrictSessionDeployment.CleanupLauncher(StrictId)
    End Sub

    Public Property WoW6432Node As Boolean
        Get
            Return Legacy32bit
        End Get
        Set(value As Boolean)
            Legacy32bit = value
            Dim PathStart As String = "SOFTWARE"
            If Legacy32bit = True Then PathStart = "SOFTWARE\Wow6432Node"
            RegistryPath = PathStart & "\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications"
        End Set
    End Property

End Class