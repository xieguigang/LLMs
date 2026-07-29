Imports System.Runtime.InteropServices
Imports System.Text.Json
Imports System.Threading
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Microsoft.Web.WebView2.Core
Imports Ollama
Imports WebView2UI.My.Resources

Public Class WebView2LLMUI

    <ClassInterface(ClassInterfaceType.AutoDual)>
    <ComVisible(True)>
    Public Class LLMHost

        ReadOnly host As WebView2LLMUI

        Sub New(host As WebView2LLMUI)
            Me.host = host
        End Sub

        ''' <summary>
        ''' the current model name that configured in the host llm client, use for display in the web ui
        ''' </summary>
        ''' <returns></returns>
        Public ReadOnly Property Model As String
            Get
                If host.llm_host Is Nothing Then
                    Return ""
                Else
                    Return host.llm_host.Model
                End If
            End Get
        End Property

        ''' <summary>
        ''' javascript call host method for send prompt text to llm, the response will be streamed
        ''' back to the web page via the push_token / start_response / end_response web messages.
        ''' </summary>
        ''' <param name="prompt_text"></param>
        Public Async Function SendMessage(prompt_text As String) As Task(Of String)
            If host.llm_host Is Nothing Then
                Call host.PushError("the llm host is not configured, please call SetHost first.")
                Return (New LLMsResponse).GetJson
            End If

            Call host.BeginChat()

            Try
                Dim response = Await host.llm_host.Chat(prompt_text, host._cts.Token)

                If response Is Nothing Then
                    Call host.PushEnd("", "")
                Else
                    Call host.PushEnd(response.output, response.think)
                    Return response.GetJson
                End If
            Catch ex As OperationCanceledException
                ' user cancelled the generation, just end the response quietly
                Call host.PushEnd("", "")
            Catch ex As Exception
                Call host.PushError(ex.Message)
            End Try

            Return (New LLMsResponse).GetJson
        End Function

        ''' <summary>
        ''' cancel the current llm generation, triggered from the web ui stop button
        ''' </summary>
        Public Sub StopGeneration()
            Call host.StopChat()
        End Sub

        ''' <summary>
        ''' clear the conversation history in the host llm client and reset the web ui message list
        ''' </summary>
        Public Sub ResetConversation()
            Call host.ResetConversation()
        End Sub

    End Class

    Dim llm_host As LLMClient
    Dim _cts As CancellationTokenSource

    ''' <summary>
    ''' bind a <see cref="LLMClient"/> instance as the backend of this chat control. the response
    ''' stream hook will be enabled so that the think/output tokens can be pushed to the web ui.
    ''' </summary>
    ''' <param name="llm_host"></param>
    Public Sub SetHost(llm_host As LLMClient)
        Me.llm_host = llm_host.HookResponseStream(getOutputToken:=AddressOf PushOutputToken, getThinkToken:=AddressOf PushThinkToken)
    End Sub

    ''' <summary>
    ''' start a new chat response: create a fresh cancellation token and notify the web ui to
    ''' create a new assistant bubble.
    ''' </summary>
    Private Sub BeginChat()
        If _cts IsNot Nothing Then
            Call _cts.Dispose()
        End If

        _cts = New CancellationTokenSource()
        Call PushStart()
    End Sub

    ''' <summary>
    ''' cancel the current chat generation
    ''' </summary>
    Private Sub StopChat()
        If _cts IsNot Nothing Then
            Call _cts.Cancel()
        End If
    End Sub

    ''' <summary>
    ''' clear conversation memory in host and reset the web ui message list
    ''' </summary>
    Private Sub ResetConversation()
        If llm_host IsNot Nothing Then
            Call llm_host.Clear()
        End If

        Call SendMessage(New With {
            .action = "reset"
        })
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

    Private Async Sub WebView2LLMUI_Load(sender As Object, e As EventArgs) Handles Me.Load
        Await WebViewLoader.Init(WebView21)
    End Sub

    Private Sub WebView21_CoreWebView2InitializationCompleted(sender As Object, e As CoreWebView2InitializationCompletedEventArgs) Handles WebView21.CoreWebView2InitializationCompleted
        WebView21.CoreWebView2.AddHostObjectToScript("llm_host", New LLMHost(Me))
        WebViewLoader.NavigateToLargeString(WebView21, HtmlUiResource.index)
    End Sub

End Class
