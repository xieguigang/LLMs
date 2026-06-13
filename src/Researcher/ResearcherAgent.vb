' ============================================================================
' ResearchAgent - 科研文献调研大语言模型Agent工具
' 
' 基于本地Ollama大语言模型服务，实现自动化科研文献调研功能。
' Agent会迭代地从本地NCBI PubMed镜像MySQL数据库中查询文献，
' 阅读并总结文献内容，最终生成研究综述报告（Markdown → HTML → PDF）。
'
' 依赖模块: Ollama (本地LLMs客户端)
' 依赖数据库: NCBI PubMed 本地MySQL镜像
' ============================================================================

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

' ============================================================================
' 核心Agent类 - 科研文献调研Agent
' ============================================================================

''' <summary>
''' 科研文献调研Agent - 基于Ollama大语言模型的自动化文献调研工具
''' </summary>
''' <remarks>
''' 该Agent实现了完整的文献调研工作流：
''' 1. 从研究主题中提取关键词
''' 2. 使用关键词查询PubMed数据库
''' 3. 阅读并总结文献内容
''' 4. 迭代优化关键词并继续查询
''' 5. 生成综述报告（Markdown → HTML → PDF）
''' </remarks>
Public Class ResearchAgent : Implements IDisposable

    ' ---- 常量定义 ----

    ''' <summary>系统提示词模板，定义Agent的角色和行为规范</summary>
    Private Const SYSTEM_PROMPT As String =
"你是一个专业的科研文献调研助手Agent。你的任务是帮助用户对指定的研究主题进行系统性的文献调研。

你的工作流程如下：
1. 分析用户给出的研究主题，提取出最相关的研究关键词
2. 使用search_papers函数从PubMed数据库中查询相关文献
3. 仔细阅读查询到的文献标题和摘要，总结与研究方向相关的知识点
4. 根据文献查询结果，判断是否需要进一步凝练新的关键词进行补充查询
5. 如果需要，使用新的关键词再次查询数据库
6. 重复上述过程，直到你认为收集的文献已经足够全面地覆盖研究主题
7. 最后，将所有调研结果整理为一篇结构完整的学术综述

重要规则：
- 每次查询的关键词应该有针对性，避免过于宽泛或过于狭窄
- 注意文献的时间分布，优先关注近年来的研究进展
- 在总结文献时，要注重发现不同研究之间的关联和差异
- 综述应当包含：研究背景、主要发现、研究方法比较、当前挑战和未来方向
- 所有引用的文献必须来自数据库查询结果，不得编造文献
- 当你认为文献已经足够充分时，明确表示'文献调研已完成'并开始撰写综述"

    ''' <summary>关键词提取的提示词模板</summary>
    Private Const KEYWORD_EXTRACTION_PROMPT As String =
"基于以下研究主题，请提取出5-10个最相关的研究关键词/主题词，用于在PubMed数据库中进行文献检索。

研究主题：{0}

请按照以下格式输出关键词，每行一个：
1. 关键词1
2. 关键词2
...

注意：
- 关键词应当是学术研究中常用的术语
- 包含英文和中文的关键词（如果适用）
- 考虑同义词和相关术语
- 关键词应该覆盖研究主题的不同方面"

    ''' <summary>文献总结的提示词模板</summary>
    Private Const PAPER_SUMMARY_PROMPT As String =
"请仔细阅读以下从PubMed数据库中检索到的文献信息，总结与研究主题「{0}」相关的知识点。

文献列表：
{1}

请从以下几个方面进行总结：
1. 主要研究发现和结论
2. 使用的研究方法和技术
3. 不同研究之间的一致性和差异
4. 研究中存在的局限性
5. 可能的研究空白和未来方向

