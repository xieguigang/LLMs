Imports System.Runtime.InteropServices
Imports Microsoft.VisualBasic.MIME.text.markdown
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama

<ClassInterface(ClassInterfaceType.AutoDual)>
<ComVisible(True)>
Public Class LLMHost

    ReadOnly host As WebView2LLMUI
    ReadOnly makrdown As New MarkdownRender

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
    Public ReadOnly Property ContextTokens As String
        Get
            If host.llm_host Is Nothing Then
                Return 0
            Else
                Return StringFormats.Lanudry(host.llm_host.context_tokens)
            End If
        End Get
    End Property

    ''' <summary>
    ''' the maximum context token size limit of the host llm client, use for display in the web ui
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property MaxContextTokens As String
        Get
            If host.llm_host Is Nothing Then
                Return 0
            Else
                Return StringFormats.Lanudry(host.llm_host.max_context_tokens)
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

        If host.SourceAvailable Then
            Dim text As String = Await host.ResolveFileText

            prompt_text = prompt_text & vbCrLf &
                $"
----- 当前所打开的文件 -----
文件路径: {host.file_ref.GetFullPath}
文件文本内容: 

{text}
"
        End If

        Call host.BeginChat()

        Try
            Dim response = Await host.llm_host.Chat(prompt_text, host._cts.Token)

            If response Is Nothing Then
                Call host.PushEnd("", "")
            Else
                response.think = makrdown.Transform(response.think)
                response.output = makrdown.Transform(response.output)

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