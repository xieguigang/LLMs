
Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.MIME.text.markdown

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
