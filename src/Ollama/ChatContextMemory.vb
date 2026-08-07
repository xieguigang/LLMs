Imports System.IO
Imports Microsoft.VisualBasic.Serialization.JSON

''' <summary>
''' 上下文管理模式：Trim = 直接丢弃旧消息；Compress = 通过 LLM 将旧消息压缩为摘要。
''' </summary>
Public Enum ContextManagementMode
    ''' <summary>直接丢弃旧消息（默认，保持向后兼容）</summary>
    Trim
    ''' <summary>将旧消息压缩为摘要文本，节省 Token 占用</summary>
    Compress
End Enum

''' <summary>
''' 对话记忆上下文：将多轮 ChatMessage 维护为先进先出队列，并以近似算法估算 token 占用，
''' 当累计 token 超过 <see cref="MaxTokens"/> 时自动从最旧消息开始裁剪或压缩，同时保证
''' assistant(tool_calls) 与其紧跟的 tool 结果消息成组保留，避免出现孤立的 tool 消息导致 API 报错。
''' </summary>
''' <remarks>
''' token 采用启发式估算（字符数/4 与 词数*1.3 取较大值 + 每条消息开销），属"软上限"，
''' 与真实 BPE 分词存在偏差，仅用于历史裁剪，不影响实际发送给模型的请求。
''' </remarks>
Public Class ChatContextMemory : Implements IEnumerable(Of ChatMessage)
    Implements IDisposable

    ''' <summary>内部消息队列（先进先出）</summary>
    ReadOnly _queue As New Queue(Of ChatMessage)
    ReadOnly _log As TextWriter

    ''' <summary>当前累计 token 估算（长整型以避免大上下文溢出）</summary>
    Dim _estimatedTokens As Long
    Private disposedValue As Boolean

    ''' <summary>
    ''' 最大上下文 token 数量上限，默认 1,000,000（1M）。超过后从历史最旧端裁剪。
    ''' </summary>
    Public Property MaxTokens As Long = 1000000

    ''' <summary>
    ''' 上下文管理模式：<see cref="ContextManagementMode.Trim"/>（直接丢弃）或 <see cref="ContextManagementMode.Compress"/>（LLM 摘要压缩）。
    ''' 默认为 Trim 以保持向后兼容。
    ''' </summary>
    Public Property Mode As ContextManagementMode = ContextManagementMode.Trim

    ''' <summary>
    ''' 压缩委托：接收待压缩的消息组列表，异步返回摘要文本。
    ''' 由外部（如 LLMClient）注入实际的 LLM 总结能力。未配置时 Compress 模式将自动回退为 Trim。
    ''' </summary>
    Public Property CompressionDelegate As Func(Of List(Of ChatMessage), Task(Of String))

    ''' <summary>
    ''' 压缩触发阈值（相对于 MaxTokens 的比例），默认 0.85（即达到 MaxTokens 的 85% 时触发压缩），
    ''' 预留缓冲空间避免在发送请求前紧急压缩。
    ''' </summary>
    Public Property CompressionThreshold As Double = 0.85

    ''' <summary>当前累计 token 估算值</summary>
    Public ReadOnly Property EstimatedTokens As Long
        Get
            Return _estimatedTokens
        End Get
    End Property

    ''' <summary>当前记忆中的消息条数</summary>
    Public ReadOnly Property Count As Integer
        Get
            Return _queue.Count
        End Get
    End Property

    Sub New(Optional logfile As String = Nothing)
        _log = New StreamWriter(GetLogFile(logfile), append:=False)
    End Sub

    Private Shared Function GetLogFile(logfile As String) As String
        If logfile.StringEmpty(, True) Then
            logfile = $"{IO.Path.GetTempPath()}/ollama-log_{Guid.NewGuid().ToString("N")}.jsonl"
        End If

        Call logfile.ParentPath.MakeDir

        Return logfile
    End Function

    ''' <summary>
    ''' 异步入队一条消息：累加 token 估算并根据 <see cref="Mode"/> 触发裁剪或压缩。
    ''' </summary>
    Public Async Function EnqueueAsync(msg As ChatMessage) As Task
        If msg Is Nothing Then
            Return
        ElseIf _log IsNot Nothing Then
            Call _log.WriteLine(msg.GetJson(simpleDict:=True))
        End If

        Dim req_size As Long = EstimateTokens(msg)

        _queue.Enqueue(msg)
        _estimatedTokens += req_size

        ' 根据模式选择裁剪或压缩
        If Mode = ContextManagementMode.Compress Then
            Dim triggerTokens As Long = CLng(MaxTokens * CompressionThreshold)
            If _estimatedTokens > triggerTokens Then
                Await CompressAsync()
            End If
        Else
            Call Trim()
        End If

        Call Console.WriteLine()
        Call Console.WriteLine(Me.ToString)
        Call Console.WriteLine($"Current Request Tokens: {StringFormats.Lanudry(req_size)}")
        Call Console.WriteLine()
    End Function

    ''' <summary>
    ''' 清空记忆上下文。
    ''' </summary>
    Public Sub Clear()
        Call _queue.Clear()
        _estimatedTokens = 0
    End Sub

    ''' <summary>
    ''' 将内部队列快照为列表（供构造请求消息时复制）。
    ''' </summary>
    Private Function Snapshot() As List(Of ChatMessage)
        Dim list As New List(Of ChatMessage)
        For Each m In _queue
            list.Add(m)
        Next
        Return list
    End Function

    ''' <summary>
    ''' 将消息列表切分为"组"：普通消息自成一组；assistant(含 tool_calls) 吸收其后所有紧邻的 tool 消息。
    ''' </summary>
    ''' <param name="list">消息列表</param>
    ''' <returns>分组后的消息组列表</returns>
    Private Shared Function GroupMessages(list As List(Of ChatMessage)) As List(Of List(Of ChatMessage))
        Dim groups As New List(Of List(Of ChatMessage))
        Dim i As Integer = 0

        While i < list.Count
            Dim startIdx = i

            If list(i).ToolCalls IsNot Nothing AndAlso list(i).ToolCalls.Count > 0 Then
                i += 1
                While i < list.Count AndAlso list(i).Role = "tool"
                    i += 1
                End While
            Else
                i += 1
            End If

            groups.Add(list.GetRange(startIdx, i - startIdx))
        End While

        Return groups
    End Function

    ''' <summary>
    ''' 使用剩余分组重建内部队列并重新精确计算 token 总量。
    ''' </summary>
    Private Sub RebuildQueueFromGroups(groups As List(Of List(Of ChatMessage)), fromIdx As Integer)
        _queue.Clear()
        _estimatedTokens = 0
        For g = fromIdx To groups.Count - 1
            For Each m In groups(g)
                _queue.Enqueue(m)
                _estimatedTokens += EstimateTokens(m)
            Next
        Next
    End Sub

    ''' <summary>
    ''' 按 token 上限裁剪：把 assistant(tool_calls) 与紧随其后的连续 tool 消息视为一个"组"，
    ''' 超过预算时从最旧组开始整组弹出，至少保留最新一组。
    ''' </summary>
    Private Sub Trim()
        If _estimatedTokens <= MaxTokens Then Return

        Dim list = Snapshot()
        Dim groups = GroupMessages(list)

        ' 从最旧组开始移除，直到回到预算内；但至少保留最新一组
        Dim removeGroups As Integer = 0
        While _estimatedTokens > MaxTokens AndAlso (groups.Count - removeGroups) > 1
            For Each m In groups(removeGroups)
                _estimatedTokens -= EstimateTokens(m)
            Next
            removeGroups += 1
        End While

        ' 用剩余分组重建队列
        Call RebuildQueueFromGroups(groups, removeGroups)
    End Sub

    ''' <summary>
    ''' 异步上下文压缩：弹出最旧的消息组，通过 <see cref="CompressionDelegate"/> 委托调用 LLM 生成摘要，
    ''' 用摘要 system 消息替代原始消息以节省 Token。压缩失败时自动回退为 Trim。
    ''' </summary>
    Private Async Function CompressAsync() As Task
        ' 若未配置压缩委托，回退为 Trim
        If CompressionDelegate Is Nothing Then
            Call Trim()
            Return
        End If

        Dim allRemovedMessages As New List(Of ChatMessage)

        Try
            Dim list = Snapshot()
            Dim groups = GroupMessages(list)

            ' 从最旧组开始移除，直到回到预算内；至少保留最新一组
            Dim removeGroups As Integer = 0
            While _estimatedTokens > MaxTokens AndAlso (groups.Count - removeGroups) > 1
                For Each m In groups(removeGroups)
                    allRemovedMessages.Add(m)
                    _estimatedTokens -= EstimateTokens(m)
                Next
                removeGroups += 1
            End While

            ' 如果有消息被移除，调用 LLM 生成摘要
            If allRemovedMessages.Count > 0 Then
                Dim summaryText As String = Await CompressionDelegate(allRemovedMessages)

                If Not String.IsNullOrEmpty(summaryText) Then
                    ' 创建摘要 system 消息并插入队列头部
                    Dim summaryMsg As New ChatMessage With {
                        .Role = "system",
                        .Content = $"[Conversation Context Summary]{vbCrLf}{summaryText}"
                    }

                    _queue.Clear()
                    _estimatedTokens = 0
                    _queue.Enqueue(summaryMsg)
                    _estimatedTokens += EstimateTokens(summaryMsg)

                    For g = removeGroups To groups.Count - 1
                        For Each m In groups(g)
                            _queue.Enqueue(m)
                            _estimatedTokens += EstimateTokens(m)
                        Next
                    Next
                Else
                    ' 摘要为空，直接重建队列
                    Call RebuildQueueFromGroups(groups, removeGroups)
                End If
            End If
        Catch ex As Exception
            ' 压缩失败，回退为 Trim
            Call Trim()
        End Try
    End Function

    ''' <summary>
    ''' 粗略估算单条消息的 token 数量（启发式，非精确 BPE）。
    ''' </summary>
    Public Shared Function EstimateTokens(msg As ChatMessage) As Long
        Dim tokens As Long = 4 ' 每条消息的基础开销

        If Not String.IsNullOrEmpty(msg.Content) Then
            tokens += EstimateTextTokens(msg.Content)
        End If

        If msg.ToolCalls IsNot Nothing Then
            For Each tc In msg.ToolCalls
                tokens += 3 ' 每个工具调用开销
                If Not String.IsNullOrEmpty(tc.FunctionName) Then
                    tokens += EstimateTextTokens(tc.FunctionName)
                End If
                If tc.FunctionArguments IsNot Nothing Then
                    For Each kvp In tc.FunctionArguments
                        tokens += EstimateTextTokens(kvp.Key)
                        tokens += EstimateTextTokens(If(kvp.Value, ""))
                    Next
                End If
            Next
        End If

        If Not String.IsNullOrEmpty(msg.ToolCallId) Then
            tokens += EstimateTextTokens(msg.ToolCallId)
        End If

        Return tokens
    End Function

    Public Overrides Function ToString() As String
        Return $"LLM Context Token Size: {StringFormats.Lanudry(EstimatedTokens)} / {StringFormats.Lanudry(MaxTokens)}"
    End Function

    ''' <summary>
    ''' 文本 token 启发式估算：取「字符数/4」与「词数*1.3」的较大值（向上取整）。
    ''' 改进的 LLM Token 启发式估算：兼容中英文，无内存分配
    ''' </summary>
    Public Shared Function EstimateTextTokens(text As String) As Long
        If String.IsNullOrEmpty(text) Then Return 0

        Dim span As String = text
        Dim wordCount As Long = 0
        Dim cjkCount As Long = 0
        Dim inWord As Boolean = False

        For i As Integer = 0 To span.Length - 1
            Dim c As Char = span(i)

            ' 判断是否为中日韩字符 (基本汉字范围: 0x4E00 - 0x9FFF)
            If c >= ChrW(&H4E00) AndAlso c <= ChrW(&H9FFF) Then
                cjkCount += 1
                inWord = False ' 遇到中文，重置英文单词状态
            ElseIf Char.IsWhiteSpace(c) Then
                inWord = False
            ElseIf Not inWord Then
                wordCount += 1
                inWord = True
            End If
        Next

        ' 估算逻辑：
        ' 1. 中文字符按 1.5 个 token 估算
        ' 2. 英文等按空格分割的词数 * 1.3
        ' 3. 整体字符数 / 4 向上取整作为兜底
        Dim estimatedTokens As Double = (cjkCount * 1.5) + (wordCount * 1.3)
        Dim lenTokens As Double = Math.Ceiling(text.Length / 4.0)

        Return CLng(Math.Max(estimatedTokens, lenTokens))
    End Function

    ''' <summary>
    ''' 实现 IEnumerable(Of ChatMessage)，使 <c>New List(Of ChatMessage)(memory)</c> 等用法保持兼容。
    ''' </summary>
    Public Function GetEnumerator() As IEnumerator(Of ChatMessage) Implements IEnumerable(Of ChatMessage).GetEnumerator
        Return _queue.GetEnumerator()
    End Function

    Private Function GetEnumeratorNonGeneric() As System.Collections.IEnumerator Implements System.Collections.IEnumerable.GetEnumerator
        Return _queue.GetEnumerator()
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' TODO: dispose managed state (managed objects)
                If _log IsNot Nothing Then
                    Call _log.Flush()
                    Call _log.Dispose()
                End If
            End If

            ' TODO: free unmanaged resources (unmanaged objects) and override finalizer
            ' TODO: set large fields to null
            disposedValue = True
        End If
    End Sub

    ' ' TODO: override finalizer only if 'Dispose(disposing As Boolean)' has code to free unmanaged resources
    ' Protected Overrides Sub Finalize()
    '     ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class
