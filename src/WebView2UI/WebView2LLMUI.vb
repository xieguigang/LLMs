Imports System.Text.Json

Public Class WebView2LLMUI

    ''' <summary>
    ''' Push LLM think token from host to web html ui via message
    ''' </summary>
    ''' <param name="token"></param>
    Public Sub PushThinkToken(token As String)
        Call SendMessage(New With {
            .action = "push_token",
            .text = token,
            .type = "think"
        })
    End Sub

    ''' <summary>
    ''' Push LLM output token from host to web html ui via message
    ''' </summary>
    ''' <param name="token"></param>
    Public Sub PushOutputToken(token As String)
        Call SendMessage(New With {
            .action = "push_token",
            .text = token,
            .type = "output"
        })
    End Sub

    Private Sub SendMessage(Of T As Class)(payload As T)
        WebView21.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload))
    End Sub

    Private Async Sub WebView2LLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        Await WebViewLoader.Init(WebView21)
    End Sub
End Class
