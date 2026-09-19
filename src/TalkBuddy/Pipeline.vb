' ---------------------------------------------------------------------------
' DemoPipeline —— 把 demo 需要的所有零件装配起来
'
' 它是"装配层"：负责把分词器、模型、模板、语料合成器、工具注册表拼到一起，
' 并把三大机制（MoE / KV Cache / Function Calling）逐个可观测地演示出来。
' 所有算法都在 Microsoft.VisualBasic.MachineLearning.LLM 里，这里不实现任何算法。
'
' 三个训练阶段共用同一个模型，只是换数据与序列长度：
'
'   Stage 1  预训练      纯文本 + 下一个 token 预测           → 获得"续写"能力
'   Stage 2  指令 SFT    合成指令-回复对，损失只计 assistant  → 学会"轮到我说话"
'   Stage 3  工具 SFT    合成工具调用轨迹，工具结果不计损失    → 学会输出调用片段
' ---------------------------------------------------------------------------

Imports System.Collections.Generic
Imports Microsoft.VisualBasic.MachineLearning.LLM
Imports Microsoft.VisualBasic.MachineLearning.TensorFlow
Imports GpuTensor = Microsoft.VisualBasic.Computing.ILCuda.GPUTensor
Imports Diagnostics = System.Diagnostics

Public Class DemoPipeline

    Private ReadOnly _verbosity As Integer

    Private _codec As DeepSeekTokenizerAdapter
    Private _template As ChatTemplate
    Private _registry As ToolRegistry
    Private _model As LLMModel
    Private _corpus As EmbeddedCorpus
    Private _instructionSynth As SftSynthesizer
    Private _toolSynth As ToolCallSynthesizer

    Private _pretrainTokens As Integer()
    Private _cursor As Integer
    Private _eosId As Integer

    ''' <summary>是否成功切到了 CUDA 后端。</summary>
    Public ReadOnly Property CudaEnabled As Boolean

    ''' <summary>分词器适配器。</summary>
    Public ReadOnly Property Codec As DeepSeekTokenizerAdapter
        Get
            Return _codec
        End Get
    End Property

    ''' <summary>语言模型。</summary>
    Public ReadOnly Property Model As LLMModel
        Get
            Return _model
        End Get
    End Property

    ''' <summary>工具注册表。</summary>
    Public ReadOnly Property Registry As ToolRegistry
        Get
            Return _registry
        End Get
    End Property

    ''' <summary>对话模板。</summary>
    Public ReadOnly Property Template As ChatTemplate
        Get
            Return _template
        End Get
    End Property

    ''' <param name="verbosity">0 = 只在阶段之间打印，1 = 每个训练步都打印</param>
    Public Sub New(Optional verbosity As Integer = 1)
        _verbosity = verbosity

        ConsoleReport.Section("0. 初始化")

        ' ---- 计算后端 ----
        CudaEnabled = TryRegisterCuda()

        ' ---- 随机种子：让整次运行可复现 ----
        Microsoft.VisualBasic.MachineLearning.Transformer.TensorOps.Seed = DemoConfig.RandomSeed
        ConsoleReport.KeyValue("random seed", DemoConfig.RandomSeed)

        ' ---- 分词器 ----
        _codec = DeepSeekTokenizerAdapter.Load(DemoConfig.ResolveTokenizerDirectory(),
                                              DemoConfig.VocabularyLimit, verbose:=True)
        _eosId = _codec.TokenIdOf(ToolCallProtocol.EndOfSentenceMarker)

        ConsoleReport.KeyValue("protocol tokens", DescribeSpecialTokens())

        ' ---- 模型 ----
        Dim modelConfig = DemoConfig.CreateModelConfig(_codec.VocabSize)

        _model = New LLMModel(modelConfig, DemoConfig.WeightDecay)

        ConsoleReport.KeyValue("dtype", "Double（纯托管张量运行时）")
        ConsoleReport.KeyValue("backend", If(CudaEnabled, "CUDA GPU", "SIMD CPU"))

        ' ---- 模板 / 语料 / 工具 ----
        _template = New ChatTemplate(_codec)
        _registry = DemoTools.CreateRegistry()
        _corpus = New EmbeddedCorpus()
        _instructionSynth = New SftSynthesizer(_codec, _template, DemoConfig.RandomSeed)
        _toolSynth = New ToolCallSynthesizer(_codec, _template, _registry, DemoConfig.RandomSeed + 1)

        _pretrainTokens = _corpus.Tokenize(_codec, DemoConfig.PretrainSequenceLength)
    End Sub

    ''' <summary>尝试注册 CUDA 后端；失败时保持 CPU 并打印原因。</summary>
    Private Shared Function TryRegisterCuda() As Boolean
        If Not DemoConfig.TryCuda Then
            ConsoleReport.KeyValue("cuda", "已按配置禁用")
            Return False
        End If

        Try
            Dim ok = GpuTensor.CudaTensor.Register()

            If ok Then
                ConsoleReport.KeyValue("cuda", $"已启用（{GpuTensor.CudaTensor.Current?.Name}）")
                Return True
            End If

            ConsoleReport.KeyValue("cuda", $"不可用，回退 CPU：{GpuTensor.CudaTensor.LastError}")
        Catch ex As Exception
            ConsoleReport.KeyValue("cuda", $"注册异常，回退 CPU：{ex.Message}")
        End Try

        Return False
    End Function

    Private Function DescribeSpecialTokens() As String
        Dim parts As New List(Of String)

        For Each marker In New String() {
            ToolCallProtocol.BeginOfSentenceMarker,
            ToolCallProtocol.EndOfSentenceMarker,
            ToolCallProtocol.UserMarker,
            ToolCallProtocol.AssistantMarker,
            ToolCallProtocol.CallsBeginMarker,
            ToolCallProtocol.CallBeginMarker,
            ToolCallProtocol.SepMarker,
            ToolCallProtocol.CallEndMarker,
            ToolCallProtocol.CallsEndMarker,
            ToolCallProtocol.OutputsBeginMarker,
            ToolCallProtocol.OutputBeginMarker,
            ToolCallProtocol.OutputEndMarker,
            ToolCallProtocol.OutputsEndMarker}

            Call parts.Add($"{marker}={_codec.TokenIdOf(marker)}")
        Next

        Return String.Join(", ", parts)
    End Function

#Region "1. 模型结构"

    ''' <summary>打印模型结构与参数量统计（总参数 vs 单 token 激活参数）。</summary>
    Public Sub ShowModelStructure()
        ConsoleReport.Section("1. 模型结构")

        ConsoleReport.Note(_model.DescribeModel())

        ConsoleReport.Note("")
        ConsoleReport.Note("读法：")
        ConsoleReport.Note("  * total 是"知识容量"——全部专家的参数都要驻留内存，即使某个 token 用不到；")
        ConsoleReport.Note("  * active/token 是"实际计算量"——只有 Top-K 路由专家 + 全部共享专家参与前向；")
        ConsoleReport.Note("  * 两者的比值就是 MoE 的激活率，也是"大容量、低算力"这一卖点的量化形式。")
    End Sub

