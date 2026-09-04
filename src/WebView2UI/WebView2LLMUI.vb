Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Microsoft.VisualBasic.Linq
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

    ''' <summary>
    ''' 网页界面初始化完成之后触发
    ''' </summary>
    Public Event UIInitialized()

    ''' <summary>
    ''' 用户在网页界面上点击某个文件附件的文件名，请求查看该文件内容时触发。
    ''' 宿主应用程序可以挂接该事件以接管文件查看行为（例如在自己的编辑器中打开），
    ''' 将 <see cref="FileViewRequestEventArgs.Handled"/> 置为 True 即可阻止控件的默认行为。
    ''' </summary>
    ''' <param name="sender"></param>
    ''' <param name="e"></param>
    Public Event ViewFileRequested(sender As Object, e As FileViewRequestEventArgs)

    ''' <summary>
    ''' 会话上下文中的文件附件列表发生变化（添加/移除/清空）时触发
    ''' </summary>
    ''' <param name="sender"></param>
    Public Event FileReferencesChanged(sender As Object)

    ' 流式输出的 token 合批推送缓冲：LLM 的流式回调发生在后台线程上，
    ' 这里只做加锁拼接，真正的跨进程推送由 ui 线程上的定时器按帧合批完成
    ReadOnly tokenLock As New Object
    ReadOnly thinkBuffer As New StringBuilder
    ReadOnly outputBuffer As New StringBuilder
    Dim tokenTimer As System.Windows.Forms.Timer
    Dim tokenFlushActive As Boolean = False
    Dim _tokenFlushInterval As Integer = DefaultTokenFlushInterval

    ''' <summary>
    ''' token 合批推送的默认时间间隔（毫秒）
    ''' </summary>
    Public Const DefaultTokenFlushInterval As Integer = 60

    ''' <summary>
    ''' 流式输出时 token 的合批推送间隔（毫秒），默认 60ms，可设置范围 10~1000ms。
    ''' 调小可以更接近实时输出，但会增加宿主与网页之间的跨进程消息数量；
    ''' 调大可以进一步降低开销，但输出会显得更有“顿挫感”。
    ''' </summary>
    ''' <returns></returns>
    Public Property TokenFlushInterval As Integer
        Get
            Return _tokenFlushInterval
        End Get
        Set(value As Integer)
            _tokenFlushInterval = Math.Min(Math.Max(value, 10), 1000)

            If tokenTimer IsNot Nothing Then
                tokenTimer.Interval = _tokenFlushInterval
            End If
        End Set
    End Property

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

        ' 启动 token 合批推送定时器（定时器运行在 ui 线程上）
        tokenFlushActive = True
        Call EnsureTokenTimer().Start()
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

        tokenFlushActive = False
        Call FlushTokens()
        Call SendMessage(New With {
            .action = "reset"
        })
        ' 网页端的 reset 会清空芯片区域，但宿主中的附件列表依然有效，
        ' 这里重新下发一次附件列表，保持界面与宿主状态一致
        Call RefreshFileReference()
        Call PushTokenInfo()
    End Sub

    ''' <summary>
    ''' Push LLM think token from host to web html ui via message.
    ''' 该回调由 LLM 的流式读取循环在后台线程上触发，这里只把文本追加到加锁的缓冲区中，
    ''' 真正的跨进程推送由 ui 线程上的定时器按 <see cref="TokenFlushInterval"/> 合批完成，
    ''' 以避免高频的跨线程+跨进程调用阻塞住流式读取与界面响应。
    ''' </summary>
    ''' <param name="token"></param>
    Public Sub PushThinkToken(token As String)
        If String.IsNullOrEmpty(token) Then
            Return
        End If

        SyncLock tokenLock
            Call thinkBuffer.Append(token)
        End SyncLock

        Call ScheduleTokenFlush()
    End Sub

    ''' <summary>
    ''' Push LLM output token from host to web html ui via message.
    ''' 合批处理逻辑同 <see cref="PushThinkToken"/>。
    ''' </summary>
    ''' <param name="token"></param>
    Public Sub PushOutputToken(token As String)
        If String.IsNullOrEmpty(token) Then
            Return
        End If

        SyncLock tokenLock
            Call outputBuffer.Append(token)
        End SyncLock

        Call ScheduleTokenFlush()
    End Sub

    ''' <summary>
    ''' 立即把缓冲区中累积的 think / output token 合批推送到网页。
    ''' 所有与流式输出存在时序依赖的消息（start_response / end_response / error / reset）
    ''' 都必须在推送之前调用该方法，保证消息顺序正确。
    ''' </summary>
    Private Sub FlushTokens()
        Dim think As String = Nothing
        Dim output As String = Nothing

        SyncLock tokenLock
            If thinkBuffer.Length > 0 Then
                think = thinkBuffer.ToString()
                Call thinkBuffer.Clear()
            End If
            If outputBuffer.Length > 0 Then
                output = outputBuffer.ToString()
                Call outputBuffer.Clear()
            End If
        End SyncLock

        If think IsNot Nothing Then
            Call SendMessage(New With {
                .action = "push_token",
                .text = think,
                .type = "think"
            })
        End If
        If output IsNot Nothing Then
            Call SendMessage(New With {
                .action = "push_token",
                .text = output,
                .type = "output"
            })
        End If
    End Sub

    ''' <summary>
    ''' 获取（必要时创建）用于合批推送 token 的 ui 线程定时器
    ''' </summary>
    ''' <returns></returns>
    Private Function EnsureTokenTimer() As System.Windows.Forms.Timer
        If tokenTimer Is Nothing Then
            tokenTimer = New System.Windows.Forms.Timer With {.Interval = _tokenFlushInterval}
            AddHandler tokenTimer.Tick, AddressOf OnTokenFlushTick
        End If

        Return tokenTimer
    End Function

    ''' <summary>
    ''' 确保合批定时器处于运行状态；winforms 定时器只能在 ui 线程上启动，
    ''' 因此当该方法从后台的流式读取线程上被调用时通过 BeginInvoke 封送回 ui 线程
    ''' </summary>
    Private Sub ScheduleTokenFlush()
        If tokenTimer IsNot Nothing AndAlso tokenTimer.Enabled Then
            Return
        End If
        If IsDisposed OrElse Not IsHandleCreated Then
            Return
        End If

        Call BeginInvoke(Sub()
                             If IsDisposed Then
                                 Return
                             End If

                             Call EnsureTokenTimer().Start()
                         End Sub)
    End Sub

    Private Sub OnTokenFlushTick(sender As Object, e As EventArgs)
        Call FlushTokens()

        If tokenFlushActive Then
            Return
        End If

        ' 流式输出已经结束，缓冲区排空之后就停掉定时器，避免留下一个空转的定时器
        SyncLock tokenLock
            If thinkBuffer.Length = 0 AndAlso outputBuffer.Length = 0 Then
                Call tokenTimer.Stop()
            End If
        End SyncLock
    End Sub

    ''' <summary>
    ''' notify the web ui that a new assistant response starts
    ''' </summary>
    Public Sub PushStart()
        Call FlushTokens()
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
        Call FlushTokens()
        Call SendMessage(New With {
            .action = "end_response",
            .output = output,
            .think = think
        })

        tokenFlushActive = False

        If tokenTimer IsNot Nothing Then
            Call tokenTimer.Stop()
        End If

        Call PushTokenInfo()
    End Sub

    ''' <summary>
    ''' push the current context token usage (context_tokens / max_context_tokens) and the kv cache
    ''' / token usage summary of the host llm client to the web html ui, so it can render the
    ''' context size indicator and the cache hit rate indicator.
    ''' </summary>
    Public Sub PushTokenInfo()
        If llm_host Is Nothing Then
            Return
        End If

        Call SendMessage(New With {
            .action = "token_update",
            .context_tokens = StringFormats.Lanudry(llm_host.context_tokens),
            .max_context_tokens = StringFormats.Lanudry(llm_host.max_context_tokens),
            .cache_supported = llm_host.cache_supported,
            .cache_hit_rate = llm_host.cache_hit_rate,
            .last_cache_hit_rate = llm_host.last_cache_hit_rate,
            .cache_hit_tokens = llm_host.cache_hit_tokens,
            .cache_miss_tokens = llm_host.cache_miss_tokens,
            .prompt_tokens = llm_host.prompt_tokens,
            .completion_tokens = llm_host.completion_tokens
        })
    End Sub

    ''' <summary>
    ''' 收集当前会话的 token 用量以及 kv 缓存命中统计，序列化为 json 字符串。
    ''' 该结果供网页端的用量详情模态框展示使用，数据全部来自 <see cref="LLMClient"/> 的只读统计属性。
    ''' </summary>
    ''' <returns></returns>
    Public Function GetUsageStatsJson() As String
        If llm_host Is Nothing Then
            Return "{}"
        End If

        Dim log As ChatUsageRecord() = llm_host.cache_usage_log
        Dim recent As Object() = log _
            .Reverse() _
            .Take(UsageLogDisplaySize) _
            .Select(Function(r) New With {
                .time = r.TimeStamp.ToString("HH:mm:ss"),
                .model = r.Model,
                .prompt_tokens = r.PromptTokens,
                .completion_tokens = r.CompletionTokens,
                .cache_hit_tokens = r.CacheHitTokens,
                .cache_miss_tokens = r.CacheMissTokens,
                .hit_rate = r.HitRate
            }) _
            .Cast(Of Object)() _
            .ToArray()

        Dim stats = New With {
            .model = llm_host.Model,
            .context_tokens = StringFormats.Lanudry(llm_host.context_tokens),
            .max_context_tokens = StringFormats.Lanudry(llm_host.max_context_tokens),
            .cache_supported = llm_host.cache_supported,
            .requests = log.Length,
            .total = New With {
                .prompt_tokens = llm_host.prompt_tokens,
                .completion_tokens = llm_host.completion_tokens,
                .cache_hit_tokens = llm_host.cache_hit_tokens,
                .cache_miss_tokens = llm_host.cache_miss_tokens,
                .hit_rate = llm_host.cache_hit_rate
            },
            .last = New With {
                .prompt_tokens = If(log.Length > 0, log(log.Length - 1).PromptTokens, 0L),
                .completion_tokens = If(log.Length > 0, log(log.Length - 1).CompletionTokens, 0L),
                .cache_hit_tokens = llm_host.last_cache_hit_tokens,
                .cache_miss_tokens = llm_host.last_cache_miss_tokens,
                .hit_rate = llm_host.last_cache_hit_rate
            },
            .records = recent
        }

        Return JsonSerializer.Serialize(stats)
    End Function

    ''' <summary>
    ''' 用量详情模态框中最多展示的最近请求明细条数
    ''' </summary>
    Public Const UsageLogDisplaySize As Integer = 100

    ''' <summary>
    ''' notify the web ui that an error occured during the chat
    ''' </summary>
    Public Sub PushError(err As String)
        Call FlushTokens()
        Call SendMessage(New With {
            .action = "error",
            .message = err
        })

        tokenFlushActive = False

        If tokenTimer IsNot Nothing Then
            Call tokenTimer.Stop()
        End If
    End Sub

    ''' <summary>
    ''' post a json payload from the host to the web html ui, the call is marshaled to the UI
    ''' thread to keep CoreWebView2 happy when the token callback fires on a background thread.
    ''' 这里使用 BeginInvoke 异步封送：既保证消息按调用顺序投递，又不会让后台的流式读取线程
    ''' 阻塞在 ui 线程上（这是流式输出期间界面卡顿的原因之一）。
    ''' </summary>
    Private Sub SendMessage(Of T As Class)(payload As T)
        If WebView21 Is Nothing OrElse WebView21.IsDisposed OrElse WebView21.CoreWebView2 Is Nothing Then
            Return
        End If

        Dim json = JsonSerializer.Serialize(payload)

        If WebView21.InvokeRequired Then
            Call WebView21.BeginInvoke(Sub()
                                           If WebView21 Is Nothing OrElse WebView21.IsDisposed Then
                                               Return
                                           End If
                                           If WebView21.CoreWebView2 Is Nothing Then
                                               Return
                                           End If

                                           Call WebView21.CoreWebView2.PostWebMessageAsJson(json)
                                       End Sub)
        Else
            Call WebView21.CoreWebView2.PostWebMessageAsJson(json)
        End If
    End Sub

    ' 会话上下文中的文件附件列表：只在 ui 线程上变更，后台线程读取时通过 fileLock 取快照
    ReadOnly fileLock As New Object
    Dim fs As New List(Of FileReference)
    Dim webViewInitialized As Boolean = False

    ''' <summary>
    ''' 当前会话上下文中已经添加的文件附件数量
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property FileReferenceCount As Integer
        Get
            SyncLock fileLock
                Return fs.Count
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' 当前是否存在至少一个可用的文件附件
    ''' </summary>
    ''' <returns></returns>
    Public Function SourceAvailable() As Boolean
        SyncLock fileLock
            Return fs.Any(Function(f) f.Available)
        End SyncLock
    End Function

    ''' <summary>
    ''' 获取当前会话上下文中的全部文件附件；返回的是快照副本，可以安全地在后台线程上遍历
    ''' </summary>
    ''' <returns></returns>
    Public Function GetReferenceFiles() As IEnumerable(Of FileReference)
        SyncLock fileLock
            Return fs.ToArray()
        End SyncLock
    End Function

    ''' <summary>
    ''' 按照 <see cref="FileReference.uid"/> 查找文件附件，不存在时返回 Nothing
    ''' </summary>
    ''' <param name="uid"></param>
    ''' <returns></returns>
    Public Function GetFileReference(uid As String) As FileReference
        If uid.StringEmpty Then
            Return Nothing
        End If

        SyncLock fileLock
            Return fs.Where(Function(f) f.uid = uid).FirstOrDefault
        End SyncLock
    End Function

    ''' <summary>
    ''' 将一段内存数据作为文件附件添加到会话上下文（该数据不存在于磁盘上，只能以文本形式预览）
    ''' </summary>
    ''' <param name="handle">用于读取内存文本数据的回调</param>
    ''' <param name="filename">在界面上显示出来的文件名</param>
    ''' <returns></returns>
    Public Async Function AddFileReference(handle As Func(Of Task(Of String)), filename As String) As Task
        SyncLock fileLock
            Call fs.Add(New MemoryReference(handle, filename))
        End SyncLock

        RaiseEvent FileReferencesChanged(Me)
        Await RefreshFileReference()
    End Function

    ''' <summary>
    ''' 添加单个文件到会话上下文；重复添加同一个文件会被自动忽略，
    ''' 因此可以连续调用该方法把多个文件依次追加进当前会话。
    ''' </summary>
    ''' <param name="filepath">文件路径（相对路径会被转换为全路径）</param>
    ''' <returns></returns>
    Public Async Function AddFileReference(filepath As String) As Task
        Call AddDiskReferences({filepath})

        RaiseEvent FileReferencesChanged(Me)
        Await RefreshFileReference()
    End Function

    ''' <summary>
    ''' 批量添加多个文件到会话上下文；按照文件全路径去重，已经添加过的文件会被自动忽略。
    ''' </summary>
    ''' <param name="filepaths"></param>
    ''' <returns></returns>
    Public Async Function AddFileReference(ParamArray filepaths As String()) As Task
        Call AddDiskReferences(filepaths)

        RaiseEvent FileReferencesChanged(Me)
        Await RefreshFileReference()
    End Function

    ''' <summary>
    ''' 把磁盘文件追加进附件列表的内部实现（按全路径去重），返回实际新添加的文件数量
    ''' </summary>
    Private Function AddDiskReferences(filepaths As IEnumerable(Of String)) As Integer
        Dim added As Integer = 0

        For Each filepath As String In filepaths.SafeQuery
            If filepath.StringEmpty Then
                Continue For
            End If

            Dim full As String = filepath.GetFullPath
            Dim duplicated As Boolean = False

            SyncLock fileLock
                duplicated = fs.Any(Function(f) f.onDisk AndAlso String.Equals(f.path.GetFullPath, full, StringComparison.OrdinalIgnoreCase))

                If Not duplicated Then
                    Call fs.Add(New FileReference With {.path = full})
                    added += 1
                End If
            End SyncLock
        Next

        Return added
    End Function

    ''' <summary>
    ''' 从会话上下文中移除指定 uid 的文件附件。
    ''' 注意：移除只影响后续的请求，不会改写之前已经发送给模型的历史消息内容。
    ''' </summary>
    ''' <param name="uid">目标附件的 <see cref="FileReference.uid"/></param>
    ''' <returns>是否成功移除了指定的附件</returns>
    Public Async Function RemoveFileReference(uid As String) As Task(Of Boolean)
        Dim file As FileReference = GetFileReference(uid)

        If file Is Nothing Then
            Return False
        End If

        SyncLock fileLock
            Call fs.Remove(file)
        End SyncLock

        RaiseEvent FileReferencesChanged(Me)
        Await RefreshFileReference()

        Return True
    End Function

    ''' <summary>
    ''' 清空会话上下文中的全部文件附件
    ''' </summary>
    ''' <returns></returns>
    Public Async Function ClearFileReference() As Task
        SyncLock fileLock
            Call fs.Clear()
        End SyncLock

        RaiseEvent FileReferencesChanged(Me)
        Await RefreshFileReference()
    End Function

    ''' <summary>
    ''' 将宿主中的文件附件列表整体下发到网页重新渲染 chip 列表。
    ''' 该方法只会重绘界面、不会清空宿主中的附件列表；
    ''' 以单条消息整体下发可以避免逐条执行脚本带来的闪烁与竞态。
    ''' </summary>
    ''' <returns></returns>
    Public Function RefreshFileReference() As Task
        If Not webViewInitialized Then
            Return Task.CompletedTask
        End If

        Call SendMessage(New With {
            .action = "file_refs",
            .files = FileSnapshot()
        })

        Return Task.CompletedTask
    End Function

    ''' <summary>
    ''' 对当前附件列表做一份用于序列化下发的快照
    ''' </summary>
    ''' <returns></returns>
    Private Function FileSnapshot() As Object()
        Dim files As FileReference()

        SyncLock fileLock
            files = fs.ToArray()
        End SyncLock

        Return files _
            .Select(Function(f) New With {
                .uid = f.uid,
                .name = If(f.path, "").FileName,
                .path = If(f.path, ""),
                .size = f.size,
                .on_disk = f.onDisk,
                .viewable = f.Available()
            }) _
            .Cast(Of Object)() _
            .ToArray()
    End Function

    ''' <summary>
    ''' 将当前的文件附件列表序列化为 json 字符串（供网页端通过 host object 拉取）
    ''' </summary>
    ''' <returns></returns>
    Public Function GetFileReferencesJson() As String
        Return JsonSerializer.Serialize(FileSnapshot())
    End Function

    ''' <summary>
    ''' 触发对某个文件附件的查看请求：优先抛出 <see cref="ViewFileRequested"/> 事件交给宿主应用程序处理；
    ''' 若宿主没有接管，则对磁盘文件调用系统默认程序打开，对内存数据引用则把文本内容推送到网页模态框中展示。
    ''' </summary>
    ''' <param name="uid">目标附件的 <see cref="FileReference.uid"/></param>
    ''' <returns></returns>
    Public Async Function ViewFile(uid As String) As Task
        Dim file As FileReference = GetFileReference(uid)

        If file Is Nothing Then
            Call PushError("文件附件不存在或者已经被移除。")
            Return
        End If

        Dim args As New FileViewRequestEventArgs(file)

        RaiseEvent ViewFileRequested(Me, args)

        If args.Handled Then
            Return
        End If

        If file.onDisk Then
            Call OpenFileWithShell(file)
        Else
            Await PushFilePreview(file)
        End If
    End Function

    ''' <summary>
    ''' 默认的文件查看行为：调用系统默认程序打开磁盘文件
    ''' </summary>
    ''' <param name="file"></param>
    Private Sub OpenFileWithShell(file As FileReference)
        ' 安全限制：只允许打开当前会话附件列表中已经存在的绝对路径
        Dim target As String = file.path.GetFullPath

        If Not file.Available() Then
            Call PushError($"文件不存在或者已经无法访问：{target}")
            Return
        End If

        Try
            Call Process.Start(New ProcessStartInfo With {
                .FileName = target,
                .UseShellExecute = True
            })
        Catch ex As Exception
            Call PushError($"打开文件失败：{ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' 读取附件的文本内容并推送到网页模态框中展示（用于内存数据引用等没有磁盘路径的附件）
    ''' </summary>
    ''' <param name="file"></param>
    ''' <returns></returns>
    Private Async Function PushFilePreview(file As FileReference) As Task
        Try
            Dim maxChars As Integer = FileReference.PreviewMaxChars
            Dim text As String = Await Task.Run(Function() file.ReadPreviewText(maxChars))

            Call SendMessage(New With {
                .action = "file_content",
                .uid = file.uid,
                .name = If(file.path, "").FileName,
                .content = If(text, ""),
                .truncated = text IsNot Nothing AndAlso text.Length > maxChars
            })
        Catch ex As Exception
            Call PushError($"读取文件内容失败：{ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' 弹出文件选择对话框（支持多选）并返回用户选中的文件路径列表；用户取消选择时返回空数组。
    ''' 对话框始终在 ui 线程上显示。
    ''' </summary>
    ''' <returns></returns>
    Friend Function ShowAddFileDialog() As String()
        If InvokeRequired Then
            Dim selected As String() = Nothing

            Call Invoke(Sub() selected = ShowAddFileDialog())

            Return If(selected, New String() {})
        End If

        Using dialog As New OpenFileDialog With {
            .Multiselect = True,
            .CheckFileExists = True,
            .Title = "添加文件到会话上下文"
        }
            If dialog.ShowDialog(Me) = DialogResult.OK Then
                Return dialog.FileNames
            Else
                Return New String() {}
            End If
        End Using
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

        ' 本页是通过虚拟域名加载的，任何 file:// 导航都只可能来自"把文件拖进窗口"这一操作，
        ' 在这里拦截下来并转换为会话附件，避免页面被替换成被拖入的文件内容
        AddHandler WebView21.CoreWebView2.NavigationStarting, AddressOf WebView21_NavigationStarting

        WebView21.CoreWebView2.AddHostObjectToScript("llm_host", llm_client)
        WebViewLoader.NavigateToLargeString(WebView21, HtmlUiResource.index)
    End Sub

    ''' <summary>
    ''' 拦截由文件拖放产生的 file:// 导航：取消导航并把被拖入的文件加入会话附件
    ''' </summary>
    Private Sub WebView21_NavigationStarting(sender As Object, e As CoreWebView2NavigationStartingEventArgs)
        If e.Uri.StringEmpty OrElse Not e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) Then
            Return
        End If

        e.Cancel = True

        Dim filepath As String

        Try
            filepath = New Uri(e.Uri).LocalPath
        Catch ex As Exception
            Return
        End Try

        Call AddDroppedFile(filepath)
    End Sub

    Private Async Sub AddDroppedFile(filepath As String)
        Try
            If filepath.StringEmpty OrElse Not filepath.FileExists Then
                Return
            End If

            Await AddFileReference(filepath)
        Catch ex As Exception
            Call PushError($"添加拖放文件失败：{ex.Message}")
        End Try
    End Sub

    Private Async Sub WebView21_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs) Handles WebView21.NavigationCompleted
        webViewInitialized = True

        Await RefreshFileReference()

        Call ApplyImage("set_avatar", _avatar)
        Call ApplyImage("set_logo", _logo)

        RaiseEvent UIInitialized()
    End Sub

    Private Sub WebView2LLMUI_Disposed(sender As Object, e As EventArgs) Handles Me.Disposed
        If tokenTimer IsNot Nothing Then
            Call tokenTimer.Stop()
            RemoveHandler tokenTimer.Tick, AddressOf OnTokenFlushTick
            Call tokenTimer.Dispose()

            tokenTimer = Nothing
        End If
    End Sub
End Class
