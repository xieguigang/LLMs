Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Public Class FileTool

    <Description("读取工作区内文件的全部文本内容，以字符串形式返回。请勿使用此函数读取大型文本/csv 文件的全部内容")>
    Public Function read_file(<Argument("path", Description:="文件路径，相对于工作区根目录或绝对路径")> path As String) As String
        Try
            Dim err As String = Nothing

            If Not File.Exists(path) Then
                Return $"error on file not found: {path}"
            End If

            Dim content = File.ReadAllText(path, Encoding.UTF8)
            Return content
        Catch ex As Exception
            Return "read_file_error: " & ex.Message
        End Try
    End Function
End Class