同时，请评估当前收集的文献是否已经足够全面地覆盖研究主题。如果不够充分，请建议3-5个新的搜索关键词用于补充查询。
如果认为文献已经足够，请明确说明'文献调研已完成，可以开始撰写综述'。"

    ''' <summary>综述撰写的提示词模板</summary>
    Private Const REVIEW_WRITING_PROMPT As String =
"基于以下文献调研结果，请撰写一篇关于「{0}」的学术综述。

调研轮次和总结：
{1}

所有引用文献列表：
{2}

请按照以下结构撰写综述（使用Markdown格式）：

# {0} - 研究综述

## 摘要
（简要概述研究主题的背景、主要发现和结论，200-300字）

**关键词：** （列出5-8个核心关键词）

## 1. 引言
（介绍研究主题的背景、重要性和研究意义，说明本综述的目的和范围）

## 2. 研究方法概述
（概述该领域常用的研究方法和技术路线，比较不同方法的优缺点）

## 3. 主要研究发现
（按主题或方法分类，详细总结文献中的主要发现，引用具体文献）

### 3.1 [子主题1]
### 3.2 [子主题2]
### 3.3 [子主题3]
（根据文献内容自行确定子主题分类）

## 4. 讨论与比较
（对不同研究的结果进行比较和讨论，分析一致性和差异，探讨可能的原因）

## 5. 当前挑战与局限性
（总结该领域目前面临的主要挑战和各研究的局限性）

## 6. 未来研究方向
（基于文献调研结果，提出未来可能的研究方向和发展趋势）

## 7. 结论
（总结本综述的核心发现和结论）

## 参考文献
（按编号列出所有引用的文献，格式：[序号] 作者. 标题. 期刊. 年份. PMID: xxx）

重要要求：
- 综述内容必须基于实际查询到的文献，不得编造任何研究数据或结论
- 引用文献时使用[序号]格式，与参考文献列表对应
- 语言应当学术严谨、逻辑清晰
- 每个章节内容应当充实，避免过于简略
- 参考文献必须包含所有在正文中引用的文献"

    ''' <summary>关键词优化提示词模板</summary>
    Private Const KEYWORD_REFINEMENT_PROMPT As String =
"基于当前的文献调研进展，请分析已有的查询结果，并提出新的搜索关键词以补充文献覆盖的不足。

研究主题：{0}

已使用的搜索关键词：
{1}

当前调研总结：
{2}

请提出3-5个新的搜索关键词，这些关键词应当：
1. 与研究主题相关但尚未被充分覆盖的方面
2. 可能是已有文献中提到但未深入探讨的方向
3. 考虑使用MeSH主题词或更专业的学术术语

请按以下格式输出：
新关键词1: [关键词]
新关键词2: [关键词]
...

