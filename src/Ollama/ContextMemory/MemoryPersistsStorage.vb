Imports System.IO
Imports Microsoft.VisualBasic.Serialization.JSON
Imports ComponentModel.DataSourceModel.Repository

''' <summary>
''' 上下文记忆的持久化与全文模糊检索门面。
'''
''' 该模块承担两类职责：
''' 1. 将 <see cref="ChatContextMemory"/> 中的会话消息落盘为本地 JSON 文件，并能从文件加载恢复到上下文；
''' 2. 利用 GCModeller 的 <see cref="QGramFullText"/> 全文索引引擎，对"活跃上下文窗口之外"的记忆
'''    （即已被裁剪 / 压缩、超出 <see cref="ChatContextMemory"/> 当前队列容量的历史信息）建立 q-gram 索引，
'''    使 LLM 能够通过一组关键词对长期记忆进行模糊匹配召回。
'''
''' 设计要点：
''' - 持久化文件与内存索引保持同步：保存时写入文件并重建索引；加载时反序列化并重建索引。
''' - 活跃窗口始终以 <see cref="ChatContextMemory"/> 队列为准，本模块索引仅覆盖窗口外记忆，避免重复注入。
''' - 文件损坏或反序列化失败时安全回退为空记忆并保留异常日志，不中断会话。
''' </summary>
Public Class MemoryPersistsStorage
    Implements IDisposable

    ''' <summary>
    ''' 被持久化 / 索引所服务的上下文记忆对象。
    ''' </summary>
    ReadOnly _memory As ChatContextMemory

    ''' <summary>
    ''' 持久化文件路径（JSON 数组格式，元素为 <see cref="ChatMessage"/>）。
    ''' </summary>
    ReadOnly _filePath As String

    ''' <summary>
    ''' 全文模糊索引引擎：每条记忆文档加入一篇文本，支持基于关键词组的近似匹配召回。
    ''' 由于引擎内部字典为只读字段、未提供 Clear，故以可重新赋值的方式持有，便于清空重建。
    ''' </summary>
    Dim _index As QGramFullText

    ''' <summary>
    ''' 文档文本（消息拼接摘要）到原始 <see cref="ChatMessage"/> 的稳定映射，用于命中后回填完整消息。
    ''' key 为送入索引的文档文本，value 为对应的原始消息。
    ''' </summary>
    ReadOnly _documents As New Dictionary(Of String, ChatMessage)

    ''' <summary>全文索引 q-gram 长度，默认 3。</summary>
    Public Property Q As Integer = 3

    ''' <summary>模糊检索默认返回条数，默认 5。</summary>
    Public Property DefaultTop As Integer = 5

    ''' <summary>模糊检索相似度阈值（0~1），低于该值的结果被过滤，默认 0（不过滤）。</summary>
    Public Property SimilarityThreshold As Double = 0.0

    Private disposedValue As Boolean

    ''' <summary>
    ''' 构造一个持久化存储门面。
    ''' </summary>
    ''' <param name="memory">需要持久化与检索的上下文记忆对象。</param>
    ''' <param name="filePath">
    ''' 持久化文件路径（JSON）。若目录不存在将自动创建。为空或空白时仅启用内存索引、不进行落盘。
    ''' </param>
    Public Sub New(memory As ChatContextMemory, Optional filePath As String = Nothing)
        If memory Is Nothing Then
            Throw New ArgumentNullException(NameOf(memory), "ChatContextMemory 不能为空")
        End If

        _memory = memory
        _filePath = filePath
        _index = New QGramFullText(q:=Q)

        If Not String.IsNullOrEmpty(filePath) Then
            Call Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)))
        End If
    End Sub

    ''' <summary>
    ''' 取得当前持久化文件路径；若未配置落盘则返回空。
    ''' </summary>
    Public ReadOnly Property FilePath As String
        Get
            Return _filePath
        End Get
    End Property

    ''' <summary>
    ''' 将一条消息转换为可索引的文档文本（仅取其文本语义部分，剥离敏感/冗长的工具参数细节）。
    ''' </summary>
    Private Shared Function ToDocument(msg As ChatMessage) As String
        If msg Is Nothing Then
            Return String.Empty
        End If

        Dim role As String = If(msg.Role, "unknown")
        Dim content As String = If(msg.Content, String.Empty)

        If msg.ToolCalls IsNot Nothing AndAlso msg.ToolCalls.Length > 0 Then
            ' assistant 的工具调用：记录函数名，省略参数细节以避免索引噪声
            Dim calls As String = String.Join(", ", msg.ToolCalls.Select(Function(t) t.FunctionName))
            content = content & " [tool_call: " & calls & "]"
        End If

        Return role & ": " & content.Trim()
    End Function

    ''' <summary>
    ''' 将一条消息加入全文索引（以及文档映射表）。
    ''' </summary>
    Private Sub IndexMessage(msg As ChatMessage)
        Dim doc As String = ToDocument(msg)

        If String.IsNullOrEmpty(doc) Then
            Return
        End If

        Call _index.Add(doc)
        ' 若文档文本重复（相同内容出现多次），保留首次关联的原始消息即可
        If Not _documents.ContainsKey(doc) Then
            _documents(doc) = msg
        End If
    End Sub

    ''' <summary>
    ''' 清空全文索引与文档映射表。
    ''' 由于 <see cref="QGramFullText"/> 未提供 Clear 方法，此处重建一个新的空索引实例。
    ''' </summary>
    Public Sub ClearIndex()
        _index = New QGramFullText(q:=Q)
        _documents.Clear()
    End Sub

    ''' <summary>
    ''' 仅基于内存中当前上下文（<see cref="_memory"/>.ExportMessages）重建索引，不读写文件。
    ''' 适用于不配置落盘、仅需要"窗口外"记忆检索的场景。
    ''' </summary>
    Public Sub RebuildIndexFromMemory()
        Call ClearIndex()

        For Each msg In _memory.ExportMessages()
            Call IndexMessage(msg)
        Next
    End Sub

    ''' <summary>
    ''' 将当前上下文记忆保存到持久化文件，并同步重建全文索引。
    ''' 若未配置文件路径则返回 False（不落盘，但索引仍会基于当前上下文重建）。
    ''' 文件写入失败或序列化异常将被捕获并记录到控制台，不向上抛出以免中断会话。
    ''' </summary>
    Public Function Save() As Boolean
        Dim messages As List(Of ChatMessage) = _memory.ExportMessages()

        ' 保存即重建索引（保证索引与落盘内容一致）
        Call ClearIndex()
        For Each msg In messages
            Call IndexMessage(msg)
        Next

        If String.IsNullOrEmpty(_filePath) Then
            Return True
        End If

        Try
            Dim json As String = messages.GetJson(Of List(Of ChatMessage))(simpleDict:=True)
            Call File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8)
            Console.WriteLine($"[MemoryPersistsStorage] 已保存 {messages.Count} 条消息到 {_filePath}")
            Return True
        Catch ex As Exception
            Call Console.Error.WriteLine($"[MemoryPersistsStorage] 保存失败: {ex.Message}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 从持久化文件加载消息并恢复到上下文记忆，同时重建全文索引。
    ''' 文件不存在时安全返回空列表且不改变现有索引。
    ''' 文件损坏或反序列化失败时安全回退为空记忆、保留异常日志，不抛出异常。
    ''' </summary>
    Public Function Load() As List(Of ChatMessage)
        If String.IsNullOrEmpty(_filePath) OrElse Not File.Exists(_filePath) Then
            Return New List(Of ChatMessage)
        End If

        Try
            Dim messages As List(Of ChatMessage) = LoadJsonFile(Of List(Of ChatMessage))(file:=_filePath, simpleDict:=True)

            If messages Is Nothing Then
                messages = New List(Of ChatMessage)
            End If

            ' 恢复到上下文（LoadMessages 按原样重建队列与 token 估算，不触发裁剪）
            Call _memory.LoadMessages(messages)

            ' 重建全文索引
            Call ClearIndex()
            For Each msg In messages
                Call IndexMessage(msg)
            Next

            Console.WriteLine($"[MemoryPersistsStorage] 已从 {_filePath} 加载 {messages.Count} 条消息并重建索引")
            Return messages
        Catch ex As Exception
            Call Console.Error.WriteLine($"[MemoryPersistsStorage] 加载失败，已回退为空记忆: {ex.Message}")
            Call _memory.LoadMessages(Nothing)
            Call ClearIndex()
            Return New List(Of ChatMessage)
        End Try
    End Function

    ''' <summary>
    ''' 基于一组关键词对窗口外记忆进行全文模糊检索，返回命中的原始文档与相似度。
    ''' 检索结果按相似度降序排列。
    ''' </summary>
    ''' <param name="keywords">关键词组（如 LLM 提取的查询意图词）。</param>
    ''' <param name="top">返回的最大条数，默认取 <see cref="DefaultTop"/>。</param>
    ''' <returns>
    ''' 命中的 <see cref="FindResult"/> 序列（text 为被索引的文档文本，similarity 为相似度，index 为内部文档序号）。
    ''' 若关键词为空或未建索引则返回空序列。
    ''' </returns>
    Public Iterator Function Search(keywords As IEnumerable(Of String), Optional top As Integer = -1) As IEnumerable(Of FindResult)
        If keywords Is Nothing Then
            Return
        End If

        Dim words As String() = keywords _
            .Where(Function(w) Not String.IsNullOrWhiteSpace(w)) _
            .Select(Function(w) w.Trim()) _
            .ToArray()

        If words.Length = 0 Then
            Return
        End If

        Dim n As Integer = If(top <= 0, DefaultTop, top)

        For Each hit In _index.Search(queryWords:=words, top:=n, threshold:=SimilarityThreshold)
            If hit IsNot Nothing Then
                Yield hit
            End If
        Next
    End Function

    ''' <summary>
    ''' 基于一组关键词召回命中的原始 <see cref="ChatMessage"/>，供 LLM 作为补充上下文注入。
    ''' 该方法在 <see cref="Search"/> 基础上用文档文本回查映射表，得到完整消息对象。
    ''' </summary>
    ''' <param name="keywords">关键词组。</param>
    ''' <param name="top">返回的最大条数，默认取 <see cref="DefaultTop"/>。</param>
    ''' <returns>按相似度降序排列的命中消息（去重，保留首次命中的原始消息）。</returns>
    Public Iterator Function RecallMessages(keywords As IEnumerable(Of String), Optional top As Integer = -1) As IEnumerable(Of ChatMessage)
        Dim seen As New HashSet(Of ChatMessage)

        For Each hit In Search(keywords, top)
            Dim msg As ChatMessage = Nothing

            If _documents.TryGetValue(hit.text, msg) AndAlso msg IsNot Nothing AndAlso seen.Add(msg) Then
                Yield msg
            End If
        Next
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' 当前索引与映射表均为托管内存结构，无需释放非托管资源
                Call ClearIndex()
            End If
            disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Call Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub
End Class
