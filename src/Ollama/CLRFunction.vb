Imports System.ComponentModel
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Ollama.JSON.FunctionCall

Module CLRFunction

    Public Iterator Function GetTarget(obj As Type, name As String) As IEnumerable(Of MethodInfo)
        Dim list = obj.GetMethods.Where(Function(a) a.Name = name).ToArray

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
        Dim requires As IEnumerable(Of String) = From a As ArgumentAttribute
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
                .type = arg.Pipeline.Description.ToLower
            }
        Next

        Return New FunctionModel With {
            .name = func,
            .description = desc.Description,
            .parameters = params
        }
    End Function

    Public Function Caller(Of T)(obj As T, handle As MethodInfo) As Func(Of FunctionCall, String)

    End Function
End Module
