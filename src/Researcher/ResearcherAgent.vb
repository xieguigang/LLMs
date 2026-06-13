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

Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports MySql.Data.MySqlClient

' ============================================================================
' 数据模型定义
' ============================================================================

''' <summary>
''' 表示从PubMed数据库中查询得到的单篇文献记录
''' </summary>
Public Class PaperRecord

    ''' <summary>
    ''' PubMed唯一标识符 (PMID)
    ''' </summary>
    Public Property PMID As String

    ''' <summary>
    ''' 文献标题
    ''' </summary>
    Public Property Title As String

    ''' <summary>
    ''' 文献作者列表，以逗号分隔
    ''' </summary>
    Public Property Authors As String

    ''' <summary>
    ''' 期刊名称
    ''' </summary>
    Public Property Journal As String

    ''' <summary>
    ''' 发表年份
    ''' </summary>
    Public Property Year As Integer

    ''' <summary>
    ''' 文献摘要文本
    ''' </summary>
    Public Property Abstract As String

    ''' <summary>
    ''' 文献全文文本（如果可用）
    ''' </summary>
    Public Property FullText As String

    ''' <summary>
    ''' DOI标识符
    ''' </summary>
    Public Property DOI As String

    ''' <summary>
    ''' 关键词列表，以逗号分隔
    ''' </summary>
    Public Property Keywords As String

    ''' <summary>
    ''' MeSH主题词，以逗号分隔
    ''' </summary>
    Public Property MeshTerms As String

    ''' <summary>
    ''' 获取文献的简要引用格式字符串
    ''' </summary>
    Public ReadOnly Property Citation As String
        Get
            Return $"{Authors} ({Year}). {Title}. {Journal}. PMID: {PMID}{If(String.IsNullOrEmpty(DOI), "", $", DOI: {DOI}")}"
        End Get
    End Property

    ''' <summary>
    ''' 获取用于LLM阅读的文献摘要信息（标题+摘要）
    ''' </summary>
    Public ReadOnly Property SummaryText As String
        Get
            Dim sb As New StringBuilder()
            sb.AppendLine($"[PMID: {PMID}]")
            sb.AppendLine($"Title: {Title}")
            sb.AppendLine($"Authors: {Authors}")
            sb.AppendLine($"Journal: {Journal} ({Year})")
            If Not String.IsNullOrEmpty(Abstract) Then
                sb.AppendLine($"Abstract: {Abstract}")
            End If
            If Not String.IsNullOrEmpty(Keywords) Then
                sb.AppendLine($"Keywords: {Keywords}")
            End If
            If Not String.IsNullOrEmpty(MeshTerms) Then
                sb.AppendLine($"MeSH Terms: {MeshTerms}")
            End If
            Return sb.ToString()
        End Get
    End Property

    ''' <summary>
    ''' 获取用于LLM深度阅读的文献完整信息（标题+摘要+全文）
    ''' </summary>
    Public ReadOnly Property FullReadingText As String
        Get
            Dim sb As New StringBuilder()
            sb.AppendLine(SummaryText)
            If Not String.IsNullOrEmpty(FullText) Then
                sb.AppendLine("--- Full Text ---")
                sb.AppendLine(FullText)
            End If
            Return sb.ToString()
        End Get
    End Property

End Class

''' <summary>
''' 表示一轮文献调研的状态快照，用于跟踪Agent的迭代进度
''' </summary>
Public Class ResearchRound

    ''' <summary>
    ''' 当前轮次编号（从1开始）
    ''' </summary>
    Public Property RoundNumber As Integer

    ''' <summary>
    ''' 本轮使用的研究主题词条列表
    ''' </summary>
    Public Property Keywords As New List(Of String)()

    ''' <summary>
    ''' 本轮查询到的文献列表
    ''' </summary>
    Public Property Papers As New List(Of PaperRecord)()

    ''' <summary>
    ''' 本轮LLM对文献的知识点总结文本
    ''' </summary>
    Public Property Summary As String

    ''' <summary>
    ''' 本轮使用的数据库查询SQL语句
    ''' </summary>
    Public Property QuerySQL As String

End Class

