Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON.FunctionCall

''' <summary>
''' 统一的聊天消息模型
''' </summary>
Public Class ChatMessage
    Public Property Role As String ' system, user, assistant, tool
    Public Property Content As String
    Public Property ToolCalls As List(Of ToolCallInfo) ' 仅当 Role=assistant 且触发工具时使用
    Public Property ToolCallId As String ' 仅当 Role=tool 时使用

    Public Overrides Function ToString() As String
        Return Content
    End Function
End Class

''' <summary>
''' 统一的工具调用信息
''' </summary>
Public Class ToolCallInfo
    Public Property Id As String
    Public Property FunctionName As String
    Public Property FunctionArguments As Dictionary(Of String, String)
    Public Property DeepSeekDSMLLeak As Boolean

    Public Overrides Function ToString() As String
        Return $"[&{Id}] {FunctionName}({FunctionArguments.GetJson});"
    End Function
End Class

''' <summary>
''' 统一的请求参数
''' </summary>
Public Class ChatRequestOptions
    Public Property Model As String
    Public Property Messages As List(Of ChatMessage)
    ''' <summary>
    ''' 现有的工具定义类
    ''' </summary>
    ''' <returns></returns>
    Public Property Tools As List(Of FunctionTool)
    Public Property Temperature As Double?
    Public Property MaxTokens As Integer?
End Class

''' <summary>
''' 统一的响应结果（包含流式中间状态和最终状态）
''' </summary>
Public Class ChatResponseChunk
    Public Property IsDone As Boolean
    ''' <summary>
    ''' 流式增量文本
    ''' </summary>
    ''' <returns></returns>
    Public Property DeltaContent As String
    ''' <summary>
    ''' 流式思考(reasoning)增量，例如 Ollama/DeepSeek-R1 的 <think> 内容
    ''' </summary>
    ''' <returns></returns>
    Public Property ThinkContent As String
    ''' <summary>
    ''' 如果本轮触发了工具
    ''' </summary>
    ''' <returns></returns>
    Public Property ToolCalls As List(Of ToolCallInfo)
End Class
