Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON.FunctionCall

Public Class OpenAIProvider : Implements ILLMProvider

    ReadOnly _baseUrl As String
    ReadOnly _apiKey As String

    Public ReadOnly Property ApiEndpoint As String Implements ILLMProvider.ApiEndpoint
        Get
            Return $"{_baseUrl}/v1/chat/completions"
        End Get
    End Property

    Public Sub New(baseUrl As String, apiKey As String)
        If Not (baseUrl.StartsWith("http://") OrElse baseUrl.StartsWith("https://")) Then
            baseUrl = $"https://{baseUrl}"
        End If

        _baseUrl = baseUrl
        _apiKey = apiKey
    End Sub

    Public Async Function StreamChatAsync(options As ChatRequestOptions, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ChatResponseChunk)) Implements ILLMProvider.StreamChatAsync
        ' 1. 转换为 OpenAI 的请求体结构
        Dim openaiReq As New Dictionary(Of String, Object) From {
            {"model", options.Model},
            {"stream", True},
            {"messages", ConvertToOpenAIMessages(options.Messages).ToArray}
        }
        If options.Temperature.HasValue Then openaiReq("temperature") = options.Temperature.Value
        If options.MaxTokens.HasValue Then openaiReq("max_tokens") = options.MaxTokens.Value
        If Not options.Tools.IsNullOrEmpty Then
            openaiReq("tools") = ConvertToOpenAITools(options.Tools).ToArray
        End If

        ' 2. 发送带 Auth 的 HTTP 请求
        Dim json As String = JSONSerializer.GetJson(openaiReq, enumToStr:=True)

        Using request As New HttpRequestMessage(HttpMethod.Post, ApiEndpoint)
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _apiKey)
            request.Content = New StringContent(json, Encoding.UTF8, "application/json")

            Dim response = Await LLMClient.SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            response.EnsureSuccessStatusCode()

            ' 3. 解析 OpenAI 的 SSE 流
            Return ParseOpenAIStream(Await response.Content.ReadAsStreamAsync())
        End Using
    End Function

    Private Iterator Function ParseOpenAIStream(stream As Stream) As IEnumerable(Of ChatResponseChunk)
        Using reader As New StreamReader(stream)
            Dim line As String
            ' OpenAI 的 tool_calls 以 index 分片流式下发，需要跨 chunk 拼装
            Dim pending As New List(Of ToolCallInfo)
            Dim argsBuf As New List(Of StringBuilder)

            While Not reader.EndOfStream
                line = reader.ReadLine()
                ' OpenAI 流式格式: "data: {...}"
                If String.IsNullOrEmpty(line) OrElse Not line.StartsWith("data: ") Then Continue While

                Dim data = line.Substring(6).Trim()
                If data = "[DONE]" Then
                    Dim doneChunk As New ChatResponseChunk With {.IsDone = True}
                    If pending.Count > 0 Then
                        ' 在流结束时，将拼装好的参数 JSON 字符串解析为统一的 Dictionary
                        For i As Integer = 0 To pending.Count - 1
                            pending(i).FunctionArguments = ParseArguments(argsBuf(i).ToString())
                        Next
                        doneChunk.ToolCalls = pending
                    End If
                    Yield doneChunk
                    Exit While
                End If

                Dim result As JsonObject = JsonParser.Parse(data)
                Dim delta As JsonObject = DirectCast(DirectCast(result("choices"), JsonArray)(0), JsonObject)("delta")
                Dim chunk As New ChatResponseChunk With {.IsDone = False}

                ' 解析正文文本
                If delta.HasObjectKey("content") AndAlso delta("content") IsNot Nothing Then
                    chunk.DeltaContent = GetString(delta("content"))
                End If

                ' 解析思考(reasoning)增量（o-series / DeepSeek 兼容接口）
                If delta.HasObjectKey("reasoning_content") AndAlso delta("reasoning_content") IsNot Nothing Then
                    chunk.ThinkContent = GetString(delta("reasoning_content"))
                ElseIf delta.HasObjectKey("think") AndAlso delta("think") IsNot Nothing Then
                    chunk.ThinkContent = GetString ( delta("think"))
                End If

                ' 解析 Tool Calls 分片并按 index 拼装
                If delta.HasObjectKey("tool_calls") Then
                    Dim tcArray As JsonArray = delta("tool_calls")
                    For Each tc As JsonObject In tcArray
                        Dim idx As Integer = If(tc.HasObjectKey("index") AndAlso tc("index") IsNot Nothing, Integer.Parse(tc("index").ToString()), pending.Count)
                        While pending.Count <= idx
                            pending.Add(New ToolCallInfo With {.FunctionArguments = New Dictionary(Of String, String)})
                            argsBuf.Add(New StringBuilder())
                        End While

                        If tc.HasObjectKey("id") AndAlso tc("id") IsNot Nothing Then
                            Dim idStr = GetString(tc("id"))
                            If Not String.IsNullOrEmpty(idStr) Then pending(idx).Id = idStr
                        End If

                        If tc.HasObjectKey("function") AndAlso tc("function") IsNot Nothing Then
                            Dim func As JsonObject = tc("function")
                            If func.HasObjectKey("name") AndAlso func("name") IsNot Nothing Then
                                Dim nm = GetString(func("name"))
                                If Not String.IsNullOrEmpty(nm) Then pending(idx).FunctionName &= nm
                            End If
                            If func.HasObjectKey("arguments") AndAlso func("arguments") IsNot Nothing Then
                                Dim argStr = GetString(func("arguments"))
                                If Not String.IsNullOrEmpty(argStr) Then argsBuf(idx).Append(argStr)
                            End If
                        End If
                    Next
                End If

                Yield chunk
            End While
        End Using
    End Function

    Private Shared Function GetString(strval As JsonValue) As String
        If strval.IsEmptyString Then
            Return ""
        Else
            Return strval.GetStripString(decodeMetachar:=True)
        End If
    End Function

    ''' <summary>
    ''' 将拼装后的参数 JSON 字符串解析为统一的 Dictionary(Of String, String)
    ''' </summary>
    Private Shared Function ParseArguments(jsonStr As String) As Dictionary(Of String, String)
        If String.IsNullOrWhiteSpace(jsonStr) Then Return New Dictionary(Of String, String)
        Try
            Dim dict = jsonStr.LoadJSON(Of Dictionary(Of String, String))
            If dict Is Nothing Then Return New Dictionary(Of String, String)
            Return dict
        Catch
            Return New Dictionary(Of String, String)
        End Try
    End Function

    Private Function ConvertToOpenAIMessages(messages As List(Of ChatMessage)) As List(Of Dictionary(Of String, Object))
        Dim list As New List(Of Dictionary(Of String, Object))
        If messages Is Nothing Then Return list

        For Each m In messages
            If m Is Nothing Then Continue For

            If Not m.ToolCalls.IsNullOrEmpty Then
                ' assistant 消息，带有 tool_calls（历史中的工具调用）
                Dim tcs As New List(Of Dictionary(Of String, Object))
                For Each tc In m.ToolCalls
                    Dim argsJson As String = If(tc.FunctionArguments.IsNullOrEmpty, "{}", tc.FunctionArguments.GetJson(simpleDict:=True))
                    tcs.Add(New Dictionary(Of String, Object) From {
                        {"id", If(String.IsNullOrEmpty(tc.Id), Guid.NewGuid().ToString("N"), tc.Id)},
                        {"type", "function"},
                        {"function", New Dictionary(Of String, Object) From {
                            {"name", tc.FunctionName},
                            {"arguments", argsJson}
                        }}
                    })
                Next
                list.Add(New Dictionary(Of String, Object) From {
                    {"role", "assistant"},
                    {"content", If(m.Content, "")},
                    {"tool_calls", tcs}
                })
            ElseIf m.Role = "tool" Then
                list.Add(New Dictionary(Of String, Object) From {
                    {"role", "tool"},
                    {"tool_call_id", m.ToolCallId},
                    {"content", m.Content}
                })
            Else
                list.Add(New Dictionary(Of String, Object) From {
                    {"role", m.Role},
                    {"content", m.Content}
                })
            End If
        Next
        Return list
    End Function

    Private Function ConvertToOpenAITools(tools As List(Of FunctionTool)) As List(Of Dictionary(Of String, Object))
        Dim list As New List(Of Dictionary(Of String, Object))
        If tools Is Nothing Then Return list

        For Each t In tools
            Dim fn = t.function
            Dim paramObj As New Dictionary(Of String, Object) From {
                {"type", If(fn.parameters?.type, "object")}
            }

            If fn.parameters IsNot Nothing AndAlso Not fn.parameters.properties.IsNullOrEmpty Then
                Dim props As New Dictionary(Of String, Object)
                For Each kvp In fn.parameters.properties
                    props(kvp.Key) = New Dictionary(Of String, Object) From {
                        {"type", If(kvp.Value.type, "string")},
                        {"description", kvp.Value.description}
                    }
                Next
                paramObj("properties") = props
            Else
                paramObj("properties") = New Dictionary(Of String, Object)()
            End If

            If fn.parameters IsNot Nothing AndAlso Not fn.parameters.required.IsNullOrEmpty Then
                paramObj("required") = fn.parameters.required
            End If

            list.Add(New Dictionary(Of String, Object) From {
                {"type", If(t.type, "function")},
                {"function", New Dictionary(Of String, Object) From {
                    {"name", fn.name},
                    {"description", fn.description},
                    {"parameters", paramObj}
                }}
            })
        Next
        Return list
    End Function

    ''' <summary>
    ''' 获取模型信息：向 OpenAI 的 GET /v1/models/{model} 发起请求（携带 Bearer 鉴权），并映射为统一的 <see cref="ModelInfo"/>
    ''' </summary>
    Public Async Function GetModelInformation(model As String, timeout As Double, verbose As Boolean) As Task(Of ModelInfo) Implements ILLMProvider.GetModelInformation
        ' ApiEndpoint 形如 {base}/v1/chat/completions，推导为 {base}/v1/models/{model}
        Dim modelsUrl As String = ApiEndpoint.Replace("/v1/chat/completions", "/v1/models/") & model

        Using request As New HttpRequestMessage(HttpMethod.Get, modelsUrl)
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _apiKey)

            Using source = New Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout))
                Dim response = Await LLMClient.SharedHttpClient.SendAsync(request, source.Token)
                response.EnsureSuccessStatusCode()
                Dim respText = Await response.Content.ReadAsStringAsync()
                Dim raw As JsonObject = JsonParser.Parse(respText)

                Dim info As New ModelInfo With {
                    .Provider = "openai",
                    .Raw = raw
                }

                If raw.HasObjectKey("id") AndAlso raw("id") IsNot Nothing Then info.Id = GetString(raw("id"))
                If raw.HasObjectKey("owned_by") AndAlso raw("owned_by") IsNot Nothing Then info.OwnedBy = GetString(raw("owned_by"))

                ' created 为 Unix 秒时间戳
                If raw.HasObjectKey("created") AndAlso raw("created") IsNot Nothing Then
                    Dim createdLong As Long
                    If Long.TryParse(GetString(raw("created")), createdLong) Then
                        info.CreatedAt = createdLong
                    End If
                End If

                Return info
            End Using
        End Using
    End Function
End Class
