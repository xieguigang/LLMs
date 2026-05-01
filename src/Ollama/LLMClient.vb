Imports System.IO
Imports System.Text
Imports System.Threading
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall

Public Class LLMClient : Implements IDisposable

    Private ReadOnly _provider As ILLMProvider
    Private ReadOnly _model As String

    Dim ai_memory As New Queue(Of History)
    Dim ai_caller As New FunctionCaller
    Dim ai_log As TextWriter
    Dim ai_calls As New List(Of FunctionCall)

    Public Property temperature As Double = 0.1
    Public Property tools As List(Of FunctionTool)

    Sub New(provider As ILLMProvider, model As String, Optional logfile As String = Nothing)
        _provider = provider
        _model = model
        ' ... 初始化日志等 ...
    End Sub

    Public Async Function Chat(message As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
        Dim newUserMsg As New ChatMessage With {.Role = "user", .Content = message}
        ai_memory.Enqueue(newUserMsg)

        Dim reqOptions As New ChatRequestOptions With {
            .Model = _model,
            .Messages = ai_memory.ToList(),
            .Tools = Me.tools,
            .Temperature = Me.temperature
        }

        ' 循环处理：如果模型返回 Tool Calls，执行后继续请求，直到返回最终文本
        Dim maxRounds As Integer = 10
        Dim currentReq = reqOptions

        For round = 1 To maxRounds
            Dim fullContent As New StringBuilder
            Dim toolCallsToExecute As New List(Of ToolCallInfo)

            ' 1. 通过 Provider 拉取流式数据
            Dim chunks = Await _provider.StreamChatAsync(currentReq, cancellationToken)

            ' 2. 处理流式响应
            For Each chunk In chunks
                ' 展示增量文本到 UI
                Console.Write(chunk.DeltaContent)
                fullContent.Append(chunk.DeltaContent)

                ' 收集 Tool Calls
                If chunk.ToolCalls IsNot Nothing Then
                    toolCallsToExecute.AddRange(chunk.ToolCalls)
                End If

                If chunk.IsDone Then Exit For
            Next

            ' 3. 如果没有工具调用，直接返回结果
            If toolCallsToExecute.IsNullOrEmpty Then
                Dim finalAssistantMsg As New ChatMessage With {.Role = "assistant", .Content = fullContent.ToString()}
                ai_memory.Enqueue(finalAssistantMsg)
                Return fullContent.ToString()
            End If

            ' 4. 如果有工具调用，执行并追加历史记录
            ' 先把 Assistant 的工具调用意图加入历史
            Dim assistantMsg As New ChatMessage With {
                .Role = "assistant",
                .Content = fullContent.ToString(),
                .ToolCalls = toolCallsToExecute
            }
            ai_memory.Enqueue(assistantMsg)
            currentReq.Messages.Add(assistantMsg)

            ' 逐个执行工具
            For Each tc In toolCallsToExecute
                Dim fval As String = ExecuteTool(tc) ' 封装你的 ai_caller 逻辑
                Dim toolMsg As New ChatMessage With {
                    .Role = "tool",
                    .ToolCallId = tc.Id,
                    .Content = fval
                }
                ai_memory.Enqueue(toolMsg)
                currentReq.Messages.Add(toolMsg)
            Next

            ' 5. 准备下一轮请求 (带上工具结果)
            currentReq.Tools = Nothing ' 通常第二轮不需要再传 tools 定义

        Next

        Throw New Exception("Exceeded max tool call rounds")
    End Function

End Class
