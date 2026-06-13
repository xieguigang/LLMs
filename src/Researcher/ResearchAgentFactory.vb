
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