Imports Microsoft.VisualBasic.ComponentModel.Settings.Inf

Public Class LLMConfig

    Public Property provider As String
    Public Property key As String
    Public Property model As String

    Public Function CreateLLm() As LLMClient
        Return New LLMClient(LLMUrl.Create(provider, apikey:=key), model)
    End Function

    Public Sub SaveDefault()
        Call Save(defaultConfig)
    End Sub

    Public Sub Save(ini As String)
        Using inifile As New IniFile(ini)
            Dim data = inifile.OpenSection("llm")

            Call data.SetValue("provider", provider)
            Call data.SetValue("key", key)
            Call data.SetValue("model", model)
        End Using
    End Sub

    Public Shared Function Load(ini As String) As LLMConfig
        Dim inifile As New IniFile(ini)
        Dim config As Section = inifile.OpenSection("llm")
        Dim llm As New LLMConfig With {
            .key = config.GetValue("key"),
            .model = config.GetValue("model"),
            .provider = config.GetValue("provider")
        }

        Return llm
    End Function

    Shared ReadOnly defaultConfig As String = App.ProductProgramData & "/llm.ini"

    Public Shared Function LoadDefault() As LLMConfig
        If Not defaultConfig.FileExists Then
            Call New LLMConfig().Save(defaultConfig)
        End If

        Return Load(defaultConfig)
    End Function

End Class
