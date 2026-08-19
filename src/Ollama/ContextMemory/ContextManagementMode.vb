
''' <summary>
''' 上下文管理模式：Trim = 直接丢弃旧消息；Compress = 通过 LLM 将旧消息压缩为摘要。
''' </summary>
Public Enum ContextManagementMode
    ''' <summary>直接丢弃旧消息（默认，保持向后兼容）</summary>
    Trim
    ''' <summary>将旧消息压缩为摘要文本，节省 Token 占用</summary>
    Compress
End Enum