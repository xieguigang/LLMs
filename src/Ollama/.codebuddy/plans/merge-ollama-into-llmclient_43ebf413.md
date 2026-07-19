---
name: merge-ollama-into-llmclient
overview: 将旧的 Ollama.vb 单体客户端合并到基于 ILLMProvider 的新 LLMClient.vb 中，补全两个后端（Ollama / OpenAI）的 JSON 响应到统一数据结构（ChatResponseChunk + 统一的 OllamaResponse/think+output）的转换逻辑，删除旧 Ollama.vb 并把 SkillAgent.vb 改造为使用 LLMClient + OllamaProvider。
todos:
  - id: extend-unified-types
    content: 扩展 ChatResponseChunk 增加 ThinkContent，清理 OllamaResponse 静态 Chat 方法
    status: completed
  - id: impl-ollama-provider
    content: 实现 OllamaProvider 消息转换与流式 think/tool_calls 解析
    status: completed
    dependencies:
      - extend-unified-types
  - id: impl-openai-provider
    content: 实现 OpenAIProvider 转换并修复流式 tool_calls 拼装与参数解析
    status: completed
    dependencies:
      - extend-unified-types
  - id: merge-into-llmclient
    content: 将 Ollama 功能合并进 LLMClient，Chat 返回 OllamaResponse
    status: completed
    dependencies:
      - extend-unified-types
  - id: migrate-skillagent
    content: 删除旧 Ollama.vb，把 SkillAgent 改造为使用 LLMClient+OllamaProvider
    status: completed
    dependencies:
      - merge-into-llmclient
  - id: build-verify
    content: 编译验证整个项目并修复引用
    status: completed
    dependencies:
      - impl-ollama-provider
      - impl-openai-provider
      - merge-into-llmclient
      - migrate-skillagent
---

## 用户需求

将旧的 `Ollama.vb` 单体客户端合并进基于 `ILLMProvider` 接口的新统一客户端 `LLMClient.vb`，并实现 OpenAI 与 Ollama 两种后端 JSON 响应到对外统一数据结构的转换。合并后删除旧的 `Ollama.vb`，并将依赖它的 `SkillAgent.vb` 改造为使用 `LLMClient` + `OllamaProvider`。

## 产品概述

围绕统一接口 `ILLMProvider`，把 Ollama（NDJSON 流）与 OpenAI（SSE 流）两类后端封装为一致的使用方式：`LLMClient` 持有记忆、函数注册表与日志，调用任意 Provider 完成多轮对话与工具调用，对外返回包含「思考(think)」与「正文(output)」的统一响应对象。

## 核心特性

- 统一数据结构：流式 `ChatResponseChunk` 增加 `ThinkContent`（思考增量），最终 `OllamaResponse(think, output)` 作为统一对外响应类型。
- Ollama 后端：将 `ChatMessage` 转为 Ollama `History` 请求；解析 NDJSON 响应，按 `<think>` 状态机拆分思考/正文增量，并在 `done` 时收集 `tool_calls`。
- OpenAI 后端：将消息/工具转为 OpenAI 请求格式；修复 SSE 流中 `tool_calls` 按 `index` 跨 chunk 拼装与参数(JSON 字符串)解析，并把 `reasoning_content` 映射到 `ThinkContent`。
- 功能合并：`AddFunction`（含 CLR 反射注册）、系统提示、`ExecuteTool` 工具执行、记忆与 jsonl 日志、`GetModelInformation`、`Clear` 全部并入 `LLMClient`。
- 接口收敛：删除 `Ollama.vb`，`SkillAgent` 改用 `LLMClient` + `OllamaProvider`。

## 技术栈与约束

- 语言/框架：VB.NET（.NET 10，net10.0 / net10.0-windows），项目 `Ollama.vbproj`。
- 依赖：sciBASIC#（`Microsoft.VisualBasic.Core`、`JSON-netcore5`），使用其 `Serialization.JSON` 扩展（`LoadJSON`/`GetJson`/`CreateObject`）与 `Microsoft.VisualBasic.MIME.application.json`（`JsonObject`/`JsonArray`/`JsonParser`）。
- 约束：复用现有 `ILLMProvider`/`ChatMessage`/`ToolCallInfo`/`FunctionTool` 等类型；不引入新依赖；保持 `SkillAgent` 对 `.think`/`.output`/`.AddFunction` 的调用形态不变。

## 实现思路

