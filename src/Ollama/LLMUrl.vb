Public Module LLMUrl

    ''' <summary>
    ''' 
    ''' </summary>
    ''' <param name="url">
    ''' openai://api.deepseek.com
    ''' ollama://127.0.0.1:11434
    ''' </param>
    ''' <returns></returns>
    Public Function Create(url As String, Optional apikey As String = Nothing) As ILLMProvider
        Dim t = url.GetTagValue(":", trim:=True, failureNoName:=True)
        Dim server As String = t.Value.Trim("/"c)

        Select Case t.Name.ToLower
            Case "openai"
                Return New OpenAIProvider(server, apikey)
            Case Else
                ' ollama or empty
                Return New OllamaProvider(server)
        End Select
    End Function
End Module
