Imports System.Runtime.InteropServices
Imports System.Text
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

    Private Async Function AttachFile(prompt_text As String, top As Integer) As Task(Of String)
        Dim promptBuild As New StringBuilder
        Dim files As FileReference() = host.GetReferenceFiles.ToArray

        Call promptBuild.AppendLine($"SYSTEM：当前会话上下文有 {files.Length} 个文件附件")

        For Each file As FileReference In files
            Call promptBuild.AppendLine()
            Call promptBuild.AppendLine(Await file.GetFileContent(top))
        Next

        Call promptBuild.AppendLine("文件附件区域结束，以下内容为当前用户会话内容：")
        Call promptBuild.AppendLine()
        Call promptBuild.AppendLine(prompt_text)

        Return promptBuild.ToString
    End Function

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
            prompt_text = Await AttachFile(prompt_text, 300)
        End If

        Call host.BeginChat()

        Try
            Dim response = Await host.llm_host.Chat(prompt_text, host._cts.Token)

            If host.llm_callback IsNot Nothing Then
                Call host.llm_callback(If(response, New LLMsResponse))
            End If

            If response Is Nothing Then
                Call host.PushEnd("", "")
            Else
                response = New LLMsResponse With {
                    .think = makrdown.Transform(response.think),
                    .output = makrdown.Transform(response.output)
                }

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