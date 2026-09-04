Imports System.Text
Imports Microsoft.VisualBasic.Net.Protocols.ContentTypes

Public Class FileReference

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

        Call sb.AppendLine($"<attached-file name=""{path.FileName}"" path=""{path.GetFullPath}"" type=""{type.MIMEType}"">")

        If len > topN Then
            Call sb.AppendLine($"--- 文件行数：{len} (在这里预览前{topN}行数据，如果需要读取文件全部数据，可以使用read_file函数来读取)")
        End If

        Call sb.AppendLine(lines.Take(topN).JoinBy(vbCrLf))
        Call sb.AppendLine("</attached-file>")

        Return sb.ToString
    End Function

End Class

Public Class MemoryReference : Inherits FileReference


End Class