#End Region

#Region "2. 预训练"

    ''' <summary>Stage 1：下一个 token 预测。</summary>
    Public Sub RunPretrain(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.PretrainSteps

        ConsoleReport.Section("2. Stage 1 · 预训练（下一个 token 预测）")

        Dim tokenCount = _pretrainTokens.Length

        ConsoleReport.KeyValue("corpus length", $"{_corpus.Text.Length} 字符 → {tokenCount} tokens")
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.PretrainSequenceLength} " &
                                          $"→ 每步 {DemoConfig.BatchSize * DemoConfig.PretrainSequenceLength} 个预测目标")
        ConsoleReport.KeyValue("steps", steps)
        ConsoleReport.KeyValue("learning rate", $"{DemoConfig.LearningRate} (warmup {DemoConfig.WarmupSteps} + cosine)")
        ConsoleReport.Note("")
        ConsoleReport.Note("注意：LM head 是 [d_model] × [vocab] 级别的矩阵乘，在 10 万词表下它是整步耗时的大头。")

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps))
        Dim watch = Diagnostics.Stopwatch.StartNew()

        _cursor = 0

        For [step] As Integer = 1 To steps
            Dim batch = EmbeddedCorpus.TakeBatch(_pretrainTokens, DemoConfig.BatchSize,
                                                 DemoConfig.PretrainSequenceLength, _cursor, _eosId)
            Dim report = trainer.TrainStep(batch)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        watch.Stop()

        ConsoleReport.KeyValue("total time", $"{watch.Elapsed.TotalSeconds:F1} s " &
                                              $"({watch.Elapsed.TotalMilliseconds / steps:F0} ms/step)")
        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
        ConsoleReport.Note("")
        ConsoleReport.Note("loss 曲线：")
        ConsoleReport.Note(ConsoleReport.LossCurve(trainer.History.Select(Function(r) r.Loss)))
    End Sub

#End Region

#Region "3. 指令 SFT"

    ''' <summary>Stage 2：指令跟随 SFT。</summary>
    Public Sub RunInstructionSft(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.InstructionSftSteps

        ConsoleReport.Section("3. Stage 2 · 指令跟随 SFT")

        Dim samples = _instructionSynth.CreateSamples(System.Math.Max(steps * DemoConfig.BatchSize, 16))
        Dim sample = samples(0)

        ConsoleReport.KeyValue("samples", samples.Count)
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.InstructionSequenceLength}")
        ConsoleReport.KeyValue("sample tokens", $"{sample.TokenIds.Length}（其中 {sample.SupervisedTokens} 个计入损失）")
        ConsoleReport.Note("")
        ConsoleReport.Note("样本明文（---- 之前是 system+user，一律不计损失）：")
        ConsoleReport.Note("  " & sample.Text.Replace(vbLf, " ⏎ ").Replace(ToolCallProtocol.BeginOfSentenceMarker, "[BOS]"))
        ConsoleReport.Note("")
        ConsoleReport.Note("损失掩码的实际作用是：模型只在 assistant 的 token 上学"该怎么回答"，")
        ConsoleReport.Note("不会去学"复述用户问了什么"——那样只会把有限的容量浪费在记忆输入上。")

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps))

        For [step] As Integer = 1 To steps
            Dim slice = Slice(samples, [step] * DemoConfig.BatchSize, DemoConfig.BatchSize)
            Dim batch = _template.CreateBatch(slice, DemoConfig.InstructionSequenceLength, DemoConfig.BatchSize)
            Dim report = trainer.TrainStep(batch)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
    End Sub

