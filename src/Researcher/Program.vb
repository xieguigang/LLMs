

''' <summary>
''' 科研文献调研Agent使用示例
''' </summary>
Public Class Program

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
