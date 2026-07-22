Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall

''' <summary>
''' 统一的大语言模型客户端：持有记忆、函数注册表与日志，调用任意 <see cref="ILLMProvider"/> 完成多轮对话与工具调用，
''' 对外返回包含「思考(think)」与「正文(output)」的统一响应对象 <see cref="LLMsResponse"/>。
''' </summary>
Public Class LLMClient : Implements IDisposable

    Private ReadOnly _provider As ILLMProvider
    Private ReadOnly _model As String

    Dim ai_memory As New Queue(Of ChatMessage)
    Dim ai_caller As New FunctionCaller
    Dim ai_log As TextWriter
    Dim ai_calls As New List(Of FunctionCall)
    Dim preserveMemory As Boolean = True
    Private disposedValue As Boolean

    Public Property temperature As Double = 0.1
    Public Property tools As List(Of FunctionTool)

    ''' <summary>
    ''' system message to the LLMs AI
    ''' </summary>
    Public Property system_message As String
    ''' <summary>
    ''' 可选：外部工具执行引擎（当函数未在 ai_caller 中注册时使用）
    ''' </summary>
    Public Property tool_invoke As Func(Of FunctionCall, String)
    ''' <summary>
    ''' 记忆队列的最大消息条数
    ''' </summary>
    Public Property max_memory_size As Integer = 100000

    Friend Shared ReadOnly SharedHttpClient As New HttpClient(New HttpClientHandler With {
        .Proxy = Nothing,
        .UseProxy = False
    }) With {.Timeout = TimeSpan.FromHours(1)}

    Sub New(provider As ILLMProvider, model As String, Optional logfile As String = Nothing, Optional preserveMemory As Boolean = True)
        _provider = provider
        _model = model
        Me.preserveMemory = preserveMemory

        Dim temp_logfile As String = If(String.IsNullOrEmpty(logfile),
            IO.Path.Combine(IO.Path.GetTempPath(), "ollama_log_" & Guid.NewGuid().ToString("N") & ".jsonl"),
            logfile)
        Me.ai_log = New StreamWriter(temp_logfile, append:=False)
    End Sub

    ''' <summary>
    ''' 执行单个工具调用：优先使用 ai_caller 中注册的函数，否则使用外部 tool_invoke 引擎
    ''' </summary>
    Private Function ExecuteTool(calle As ToolCallInfo) As String
        Dim [call] As New FunctionCall With {
            .name = calle.FunctionName,
            .arguments = calle.FunctionArguments
        }

        If ai_caller.CheckFunction([call].name) Then
            Return ai_caller.Call([call])
        ElseIf tool_invoke IsNot Nothing Then
            Return tool_invoke([call])
        Else
            Throw New InvalidProgramException($"the function '{[call].name}' is not registered and no external invoke engine is set!")
        End If
    End Function

    ''' <summary>
    ''' 构造发送给 Provider 的消息列表：若首条不是 system 且设置了 system_message，则前置系统消息
    ''' </summary>
    Private Function BuildRequestMessages() As List(Of ChatMessage)
        Dim msgs As New List(Of ChatMessage)(ai_memory)
        If (msgs.Count = 0 OrElse msgs(0).Role <> "system") AndAlso Not String.IsNullOrEmpty(system_message) Then
            msgs.Insert(0, New ChatMessage With {.Role = "system", .Content = system_message})
        End If
        Return msgs
    End Function

    ''' <summary>
    ''' Chat with LLMs, send the user message to LLMs model and then get response result text
    ''' </summary>
    ''' <param name="prompt_text">the prompt text message that send to the LLMs model</param>
    ''' <returns>
    ''' the LLMs response output text data, includes <see cref="LLMsResponse.think"/> text and 
    ''' the real LLMs content <see cref="LLMsResponse.output"/>.
    ''' </returns>
    Public Async Function Chat(prompt_text As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of LLMsResponse)
        Dim newUserMsg As New ChatMessage With {.Role = "user", .Content = prompt_text}

        If preserveMemory Then
            ai_memory.Enqueue(newUserMsg)
            If ai_log IsNot Nothing Then ai_log.WriteLine(newUserMsg.GetJson(simpleDict:=True))

            While ai_memory.Count > max_memory_size
                ai_memory.Dequeue()
            End While
        End If

        Dim reqOptions As New ChatRequestOptions With {
            .Model = _model,
            .Messages = BuildRequestMessages(),
            .Tools = Me.tools,
            .Temperature = Me.temperature
        }

        ' 循环处理：如果模型返回 Tool Calls，执行后继续请求，直到返回最终文本
        Dim maxRounds As Integer = 10
        Dim currentReq = reqOptions
        Dim fullThink As New StringBuilder
        Dim fullOutput As New StringBuilder

        For round = 1 To maxRounds
            Dim thinkBuf As New StringBuilder
            Dim outBuf As New StringBuilder
            Dim toolCallsToExecute As New List(Of ToolCallInfo)

            ' 1. 通过 Provider 拉取流式数据
            Dim chunks = Await _provider.StreamChatAsync(currentReq, cancellationToken)

            ' 2. 处理流式响应
            For Each chunk In chunks
                If Not String.IsNullOrEmpty(chunk.ThinkContent) Then
                    Console.Write(chunk.ThinkContent)
                    thinkBuf.Append(chunk.ThinkContent)
                End If
                If Not String.IsNullOrEmpty(chunk.DeltaContent) Then
                    Console.Write(chunk.DeltaContent)
                    outBuf.Append(chunk.DeltaContent)
                End If

                ' 收集 Tool Calls
                If chunk.ToolCalls IsNot Nothing Then
                    toolCallsToExecute.AddRange(chunk.ToolCalls)
                End If

                If chunk.IsDone Then Exit For
            Next

            fullThink.Append(thinkBuf.ToString())
            fullOutput.Append(outBuf.ToString())

            ' 3. 如果没有工具调用，直接返回结果
            If toolCallsToExecute.IsNullOrEmpty Then
                Dim finalAssistantMsg As New ChatMessage With {.Role = "assistant", .Content = outBuf.ToString()}
                If preserveMemory Then
                    ai_memory.Enqueue(finalAssistantMsg)
                    If ai_log IsNot Nothing Then ai_log.WriteLine(finalAssistantMsg.GetJson(simpleDict:=True))
                End If
                Return New LLMsResponse With {
                    .think = fullThink.ToString().Trim(),
                    .output = fullOutput.ToString().Trim()
                }
            End If

            ' 4. 如果有工具调用，执行并追加历史记录
            Dim assistantMsg As New ChatMessage With {
                .Role = "assistant",
                .Content = outBuf.ToString(),
                .ToolCalls = toolCallsToExecute
            }
            If preserveMemory Then ai_memory.Enqueue(assistantMsg)
            currentReq.Messages.Add(assistantMsg)

            ' 逐个执行工具
            For Each tc As ToolCallInfo In toolCallsToExecute
                Dim fval As String = ExecuteTool(tc)
                ai_calls.Add(New FunctionCall With {.name = tc.FunctionName, .arguments = tc.FunctionArguments})

                Dim toolMsg As New ChatMessage With {
                    .Role = "tool",
                    .ToolCallId = tc.Id,
                    .Content = fval
                }
                If preserveMemory Then ai_memory.Enqueue(toolMsg)
                currentReq.Messages.Add(toolMsg)
            Next

            ' 5. 准备下一轮请求 (带上工具结果)
            currentReq.Tools = Nothing ' 通常第二轮不需要再传 tools 定义
        Next

        Throw New Exception("Exceeded max tool call rounds")
    End Function

    ''' <summary>
    ''' get the function calls and then clear the function calls history temp cache list
    ''' </summary>
    Public Function GetLastFunctionCalls() As FunctionCall()
        Dim calls = ai_calls.ToArray
        ai_calls.Clear()
        Return calls
    End Function

    ''' <summary>
    ''' registry custom function tool for LLMs function calling
    ''' </summary>
    ''' <param name="func">the function tool metadata, includes the function name, parameter information</param>
    ''' <param name="f">the clr function call backend for the given <paramref name="func"/> metadata</param>
    ''' <example>
    ''' ' define two function parameter
    ''' Dim a As New ParameterProperties("a", "number a", TypeCode.Double)
    ''' Dim b As New ParameterProperties("b", "number b", TypeCode.Double)
    ''' 
    ''' ' register a new function for add two number.
    ''' LLMs_ollama.AddFunction(
    '''    func:=New FunctionModel("number_add", "add two number, return a json string of the two number math add result.", a, b), 
    '''    f:=Function(calls) 
    '''           Dim num1 = Val(calls!a)
    '''           Dim num2 = Val(calls!b)
    '''           
    '''           ' return a json string of the two number math add result. 
    '''           Return $"{{result: {num1 + num2}}}"
    '''       End Function
    ''' )
    ''' </example>
    Public Sub AddFunction(func As FunctionModel, Optional f As Func(Of FunctionCall, String) = Nothing)
        If tools Is Nothing Then
            tools = New List(Of FunctionTool)
        End If

        Call tools.Add(New FunctionTool With {.[function] = func})

        If Not f Is Nothing Then
            Call ai_caller.Register(func.name, f)
        End If
    End Sub

    ''' <summary>
    ''' registry new clr function
    ''' </summary>
    ''' <typeparam name="T"></typeparam>
    ''' <param name="obj">A clr object instance, the container for the function tools that could be called by the LLMs</param>
    ''' <param name="fun">target function name to get clr function from the given clr <paramref name="obj"/>.</param>
    ''' <example>
    ''' ' example for define a container class in VB.NET
    ''' Public Class MathTool
    '''     
    '''     &lt;Description("add two number, return a json string of the two number math add result.")>
    '''     Public Function number_add(&lt;Argument("a", Description:="number a")>a As Double, 
    '''                                &lt;Argument("b", Description:="number b")>b As Double) As String
    '''         Return $"{{result: {a + b}}}"
    '''     End Function
    ''' End Class
    ''' 
    ''' LLMs_ollama.AddFunction(New MathTool(), fun:= "number_add")
    ''' </example>
    ''' <remarks>
    ''' Reflection custom attribute that used for make function tool annotations for LLMs:
    ''' 
    ''' 1. <see cref="DescriptionAttribute"/>: export the function tool description text to LLMs
    ''' 2. <see cref="ArgumentAttribute"/>: make the annotation description of the function parameters, for make export of the function parameters to LLMs
    ''' </remarks>
    Public Sub AddFunction(Of T)(obj As T, fun As String)
        For Each handle As Reflection.MethodInfo In CLRFunction.GetTarget(GetType(T), fun)
            Call AddFunction(handle.GetMetadata, CLRFunction.Caller(obj, handle))
        Next
    End Sub

    Public Function Clear() As LLMClient
        Call ai_memory.Clear()
        Return Me
    End Function

    Public Sub AddSystemPrompt(text As String)
        system_message = If(String.IsNullOrEmpty(system_message), text, system_message & vbCrLf & text)
    End Sub

    ''' <summary>
    ''' 获取当前模型的归一化信息：委托给底层 Provider 实现，自动兼容 Ollama / OpenAI 等后端。
    ''' </summary>
    ''' <param name="timeout">请求超时（秒）</param>
    ''' <param name="verbose">是否返回详细信息（Ollama 有效，OpenAI 忽略）</param>
    ''' <returns>归一化后的 <see cref="ModelInfo"/>；请求失败则向上抛出异常</returns>
    Public Async Function GetModelInformation(Optional timeout As Double = 1, Optional verbose As Boolean = True) As Task(Of ModelInfo)
        Return Await _provider.GetModelInformation(_model, timeout, verbose)
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                If ai_log IsNot Nothing Then
                    Call ai_log.Flush()
                    Call ai_log.Dispose()
                End If
            End If

            disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class