#End Region

#Region "4. Function Calling SFT"

    ''' <summary>Stage 3：工具调用 SFT。</summary>
    Public Sub RunToolSft(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.ToolSftSteps

        ConsoleReport.Section("4. Stage 3 · Function Calling SFT")

        Dim samples = _toolSynth.CreateSamples(System.Math.Max(steps * DemoConfig.BatchSize, 20))

        ConsoleReport.KeyValue("samples", samples.Count)
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.ToolSequenceLength}")

        ' 找一条"完整闭环"样本（含 tool 结果）来展示掩码
        Dim withTool = samples.FirstOrDefault(Function(s) s.Text.Contains(ToolCallProtocol.OutputsBeginMarker))

        If withTool IsNot Nothing Then
            ConsoleReport.KeyValue("full-trajectory sample", $"{withTool.TokenIds.Length} tokens / {withTool.SupervisedTokens} supervised")
            ConsoleReport.Note("")
            ConsoleReport.Note("这条样本里被 mask 掉的部分：")
            ConsoleReport.Note("  " & ToolCallProtocol.OutputsBeginMarker & " … " & ToolCallProtocol.OutputsEndMarker &
                               "（框架回填的工具结果，模型不需要学习"预测工具返回什么"）")
        End If

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps))

        For [step] As Integer = 1 To steps
            Dim slice = Slice(samples, [step] * DemoConfig.BatchSize, DemoConfig.BatchSize)
            Dim batch = _template.CreateBatch(slice, DemoConfig.ToolSequenceLength, DemoConfig.BatchSize)
            Dim report = trainer.TrainStep(batch)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
    End Sub

    ''' <summary>
    ''' 教师强制下的"调用片段合规率"：逐位置看 argmax 是否等于目标 token。
    ''' </summary>
    ''' <remarks>
    ''' 之所以用教师强制而不是自由生成，是因为它衡量的是"模型是否记住了调用片段的格式"，
    ''' 与采样策略无关，也不会因为一两个 token 走偏而整体判错。
    ''' </remarks>
    Public Function MeasureToolCallAccuracy(Optional sampleCount As Integer = 16) As Double
        Dim samples = _toolSynth.CreateSamples(sampleCount)
        Dim batch = _template.CreateBatch(samples, DemoConfig.ToolSequenceLength, System.Math.Min(4, sampleCount))

        _model.Parameters.ZeroGradients()

        Dim logits = _model.Forward(batch.TokenIds, batch.BatchSize, batch.SeqLen)
        Dim vocab = logits.Shape(1)

        Dim correct As Integer = 0
        Dim total As Integer = 0

        For n As Integer = 0 To batch.TotalTokens - 1
            If Not batch.LossMask(n) Then Continue For

            Dim best As Integer = 0
            Dim bestValue = Double.NegativeInfinity
            Dim offset = n * vocab

            For v As Integer = 0 To vocab - 1
                If logits.Data(offset + v) > bestValue Then
                    bestValue = logits.Data(offset + v)
                    best = v
                End If
            Next

            total += 1

            If best = batch.Targets(n) Then correct += 1
        Next

        _model.Parameters.ZeroGradients()

        If total = 0 Then Return 0.0

        Return correct / CDbl(total)
    End Function

