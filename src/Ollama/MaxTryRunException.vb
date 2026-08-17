Public Class MaxTryRunException : Inherits Exception

    Sub New(max_round As Integer)
        Call MyBase.New($"Exceeded max tool call rounds: {max_round} round reached!")
    End Sub

End Class
