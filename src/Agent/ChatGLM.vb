
Imports System.IO
Imports Agent.ChatGLM
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.Language
Imports Microsoft.VisualBasic.Scripting.MetaData
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports SMRUCC.Rsharp
Imports SMRUCC.Rsharp.Runtime
Imports SMRUCC.Rsharp.Runtime.Components
Imports SMRUCC.Rsharp.Runtime.Internal.[Object]
Imports SMRUCC.Rsharp.Runtime.Interop
Imports SMRUCC.Rsharp.Runtime.Vectorization

<Package("chatglm")>
<RTypeExport("chat_history", GetType(ChatHistory))>
Public Module ChatGLMHelper

    ''' <summary>
    ''' add a new record of chat input and ai response for create the history data
    ''' </summary>
    ''' <param name="his"></param>
    ''' <param name="input"></param>
    ''' <param name="response"></param>
    ''' <returns></returns>
    <ExportAPI("input_and_response")>
    Public Function input_and_response(his As ChatHistory, input As String, response As String) As ChatHistory
        If his Is Nothing Then
            his = New ChatHistory
        End If

        Return his.AddUserInput(input).AddAIResponse(response)
    End Function

    ''' <summary>
    ''' build json string for chatglm history input
    ''' </summary>
    ''' <param name="his"></param>
    ''' <returns></returns>
    <ExportAPI("history_json")>
    Public Function history_json(his As ChatHistory) As String
        If his Is Nothing Then
            Return Nothing
        End If

        Return his.ToJson
    End Function

    ''' <summary>
    ''' create json data for run chatglm batch task
    ''' </summary>
    ''' <param name="content">
    ''' a dataframe object that contains the batch task data for run:
    ''' 
    ''' 1. id: the unique reference id of each task
    ''' 2. term: the term for do translation
    ''' </param>
    ''' <returns>
    ''' a collection of the batch task data for run the request
    ''' </returns>
    ''' <remarks>
    ''' the required file extension suffix name by upload 
    ''' batch data from web must be ``jsonl``!
    ''' </remarks>
    <ExportAPI("batch_transaltion")>
    <RApiReturn(GetType(GLMBatchTask))>
    Public Function batch_translation(content As dataframe,
                                      Optional prompt_text As String = "你是一个从英文到中文的语言翻译器",
                                      Optional add_explains As Boolean = True,
                                      Optional env As Environment = Nothing) As Object

        Dim id As String() = CLRVector.asCharacter(content.getBySynonym("id", "unique_id", "task_id"))
        Dim term As String() = CLRVector.asCharacter(content.getBySynonym("term", "data"))

        If id.IsNullOrEmpty Then
            Return Internal.debug.stop("the required of the unique task reference id is missing!", env)
        End If
        If term.IsNullOrEmpty Then
            Return Internal.debug.stop("the required of the term data for do translation is missing!", env)
        End If

        Dim batch As GLMBatchTask() = New GLMBatchTask(id.Length - 1) {}
        Dim text As String
        Dim prompt_data As String

        For i As Integer = 0 To id.Length - 1
            text = term(i)
            text = Strings.Trim(text).Replace(""""c, "'")
            text = text.Replace("%", "").Replace("\", "-")

            If add_explains Then
                prompt_data = $"# 任务：对以下用户文本进行英文到中文的翻译，需要输出翻译后的结果文本以及对应的名词解释，
# 用户文本： data=""{text}""
# 输出格式： {{""zh-CN"": "" "", ""explains"": "" ""}}"
            Else
                prompt_data = $"# 任务：对以下用户文本进行英文到中文的翻译，只输出翻译后的结果文本，
# 用户文本： data=""{text}""
# 输出格式： {{""zh-CN"": "" ""}}"
            End If

            batch(i) = New GLMBatchTask With {
                .custom_id = id(i),
                .method = "POST",
                .url = "/v4/chat/completions",
                .body = New RequestBody With {
                    .options = New RequestOptions With {.temperature = 0.1},
                    .model = "glm-4",
                    .messages = {
                        New History(roles.system, prompt_text),
                        New History(roles.user, prompt_data)
                    }
                }
            }
        Next

        Return batch
    End Function

    ''' <summary>
    ''' parse the result of the chatglm batch request
    ''' </summary>
    ''' <param name="file"></param>
    ''' <param name="env"></param>
    ''' <returns></returns>
    <ExportAPI("parse_batch_output")>
    Public Function ParseBatchResult(<RRawVectorArgument>
                                     file As Object,
                                     Optional parse_annotation As Boolean = True,
                                     Optional env As Environment = Nothing) As Object

        Dim ispath As Boolean = False
        Dim s = SMRUCC.Rsharp.GetFileStream(file, IO.FileAccess.Read, env, is_filepath:=ispath)
        Dim request_id As New List(Of String)
        Dim result As New List(Of String)
        Dim annotations As New List(Of String)
        Dim rownames As New List(Of String)

        If s Like GetType(Message) Then
            Return s.TryCast(Of Message)
        End If

        Dim line As Value(Of String) = ""
        Dim glm_result As Result
        Dim glm_text As String()

        Using rd As New StreamReader(s.TryCast(Of Stream))
            Do While Not (line = rd.ReadLine) Is Nothing
                glm_result = CStr(line).LoadJSON(Of Result)
                request_id.Add(glm_result.custom_id)
                rownames.Add(glm_result.id)

                If parse_annotation Then
                    glm_text = glm_result.GetResponseText.FirstOrDefault.LineTokens
                    result.Add(glm_text.FirstOrDefault)
                    annotations.Add(glm_text.Skip(1).JoinBy("\n"))
                Else
                    result.Add(glm_result.GetResponseText.FirstOrDefault)
                End If
            Loop
        End Using

        If ispath Then
            Try
                Call s.TryCast(Of Stream).Close()
            Catch ex As Exception

            End Try
        End If

        If Not parse_annotation Then
            Call annotations.Add("-")
        End If

        Return dataframe.Create(
            rownames,
            slot("request_id") = request_id.ToArray,
            slot("response") = result.ToArray,
            slot("annotations") = annotations.ToArray
        )
    End Function

End Module
