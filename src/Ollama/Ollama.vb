Imports System.ComponentModel
Imports System.IO
Imports System.Net.Http
Imports System.Reflection
Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.Linq
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall

''' <summary>
''' the ollama client model
''' </summary>
Public Class Ollama : Implements IDisposable

    Public ReadOnly Property server As String
    Public ReadOnly Property model As String

    ''' <summary>
    ''' system message to the LLMs AI
    ''' </summary>
    ''' <returns></returns>
    Public Property system_message As String

    Public Property temperature As Double = 0.1
    Public Property tools As List(Of FunctionTool)
    Public Property tool_invoke As Func(Of FunctionCall, String)
    Public Property max_memory_size As Integer = 100000

    Public ReadOnly Property url As String
        Get
            Return $"http://{_server}/api/chat"
        End Get
    End Property

    Dim ai_memory As New Queue(Of History)
    Dim ai_caller As New FunctionCaller
    Dim ai_log As TextWriter
    Dim ai_calls As New List(Of FunctionCall)

    Dim preserveMemory As Boolean

    Private disposedValue As Boolean

    Sub New(model As String,
            Optional server As String = "127.0.0.1:11434",
            Optional logfile As String = Nothing,
            Optional preserveMemory As Boolean = True)

        Dim temp_logfile As String = TempFileSystem.GetAppSysTempFile(".jsonl", prefix:="ollama_log_" & App.PID & "-history_message")

        Me.preserveMemory = preserveMemory
        Me.model = model
        Me.server = server
        Me.ai_log = New StreamWriter(
            If(logfile.StringEmpty(, True), temp_logfile, logfile) _
                .Open(FileMode.OpenOrCreate, doClear:=True)
        )
    End Sub

    Public Async Function GetModelInformation(Optional timeout As Double = 1, Optional verbose As Boolean = True) As Task(Of JsonObject)
        Dim req As New RequestShowModelInformation With {.model = model, .verbose = verbose}
        Dim url As String = $"http://{_server}/api/show"
        Dim json_input As String = req.GetJson(maskReadonly:=True)
        Dim content = New StringContent(json_input, Encoding.UTF8, "application/json")
        Dim settings As New HttpClientHandler With {
            .Proxy = Nothing,
            .UseProxy = False
        }
        Using client As New HttpClient(settings) With {.Timeout = TimeSpan.FromSeconds(timeout)}
            Dim resp As IEnumerable(Of String) = Await RequestMessage(client, url, content)
            Dim data As JsonObject = JsonParser.Parse(resp.JoinBy(vbCrLf))
            Return data
        End Using
    End Function

    ''' <summary>
    ''' get the function calls and then clear the function calls history temp cache list
    ''' </summary>
    ''' <returns></returns>
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
    ''' 
    Public Sub AddFunction(Of T)(obj As T, fun As String)
        For Each handle As MethodInfo In CLRFunction.GetTarget(GetType(T), fun)
            Call AddFunction(handle.GetMetadata, CLRFunction.Caller(obj, handle))
        Next
    End Sub

    Public Function Clear() As Ollama
        Call ai_memory.Clear()
        Return Me
    End Function

    Public Sub AddSystemPrompt(text As String)
        Call ai_memory.Enqueue(New History With {
            .content = text,
            .role = roles.system.ToString
        })
    End Sub

    ''' <summary>
    ''' Chat with LLMs, send the user message to LLMs model and then get response result text
    ''' </summary>
    ''' <param name="prompt_text">the prompt text message that send to the LLMs model</param>
    ''' <returns>
    ''' the LLMs response output text data, includes <see cref="OllamaResponse.think"/> text and 
    ''' the real LLMs content <see cref="OllamaResponse.output"/>.
    ''' </returns>
    Public Async Function Chat(prompt_text As String) As Task(Of OllamaResponse)
        Dim newUserMsg As New History With {.content = prompt_text, .role = "user"}

        If preserveMemory Then
            Call ai_memory.Enqueue(newUserMsg)
            Call ai_log.WriteLine(newUserMsg.GetJson(simpleDict:=True))

            If ai_memory.Count > max_memory_size Then
                While ai_memory.Count > max_memory_size
                    ai_memory.Dequeue()
                End While
            End If
        End If

        Dim req As New RequestBody With {
            .messages = If(Not preserveMemory, {newUserMsg}, ai_memory.ToArray),
            .model = model,
            .stream = True,
            .options = New RequestOptions With {.temperature = temperature},
            .tools = If(tools.IsNullOrEmpty, Nothing, tools.ToArray)
        }

        Return Await Chat(req)
    End Function

    Private Function execExternal(arg As FunctionCall) As String
        If tool_invoke Is Nothing Then
            Throw New InvalidProgramException("the invoke engine function intptr should not be nothing!")
        Else
            Return _tool_invoke(arg)
        End If
    End Function

    Private Function RequestPayloadJSON(req As RequestBody) As String
        If req.messages(0).role <> "system" AndAlso Not system_message.StringEmpty(, True) Then
            req.messages = New History() {
                New History With {
                    .role = roles.system.Description,
                    .content = system_message
                }
            }.JoinIterates(req.messages) _
             .ToArray
        End If

        Return req.GetJson(simpleDict:=True)
    End Function

    Private Async Function Chat(req As RequestBody) As Task(Of OllamaResponse)
        Dim json_input As String = RequestPayloadJSON(req)
        Dim content = New StringContent(json_input, Encoding.UTF8, "application/json")
        Dim settings As New HttpClientHandler With {
            .Proxy = Nothing,
            .UseProxy = False
        }

        Using client As New HttpClient(settings) With {.Timeout = TimeSpan.FromHours(1)}
            Dim jsonl As IEnumerable(Of String) = Await RequestMessage(client, url, content)
            Dim msg As New StringBuilder

            For Each stream As String In jsonl
                Dim result = stream.LoadJSON(Of ResponseBody)
                Dim llms_think = result.message.content

                Call ai_memory.Enqueue(result.message)
                Call ai_log.WriteLine(stream)
                Call Console.Write(llms_think)

                If llms_think = "" AndAlso Not result.message.tool_calls.IsNullOrEmpty Then
                    ' is function calls
                    Dim tool_call As ToolCall = result.message.tool_calls(0)
                    Dim invoke As FunctionCall = tool_call.function
                    Dim fval As String = If(ai_caller.CheckFunction(invoke.name), ai_caller.Call(invoke), execExternal(invoke))
                    Dim [next] As New History With {
                        .content = fval,
                        .role = "tool",
                        .tool_call_id = tool_call.id
                    }
                    Dim messages As New List(Of History)(req.messages)

                    Call ai_calls.Add(invoke)
                    Call messages.Add(result.message)
                    Call messages.Add([next])

                    req = New RequestBody(req)
                    req.messages = messages.ToArray

                    Return Await Chat(req)
                End If

                Call msg.Append(llms_think)
            Next

            Dim output As OllamaResponse = OllamaResponse.ParseResponse(msg.ToString)
            Return output
        End Using
    End Function

    Private Shared Async Function RequestMessage(client As HttpClient, url As String, content As StringContent) As Task(Of IEnumerable(Of String))
        Dim response As HttpResponseMessage = Await client.PostAsync(url, content)

        If Not response.IsSuccessStatusCode Then
            Dim msg As String = Await response.Content.ReadAsStringAsync
            msg = $"{response.StatusCode.Description}{vbCrLf}{vbCrLf}{msg}"

            Throw New Exception(msg)
        End If

        Dim responseStream As Stream = Await response.Content.ReadAsStreamAsync

        Return ReadLines(responseStream)
    End Function

    Private Shared Iterator Function ReadLines(responseStream As Stream) As IEnumerable(Of String)
        Using reader As New StreamReader(responseStream)
            Dim line As String
            While True
                line = reader.ReadLine
                If line Is Nothing Then Exit While
                Yield line
            End While
        End Using
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' TODO: dispose managed state (managed objects)
                Call ai_log.Flush()
                Call ai_log.Dispose()
            End If

            ' TODO: free unmanaged resources (unmanaged objects) and override finalizer
            ' TODO: set large fields to null
            disposedValue = True
        End If
    End Sub

    ' ' TODO: override finalizer only if 'Dispose(disposing As Boolean)' has code to free unmanaged resources
    ' Protected Overrides Sub Finalize()
    '     ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class
