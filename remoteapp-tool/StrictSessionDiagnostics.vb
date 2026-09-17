Public NotInheritable Class StrictSessionDiagnostics
    Private Const PolicyPath As String = "SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
    Private Const RuntimePath As String = "SYSTEM\CurrentControlSet\Control\Terminal Server"

    Private Sub New()
    End Sub

    Public Shared Function IsSingleSessionPerUserEnabled() As Boolean
        Dim policyValue As Nullable(Of Integer) = ReadDword(PolicyPath, "fSingleSessionPerUser")
        If policyValue.HasValue Then Return policyValue.Value <> 0

        Dim runtimeValue As Nullable(Of Integer) = ReadDword(RuntimePath, "fSingleSessionPerUser")
        Return runtimeValue.HasValue AndAlso runtimeValue.Value <> 0
    End Function

    Private Shared Function ReadDword(ByVal registryPath As String, ByVal valueName As String) As Nullable(Of Integer)
        Try
            Using key As Microsoft.Win32.RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryPath)
                If key Is Nothing Then Return Nothing

                Dim rawValue As Object = key.GetValue(valueName, Nothing)
                If rawValue Is Nothing Then Return Nothing

                Return Convert.ToInt32(rawValue)
            End Using
        Catch
            Return Nothing
        End Try
    End Function
End Class
