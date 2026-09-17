Imports System
Imports System.Collections.Generic

Friend NotInheritable Class SessionHostOptions
    Public Property TargetPath As String
    Public Property TargetArguments As List(Of String)
    Public Property StartupGraceMs As Integer
    Public Property CloseGraceMs As Integer
    Public Property DisconnectGraceMs As Integer
    Public Property PollIntervalMs As Integer
    Public Property NoLogoff As Boolean
    Public Property TerminationMode As Integer
    Public Property ConfigurationSource As String

    Friend Sub New()
        TargetArguments = New List(Of String)()
        StartupGraceMs = 10000
        CloseGraceMs = 1500
        DisconnectGraceMs = 30000
        PollIntervalMs = 250
        TerminationMode = 0
        ConfigurationSource = "spike-command-line"
    End Sub

    Public Shared Function Resolve(ByVal args() As String) As SessionHostOptions
        If args Is Nothing Then Throw New ArgumentNullException("args")

        Dim deployedOptions As SessionHostOptions = Nothing
        If StrictAppConfig.TryLoadFromDeploymentPath(args, deployedOptions) Then
            Return deployedOptions
        End If

        Return ParseSpikeArguments(args)
    End Function

    Private Shared Function ParseSpikeArguments(ByVal args() As String) As SessionHostOptions

        Dim options As New SessionHostOptions()
        Dim index As Integer = 1

        While index < args.Length
            Dim current As String = args(index)

            Select Case current
                Case "--target"
                    options.TargetPath = RequireValue(args, index, current)
                Case "--startup-grace-ms"
                    options.StartupGraceMs = ParseInteger(RequireValue(args, index, current), current)
                Case "--close-grace-ms"
                    options.CloseGraceMs = ParseInteger(RequireValue(args, index, current), current)
                Case "--disconnect-grace-ms"
                    options.DisconnectGraceMs = ParseInteger(RequireValue(args, index, current), current)
                Case "--poll-ms"
                    options.PollIntervalMs = ParseInteger(RequireValue(args, index, current), current)
                Case "--no-logoff"
                    options.NoLogoff = True
                Case "--"
                    index += 1
                    While index < args.Length
                        options.TargetArguments.Add(args(index))
                        index += 1
                    End While
                    Exit While
                Case Else
                    Throw New ArgumentException("Unknown option: " & current)
            End Select

            index += 1
        End While

        If String.IsNullOrWhiteSpace(options.TargetPath) Then
            Throw New ArgumentException("--target is required")
        End If

        If options.StartupGraceMs < 0 Then Throw New ArgumentException("--startup-grace-ms must be >= 0")
        If options.CloseGraceMs < 0 Then Throw New ArgumentException("--close-grace-ms must be >= 0")
        If options.DisconnectGraceMs < -1 Then Throw New ArgumentException("--disconnect-grace-ms must be >= -1")
        If options.PollIntervalMs < 50 OrElse options.PollIntervalMs > 5000 Then
            Throw New ArgumentException("--poll-ms must be between 50 and 5000")
        End If

        Return options
    End Function

    Private Shared Function RequireValue(ByVal args() As String, ByRef index As Integer, ByVal optionName As String) As String
        index += 1
        If index >= args.Length Then Throw New ArgumentException(optionName & " requires a value")
        Return args(index)
    End Function

    Private Shared Function ParseInteger(ByVal value As String, ByVal optionName As String) As Integer
        Dim parsed As Integer
        If Not Integer.TryParse(value, parsed) Then
            Throw New ArgumentException(optionName & " requires an integer value")
        End If
        Return parsed
    End Function
End Class
