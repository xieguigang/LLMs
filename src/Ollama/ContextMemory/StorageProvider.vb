Imports System.IO
Imports Microsoft.VisualBasic.Serialization.JSON

Public Delegate Function LoadContext(s As Stream) As IEnumerable(Of ChatMessage)
Public Delegate Sub SaveContext(context As IEnumerable(Of ChatMessage), s As Stream)

Public Module StorageProvider

    Public Iterator Function LoadContext(s As Stream) As IEnumerable(Of ChatMessage)
        For Each line As String In s.IterateAllLines
            Yield line.LoadJSON(Of ChatMessage)
        Next
    End Function

    Public Sub SaveContext(context As IEnumerable(Of ChatMessage), s As Stream)
        Using wd As New StreamWriter(s, leaveOpen:=True)
            For Each line As ChatMessage In context
                Call wd.WriteLine(line.GetJson)
            Next
        End Using
    End Sub

End Module