#End Region

#Region "5. MoE 可视化"

    ''' <summary>展示专家负载分布与负载均衡偏置。</summary>
    Public Sub ShowMoERouting()
        ConsoleReport.Section("5. MoE 路由与负载均衡")

        Dim moe = _model.FirstMoELayer()

        If moe Is Nothing Then
            ConsoleReport.Note("本次配置没有启用 MoE。")
            Return
        End If

        ' 造一批 token 走一次前向，读取路由统计
        Dim batch = EmbeddedCorpus.TakeBatch(_pretrainTokens, 2, DemoConfig.PretrainSequenceLength, _cursor, _eosId)

        _model.Parameters.ZeroGradients()
        Call _model.Forward(batch.TokenIds, batch.BatchSize, batch.SeqLen)
        _model.Parameters.ZeroGradients()

        Dim info = moe.LastRouteInfo

        ConsoleReport.KeyValue("config", $"{moe.NumRoutedExperts} routed experts, top-{moe.TopK}, " &
                                          $"{moe.NumSharedExperts} shared expert(s)")
        ConsoleReport.KeyValue("tokens routed", info.Tokens)
        ConsoleReport.KeyValue("activated experts", $"{info.ActivatedExperts} / {moe.NumRoutedExperts}")
        ConsoleReport.KeyValue("max load ratio", $"{info.MaxLoadRatio:F3}x  (1.00 = 完全均匀)")
        ConsoleReport.Note("")
        ConsoleReport.Note("最近一个批次的专家命中分布（以理想均匀负载 1/N 为基准的占比）：")

        Dim labels = Enumerable.Range(0, moe.NumRoutedExperts).Select(Function(i) "expert " & i).ToArray()

        ConsoleReport.Note(ConsoleReport.Histogram(info.Load, labels, 36))

        ConsoleReport.Note("当前的选择偏置 b_i（无辅助损失负载均衡的全部状态）：")
        ConsoleReport.Note("  " & String.Join("  ", Enumerable.Range(0, moe.NumRoutedExperts).
                               Select(Function(i) $"b{i}={moe.BalanceBias(i):+0.00000;-0.00000}")))

        ConsoleReport.Note("")
        ConsoleReport.Note("累计负载（训练全过程）：")
        ConsoleReport.Note(ConsoleReport.Histogram(moe.LifetimeLoad(), labels, 36))
    End Sub

#End Region

#Region "6. 采样策略"

    ''' <summary>对比不同采样策略的生成差异。</summary>
    Public Sub DemoSampling(Optional prompt As String = "什么是 MoE")
        ConsoleReport.Section("6. 采样策略对比")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & prompt & ToolCallProtocol.AssistantMarker)

        ConsoleReport.KeyValue("prompt", prompt)

        Dim configs As New List(Of SamplingConfig) From {
            SamplingConfig.GreedySampling(),
            SamplingConfig.WithTemperature(0.7),
            SamplingConfig.WithTopK(20, 1.0),
            SamplingConfig.WithTopP(0.9, 1.0)
        }

        ConsoleReport.Note("")

        For Each config In configs
            Dim sampler As New Sampler(config)
            Dim generator As New TextGenerator(_model, sampler)

            Dim options As New GenerationOptions With {
                .MaxNewTokens = DemoConfig.MaxNewTokens,
                .UseCache = True,
                .StopTokenIds = New Integer() {_eosId}
            }

            Dim result = generator.Generate(promptIds, options)
            Dim text = _codec.Decode(result.GeneratedTokens)

            ConsoleReport.KeyValue(config.ToString(),
                                   "「" & text.Replace(vbLf, " ⏎ ") & "」")
            ConsoleReport.Note($"候选集={sampler.LastCandidateCount}, 分布熵={sampler.LastEntropy:F2} nats, " &
                               $"{result.AverageStepMilliseconds:F0} ms/step", 4)
        Next
    End Sub

