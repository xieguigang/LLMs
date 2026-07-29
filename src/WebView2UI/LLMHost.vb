Imports System.Runtime.InteropServices
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama

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
    ''' the estimated current context token size of the host llm client, use for display in the web ui
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property ContextTokens As Integer
        Get
            If host.llm_host Is Nothing Then
                Return 0
            Else
                Return host.llm_host.context_tokens
            End If
        End Get
    End Property

    ''' <summary>
    ''' the maximum context token size limit of the host llm client, use for display in the web ui
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property MaxContextTokens As Integer
        Get
            If host.llm_host Is Nothing Then
                Return 0
            Else
                Return host.llm_host.max_context_tokens
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

        If host.file_ref.FileExists Then
            prompt_text = prompt_text & vbCrLf &
                $"
----- referenced opened local file -----
filename: {host.file_ref.FileName}
filetext: 

{host.file_ref.ReadAllText}
"
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