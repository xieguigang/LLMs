' ============================================================================
' Example.vb
' LLMClient 模块使用示例
'
' 演示内容:
'   1. 使用 ChatGLM 在线服务进行对话 (含工具调用)
'   2. 使用 Ollama 本地服务进行对话 (含工具调用)
'   3. 自定义工具类的编写方式 (反射自动注册)
'   4. 上下文记忆管理 (多轮对话)
' ============================================================================

Imports System
Imports System.Threading.Tasks
Imports LLMSdk

Module Program

    Sub Main()
        ' 同步等待异步主方法
        MainAsync().GetAwaiter().GetResult()
    End Sub

    Async Function MainAsync() As Task

        ' ============================================================
        ' 示例 1: 使用 ChatGLM (智谱 AI) 在线服务
        ' ============================================================
        Console.WriteLine("="c * 60)
        Console.WriteLine("示例 1: ChatGLM 在线服务")
        Console.WriteLine("="c * 60)

        Try
            ' 1. 创建客户端 (替换为你的 API Key)
            Using client As New ChatGLMClient(
                apiKey:="your-api-key-here",
                model:="glm-4")

                ' 2. 创建对话管理器 (设置系统提示词)
                Dim conversation As New ConversationManager(
                    "你是一个智能助手, 可以调用工具帮助用户解决问题")

                ' 3. 注册工具 (传入包含 Public Function 的对象实例)
                Dim tools As New MyTools()
                client.RegisterTools(tools)

                ' 4. 第一轮对话 (LLM 会调用 GetWeather 工具)
                Console.WriteLine(vbCrLf & "用户: 北京今天天气怎么样?")
                Dim r1 = Await client.ChatAsync(conversation, "北京今天天气怎么样?")
                Console.WriteLine($"助手: {r1}")

                ' 5. 第二轮对话 (上下文记忆 - LLM 记得上一轮内容)
                Console.WriteLine(vbCrLf & "用户: 那上海呢?")
                Dim r2 = Await client.ChatAsync(conversation, "那上海呢?")
                Console.WriteLine($"助手: {r2}")

                ' 6. 第三轮对话 (调用计算工具)
                Console.WriteLine(vbCrLf & "用户: 计算 15 乘以 23 等于多少?")
                Dim r3 = Await client.ChatAsync(conversation, "计算 15 乘以 23 等于多少?")
                Console.WriteLine($"助手: {r3}")
            End Using
        Catch ex As Exception
            Console.WriteLine($"ChatGLM 示例出错: {ex.Message}")
        End Try

        ' ============================================================
        ' 示例 2: 使用 Ollama 本地部署服务
        ' ============================================================
        Console.WriteLine(vbCrLf & "="c * 60)
        Console.WriteLine("示例 2: Ollama 本地服务")
        Console.WriteLine("="c * 60)

        Try
            ' 1. 创建客户端 (确保 Ollama 已安装并拉取了模型: ollama pull llama3)
            Using client As New OllamaClient(
                baseUrl:="http://localhost:11434",
                model:="llama3")

                ' 2. 创建对话管理器
                Dim conversation As New ConversationManager(
                    "你是一个智能助手, 可以调用工具帮助用户解决问题")

                ' 3. 注册工具
                Dim tools As New MyTools()
                client.RegisterTools(tools)

                ' 4. 对话 (LLM 会调用 Multiply 工具)
                Console.WriteLine(vbCrLf & "用户: 计算 15 乘以 23 等于多少?")
                Dim r = Await client.ChatAsync(conversation, "计算 15 乘以 23 等于多少?")
                Console.WriteLine($"助手: {r}")

                ' 5. 调用无参数工具
                Console.WriteLine(vbCrLf & "用户: 现在几点了?")
                r = Await client.ChatAsync(conversation, "现在几点了?")
                Console.WriteLine($"助手: {r}")
            End Using
        Catch ex As Exception
            Console.WriteLine($"Ollama 示例出错: {ex.Message}")
            Console.WriteLine("(请确保 Ollama 服务已启动: ollama serve)")
        End Try

        ' ============================================================
        ' 示例 3: 上下文记忆管理演示
        ' ============================================================
        Console.WriteLine(vbCrLf & "="c * 60)
        Console.WriteLine("示例 3: 上下文记忆管理")
        Console.WriteLine("="c * 60)

        Using client As New ChatGLMClient("your-api-key-here", "glm-4")
            Dim conv As New ConversationManager("你是一个助手")
            conv.MaxMessages = 10  ' 最多保留 10 条消息

            ' 多轮对话, 上下文会被自动管理
            Console.WriteLine(vbCrLf & "用户: 我叫张三")
            Console.WriteLine($"助手: {Await client.ChatAsync(conv, "我叫张三")}")

            Console.WriteLine(vbCrLf & "用户: 我喜欢编程")
            Console.WriteLine($"助手: {Await client.ChatAsync(conv, "我喜欢编程")}")

            Console.WriteLine(vbCrLf & "用户: 我叫什么名字? 我喜欢什么?")
            Console.WriteLine($"助手: {Await client.ChatAsync(conv, "我叫什么名字? 我喜欢什么?")}")

            ' 清空对话 (保留系统提示词)
            conv.Reset()
            Console.WriteLine(vbCrLf & "(已清空对话历史)")
            Console.WriteLine("用户: 我叫什么名字?")
            Console.WriteLine($"助手: {Await client.ChatAsync(conv, "我叫什么名字?")}")
        End Using

        Console.WriteLine(vbCrLf & "按任意键退出...")
        Console.ReadKey()
    End Function

