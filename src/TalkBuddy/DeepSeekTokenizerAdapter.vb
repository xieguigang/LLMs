' ---------------------------------------------------------------------------
' DeepSeekTokenizerAdapter —— 把 HuggingFace 分词器接到 LLM 算法层
'
' 它做三件事：
'
'   1. 加载 DeepSeek 的 tokenizer.json（全量约 10 万词表）并暴露
'      <see cref="ITextCodec"/>，供模型与 Agent 循环使用；
'   2. 解析并缓存全部<b>保留 token</b>（角色标记与工具调用标记）的 id
'      —— 这就是"特殊 token 不是魔法，而是被加进词表的保留 token"那句话的落地；
'   3. 构建约束解码所需的词表文本视图（<see cref="TokenizerVocabulary"/>）。
'
' 第 3 点是一次性的重活：需要把 10 万个 token 逐个单独解码，才能知道每个 token
' "实际贡献了什么文本"。注意不能直接用 IdToToken —— 那给出的是 byte-level 编码后的
' 原始词表项（例如 "▁weather"），而真正写进上下文的是解码（ByteLevel 逆映射）之后的文本。
'
' 实例加载完成后是只读的，应当在整个 demo 里复用同一份。
' ---------------------------------------------------------------------------

Imports System.Collections.Generic
Imports System.Diagnostics
Imports Microsoft.VisualBasic.MachineLearning.LLM
Imports ChineseTokenizer.HuggingFace

Public Class DeepSeekTokenizerAdapter
    Implements ITextCodec

    Private ReadOnly _tokenizer As HuggingFaceTokenizer
    Private ReadOnly _unkId As Integer
    Private ReadOnly _vocabulary As TokenizerVocabulary
    Private ReadOnly _specialIds As New Dictionary(Of String, Integer)()

    ''' <summary>底层分词器（需要做 tokenize 展示时可以取用）。</summary>
    Public ReadOnly Property Tokenizer As HuggingFaceTokenizer
        Get
            Return _tokenizer
        End Get
    End Property

    ''' <summary>分词器原始词表大小。</summary>
    Public ReadOnly Property RawVocabSize As Integer

    ''' <summary>本适配器实际使用的词表大小（受 <c>VocabularyLimit</c> 约束）。</summary>
    Public ReadOnly Property VocabSize As Integer

    ''' <summary>约束解码使用的词表文本视图。</summary>
    Public ReadOnly Property Vocabulary As TokenizerVocabulary Implements ITextCodec.Vocabulary

    ''' <summary>保留 token 的字面形式 → token id。</summary>
    Public ReadOnly Property SpecialTokenIds As IReadOnlyDictionary(Of String, Integer)
        Get
            Return _specialIds
        End Get
    End Property

    ''' <summary>构建词表所花的时间（毫秒），用于说明这一步是一次性成本。</summary>
    Public ReadOnly Property VocabularyBuildMilliseconds As Double

    ''' <summary>词表中"无法用于约束解码"的 token 个数（保留 token 与被截断的部分）。</summary>
    Public ReadOnly Property UnusableTokens As Integer

    Private Sub New(tokenizer As HuggingFaceTokenizer, limit As Integer, verbose As Boolean)
        _tokenizer = tokenizer
        RawVocabSize = tokenizer.VocabSize

        If limit > 0 AndAlso limit < RawVocabSize Then
            VocabSize = limit
        Else
            VocabSize = RawVocabSize
        End If

        ' 词表被截断时，超界 id 统一落到句尾标记上（仅用于冒烟档，语义会变差）
        Dim eos As Integer? = tokenizer.TokenToId(ToolCallProtocol.EndOfSentenceMarker)

        _unkId = If(VocabSize < RawVocabSize, If(eos.HasValue, eos.Value, 0), -1)

        Call ResolveSpecialTokens()

        Dim elapsed As Double = 0.0

        _vocabulary = BuildVocabulary(VocabSize, elapsed)
        VocabularyBuildMilliseconds = elapsed
        UnusableTokens = VocabSize - _vocabulary.UsableTokens - _vocabulary.SpecialTokens

        If verbose Then
            Call Console.WriteLine($"[tokenizer] vocab={VocabSize:N0} (raw {RawVocabSize:N0}), " &
                                   $"model={tokenizer.ModelType}, special_tokens={_specialIds.Count}")
            Call Console.WriteLine($"[tokenizer] constrained-decoding view built in {elapsed:F0} ms: " &
                                   $"{_vocabulary.UsableTokens:N0} usable tokens over {_vocabulary.FirstCharacters.Length:N0} first-characters")
        End If
    End Sub

    ''' <summary>
    ''' 从模型目录加载分词器。
    ''' </summary>
    ''' <param name="directory">包含 tokenizer.json 的目录</param>
    ''' <param name="limit">词表上限；&lt;= 0 或大于词表本身表示使用全量词表</param>
    ''' <param name="verbose">是否打印加载摘要</param>
    Public Shared Function Load(directory As String, Optional limit As Integer = 0,
                                Optional verbose As Boolean = True) As DeepSeekTokenizerAdapter

        If verbose Then
            Call Console.WriteLine($"[tokenizer] loading from {directory} ...")
        End If

        Dim tokenizer = HuggingFaceTokenizer.FromPretrained(directory, verbose:=False)

        Return New DeepSeekTokenizerAdapter(tokenizer, limit, verbose)
    End Function

#Region "保留 token"

    ''' <summary>
    ''' 解析协议里用到的全部保留 token 的 id。
    ''' </summary>
    ''' <remarks>
    ''' 任何一个缺失都说明分词器与协议不匹配（例如换成了别的模型），
    ''' 此时必须立刻失败而不是静默降级 —— 否则后续生成的文本会缺失信号灯，
    ''' 表现为"模型永远不调用工具"这种极难排查的现象。
    ''' </remarks>
    Private Sub ResolveSpecialTokens()
        Dim markers = ToolCallProtocol.AllMarkers _
            .Concat(New String() {ToolCallProtocol.BeginOfSentenceMarker}).Distinct()

        For Each marker In markers
            Dim id As Integer? = _tokenizer.TokenToId(marker)

            If Not id.HasValue Then
                Throw New InvalidOperationException($"分词器词表中缺少协议所需的保留 token：'{marker}'")
            End If

            If id.Value >= VocabSize Then
                Throw New InvalidOperationException(
                    $"保留 token '{marker}' 的 id={id.Value} 超出 VocabularyLimit={VocabSize}；" &
                    "请把 VocabularyLimit 调大，或设为 0 使用全量词表")
            End If

            _specialIds(marker) = id.Value
        Next
    End Sub

#End Region

#Region "词表文本视图"

    ''' <summary>
    ''' 逐个解码词表中的每一个 token，得到"该 token 贡献的文本"，并建立首字符分桶。
    ''' </summary>
    ''' <remarks>
    ''' 判定"是否为保留 token"的方式是：跳过特殊 token 以后什么都不剩。
    ''' 这样不需要分词器额外暴露 special token 表。
    ''' </remarks>
    Private Function BuildVocabulary(limit As Integer, ByRef elapsedMs As Double) As TokenizerVocabulary
        Dim watch = Diagnostics.Stopwatch.StartNew()

        Dim texts(limit - 1) As String
        Dim special(limit - 1) As Boolean

        For id As Integer = 0 To limit - 1
            Dim asPlain = _tokenizer.Decode(New Integer() {id}, skipSpecialTokens:=False)

            texts(id) = asPlain

            If asPlain.Length > 0 Then
                special(id) = _tokenizer.Decode(New Integer() {id}, skipSpecialTokens:=True).Length = 0
            End If
        Next

        watch.Stop()
        elapsedMs = watch.Elapsed.TotalMilliseconds

        Return New TokenizerVocabulary(texts, special)
    End Function

