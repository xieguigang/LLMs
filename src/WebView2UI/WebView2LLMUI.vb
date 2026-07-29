Imports System.Text.Json
Imports System.Threading
Imports Microsoft.Web.WebView2.Core
Imports Ollama
Imports WebView2UI.My.Resources

Public Class WebView2LLMUI

    Friend llm_host As LLMClient
    Friend _cts As CancellationTokenSource

    Public ReadOnly Property llm As String
        Get
            Return llm_host.Model
        End Get
    End Property

    ''' <summary>
    ''' bind a <see cref="LLMClient"/> instance as the backend of this chat control. the response
    ''' stream hook will be enabled so that the think/output tokens can be pushed to the web ui.
    ''' </summary>
    ''' <param name="llm_host"></param>
    Public Sub SetHost(llm_host As LLMClient)
        Me.llm_host = llm_host.HookResponseStream(getOutputToken:=AddressOf PushOutputToken, getThinkToken:=AddressOf PushThinkToken)
        Call PushTokenInfo()
    End Sub

    ''' <summary>
    ''' start a new chat response: create a fresh cancellation token and notify the web ui to
    ''' create a new assistant bubble.
    ''' </summary>
    Friend Sub BeginChat()
        If _cts IsNot Nothing Then
            Call _cts.Dispose()
        End If

        _cts = New CancellationTokenSource()
        Call PushStart()
    End Sub

    ''' <summary>
    ''' cancel the current chat generation
    ''' </summary>
    Friend Sub StopChat()
        If _cts IsNot Nothing Then
            Call _cts.Cancel()
        End If
    End Sub

    ''' <summary>
    ''' clear conversation memory in host and reset the web ui message list
    ''' </summary>
    Friend Sub ResetConversation()
        If llm_host IsNot Nothing Then
            Call llm_host.Clear()
        End If

        Call SendMessage(New With {
            .action = "reset"
        })
        Call PushTokenInfo()
    End Sub

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

    ''' <summary>
    ''' notify the web ui that a new assistant response starts
    ''' </summary>
    Public Sub PushStart()
        Call SendMessage(New With {
            .action = "start_response",
            .role = "assistant"
        })
    End Sub

    ''' <summary>
    ''' notify the web ui that the current assistant response ends, the final text is used as a
    ''' safe fallback in case some streamed tokens were missed.
    ''' </summary>
    Public Sub PushEnd(output As String, think As String)
        Call SendMessage(New With {
            .action = "end_response",
            .output = output,
            .think = think
        })
        Call PushTokenInfo()
    End Sub

    ''' <summary>
    ''' push the current context token usage (context_tokens / max_context_tokens) of the host
    ''' llm client to the web html ui so it can render the context size indicator.
    ''' </summary>
    Public Sub PushTokenInfo()
        If llm_host Is Nothing Then
            Return
        End If

        Call SendMessage(New With {
            .action = "token_update",
            .context_tokens = llm_host.context_tokens,
            .max_context_tokens = llm_host.max_context_tokens
        })
    End Sub

    ''' <summary>
    ''' notify the web ui that an error occured during the chat
    ''' </summary>
    Public Sub PushError(err As String)
        Call SendMessage(New With {
            .action = "error",
            .message = err
        })
    End Sub

    ''' <summary>
    ''' post a json payload from the host to the web html ui, the call is marshaled to the UI
    ''' thread to keep CoreWebView2 happy when the token callback fires on a background thread.
    ''' </summary>
    Private Sub SendMessage(Of T As Class)(payload As T)
        If WebView21 Is Nothing OrElse WebView21.CoreWebView2 Is Nothing Then
            Return
        End If

        Dim json = JsonSerializer.Serialize(payload)

        If WebView21.InvokeRequired Then
            WebView21.Invoke(Sub() WebView21.CoreWebView2.PostWebMessageAsJson(json))
        Else
            WebView21.CoreWebView2.PostWebMessageAsJson(json)
        End If
    End Sub

    Friend file_ref As String

    Dim webViewInitialized As Boolean = False

    Public Async Function SetFileReference(filepath As String) As Task
        Dim filename As String = filepath.FileName
        Dim json = JsonSerializer.Serialize(filename)

        file_ref = filepath

        If webViewInitialized Then
            Await WebView21.ExecuteScriptAsync($"set_file_reference({json})")
        End If
    End Function

    Private Async Sub WebView2LLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        Await WebViewLoader.Init(WebView21)
    End Sub

    Private Sub WebView21_CoreWebView2InitializationCompleted(sender As Object, e As CoreWebView2InitializationCompletedEventArgs) Handles WebView21.CoreWebView2InitializationCompleted
        WebView21.CoreWebView2.AddHostObjectToScript("llm_host", New LLMHost(Me))
        WebViewLoader.NavigateToLargeString(WebView21, HtmlUiResource.index)
    End Sub

    Private Async Sub WebView21_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs) Handles WebView21.NavigationCompleted
        webViewInitialized = True

        If Not file_ref.StringEmpty(, True) Then
            Dim filename As String = file_ref.FileName
            Dim json = JsonSerializer.Serialize(filename)

            Await WebView21.ExecuteScriptAsync($"set_file_reference({json})")
        End If
    End Sub
End Class