''' <summary>
''' 表示整个文献调研任务的最终结果
''' </summary>
Public Class ResearchResult

    ''' <summary>
    ''' 用户指定的原始研究主题
    ''' </summary>
    Public Property Topic As String

    ''' <summary>
    ''' 所有轮次的调研记录
    ''' </summary>
    Public Property Rounds As New List(Of ResearchRound)()

    ''' <summary>
    ''' 最终生成的综述Markdown文本
    ''' </summary>
    Public Property ReviewMarkdown As String

    ''' <summary>
    ''' 去重后的所有引用文献列表
    ''' </summary>
    Public Property AllPapers As New List(Of PaperRecord)()

    ''' <summary>
    ''' 最终输出的PDF文件路径
    ''' </summary>
    Public Property OutputPdfPath As String

    ''' <summary>
    ''' 最终输出的Markdown文件路径
    ''' </summary>
    Public Property OutputMarkdownPath As String

End Class

' ============================================================================
' 函数工具类 - 注册到Ollama供LLM调用的工具
' ============================================================================

''' <summary>
''' PubMed数据库查询工具类，提供从本地NCBI PubMed MySQL镜像数据库中查询文献的函数
''' </summary>
''' <remarks>
''' 此类中的公开方法将通过Ollama.AddFunction注册为大语言模型的函数调用工具，
''' 使LLM能够自主构建查询条件并从PubMed数据库中检索文献。
''' </remarks>
Public Class PubMedQueryTool

    Private ReadOnly _connectionString As String

    ''' <summary>
    ''' 创建PubMed查询工具实例
    ''' </summary>
    ''' <param name="connectionString">MySQL数据库连接字符串</param>
    Public Sub New(connectionString As String)
        _connectionString = connectionString
    End Sub

    ''' <summary>
    ''' 根据关键词从PubMed数据库中查询文献记录
    ''' </summary>
    ''' <param name="keywords">搜索关键词，多个关键词以空格或逗号分隔</param>
    ''' <param name="year_from">发表年份起始范围（可选，默认不限制）</param>
    ''' <param name="year_to">发表年份结束范围（可选，默认不限制）</param>
    ''' <param name="max_results">返回结果的最大数量（默认20，最大100）</param>
    ''' <returns>JSON格式的文献查询结果列表</returns>
    <Description("Search PubMed database for research papers by keywords. Returns a JSON array of paper records including PMID, title, authors, journal, year, abstract, keywords, and MeSH terms. Use this to find relevant scientific literature on a research topic.")>
    Public Function search_papers(
        <Argument("keywords", Description:="Search keywords separated by spaces or commas, e.g. 'CRISPR gene editing therapy'")> keywords As String,
        <Argument("year_from", Description:="Minimum publication year filter (optional, 0 for no limit)")> Optional year_from As Integer = 0,
        <Argument("year_to", Description:="Maximum publication year filter (optional, 0 for no limit)")> Optional year_to As Integer = 0,
        <Argument("max_results", Description:="Maximum number of results to return (default 20, max 100)")> Optional max_results As Integer = 20
    ) As String
        Try
            If String.IsNullOrWhiteSpace(keywords) Then
                Return "{""error"": ""Keywords parameter cannot be empty.""}"
            End If

            ' 限制最大返回数量
            max_results = Math.Min(Math.Max(max_results, 1), 100)

            ' 解析关键词为SQL全文搜索条件
            Dim keywordList = keywords.Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries).
                                       Select(Function(k) k.Trim().ToLower()).
                                       Where(Function(k) k.Length > 0).
                                       ToList()

            If keywordList.Count = 0 Then
                Return "{""error"": ""No valid keywords provided.""}"
            End If

            ' 构建SQL查询语句
            ' 假设PubMed镜像数据库表结构:
            '   Table: pubmed_articles
            '   Columns: pmid, title, authors, journal, pub_year, abstract, full_text, doi, keywords, mesh_terms
            Dim sql As New StringBuilder()
            sql.AppendLine("SELECT pmid, title, authors, journal, pub_year, abstract, doi, keywords, mesh_terms")
            sql.AppendLine("FROM pubmed_articles")
            sql.AppendLine("WHERE 1=1")

            ' 构建全文搜索条件：在标题、摘要、关键词、MeSH词中搜索
            Dim conditions As New List(Of String)()
            For Each kw In keywordList
                conditions.Add($"(LOWER(title) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR LOWER(abstract) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR LOWER(keywords) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR LOWER(mesh_terms) LIKE '%{EscapeSql(kw)}%')")
            Next

            ' 所有关键词条件以AND组合，确保结果与所有关键词相关
            sql.AppendLine($"  AND ({String.Join(" AND ", conditions)})")

            ' 年份范围过滤
            If year_from > 0 Then
                sql.AppendLine($"  AND pub_year >= {year_from}")
            End If
            If year_to > 0 Then
                sql.AppendLine($"  AND pub_year <= {year_to}")
            End If

            ' 按发表年份降序排列，优先返回最新文献
            sql.AppendLine("ORDER BY pub_year DESC, pmid DESC")
            sql.AppendLine($"LIMIT {max_results}")

            Dim queryStr = sql.ToString()

            ' 执行数据库查询
            Using conn As New MySqlConnection(_connectionString)
                conn.Open()
                Using cmd As New MySqlCommand(queryStr, conn)
                    Using reader = cmd.ExecuteReader()
                        Dim results As New List(Of Dictionary(Of String, String))()
                        While reader.Read()
                            Dim record As New Dictionary(Of String, String)() From {
                                {"pmid", If(reader("pmid")?.ToString(), "")},
                                {"title", If(reader("title")?.ToString(), "")},
                                {"authors", If(reader("authors")?.ToString(), "")},
                                {"journal", If(reader("journal")?.ToString(), "")},
                                {"year", If(reader("pub_year")?.ToString(), "")},
                                {"abstract", If(reader("abstract")?.ToString(), "")},
                                {"doi", If(reader("doi")?.ToString(), "")},
                                {"keywords", If(reader("keywords")?.ToString(), "")},
                                {"mesh_terms", If(reader("mesh_terms")?.ToString(), "")}
                            }
                            results.Add(record)
                        End While
                        Return SerializeToJson(results, queryStr)
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Return $"{{""error"": ""Database query failed: {EscapeJson(ex.Message)}""}}"
        End Try
    End Function

    ''' <summary>
    ''' 根据PMID获取文献的全文文本
    ''' </summary>
    ''' <param name="pmid">PubMed唯一标识符</param>
    ''' <returns>JSON格式的文献全文数据</returns>
    <Description("Get the full text of a specific paper by its PMID. Returns the complete article text including title, abstract, and full body text. Use this when you need to read a paper in detail after finding it through search_papers.")>
    Public Function get_full_text(
        <Argument("pmid", Description:="The PubMed ID (PMID) of the paper to retrieve full text for")> pmid As String
    ) As String
        Try
            If String.IsNullOrWhiteSpace(pmid) Then
                Return "{""error"": ""PMID parameter cannot be empty.""}"
            End If

            Dim sql = "SELECT pmid, title, authors, journal, pub_year, abstract, full_text, doi, keywords, mesh_terms " &
                      "FROM pubmed_articles WHERE pmid = @pmid"

            Using conn As New MySqlConnection(_connectionString)
                conn.Open()
                Using cmd As New MySqlCommand(sql, conn)
                    cmd.Parameters.AddWithValue("@pmid", pmid)
                    Using reader = cmd.ExecuteReader()
                        If reader.Read() Then
                            Dim result As New Dictionary(Of String, String)() From {
                                {"pmid", If(reader("pmid")?.ToString(), "")},
                                {"title", If(reader("title")?.ToString(), "")},
                                {"authors", If(reader("authors")?.ToString(), "")},
                                {"journal", If(reader("journal")?.ToString(), "")},
                                {"year", If(reader("pub_year")?.ToString(), "")},
                                {"abstract", If(reader("abstract")?.ToString(), "")},
                                {"full_text", If(reader("full_text")?.ToString(), "")},
                                {"doi", If(reader("doi")?.ToString(), "")},
                                {"keywords", If(reader("keywords")?.ToString(), "")},
                                {"mesh_terms", If(reader("mesh_terms")?.ToString(), "")}
                            }
                            Dim results As New List(Of Dictionary(Of String, String))() From {result}
                            Return SerializeToJson(results, sql)
                        Else
                            Return $"{{""error"": ""No paper found with PMID: {EscapeJson(pmid)}""}}"
                        End If
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Return $"{{""error"": ""Failed to retrieve full text: {EscapeJson(ex.Message)}""}}"
        End Try
    End Function

    ''' <summary>
    ''' 获取PubMed数据库的统计信息，包括总文献数量、年份分布等
    ''' </summary>
    ''' <param name="keywords">可选的关键词过滤条件</param>
    ''' <returns>JSON格式的统计信息</returns>
    <Description("Get statistics about the PubMed database, including total article count and year distribution. Optionally filter by keywords. Use this to understand the scope of available literature before conducting detailed searches.")>
    Public Function get_database_stats(
        <Argument("keywords", Description:="Optional keywords to filter statistics (empty for all articles)")> Optional keywords As String = ""
    ) As String
        Try
            Dim sql As New StringBuilder()
            Dim hasKeywords = Not String.IsNullOrWhiteSpace(keywords)

            If hasKeywords Then
                Dim keywordList = keywords.Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries).
                                           Select(Function(k) k.Trim().ToLower()).ToList()
                Dim conditions As New List(Of String)()
                For Each kw In keywordList
                    conditions.Add($"(LOWER(title) LIKE '%{EscapeSql(kw)}%' " &
                                   $"OR LOWER(abstract) LIKE '%{EscapeSql(kw)}%' " &
                                   $"OR LOWER(keywords) LIKE '%{EscapeSql(kw)}%')")
                Next
                sql.AppendLine("SELECT pub_year, COUNT(*) as cnt FROM pubmed_articles")
                sql.AppendLine($"WHERE {String.Join(" AND ", conditions)}")
                sql.AppendLine("GROUP BY pub_year ORDER BY pub_year DESC LIMIT 50")
            Else
                sql.AppendLine("SELECT pub_year, COUNT(*) as cnt FROM pubmed_articles")
                sql.AppendLine("GROUP BY pub_year ORDER BY pub_year DESC LIMIT 50")
            End If

            Using conn As New MySqlConnection(_connectionString)
                conn.Open()
                Using cmd As New MySqlCommand(sql.ToString(), conn)
                    Using reader = cmd.ExecuteReader()
                        Dim stats As New List(Of Dictionary(Of String, String))()
                        Dim total As Long = 0
                        While reader.Read()
                            Dim year = If(reader("pub_year")?.ToString(), "unknown")
                            Dim cnt = If(reader("cnt")?.ToString(), "0")
                            total += Long.Parse(cnt)
                            stats.Add(New Dictionary(Of String, String)() From {
                                {"year", year},
                                {"count", cnt}
                            })
                        End While
                        Return $"{{""total_articles"": {total}, ""year_distribution"": {DictListToJsonArray(stats)}}}"
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Return $"{{""error"": ""Failed to get database stats: {EscapeJson(ex.Message)}""}}"
        End Try
    End Function

    ' ---- 辅助方法 ----

    ''' <summary>
    ''' 转义SQL字符串中的特殊字符，防止SQL注入
    ''' </summary>
    Private Function EscapeSql(input As String) As String
        If String.IsNullOrEmpty(input) Then Return ""
        Return input.Replace("'", "''").Replace("\", "\\").Replace(";", "").Replace("--", "")
    End Function

    ''' <summary>
    ''' 转义JSON字符串中的特殊字符
    ''' </summary>
    Private Function EscapeJson(input As String) As String
        If String.IsNullOrEmpty(input) Then Return ""
        Return input.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, "\r").Replace(vbLf, "\n").Replace(vbTab, "\t")
    End Function

    ''' <summary>
    ''' 将查询结果字典列表序列化为JSON字符串
    ''' </summary>
    Private Function SerializeToJson(results As List(Of Dictionary(Of String, String)), querySQL As String) As String
        Dim sb As New StringBuilder()
        sb.Append("{")
        sb.Append($"""count"": {results.Count}, ")
        sb.Append($"""query_sql"": ""{EscapeJson(querySQL)}"", ")
        sb.Append($"""papers"": {DictListToJsonArray(results)}")
        sb.Append("}")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' 将字典列表转换为JSON数组字符串
    ''' </summary>
    Private Function DictListToJsonArray(dictList As List(Of Dictionary(Of String, String))) As String
        Dim sb As New StringBuilder()
        sb.Append("[")
        For i = 0 To dictList.Count - 1
            If i > 0 Then sb.Append(", ")
            sb.Append("{")
            Dim items = dictList(i).ToList()
            For j = 0 To items.Count - 1
                If j > 0 Then sb.Append(", ")
                sb.Append($"""{items(j).Key}"": ""{EscapeJson(items(j).Value)}""")
            Next
            sb.Append("}")
        Next
        sb.Append("]")
        Return sb.ToString()
    End Function

