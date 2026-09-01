Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON.FunctionCall

''' <summary>
''' 统一的聊天消息模型
''' </summary>
Public Class ChatMessage

    ''' <summary>
    ''' system, user, assistant, tool
    ''' </summary>
    ''' <returns></returns>
    Public Property Role As String
    Public Property Content As String
    ''' <summary>
    ''' 仅当 Role=assistant 且触发工具时使用
    ''' </summary>
    ''' <returns></returns>
    Public Property ToolCalls As ToolCallInfo()
    ''' <summary>
    ''' 仅当 Role=tool 时使用
    ''' </summary>
    ''' <returns></returns>
    Public Property ToolCallId As String

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

    ''' <summary>
    ''' 是否在流式请求中向后端索取 token 用量统计（OpenAI 兼容接口对应 
    ''' <c>stream_options.include_usage</c>）。默认为 True；若后端不支持该扩展参数
    ''' （返回 400 等错误），可将其关闭以回退到原有的流式协议。
    ''' </summary>
    ''' <returns></returns>
    Public Property StreamUsage As Boolean = True
End Class

''' <summary>
''' 归一化的 token 用量统计
''' </summary>
''' <remarks>
''' 缓存命中相关的字段（<see cref="CacheHitTokens"/> / <see cref="CacheMissTokens"/>）
''' 仅在后端支持 KV 缓存统计时才有值，例如 DeepSeek 会在 usage 中返回
''' <c>prompt_cache_hit_tokens</c> 与 <c>prompt_cache_miss_tokens</c>；
''' Ollama 本地后端没有对应的概念，这两个字段保持为 <c>Nothing</c>。
''' </remarks>
Public Class ChatUsage

    ''' <summary>
    ''' 本次请求的输入 token 数（对应 usage.prompt_tokens）
    ''' </summary>
    ''' <returns></returns>
    Public Property PromptTokens As Long
    ''' <summary>
    ''' 本次请求的输出 token 数（对应 usage.completion_tokens）
    ''' </summary>
    ''' <returns></returns>
    Public Property CompletionTokens As Long
    ''' <summary>
    ''' 输入中被 KV 缓存命中的 token 数；后端不支持时为 <c>Nothing</c>
    ''' </summary>
    ''' <returns></returns>
    Public Property CacheHitTokens As Long?
    ''' <summary>
    ''' 输入中未命中 KV 缓存的 token 数；后端不支持时为 <c>Nothing</c>
    ''' </summary>
    ''' <returns></returns>
    Public Property CacheMissTokens As Long?

    ''' <summary>
    ''' 当前用量数据中是否包含可用的缓存命中统计
    ''' </summary>
    ''' <returns>命中与未命中 token 数均有值时返回 True</returns>
    Public ReadOnly Property HasCacheStats As Boolean
        Get
            Return CacheHitTokens.HasValue AndAlso CacheMissTokens.HasValue
        End Get
    End Property

    ''' <summary>
    ''' 本次请求的缓存命中率：hit / (hit + miss)，取值 0~1；无缓存统计或分母为 0 时返回 0
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property HitRate As Double
        Get
            Return CacheUsageMath.HitRate(CacheHitTokens, CacheMissTokens)
        End Get
    End Property

    ''' <summary>
    ''' 后端返回的原始 usage 数据对象，便于兼容未来新增的字段
    ''' </summary>
    ''' <returns></returns>
    Public Property Raw As Object

    Public Overrides Function ToString() As String
        If HasCacheStats Then
            Return $"prompt={PromptTokens}, completion={CompletionTokens}, cache hit={CacheHitTokens.Value}/{CacheHitTokens.Value + CacheMissTokens.Value} ({HitRate.ToString("P2")})"
        Else
            Return $"prompt={PromptTokens}, completion={CompletionTokens}, cache hit=n/a"
        End If
    End Function
End Class

