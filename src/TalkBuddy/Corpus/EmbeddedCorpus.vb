' ---------------------------------------------------------------------------
' EmbeddedCorpus —— 内置预训练语料
'
' 预训练阶段的任务是"下一个 token 预测"，只需要纯文本，不需要标注。
' 语料直接写在代码里，保证整个 demo 离线自包含、任何机器上跑出来的结果一致。
'
' 语料内容刻意围绕本 demo 自身涉及的概念（语言模型、注意力、MoE、KV Cache、
' 工具调用…）展开：这样即使模型很小，续写出来的东西也还能读出一点"主题感"，
' 比用完全无关的随机文本更容易观察 loss 的下降与生成的合理性。
'
' 需要强调的预期管理：几十步训练不可能让模型"学会语言"。这里的验收口径是
'   * loss / 困惑度单调下降；
'   * 模型能复现语料中的高频词与句式；
' 而不是"能写一篇通顺的文章"。
' ---------------------------------------------------------------------------

Imports System.Collections.Generic
Imports System.Text
Imports Microsoft.VisualBasic.MachineLearning.LLM

Public Class EmbeddedCorpus

    ''' <summary>内置语料正文。</summary>
    Public ReadOnly Property Text As String

    Public Sub New()
        Text = RawText
    End Sub

    ''' <summary>
    ''' 把语料编码成 token 序列，并按固定窗口切片。
    ''' </summary>
    ''' <param name="codec">分词器适配器</param>
    ''' <param name="sequenceLength">窗口长度（每行需要 seqLen + 1 个 token）</param>
    Public Function Tokenize(codec As DeepSeekTokenizerAdapter, sequenceLength As Integer) As Integer()
        Dim ids = codec.Encode(Text)

        ' 语料太短时循环拼接，保证样本数量足够
        If ids.Length < sequenceLength + 2 Then
            Dim repeated As New List(Of Integer)

            While repeated.Count < sequenceLength + 2
                Call repeated.AddRange(ids)
            End While

            Return repeated.ToArray()
        End If

        Return ids
    End Function

    ''' <summary>
    ''' 取一个预训练批次：从 <paramref name="cursor"/> 开始连续切出 batchSize 行。
    ''' </summary>
    ''' <param name="tokenIds">已编码的语料</param>
    ''' <param name="batchSize">批次大小</param>
    ''' <param name="sequenceLength">序列长度</param>
    ''' <param name="cursor">当前位置；会被推进</param>
    ''' <param name="eosId">行尾填充用的句尾标记 id</param>
    Public Shared Function TakeBatch(tokenIds As Integer(), batchSize As Integer, sequenceLength As Integer,
                                     ByRef cursor As Integer, eosId As Integer) As LMBatch

        Dim rows(batchSize - 1)() As Integer
        Dim masks(batchSize - 1)() As Boolean
        Dim need = sequenceLength + 1

        For b As Integer = 0 To batchSize - 1
            If cursor + need > tokenIds.Length Then cursor = 0

            Dim row(need - 1) As Integer
            Dim rowMask(need - 1) As Boolean

            Call Array.Copy(tokenIds, cursor, row, 0, need)

            For i As Integer = 0 To need - 1
                rowMask(i) = True
            Next

            rows(b) = row
            masks(b) = rowMask

            cursor += sequenceLength
        Next

        Return LMBatch.Create(rows, masks, eosId)
    End Function

    ''' <summary>
    ''' 内置语料正文。
    ''' </summary>
    ''' <remarks>
    ''' 用 <c>StringBuilder</c> 拼装而不是写成一个巨大的字符串常量，
    ''' 是为了避免源码里出现超长行。
    ''' </remarks>
    Private ReadOnly Property RawText As String
        Get
            Dim text As New StringBuilder()

            Call text.AppendLine("大语言模型的本质，是一个基于 Transformer 解码器架构、通过海量文本进行下一个词预测训练出来的自回归概率模型。")
            Call text.AppendLine("它把语言建模为条件概率链：一段文本的概率等于每个位置在给定前文条件下的条件概率之积。")
            Call text.AppendLine("训练目标因此变得极其简单，就是让模型在每一个位置都更准确地预测下一个 token。")
            Call text.AppendLine("注意力机制是语言模型的灵魂。对于输入矩阵，每个注意力头先做三个可学习的线性投影，得到查询、键和值。")
            Call text.AppendLine("查询表示我在找什么，键表示我能提供什么匹配，值表示我实际贡献什么信息。")
            Call text.AppendLine("缩放点积注意力先把查询与键做点积得到打分矩阵，再除以维度的平方根，然后做 softmax 得到权重，最后对值加权求和。")
            Call text.AppendLine("除以平方根是为了防止高维点积数值过大导致 softmax 进入饱和区，从而让梯度消失。")
            Call text.AppendLine("在解码器里还必须加因果掩码，把未来位置设为负无穷，保证每个位置只能看到自己和之前的位置。")
            Call text.AppendLine("多头机制把表示空间切分成若干子空间，让模型同时在不同子空间捕捉不同类型的依赖关系。")
            Call text.AppendLine("注意力让任意两个位置之间的信息传递路径长度为一，彻底解决了循环网络的长程依赖衰减问题。")
            Call text.AppendLine("位置编码用来注入词序信息。旋转位置编码直接对查询和键做旋转，使点积只依赖相对距离。")
            Call text.AppendLine("旋转位置编码与增量解码天然契合，因为只需要按绝对位置旋转当前的查询和键。")
            Call text.AppendLine("归一化方面，现代模型普遍采用均方根归一化，并把归一化放在子层之前，也就是所谓的前置归一化。")
            Call text.AppendLine("前馈网络负责对每个位置的表示做深度非线性变换，一般认为它存储了大量事实知识。")
            Call text.AppendLine("现代模型多用门控激活，例如 SwiGLU，用乘法交互代替硬门限，表达能力更强。")
            Call text.AppendLine("混合专家把前馈网络替换为若干个并行专家加一个路由器，每个 token 只被路由到少数几个专家。")
            Call text.AppendLine("这样做的收益是总参数量可以做得很大，而每个 token 的实际计算量只激活其中一小部分。")
            Call text.AppendLine("细粒度专家分割把大专家切成更多小专家，同时提高激活数量，从而让专家组合数指数增长。")
            Call text.AppendLine("共享专家隔离则划出若干无条件激活的专家，专门承载跨语境的通用知识，让路由专家专注特化。")
            Call text.AppendLine("路由器的负载均衡是关键难点，专家太粗或负载不均都会造成计算热点甚至专家塌缩。")
            Call text.AppendLine("无辅助损失的负载均衡为每个专家维护一个动态偏置，只作用在选择的打分上，不影响梯度方向。")
            Call text.AppendLine("推理阶段的关键优化是键值缓存，把已经生成过的键和值缓存起来复用，避免每一步都重新计算全部历史。")
            Call text.AppendLine("有了键值缓存，单步解码的注意力开销从序列长度的平方降到线性，代价是缓存占用随序列线性增长。")
            Call text.AppendLine("采样策略共同决定生成风格，温度让分布变平或变尖，Top-k 截断长尾，Top-p 按累计概率自适应截断。")
            Call text.AppendLine("工具调用是另一个层面的能力，模型本身只是一个文本生成器，它并不会真正调用任何函数。")
            Call text.AppendLine("所谓函数调用，本质上是结构化提示工程、生成特定格式的标记序列、以及外部代码执行解析三件事的协作。")
            Call text.AppendLine("这些特殊标记并不仅仅是魔法，而是被加进词表的保留标记，模型经过训练学会在需要时输出它们。")
            Call text.AppendLine("约束解码把参数的语法编译成有限状态机，每一步只允许结构合法的标记参与采样。")
            Call text.AppendLine("于是参数结构在物理上不可能非法，不过语义是否合理仍然取决于模型本身的能力。")
            Call text.AppendLine("训练通常分三个阶段。预训练获得语言能力，监督微调教会模型听懂指令，偏好对齐让回答更优秀也更无害。")
            Call text.AppendLine("微调阶段只对助手自己产出的标记计算损失，用户消息与工具返回结果都不计入损失。")
            Call text.AppendLine("A language model learns to predict the next token from all the tokens before it.")
            Call text.AppendLine("Attention lets any two positions exchange information in a single step.")
            Call text.AppendLine("A mixture of experts keeps the knowledge capacity large while the compute per token stays small.")
            Call text.AppendLine("The key value cache turns quadratic decoding into linear decoding.")
            Call text.AppendLine("Tool calling is a contract written in tokens between the model and the framework.")

            Return text.ToString()
        End Get
    End Property

End Class
