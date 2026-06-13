
Imports System.Text

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
