Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json

Public Class OpenAIProvider
    Implements ILLMProvider

    Private ReadOnly _baseUrl As String
    Private ReadOnly _apiKey As String

    Public Sub New(baseUrl As String, apiKey As String)
        _baseUrl = baseUrl
        _apiKey = apiKey
    End Sub

    Public ReadOnly Property ApiEndpoint As String Implements ILLMProvider.ApiEndpoint
        Get
            Return $"{_baseUrl}/v1/chat/completions"
        End Get
    End Property

    Public Async Function StreamChatAsync(options As ChatRequestOptions, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ChatResponseChunk)) Implements ILLMProvider.StreamChatAsync
        ' 1. 转换为 OpenAI 的请求体结构
        Dim openaiReq = New With {
            .model = options.Model,
            .stream = True,
            .messages = ConvertToOpenAIMessages(options.Messages),
            .tools = ConvertToOpenAITools(options.Tools),
            .temperature = options.Temperature,
            .max_tokens = options.MaxTokens
        }

        ' 2. 发送带 Auth 的 HTTP 请求
        Dim json = openaiReq.GetJson()
        Using request As New HttpRequestMessage(HttpMethod.Post, ApiEndpoint)
            request.Headers.Authorization = New Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey)
            request.Content = New StringContent(json, Encoding.UTF8, "application/json")

            Dim response = Await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            response.EnsureSuccessStatusCode()

            ' 3. 解析 OpenAI 的 SSE 流
            Return ParseOpenAIStream(Await response.Content.ReadAsStreamAsync())
        End Using
    End Function

    Private Iterator Function ParseOpenAIStream(stream As Stream) As IEnumerable(Of ChatResponseChunk)
        Using reader As New StreamReader(stream)
            Dim line As String
            While Not reader.EndOfStream
                line = reader.ReadLine()
                ' OpenAI 流式格式: "data: {...}"
                If String.IsNullOrEmpty(line) OrElse Not line.StartsWith("data: ") Then Continue While

                Dim data = line.Substring(6).Trim()
                If data = "[DONE]" Then
                    Yield New ChatResponseChunk With {.IsDone = True}
                    Exit While
                End If

                Dim result = JsonParser.Parse(data)
                Dim delta = result("choices")(0)("delta")
                Dim chunk As New ChatResponseChunk With {.IsDone = False}

                ' 解析文本
                If delta.ContainsKey("content") AndAlso delta("content") IsNot Nothing Then
                    chunk.DeltaContent = delta("content").ToString()
                End If

                ' 解析 Tool Calls (OpenAI 的 tool_calls 在 delta 里是数组片段，需要拼装)
                If delta.ContainsKey("tool_calls") Then
                    chunk.ToolCalls = New List(Of ToolCallInfo)
                    Dim tcArray = delta("tool_calls")
                    For Each tc In tcArray
                        chunk.ToolCalls.Add(New ToolCallInfo With {
                            .Id = tc("id")?.ToString(),
                            .FunctionName = tc("function")("name")?.ToString(),
                            .FunctionArguments = tc("function")("arguments")?.ToString()
                        })
                    Next
                End If

                Yield chunk
            End While
        End Using
    End Function

    ' ... ConvertToOpenAIMessages 和 ConvertToOpenAITools 的映射逻辑 ...
End Class
