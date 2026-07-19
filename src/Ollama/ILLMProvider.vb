Imports System.Threading
Imports Ollama.JSON.FunctionCall

''' <summary>
''' 大语言模型 API 提供者接口
''' </summary>
Public Interface ILLMProvider
    ''' <summary>
    ''' 获取 API 端点
    ''' </summary>
    ReadOnly Property ApiEndpoint As String

    ''' <summary>
    ''' 异步流式请求
    ''' </summary>
    ''' <param name="options">统一的请求参数</param>
    ''' <param name="cancellationToken">取消令牌</param>
    ''' <returns> yield 返回一个个 Chunk </returns>
    Function StreamChatAsync(options As ChatRequestOptions, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ChatResponseChunk))

    ' 未来可扩展：非流式请求、获取模型列表等
End Interface

''' <summary>
''' 统一的聊天消息模型
''' </summary>
Public Class ChatMessage
    Public Property Role As String ' system, user, assistant, tool
    Public Property Content As String
    Public Property ToolCalls As List(Of ToolCallInfo) ' 仅当 Role=assistant 且触发工具时使用
    Public Property ToolCallId As String ' 仅当 Role=tool 时使用
End Class

''' <summary>
''' 统一的工具调用信息
''' </summary>
Public Class ToolCallInfo
    Public Property Id As String
    Public Property FunctionName As String
    Public Property FunctionArguments As Dictionary(Of String, String) ' JSON 字符串
End Class

''' <summary>
''' 统一的请求参数
''' </summary>
Public Class ChatRequestOptions
    Public Property Model As String
    Public Property Messages As List(Of ChatMessage)
    Public Property Tools As List(Of FunctionTool) ' 你现有的工具定义类
    Public Property Temperature As Double?
    Public Property MaxTokens As Integer?
End Class

''' <summary>
''' 统一的响应结果（包含流式中间状态和最终状态）
''' </summary>
Public Class ChatResponseChunk
    Public Property IsDone As Boolean
    Public Property DeltaContent As String ' 流式增量文本
    Public Property ThinkContent As String ' 流式思考(reasoning)增量，例如 Ollama/DeepSeek-R1 的 <think> 内容
    Public Property ToolCalls As List(Of ToolCallInfo) ' 如果本轮触发了工具
End Class
