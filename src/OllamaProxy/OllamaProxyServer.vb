Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Runtime
Imports System.Text
Imports Flute.Http.Configurations
Imports Flute.Http.Core
Imports Microsoft.VisualBasic.ApplicationServices
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.ComponentModel.DataSourceModel
Imports Microsoft.VisualBasic.Language
Imports Microsoft.VisualBasic.Linq
Imports Microsoft.VisualBasic.Net.Http
Imports Microsoft.VisualBasic.Serialization.JSON

''' <summary>
''' A proxy server that forwards requests to Ollama with RAG-augmented context.
''' Implements OpenAPI-compatible /v1/chat/completions endpoint.
''' </summary>
Public Class OllamaProxyServer
    Inherits HttpServer

    Private ReadOnly _ollamaBaseUrl As String = "http://localhost:11434"

    Public Sub New(port As Integer, Optional threads% = -1, Optional configs As Configuration = Nothing)
        MyBase.New(port, threads, configs)
    End Sub

    Protected Overrides Function getHttpProcessor(client As TcpClient, bufferSize%) As HttpProcessor
        Return New OllamaHttpProcessor(client, Me, bufferSize, _settings)
    End Function

    Public Overrides Sub handleGETRequest(p As HttpProcessor)
        ' Simple health check or redirect
        If p.http_url = "/" OrElse p.http_url.StartsWith("/health") Then
            p.writeSuccess("text/plain")
            p.WriteLine("Ollama Proxy Server is running.")
        Else
            p.writeFailure(HTTP_RFC.RFC_NOT_FOUND, "Endpoint not found.")
        End If
    End Sub

    Public Overrides Sub handlePOSTRequest(p As HttpProcessor, inputData$)
        Dim reqUrl = p.http_url.ToLowerInvariant()

        If reqUrl = "/v1/chat/completions" Then
            HandleChatCompletion(p, inputData)
        Else
            p.writeFailure(HTTP_RFC.RFC_NOT_FOUND, "Unsupported endpoint.")
        End If
    End Sub

    Public Overrides Sub handleOtherMethod(p As HttpProcessor)
        p.writeFailure(HTTP_RFC.RFC_METHOD_NOT_ALLOWED, "Method not allowed.")
    End Sub

    Private Sub HandleChatCompletion(p As HttpProcessor, tempFile$)
        Try
            ' Read the original JSON request
            Dim jsonReq As String = File.ReadAllText(tempFile, Encoding.UTF8)
            Dim obj = jsonReq.LoadJSON(Of Dictionary(Of String, Object))

            If Not obj.ContainsKey("messages") OrElse Not TypeOf obj("messages") Is IEnumerable(Of Object) Then
                p.writeFailure(HTTP_RFC.RFC_BAD_REQUEST, "Invalid messages format.")
                Return
            End If

            Dim messages = CType(obj("messages"), IEnumerable(Of Object))
            Dim lastMsg = messages.LastOrDefault()
            Dim userContent As String = ""

            If TypeOf lastMsg Is Dictionary(Of String, Object) Then
                Dim msgDict = CType(lastMsg, Dictionary(Of String, Object))
                If msgDict.ContainsKey("role") AndAlso CStr(msgDict("role")) = "user" AndAlso msgDict.ContainsKey("content") Then
                    userContent = CStr(msgDict("content"))
                End If
            End If

            ' === Perform RAG Search ===
            Dim ragContext As String = DoRagSearch(userContent)

            ' Augment the user message with RAG context
            Dim augmentedContent As String
            If Not String.IsNullOrEmpty(ragContext) Then
                augmentedContent = $"[CONTEXT FROM KNOWLEDGE BASE] {ragContext} [END CONTEXT] {userContent}"
            Else
                augmentedContent = userContent
            End If

            ' Rebuild messages with augmented content
            Dim newMessages As New List(Of Dictionary(Of String, Object))
            For i As Integer = 0 To messages.Count - 2
                newMessages.Add(CType(messages.ElementAt(i), Dictionary(Of String, Object)))
            Next
            newMessages.Add(New Dictionary(Of String, Object) From {
                {"role", "user"},
                {"content", augmentedContent}
            })

            ' Build Ollama request (compatible with /api/generate)
            Dim ollamaReq = New Dictionary(Of String, Object) From {
                {"model", obj.TryGetValue("model", "llama3")},
                {"prompt", BuildPromptFromMessages(newMessages)},
                {"stream", True}
            }

            Dim ollamaJson = ollamaReq.GetJson(indent:=False)
            Dim ollamaBytes = Encoding.UTF8.GetBytes(ollamaJson)

            ' Forward to Ollama
            Dim ollamaUrl = $"{_ollamaBaseUrl}/api/generate"
            Dim httpReq As HttpWebRequest = CType(WebRequest.Create(ollamaUrl), HttpWebRequest)
            httpReq.Method = "POST"
            httpReq.ContentType = "application/json"
            httpReq.ContentLength = ollamaBytes.Length

            Using reqStream = httpReq.GetRequestStream()
                reqStream.Write(ollamaBytes, 0, ollamaBytes.Length)
            End Using

            ' Get response and stream back to client
            Using resp As HttpWebResponse = CType(httpReq.GetResponse(), HttpWebResponse)
                p.outputStream.WriteLine("HTTP/1.0 200 OK")
                p.outputStream.WriteLine("Content-Type: text/event-stream")
                p.outputStream.WriteLine("Connection: close")
                p.outputStream.WriteLine("Cache-Control: no-cache")
                p.outputStream.WriteLine("X-Powered-By: OllamaProxy/VB.NET")
                p.outputStream.WriteLine() ' End headers

                Using reader As New StreamReader(resp.GetResponseStream())
                    Dim line As Value(Of String) = ""

                    While (line = reader.ReadLine()) IsNot Nothing
                        ' Forward each line as SSE
                        p.outputStream.WriteLine($"data: {CStr(line)}")
                        p.outputStream.WriteLine()
                        p.outputStream.Flush()
                    End While
                End Using
            End Using

        Catch ex As WebException When ex.Status = WebExceptionStatus.ProtocolError
            Dim resp = CType(ex.Response, HttpWebResponse)
            p.writeFailure(CType(resp.StatusCode, HTTP_RFC), ex.Message)
        Catch ex As Exception
            p.writeFailure(HTTP_RFC.RFC_INTERNAL_SERVER_ERROR, ex.ToString())
        Finally
            If File.Exists(tempFile) Then File.Delete(tempFile)
        End Try
    End Sub

    ''' <summary>
    ''' Convert chat messages to a single prompt string (for /api/generate).
    ''' You can customize this for your model's prompt template.
    ''' </summary>
    Private Function BuildPromptFromMessages(messages As List(Of Dictionary(Of String, Object))) As String
        Dim sb As New StringBuilder()
        For Each msg In messages
            Dim role = CStr(msg("role"))
            Dim content = CStr(msg("content"))
            sb.AppendLine($"{role.ToUpper()}: {content}")
        Next
        sb.AppendLine("ASSISTANT:")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' YOUR RAG IMPLEMENTATION HERE.
    ''' Given a user query, return relevant context from your knowledge base.
    ''' </summary>
    Private Function DoRagSearch(query As String) As String
        ' TODO: Replace with actual RAG logic (e.g., vector DB lookup, BM25, etc.)
        ' Example:
        '   Return MyVectorDB.Search(query, topK:=3).Join(vbCrLf)
        Return "hello world"
        Return "" ' No context by default
    End Function
End Class

''' <summary>
''' Custom processor to handle disposal properly
''' </summary>
Public Class OllamaHttpProcessor
    Inherits HttpProcessor

    Sub New(socket As TcpClient, srv As HttpServer, MAX_POST_SIZE%, settings As Configuration)
        MyBase.New(socket, srv, MAX_POST_SIZE, settings)
    End Sub
End Class