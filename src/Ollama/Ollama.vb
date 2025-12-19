Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices
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
            Optional preserveMemory As Boolean = False)

        Dim temp_logfile As String = TempFileSystem.GetAppSysTempFile(".jsonl", prefix:="ollama_log_" & App.PID & "-history_message")

        Me.preserveMemory = preserveMemory
        Me.model = model
        Me.server = server
        Me.ai_log = New StreamWriter(
            If(logfile.StringEmpty(, True), temp_logfile, logfile) _
                .Open(FileMode.OpenOrCreate, doClear:=True)
        )
    End Sub

    Public Function GetModelInformation(Optional timeout As Double = 1) As JsonObject
        Dim req As New RequestShowModelInformation With {.model = model}
        Dim url As String = $"http://{_server}/api/show"
        Dim json_input As String = req.GetJson(maskReadonly:=True)
        Dim content = New StringContent(json_input, Encoding.UTF8, "application/json")
        Dim settings As New HttpClientHandler With {
            .Proxy = Nothing,
            .UseProxy = False
        }
        Using client As New HttpClient(settings) With {.Timeout = TimeSpan.FromSeconds(timeout)}
            Dim resp As String = RequestMessage(client, url, content).JoinBy(vbCrLf)
            Dim data As JsonObject = JsonParser.Parse(resp)
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

    Public Sub AddFunction(func As FunctionModel, Optional f As Func(Of FunctionCall, String) = Nothing)
        If tools Is Nothing Then
            tools = New List(Of FunctionTool)
        End If

        Call tools.Add(New FunctionTool With {.[function] = func})

        If Not f Is Nothing Then
            Call ai_caller.Register(func.name, f)
        End If
    End Sub

    Public Function Chat(message As String) As DeepSeekResponse
        Dim newUserMsg As New History With {.content = message, .role = "user"}

        If preserveMemory Then
            Call ai_memory.Enqueue(newUserMsg)
            Call ai_log.WriteLine(newUserMsg.GetJson(simpleDict:=True))

            If ai_memory.Count > max_memory_size Then
                For i As Integer = 0 To max_memory_size
                    ai_memory.Dequeue()

                    If ai_memory.Count <= max_memory_size Then
                        Exit For
                    End If
                Next
            End If
        End If

        Dim req As New RequestBody With {
            .messages = If(Not preserveMemory, {newUserMsg}, ai_memory.ToArray),
            .model = model,
            .stream = True,
            .temperature = 0.1,
            .tools = If(tools.IsNullOrEmpty, Nothing, tools.ToArray)
        }

        Return Chat(req)
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

    Private Function Chat(req As RequestBody) As DeepSeekResponse
        Dim json_input As String = RequestPayloadJSON(req)
        Dim content = New StringContent(json_input, Encoding.UTF8, "application/json")
        Dim settings As New HttpClientHandler With {
            .Proxy = Nothing,
            .UseProxy = False
        }

        Using client As New HttpClient(settings) With {.Timeout = TimeSpan.FromHours(1)}
            Dim jsonl As IEnumerable(Of String) = RequestMessage(client, url, content)
            Dim msg As New StringBuilder

            For Each stream As String In jsonl
                Dim result = stream.LoadJSON(Of ResponseBody)
                Dim deepseek_think = result.message.content

                Call ai_memory.Enqueue(result.message)
                Call ai_log.WriteLine(stream)

                If deepseek_think = "" AndAlso Not result.message.tool_calls.IsNullOrEmpty Then
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

                    Return Chat(req)
                End If

                Call msg.Append(deepseek_think)
            Next

            Dim output As DeepSeekResponse = DeepSeekResponse.ParseResponse(msg.ToString)
            Return output
        End Using
    End Function

    Private Shared Iterator Function RequestMessage(client As HttpClient, url As String, content As StringContent) As IEnumerable(Of String)
        Dim response As HttpResponseMessage = client.PostAsync(url, content).GetAwaiter.GetResult

        If Not response.IsSuccessStatusCode Then
            Dim msg As String = response.Content.ReadAsStringAsync.GetAwaiter.GetResult
            msg = $"{response.StatusCode.Description}{vbCrLf}{vbCrLf}{msg}"

            Throw New Exception(msg)
        End If

        Dim responseStream As Stream = response.Content.ReadAsStream
        Dim reader As New StreamReader(responseStream)
        Dim line As String

        While True
            line = reader.ReadLineAsync.GetAwaiter.GetResult
            If line Is Nothing Then
                Exit While
            End If
            Yield line
        End While
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
