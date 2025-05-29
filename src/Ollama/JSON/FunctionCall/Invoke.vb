
Namespace JSON.FunctionCall

    Public Class ToolCall

        Public Property id As String
        Public Property [function] As FunctionCall

        Public Overrides Function ToString() As String
            Return $"[{id}] {[function]}"
        End Function

    End Class

    ''' <summary>
    ''' the function invoke and parameters
    ''' </summary>
    ''' <remarks>
    ''' call func(...)
    ''' </remarks>
    Public Class FunctionCall

        Public Property name As String
        Public Property arguments As Dictionary(Of String, String)

        Public Overrides Function ToString() As String
            Return $"call {name}({arguments.Select(Function(a) $"{a.Key}:={a.Value}").JoinBy(", ")})"
        End Function

    End Class
End Namespace