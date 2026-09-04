Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.Net.Protocols.ContentTypes

''' <summary>
''' 会话上下文中的一个文件附件引用
''' </summary>
Public Class FileReference

    ''' <summary>
    ''' 网页界面与宿主程序之间使用的稳定唯一标识。
    ''' 该标识与写进提示词 XML 中的 <see cref="id"/> 相互独立，不受文件路径变化的影响，
    ''' 主要用于网页端删除/查看某个具体附件时的定位。
    ''' </summary>
    Public ReadOnly Property uid As String = Guid.NewGuid().ToString("N")

    Public Property path As String

    Public Overridable ReadOnly Property size As String
        Get
            Return StringFormats.Lanudry(path.FileLength)
        End Get
    End Property

    Public ReadOnly Property type As ContentType
        Get
            Return MIME.FileMimeType(path)
        End Get
    End Property

    Public Overridable ReadOnly Property id As String
        Get
            Return path.GetHashCode.ToString
        End Get
    End Property

    ''' <summary>
    ''' 当前引用是否指向磁盘上的真实文件；内存数据引用（<see cref="MemoryReference"/>）返回 False，
    ''' 此时无法通过系统默认程序打开，只能以文本形式预览。
    ''' </summary>
    ''' <returns></returns>
    Public Overridable ReadOnly Property onDisk As Boolean
        Get
            Return True
        End Get
    End Property

    ''' <summary>
    ''' 界面预览时允许读取的最大字符数，避免把超大文件整体读进内存
    ''' </summary>
    Public Const PreviewMaxChars As Integer = 200000

    Public Overridable Function Available() As Boolean
        Return path.FileExists
    End Function

    Public Overridable Async Function ResolveFileText() As Task(Of String)
        Return Await Task.FromResult(path.ReadAllText)
    End Function

    ''' <summary>
    ''' 读取用于界面预览的文本内容：为避免把超大文件整体读进内存，超过 
    ''' <paramref name="maxChars"/> 时只读取文件头部并追加一行截断提示。
    ''' </summary>
    ''' <param name="maxChars">预览所允许的最大字符数</param>
    ''' <returns></returns>
    Public Overridable Async Function ReadPreviewText(Optional maxChars As Integer = PreviewMaxChars) As Task(Of String)
        Return Await Task.Run(Function() ReadTextHead(maxChars))
    End Function

    Private Function ReadTextHead(maxChars As Integer) As String
        If maxChars <= 0 Then
            maxChars = PreviewMaxChars
        End If

        Using reader As New StreamReader(New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, True)
            Dim buffer As Char() = New Char(maxChars - 1) {}
            Dim n As Integer = reader.Read(buffer, 0, maxChars)
            Dim head As String = New String(buffer, 0, n)

            ' 只有文件确实还有剩余内容时才提示截断（正好读完时不提示）
            If n >= maxChars AndAlso reader.Peek() >= 0 Then
                head = head & vbCrLf & $"... (内容过长，仅预览前 {maxChars} 个字符)"
            End If

            Return head
        End Using
    End Function

    Public Async Function GetFileContent(topN As Integer) As Task(Of String)
        Dim sb As New StringBuilder
        Dim lines As String() = (Await ResolveFileText()).LineTokens
        Dim len As Integer = lines.Length

        Call sb.AppendLine($"<attached-file id=""{id}"" name=""{path.FileName}"" path=""{path.GetFullPath}"" type=""{type.MIMEType}"">")

        If len > topN Then
            Call sb.AppendLine($"--- 文件大小： {size}")
            Call sb.AppendLine($"--- 文件行数：{len} (在这里预览前{topN}行数据，如果需要读取文件全部数据，可以使用read_file函数来读取)")
        End If

        Call sb.AppendLine(lines.Take(topN).JoinBy(vbCrLf))
        Call sb.AppendLine("</attached-file>")

        Return sb.ToString
    End Function

End Class

Public Class MemoryReference : Inherits FileReference

    Dim memoryHandle As Func(Of Task(Of String))

    Public Overrides ReadOnly Property size As String
        Get
            Return "[#in-memory-data]"
        End Get
    End Property

    Public Overrides ReadOnly Property id As String
        Get
            Return memoryHandle.GetHashCode.ToString
        End Get
    End Property

    Sub New(handle As Func(Of Task(Of String)), filename As String)
        memoryHandle = handle
        path = filename
    End Sub

    Public Overrides ReadOnly Property onDisk As Boolean
        Get
            Return False
        End Get
    End Property

    Public Overrides Function Available() As Boolean
        Return memoryHandle IsNot Nothing
    End Function

    Public Overrides Async Function ResolveFileText() As Task(Of String)
        Return Await memoryHandle()
    End Function

    Public Overrides Async Function ReadPreviewText(Optional maxChars As Integer = PreviewMaxChars) As Task(Of String)
        Dim text As String = Await ResolveFileText()

        If maxChars <= 0 Then
            maxChars = PreviewMaxChars
        End If
        If text Is Nothing Then
            Return ""
        ElseIf text.Length > maxChars Then
            Return text.Substring(0, maxChars) & vbCrLf & $"... (内容过长，仅预览前 {maxChars} 个字符)"
        Else
            Return text
        End If
    End Function

End Class