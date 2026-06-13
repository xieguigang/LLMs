Imports System.Runtime.CompilerServices

Namespace JSON.FunctionCall

    Public Class FunctionModel

        Public Property name As String
        Public Property description As String
        Public Property parameters As FunctionParameters

        Sub New()
        End Sub

        Sub New(name As String, desc As String, ParamArray args As ParameterProperties())
            Me.name = name
            Me.description = desc
            Me.parameters = New FunctionParameters With {
                .properties = args.ToDictionary(Function(a) a.name)
            }
        End Sub

    End Class

    Public Class FunctionParameters

        Public Property type As String = "object"
        Public Property properties As Dictionary(Of String, ParameterProperties)
        Public Property required As String()

        Default Public Property Argument(name As String) As ParameterProperties
            Get
                Return properties.TryGetValue(name)
            End Get
            Set(value As ParameterProperties)
                If value Is Nothing Then
                    properties.Remove(name)
                Else
                    properties(name) = value
                End If
            End Set
        End Property

    End Class

    Public Class ParameterProperties

        Public Property name As String
        Public Property description As String
        Public Property type As String = "string"

        Sub New()
        End Sub

        Sub New(name As String, desc As String, Optional type As TypeCode = TypeCode.String)
            Me.name = name
            Me.description = desc
            Me.type = type.ToString.ToLower
        End Sub

    End Class

    Public Class FunctionTool

        Public Property type As String = "function"
        Public Property [function] As FunctionModel

        <MethodImpl(MethodImplOptions.AggressiveInlining)>
        Public Shared Function CreateToolSet(ParamArray f As FunctionModel()) As FunctionTool()
            Return (From fi As FunctionModel
                    In f
                    Select New FunctionTool With {
                        .[function] = fi
                    }).ToArray
        End Function

    End Class
End Namespace