#End Region

#Region "7. KV Cache 验证"

    ''' <summary>
    ''' 用同一 prompt 跑"有缓存"与"无缓存"两条路径，验证输出一致并对比耗时。
    ''' </summary>
    Public Sub VerifyKVCache(Optional prompt As String = "什么是注意力")
        ConsoleReport.Section("7. KV Cache 正确性验证与加速比")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & prompt & ToolCallProtocol.AssistantMarker)

        ConsoleReport.KeyValue("prompt", prompt)
        ConsoleReport.KeyValue("prompt tokens", promptIds.Length)
        ConsoleReport.KeyValue("max new tokens", DemoConfig.MaxNewTokens)

        ' 两条路径都用贪心，排除采样随机性的干扰
        Dim cachedResult = Generate(promptIds, useCache:=True)
        Dim plainResult = Generate(promptIds, useCache:=False)

        Dim identical As Boolean = cachedResult.GeneratedTokens.SequenceEqual(plainResult.GeneratedTokens)

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("有缓存输出", "「" & _codec.Decode(cachedResult.GeneratedTokens).Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("无缓存输出", "「" & _codec.Decode(plainResult.GeneratedTokens).Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("逐 token 一致性", If(identical, "通过 ✓（两条路径完全一致）", "失败 ✗（KV Cache 有 bug）"))

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("有缓存 单步均值", $"{cachedResult.AverageStepMilliseconds:F0} ms")
        ConsoleReport.KeyValue("无缓存 单步均值", $"{plainResult.AverageStepMilliseconds:F0} ms")

        Dim speedup = If(cachedResult.AverageStepMilliseconds > 0,
                         plainResult.AverageStepMilliseconds / cachedResult.AverageStepMilliseconds, 0.0)

        ConsoleReport.KeyValue("加速比", $"{speedup:F2}x")
        ConsoleReport.KeyValue("缓存占用", $"{ConsoleReport.Human(cachedResult.CacheBytes)}（随序列长度线性增长）")

        ConsoleReport.Note("")
        ConsoleReport.Note("为什么会有这个加速：无缓存时每生成一个 token 都要把整段前缀重新前向一次，")
        ConsoleReport.Note("注意力段是 O(t²)；有缓存时历史 K/V 直接读，注意力段降为 O(t)。")
        ConsoleReport.Note("序列越长，两者的差距越大 —— 上面这个比值还只是几十个 token 量级的结果。")
    End Sub

    Private Function Generate(promptIds As Integer(), useCache As Boolean) As GenerationResult
        Dim generator As New TextGenerator(_model, New Sampler(SamplingConfig.GreedySampling()))

        Dim options As New GenerationOptions With {
            .MaxNewTokens = DemoConfig.MaxNewTokens,
            .UseCache = useCache,
            .StopTokenIds = New Integer() {_eosId}
        }

        Return generator.Generate(promptIds, options)
    End Function

#End Region

#Region "8. Function Calling"

    ''' <summary>跑一轮完整的多轮工具调用闭环。</summary>
    Public Sub DemoAgent(Optional userMessage As String = "Beijing 的天气怎么样？",
                         Optional forcedTool As String = "get_weather")

        ConsoleReport.Section("8. Function Calling · 多轮 Agent Loop")

        ConsoleReport.KeyValue("user message", userMessage)
        ConsoleReport.KeyValue("forced tool (首轮)", If(forcedTool, "<模型自主决策>"))

        Dim systemPrompt = _registry.RenderCompactCatalog()

        ConsoleReport.Note("")
        ConsoleReport.Note("注入 prompt 的工具清单（readme 里"把 Schema 变成 token"的第一步）：")
        ConsoleReport.Note(systemPrompt, 4)

        Dim loop As New AgentLoop(_model, _codec, _registry)

        Dim options As New AgentLoopOptions With {
            .MaxToolRounds = 3,
            .MaxTokensPerReply = 12,
            .MaxArgumentTokens = 64,
            .UseCache = True,
            .Sampling = New SamplingConfig With {.Greedy = True},
            .ForcedToolName = forcedTool,
            .Verbose = False
        }

        Dim watch = Diagnostics.Stopwatch.StartNew()
        Dim result = loop.Run(systemPrompt, userMessage, options)
        watch.Stop()

        For Each round In result.Rounds
            ConsoleReport.Note("")
            ConsoleReport.Note($"---- 第 {round.Index} 轮 ----")

            If Not String.IsNullOrEmpty(round.AssistantText) Then
                ConsoleReport.KeyValue("模型自由生成", "「" & round.AssistantText.Replace(vbLf, " ⏎ ") & "」")
            End If

            If Not round.HasToolCall Then Continue For

            ConsoleReport.KeyValue("检测到工具信号", ToolCallProtocol.CallsBeginMarker)
            ConsoleReport.KeyValue("工具名（模型决策）", round.ToolName)

            If round.UsedConstrainedDecoding Then
                ConsoleReport.KeyValue("约束解码", "已启用 —— 参数结构必然符合 JSON Schema")

                For Each trace In round.ConstrainedTrace
                    ConsoleReport.Note("    " & trace, 4)
                Next

                ConsoleReport.KeyValue("参数 JSON", round.ArgumentsJson)
                ConsoleReport.KeyValue("解析后参数",
                                       String.Join(", ", round.Arguments.Select(Function(kv) kv.Key & "=" & kv.Value)))
            End If

            ConsoleReport.KeyValue("工具真实返回", round.ToolResult)
            ConsoleReport.KeyValue("回填方式", $"工具结果作为新 token 一次性 prefill 进 KV Cache " &
                                               $"（复用前缀 {round.ReusedPrefixTokens} tokens）")
        Next

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("最终回复", "「" & If(result.FinalText, "").Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("工具调用次数", result.ToolCallCount)
        ConsoleReport.KeyValue("上下文 token 数", result.TotalContextTokens)
        ConsoleReport.KeyValue("KV Cache 占用", ConsoleReport.Human(result.CacheBytes))
        ConsoleReport.KeyValue("总耗时", $"{watch.Elapsed.TotalMilliseconds:F0} ms")
    End Sub

    ''' <summary>演示约束解码的"物理上无法生成非法值"这一性质。</summary>
    Public Sub DemoConstrainedDecoding(Optional toolName As String = "get_weather")
        ConsoleReport.Section("9. 约束解码：让非法参数在物理上不可达")

        Dim tool = _registry.Find(toolName)

        If tool Is Nothing Then
            ConsoleReport.Note($"找不到工具 {toolName}")
            Return
        End If

        ConsoleReport.KeyValue("工具", tool.Name)
        ConsoleReport.KeyValue("参数 schema", String.Join(" ; ",
            tool.Schema.Properties.Select(Function(p)
                If p.EnumValues IsNot Nothing AndAlso p.EnumValues.Length > 0 Then
                    Return $"{p.Name} ∈ {{{String.Join(" | ", p.EnumValues)}}}"
                End If
                Return $"{p.Name}: {p.Type}"
            End Function)))

        Dim decoder As New ConstrainedDecoder(tool.Schema, _codec.Vocabulary)

        ConsoleReport.Note("")
        ConsoleReport.Note("逐步观察状态机如何收紧候选集：")

        Dim head = ToolCallProtocol.FormatCallHeader(tool.Name)
        Dim stream = TokenStream.Create(_model, _codec.Encode(head), useCache:=False)
        Dim sampler As New Sampler(SamplingConfig.GreedySampling())

        Dim tokens = decoder.Generate(stream, sampler, 64)

        For Each trace In decoder.Trace
            ConsoleReport.Note("  " & trace)
        Next

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("生成结果", decoder.GeneratedText)
        ConsoleReport.KeyValue("是否闭合", If(decoder.IsComplete, "是", "否（达到长度上限）"))

        ' 独立验证：把生成的 JSON 交给一个"忠于 schema"的校验器
        Dim failures As New List(Of String)

        If Not decoder.IsComplete Then
            Call failures.Add("对象未闭合")
        Else
            Dim parsed = ToolCallProtocol.ParseArgumentObject(decoder.GeneratedText)

            For Each p In tool.Schema.Properties
                If p.Required AndAlso Not parsed.ContainsKey(p.Name) Then
                    Call failures.Add($"缺少必填参数 {p.Name}")
                End If

                If parsed.ContainsKey(p.Name) AndAlso p.EnumValues IsNot Nothing AndAlso p.EnumValues.Length > 0 Then
                    If Array.IndexOf(p.EnumValues, parsed(p.Name)) < 0 Then
                        Call failures.Add($"{p.Name} 的取值不在枚举内")
                    End If
                End If
            Next
        End If

        ConsoleReport.Note("")
        ConsoleReport.Note(If(failures.Count = 0,
            "Schema 校验：通过 ✓ —— 整个生成过程没有一步允许非法 token 进入采样，因此这是必然结果。",
            "Schema 校验：失败 ✗ —— " & String.Join("; ", failures)))
    End Sub

#End Region

#Region "10. 持久化"

    ''' <summary>保存权重并在重新加载后复现同一段生成，验证持久化正确。</summary>
    Public Sub VerifyPersistence(path As String)
        ConsoleReport.Section("10. 权重持久化")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & "什么是 KV Cache" &
                                      ToolCallProtocol.AssistantMarker)

        Dim before = Generate(promptIds, useCache:=True).GeneratedTokens.ToArray()

        _model.Save(path)
        ConsoleReport.KeyValue("已保存", $"{path}（{ConsoleReport.Human(IO.FileInfo(path).Length)}）")

        Dim restored = _model.Load(path)
        ConsoleReport.KeyValue("已恢复参数", $"{restored} / {_model.Parameters.Entries.Count}")

        Dim after = Generate(promptIds, useCache:=True).GeneratedTokens.ToArray()

        ConsoleReport.KeyValue("重载后输出一致", If(before.SequenceEqual(after), "通过 ✓", "失败 ✗"))
    End Sub

#End Region

#Region "工具方法"

    Private Shared Function Slice(samples As IList(Of TemplatedSample), start As Integer,
                                  count As Integer) As List(Of TemplatedSample)

        Dim result As New List(Of TemplatedSample)()

        For i As Integer = 0 To count - 1
            Call result.Add(samples((start + i) Mod samples.Count))
        Next

        Return result
    End Function

#End Region

End Class
