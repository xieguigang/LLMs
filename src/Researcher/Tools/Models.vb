' ============================================================================
' 数据模型定义
' ============================================================================

Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.MIME.text.markdown

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
            Dim htmlBody = New MarkdownRender().Transform(markdown_content)

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

            Dim chromePath = chromePaths _
                .Where(Function(p)
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
                       End Function) _
                .FirstOrDefault

            If String.IsNullOrEmpty(chromePath) Then
                errorMsg = "Neither wkhtmltopdf nor Chrome/Chromium found for PDF conversion."
                Return False
            Else
                Return ChromePDFConvert(chromePath, htmlPath, pdfPath, errorMsg)
            End If
        Catch ex As Exception
            errorMsg = $"Chrome headless conversion failed: {ex.Message}"
            Return False
        End Try
    End Function

    Private Function ChromePDFConvert(chromePath As String, htmlPath As String, pdfPath As String, ByRef errorMsg As String) As Boolean
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
    End Function

    ''' <summary>
    ''' 转义JSON字符串中的特殊字符
    ''' </summary>
    Private Function EscapeJson(input As String) As String
        If String.IsNullOrEmpty(input) Then Return ""
        Return input.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, "\r").Replace(vbLf, "\n").Replace(vbTab, "\t")
    End Function

End Class
