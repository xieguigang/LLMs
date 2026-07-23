Imports System.Text
Imports System.Text.RegularExpressions
Imports ASCII = Microsoft.VisualBasic.Text.ASCII

''' <summary>
''' Ollama LLMs response output
''' </summary>
Public Class LLMsResponse

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

    Public Shared Function ParseResponse(content_str As String) As LLMsResponse
        Dim think_str As String = content_str.Match("[<]think[>].+[<]/think[>]", RegularExpressions.RegexOptions.Singleline)
        content_str = content_str.Substring(think_str.Length)
        Return New LLMsResponse With {
            .think = Strings.Trim(think_str.GetStackValue(">", "<").Trim(ASCII.CR, ASCII.LF, ASCII.TAB, " "c)),
            .output = Strings.Trim(Strings.Trim(content_str).Trim(ASCII.CR, ASCII.LF, ASCII.TAB, " "c))
        }
    End Function


    ''' <summary>从 LLM 响应中提取代码块</summary>
    Public Function ExtractCodeBlock(language As String) As String
        If String.IsNullOrEmpty(output) Then Return ""

        ' 尝试提取 ```language ... ``` 代码块
        Dim pattern = $"```(?:{language})?\s*([\s\S]*?)```"
        Dim match = Regex.Match(output, pattern, RegexOptions.IgnoreCase)
        If match.Success Then
            Return match.Groups(1).Value.Trim()
        End If

        ' 如果没有代码块标记，返回整个文本
        Return output.Trim()
    End Function

    ''' <summary>
    ''' 从 LLM 响应中提取 JSON 内容。支持提取完整、截断/不完整的 JSON 数据。
    ''' </summary>
    Public Function ExtractJsonFromResponse() As String
        Return LlmJsonExtractor.ExtractJsonFromLlmResponse(output)
    End Function

    ''' <summary>
    ''' get output text
    ''' </summary>
    ''' <param name="result"></param>
    ''' <returns></returns>
    Public Shared Narrowing Operator CType(result As LLMsResponse) As String
        If result Is Nothing Then
            Return Nothing
        Else
            Return result.output
        End If
    End Operator

End Class


