Imports System.Threading

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
