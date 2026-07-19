Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall

Public Class OllamaProvider : Implements ILLMProvider

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
        Dim response = Await LLMClient.SharedHttpClient.PostAsync(ApiEndpoint, content, cancellationToken)
        response.EnsureSuccessStatusCode()

        ' 3. 解析 Ollama 的 NDJSON 流
        Return ParseOllamaStream(Await response.Content.ReadAsStreamAsync())
    End Function

    Private Iterator Function ParseOllamaStream(stream As Stream) As IEnumerable(Of ChatResponseChunk)
        Using reader As New StreamReader(stream)
            Dim line As String
            ' 累积原始输出，以便正确处理可能被分片在多个 chunk 之间的 <think>/</think> 标签
            Dim raw As New StringBuilder()
            Dim pos As Integer = 0
            Dim inThink As Boolean = False

            While Not reader.EndOfStream
                line = reader.ReadLine()
                If String.IsNullOrEmpty(line) Then Continue While

                Dim result = line.LoadJSON(Of ResponseBody)

                If result.message IsNot Nothing AndAlso Not String.IsNullOrEmpty(result.message.content) Then
                    raw.Append(result.message.content)
                End If

                Dim chunk As New ChatResponseChunk With {
                    .IsDone = result.done
                }

                ' 切分 think / output 增量（状态机）
                Dim s As String = raw.ToString()
                Dim thinkPart As New StringBuilder()
                Dim outPart As New StringBuilder()
                Dim i As Integer = pos
                Dim n As Integer = s.Length
                While i < n
                    Dim lt As Integer = s.IndexOf("<"c, i)
                    If lt < 0 Then
                        ' 余下全部为普通文本
                        Dim tail = s.Substring(i)
                        If inThink Then thinkPart.Append(tail) Else outPart.Append(tail)
                        i = n
                    Else
                        ' < 之前的普通文本
                        If lt > i Then
                            Dim normal = s.Substring(i, lt - i)
                            If inThink Then thinkPart.Append(normal) Else outPart.Append(normal)
                        End If

                        Dim rest = s.Substring(lt)
                        If rest.StartsWith("<think>") Then
                            inThink = True
                            i = lt + 7
                        ElseIf rest.StartsWith("</think>") Then
                            inThink = False
                            i = lt + 8
                        ElseIf IsTagPrefix(rest) Then
                            ' 可能是被分片的标签（如 "<thi"），留到下一 chunk 继续拼接
                            i = lt
                            Exit While
                        Else
                            ' 不是 think 标签，按普通文本处理这个 '<'
                            If inThink Then thinkPart.Append("<"c) Else outPart.Append("<"c)
                            i = lt + 1
                        End If
                    End If
                End While
                pos = i
                chunk.ThinkContent = thinkPart.ToString()
                chunk.DeltaContent = outPart.ToString()

                ' Ollama 的 tool_calls 在 done=true 的最终消息中一次性给出
                If result.done AndAlso result.message IsNot Nothing AndAlso Not result.message.tool_calls.IsNullOrEmpty Then
                    chunk.ToolCalls = New List(Of ToolCallInfo)
                    For Each tc In result.message.tool_calls
                        Dim callId = If(String.IsNullOrEmpty(tc.id), "call_" & Guid.NewGuid().ToString("N"), tc.id)
                        chunk.ToolCalls.Add(New ToolCallInfo With {
                            .Id = callId,
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

    ''' <summary>
    ''' 判断 <paramref name="s"/> 是否可能是被网络分片截断的 &lt;think&gt;/&lt;/think&gt; 标签前缀
    ''' </summary>
    Private Shared Function IsTagPrefix(s As String) As Boolean
        If String.IsNullOrEmpty(s) OrElse s(0) <> "<"c Then Return False
        If s.Length >= 7 AndAlso s.StartsWith("<think>") Then Return False
        If s.Length >= 8 AndAlso s.StartsWith("</think>") Then Return False
        Return "<think>".StartsWith(s) OrElse "</think>".StartsWith(s)
    End Function

    Private Function ConvertToOllamaMessages(messages As List(Of ChatMessage)) As History()
        If messages Is Nothing OrElse messages.Count = 0 Then Return New History() {}

        Dim result(messages.Count - 1) As History
        For m As Integer = 0 To messages.Count - 1
            Dim msg = messages(m)
            Dim h As New History With {
                .role = msg.Role,
                .content = msg.Content
            }

            If Not msg.ToolCalls.IsNullOrEmpty Then
                Dim tcs(msg.ToolCalls.Count - 1) As ToolCall
                For t As Integer = 0 To msg.ToolCalls.Count - 1
                    Dim tc = msg.ToolCalls(t)
                    tcs(t) = New ToolCall With {
                        .id = tc.Id,
                        .type = "function",
                        .function = New FunctionCall With {
                            .name = tc.FunctionName,
                            .arguments = tc.FunctionArguments
                        }
                    }
                Next
                h.tool_calls = tcs
            ElseIf Not String.IsNullOrEmpty(msg.ToolCallId) Then
                h.tool_call_id = msg.ToolCallId
            End If

            result(m) = h
        Next
        Return result
    End Function

    Public Shared Async Function Chat(prompt_text As String, url As String, model As String) As Task(Of LLMsResponse)
        Return Await New LLMClient(New OllamaProvider(url), model).Chat(prompt_text)
    End Function
End Class
