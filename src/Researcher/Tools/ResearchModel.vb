
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
