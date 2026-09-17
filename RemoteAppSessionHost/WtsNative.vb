Imports System
Imports System.Runtime.InteropServices

Friend NotInheritable Class WtsNative
    Friend Enum ConnectState
        Active = 0
        Connected = 1
        ConnectQuery = 2
        Shadow = 3
        Disconnected = 4
        Idle = 5
        Listen = 6
        Reset = 7
        Down = 8
        Init = 9
        Unknown = -1
    End Enum

    Private Enum InfoClass
        ConnectState = 8
    End Enum

    Private Sub New()
    End Sub

    <DllImport("wtsapi32.dll", EntryPoint:="WTSQuerySessionInformationW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function WTSQuerySessionInformation(
        ByVal serverHandle As IntPtr,
        ByVal sessionId As Integer,
        ByVal infoClass As InfoClass,
        ByRef buffer As IntPtr,
        ByRef bytesReturned As Integer) As Boolean
    End Function

    <DllImport("wtsapi32.dll")>
    Private Shared Sub WTSFreeMemory(ByVal memory As IntPtr)
    End Sub

    <DllImport("wtsapi32.dll", SetLastError:=True)>
    Private Shared Function WTSLogoffSession(
        ByVal serverHandle As IntPtr,
        ByVal sessionId As Integer,
        <MarshalAs(UnmanagedType.Bool)> ByVal wait As Boolean) As Boolean
    End Function

    Public Shared Function TryGetConnectState(
        ByVal sessionId As Integer,
        ByRef state As ConnectState,
        ByRef errorCode As Integer) As Boolean

        Dim buffer As IntPtr = IntPtr.Zero
        Dim bytesReturned As Integer = 0

        Try
            If Not WTSQuerySessionInformation(IntPtr.Zero, sessionId, InfoClass.ConnectState, buffer, bytesReturned) Then
                errorCode = Marshal.GetLastWin32Error()
                state = ConnectState.Unknown
                Return False
            End If

            If buffer = IntPtr.Zero OrElse bytesReturned < 4 Then
                errorCode = 0
                state = ConnectState.Unknown
                Return False
            End If

            state = CType(Marshal.ReadInt32(buffer), ConnectState)
            errorCode = 0
            Return True
        Finally
            If buffer <> IntPtr.Zero Then WTSFreeMemory(buffer)
        End Try
    End Function

    Public Shared Function TryLogoff(ByVal sessionId As Integer, ByRef errorCode As Integer) As Boolean
        If WTSLogoffSession(IntPtr.Zero, sessionId, False) Then
            errorCode = 0
            Return True
        End If

        errorCode = Marshal.GetLastWin32Error()
        Return False
    End Function
End Class
