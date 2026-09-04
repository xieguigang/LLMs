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
    ''' <returns></returns>
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
    ''' <returns></returns>
    Public Const PreviewMaxChars As Integer = 200000

    Public Overridable Function Available() As Boolean
        Return path.FileExists
    End Function

    Public Overridable Async Function ResolveFileText() As Task(Of String)
        Return Await Task.FromResult(path.ReadAllText)
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

    Public Overrides Function Available() As Boolean
        Return memoryHandle IsNot Nothing
    End Function

    Public Overrides Async Function ResolveFileText() As Task(Of String)
        Return Await memoryHandle()
    End Function

End Class