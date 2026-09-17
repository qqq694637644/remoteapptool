Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices

Friend NotInheritable Class ProcessTracker
    Private Const TH32CS_SNAPPROCESS As UInteger = &H2UI
    Private Shared ReadOnly InvalidHandleValue As New IntPtr(-1)

    Private ReadOnly SessionId As Integer
    Private ReadOnly KnownProcessIds As New HashSet(Of Integer)()

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure ProcessEntry32
        Public Size As UInteger
        Public Usage As UInteger
        Public ProcessId As UInteger
        Public DefaultHeapId As UIntPtr
        Public ModuleId As UInteger
        Public Threads As UInteger
        Public ParentProcessId As UInteger
        Public PriorityClassBase As Integer
        Public Flags As UInteger
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
        Public ExeFile As String
    End Structure

    Private Structure ProcessRelation
        Public ProcessId As Integer
        Public ParentProcessId As Integer
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CreateToolhelp32Snapshot(ByVal flags As UInteger, ByVal processId As UInteger) As IntPtr
    End Function

    <DllImport("kernel32.dll", EntryPoint:="Process32FirstW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function Process32First(ByVal snapshot As IntPtr, ByRef entry As ProcessEntry32) As Boolean
    End Function

    <DllImport("kernel32.dll", EntryPoint:="Process32NextW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function Process32Next(ByVal snapshot As IntPtr, ByRef entry As ProcessEntry32) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function ProcessIdToSessionId(ByVal processId As UInteger, ByRef sessionId As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(ByVal handle As IntPtr) As Boolean
    End Function

    Public Sub New(ByVal rootProcessId As Integer, ByVal sessionId As Integer)
        If rootProcessId <= 0 Then Throw New ArgumentOutOfRangeException("rootProcessId")
        Me.SessionId = sessionId
        KnownProcessIds.Add(rootProcessId)
    End Sub

    Public Sub Adopt(ByVal processIds As IEnumerable(Of Integer))
        If processIds Is Nothing Then Return

        For Each processId As Integer In processIds
            If processId > 0 AndAlso IsInExpectedSession(processId) Then
                KnownProcessIds.Add(processId)
            End If
        Next
    End Sub

    Public Function Refresh() As HashSet(Of Integer)
        Dim relations As List(Of ProcessRelation) = SnapshotProcesses()
        Dim activeIds As New HashSet(Of Integer)()

        For Each relation As ProcessRelation In relations
            activeIds.Add(relation.ProcessId)
        Next

        Dim activeTracked As New HashSet(Of Integer)()
        For Each processId As Integer In KnownProcessIds
            If activeIds.Contains(processId) Then activeTracked.Add(processId)
        Next

        Dim changed As Boolean = True
        While changed
            changed = False

            For Each relation As ProcessRelation In relations
                If KnownProcessIds.Contains(relation.ProcessId) Then Continue For
                If Not KnownProcessIds.Contains(relation.ParentProcessId) Then Continue For
                If Not IsInExpectedSession(relation.ProcessId) Then Continue For

                KnownProcessIds.Add(relation.ProcessId)
                activeTracked.Add(relation.ProcessId)
                changed = True
            Next
        End While

        Return activeTracked
    End Function

    Private Function IsInExpectedSession(ByVal processId As Integer) As Boolean
        Dim nativeSessionId As UInteger
        If Not ProcessIdToSessionId(CUInt(processId), nativeSessionId) Then Return False
        Return CInt(nativeSessionId) = SessionId
    End Function

    Private Shared Function SnapshotProcesses() As List(Of ProcessRelation)
        Dim snapshot As IntPtr = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0UI)
        If snapshot = InvalidHandleValue Then
            Throw New InvalidOperationException("CreateToolhelp32Snapshot failed with Win32 error " & Marshal.GetLastWin32Error().ToString())
        End If

        Try
            Dim result As New List(Of ProcessRelation)()
            Dim entry As New ProcessEntry32()
            entry.Size = CUInt(Marshal.SizeOf(GetType(ProcessEntry32)))

            If Process32First(snapshot, entry) Then
                Do
                    Dim relation As New ProcessRelation()
                    relation.ProcessId = CInt(entry.ProcessId)
                    relation.ParentProcessId = CInt(entry.ParentProcessId)
                    result.Add(relation)

                    entry.Size = CUInt(Marshal.SizeOf(GetType(ProcessEntry32)))
                Loop While Process32Next(snapshot, entry)
            End If

            Return result
        Finally
            CloseHandle(snapshot)
        End Try
    End Function
End Class