#End Region

#Region "ITextCodec"

    ''' <summary>
    ''' 文本 → token。
    ''' </summary>
    ''' <remarks>
    ''' 刻意<b>不</b>自动追加 BOS / EOS：prompt 中的角色标记与工具调用标记都是显式写出来的，
    ''' 自动追加会破坏协议。
    ''' </remarks>
    Public Function Encode(text As String) As Integer() Implements ITextCodec.Encode
        If String.IsNullOrEmpty(text) Then Return New Integer() {}

        Dim ids = _tokenizer.EncodeToIds(text, addSpecialTokens:=False)

        If _unkId < 0 Then Return ids

        ' 词表被截断时做一次 remap
        Dim mapped(ids.Length - 1) As Integer

        For i As Integer = 0 To ids.Length - 1
            mapped(i) = If(ids(i) < VocabSize, ids(i), _unkId)
        Next

        Return mapped
    End Function

    ''' <summary>token → 文本。</summary>
    Public Function Decode(ids As IEnumerable(Of Integer)) As String Implements ITextCodec.Decode
        Return _tokenizer.Decode(ids, skipSpecialTokens:=False)
    End Function

    ''' <summary>查询标记对应的 token id；不存在时返回 -1。</summary>
    Public Function TokenIdOf(marker As String) As Integer Implements ITextCodec.TokenIdOf
        Dim id As Integer = -1

        If _specialIds.TryGetValue(marker, id) Then Return id

        Return -1
    End Function

#End Region

#Region "便捷访问"

    ''' <summary>单个 token 贡献的文本（用于逐 token 打印协议）。</summary>
    Public Function TokenTextOf(id As Integer) As String
        If id < 0 OrElse id >= VocabSize Then Return "<out-of-range>"

        Return _tokenizer.Decode(New Integer() {id}, skipSpecialTokens:=False)
    End Function

    ''' <summary>把 token 序列渲染成 <c>[id]文本</c> 串联的形式，便于观察协议结构。</summary>
    Public Function Describe(ids As IEnumerable(Of Integer)) As String
        Dim text As New Text.StringBuilder()

        For Each id In ids
            Call text.Append($"[{id}]{TokenTextOf(id)}")
        Next

        Return text.ToString()
    End Function

    ''' <summary>把文本直接分词出来的 token 序列渲染成 <c>[id]文本</c> 形式。</summary>
    Public Function DescribeText(text As String) As String
        Return Describe(Encode(text))
    End Function

#End Region

End Class
