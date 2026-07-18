' ============================================================================ 
' 数据模型定义 
' ============================================================================ 
Imports System.ComponentModel
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports MySql.Data.MySqlClient

' ============================================================================ 
' 函数工具类 - 注册到Ollama供LLM调用的工具 
' ============================================================================ 
''' <summary> 
''' PubMed数据库查询工具类，提供从本地NCBI PubMed MySQL镜像数据库中查询文献的函数 
''' </summary> 
''' <remarks> 
''' 此类中的公开方法将通过Ollama.AddFunction注册为大语言模型的函数调用工具， 
''' 使LLM能够自主构建查询条件并从PubMed数据库中检索文献。 
''' 
''' 新版数据库结构（pubmed schema）说明： 
''' - articles  表：文献基本信息（id=PMID, authors, title, journal, doi, year） 
''' - fulltext  表：文献摘要与全文（id=PMID, abstract, fulltext） 
''' - mesh      表：MeSH 主题词主表（id, mesh_id, term, category, ...） 
''' - metadata  表：文献与 MeSH 主题词的关联表（pubmed_id, mesh_id） 
''' 
''' 与旧版（pubmed_mirror.pubmed_articles 单表）的主要差异： 
''' 1. 文献信息拆分为 articles + fulltext 两张表，通过 id（PMID）一对一关联 
''' 2. 不再有 keywords 列；MeSH 主题词从扁平字符串改为 mesh + metadata 规范化结构 
''' 3. PMID 字段类型由 VARCHAR(16) 改为 INT UNSIGNED 
''' 4. 年份字段名由 pub_year 改为 year 
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

    ' ======================================================================== 
    ' 1. search_papers — 关键词检索文献 
    ' ======================================================================== 

    ''' <summary> 
    ''' 根据关键词从PubMed数据库中查询文献记录 
    ''' </summary> 
    ''' <param name="keywords">搜索关键词，多个关键词以空格或逗号分隔</param> 
    ''' <param name="year_from">发表年份起始范围（可选，默认不限制）</param> 
    ''' <param name="year_to">发表年份结束范围（可选，默认不限制）</param> 
    ''' <param name="max_results">返回结果的最大数量（默认20，最大100）</param> 
    ''' <returns>JSON格式的文献查询结果列表</returns> 
    <Description("Search PubMed database for research papers by keywords. Returns a JSON array of paper records including PMID, title, authors, journal, year, abstract, doi, and MeSH terms. Use this to find relevant scientific literature on a research topic.")>
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

            ' 解析关键词 
            Dim keywordList = keywords.Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(k) k.Trim().ToLower()).
                Where(Function(k) k.Length > 0).
                ToList()

            If keywordList.Count = 0 Then
                Return "{""error"": ""No valid keywords provided.""}"
            End If

            ' ---------------------------------------------------------------- 
            ' 构建 SQL 查询语句 
            ' ---------------------------------------------------------------- 
            ' 新版结构需要 JOIN articles + fulltext 两个表； 
            ' MeSH 主题词通过 metadata + mesh 关联表获取， 
            ' 使用相关子查询 + GROUP_CONCAT 聚合为分号分隔的字符串。 
            ' 
            ' 关键词搜索范围： 
            '   - articles.title        （标题） 
            '   - fulltext.abstract     （摘要） 
            '   - fulltext.`fulltext`   （全文） 
            '   - mesh.term             （MeSH 主题词，通过 EXISTS 子查询匹配） 
            ' 
            ' 多个关键词之间以 AND 组合，确保结果与所有关键词相关。 
            ' ---------------------------------------------------------------- 

            Dim sql As New StringBuilder()
            sql.AppendLine("SELECT a.id AS pmid, a.title, a.authors, a.journal, a.year AS pub_year,")
            sql.AppendLine("       f.abstract, a.doi,")
            sql.AppendLine("       (SELECT GROUP_CONCAT(DISTINCT m.term SEPARATOR '; ')")
            sql.AppendLine("        FROM metadata md")
            sql.AppendLine("        JOIN mesh m ON md.mesh_id = m.id")
            sql.AppendLine("        WHERE md.pubmed_id = a.id) AS mesh_terms")
            sql.AppendLine("FROM articles a")
            sql.AppendLine("LEFT JOIN `fulltext` f ON a.id = f.id")
            sql.AppendLine("WHERE 1=1")

            ' 构建全文搜索条件：每个关键词需匹配 标题 / 摘要 / 全文 / MeSH词 中的任意一个 
            Dim conditions As New List(Of String)()
            For Each kw In keywordList
                Dim cond = $"(LOWER(a.title) LIKE '%{EscapeSql(kw)}%' " &
                           $"OR LOWER(f.abstract) LIKE '%{EscapeSql(kw)}%' " &
                           $"OR LOWER(f.`fulltext`) LIKE '%{EscapeSql(kw)}%' " &
                           $"OR EXISTS (SELECT 1 FROM metadata md2 " &
                           $"           JOIN mesh m2 ON md2.mesh_id = m2.id " &
                           $"           WHERE md2.pubmed_id = a.id " &
                           $"           AND LOWER(m2.term) LIKE '%{EscapeSql(kw)}%'))"
                conditions.Add(cond)
            Next

            ' 所有关键词条件以 AND 组合 
            sql.AppendLine($" AND ({String.Join(" AND ", conditions)})")

            ' 年份范围过滤 
            If year_from > 0 Then
                sql.AppendLine($" AND a.year >= {year_from}")
            End If
            If year_to > 0 Then
                sql.AppendLine($" AND a.year <= {year_to}")
            End If

            ' 按发表年份降序排列，优先返回最新文献 
            sql.AppendLine("ORDER BY a.year DESC, a.id DESC")
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

    ' ======================================================================== 
    ' 2. get_full_text — 根据 PMID 获取全文 
    ' ======================================================================== 

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

            ' ---------------------------------------------------------------- 
            ' 查询 articles + fulltext 两张表，并通过子查询获取 MeSH 主题词 
            ' ---------------------------------------------------------------- 
            Dim sql = "SELECT a.id AS pmid, a.title, a.authors, a.journal, a.year AS pub_year, " &
                      "f.abstract, f.`fulltext`, a.doi, " &
                      "(SELECT GROUP_CONCAT(DISTINCT m.term SEPARATOR '; ') " &
                      " FROM metadata md " &
                      " JOIN mesh m ON md.mesh_id = m.id " &
                      " WHERE md.pubmed_id = a.id) AS mesh_terms " &
                      "FROM articles a " &
                      "LEFT JOIN `fulltext` f ON a.id = f.id " &
                      "WHERE a.id = @pmid"

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
                                {"full_text", If(reader("fulltext")?.ToString(), "")},
                                {"doi", If(reader("doi")?.ToString(), "")},
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

    ' ======================================================================== 
    ' 3. get_database_stats — 数据库统计信息 
    ' ======================================================================== 

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
                ' ------------------------------------------------------------ 
                ' 带关键词过滤的统计：需要 JOIN fulltext 表并在 
                ' title / abstract / fulltext / mesh.term 中搜索 
                ' 使用 COUNT(DISTINCT a.id) 避免因 JOIN 产生重复计数 
                ' ------------------------------------------------------------ 
                Dim keywordList = keywords.Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries).
                    Select(Function(k) k.Trim().ToLower()).ToList()

                Dim conditions As New List(Of String)()
                For Each kw In keywordList
                    Dim cond = $"(LOWER(a.title) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR LOWER(f.abstract) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR LOWER(f.`fulltext`) LIKE '%{EscapeSql(kw)}%' " &
                               $"OR EXISTS (SELECT 1 FROM metadata md2 " &
                               $"           JOIN mesh m2 ON md2.mesh_id = m2.id " &
                               $"           WHERE md2.pubmed_id = a.id " &
                               $"           AND LOWER(m2.term) LIKE '%{EscapeSql(kw)}%'))"
                    conditions.Add(cond)
                Next

                sql.AppendLine("SELECT a.year AS pub_year, COUNT(DISTINCT a.id) AS cnt")
                sql.AppendLine("FROM articles a")
                sql.AppendLine("LEFT JOIN `fulltext` f ON a.id = f.id")
                sql.AppendLine($"WHERE {String.Join(" AND ", conditions)}")
                sql.AppendLine("GROUP BY a.year ORDER BY a.year DESC LIMIT 50")
            Else
                ' ------------------------------------------------------------ 
                ' 无关键词：直接对 articles 表按年份分组统计 
                ' ------------------------------------------------------------ 
                sql.AppendLine("SELECT year AS pub_year, COUNT(*) AS cnt")
                sql.AppendLine("FROM articles")
                sql.AppendLine("GROUP BY year ORDER BY year DESC LIMIT 50")
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

    ' ======================================================================== 
    ' 辅助方法 
    ' ======================================================================== 

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
