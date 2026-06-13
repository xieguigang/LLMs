
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

