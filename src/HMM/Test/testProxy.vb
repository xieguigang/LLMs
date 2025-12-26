Public Class testProxy


    Shared Sub runHttp()

        Dim proxy As New OllamaProxy.OllamaProxyServer(333)

        proxy.Run()

    End Sub
End Class
