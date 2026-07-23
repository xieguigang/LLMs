Imports System.ComponentModel
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.Scripting.Runtime
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON.FunctionCall
Imports any = Microsoft.VisualBasic.Scripting

Module CLRFunction

    Public Iterator Function GetTarget(obj As Type, name As String) As IEnumerable(Of MethodInfo)
        Dim list = CType(obj, TypeInfo).DeclaredMethods _
            .Where(Function(a) a.Name = name) _
            .ToArray

        For Each method As MethodInfo In list
            If method.GetCustomAttribute(Of DescriptionAttribute) IsNot Nothing Then
                Yield method
            End If
        Next
    End Function

    <Extension>
    Private Iterator Function GetArguments(handle As MethodInfo) As IEnumerable(Of ToolArgument)
        Dim args As ArgumentAttribute() = handle.GetCustomAttributes(Of ArgumentAttribute).ToArray
        Dim pars = handle.GetParameters

        If args.IsNullOrEmpty AndAlso Not pars.IsNullOrEmpty Then
            For Each p As ParameterInfo In pars
                Dim a = p.GetCustomAttribute(Of ArgumentAttribute)

                If a Is Nothing Then
                    a = New ArgumentAttribute(p.Name, p.IsOptional)
                Else
                    a.SetOptional(p.IsOptional)
                End If

                Yield New ToolArgument With {
                    .[default] = any.ToString(p.DefaultValue, null:="null"),
                    .desc = a.Description,
                    .name = a.Name,
                    .type = p.ParameterType.PrimitiveTypeCode.Description.ToLower,
                    .[optional] = p.IsOptional
                }
            Next
        Else
            Dim pIndex = pars.ToDictionary(Function(a) a.Name)

            For Each a As ArgumentAttribute In args
                Dim p As ParameterInfo = pIndex(a.Name)
                Call a.SetOptional(p.IsOptional)
                Yield New ToolArgument With {
                    .[default] = any.ToString(p.DefaultValue, null:="null"),
                    .desc = a.Description,
                    .name = a.Name,
                    .type = p.ParameterType.PrimitiveTypeCode.Description.ToLower,
                    .[optional] = p.IsOptional
                }
            Next
        End If
    End Function

    <Extension>
    Public Function GetMetadata(handle As MethodInfo) As FunctionModel
        Dim desc As DescriptionAttribute = handle.GetCustomAttribute(Of DescriptionAttribute)
        Dim args As ToolArgument() = handle.GetArguments.ToArray
        Dim func As String
        Dim export As ExportAPIAttribute = handle.GetCustomAttribute(Of ExportAPIAttribute)
        Dim requires As IEnumerable(Of String) = From a As ToolArgument
                                                 In args
                                                 Where Not a.Optional
                                                 Select a.name

        If export Is Nothing OrElse export.Name.StringEmpty Then
            func = handle.Name
        Else
            func = export.Name
        End If

        Dim params As New FunctionParameters With {
            .required = requires.ToArray,
            .properties = New Dictionary(Of String, ParameterProperties)
        }

        For Each arg As ToolArgument In args
            params(arg.name) = New ParameterProperties With {
                .name = arg.name,
                .description = arg.desc,
                .type = arg.type,
                .[default] = arg.default
            }
        Next

        Return New FunctionModel With {
            .name = func,
            .description = desc.Description,
            .parameters = params
        }
    End Function

    Public Function Caller(Of T)(obj As T, handle As MethodInfo) As Func(Of FunctionCall, String)
        Return AddressOf (New CLRCaller(obj, handle)).Invoke
    End Function

    Private Function TryCastValue(val_str As String, p As ParameterInfo) As Object
        Dim val_type As Type = p.ParameterType

        If GetType(String) Is val_type Then
            Return val_str
        Else
            Return any.CTypeDynamic(val_str, val_type)
        End If
    End Function

    Private Class CLRCaller

        ReadOnly obj As Object
        ReadOnly handle As MethodInfo
        ReadOnly pars As ParameterInfo()
        ReadOnly singleRequired As Boolean

        Sub New(obj As Object, handle As MethodInfo)
            Me.pars = handle.GetParameters
            Me.obj = obj
            Me.handle = handle

            singleRequired = pars.Length = 1 OrElse pars.Where(Function(p) Not p.IsOptional).Count = 1
        End Sub

        Public Function Invoke(args As FunctionCall) As String
            Dim argVals As Object() = New Object(pars.Length - 1) {}

            ' 20260723 mis-matched parameter name maybe generated via LLMs tools function call
            ' set parameter value directly by offset in un-strict mode
            ' andalso if the target function only required one parameter
            If (Not args.strict) AndAlso singleRequired Then
                argVals(Scan0) = TryCastValue(args(0), handle.GetParameters.First)
            Else
                For i As Integer = 0 To argVals.Length - 1
                    Dim name As String = pars(i).Name
                    Dim val As Object = pars(i).DefaultValue

                    If args.has(name) Then
                        val = TryCastValue(args(name), pars(i))
                    ElseIf Not pars(i).IsOptional Then
                        Return $"error: call of the function '{args.name}' missing of the required parameter '{name}'!".GetJson
                    End If

                    argVals(i) = val
                Next
            End If

            Dim result As Object = handle.Invoke(obj, argVals)
            Dim str As String = any.ToString(result)

            Return str
        End Function
    End Class
End Module
