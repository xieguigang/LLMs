
' ============================================================================
' Agent配置类 - 用于简化Agent的创建和配置
' ============================================================================

''' <summary>
''' 科研文献调研Agent的配置选项
''' </summary>
Public Class ResearchAgentConfig

    ''' <summary>
    ''' Ollama服务端点URL（默认：http://localhost:11434）
    ''' </summary>
    Public Property OllamaEndpoint As String = "http://localhost:11434"

    ''' <summary>
    ''' 使用的大语言模型名称（默认：llama3）
    ''' </summary>
    Public Property ModelName As String = "llama3"

    ''' <summary>
    ''' PubMed MySQL镜像数据库连接字符串
    ''' </summary>
    Public Property DatabaseConnectionString As String = "Server=localhost;Database=pubmed;Uid=root;Pwd=password;Charset=utf8mb4;"

    ''' <summary>
    ''' 输出文件目录路径
    ''' </summary>
    Public Property OutputDirectory As String = "./research_output"

    ''' <summary>
    ''' 最大迭代调研轮次（默认5轮）
    ''' </summary>
    Public Property MaxRounds As Integer = 5

    ''' <summary>
    ''' 每次数据库查询返回的最大文献数（默认20篇）
    ''' </summary>
    Public Property PapersPerQuery As Integer = 20

    ''' <summary>
    ''' 是否在控制台输出详细的调试日志（默认True）
    ''' </summary>
    Public Property VerboseLogging As Boolean = True

End Class

' ============================================================================
' 工厂类 - 简化Agent实例的创建
' ============================================================================

''' <summary>
''' 科研文献调研Agent工厂类，提供便捷的Agent创建方法
''' </summary>
Public Module ResearchAgentFactory

    ''' <summary>
    ''' 根据配置创建并初始化一个科研文献调研Agent实例
    ''' </summary>
    ''' <param name="config">Agent配置选项</param>
    ''' <returns>已初始化的ResearchAgent实例</returns>
    Public Function CreateAgent(config As ResearchAgentConfig) As ResearchAgent
        ' 创建Ollama客户端实例
        Dim ollama As New Ollama.Ollama(config.ModelName, config.OllamaEndpoint)

        ' 创建ResearchAgent实例
        Dim agent As New ResearchAgent(
            ollama,
            config.DatabaseConnectionString,
            config.OutputDirectory,
            config.MaxRounds,
            config.PapersPerQuery
        )

        ' 初始化Agent（注册函数工具）
        agent.Initialize()

        Return agent
    End Function

    ''' <summary>
    ''' 使用默认配置创建并初始化一个科研文献调研Agent实例
    ''' </summary>
    ''' <param name="dbConnectionString">PubMed数据库连接字符串</param>
    ''' <param name="outputDir">输出目录路径</param>
    ''' <returns>已初始化的ResearchAgent实例</returns>
    Public Function CreateAgent(dbConnectionString As String, outputDir As String) As ResearchAgent
        Dim config As New ResearchAgentConfig() With {
            .DatabaseConnectionString = dbConnectionString,
            .OutputDirectory = outputDir
        }
        Return CreateAgent(config)
    End Function

End Module

' ============================================================================
' 使用示例 - 主程序入口
' ============================================================================

''' <summary>
''' 科研文献调研Agent使用示例
''' </summary>
Public Class ResearchAgentExample

    ''' <summary>
    ''' 主程序入口 - 演示如何使用ResearchAgent进行文献调研
    ''' </summary>
    Public Shared Async Sub Main()

        ' ---- 步骤1：配置Agent参数 ----
        Dim config As New ResearchAgentConfig() With {
            .OllamaEndpoint = "http://localhost:11434",
            .ModelName = "llama3",
            .DatabaseConnectionString = "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;",
            .OutputDirectory = "./research_output",
            .MaxRounds = 5,
            .PapersPerQuery = 20,
            .VerboseLogging = True
        }

        ' ---- 步骤2：创建Agent实例 ----
        Console.WriteLine("正在创建科研文献调研Agent...")
        Dim agent = ResearchAgentFactory.CreateAgent(config)

        ' ---- 步骤3：执行文献调研 ----
        Try
            Dim topic = "CRISPR-Cas9基因编辑技术在遗传疾病治疗中的应用与挑战"
            Console.WriteLine($"开始文献调研，主题：{topic}")
            Console.WriteLine(New String("="c, 60))

            Dim result = Await agent.ConductResearchAsync(topic)

            ' ---- 步骤4：输出结果 ----
            Console.WriteLine(New String("="c, 60))
            Console.WriteLine("文献调研完成！")
            Console.WriteLine($"  - 收集文献总数：{result.AllPapers.Count} 篇")
            Console.WriteLine($"  - 调研迭代轮次：{result.Rounds.Count} 轮")
            Console.WriteLine($"  - Markdown文件：{result.OutputMarkdownPath}")
            Console.WriteLine($"  - PDF文件：{result.OutputPdfPath}")

            ' 输出每轮调研的简要信息
            For Each round In result.Rounds
                Console.WriteLine($"  - 第{round.RoundNumber}轮：关键词[{String.Join(", ", round.Keywords)}]，" &
                                  $"查询到{round.Papers.Count}篇新文献")
            Next

        Catch ex As Exception
            Console.WriteLine($"文献调研过程中发生错误：{ex.Message}")
            Console.WriteLine(ex.StackTrace)
        Finally
            agent.Dispose()
        End Try

        Console.WriteLine("按任意键退出...")
        Console.ReadKey()

    End Sub

    ''' <summary>
    ''' 高级使用示例 - 自定义函数工具注册
    ''' </summary>
    Public Shared Async Sub AdvancedExample()

        ' 创建Ollama客户端
        Dim ollama As New Ollama.Ollama("llama")

        ' 创建自定义的PubMed查询工具（可自定义SQL查询逻辑）
        Dim pubmedTool As New PubMedQueryTool(
            "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;"
        )

        ' 创建文档转换工具
        Dim converterTool As New DocumentConverterTool("./research_output")

        ' 手动注册函数工具（可选择只注册部分工具）
        ollama.AddFunction(pubmedTool, fun:=NameOf(PubMedQueryTool.search_papers))
        ollama.AddFunction(pubmedTool, fun:=NameOf(PubMedQueryTool.get_full_text))
        ollama.AddFunction(converterTool, fun:=NameOf(DocumentConverterTool.markdown_to_html))
        ollama.AddFunction(converterTool, fun:=NameOf(DocumentConverterTool.html_to_pdf))

        ' 创建Agent（不使用工厂方法，手动控制初始化过程）
        Dim agent As New ResearchAgent(
            ollama,
            "Server=localhost;Database=pubmed;Uid=root;Pwd=your_password;Charset=utf8mb4;",
            "./research_output",
            maxRounds:=3,
            papersPerQuery:=15
        )

        agent.Initialize()

        ' 执行调研
        Dim result = Await agent.ConductResearchAsync("深度学习在蛋白质结构预测中的应用")

        ' 处理结果
        If Not String.IsNullOrEmpty(result.OutputPdfPath) Then
            Console.WriteLine($"PDF报告已生成：{result.OutputPdfPath}")
        End If

        agent.Dispose()

    End Sub

End Class
