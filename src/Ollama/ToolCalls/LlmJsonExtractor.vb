Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions

Public Module LlmJsonExtractor

    ''' <summary>
    ''' 从LLM的响应文本中提取并尝试修复JSON字符串。
    ''' 支持纯JSON、Markdown代码块包裹的JSON、被截断的JSON（对象或数组）。
    ''' </summary>
    ''' <param name="rawResponse">LLM输出的原始文本</param>
    ''' <returns>提取并尽可能修复后的JSON字符串</returns>
    Public Function ExtractJsonFromLlmResponse(rawResponse As String) As String
        If String.IsNullOrWhiteSpace(rawResponse) Then Return String.Empty

        Dim text As String = rawResponse.Trim()

        ' 1. 尝试直接解析整个响应（LLM可能只输出了纯JSON）
        If IsValidJson(text) Then
            Return text
        End If

        ' 2. 尝试从Markdown代码块中提取 (JSON … 或 … ```)
        Dim markdownJson As String = ExtractFromMarkdown(text)
        If Not String.IsNullOrEmpty(markdownJson) Then
            If IsValidJson(markdownJson) Then Return markdownJson
            ' 如果代码块内的JSON被截断，尝试修复
            Dim repaired As String = TryRepairJson(markdownJson)
            If IsValidJson(repaired) Then Return repaired
        End If

        ' 3. 尝试寻找第一个 { 或 [ 开始的子串，并提取括号匹配的内容
        Dim startIndex As Integer = -1
        For i As Integer = 0 To text.Length - 1
            If text(i) = "{"c OrElse text(i) = "["c Then
                startIndex = i
                Exit For
            End If
        Next

        If startIndex > -1 Then
            Dim rawJson As String = ExtractBalancedSubstring(text.Substring(startIndex))
            If IsValidJson(rawJson) Then Return rawJson

            ' 如果提取的括号匹配内容无效，尝试修复截断
            Dim repaired As String = TryRepairJson(rawJson)
            If IsValidJson(repaired) Then Return repaired
        End If

        ' 4. 如果以上都失败，返回最后尝试修复的原文本（兜底）
        Dim finalRepair As String = TryRepairJson(text)
        If IsValidJson(finalRepair) Then Return finalRepair

        Return String.Empty ' 彻底无法解析
    End Function

    ''' <summary>
    ''' 检查字符串是否为合法的JSON格式
    ''' </summary>
    Private Function IsValidJson(str As String) As Boolean
        If String.IsNullOrWhiteSpace(str) Then Return False
        Try
            ' 尝试解析为任意JSON节点（对象、数组、基础类型）
            Using doc As JsonDocument = JsonDocument.Parse(str)
                Return True
            End Using
        Catch ex As Exception
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 使用正则表达式提取Markdown代码块中的内容，兼容被截断（没有闭合```）的情况
    ''' </summary>
    Private Function ExtractFromMarkdown(str As String) As String
        ' 匹配标准的闭合代码块: ```json ... ``` 或 ``` ... 
        Dim patternClosed As String = "
(?:json|JSON)?\s*([\s\S]*?)```"
        Dim match As Match = Regex.Match(str, patternClosed)
        If match.Success Then
            Return match.Groups(1).Value.Trim()
        End If

        ' 匹配未闭合的代码块（LLM输出被截断，只有 ```json 开头）
        Dim patternOpen As String = "```(?:json|JSON)?\s*([\s\S]*)"
        match = Regex.Match(str, patternOpen)
        If match.Success Then
            Return match.Groups(1).Value.Trim()
        End If

        Return String.Empty
    End Function

    ''' <summary>
    ''' 提取括号匹配的子串。如果未闭合（截断），则截取到末尾。
    ''' </summary>
    Private Function ExtractBalancedSubstring(s As String) As String
        If String.IsNullOrEmpty(s) Then Return s

        Dim openChar As Char = s(0)
        Dim closeChar As Char
        If openChar = "{"c Then
            closeChar = "}"c
        ElseIf openChar = "["c Then
            closeChar = "]"c
        Else
            Return s ' 不是JSON括号开头，直接返回原字符串
        End If

        Dim depth As Integer = 0
        Dim inString As Boolean = False
        Dim escape As Boolean = False
        Dim endPos As Integer = -1

        For i As Integer = 0 To s.Length - 1
            Dim c As Char = s(i)

            If escape Then
                escape = False
                Continue For
            End If

            If c = "\"c AndAlso inString Then
                escape = True
                Continue For
            End If

            If c = """"c Then
                inString = Not inString
                Continue For
            End If

            If Not inString Then
                If c = openChar Then
                    depth += 1
                ElseIf c = closeChar Then
                    depth -= 1
                    If depth = 0 Then
                        endPos = i
                        Exit For
                    End If
                End If
            End If
        Next

        If endPos >= 0 Then
            Return s.Substring(0, endPos + 1)
        Else
            ' 如果遍历完整个字符串括号仍未闭合（发生截断），返回整个截取后的字符串
            Return s
        End If
    End Function

    ''' <summary>
    ''' 尝试修复被截断的JSON字符串
    ''' </summary>
    Private Function TryRepairJson(str As String) As String
        If String.IsNullOrWhiteSpace(str) Then Return str
        Dim s As String = str.Trim()

        Dim depth As Integer = 0
        Dim inString As Boolean = False
        Dim escape As Boolean = False
        Dim lastSafePos As Integer = 0
        Dim stack As New Stack(Of Char)()

        ' 遍历字符串，记录状态机，寻找最后一个安全的截断点
        For i As Integer = 0 To s.Length - 1
            Dim c As Char = s(i)
            If escape Then
                escape = False
                Continue For
            End If

            If c = "\"c AndAlso inString Then
                escape = True
                Continue For
            End If

            If c = """"c Then
                inString = Not inString
                If Not inString Then
                    lastSafePos = i ' 字符串闭合完成，是一个安全点
                End If
                Continue For
            End If

            If Not inString Then
                If c = "{"c OrElse c = "["c Then
                    stack.Push(c)
                    lastSafePos = i
                ElseIf c = "}"c OrElse c = "]"c Then
                    If stack.Count > 0 Then stack.Pop()
                    lastSafePos = i
                ElseIf c = ","c Then
                    lastSafePos = i ' 逗号是安全的截断点
                End If
            End If
        Next

        ' 如果在字符串内部发生截断，退回到上一个闭合字符串的末尾
        If inString Then
            s = s.Substring(0, lastSafePos + 1).Trim()
        Else
            s = s.Substring(0, lastSafePos + 1).Trim()
        End If

        ' 清除可能因为截断留下的末尾不完整逗号
        If s.EndsWith(",") Then
            s = s.Substring(0, s.Length - 1).Trim()
        End If

        ' 根据状态机记录的未闭合括号，自动补全
        Dim sb As New StringBuilder(s)
        While stack.Count > 0
            Dim openChar As Char = stack.Pop()
            If openChar = "{"c Then
                sb.Append("}"c)
            Else
                sb.Append("]"c)
            End If
        End While

        Return sb.ToString()
    End Function
End Module