1. 扩展统一数据结构：`ChatResponseChunk` 增加 `ThinkContent As String`，承载流式思考增量；`OllamaResponse(think, output)` 作为统一最终响应类型（移除其静态 `Chat` 方法，避免删除 `Ollama` 类后编译失败）。
2. Ollama 适配：在 `OllamaProvider` 实现 `ConvertToOllamaMessages`（`ChatMessage`→`History`，含 `tool_calls`/`tool_call_id` 映射）；`ParseOllamaStream` 用轻量状态机按 `<think>…</think>` 边界把增量拆分到 `ThinkContent`/`DeltaContent`，在 `done` 时构建 `ToolCallInfo`（`Id` 为空时生成兜底 id，`FunctionArguments` 已是 `Dictionary` 直接复用）。
3. OpenAI 适配：在 `OpenAIProvider` 内实现 `ConvertToOpenAIMessages`/`ConvertToOpenAITools`（替代空的 `JSONFormat` 模块）；修复 `ParseOpenAIStream`：维护按 `index` 的临时 `ToolCallInfo` 列表，跨 chunk 追加 `function.name` 与 `function.arguments`（JSON 字符串片段），在 `[DONE]` 时将拼接后的 JSON 字符串解析为 `Dictionary(Of String, String)` 并随 `IsDone` chunk 输出；`delta.reasoning_content`/`。think` 映射到 `ThinkContent`。
4. 合并客户端：`LLMClient` 并入旧 `Ollama` 的能力（`AddFunction` 两个重载、`system_message`/`AddSystemPrompt`、`tool_invoke`/`ExecuteTool`、`max_memory_size`/`preserveMemory`、jsonl 日志、`GetLastFunctionCalls`、`Clear`、`GetModelInformation`）；`Chat(message)` 改为 `Task(Of OllamaResponse)`，复用现有多轮工具循环，聚合 `ThinkContent`→think、`DeltaContent`→output 并保留流式 `Console` 输出；系统提示在构建请求时前置（若无 system 消息）。
5. 收敛依赖：删除 `Ollama.vb` 与空壳 `JSONFormat.vb`；`SkillAgent` 字段/构造改为 `LLMClient` + `OllamaProvider`（默认 `127.0.0.1:11434` 与默认模型），`AddFunction` 与 `Chat` 返回 `OllamaResponse` 行为保持不变。

## 实现要点（防回归）

- 性能：流式逐行/逐事件解析，复杂度 O(_chunk)；`tool_calls` 拼装仅在 `[DONE]` 解析一次参数，避免每 chunk 重复 JSON 解析；`ai_memory` 受 `max_memory_size` 限制。
- 兼容性：`ToolCallInfo.FunctionArguments` 已是 `Dictionary(Of String, String)`，Ollama 直传、OpenAI 需将参数 JSON 字符串解析为 Dict；`OllamaResponse` 的 `Narrowing CType` 到 `String` 与 `ParseResponse` 保留。
- 日志：沿用旧 `Ollama` 的临时 jsonl 日志初始化方式（`TempFileSystem.GetAppSysTempFile`）；`ai_log` 在 `Dispose` 中 flush/dispose。
- 爆炸半径：仅修改/删除上述文件；`test/` 项目在主构建中已被排除，不改动。

## 架构设计

- 数据流：调用方 → `LLMClient.Chat` → 构造 `ChatRequestOptions` → `ILLMProvider.StreamChatAsync`（Ollama/OpenAI Provider 各自做 请求转换 + HTTP 流式 + 响应→`ChatResponseChunk`）→ `LLMClient` 聚合 `ChatResponseChunk` 为 `OllamaResponse`；遇 `tool_calls` 经 `ExecuteTool` 执行后追加历史多轮循环。
- 模块关系：`ILLMProvider`（统一契约）→ `OllamaProvider`/`OpenAIProvider`（后端适配）→ `LLMClient`（统一客户端/记忆/工具）→ `SkillAgent`（业务编排）。

## 目录结构与改动

```
g:/LLMs/src/Ollama/
├── ILLMProvider.vb          # [MODIFY] 为 ChatResponseChunk 增加 ThinkContent 字段（流式思考增量），其余统一类型不变。
├── OllamaResponse.vb        # [MODIFY] 移除静态 Chat 方法（依赖旧 Ollama 类）；保留 think/output、ParseResponse、CType、who_are_you。
├── OllamaProvider.vb        # [MODIFY] 实现 ConvertToOllamaMessages（ChatMessage→History）；ParseOllamaStream 增加 <think> 状态机拆分与 done 时 tool_calls 收集。
├── OpenAIProvider.vb        # [MODIFY] 实现 ConvertToOpenAIMessages/ConvertToOpenAITools（私有）；修复 ParseOpenAIStream 的 tool_calls 按 index 跨 chunk 拼装、参数 JSON 字符串→Dict、reasoning_content→ThinkContent。
├── JSONFormat.vb            # [DELETE] 空壳转换函数已移入 OpenAIProvider。
├── LLMClient.vb             # [MODIFY] 并入旧 Ollama 功能：AddFunction(两重载)、system_message/AddSystemPrompt、tool_invoke/ExecuteTool、记忆与 jsonl 日志、GetLastFunctionCalls、Clear、GetModelInformation；Chat 返回 OllamaResponse，聚合 think/output。
├── Ollama.vb                # [DELETE] 旧单体客户端，逻辑已并入 LLMClient。
└── SkillSystem/
    └── SkillAgent.vb        # [MODIFY] 字段 _ollama 类型改为 LLMClient；构造改为 New LLMClient(New OllamaProvider("127.0.0.1:11434"), model)；AddFunction/Chat(.think/.output) 保持不变。
```

## 关键代码结构

```
' ILLMProvider.vb —— 统一流式响应块（新增 ThinkContent）
Public Class ChatResponseChunk
    Public Property IsDone As Boolean
    Public Property DeltaContent As String        ' 正文增量
    Public Property ThinkContent As String        ' 思考(reasoning)增量
    Public Property ToolCalls As List(Of ToolCallInfo)
End Class

' OpenAIProvider 内部：将拼装后的参数 JSON 字符串解析为统一 Dict
Private Shared Function ParseArguments(json As String) As Dictionary(Of String, String)
    ' 使用 Microsoft.VisualBasic.MIME.application.json.JsonParser 解析为 JsonObject，
    ' 遍历键值生成 Dictionary(Of String, String)（值统一 ToString）
End Function
```