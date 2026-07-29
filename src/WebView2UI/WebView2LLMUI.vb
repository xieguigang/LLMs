Imports System.Runtime.InteropServices
Imports System.Text.Json
Imports Microsoft.Web.WebView2.Core
Imports WebView2UI.My.Resources

Public Class WebView2LLMUI

    <ComVisible(True)>
    Private Class LLMHost

        ReadOnly host As WebView2LLMUI

        Sub New(host As WebView2LLMUI)
            Me.host = host
        End Sub

    End Class

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

    Private Sub WebView21_CoreWebView2InitializationCompleted(sender As Object, e As CoreWebView2InitializationCompletedEventArgs) Handles WebView21.CoreWebView2InitializationCompleted
        WebView21.CoreWebView2.AddHostObjectToScript("llm_host", New LLMHost(Me))
        WebViewLoader.NavigateToLargeString(WebView21, HtmlUiResource.index)
    End Sub
End Class
