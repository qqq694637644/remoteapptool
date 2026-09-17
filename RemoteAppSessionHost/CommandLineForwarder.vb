Imports System
Imports System.Collections.Generic
Imports System.Text

Friend NotInheritable Class CommandLineForwarder
    Private Sub New()
    End Sub

    Public Shared Function BuildArguments(ByVal arguments As IEnumerable(Of String)) As String
        If arguments Is Nothing Then Return String.Empty

        Dim builder As New StringBuilder()
        Dim first As Boolean = True

        For Each argument As String In arguments
            If Not first Then builder.Append(" "c)
            builder.Append(QuoteArgument(If(argument, String.Empty)))
            first = False
        Next

        Return builder.ToString()
    End Function

    Private Shared Function QuoteArgument(ByVal value As String) As String
        If value.Length = 0 Then Return """"""

        Dim needsQuotes As Boolean = False
        For Each ch As Char In value
            If Char.IsWhiteSpace(ch) OrElse ch = """"c Then
                needsQuotes = True
                Exit For
            End If
        Next

        If Not needsQuotes Then Return value

        Dim builder As New StringBuilder()
        builder.Append(""""c)
        Dim backslashes As Integer = 0

        For Each ch As Char In value
            If ch = "\"c Then
                backslashes += 1
            ElseIf ch = """"c Then
                AppendBackslashes(builder, backslashes * 2 + 1)
                builder.Append(""""c)
                backslashes = 0
            Else
                AppendBackslashes(builder, backslashes)
                backslashes = 0
                builder.Append(ch)
            End If
        Next

        AppendBackslashes(builder, backslashes * 2)
        builder.Append(""""c)
        Return builder.ToString()
    End Function

    Private Shared Sub AppendBackslashes(ByVal builder As StringBuilder, ByVal count As Integer)
        For index As Integer = 1 To count
            builder.Append("\"c)
        Next
    End Sub
End Class
