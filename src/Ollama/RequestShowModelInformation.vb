Imports Microsoft.VisualBasic.MIME.application.json.Javascript

Public Class RequestShowModelInformation

    Public Property model As String
    Public Property verbose As Boolean = True

End Class

''' <summary>
''' 归一化后的模型信息：将 Ollama / OpenAI 等不同后端的模型信息映射为统一结构，
''' 调用方无需关心底层后端；同时保留 <see cref="Raw"/> 原始响应以供深入解析。
''' </summary>
Public Class ModelInfo
    ''' <summary>模型标识（Ollama: model；OpenAI: id）</summary>
    Public Property Id As String
    ''' <summary>后端来源："ollama" / "openai"</summary>
    Public Property Provider As String
    ''' <summary>模型族（Ollama: details.family）</summary>
    Public Property Family As String
    ''' <summary>参数规模（Ollama: details.parameter_size）</summary>
    Public Property ParameterSize As String
    ''' <summary>量化等级（Ollama: details.quantization_level）</summary>
    Public Property QuantizationLevel As String
    ''' <summary>格式（Ollama: details.format）</summary>
    Public Property Format As String
    ''' <summary>拥有者（OpenAI: owned_by）</summary>
    Public Property OwnedBy As String
    ''' <summary>创建/修改时间戳（Unix 秒；OpenAI: created，Ollama: modified_at 转换）</summary>
    Public Property CreatedAt As Long?
    ''' <summary>后端原始响应，供调用方深入解析</summary>
    Public Property Raw As JsonObject
End Class