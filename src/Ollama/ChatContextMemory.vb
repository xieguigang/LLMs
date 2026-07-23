Imports System.IO
Imports Microsoft.VisualBasic.Serialization.JSON

''' <summary>
''' 对话记忆上下文：将多轮 ChatMessage 维护为先进先出队列，并以近似算法估算 token 占用，
''' 当累计 token 超过 <see cref="MaxTokens"/> 时自动从最旧消息开始裁剪，同时保证
''' assistant(tool_calls) 与其紧跟的 tool 结果消息成组保留，避免出现孤立的 tool 消息导致 API 报错。
''' </summary>
''' <remarks>
''' token 采用启发式估算（字符数/4 与 词数*1.3 取较大值 + 每条消息开销），属“软上限”，
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
    ''' 入队一条消息：累加 token 估算并立即触发裁剪。
    ''' </summary>
    Public Sub Enqueue(msg As ChatMessage)
        If msg Is Nothing Then
            Return
        ElseIf _log IsNot Nothing Then
            Call _log.WriteLine(msg.GetJson(simpleDict:=True))
        End If

        _queue.Enqueue(msg)
        _estimatedTokens += EstimateTokens(msg)

        Call Trim()
        Call Console.WriteLine()
        Call Console.WriteLine(Me.ToString)
        Call Console.WriteLine()
    End Sub

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
    ''' 按 token 上限裁剪：把 assistant(tool_calls) 与紧随其后的连续 tool 消息视为一个“组”，
    ''' 超过预算时从最旧组开始整组弹出，至少保留最新一组。
    ''' </summary>
    Private Sub Trim()
        If _estimatedTokens <= MaxTokens Then Return

        Dim list = Snapshot()
        Dim groups As New List(Of List(Of ChatMessage))
        Dim i As Integer = 0

        ' 将消息切分为组：普通消息自成一组；assistant(含 tool_calls) 吸收其后所有紧邻的 tool 消息
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

        ' 从最旧组开始移除，直到回到预算内；但至少保留最新一组
        Dim removeGroups As Integer = 0
        While _estimatedTokens > MaxTokens AndAlso (groups.Count - removeGroups) > 1
            For Each m In groups(removeGroups)
                _estimatedTokens -= EstimateTokens(m)
            Next
            removeGroups += 1
        End While

        ' 用剩余分组重建队列，并重新精确计算 token 总量
        _queue.Clear()
        _estimatedTokens = 0
        For g = removeGroups To groups.Count - 1
            For Each m In groups(g)
                _queue.Enqueue(m)
                _estimatedTokens += EstimateTokens(m)
            Next
        Next
    End Sub

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
        Return $"LLM Context: {StringFormats.Lanudry(EstimatedTokens)} / {StringFormats.Lanudry(MaxTokens)}"
    End Function

    ''' <summary>
    ''' 文本 token 启发式估算：取「字符数/4」与「词数*1.3」的较大值（向上取整）。
    ''' </summary>
    Public Shared Function EstimateTextTokens(text As String) As Long
        If String.IsNullOrEmpty(text) Then Return 0

        Dim lenTokens As Long = text.Length \ 4
        Dim words = text.Split(New Char() {" "c, vbTab, vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
        Dim wordTokens As Long = CLng(Math.Ceiling(words.Length * 1.3))

        Return Math.Max(lenTokens, wordTokens)
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
