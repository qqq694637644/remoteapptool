Imports System.Windows.Forms

Friend NotInheritable Class HostErrorDisplay
    Private Sub New()
    End Sub

    Public Shared Sub Show(ByVal message As String)
        Try
            MessageBox.Show(
                message,
                "RemoteApp Strict Session",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error)
        Catch
            ' Logging remains the fallback if a UI cannot be shown in this session.
        End Try
    End Sub
End Class
