Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices

Friend NotInheritable Class WindowTracker
    Private Delegate Function EnumWindowsCallback(ByVal windowHandle As IntPtr, ByVal parameter As IntPtr) As Boolean

    Private Sub New()
    End Sub

    <DllImport("user32.dll")>
    Private Shared Function EnumWindows(ByVal callback As EnumWindowsCallback, ByVal parameter As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll")>
    Private Shared Function IsWindowVisible(ByVal windowHandle As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll")>
    Private Shared Function GetWindowThreadProcessId(ByVal windowHandle As IntPtr, ByRef processId As UInteger) As UInteger
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function ProcessIdToSessionId(ByVal processId As UInteger, ByRef sessionId As UInteger) As Boolean
    End Function

    Public Shared Function CountVisibleWindows(ByVal processIds As HashSet(Of Integer)) As Integer
        If processIds Is Nothing OrElse processIds.Count = 0 Then Return 0

        Dim visibleCount As Integer = 0
        Dim callback As EnumWindowsCallback =
            Function(windowHandle As IntPtr, parameter As IntPtr) As Boolean
                If Not IsWindowVisible(windowHandle) Then Return True

                Dim processId As UInteger
                GetWindowThreadProcessId(windowHandle, processId)
                If processIds.Contains(CInt(processId)) Then visibleCount += 1
                Return True
            End Function

        EnumWindows(callback, IntPtr.Zero)
        Return visibleCount
    End Function

    Public Shared Function GetVisibleProcessIdsInSession(ByVal sessionId As Integer, ByVal excludedProcessId As Integer) As HashSet(Of Integer)
        Dim processIds As New HashSet(Of Integer)()

        Dim callback As EnumWindowsCallback =
            Function(windowHandle As IntPtr, parameter As IntPtr) As Boolean
                If Not IsWindowVisible(windowHandle) Then Return True

                Dim processId As UInteger
                GetWindowThreadProcessId(windowHandle, processId)
                If CInt(processId) = excludedProcessId Then Return True

                Dim processSessionId As UInteger
                If ProcessIdToSessionId(processId, processSessionId) AndAlso CInt(processSessionId) = sessionId Then
                    processIds.Add(CInt(processId))
                End If

                Return True
            End Function

        EnumWindows(callback, IntPtr.Zero)
        Return processIds
    End Function
End Class
