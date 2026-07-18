' ============================================================================
' 数据模型定义
' ============================================================================

Imports System.ComponentModel
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

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