''' <summary>
''' 单次请求的用量快照，用于会话级的用量明细列表
''' </summary>
Public Class ChatUsageRecord

    ''' <summary>
    ''' 该次请求的用量结算时间（本地时间）
    ''' </summary>
    ''' <returns></returns>
    Public Property TimeStamp As DateTime
    ''' <summary>
    ''' 该次请求所使用的模型名称
    ''' </summary>
    ''' <returns></returns>
    Public Property Model As String
    ''' <summary>
    ''' 输入 token 数
    ''' </summary>
    ''' <returns></returns>
    Public Property PromptTokens As Long
    ''' <summary>
    ''' 输出 token 数
    ''' </summary>
    ''' <returns></returns>
    Public Property CompletionTokens As Long
    ''' <summary>
    ''' 缓存命中的输入 token 数；后端不支持时为 0
    ''' </summary>
    ''' <returns></returns>
    Public Property CacheHitTokens As Long
    ''' <summary>
    ''' 缓存未命中的输入 token 数；后端不支持时为 0
    ''' </summary>
    ''' <returns></returns>
    Public Property CacheMissTokens As Long

    ''' <summary>
    ''' 本次请求的缓存命中率：hit / (hit + miss)，取值 0~1；分母为 0 时返回 0
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property HitRate As Double
        Get
            Return CacheUsageMath.HitRate(CacheHitTokens, CacheMissTokens)
        End Get
    End Property

    ''' <summary>
    ''' 当前快照是否包含有效的缓存命中统计
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property HasCacheStats As Boolean
        Get
            Return CacheHitTokens + CacheMissTokens > 0
        End Get
    End Property

    ''' <summary>
    ''' 基于一次请求的用量数据 <paramref name="usage"/> 构建快照
    ''' </summary>
    ''' <param name="usage">provider 归一化后的用量数据</param>
    ''' <param name="model">该次请求所使用的模型名称</param>
    ''' <returns></returns>
    Public Shared Function Create(usage As ChatUsage, Optional model As String = Nothing) As ChatUsageRecord
        Return New ChatUsageRecord With {
            .TimeStamp = DateTime.Now,
            .Model = model,
            .PromptTokens = usage.PromptTokens,
            .CompletionTokens = usage.CompletionTokens,
            .CacheHitTokens = If(usage.CacheHitTokens, 0L),
            .CacheMissTokens = If(usage.CacheMissTokens, 0L)
        }
    End Function

    Public Overrides Function ToString() As String
        If HasCacheStats Then
            Return $"[{TimeStamp:HH:mm:ss}] {Model}: prompt={PromptTokens}, completion={CompletionTokens}, cache hit={HitRate.ToString("P2")}"
        Else
            Return $"[{TimeStamp:HH:mm:ss}] {Model}: prompt={PromptTokens}, completion={CompletionTokens}"
        End If
    End Function
End Class

''' <summary>
''' 缓存命中率的计算辅助模块
''' </summary>
Friend Module CacheUsageMath

    ''' <summary>
    ''' 计算缓存命中率：hit / (hit + miss)，分母为 0 时返回 0
    ''' </summary>
    ''' <param name="hit">缓存命中的 token 数</param>
    ''' <param name="miss">未命中缓存的 token 数</param>
    ''' <returns>取值区间为 0~1 的命中率</returns>
    Public Function HitRate(hit As Long?, miss As Long?) As Double
        Dim h As Long = If(hit, 0L)
        Dim m As Long = If(miss, 0L)
        Dim total As Long = h + m

        If total <= 0 Then
            Return 0.0R
        Else
            Return h / total
        End If
    End Function
End Module

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
    ''' 流式思考(reasoning)增量，例如 Ollama/DeepSeek-R1 的 &lt;think> 内容
    ''' </summary>
    ''' <returns></returns>
    Public Property ThinkContent As String
    ''' <summary>
    ''' 如果本轮触发了工具
    ''' </summary>
    ''' <returns></returns>
    Public Property ToolCalls As List(Of ToolCallInfo)
    ''' <summary>
    ''' 本轮请求的 token 用量统计（含 KV 缓存命中信息）。
    ''' 流式场景下由 provider 统一挂载在 <see cref="IsDone"/> 为 True 的结束帧上，
    ''' 消费方只需处理结束帧即可拿到完整用量，无需关心后端是逐帧下发还是单独成帧。
    ''' </summary>
    ''' <returns>后端未返回用量数据时为 <c>Nothing</c></returns>
    Public Property Usage As ChatUsage
End Class
