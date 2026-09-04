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
    ''' 在后台线程上完成一次对话：llm 的流式响应是通过惰性迭代器逐块读取的，
    ''' 如果这段逻辑跑在 ui 线程上，读流时的同步等待会直接把界面卡住。
    ''' 这里整体卸载到线程池，ui 线程只负责开始与结束的通知。
    ''' </summary>
    Private Async Function RunChat(prompt_text As String) As Task(Of LLMsResponse)
        Dim response = Await host.llm_host.Chat(prompt_text, host._cts.Token)

        If response IsNot Nothing Then
            response = New LLMsResponse With {
                .think = makrdown.Transform(response.think),
                .output = makrdown.Transform(response.output)
            }
        End If

        Return response
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
            ' 附件的读取是文件 io，同样放到线程池上执行
            prompt_text = Await Task.Run(Function() AttachFile(prompt_text, 300))
        End If

        Call host.BeginChat()

        Try
            Dim response = Await Task.Run(Function() RunChat(prompt_text))

            If host.llm_callback IsNot Nothing Then
                Call host.llm_callback(If(response, New LLMsResponse))
            End If

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

    ''' <summary>
    ''' 从会话上下文中移除指定的文件附件（由网页端点击附件 chip 上的删除链接触发）
    ''' </summary>
    ''' <param name="uid">附件的 <see cref="FileReference.uid"/></param>
    ''' <returns>移除成功返回 "ok"，附件不存在返回 "not_found"</returns>
    Public Async Function RemoveFileReference(uid As String) As Task(Of String)
        Try
            If Await host.RemoveFileReference(uid) Then
                Return "ok"
            Else
                Return "not_found"
            End If
        Catch ex As Exception
            Call host.PushError($"移除文件附件失败：{ex.Message}")
            Return "error"
        End Try
    End Function

    ''' <summary>
    ''' 请求查看指定的文件附件（由网页端点击附件 chip 上的文件名触发），
    ''' 具体行为由宿主程序的 <see cref="WebView2LLMUI.ViewFileRequested"/> 事件或者控件的默认实现决定
    ''' </summary>
    ''' <param name="uid">附件的 <see cref="FileReference.uid"/></param>
    ''' <returns></returns>
    Public Async Function OpenFileReference(uid As String) As Task(Of String)
        Try
            Await host.ViewFile(uid)
        Catch ex As Exception
            Call host.PushError($"查看文件失败：{ex.Message}")
        End Try

        Return "ok"
    End Function

    ''' <summary>
    ''' 弹出文件选择对话框（支持多选），把用户选中的文件添加到会话上下文中；
    ''' 由网页端的文件添加按钮触发，返回本次实际新添加的文件数量
    ''' </summary>
    ''' <returns></returns>
    Public Async Function AddFileReferences() As Task(Of String)
        Try
            Dim files As String() = host.ShowAddFileDialog()
            Dim count As Integer = host.FileReferenceCount

            If files.IsNullOrEmpty Then
                Return "0"
            End If

            For Each filepath As String In files
                Await host.AddFileReference(filepath)
            Next

            Return (host.FileReferenceCount - count).ToString()
        Catch ex As Exception
            Call host.PushError($"添加文件失败：{ex.Message}")
            Return "0"
        End Try
    End Function

    ''' <summary>
    ''' 获取当前会话中全部文件附件的 json 描述，供网页端渲染附件区
    ''' </summary>
    ''' <returns></returns>
    Public Function GetFileReferences() As String
        Return host.GetUsageStatsJson(Nothing)
    End Function

    ''' <summary>
    ''' 获取当前会话的 token 用量以及 kv 缓存命中统计的 json 字符串，
    ''' 由网页端的用量详情模态框在打开时调用
    ''' </summary>
    ''' <returns></returns>
    Public Function GetUsageStats() As String
        Return host.GetUsageStatsJson()
    End Function

End Class
