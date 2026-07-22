Imports System.ComponentModel
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic.CommandLine.Reflection
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
    Public Function GetMetadata(handle As MethodInfo) As FunctionModel
        Dim desc As DescriptionAttribute = handle.GetCustomAttribute(Of DescriptionAttribute)
        Dim args As ArgumentAttribute() = handle.GetCustomAttributes(Of ArgumentAttribute).ToArray
        Dim func As String
        Dim export As ExportAPIAttribute = handle.GetCustomAttribute(Of ExportAPIAttribute)
        Dim requires As IEnumerable(Of String) =
            From a As ArgumentAttribute
            In args
            Where Not a.Optional
            Select a.Name

        If export Is Nothing OrElse export.Name.StringEmpty Then
            func = handle.Name
        Else
            func = export.Name
        End If

        Dim params As New FunctionParameters With {
            .required = requires.ToArray,
            .properties = New Dictionary(Of String, ParameterProperties)
        }

        For Each arg As ArgumentAttribute In args
            params(arg.Name) = New ParameterProperties With {
                .name = arg.Name,
                .description = arg.Description,
                .type = arg.TokenType.Description.ToLower
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

        Sub New(obj As Object, handle As MethodInfo)
            Me.obj = obj
            Me.handle = handle
        End Sub

        Public Function Invoke(args As FunctionCall) As String
            Dim pars As ParameterInfo() = handle.GetParameters
            Dim argVals As Object() = New Object(pars.Length - 1) {}

            If (Not args.strict) AndAlso argVals.Length = 1 AndAlso handle.GetParameters.Length = 1 Then
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
