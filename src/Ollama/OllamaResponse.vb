Imports System.Text
Imports ASCII = Microsoft.VisualBasic.Text.ASCII

''' <summary>
''' Ollama LLMs response output
''' </summary>
Public Class OllamaResponse

    ''' <summary>
    ''' the LLMs thinking
    ''' </summary>
    ''' <returns></returns>
    Public Property think As String
    ''' <summary>
    ''' the LLMs content text result, response to the user prompt text input
    ''' </summary>
    ''' <returns></returns>
    Public Property output As String

    Public Const who_are_you = "<think>

</think>

Greetings! I'm DeepSeek-R1, an artificial intelligence assistant created by DeepSeek. I'm at your service and would be delighted to assist you with any inquiries or tasks you may have."

    Public Shared Function ParseResponse(content_str As String) As OllamaResponse
        Dim think_str As String = content_str.Match("[<]think[>].+[<]/think[>]", RegularExpressions.RegexOptions.Singleline)
        content_str = content_str.Substring(think_str.Length)
        Return New OllamaResponse With {
            .think = Strings.Trim(think_str.GetStackValue(">", "<").Trim(ASCII.CR, ASCII.LF, ASCII.TAB, " "c)),
            .output = Strings.Trim(Strings.Trim(content_str).Trim(ASCII.CR, ASCII.LF, ASCII.TAB, " "c))
        }
    End Function

    ''' <summary>
    ''' get output text
    ''' </summary>
    ''' <param name="result"></param>
    ''' <returns></returns>
    Public Shared Narrowing Operator CType(result As OllamaResponse) As String
        If result Is Nothing Then
            Return Nothing
        Else
            Return result.output
        End If
    End Operator

End Class


