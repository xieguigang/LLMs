Namespace JSON

    Public Class ResponseBody

        ''' <summary>
        ''' 每个batch文件只能包含对单个模型的请求,支持 glm-4、glm-3-turbo.
        ''' </summary>
        ''' <returns></returns>
        Public Property model As String
        Public Property message As History
        Public Property done As Boolean

        ''' <summary>
        ''' 仅当 done=True 时有效：prompt 求值时所处理的 token 数量
        ''' </summary>
        ''' <returns></returns>
        Public Property prompt_eval_count As Long?
        ''' <summary>
        ''' 仅当 done=True 时有效：本次生成的 token 数量
        ''' </summary>
        ''' <returns></returns>
        Public Property eval_count As Long?

    End Class
End Namespace