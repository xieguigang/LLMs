
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.ComponentModel.DataSourceModel
Imports Microsoft.VisualBasic.Linq
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.Scripting.MetaData
Imports Ollama
Imports Ollama.JSON.FunctionCall
Imports SMRUCC.Rsharp
Imports SMRUCC.Rsharp.Interpreter.ExecuteEngine.ExpressionSymbols.Operators
Imports SMRUCC.Rsharp.Runtime
Imports SMRUCC.Rsharp.Runtime.Components
Imports SMRUCC.Rsharp.Runtime.Components.[Interface]
Imports SMRUCC.Rsharp.Runtime.Internal.Object
Imports SMRUCC.Rsharp.Runtime.Interop
Imports SMRUCC.Rsharp.Runtime.Vectorization
Imports RInternal = SMRUCC.Rsharp.Runtime.Internal

<Package("ollama")>
Module OLlamaDemo

    ''' <summary>
    ''' Create a new ollama client for LLMs chat
    ''' </summary>
    ''' <param name="model"></param>
    ''' <param name="ollama_server"></param>
    ''' <param name="max_memory_size"></param>
    ''' <param name="logfile"></param>
    ''' <returns></returns>
    <ExportAPI("new")>
    Public Function create(model As String,
                           Optional ollama_server As String = "127.0.0.1:11434",
                           Optional max_memory_size As Integer = 1000,
                           Optional logfile As String = Nothing) As Ollama.Ollama

        Return New Ollama.Ollama(model, ollama_server, logfile:=logfile) With {
            .max_memory_size = max_memory_size
        }
    End Function

    <ExportAPI("get_modelinfo")>
    Public Function get_modelinfo(ollama As Ollama.Ollama, Optional timeout As Double = 1, Optional env As Environment = Nothing) As Object
        Dim json As JsonObject = ollama.GetModelInformation(timeout).GetAwaiter.GetResult
        Dim modelinfo = json.createRObj(env)
        Return modelinfo
    End Function

    ''' <summary>
    ''' set or get the system message for the ollama client
    ''' </summary>
    ''' <param name="model"></param>
    ''' <param name="msg"></param>
    ''' <returns></returns>
    <ExportAPI("system_message")>
    Public Function system_message(model As Ollama.Ollama,
                                   <RByRefValueAssign>
                                   <RRawVectorArgument>
                                   msg As Object,
                                   Optional env As Environment = Nothing) As Object

        If Not msg Is Nothing Then
            model.system_message = CLRVector.asCharacter(msg).JoinBy(vbCrLf)
        End If

        Return model.system_message
    End Function

    ''' <summary>
    ''' chat with the LLMs model throught the ollama client
    ''' </summary>
    ''' <param name="model"></param>
    ''' <param name="msg"></param>
    ''' <returns>
    ''' a tuple list that contains the LLMs result output:
    ''' 
    ''' 1. output - the LLMs thinking and LLMs <see cref="DeepSeekResponse"/> message
    ''' 2. function_calls - the <see cref="FunctionCall"/> during the LLMs thinking
    ''' 
    ''' </returns>
    <ExportAPI("chat")>
    <RApiReturn("output", "function_calls")>
    Public Function chat(model As Ollama.Ollama, msg As String) As Object
        Return New list(
            slot("output") = model.Chat(msg).GetAwaiter.GetResult,
            slot("function_calls") = model.GetLastFunctionCalls
        )
    End Function

    <ExportAPI("add_tool")>
    Public Function add_tool(model As Ollama.Ollama, name$, desc$,
                             <RRawVectorArgument>
                             requires As Object,
                             Optional args As list = Nothing,
                             <RByRefValueAssign>
                             Optional fcall As RFunction = Nothing,
                             Optional env As Environment = Nothing) As Object

        Dim argList As New Dictionary(Of String, ParameterProperties)

        If fcall Is Nothing Then
            Return RInternal.debug.stop("Missing function value for make LLMs tool call!", env)
        End If
        If Not args Is Nothing Then
            For Each arg As String In args.getNames
                Call argList.Add(arg, New ParameterProperties With {
                    .name = arg,
                    .description = CLRVector.asCharacter(args.getByName(arg)).JoinBy(vbCrLf)
                })
            Next
        End If

        Dim f As New FunctionModel With {
            .name = name,
            .description = desc,
            .parameters = New FunctionParameters With {
                .required = CLRVector _
                    .asCharacter(requires) _
                    .SafeQuery _
                    .ToArray,
                .properties = argList
            }
        }

        Call model.AddFunction(
            f, Function(arg)
                   Dim argSet As InvokeParameter() = arg.arguments _
                       .SafeQuery _
                       .Select(Function(a, i) New InvokeParameter(a.Key, a.Value, index:=i + 1)) _
                       .ToArray
                   Dim eval As Object = fcall.Invoke(env, argSet)

                   If TypeOf eval Is ReturnValue Then
                       eval = DirectCast(eval, ReturnValue).Evaluate(env)
                   End If
                   If TypeOf eval Is list Then
                       ' to json
                       eval = env.globalEnvironment.doCall(
                           "JSON::json_encode",
                           New NamedValue(Of Object)("x", eval),
                           New NamedValue(Of Object)("env", env)
                       )
                   End If

                   If eval Is Nothing Then
                       Return "nothing returns"
                   ElseIf TypeOf eval Is Message Then
                       Return $"Error: {DirectCast(eval, Message).message.JoinBy("")}"
                   Else
                       Return CLRVector.asCharacter(eval).JoinBy("")
                   End If
               End Function)

        Return model
    End Function

    ' start server
    ' docker run --privileged --net=host --env OLLAMA_HOST=0.0.0.0  -itd  -p "11434:11434" ubuntu:deepseek_20250301 ollama serve
    ' start model server
    ' docker run -it --net=host -d  ubuntu:deepseek_20250301 ollama run deepseek-r1:1.5b

    <ExportAPI("deepseek_chat")>
    Public Function deepseek_chat(message As String,
                                  Optional ollama_serve As String = "127.0.0.1:11434",
                                  Optional model As String = "deepseek-r1:671b") As DeepSeekResponse

        Return DeepSeekResponse.Chat(message, ollama_serve, model)
    End Function

End Module
