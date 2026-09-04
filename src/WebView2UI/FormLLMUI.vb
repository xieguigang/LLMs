Imports System.Diagnostics
Imports Ollama

Public Class FormLLMUI

    Private Sub FormLLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        ' bind an LLM backend to the chat control. change the model name to whatever
        ' model you have pulled into your local Ollama server (default port 11434).
        Dim client As New LLMClient(New OllamaProvider(), "deepseek-r1:8b") With {
            .temperature = 0.6
        }

        WebView2llmui1.SetHost(client)

        ' 可选：接管网页端点击附件文件名时的文件查看行为。
        ' 不设置 e.Handled 时，控件会回退为使用系统默认程序打开该磁盘文件。
        AddHandler WebView2llmui1.ViewFileRequested, AddressOf OnViewFileRequested
    End Sub

    ''' <summary>
    ''' 演示如何接管文件查看请求：这里简单地用记事本打开被点击的文件，
    ''' 实际应用可以替换为在自己的编辑器/查看器中打开。
    ''' </summary>
    Private Sub OnViewFileRequested(sender As Object, e As FileViewRequestEventArgs)
        If Not e.File.onDisk OrElse Not e.File.Available() Then
            ' 内存数据引用交给控件的默认行为（推送文本内容到网页模态框）处理
            Return
        End If

        Try
            Dim psi As New ProcessStartInfo("notepad.exe") With {.UseShellExecute = True}

            psi.ArgumentList.Add(e.File.path)

            Call Process.Start(psi)

            e.Handled = True
        Catch ex As Exception
            ' 打开失败时保持 Handled = False，交回控件执行默认的打开行为
        End Try
    End Sub

End Class