End Module

' ============================================================================
' 自定义工具类
' ============================================================================
' 规则:
'   - 类中的 Public Function 会被自动注册为工具
'   - 使用 <ToolDescription> 标注工具描述 (LLM 据此判断何时调用)
'   - 使用 <ParameterDescription> 标注参数描述
'   - 返回值会被序列化为 JSON 返回给 LLM
' ============================================================================

''' <summary>
''' 示例工具集合
''' </summary>
Public Class MyTools

    ''' <summary>
    ''' 获取指定城市的天气信息
    ''' LLM 看到描述后会判断: 用户问天气 -> 调用此函数
    ''' </summary>
    <ToolDescription("获取指定城市的当前天气信息, 包括温度和天气状况")>
    Public Function GetWeather(
        <ParameterDescription("城市名称, 如: 北京、上海、广州")> city As String
    ) As String
        ' 这里模拟天气查询, 实际可替换为真实天气 API 调用
        Dim rnd As New Random(city.GetHashCode())
        Dim temp As Integer = rnd.Next(15, 35)
        Dim weathers() As String = {"晴", "多云", "阴", "小雨", "大雨"}
        Dim weather As String = weathers(rnd.Next(weathers.Length))

        Return $"{{""city"":""{city}"",""temperature"":{temp},""weather"":""{weather}"",""humidity"":{rnd.Next(30, 90)}}}"
    End Function

    ''' <summary>
    ''' 计算两个数字的乘积
    ''' </summary>
    <ToolDescription("计算两个数字的乘积")>
    Public Function Multiply(
        <ParameterDescription("第一个乘数")> a As Double,
        <ParameterDescription("第二个乘数")> b As Double
    ) As Double
        Return a * b
    End Function

    ''' <summary>
    ''' 获取当前日期时间 (无参数工具示例)
    ''' </summary>
    <ToolDescription("获取当前的系统日期和时间")>
    Public Function GetCurrentTime() As String
        Return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    End Function

    ''' <summary>
    ''' 字符串拼接工具 (多参数示例)
    ''' </summary>
    <ToolDescription("将多个字符串拼接在一起")>
    Public Function ConcatStrings(
        <ParameterDescription("第一个字符串")> str1 As String,
        <ParameterDescription("第二个字符串")> str2 As String,
        <ParameterDescription("分隔符, 默认为空格")> Optional separator As String = " "
    ) As String
        Return String.Join(separator, New String() {str1, str2})
    End Function

End Class