如果认为当前文献已经足够充分，无需进一步查询，请回复：文献调研已完成"

    ' ---- 成员变量 ----

    Private ReadOnly _ollama As Ollama.Ollama
    Private ReadOnly _pubmedTool As PubMedQueryTool
    Private ReadOnly _converterTool As DocumentConverterTool
    Private ReadOnly _outputDir As String
    Private ReadOnly _maxRounds As Integer
    Private ReadOnly _papersPerQuery As Integer

    ''' <summary>已收集的所有去重文献字典，键为PMID</summary>
    Private ReadOnly _allPapers As New Dictionary(Of String, PaperRecord)()

    ''' <summary>所有轮次的调研记录</summary>
    Private ReadOnly _rounds As New List(Of ResearchRound)()

    ''' <summary>对话历史消息列表</summary>
    Private ReadOnly _conversationHistory As New List(Of String)()

    ''' <summary>Agent是否已被初始化（函数工具已注册）</summary>
    Private _initialized As Boolean = False

    ' ---- 构造函数 ----

    ''' <summary>
    ''' 创建科研文献调研Agent实例
    ''' </summary>
    ''' <param name="ollama">已配置的Ollama客户端实例</param>
    ''' <param name="dbConnectionString">PubMed MySQL镜像数据库连接字符串</param>
    ''' <param name="outputDir">输出文件目录路径</param>
    ''' <param name="maxRounds">最大迭代调研轮次（默认5轮）</param>
    ''' <param name="papersPerQuery">每次查询返回的最大文献数（默认20篇）</param>
    Public Sub New(ollama As Ollama.Ollama,
                   dbConnectionString As String,
                   outputDir As String,
                   Optional maxRounds As Integer = 5,
                   Optional papersPerQuery As Integer = 20)
        _ollama = ollama
        _pubmedTool = New PubMedQueryTool(dbConnectionString)
        _converterTool = New DocumentConverterTool(outputDir)
        _outputDir = outputDir
        _maxRounds = maxRounds
        _papersPerQuery = papersPerQuery

        If Not Directory.Exists(_outputDir) Then
            Directory.CreateDirectory(_outputDir)
        End If
    End Sub

    ' ---- 公开方法 ----

    ''' <summary>
    ''' 初始化Agent，将函数工具注册到Ollama客户端
    ''' </summary>
    Public Sub Initialize()
        If _initialized Then Return

        ' 注册PubMed数据库查询工具函数
        _ollama.AddFunction(_pubmedTool, fun:=NameOf(PubMedQueryTool.search_papers))
        _ollama.AddFunction(_pubmedTool, fun:=NameOf(PubMedQueryTool.get_full_text))
        _ollama.AddFunction(_pubmedTool, fun:=NameOf(PubMedQueryTool.get_database_stats))

        ' 注册文档格式转换工具函数
        _ollama.AddFunction(_converterTool, fun:=NameOf(DocumentConverterTool.markdown_to_html))
        _ollama.AddFunction(_converterTool, fun:=NameOf(DocumentConverterTool.html_to_pdf))

        _initialized = True
    End Sub

    ''' <summary>
    ''' 执行科研文献调研任务
    ''' </summary>
    ''' <param name="topic">用户指定的研究主题</param>
    ''' <returns>包含完整调研结果的ResearchResult对象</returns>
    Public Async Function ConductResearchAsync(topic As String) As Task(Of ResearchResult)
        If Not _initialized Then
            Throw New InvalidOperationException("Agent尚未初始化，请先调用Initialize()方法注册函数工具。")
        End If

        If String.IsNullOrWhiteSpace(topic) Then
            Throw New ArgumentException("研究主题不能为空。", NameOf(topic))
        End If

        ' 重置状态
        _allPapers.Clear()
        _rounds.Clear()
        _conversationHistory.Clear()

        Dim result As New ResearchResult() With {.Topic = topic}

        ' ====== 阶段1：提取初始研究关键词 ======
        Console.WriteLine($"[ResearchAgent] 开始文献调研，研究主题：{topic}")
        Console.WriteLine("[ResearchAgent] 阶段1：提取初始研究关键词...")

        Dim initialKeywords = Await ExtractKeywordsAsync(topic)
        Console.WriteLine($"[ResearchAgent] 提取到 {initialKeywords.Count} 个关键词：{String.Join(", ", initialKeywords)}")

        ' ====== 阶段2：迭代文献查询与总结 ======
        Console.WriteLine("[ResearchAgent] 阶段2：开始迭代文献查询与总结...")

        Dim currentKeywords = initialKeywords
        Dim isResearchComplete = False
        Dim roundNum = 0

        While Not isResearchComplete AndAlso roundNum < _maxRounds
            roundNum += 1
            Console.WriteLine($"[ResearchAgent] --- 第 {roundNum} 轮调研 ---")

            Dim roundRecord As New ResearchRound() With {.RoundNumber = roundNum}
            roundRecord.Keywords.AddRange(currentKeywords)

            ' 2a. 使用关键词查询PubMed数据库
            Console.WriteLine($"[ResearchAgent] 使用关键词查询数据库：{String.Join(", ", currentKeywords)}")
            Dim queryResult = Await QueryPapersAsync(currentKeywords, topic)
            roundRecord.QuerySQL = queryResult.QuerySQL

            ' 2b. 解析查询结果并添加到文献集合
            Dim newPapers = ParsePaperRecords(queryResult.RawJson)
            Console.WriteLine($"[ResearchAgent] 查询到 {newPapers.Count} 篇文献")

            ' 去重并添加到总文献集合
            Dim trulyNewPapers As New List(Of PaperRecord)()
            For Each paper In newPapers
                If Not _allPapers.ContainsKey(paper.PMID) Then
                    _allPapers(paper.PMID) = paper
                    trulyNewPapers.Add(paper)
                End If
            Next
            roundRecord.Papers.AddRange(trulyNewPapers)
            Console.WriteLine($"[ResearchAgent] 其中 {trulyNewPapers.Count} 篇为新文献（去重后）")

            ' 2c. 如果有新文献，阅读并总结
            If trulyNewPapers.Count > 0 Then
                Console.WriteLine("[ResearchAgent] 阅读并总结文献内容...")
                Dim summary = Await SummarizePapersAsync(topic, trulyNewPapers, roundNum)
                roundRecord.Summary = summary
                Console.WriteLine($"[ResearchAgent] 文献总结完成，总结长度：{summary.Length} 字符")

                ' 2d. 判断是否需要继续查询
                If Me.IsResearchComplete(summary) Then
                    Console.WriteLine("[ResearchAgent] Agent判断文献调研已完成")
                    isResearchComplete = True
                Else
                    ' 2e. 提取新的关键词
                    Console.WriteLine("[ResearchAgent] 凝练新的搜索关键词...")
                    Dim newKeywords = Await RefineKeywordsAsync(topic, currentKeywords, summary)
                    If newKeywords.Count = 0 Then
                        Console.WriteLine("[ResearchAgent] 未获得新的关键词，结束迭代")
                        isResearchComplete = True
                    Else
                        currentKeywords = newKeywords
                        Console.WriteLine($"[ResearchAgent] 新的关键词：{String.Join(", ", newKeywords)}")
                    End If
                End If
            Else
                Console.WriteLine("[ResearchAgent] 未查询到新文献，尝试更换关键词...")
                Dim newKeywords = Await RefineKeywordsAsync(topic, currentKeywords, "未找到新的相关文献，需要更换搜索策略。")
                If newKeywords.Count = 0 Then
                    isResearchComplete = True
                Else
                    currentKeywords = newKeywords
                End If
            End If

            _rounds.Add(roundRecord)
        End While

        If roundNum >= _maxRounds Then
            Console.WriteLine($"[ResearchAgent] 已达到最大迭代轮次（{_maxRounds}轮），结束调研")
        End If

        ' ====== 阶段3：撰写综述 ======
        Console.WriteLine("[ResearchAgent] 阶段3：撰写研究综述...")
        Dim reviewMarkdown = Await WriteReviewAsync(topic)
        result.ReviewMarkdown = reviewMarkdown
        result.AllPapers = _allPapers.Values.ToList()
        result.Rounds = _rounds.ToList()

        ' ====== 阶段4：输出文件 ======
        Console.WriteLine("[ResearchAgent] 阶段4：输出结果文件...")

        ' 4a. 保存Markdown文件
        Dim mdFileName = SanitizeFileName(topic) & "_review.md"
        Dim mdFilePath = Path.Combine(_outputDir, mdFileName)
        File.WriteAllText(mdFilePath, reviewMarkdown, Encoding.UTF8)
        result.OutputMarkdownPath = mdFilePath
        Console.WriteLine($"[ResearchAgent] Markdown文件已保存：{mdFilePath}")

        ' 4b. 转换为HTML
        Dim htmlFileName = SanitizeFileName(topic) & "_review.html"
        Dim htmlResult = _converterTool.markdown_to_html(reviewMarkdown, htmlFileName)
        Console.WriteLine($"[ResearchAgent] HTML转换结果：{htmlResult}")

        ' 4c. 转换为PDF
        Dim htmlFilePath = Path.Combine(_outputDir, htmlFileName)
        Dim pdfFileName = SanitizeFileName(topic) & "_review.pdf"
        Dim pdfResult = _converterTool.html_to_pdf(htmlFilePath, pdfFileName)
        Console.WriteLine($"[ResearchAgent] PDF转换结果：{pdfResult}")

        ' 解析PDF路径
        If pdfResult.Contains("""pdf_path""") Then
            Dim pdfPathMatch = Regex.Match(pdfResult, """pdf_path"":\s*""([^""]+)""")
            If pdfPathMatch.Success Then
                result.OutputPdfPath = pdfPathMatch.Groups(1).Value
            End If
        End If

        Console.WriteLine($"[ResearchAgent] 文献调研完成！共收集 {_allPapers.Count} 篇文献，经过 {_rounds.Count} 轮迭代")
        Return result
    End Function

    ''' <summary>
    ''' 获取当前已收集的所有文献列表
    ''' </summary>
    Public Function GetCollectedPapers() As List(Of PaperRecord)
        Return _allPapers.Values.ToList()
    End Function

    ''' <summary>
    ''' 获取所有调研轮次的记录
    ''' </summary>
    Public Function GetResearchRounds() As List(Of ResearchRound)
        Return _rounds.ToList()
    End Function

    ' ---- 私有方法 - Agent核心逻辑 ----

    ''' <summary>
    ''' 从研究主题中提取关键词
    ''' </summary>
    Private Async Function ExtractKeywordsAsync(topic As String) As Task(Of List(Of String))
        Dim prompt = String.Format(KEYWORD_EXTRACTION_PROMPT, topic)
        Dim response = Await _ollama.Chat(prompt)

        ' 从LLM响应中解析关键词
        Dim keywords = ParseKeywordsFromResponse(response.output)
        If keywords.Count = 0 Then
            ' 如果LLM未能提取关键词，使用主题本身作为关键词
            keywords = New List(Of String)() From {topic}
        End If

        Return keywords
    End Function

    ''' <summary>
    ''' 使用关键词查询PubMed数据库
    ''' </summary>
    Private Async Function QueryPapersAsync(keywords As List(Of String), topic As String) As Task(Of (RawJson As String, QuerySQL As String))
        ' 构建查询提示词，让LLM调用search_papers函数
        Dim keywordStr = String.Join(", ", keywords)
        Dim prompt = $"请使用search_papers函数搜索以下关键词的相关文献：{keywordStr}" & vbCrLf &
                     $"研究主题是：{topic}" & vbCrLf &
                     $"请搜索最多{_papersPerQuery}篇相关文献。"

        Dim response = Await _ollama.Chat(prompt)

        ' 从响应中提取查询结果
        Dim rawJson = response.output
        Dim querySQL = ""

        ' 尝试从响应中提取SQL语句（如果LLM在函数调用结果中包含了query_sql字段）
        Dim sqlMatch = Regex.Match(rawJson, """query_sql"":\s*""([^""]+)""")
        If sqlMatch.Success Then
            querySQL = sqlMatch.Groups(1).Value
        End If

        Return (rawJson, querySQL)
    End Function

    ''' <summary>
    ''' 阅读并总结文献内容
    ''' </summary>
    Private Async Function SummarizePapersAsync(topic As String, papers As List(Of PaperRecord), roundNum As Integer) As Task(Of String)
        ' 构建文献信息文本
        Dim papersText As New StringBuilder()
        For i = 0 To papers.Count - 1
            papersText.AppendLine($"--- 文献 {i + 1} ---")
            papersText.AppendLine(papers(i).SummaryText)
            papersText.AppendLine()
        Next

        ' 如果文献数量较多，分批总结
        If papers.Count > 15 Then
            Return Await SummarizePapersInBatchesAsync(topic, papers, roundNum)
        End If

        Dim prompt = String.Format(PAPER_SUMMARY_PROMPT, topic, papersText.ToString())
        Dim response = Await _ollama.Chat(prompt)
        Return response.output
    End Function

    ''' <summary>
    ''' 分批阅读并总结大量文献
    ''' </summary>
    Private Async Function SummarizePapersInBatchesAsync(topic As String, papers As List(Of PaperRecord), roundNum As Integer) As Task(Of String)
        Const BATCH_SIZE As Integer = 10
        Dim batchSummaries As New List(Of String)()

        For batchStart = 0 To papers.Count - 1 Step BATCH_SIZE
            Dim batchEnd = Math.Min(batchStart + BATCH_SIZE, papers.Count)
            Dim batchPapers = papers.Skip(batchStart).Take(BATCH_SIZE).ToList()

            Dim papersText As New StringBuilder()
            For i = 0 To batchPapers.Count - 1
                papersText.AppendLine($"--- 文献 {batchStart + i + 1} ---")
                papersText.AppendLine(batchPapers(i).SummaryText)
                papersText.AppendLine()
            Next

            Dim batchPrompt = $"请总结以下第 {batchStart + 1}-{batchEnd} 篇文献中与研究主题「{topic}」相关的关键发现：" & vbCrLf & vbCrLf &
                              papersText.ToString()

            Dim response = Await _ollama.Chat(batchPrompt)
            batchSummaries.Add(response.output)

            Console.WriteLine($"[ResearchAgent]   已总结第 {batchStart + 1}-{batchEnd} 篇文献")
        Next

        ' 合并所有批次的总结
        If batchSummaries.Count = 1 Then
            Return batchSummaries(0)
        End If

        Dim mergePrompt = $"以下是关于研究主题「{topic}」的分批文献总结，请将它们整合为一个统一的总结：" & vbCrLf & vbCrLf &
                          String.Join(vbCrLf & vbCrLf & "---" & vbCrLf & vbCrLf, batchSummaries)

        Dim mergeResponse = Await _ollama.Chat(mergePrompt)
        Return mergeResponse.output
    End Function

    ''' <summary>
    ''' 根据当前调研结果凝练新的搜索关键词
    ''' </summary>
    Private Async Function RefineKeywordsAsync(topic As String, usedKeywords As List(Of String), currentSummary As String) As Task(Of List(Of String))
        Dim usedKeywordsStr = String.Join(", ", usedKeywords)
        Dim prompt = String.Format(KEYWORD_REFINEMENT_PROMPT, topic, usedKeywordsStr, currentSummary)

        Dim response = Await _ollama.Chat(prompt)

        ' 检查是否已完成
        If IsResearchComplete(response.output) Then
            Return New List(Of String)()
        End If

        ' 解析新的关键词
        Return ParseKeywordsFromResponse(response.output)
    End Function

    ''' <summary>
    ''' 基于所有调研结果撰写综述
    ''' </summary>
    Private Async Function WriteReviewAsync(topic As String) As Task(Of String)
        ' 构建调研轮次总结文本
        Dim roundsSummary As New StringBuilder()
        For Each round In _rounds
            roundsSummary.AppendLine($"=== 第 {round.RoundNumber} 轮调研 ===")
            roundsSummary.AppendLine($"搜索关键词：{String.Join(", ", round.Keywords)}")
            roundsSummary.AppendLine($"查询到文献数：{round.Papers.Count}")
            If Not String.IsNullOrEmpty(round.Summary) Then
                roundsSummary.AppendLine($"总结：{round.Summary}")
            End If
            roundsSummary.AppendLine()
        Next

        ' 构建引用文献列表
        Dim references As New StringBuilder()
        Dim paperList = _allPapers.Values.ToList()
        For i = 0 To paperList.Count - 1
            references.AppendLine($"[{i + 1}] {paperList(i).Citation}")
        Next

        Dim prompt = String.Format(REVIEW_WRITING_PROMPT, topic, roundsSummary.ToString(), references.ToString())
        Dim response = Await _ollama.Chat(prompt)

        Return response.output
    End Function

    ' ---- 私有方法 - 辅助工具 ----

    ''' <summary>
    ''' 从LLM响应文本中解析关键词列表
    ''' </summary>
    Private Function ParseKeywordsFromResponse(responseText As String) As List(Of String)
        Dim keywords As New List(Of String)()

        If String.IsNullOrWhiteSpace(responseText) Then
            Return keywords
        End If

        ' 匹配编号列表格式：1. keyword 或 1) keyword
        Dim numberedPattern = "^\s*\d+[\.\)]\s*(.+?)$"
        For Each line In responseText.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
            Dim match = Regex.Match(line, numberedPattern)
            If match.Success Then
                Dim kw = match.Groups(1).Value.Trim().Trim(""""c, "'"c, "["c, "]"c)
                If Not String.IsNullOrWhiteSpace(kw) AndAlso kw.Length > 1 Then
                    keywords.Add(kw)
                End If
            End If
        Next

        ' 如果编号列表解析失败，尝试按行解析
        If keywords.Count = 0 Then
            For Each line In responseText.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                Dim trimmed = line.Trim().Trim("-"c, "*"c, ">"c)
                If Not String.IsNullOrWhiteSpace(trimmed) AndAlso trimmed.Length > 1 AndAlso
                   Not trimmed.StartsWith("新关键词") AndAlso Not trimmed.StartsWith("关键词") AndAlso
                   Not trimmed.StartsWith("注意") AndAlso Not trimmed.StartsWith("请") Then
                    ' 移除前缀编号
                    trimmed = Regex.Replace(trimmed, "^\d+[\.\)]\s*", "")
                    If Not String.IsNullOrWhiteSpace(trimmed) Then
                        keywords.Add(trimmed.Trim(""""c, "'"c))
                    End If
                End If
            Next
        End If

        Return keywords.Distinct().ToList()
    End Function

    ''' <summary>
    ''' 从JSON查询结果中解析文献记录
    ''' </summary>
    Private Function ParsePaperRecords(jsonResult As String) As List(Of PaperRecord)
        Dim papers As New List(Of PaperRecord)()

        If String.IsNullOrWhiteSpace(jsonResult) Then
            Return papers
        End If

        Try
            ' 尝试从JSON中提取papers数组
            ' 使用简易JSON解析（避免依赖外部JSON库）
            Dim papersMatch = Regex.Match(jsonResult, """papers"":\s*\[(.+?)\](?=\s*[},])", RegexOptions.Singleline)
            If Not papersMatch.Success Then
                ' 尝试直接匹配对象数组
                papersMatch = Regex.Match(jsonResult, "\[\s*\{.+?\}\s*\]", RegexOptions.Singleline)
            End If

            If Not papersMatch.Success Then
                Return papers
            End If

            Dim papersArray = papersMatch.Value

            ' 匹配每个文献对象
            Dim objPattern = "\{[^{}]*\}"
            For Each objMatch As Match In Regex.Matches(papersArray, objPattern)
                Dim obj = objMatch.Value
                Dim paper As New PaperRecord()

                paper.PMID = ExtractJsonValue(obj, "pmid")
                paper.Title = ExtractJsonValue(obj, "title")
                paper.Authors = ExtractJsonValue(obj, "authors")
                paper.Journal = ExtractJsonValue(obj, "journal")
                paper.Abstract = ExtractJsonValue(obj, "abstract")
                paper.FullText = ExtractJsonValue(obj, "full_text")
                paper.DOI = ExtractJsonValue(obj, "doi")
                paper.Keywords = ExtractJsonValue(obj, "keywords")
                paper.MeshTerms = ExtractJsonValue(obj, "mesh_terms")

                Dim yearStr = ExtractJsonValue(obj, "year")
                Dim yearVal As Integer
                If Integer.TryParse(yearStr, yearVal) Then
                    paper.Year = yearVal
                End If

                ' 只添加有PMID和标题的记录
                If Not String.IsNullOrWhiteSpace(paper.PMID) OrElse Not String.IsNullOrWhiteSpace(paper.Title) Then
                    papers.Add(paper)
                End If
            Next
        Catch ex As Exception
            Console.WriteLine($"[ResearchAgent] 解析文献记录时出错：{ex.Message}")
        End Try

        Return papers
    End Function

    ''' <summary>
    ''' 从JSON对象字符串中提取指定键的值
    ''' </summary>
    Private Function ExtractJsonValue(jsonObj As String, key As String) As String
        Dim pattern = $"""{key}"":\s*""(.*?)"""
        Dim match = Regex.Match(jsonObj, pattern)
        If match.Success Then
            Return match.Groups(1).Value.
                         Replace("\\", "\").Replace("\""", """").
                         Replace("\n", vbCrLf).Replace("\r", "").Replace("\t", vbTab)
        End If
        Return ""
    End Function

    ''' <summary>
    ''' 判断LLM响应中是否表示文献调研已完成
    ''' </summary>
    Private Function IsResearchComplete(responseText As String) As Boolean
        If String.IsNullOrWhiteSpace(responseText) Then Return False

        Dim completionPhrases = {
            "文献调研已完成",
            "调研已完成",
            "已经足够",
            "文献已充分",
            "无需进一步",
            "可以开始撰写综述",
            "research is complete",
            "sufficient literature",
            "no further search needed"
        }

        Dim lowerResponse = responseText.ToLower()
        Return completionPhrases.Any(Function(p) lowerResponse.Contains(p.ToLower()))
    End Function

    ''' <summary>
    ''' 清理文件名中的非法字符
    ''' </summary>
    Private Function SanitizeFileName(fileName As String) As String
        Dim invalid = Path.GetInvalidFileNameChars()
        Dim result = fileName
        For Each c In invalid
            result = result.Replace(c, "_"c)
        Next
        ' 限制文件名长度
        If result.Length > 100 Then
            result = result.Substring(0, 100)
        End If
        Return result.Trim()
    End Function

    ' ---- IDisposable实现 ----

    Private _disposed As Boolean = False

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposed Then
            If disposing Then
                ' 释放托管资源
                _allPapers.Clear()
                _rounds.Clear()
                _conversationHistory.Clear()
            End If
            _disposed = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

End Class
