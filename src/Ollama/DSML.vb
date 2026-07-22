Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic.Language

' 3. 解析逻辑模块
Public Module DsmlParser

    ''' <summary>
    ''' 匹配 invoke 块及其内部内容 (使用 Singleline 让 . 可以匹配换行符)
    ''' </summary>
    ReadOnly InvokeRegex As New Regex(
        "<｜｜DSML｜｜invoke name=""(?<funcName>[^""]+)"">(?<body>.*?)</｜｜DSML｜｜invoke>",
        RegexOptions.Singleline Or RegexOptions.Compiled)

    ''' <summary>
    ''' 匹配 parameter 标签
    ''' </summary>
    ReadOnly ParamRegex As New Regex(
        "<｜｜DSML｜｜parameter name=""(?<paramName>[^""]+)"" string=""(?<paramType>[^""]+)"">(?<paramValue>.*?)</｜｜DSML｜｜parameter>",
        RegexOptions.Singleline Or RegexOptions.Compiled)

    ''' <summary>
    ''' 将 LLM 返回的 DSML 格式文本解析为 ToolCallInfo 列表
    ''' </summary>
    Public Iterator Function ParseToolCalls(input As String) As IEnumerable(Of ToolCallInfo)
        If Not String.IsNullOrWhiteSpace(input) Then
            ' 1. 提取所有 invoke 节点
            Dim invokeMatches As MatchCollection = InvokeRegex.Matches(input)
            Dim id As i32 = 1

            For Each m As Match In invokeMatches
                Dim toolCall As New ToolCallInfo With {
                    .Id = ++id, ' 自动生成一个短ID
                    .FunctionName = m.Groups("funcName").Value,
                    .FunctionArguments = New Dictionary(Of String, String)()
                }

                ' 2. 在 invoke 节点内部提取所有 parameter
                Dim body As String = m.Groups("body").Value
                Dim paramMatches As MatchCollection = ParamRegex.Matches(body)

                For Each pm As Match In paramMatches
                    Dim pName As String = pm.Groups("paramName").Value
                    Dim pValue As String = pm.Groups("paramValue").Value.Trim()

                    ' 如果需要严格区分整数和字符串，可以解析 paramType:
                    ' Dim pType As String = pm.Groups("paramType").Value 
                    ' If pType = "false" Then ... (按需转换类型)

                    ' 因为目标字典是 Dictionary(Of String, String)，所以直接存入字符串
                    toolCall.FunctionArguments(pName) = pValue
                Next

                Yield toolCall
            Next
        End If
    End Function
End Module
