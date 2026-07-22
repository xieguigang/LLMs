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

    ''' <summary>
    ''' 从 LLM 响应中提取 JSON 内容。支持提取完整、截断/不完整的 JSON 数据。
    ''' </summary>
    Public Function ExtractJsonFromResponse() As String
        If String.IsNullOrEmpty(output) Then Return ""

        Dim contentToSearch As String = output

        ' 1. 尝试剥离 ```json ... ``` 代码块标记
        Dim codeBlockStart = output.IndexOf("```")
        If codeBlockStart >= 0 Then
            ' 寻找代码块内容开始位置 (跳过 ```json 和换行符)
            Dim contentStart = output.IndexOf(vbLf, codeBlockStart)
            If contentStart >= 0 Then
                contentStart += 1
            Else
                contentStart = codeBlockStart + 3
            End If

            ' 寻找代码块结束标记
            Dim codeBlockEnd = output.IndexOf("```", contentStart)
            If codeBlockEnd > contentStart Then
                ' 存在闭合的代码块，提取中间内容
                contentToSearch = output.Substring(contentStart, codeBlockEnd - contentStart)
            Else
                ' 代码块未闭合（说明输出被截断），提取到字符串末尾
                contentToSearch = output.Substring(contentStart)
            End If
        End If

        ' 2. 在目标内容中查找第一个 '{' 作为 JSON 起始点
        Dim startIdx = contentToSearch.IndexOf("{"c)
        If startIdx < 0 Then Return ""

        ' 3. 使用括号配对状态机寻找 JSON 的真正结束位置
        Dim depth As Integer = 0
        Dim inString As Boolean = False
        Dim escapeNext As Boolean = False

        For i As Integer = startIdx To contentToSearch.Length - 1
            Dim c As Char = contentToSearch(i)

            If inString Then
                ' 当前处于字符串内部，忽略所有括号
                If escapeNext Then
                    escapeNext = False ' 跳过被转义的字符
                ElseIf c = "\"c Then
                    escapeNext = True  ' 遇到转义符，标记下一个字符被转义
                ElseIf c = """"c Then
                    inString = False   ' 遇到闭合双引号，退出字符串模式
                End If
            Else
                ' 当前处于 JSON 结构中
                If c = """"c Then
                    inString = True    ' 遇到起始双引号，进入字符串模式
                ElseIf c = "{"c OrElse c = "["c Then
                    depth += 1          ' 遇到开括号，深度加1
                ElseIf c = "}"c OrElse c = "]"c Then
                    depth -= 1          ' 遇到闭括号，深度减1
                    If depth = 0 Then
                        ' 括号完全配平，找到了完整的 JSON 结束位置
                        Return contentToSearch.Substring(startIdx, i - startIdx + 1)
                    End If
                End If
            End If
        Next

        ' 4. 如果遍历结束仍未闭合 (depth > 0)，说明 JSON 不完整（被 LLM 截断）
        ' 直接提取从起始 '{' 到字符串末尾的所有内容作为不完整的 JSON 返回
        Return contentToSearch.Substring(startIdx)
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