End Class

''' <summary>
''' 文档格式转换工具类，提供Markdown转HTML、HTML转PDF的函数
''' </summary>
Public Class DocumentConverterTool

    Private ReadOnly _outputDir As String

    ''' <summary>
    ''' 创建文档转换工具实例
    ''' </summary>
    ''' <param name="outputDir">输出文件目录路径</param>
    Public Sub New(outputDir As String)
        _outputDir = outputDir
        If Not Directory.Exists(_outputDir) Then
            Directory.CreateDirectory(_outputDir)
        End If
    End Sub

    ''' <summary>
    ''' 将Markdown文本转换为HTML文件
    ''' </summary>
    ''' <param name="markdown_content">Markdown格式的文本内容</param>
    ''' <param name="output_filename">输出的HTML文件名（不含路径）</param>
    ''' <returns>JSON格式的转换结果，包含HTML文件路径</returns>
    <Description("Convert Markdown text to an HTML file with professional academic styling. The output HTML includes proper CSS for academic paper formatting with readable fonts, margins, and heading styles. Returns the file path of the generated HTML.")>
    Public Function markdown_to_html(
        <Argument("markdown_content", Description:="The Markdown text content to convert to HTML")> markdown_content As String,
        <Argument("output_filename", Description:="Output HTML filename (e.g. 'research_review.html')")> output_filename As String
    ) As String
        Try
            If String.IsNullOrWhiteSpace(markdown_content) Then
                Return "{""error"": ""Markdown content cannot be empty.""}"
            End If

            ' 将Markdown转换为HTML（简易转换，支持常用Markdown语法）
            Dim htmlBody = SimpleMarkdownToHtml(markdown_content)

            ' 构建完整的HTML文档，包含学术风格的CSS
            Dim html = $"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Research Literature Review</title>
    <style>
        @page {{
            size: A4;
            margin: 2.5cm;
        }}
        body {{
            font-family: 'Times New Roman', 'SimSun', serif;
            font-size: 12pt;
            line-height: 1.8;
            color: #333;
            max-width: 210mm;
            margin: 0 auto;
            padding: 2cm;
            background: #fff;
        }}
        h1 {{
            font-size: 22pt;
            text-align: center;
            margin-top: 0;
            margin-bottom: 1.5em;
            padding-bottom: 0.5em;
            border-bottom: 2px solid #333;
            font-weight: bold;
        }}
        h2 {{
            font-size: 16pt;
            margin-top: 1.8em;
            margin-bottom: 0.8em;
            padding-bottom: 0.3em;
            border-bottom: 1px solid #999;
            font-weight: bold;
        }}
        h3 {{
            font-size: 14pt;
            margin-top: 1.5em;
            margin-bottom: 0.6em;
            font-weight: bold;
        }}
        h4 {{
            font-size: 12pt;
            margin-top: 1.2em;
            margin-bottom: 0.5em;
            font-weight: bold;
        }}
        p {{
            text-align: justify;
            margin-bottom: 0.8em;
            text-indent: 2em;
        }}
        ul, ol {{
            margin-left: 2em;
            margin-bottom: 0.8em;
        }}
        li {{
            margin-bottom: 0.3em;
        }}
        blockquote {{
            margin: 1em 2em;
            padding: 0.5em 1em;
            border-left: 4px solid #ccc;
            background: #f9f9f9;
            font-style: italic;
        }}
        code {{
            font-family: 'Courier New', monospace;
            background: #f4f4f4;
            padding: 0.1em 0.3em;
            border-radius: 3px;
            font-size: 10pt;
        }}
        pre {{
            background: #f4f4f4;
            padding: 1em;
            border-radius: 5px;
            overflow-x: auto;
            font-size: 10pt;
        }}
        pre code {{
            background: none;
            padding: 0;
        }}
        table {{
            border-collapse: collapse;
            width: 100%;
            margin: 1em 0;
            font-size: 11pt;
        }}
        th, td {{
            border: 1px solid #999;
            padding: 0.5em 0.8em;
            text-align: left;
        }}
        th {{
            background: #f0f0f0;
            font-weight: bold;
        }}
        a {{
            color: #1a5276;
            text-decoration: none;
        }}
        a:hover {{
            text-decoration: underline;
        }}
        .references {{
            font-size: 10pt;
            line-height: 1.6;
        }}
        .references p {{
            text-indent: -2em;
            padding-left: 2em;
            margin-bottom: 0.4em;
        }}
        .abstract {{
            background: #f9f9f9;
            padding: 1em 1.5em;
            margin: 1em 0 2em 0;
            border-left: 4px solid #1a5276;
        }}
        .abstract p {{
            text-indent: 0;
        }}
        .keywords {{
            font-style: italic;
            color: #555;
        }}
    </style>
</head>
<body>
{htmlBody}
</body>
</html>"

            ' 写入HTML文件
            Dim filePath = Path.Combine(_outputDir, output_filename)
            File.WriteAllText(filePath, html, Encoding.UTF8)

            Return $"{{""success"": true, ""html_path"": ""{EscapeJson(filePath.Replace("\", "/"))}"", ""file_size"": {New FileInfo(filePath).Length}}}"
        Catch ex As Exception
            Return $"{{""error"": ""Markdown to HTML conversion failed: {EscapeJson(ex.Message)}""}}"
        End Try
    End Function

    ''' <summary>
    ''' 将HTML文件转换为PDF文件
    ''' </summary>
    ''' <param name="html_file_path">输入的HTML文件路径</param>
    ''' <param name="output_pdf_filename">输出的PDF文件名（不含路径）</param>
    ''' <returns>JSON格式的转换结果，包含PDF文件路径</returns>
    <Description("Convert an HTML file to a PDF document. The HTML file should be generated by markdown_to_html. Returns the file path of the generated PDF.")>
    Public Function html_to_pdf(
        <Argument("html_file_path", Description:="Path to the HTML file to convert")> html_file_path As String,
        <Argument("output_pdf_filename", Description:="Output PDF filename (e.g. 'research_review.pdf')")> output_pdf_filename As String
    ) As String
        Try
            If Not File.Exists(html_file_path) Then
                Return $"{{""error"": ""HTML file not found: {EscapeJson(html_file_path)}""}}"
            End If

            Dim pdfPath = Path.Combine(_outputDir, output_pdf_filename)

            ' 使用系统命令行工具进行HTML到PDF的转换
            ' 优先尝试wkhtmltopdf，如果不可用则尝试Chrome/Chromium headless模式
            Dim success = False
            Dim errorMsg = ""

            ' 方法1: 尝试使用wkhtmltopdf
            success = TryConvertWithWkHtmlToPdf(html_file_path, pdfPath, errorMsg)

            ' 方法2: 如果wkhtmltopdf不可用，尝试使用Chrome headless
            If Not success Then
                success = TryConvertWithChromeHeadless(html_file_path, pdfPath, errorMsg)
            End If

            If success AndAlso File.Exists(pdfPath) Then
                Return $"{{""success"": true, ""pdf_path"": ""{EscapeJson(pdfPath.Replace("\", "/"))}"", ""file_size"": {New FileInfo(pdfPath).Length}}}"
            Else
                Return $"{{""error"": ""HTML to PDF conversion failed: {EscapeJson(errorMsg)}""}}"
            End If
        Catch ex As Exception
            Return $"{{""error"": ""HTML to PDF conversion failed: {EscapeJson(ex.Message)}""}}"
        End Try
    End Function

    ' ---- 辅助方法 ----

    ''' <summary>
    ''' 简易Markdown转HTML转换器，支持常用Markdown语法
    ''' </summary>
    Private Function SimpleMarkdownToHtml(markdown As String) As String
        Dim html = markdown

        ' 转义HTML特殊字符（保留后续Markdown语法处理）
        html = html.Replace("&", "&amp;")

        ' 处理代码块（```...```）- 需在行内代码之前处理
        Dim codeBlockRegex = New Regex("```(\w*)\n(.*?)```", RegexOptions.Singleline)
        html = codeBlockRegex.Replace(html, Function(m)
                                                Dim lang = m.Groups(1).Value
                                                Dim code = m.Groups(2).Value
                                                Return $"<pre><code class=""language-{lang}"">{code}</code></pre>"
                                            End Function)

        ' 处理行内代码（`...`）
        html = Regex.Replace(html, "`([^`]+)`", "<code>$1</code>")

        ' 处理表格
        html = ConvertMarkdownTables(html)

        ' 处理标题（# ~ ####）
        html = Regex.Replace(html, "^####\s+(.+)$", "<h4>$1</h4>", RegexOptions.Multiline)
        html = Regex.Replace(html, "^###\s+(.+)$", "<h3>$1</h3>", RegexOptions.Multiline)
        html = Regex.Replace(html, "^##\s+(.+)$", "<h2>$1</h2>", RegexOptions.Multiline)
        html = Regex.Replace(html, "^#\s+(.+)$", "<h1>$1</h1>", RegexOptions.Multiline)

        ' 处理引用块（> ...）
        html = Regex.Replace(html, "^>\s+(.+)$", "<blockquote><p>$1</p></blockquote>", RegexOptions.Multiline)

        ' 处理粗体和斜体
        html = Regex.Replace(html, "\*\*\*(.+?)\*\*\*", "<strong><em>$1</em></strong>")
        html = Regex.Replace(html, "\*\*(.+?)\*\*", "<strong>$1</strong>")
        html = Regex.Replace(html, "\*(.+?)\*", "<em>$1</em>")

        ' 处理无序列表
        html = Regex.Replace(html, "^[*\-+]\s+(.+)$", "<li>$1</li>", RegexOptions.Multiline)
        html = Regex.Replace(html, "(<li>.*?</li>(\n<li>.*?</li>)*)", "<ul>$1</ul>")

        ' 处理有序列表
        html = Regex.Replace(html, "^\d+\.\s+(.+)$", "<li>$1</li>", RegexOptions.Multiline)

        ' 处理水平线
        html = Regex.Replace(html, "^---+$", "<hr/>", RegexOptions.Multiline)

        ' 处理链接
        html = Regex.Replace(html, "\[(.+?)\]\((.+?)\)", "<a href=""$2"">$1</a>")

        ' 处理段落：将连续的文本行包裹在<p>标签中
        Dim lines = html.Split({vbLf}, StringSplitOptions.None)
        Dim result As New StringBuilder()
        Dim inParagraph = False

        For Each line In lines
            Dim trimmed = line.Trim()
            If String.IsNullOrEmpty(trimmed) Then
                If inParagraph Then
                    result.AppendLine("</p>")
                    inParagraph = False
                End If
            ElseIf trimmed.StartsWith("<h") OrElse trimmed.StartsWith("<ul") OrElse
                   trimmed.StartsWith("<ol") OrElse trimmed.StartsWith("<li") OrElse
                   trimmed.StartsWith("<blockquote") OrElse trimmed.StartsWith("<pre") OrElse
                   trimmed.StartsWith("<table") OrElse trimmed.StartsWith("<hr") Then
                If inParagraph Then
                    result.AppendLine("</p>")
                    inParagraph = False
                End If
                result.AppendLine(line)
            Else
                If Not inParagraph Then
                    result.Append("<p>")
                    inParagraph = True
                    result.Append(trimmed)
                Else
                    result.Append(" " & trimmed)
                End If
            End If
        Next

        If inParagraph Then
            result.Append("</p>")
        End If

        Return result.ToString()
    End Function

    ''' <summary>
    ''' 转换Markdown表格语法为HTML表格
    ''' </summary>
    Private Function ConvertMarkdownTables(html As String) As String
        Dim tableRegex = New Regex("(\|.+\|\n)((\|[-:]+)\|\n)((\|.+\|\n?)+)", RegexOptions.Multiline)
        Return tableRegex.Replace(html, Function(m)
                                            Dim headerLine = m.Groups(1).Value
                                            Dim separatorLine = m.Groups(3).Value
                                            Dim bodyLines = m.Groups(4).Value

                                            Dim sb As New StringBuilder()
                                            sb.AppendLine("<table>")
                                            sb.AppendLine("<thead><tr>")

                                            ' 解析表头
                                            Dim headers = headerLine.Split("|"c).
                                                                     Where(Function(h) Not String.IsNullOrWhiteSpace(h)).
                                                                     Select(Function(h) h.Trim()).ToArray()
                                            For Each h In headers
                                                sb.AppendLine($"<th>{h}</th>")
                                            Next
                                            sb.AppendLine("</tr></thead>")

                                            ' 解析表体
                                            sb.AppendLine("<tbody>")
                                            For Each bodyLine In bodyLines.Split({vbLf}, StringSplitOptions.RemoveEmptyEntries)
                                                Dim cells = bodyLine.Split("|"c).
                                                                     Where(Function(c) Not String.IsNullOrWhiteSpace(c)).
                                                                     Select(Function(c) c.Trim()).ToArray()
                                                sb.AppendLine("<tr>")
                                                For Each c In cells
                                                    sb.AppendLine($"<td>{c}</td>")
                                                Next
                                                sb.AppendLine("</tr>")
                                            Next
                                            sb.AppendLine("</tbody></table>")

                                            Return sb.ToString()
                                        End Function)
    End Function

    ''' <summary>
    ''' 尝试使用wkhtmltopdf进行HTML到PDF转换
    ''' </summary>
    Private Function TryConvertWithWkHtmlToPdf(htmlPath As String, pdfPath As String, ByRef errorMsg As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = "wkhtmltopdf"
            psi.Arguments = $"--page-size A4 --margin-top 25mm --margin-bottom 25mm --margin-left 25mm --margin-right 25mm " &
                            $"--encoding UTF-8 --enable-local-file-access ""{htmlPath}"" ""{pdfPath}"""
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            Using proc = Process.Start(psi)
                If proc Is Nothing Then
                    errorMsg = "Could not start wkhtmltopdf process."
                    Return False
                End If
                proc.WaitForExit(60000) ' 60秒超时
                If proc.ExitCode = 0 AndAlso File.Exists(pdfPath) Then
                    Return True
                Else
                    errorMsg = $"wkhtmltopdf exited with code {proc.ExitCode}: {proc.StandardError.ReadToEnd()}"
                    Return False
                End If
            End Using
        Catch ex As Exception
            errorMsg = $"wkhtmltopdf not available: {ex.Message}"
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 尝试使用Chrome/Chromium headless模式进行HTML到PDF转换
    ''' </summary>
    Private Function TryConvertWithChromeHeadless(htmlPath As String, pdfPath As String, ByRef errorMsg As String) As Boolean
        Try
            ' 尝试常见的Chrome/Chromium可执行文件路径
            Dim chromePaths = {
                "google-chrome",
                "chromium-browser",
                "chromium",
                "C:\Program Files\Google\Chrome\Application\chrome.exe",
                "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            }

            Dim chromePath = chromePaths.FirstOrDefault(Function(p)
                                                            Try
                                                                If File.Exists(p) Then Return True
                                                                Dim psi As New ProcessStartInfo()
                                                                psi.FileName = p
                                                                psi.Arguments = "--version"
                                                                psi.UseShellExecute = False
                                                                psi.RedirectStandardOutput = True
                                                                psi.CreateNoWindow = True
                                                                Using proc = Process.Start(psi)
                                                                    If proc IsNot Nothing Then
                                                                        proc.WaitForExit(5000)
                                                                        Return proc.ExitCode = 0
                                                                    End If
                                                                End Using
                                                            Catch
                                                            End Try
                                                            Return False
                                                        End Function)

            If String.IsNullOrEmpty(chromePath) Then
                errorMsg = "Neither wkhtmltopdf nor Chrome/Chromium found for PDF conversion."
                Return False
            End If

            Dim psi As New ProcessStartInfo()
            psi.FileName = chromePath
            psi.Arguments = $"--headless --disable-gpu --no-sandbox --print-to-pdf=""{pdfPath}"" ""file:///{htmlPath.Replace("\", "/")}"""
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            Using proc = Process.Start(psi)
                If proc Is Nothing Then
                    errorMsg = "Could not start Chrome process."
                    Return False
                End If
                proc.WaitForExit(60000)
                If File.Exists(pdfPath) Then
                    Return True
                Else
                    errorMsg = $"Chrome headless did not produce PDF output."
                    Return False
                End If
            End Using
        Catch ex As Exception
            errorMsg = $"Chrome headless conversion failed: {ex.Message}"
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 转义JSON字符串中的特殊字符
    ''' </summary>
    Private Function EscapeJson(input As String) As String
        If String.IsNullOrEmpty(input) Then Return ""
        Return input.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, "\r").Replace(vbLf, "\n").Replace(vbTab, "\t")
    End Function

