Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON

Public Class OllamaProvider
    Implements ILLMProvider

    Private ReadOnly _server As String

    Public Sub New(server As String)
        _server = server
    End Sub

    Public ReadOnly Property ApiEndpoint As String Implements ILLMProvider.ApiEndpoint
        Get
            Return $"http://{_server}/api/chat"
        End Get
    End Property

    Public Async Function StreamChatAsync(options As ChatRequestOptions, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ChatResponseChunk)) Implements ILLMProvider.StreamChatAsync
        ' 1. 将统一的 ChatRequestOptions 转换为 Ollama 的 RequestBody
        Dim ollamaReq As New RequestBody With {
            .model = options.Model,
            .stream = True,
            .messages = ConvertToOllamaMessages(options.Messages),
            .tools = If(options.Tools.IsNullOrEmpty, Nothing, options.Tools.ToArray)
        }
        ' Ollama 的特殊参数映射
        If options.Temperature.HasValue Then
            ollamaReq.options = New RequestOptions With {.temperature = options.Temperature.Value}
        End If

        ' 2. 发送 HTTP 请求 (使用共享的 HttpClient)
        Dim json = ollamaReq.GetJson(simpleDict:=True)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        Dim response = Await SharedHttpClient.PostAsync(ApiEndpoint, content, cancellationToken)
        response.EnsureSuccessStatusCode()

        ' 3. 解析 Ollama 的 NDJSON 流
        Return ParseOllamaStream(Await response.Content.ReadAsStreamAsync())
    End Function

    Private Iterator Function ParseOllamaStream(stream As Stream) As IEnumerable(Of ChatResponseChunk)
        Using reader As New StreamReader(stream)
            Dim line As String
            While Not reader.EndOfStream
                line = reader.ReadLine()
                If String.IsNullOrEmpty(line) Then Continue While

                Dim result = line.LoadJSON(Of ResponseBody)
                Dim chunk As New ChatResponseChunk With {
                    .IsDone = result.done,
                    .DeltaContent = result.message.content
                }

                If result.done AndAlso Not result.message.tool_calls.IsNullOrEmpty Then
                    chunk.ToolCalls = New List(Of ToolCallInfo)
                    For Each tc In result.message.tool_calls
                        chunk.ToolCalls.Add(New ToolCallInfo With {
                            .Id = tc.id, ' 确保 Ollama 的 ToolCall 类有 id 字段
                            .FunctionName = tc.function.name,
                            .FunctionArguments = tc.function.arguments
                        })
                    Next
                End If

                Yield chunk
                If chunk.IsDone Then Exit While
            End While
        End Using
    End Function

    Private Function ConvertToOllamaMessages(messages As List(Of ChatMessage)) As History()
        ' 将统一的 ChatMessage 转为你原有的 History 数组
        ' ... 映射逻辑 ...
    End Function
End Class
