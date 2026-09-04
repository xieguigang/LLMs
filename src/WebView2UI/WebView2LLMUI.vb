Imports System.Text.Json
Imports System.Threading
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Microsoft.Web.WebView2.Core
Imports Ollama
Imports WebView2UI.My.Resources

Public Class WebView2LLMUI

    Friend llm_host As LLMClient
    Friend llm_callback As Action(Of LLMsResponse)
    Friend llm_client As LLMHost

    Friend _cts As CancellationTokenSource

    Public ReadOnly Property modelId As String
        Get
            Return llm_host.Model
        End Get
    End Property

    ''' <summary>
    ''' set llm model reference via <see cref="SetHost(LLMClient, Action(Of LLMsResponse))"/>
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property llm As LLMClient
        Get
            Return llm_host
        End Get
    End Property

    Dim _avatar As String
    Dim _logo As String

    ''' <summary>
    ''' the AI avatar image source that displayed in the web html ui: an url or a data uri string.
    ''' this only affects the welcome logo and the assistant message bubble avatar, the topbar logo
    ''' is configured via the <see cref="logo"/> property.
    ''' set this property to Nothing (or an empty string) to restore the default 'AI' text avatar.
    ''' </summary>
    ''' <returns></returns>
    Public Property avatar As String
        Get
            Return _avatar
        End Get
        Set(value As String)
            _avatar = value
            Call ApplyImage("set_avatar", _avatar)
        End Set
    End Property

    ''' <summary>
    ''' the topbar logo image source that displayed in the web html ui: an url or a data uri string.
    ''' this property is independent from the <see cref="avatar"/> property.
    ''' set this property to Nothing (or an empty string) to restore the default 'AI' text logo.
    ''' </summary>
    ''' <returns></returns>
    Public Property logo As String
        Get
            Return _logo
        End Get
        Set(value As String)
            _logo = value
            Call ApplyImage("set_logo", _logo)
        End Set
    End Property

    Public Event UIInitialized()

    ''' <summary>
    ''' bind a <see cref="LLMClient"/> instance as the backend of this chat control. the response
    ''' stream hook will be enabled so that the think/output tokens can be pushed to the web ui.
    ''' </summary>
    ''' <param name="llm_host"></param>
    Public Sub SetHost(llm_host As LLMClient, Optional callback As Action(Of LLMsResponse) = Nothing)
        Me.llm_host = llm_host.HookResponseStream(getOutputToken:=AddressOf PushOutputToken, getThinkToken:=AddressOf PushThinkToken)
        Me.llm_callback = callback

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
            .context_tokens = StringFormats.Lanudry(llm_host.context_tokens),
            .max_context_tokens = StringFormats.Lanudry(llm_host.max_context_tokens)
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

    Dim fs As New List(Of FileReference)
    Dim webViewInitialized As Boolean = False

    Public Function SourceAvailable() As Boolean
        Return fs.Any(Function(f) f.Available)
    End Function

    Public Async Function SetFileReferenceHandle(handle As Func(Of Task(Of String)), filename As String) As Task
        file_ref = filename
        file_handle = handle

        If webViewInitialized Then
            Await Me.SetFileReference
        End If
    End Function

    Public Async Function SetFileReference(filepath As String) As Task
        file_ref = filepath.GetFullPath

        If webViewInitialized Then
            Await Me.SetFileReference
        End If
    End Function

    Public Async Function ClearFileReference() As Task
        file_ref = Nothing
        file_handle = Nothing

        If webViewInitialized Then
            Await WebView21.ExecuteScriptAsync($"clear_file_reference();")
        End If
    End Function

    Private Async Function SetFileReference() As Task
        Dim filename As String = file_ref.FileName
        Dim json = JsonSerializer.Serialize(filename)

        Await WebView21.ExecuteScriptAsync($"set_file_reference({json});")
    End Function

    ''' <summary>
    ''' push an image source (an url or a data uri string) to the web html ui via the given javascript
    ''' setter function, the script call is marshaled to the ui thread to keep CoreWebView2 happy. when
    ''' the webview is not ready yet the value is just kept and will be applied again on navigation completed.
    ''' </summary>
    Private Sub ApplyImage(scriptFunc As String, value As String)
        If Not webViewInitialized Then
            Return
        End If

        Dim json = JsonSerializer.Serialize(If(value, ""))

        If WebView21.InvokeRequired Then
            Call WebView21.Invoke(Sub() WebView21.ExecuteScriptAsync($"{scriptFunc}({json});"))
        Else
            Call WebView21.ExecuteScriptAsync($"{scriptFunc}({json});")
        End If
    End Sub

    Private Async Sub WebView2LLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        Await WebViewLoader.Init(WebView21)
    End Sub

    Public Async Function SendMessage(promptText As String) As Task(Of LLMsResponse)
        Return (Await llm_client.SendMessage(promptText)).LoadJSON(Of LLMsResponse)
    End Function

    Private Sub WebView21_CoreWebView2InitializationCompleted(sender As Object, e As CoreWebView2InitializationCompletedEventArgs) Handles WebView21.CoreWebView2InitializationCompleted
        llm_client = New LLMHost(Me)

        WebView21.CoreWebView2.AddHostObjectToScript("llm_host", llm_client)
        WebViewLoader.NavigateToLargeString(WebView21, HtmlUiResource.index)
    End Sub

    Private Async Sub WebView21_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs) Handles WebView21.NavigationCompleted
        webViewInitialized = True

        If Not file_ref.StringEmpty(, True) Then
            Await SetFileReference()
        End If

        Call ApplyImage("set_avatar", _avatar)
        Call ApplyImage("set_logo", _logo)

        RaiseEvent UIInitialized()
    End Sub
End Class
