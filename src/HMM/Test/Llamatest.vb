Imports Ollama
Imports Ollama.JSON.FunctionCall
Imports OllamaServer = Ollama.Ollama

Module Llamatest

    Sub Main2()
        ' Call Llama2.Run("E:\HMM\data\Llama2\stories15M.bin", tokenizer:="E:\HMM\data\Llama2\tokenizer.bin")

        '  Dim test_parse = DeepSeekResponse.ParseResponse(DeepSeekResponse.who_are_you)
        '  Dim list As String() = "E:\HMM\test\stream.jsonl".ReadAllLines


        ' test deepseek
        ' deepseek-r1:32b
        Dim result = DeepSeekResponse.Chat("who are you?", "127.0.0.1:11434", "qwen3:30b")

        Call Console.WriteLine(result.think)
        Call Console.WriteLine()
        Call Console.WriteLine(New String("-", 120))
        Call Console.WriteLine(result.output)
        Call Console.WriteLine()

        Pause()

        Dim tool_time As New FunctionModel With {
            .description = "get time of now",
            .name = "get_time",
            .parameters = New FunctionParameters With {
                .required = {"loc"},
                .properties = New Dictionary(Of String, ParameterProperties) From {
                    {
                        "loc", New ParameterProperties With {
                            .name = "loc",
                            .description = "the location name for get current local time"
                        }
                    }
                }
            }
        }
        Dim ollama As New OllamaServer("qwen3:30b", "127.0.0.1:11434") With {
            .tools = New List(Of FunctionTool)(FunctionTool.CreateToolSet(tool_time)),
            .tool_invoke = AddressOf RunFunctionTool,
            .temperature = 0.99
        }
        Dim test_call = ollama.Chat("what is the time of beijing city now?").GetAwaiter.GetResult

        Call Console.WriteLine(test_call.think)
        Call Console.WriteLine(New String("-", 120))
        Call Console.WriteLine(test_call.output)

        Pause()
    End Sub

    Private Function RunFunctionTool(invoke As FunctionCall) As String
        Return $"current time of {invoke.arguments.First.Value} is yahaha!"
    End Function
End Module
