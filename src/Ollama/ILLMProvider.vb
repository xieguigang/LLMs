Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
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

    ''' <summary>
    ''' 获取指定模型的归一化信息（由各后端 Provider 实现差异）
    ''' </summary>
    ''' <param name="model">模型名称/标识</param>
    ''' <param name="timeout">请求超时（秒）</param>
    ''' <param name="verbose">是否返回详细信息（Ollama 有效，OpenAI 忽略）</param>
    ''' <returns>归一化后的 <see cref="ModelInfo"/></returns>
    Function GetModelInformation(model As String, timeout As Double, verbose As Boolean) As Task(Of ModelInfo)
End Interface

''' <summary>
''' 归一化后的模型信息：将 Ollama / OpenAI 等不同后端的模型信息映射为统一结构，
''' 调用方无需关心底层后端；同时保留 <see cref="Raw"/> 原始响应以供深入解析。
''' </summary>
Public Class ModelInfo
    ''' <summary>模型标识（Ollama: model；OpenAI: id）</summary>
    Public Property Id As String
    ''' <summary>后端来源："ollama" / "openai"</summary>
    Public Property Provider As String
    ''' <summary>模型族（Ollama: details.family）</summary>
    Public Property Family As String
    ''' <summary>参数规模（Ollama: details.parameter_size）</summary>
    Public Property ParameterSize As String
    ''' <summary>量化等级（Ollama: details.quantization_level）</summary>
    Public Property QuantizationLevel As String
    ''' <summary>格式（Ollama: details.format）</summary>
    Public Property Format As String
    ''' <summary>拥有者（OpenAI: owned_by）</summary>
    Public Property OwnedBy As String
    ''' <summary>创建/修改时间戳（Unix 秒；OpenAI: created，Ollama: modified_at 转换）</summary>
    Public Property CreatedAt As Long?
    ''' <summary>后端原始响应，供调用方深入解析</summary>
    Public Property Raw As JsonObject
End Class

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
