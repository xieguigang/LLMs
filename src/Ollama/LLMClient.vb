Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall

''' <summary>
''' 统一的大语言模型客户端：持有记忆、函数注册表与日志，调用任意 <see cref="ILLMProvider"/> 完成多轮对话与工具调用，
''' 对外返回包含「思考(think)」与「正文(output)」的统一响应对象 <see cref="LLMsResponse"/>。
''' </summary>
Public Class LLMClient : Implements IDisposable

    ReadOnly _provider As ILLMProvider
    ReadOnly _model As String
    ReadOnly _maxRounds As Integer = 15
    ReadOnly _preserveMemory As Boolean = True

    Dim _context As New ChatContextMemory
    Dim _caller As FunctionCaller
    Dim _calls As New List(Of FunctionCall)

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
    ''' 记忆上下文的最大 Token 数量上限（近似估算），默认 1,000,000（1M）。超过后从历史最旧端裁剪。
    ''' 裁剪逻辑由底层 <see cref="ChatContextMemory"/> 负责，并保证工具调用消息成组保留。
    ''' </summary>
    Public Property max_context_tokens As Integer
        Get
            Return CInt(_context.MaxTokens)
        End Get
        Set(value As Integer)
            _context.MaxTokens = value
        End Set
    End Property

    Friend Shared ReadOnly SharedHttpClient As New HttpClient(New HttpClientHandler With {
        .Proxy = Nothing,
        .UseProxy = False
    }) With {.Timeout = TimeSpan.FromHours(1)}

    ''' <summary>
    ''' 
    ''' </summary>
    ''' <param name="provider"></param>
    ''' <param name="model"></param>
    ''' <param name="logfile"></param>
    ''' <param name="preserveMemory"></param>
    ''' <param name="maxRound">
    ''' For LLM agent run a complex task, please increase this rounds number
    ''' </param>
    Sub New(provider As ILLMProvider, model As String,
            Optional logfile As String = Nothing,
            Optional preserveMemory As Boolean = True,
            Optional maxRound As Integer = 15,
            Optional debug As Boolean = False)

        _provider = provider
        _model = model
        _maxRounds = maxRound
        _preserveMemory = preserveMemory
        _caller = New FunctionCaller(verbose:=debug)
    End Sub

    ''' <summary>
    ''' 执行单个工具调用：优先使用 ai_caller 中注册的函数，否则使用外部 tool_invoke 引擎
    ''' </summary>
    Private Function ExecuteTool(calle As ToolCallInfo) As String
        Dim [call] As New FunctionCall With {
            .name = calle.FunctionName,
            .arguments = calle.FunctionArguments
        }
        Dim result_str As String

        If _caller.CheckFunction([call].name) Then
            result_str = _caller.Call([call])
        ElseIf tool_invoke IsNot Nothing Then
            result_str = tool_invoke([call])
        Else
            result_str = $"error: the tool function '{[call].name}' is not registered or no external function tool_call invoke engine was configured!"
        End If

        If calle.DeepSeekDSMLLeak Then
            result_str = $"<tool_result>{result_str}</tool_result>"
        End If

        Return result_str
    End Function

    ''' <summary>
    ''' 构造发送给 Provider 的消息列表：若首条不是 system 且设置了 system_message，则前置系统消息
    ''' </summary>
    Private Function BuildRequestMessages() As List(Of ChatMessage)
        Dim msgs As New List(Of ChatMessage)(_context)
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

        If _preserveMemory Then
            _context.Enqueue(newUserMsg)
            If ai_log IsNot Nothing Then ai_log.WriteLine(newUserMsg.GetJson(simpleDict:=True))
        End If

        Dim reqOptions As New ChatRequestOptions With {
            .Model = _model,
            .Messages = BuildRequestMessages(),
            .Tools = Me.tools,
            .Temperature = Me.temperature
        }
        Dim currentReq As ChatRequestOptions = reqOptions
        Dim fullThink As New StringBuilder
        Dim fullOutput As New StringBuilder
        Dim llmResponse As LLMsResponse

        ' 循环处理：如果模型返回 Tool Calls，执行后继续请求，直到返回最终文本
        ' 在这个for循环中用来处理工具函数调用，以及由于网络失败或者文本解析错误导致的重试
        For round As Integer = 1 To _maxRounds
            Try
                llmResponse = Nothing
                llmResponse = Await ChatRound(currentReq, cancellationToken, fullThink, fullOutput)
            Catch ex As Exception
                ' 20260723 network error will retry in next round
                llmResponse = Nothing

                Call App.LogException(ex)
                Call ex.Message.warning
            End Try

            If Not llmResponse Is Nothing Then
                Return llmResponse
            Else
                ' 5. 准备下一轮请求 (带上工具结果)
                ' 通常第二轮不需要再传 tools 定义
                ' currentReq.Tools = Nothing
            End If
        Next

        Throw New Exception("Exceeded max tool call rounds")
    End Function

    Private Async Function ChatRound(currentReq As ChatRequestOptions, cancellationToken As CancellationToken, fullThink As StringBuilder, fullOutput As StringBuilder) As Task(Of LLMsResponse)
        Dim thinkBuf As New StringBuilder
        Dim outBuf As New StringBuilder
        Dim toolCallsToExecute As New List(Of ToolCallInfo)
        ' 1. 通过 Provider 拉取流式数据
        Dim chunks As IEnumerable(Of ChatResponseChunk) = Await _provider.StreamChatAsync(currentReq, cancellationToken)
        Dim skipDeepSeekMLLeak As Boolean = True

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

            If chunk.IsDone Then
                Exit For
            End If
        Next

        fullThink.Append(thinkBuf.ToString())
        fullOutput.Append(outBuf.ToString())

        ' 3. 如果没有工具调用，直接返回结果
        If toolCallsToExecute.IsNullOrEmpty Then
            ' 20260723
            ' parse tool call information from output
            ' <｜｜DSML｜｜tool_calls>
            ' <｜｜DSML｜｜invoke name="peek_file">
            ' <｜｜DSML｜｜parameter name="path" string="true">F:/datapool/2026.7.6-energy/FWMS20256511-human_cell_pellet/agent_test/expression.csv</｜｜DSML｜｜parameter>
            ' <｜｜DSML｜｜parameter name="limit" string="false">5</｜｜DSML｜｜parameter>
            ' </｜｜DSML｜｜invoke>
            ' <｜｜DSML｜｜invoke name="execute_r">
            ' <｜｜DSML｜｜parameter name="r_script" string="true"># Quick check of expression data dimensions and value rangesexpr <- read.csv("F:/datapool/2026.7.6-energy/FWMS20256511-human_cell_pellet/agent_test/expression.csv", row.names=1, check.names=FALSE)
            ' cat("Dimensions:", nrow(expr), "x", ncol(expr), "\n")
            ' cat("Column names:", paste(colnames(expr), collapse=", "), "\n")
            ' cat("First3 rows:\n")
            ' print(head(expr[,1:8],3))
            ' cat("\nValue range: min =", min(expr, na.rm=TRUE), ", max =", max(expr, na.rm=TRUE), "\n")
            ' cat("NA count:", sum(is.na(expr)), "\n")
            ' cat("Zero count:", sum(expr==0, na.rm=TRUE), "\n")
            ' cat("\nRow sums (first5):\n")
            ' print(head(rowSums(expr, na.rm=TRUE),5))
            ' cat("\nColumn sums (first5):\n")
            ' print(head(colSums(expr, na.rm=TRUE),5))
            ' cat("\nPer-row min positive values (first5):\n")
            ' min_pos <- apply(expr,1, function(x) min(x[x>0], na.rm=TRUE))
            ' print(head(min_pos))
            ' </｜｜DSML｜｜parameter>
            ' </｜｜DSML｜｜invoke>
            ' </｜｜DSML｜｜tool_calls>
            If outBuf.Length > 0 Then
                Dim firstLine As String, lastLine As String

                With outBuf.ToString.LineTokens
                    firstLine = .First
                    lastLine = .Last
                End With

                If firstLine = "<｜｜DSML｜｜tool_calls>" AndAlso lastLine = "</｜｜DSML｜｜tool_calls>" Then
                    If skipDeepSeekMLLeak Then
                        Return Nothing
                    Else
                        toolCallsToExecute = New List(Of ToolCallInfo)(DsmlParser.ParseToolCalls(outBuf.ToString))
                    End If

                    GoTo exec
                End If
            End If

            Dim finalAssistantMsg As New ChatMessage With {
                .Role = "assistant",
                .Content = outBuf.ToString()
            }

            If _preserveMemory Then
                _context.Enqueue(finalAssistantMsg)
                If ai_log IsNot Nothing Then
                    ai_log.WriteLine(finalAssistantMsg.GetJson(simpleDict:=True))
                End If
            End If
            Return New LLMsResponse With {
                .think = fullThink.ToString().Trim(),
                .output = fullOutput.ToString().Trim()
            }
        End If
exec:
        ' 4. 如果有工具调用，执行并追加历史记录
        Dim assistantMsg As New ChatMessage With {
            .Role = "assistant",
            .Content = outBuf.ToString(),
            .ToolCalls = toolCallsToExecute
        }
        If _preserveMemory Then
            _context.Enqueue(assistantMsg)
        End If
        currentReq.Messages.Add(assistantMsg)

        ' 逐个执行工具
        For Each tc As ToolCallInfo In toolCallsToExecute
            Dim fval As String = ExecuteTool(tc)
            Dim toolMsg As New ChatMessage With {
                .Role = "tool",
                .ToolCallId = tc.Id,
                .Content = fval
            }

            Call _calls.Add(New FunctionCall With {
                .name = tc.FunctionName,
                .arguments = tc.FunctionArguments
            })

            If _preserveMemory Then
                _context.Enqueue(toolMsg)
            End If

            currentReq.Messages.Add(toolMsg)
        Next

        Return Nothing
    End Function

    ''' <summary>
    ''' get the function calls and then clear the function calls history temp cache list
    ''' </summary>
    Public Function GetLastFunctionCalls() As FunctionCall()
        Dim calls = _calls.ToArray
        _calls.Clear()
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
            Call _caller.Register(func.name, f)
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
        Call _context.Clear()
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
