Imports Ollama

Public Class FormLLMUI

    Private Sub FormLLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        ' bind an LLM backend to the chat control. change the model name to whatever
        ' model you have pulled into your local Ollama server (default port 11434).
        Dim client As New LLMClient With {
            .model = "deepseek-r1:8b",
            .temperature = 0.6
        }

        WebView2llmui1.SetHost(client)
    End Sub

End Class
