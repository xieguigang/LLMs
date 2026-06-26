' ============================================================================
' LLMClient.vb
' 大语言模型客户端模块 (VB.NET)
'
' 功能特性:
'   1. 基于 HTTP 协议访问大语言模型 API
'   2. 支持 ChatGLM (智谱AI) 在线服务 (API + APIKey 认证)
'   3. 支持 Ollama 本地部署服务 (无需认证)
'   4. 内置上下文记忆管理 (ConversationManager)
'   5. 支持 Function Call, 通过反射自动注册 VB.NET 对象实例中的 Public Function
'
' 依赖: Newtonsoft.Json (NuGet 包)
'   Install-Package Newtonsoft.Json
'
' 兼容: .NET Framework 4.6.2+ / .NET Core 2.0+ / .NET 5/6/7/8
' ============================================================================

Imports System
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Reflection
Imports System.Text
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Namespace LLMSdk

    ' ============================================================
    ' 区域 1: 数据模型
    ' ============================================================

    ''' <summary>
    ''' 聊天消息
    ''' </summary>
    Public Class ChatMessage
        Public Property role As String
        Public Property content As String
        Public Property tool_calls As List(Of ToolCall)
        Public Property tool_call_id As String

        Public Sub New(role As String, content As String)
            Me.role = role
            Me.content = content
        End Sub
    End Class

    ''' <summary>
    ''' 工具调用请求 (LLM 返回)
    ''' </summary>
    Public Class ToolCall
        Public Property id As String
        Public Property type As String = "function"
        Public Property function As FunctionCallInfo
    End Class

    Public Class FunctionCallInfo
        Public Property name As String
        Public Property arguments As String
    End Class

    ''' <summary>
    ''' 工具定义 (发送给 LLM)
    ''' </summary>
    Public Class FunctionDefinition
        Public Property type As String = "function"
        Public Property function As FunctionSchema
    End Class

    Public Class FunctionSchema
        Public Property name As String
        Public Property description As String
        Public Property parameters As JObject
    End Class

    ''' <summary>
    ''' LLM 响应
    ''' </summary>
    Public Class LLMResponse
        Public Property Content As String = ""
        Public Property ToolCalls As List(Of ToolCall)
        Public Property RawResponse As String
    End Class

    ' ============================================================
    ' 区域 2: 特性 (Attributes) - 用于标注工具函数
    ' ============================================================

    ''' <summary>
    ''' 标注工具函数的描述信息 (LLM 据此判断何时调用)
    ''' </summary>
    <AttributeUsage(AttributeTargets.Method)>
    Public Class ToolDescriptionAttribute
        Inherits Attribute
        Public Property Description As String
        Public Sub New(desc As String)
            Description = desc
        End Sub
    End Class

    ''' <summary>
    ''' 标注工具函数参数的描述信息
    ''' </summary>
    <AttributeUsage(AttributeTargets.Parameter)>
    Public Class ParameterDescriptionAttribute
        Inherits Attribute
        Public Property Description As String
        Public Sub New(desc As String)
            Description = desc
        End Sub
    End Class

    ' ============================================================
    ' 区域 3: 工具注册表 - 通过反射注册和调用工具函数
    ' ============================================================

    ''' <summary>
    ''' 工具注册表: 通过反射加载 VB.NET 对象实例中的 Public Function,
    ''' 将其注册为可被 LLM 调用的工具函数
    ''' </summary>
    Public Class ToolRegistry
        Private _tools As New Dictionary(Of String, ToolEntry)()

        Private Class ToolEntry
            Public Property Definition As FunctionDefinition
            Public Property Target As Object
            Public Property Method As MethodInfo
        End Class

        ''' <summary>
        ''' 从对象实例中注册所有公共函数为工具
        ''' 规则:
        '''   - 仅注册在该类型中直接声明的 Public Function (不含继承的方法)
        '''   - 跳过属性访问器 (Property Get/Set)
        '''   - 跳过 Sub (无返回值的方法)
        '''   - 跳过含 ByRef/Out 参数的方法
        ''' </summary>
        Public Sub RegisterFromInstance(instance As Object)
            If instance Is Nothing Then Return

            Dim type As Type = instance.GetType()
            Dim methods() As MethodInfo = type.GetMethods(
                BindingFlags.Public Or BindingFlags.Instance Or BindingFlags.DeclaredOnly)

            For Each method In methods
                ' 跳过属性访问器 (get_/set_) 和运算符等特殊方法
                If method.IsSpecialName Then Continue For
                ' 跳过 Sub (Function 才有返回值)
                If method.ReturnType Is GetType(Void) Then Continue For
                ' 跳过含 ByRef 参数的方法
                Dim hasRefParam As Boolean = False
                For Each p In method.GetParameters()
                    If p.ParameterType.IsByRef Then
                        hasRefParam = True
                        Exit For
                    End If
                Next
                If hasRefParam Then Continue For

                RegisterMethod(instance, method)
            Next
        End Sub

        Private Sub RegisterMethod(instance As Object, method As MethodInfo)
            Dim entry As New ToolEntry()
            entry.Target = instance
            entry.Method = method

            Dim def As New FunctionDefinition()
            def.function = New FunctionSchema()
            def.function.name = method.Name
            def.function.description = GetMethodDescription(method)
            def.function.parameters = BuildParametersSchema(method)

            entry.Definition = def
            _tools(method.Name) = entry
        End Sub

        Private Function GetMethodDescription(method As MethodInfo) As String
            Dim descAttr = method.GetCustomAttribute(Of ToolDescriptionAttribute)()
            If descAttr IsNot Nothing Then Return descAttr.Description
            Return $"执行 {method.Name} 函数"
        End Function

        Private Function BuildParametersSchema(method As MethodInfo) As JObject
            Dim schema As New JObject()
            schema("type") = "object"
            Dim properties As New JObject()
            Dim required As New JArray()

            For Each param In method.GetParameters()
                Dim propSchema As New JObject()
                propSchema("type") = MapTypeToSchema(param.ParameterType)

                Dim descAttr = param.GetCustomAttribute(Of ParameterDescriptionAttribute)()
                If descAttr IsNot Nothing Then
                    propSchema("description") = descAttr.Description
                Else
                    propSchema("description") = $"参数 {param.Name}"
                End If

                properties(param.Name) = propSchema

                ' 可选参数不加入 required
                If Not param.IsOptional Then
                    required.Add(param.Name)
                End If
            Next

            schema("properties") = properties
            If required.Count > 0 Then
                schema("required") = required
            End If
            Return schema
        End Function

        Private Function MapTypeToSchema(t As Type) As String
            ' 处理 Nullable(Of T)
            Dim nt As Type = Nullable.GetUnderlyingType(t)
            If nt IsNot Nothing Then t = nt

            If t Is GetType(String) OrElse t Is GetType(Char) Then Return "string"
            If t Is GetType(Integer) OrElse t Is GetType(Long) OrElse
               t Is GetType(Short) OrElse t Is GetType(Byte) OrElse
               t Is GetType(UInteger) OrElse t Is GetType(ULong) OrElse
               t Is GetType(UShort) OrElse t Is GetType(SByte) Then Return "integer"
            If t Is GetType(Double) OrElse t Is GetType(Single) OrElse
               t Is GetType(Decimal) Then Return "number"
            If t Is GetType(Boolean) Then Return "boolean"
            If t.IsEnum Then Return "string"
            If t.IsArray OrElse GetType(System.Collections.IList).IsAssignableFrom(t) Then Return "array"
            Return "string"
        End Function

        ''' <summary>
        ''' 获取所有已注册工具的定义 (用于发送给 LLM)
        ''' </summary>
        Public Function GetDefinitions() As List(Of FunctionDefinition)
            Dim result As New List(Of FunctionDefinition)()
            For Each entry In _tools.Values
                result.Add(entry.Definition)
            Next
            Return result
        End Function

        ''' <summary>
        ''' 调用指定工具
        ''' </summary>
        ''' <param name="name">工具名称</param>
        ''' <param name="argumentsJson">LLM 返回的 JSON 格式参数</param>
        ''' <returns>工具执行结果 (JSON 字符串)</returns>
        Public Function Invoke(name As String, argumentsJson As String) As String
            If Not _tools.ContainsKey(name) Then
                Return $"{{""error"":""工具 '{name}' 未注册""}}"
            End If

            Dim entry As ToolEntry = _tools(name)

            Try
                Dim arguments As JObject = Nothing
                If Not String.IsNullOrEmpty(argumentsJson) Then
                    arguments = JObject.Parse(argumentsJson)
                End If

                Dim parameters() As ParameterInfo = entry.Method.GetParameters()
                Dim args(parameters.Length - 1) As Object

                For i = 0 To parameters.Length - 1
                    Dim param = parameters(i)
                    If arguments IsNot Nothing AndAlso arguments(param.Name) IsNot Nothing Then
                        args(i) = arguments(param.Name).ToObject(param.ParameterType)
                    ElseIf param.IsOptional Then
                        args(i) = param.DefaultValue
                    Else
                        args(i) = If(Activator.CreateInstance(param.ParameterType), "")
                    End If
                Next

                Dim result = entry.Method.Invoke(entry.Target, args)

                If result Is Nothing Then Return ""
                ' 将返回值序列化为 JSON, 便于 LLM 理解
                Return JsonConvert.SerializeObject(result)
            Catch ex As TargetInvocationException
                Return $"{{""error"":""工具执行错误: {JsonEscape(ex.InnerException?.Message)}""}}"
            Catch ex As Exception
                Return $"{{""error"":""工具执行错误: {JsonEscape(ex.Message)}""}}"
            End Try
        End Function

        Private Function JsonEscape(s As String) As String
            If s Is Nothing Then Return ""
            Return s.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, " ").Replace(vbLf, " ")
        End Function

        Public Function HasTool(name As String) As Boolean
            Return _tools.ContainsKey(name)
        End Function

        Public Function Count() As Integer
            Return _tools.Count
        End Function
    End Class

    ' ============================================================
    ' 区域 4: 对话管理器 - 上下文记忆
    ' ============================================================

    ''' <summary>
    ''' 对话管理器: 维护消息历史, 实现简单的上下文记忆
    ''' 当消息数超过上限时, 自动裁剪最早的非系统消息
    ''' </summary>
    Public Class ConversationManager
        Private _messages As New List(Of ChatMessage)()
        Private _maxMessages As Integer = 20
        Private _systemPrompt As String = ""

        Public Sub New(Optional systemPrompt As String = Nothing)
            If systemPrompt IsNot Nothing Then
                _systemPrompt = systemPrompt
            End If
            Reset()
        End Sub

        ''' <summary>系统提示词 (设置助手角色和行为)</summary>
        Public Property SystemPrompt As String
            Get
                Return _systemPrompt
            End Get
            Set(value As String)
                _systemPrompt = value
                ' 同步更新已存在的系统消息
                If _messages.Count > 0 AndAlso _messages(0).role = "system" Then
                    If String.IsNullOrEmpty(value) Then
                        _messages.RemoveAt(0)
                    Else
                        _messages(0).content = value
                    End If
                ElseIf Not String.IsNullOrEmpty(value) Then
                    _messages.Insert(0, New ChatMessage("system", value))
                End If
            End Set
        End Property

        ''' <summary>最大保留消息数 (含系统消息), 默认 20</summary>
        Public Property MaxMessages As Integer
            Get
                Return _maxMessages
            End Get
            Set(value As Integer)
                _maxMessages = Math.Max(2, value)
                TrimHistory()
            End Set
        End Property

        ''' <summary>清空对话历史 (保留系统提示词)</summary>
        Public Sub Reset()
            _messages.Clear()
            If Not String.IsNullOrEmpty(_systemPrompt) Then
                _messages.Add(New ChatMessage("system", _systemPrompt))
            End If
        End Sub

        Public Sub AddUserMessage(content As String)
            _messages.Add(New ChatMessage("user", content))
            TrimHistory()
        End Sub

        Public Sub AddAssistantMessage(content As String)
            _messages.Add(New ChatMessage("assistant", content))
            TrimHistory()
        End Sub

        Public Sub AddAssistantMessageWithToolCalls(content As String, toolCalls As List(Of ToolCall))
            Dim msg As New ChatMessage("assistant", content)
            msg.tool_calls = toolCalls
            _messages.Add(msg)
            TrimHistory()
        End Sub

        Public Sub AddToolResult(toolCallId As String, content As String)
            Dim msg As New ChatMessage("tool", content)
            msg.tool_call_id = toolCallId
            _messages.Add(msg)
            TrimHistory()
        End Sub

        Public Function GetMessages() As List(Of ChatMessage)
            Return New List(Of ChatMessage)(_messages)
        End Function

        Private Sub TrimHistory()
            ' 保留系统消息 + 最近 N 条消息
            While _messages.Count > _maxMessages
                Dim removeIndex As Integer = 0
                If _messages.Count > 0 AndAlso _messages(0).role = "system" Then
                    removeIndex = 1
                End If
                If _messages.Count > removeIndex Then
                    _messages.RemoveAt(removeIndex)
                Else
                    Exit While
                End If
            End While
        End Sub
    End Class

    ' ============================================================
    ' 区域 5: LLM 客户端基类
    ' ============================================================

    ''' <summary>
    ''' LLM 客户端抽象基类
    ''' 实现通用的工具调用循环逻辑, 子类只需实现 4 个抽象方法
    ''' </summary>
    Public MustInherit Class LLMClient
        Protected _httpClient As HttpClient
        Protected _toolRegistry As New ToolRegistry()
        Protected _maxToolIterations As Integer = 10

        Public Sub New()
            _httpClient = New HttpClient()
            _httpClient.Timeout = TimeSpan.FromMinutes(5)
        End Sub

        ''' <summary>最大工具调用迭代次数 (防止无限循环)</summary>
        Public Property MaxToolIterations As Integer
            Get
                Return _maxToolIterations
            End Get
            Set(value As Integer)
                _maxToolIterations = value
            End Set
        End Property

        ''' <summary>
        ''' 注册对象实例中的所有 Public Function 为工具
        ''' </summary>
        Public Sub RegisterTools(instance As Object)
            _toolRegistry.RegisterFromInstance(instance)
        End Sub

        ''' <summary>
        ''' 发送聊天消息并获取回复 (自动处理工具调用循环)
        ''' </summary>
        Public Async Function ChatAsync(conversation As ConversationManager, userMessage As String) As Task(Of String)
            conversation.AddUserMessage(userMessage)
            Return Await ProcessConversationAsync(conversation)
        End Function

        ''' <summary>
        ''' 处理对话, 自动执行工具调用循环:
        ''' 1. 发送请求给 LLM
        ''' 2. 若 LLM 返回 tool_calls, 执行工具并将结果回传
        ''' 3. 重复直到 LLM 返回最终文本或达到最大迭代次数
        ''' </summary>
        Private Async Function ProcessConversationAsync(conversation As ConversationManager) As Task(Of String)
            Dim iterations As Integer = 0

            While iterations < _maxToolIterations
                Dim response = Await SendRequestAsync(conversation.GetMessages())

                ' 检查是否有工具调用
                If response.ToolCalls IsNot Nothing AndAlso response.ToolCalls.Count > 0 Then
                    ' 记录 assistant 的工具调用请求
                    conversation.AddAssistantMessageWithToolCalls(response.Content, response.ToolCalls)

                    ' 依次执行每个工具调用
                    For Each tc In response.ToolCalls
                        Dim result As String = _toolRegistry.Invoke(tc.function.name, tc.function.arguments)
                        conversation.AddToolResult(tc.id, result)
                    Next

                    iterations += 1
                    Continue While
                End If

                ' 没有工具调用, 返回最终结果
                conversation.AddAssistantMessage(response.Content)
                Return response.Content
            End While

            Return "已达到最大工具调用次数限制, 请重试或调整问题"
        End Function

        Protected Async Function SendRequestAsync(messages As List(Of ChatMessage)) As Task(Of LLMResponse)
            Dim body As String = BuildRequestBody(messages)
            Dim request As New HttpRequestMessage(HttpMethod.Post, GetEndpoint())
            request.Content = New StringContent(body, Encoding.UTF8, "application/json")
            ApplyAuth(request)

            Dim response As HttpResponseMessage = Await _httpClient.SendAsync(request)
            Dim responseContent As String = Await response.Content.ReadAsStringAsync()

            If Not response.IsSuccessStatusCode Then
                Throw New Exception($"API 错误 ({CInt(response.StatusCode)} {response.StatusCode}): {responseContent}")
            End If

            Dim result = ParseResponse(responseContent)
            result.RawResponse = responseContent
            Return result
        End Function

        ' ===== 子类必须实现的 4 个抽象方法 =====

        ''' <summary>返回 API 端点 URL</summary>
        Protected MustOverride Function GetEndpoint() As String

        ''' <summary>设置认证头 (如 Bearer Token)</summary>
        Protected MustOverride Sub ApplyAuth(request As HttpRequestMessage)

        ''' <summary>构建请求体 JSON</summary>
        Protected MustOverride Function BuildRequestBody(messages As List(Of ChatMessage)) As String

        ''' <summary>解析响应 JSON</summary>
        Protected MustOverride Function ParseResponse(content As String) As LLMResponse

        ' ===== 通用序列化辅助方法 (供子类使用) =====

        Protected Function SerializeMessages(messages As List(Of ChatMessage)) As JArray
            Dim arr As New JArray()
            For Each m In messages
                Dim obj As New JObject()
                obj("role") = m.role
                obj("content") = If(m.content, "")

                If m.tool_calls IsNot Nothing AndAlso m.tool_calls.Count > 0 Then
                    Dim tcs As New JArray()
                    For Each tc In m.tool_calls
                        Dim tcObj As New JObject()
                        tcObj("id") = tc.id
                        tcObj("type") = tc.type
                        Dim funcObj As New JObject()
                        funcObj("name") = tc.function.name
                        funcObj("arguments") = tc.function.arguments
                        tcObj("function") = funcObj
                        tcs.Add(tcObj)
                    Next
                    obj("tool_calls") = tcs
                End If

                If Not String.IsNullOrEmpty(m.tool_call_id) Then
                    obj("tool_call_id") = m.tool_call_id
                End If

                arr.Add(obj)
            Next
            Return arr
        End Function

        Protected Function SerializeTools(tools As List(Of FunctionDefinition)) As JArray
            Dim arr As New JArray()
            For Each t In tools
                Dim obj As New JObject()
                obj("type") = t.type
                Dim funcObj As New JObject()
                funcObj("name") = t.function.name
                funcObj("description") = t.function.description
                funcObj("parameters") = t.function.parameters
                obj("function") = funcObj
                arr.Add(obj)
            Next
            Return arr
        End Function
    End Class

    ' ============================================================
    ' 区域 6: ChatGLM 客户端 (智谱 AI 在线服务)
    ' ============================================================

    ''' <summary>
    ''' ChatGLM (智谱 AI) 客户端
    ''' API 文档: https://open.bigmodel.cn/dev/api
    ''' 使用 Bearer Token 认证
    ''' </summary>
    Public Class ChatGLMClient
        Inherits LLMClient

        Private _apiKey As String
        Private _model As String
        Private _baseUrl As String

        ''' <param name="apiKey">智谱 AI 平台 API Key</param>
        ''' <param name="model">模型名称, 默认 glm-4</param>
        ''' <param name="baseUrl">API 基础地址</param>
        Public Sub New(apiKey As String,
                       Optional model As String = "glm-4",
                       Optional baseUrl As String = "https://open.bigmodel.cn/api/paas/v4")
            MyBase.New()
            _apiKey = apiKey
            _model = model
            _baseUrl = baseUrl.TrimEnd("/"c)
        End Sub

        Protected Overrides Function GetEndpoint() As String
            Return $"{_baseUrl}/chat/completions"
        End Function

        Protected Overrides Sub ApplyAuth(request As HttpRequestMessage)
            request.Headers.Add("Authorization", $"Bearer {_apiKey}")
        End Sub

        Protected Overrides Function BuildRequestBody(messages As List(Of ChatMessage)) As String
            Dim body As New JObject()
            body("model") = _model
            body("messages") = SerializeMessages(messages)

            Dim tools = _toolRegistry.GetDefinitions()
            If tools.Count > 0 Then
                body("tools") = SerializeTools(tools)
            End If

            Return body.ToString(Formatting.None)
        End Function

        Protected Overrides Function ParseResponse(content As String) As LLMResponse
            Dim json As JObject = JObject.Parse(content)
            Dim choice = json("choices")(0)("message")

            Dim response As New LLMResponse()
            response.Content = If(choice("content")?.ToString(), "")

            If choice("tool_calls") IsNot Nothing Then
                response.ToolCalls = New List(Of ToolCall)()
                For Each tc In choice("tool_calls")
                    Dim toolCall As New ToolCall()
                    toolCall.id = tc("id")?.ToString()
                    toolCall.type = If(tc("type")?.ToString(), "function")
                    toolCall.function = New FunctionCallInfo()
                    toolCall.function.name = tc("function")("name").ToString()
                    toolCall.function.arguments = If(tc("function")("arguments")?.ToString(), "{}")
                    response.ToolCalls.Add(toolCall)
                Next
            End If

            Return response
        End Function
    End Class

    ' ============================================================
    ' 区域 7: Ollama 客户端 (本地部署服务)
    ' ============================================================

    ''' <summary>
    ''' Ollama 本地部署客户端
    ''' API 文档: https://github.com/ollama/ollama/blob/main/docs/api.md
    ''' 本地部署无需认证
    ''' </summary>
    Public Class OllamaClient
        Inherits LLMClient

        Private _baseUrl As String
        Private _model As String

        ''' <param name="baseUrl">Ollama 服务地址, 默认 http://localhost:11434</param>
        ''' <param name="model">模型名称, 如 llama3, qwen2.5, glm4 等</param>
        Public Sub New(Optional baseUrl As String = "http://localhost:11434",
                       Optional model As String = "llama3")
            MyBase.New()
            _baseUrl = baseUrl.TrimEnd("/"c)
            _model = model
        End Sub

        Protected Overrides Function GetEndpoint() As String
            Return $"{_baseUrl}/api/chat"
        End Function

        Protected Overrides Sub ApplyAuth(request As HttpRequestMessage)
            ' Ollama 本地部署默认无需认证
        End Sub

        Protected Overrides Function BuildRequestBody(messages As List(Of ChatMessage)) As String
            Dim body As New JObject()
            body("model") = _model
            body("stream") = False
            body("messages") = SerializeMessages(messages)

            Dim tools = _toolRegistry.GetDefinitions()
            If tools.Count > 0 Then
                body("tools") = SerializeTools(tools)
            End If

            Return body.ToString(Formatting.None)
        End Function

        Protected Overrides Function ParseResponse(content As String) As LLMResponse
            Dim json As JObject = JObject.Parse(content)
            Dim msg = json("message")

            Dim response As New LLMResponse()
            response.Content = If(msg("content")?.ToString(), "")

            If msg("tool_calls") IsNot Nothing Then
                response.ToolCalls = New List(Of ToolCall)()
                For Each tc In msg("tool_calls")
                    Dim toolCall As New ToolCall()
                    toolCall.id = If(tc("id")?.ToString(), Guid.NewGuid().ToString("N"))
                    toolCall.type = If(tc("type")?.ToString(), "function")
                    toolCall.function = New FunctionCallInfo()
                    toolCall.function.name = tc("function")("name").ToString()
                    toolCall.function.arguments = If(tc("function")("arguments")?.ToString(), "{}")
                    response.ToolCalls.Add(toolCall)
                Next
            End If

            Return response
        End Function
    End Class

End Namespace
