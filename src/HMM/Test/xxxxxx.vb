Imports System
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks

Module streamTest

    ' 定义 Ollama 的请求结构
    Public Class OllamaRequest
        Public Property model As String
        Public Property prompt As String
        Public Property stream As Boolean = True
    End Class

    ' 定义 Ollama 的响应结构（流式返回的单个块）
    Public Class OllamaResponse
        Public Property response As String
        Public Property done As Boolean
        ' 还有其他字段如 context, model 等，这里只处理核心字段
    End Class

    Async Function Main2() As Task
        ' Ollama 默认地址
        Dim url As String = "http://localhost:11434/api/generate"

        ' 配置 HttpClient
        Using client As New HttpClient()
            ' 设置超时时间（因为生成内容可能较久，建议设置长一点）
            client.Timeout = TimeSpan.FromMinutes(5)

            ' 构建请求体
            ' 注意：确保 model 是你在 ollama 中已经 pull 下来的模型名称，例如 "llama3", "qwen2" 等
            Dim requestData As New OllamaRequest With {
                .model = "qwen3:30b",
                .prompt = "请用 VB.NET 写一个 Hello World 程序。",
                .stream = True
            }

            Dim jsonContent As String = JsonSerializer.Serialize(requestData)
            Dim content As New StringContent(jsonContent, Encoding.UTF8, "application/json")

            Try
                Console.WriteLine("正在发送请求到 Ollama...")

                ' 发送 POST 请求
                Dim response As HttpResponseMessage = Await client.PostAsync(url, content)

                ' 确保请求成功
                response.EnsureSuccessStatusCode()

                Call "request stream".debug

                ' 获取响应流
                Using stream As Stream = Await response.Content.ReadAsStreamAsync()
                    Using reader As New StreamReader(stream)

                        Call "read stream".debug

                        ' 逐行读取流式数据
                        ' Ollama 的流式响应是按行分割的 JSON 对象
                        While Not reader.EndOfStream
                            Dim line As String = Await reader.ReadLineAsync()

                            ' 跳过空行
                            If String.IsNullOrWhiteSpace(line) Then Continue While

                            Try
                                ' 解析单行 JSON
                                Dim chunkResponse As OllamaResponse = JsonSerializer.Deserialize(Of OllamaResponse)(line)

                                ' 输出内容到控制台（不换行，模拟打字机效果）
                                If chunkResponse IsNot Nothing AndAlso chunkResponse.response IsNot Nothing Then
                                    Console.Write(chunkResponse.response)
                                End If

                                ' 检查是否完成
                                If chunkResponse IsNot Nothing AndAlso chunkResponse.done Then
                                    Console.WriteLine() ' 最后换行
                                    Console.WriteLine("接收完成。")
                                    Exit While
                                End If

                            Catch parseEx As JsonException
                                ' 如果某行解析失败，打印错误（调试用）
                                Console.WriteLine($"解析错误: {parseEx.Message} - 内容: {line}")
                            End Try
                        End While

                        Call "end of stream reader".debug
                    End Using
                End Using

            Catch ex As Exception
                Console.WriteLine($"发生错误: {ex.Message}")
            End Try
        End Using

        ' 防止控制台立即关闭
        Console.WriteLine("按任意键退出...")
        Console.ReadKey()
    End Function

End Module