End Class

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
Public Class ResearchAgent
    Implements IDisposable

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

    Private ReadOnly _ollama As Ollama
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
    Public Sub New(ollama As Ollama,
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
                If isResearchComplete(summary) Then
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

' ============================================================================
' Agent配置类 - 用于简化Agent的创建和配置
' ============================================================================

''' <summary>
''' 科研文献调研Agent的配置选项
''' </summary>
Public Class ResearchAgentConfig

    ''' <summary>
    ''' Ollama服务端点URL（默认：http://localhost:11434）
    ''' </summary>
    Public Property OllamaEndpoint As String = "http://localhost:11434"

    ''' <summary>
    ''' 使用的大语言模型名称（默认：llama3）
    ''' </summary>
    Public Property ModelName As String = "llama3"

    ''' <summary>
    ''' PubMed MySQL镜像数据库连接字符串
    ''' </summary>
    Public Property DatabaseConnectionString As String = "Server=localhost;Database=pubmed;Uid=root;Pwd=password;Charset=utf8mb4;"

    ''' <summary>
    ''' 输出文件目录路径
    ''' </summary>
    Public Property OutputDirectory As String = "./research_output"

    ''' <summary>
    ''' 最大迭代调研轮次（默认5轮）
    ''' </summary>
    Public Property MaxRounds As Integer = 5

    ''' <summary>
    ''' 每次数据库查询返回的最大文献数（默认20篇）
    ''' </summary>
    Public Property PapersPerQuery As Integer = 20

    ''' <summary>
    ''' 是否在控制台输出详细的调试日志（默认True）
    ''' </summary>
    Public Property VerboseLogging As Boolean = True

End Class

' ============================================================================
' 工厂类 - 简化Agent实例的创建
' ============================================================================

''' <summary>
''' 科研文献调研Agent工厂类，提供便捷的Agent创建方法
''' </summary>
Public Module ResearchAgentFactory

    ''' <summary>
    ''' 根据配置创建并初始化一个科研文献调研Agent实例
    ''' </summary>
    ''' <param name="config">Agent配置选项</param>
    ''' <returns>已初始化的ResearchAgent实例</returns>
    Public Function CreateAgent(config As ResearchAgentConfig) As ResearchAgent
        ' 创建Ollama客户端实例
        Dim ollama As New Ollama()

        ' 创建ResearchAgent实例
        Dim agent As New ResearchAgent(
            ollama,
            config.DatabaseConnectionString,
            config.OutputDirectory,
            config.MaxRounds,
            config.PapersPerQuery
        )

        ' 初始化Agent（注册函数工具）
        agent.Initialize()

        Return agent
    End Function

    ''' <summary>
    ''' 使用默认配置创建并初始化一个科研文献调研Agent实例
    ''' </summary>
    ''' <param name="dbConnectionString">PubMed数据库连接字符串</param>
    ''' <param name="outputDir">输出目录路径</param>
    ''' <returns>已初始化的ResearchAgent实例</returns>
    Public Function CreateAgent(dbConnectionString As String, outputDir As String) As ResearchAgent
        Dim config As New ResearchAgentConfig() With {
            .DatabaseConnectionString = dbConnectionString,
            .OutputDirectory = outputDir
        }
        Return CreateAgent(config)
    End Function

End Module

' ============================================================================
' 使用示例 - 主程序入口
' ============================================================================

''' <summary>
''' 科研文献调研Agent使用示例
''' </summary>
Public Class ResearchAgentExample

    ''' <summary>
    ''' 主程序入口 - 演示如何使用ResearchAgent进行文献调研
    ''' </summary>
    Public Shared Async Sub Main()

        ' ---- 步骤1：配置Agent参数 ----
        Dim config As New ResearchAgentConfig() With {
            .OllamaEndpoint = "http://localhost:11434",
            .ModelName = "llama3",
            .DatabaseConnectionString = "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;",
            .OutputDirectory = "./research_output",
            .MaxRounds = 5,
            .PapersPerQuery = 20,
            .VerboseLogging = True
        }

        ' ---- 步骤2：创建Agent实例 ----
        Console.WriteLine("正在创建科研文献调研Agent...")
        Dim agent = ResearchAgentFactory.CreateAgent(config)

        ' ---- 步骤3：执行文献调研 ----
        Try
            Dim topic = "CRISPR-Cas9基因编辑技术在遗传疾病治疗中的应用与挑战"
            Console.WriteLine($"开始文献调研，主题：{topic}")
            Console.WriteLine(New String("="c, 60))

            Dim result = Await agent.ConductResearchAsync(topic)

            ' ---- 步骤4：输出结果 ----
            Console.WriteLine(New String("="c, 60))
            Console.WriteLine("文献调研完成！")
            Console.WriteLine($"  - 收集文献总数：{result.AllPapers.Count} 篇")
            Console.WriteLine($"  - 调研迭代轮次：{result.Rounds.Count} 轮")
            Console.WriteLine($"  - Markdown文件：{result.OutputMarkdownPath}")
            Console.WriteLine($"  - PDF文件：{result.OutputPdfPath}")

            ' 输出每轮调研的简要信息
            For Each round In result.Rounds
                Console.WriteLine($"  - 第{round.RoundNumber}轮：关键词[{String.Join(", ", round.Keywords)}]，" &
                                  $"查询到{round.Papers.Count}篇新文献")
            Next

        Catch ex As Exception
            Console.WriteLine($"文献调研过程中发生错误：{ex.Message}")
            Console.WriteLine(ex.StackTrace)
        Finally
            agent.Dispose()
        End Try

        Console.WriteLine("按任意键退出...")
        Console.ReadKey()

    End Sub

    ''' <summary>
    ''' 高级使用示例 - 自定义函数工具注册
    ''' </summary>
    Public Shared Async Sub AdvancedExample()

        ' 创建Ollama客户端
        Dim ollama As New Ollama()

        ' 创建自定义的PubMed查询工具（可自定义SQL查询逻辑）
        Dim pubmedTool As New PubMedQueryTool(
            "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;"
        )

        ' 创建文档转换工具
        Dim converterTool As New DocumentConverterTool("./research_output")

        ' 手动注册函数工具（可选择只注册部分工具）
        ollama.AddFunction(pubmedTool, fun:=NameOf(PubMedQueryTool.search_papers))
        ollama.AddFunction(pubmedTool, fun:=NameOf(PubMedQueryTool.get_full_text))
        ollama.AddFunction(converterTool, fun:=NameOf(DocumentConverterTool.markdown_to_html))
        ollama.AddFunction(converterTool, fun:=NameOf(DocumentConverterTool.html_to_pdf))

        ' 创建Agent（不使用工厂方法，手动控制初始化过程）
        Dim agent As New ResearchAgent(
            ollama,
            "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;",
            "./research_output",
            maxRounds:=3,
            papersPerQuery:=15
        )

        agent.Initialize()

        ' 执行调研
        Dim result = Await agent.ConductResearchAsync("深度学习在蛋白质结构预测中的应用")

        ' 处理结果
        If Not String.IsNullOrEmpty(result.OutputPdfPath) Then
            Console.WriteLine($"PDF报告已生成：{result.OutputPdfPath}")
        End If

        agent.Dispose()

    End Sub

End Class
