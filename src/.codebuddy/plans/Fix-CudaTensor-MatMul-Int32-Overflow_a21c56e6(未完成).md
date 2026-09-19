---
name: Fix-CudaTensor-MatMul-Int32-Overflow
overview: 修复 CUDA 后端 MatMul 用 Int32 计算 m*k*n 规模导致的 OverflowException；把同类 Int32 规模计算统一改为 Long，并在显存缓冲 Int32 计数边界处给出可读异常与 CPU 回退，最后实跑验证 CUDA 路径可用性与加速比。
todos:
  - id: fix-cuda-overflow
    content: 修复 CudaTensor 的 Int32 规模溢出：MatMul/Transpose/ElementCount 改用 Long，并用 TryKernel 补上 GEMM 内核不可用时的 CPU 回退
    status: pending
  - id: add-gpu-stats
    content: 新增 GpuOpStats 计数器，在 EwBinary/EwUnary/RowReduce/RowSoftmax/ReduceGlobal/MatMul/Transpose/SpMM 埋点统计 GPU 执行与 CPU 回退次数
    status: pending
    dependencies:
      - fix-cuda-overflow
  - id: expose-stats-in-demo
    content: 在 Pipeline 与 ConsoleReport 中输出 CUDA 启用状态、各算子 GPU/回退统计及 CPU 与 CUDA 单步耗时对比
    status: pending
    dependencies:
      - add-gpu-stats
  - id: verify-full-run
    content: 用 [skill:lsp-code-analysis] 核对改动引用完整性后，不带 --no-cuda 完整实跑，确认无异常、既有检查全通过并记录真实加速比
    status: pending
    dependencies:
      - expose-stats-in-demo
---

## 需求概述

直接运行 `TalkBuddy\test\test.vbproj`（即**不携带 `--no-cuda` 参数**，走 CUDA 后端）时，程序在第三阶段 Function Calling SFT 崩溃：

```
System.OverflowException: Arithmetic operation resulted in an overflow.
  at ILCuda.GPUTensor.CudaTensor.MatMul(Tensor a, Tensor b)      CudaTensor.vb:line 561
  at LLM.LLMModel.LinearTransposed(Tensor x)                     LLMModel.vb:line 371
  at LLM.LLMModel.Forward(Int32[] ids, Int32 batchSize, Int32 seqLen)  LLMModel.vb:line 257
  at LLM.LMTrainer.TrainStep(LMBatch batch)                      LMTrainer.vb:line 258
  at TalkBuddy.DemoPipeline.RunToolSft(Int32 steps)              Pipeline.vb:line 291
  at test.Program.Main(String[] args)                            Program.vb:line 60
```

带 `--no-cuda` 时（走 SIMD CPU 后端）整条链路可正常跑完，说明这是 **CUDA 后端的缺陷**，而非模型或训练逻辑的问题。

## 核心诉求

1. 修复该溢出崩溃，使 demo 在**启用 CUDA 的默认路径**下也能完整跑通。
2. 修复后既有验收项不得回退：三阶段 loss 正常下降、KV Cache 逐 token 一致性与加速比、约束解码 Schema 校验、权重重载一致性等检查全部保持通过。
3. 一并清除同类隐患（同一文件内其余 Int32 规模计算），避免修完一个又冒出下一个。
4. 给出 CPU 与 CUDA 的真实耗时对比，用实测数据说明加速比；若加速比不理想，需如实说明原因。

## 根因分析（已逐行读代码确认）

崩溃点 `CudaTensor.vb:561`：

```
Dim m = a.Shape(0)
Dim k = a.Shape(1)
Dim n = b.Shape(1)

If m * k * n &lt; MinGemmElements Then Return MyBase.MatMul(a, b)   ' 溢出点
```

`m * k * n` 以 **Int32** 运算。该 `MatMul` 来自 LM head：`[rows, 128] × [128, 128815]`（`rows = BatchSize × SequenceLength`，VocabSize = DeepSeek 全量 128,815），故 `m*k*n = rows × 128 × 128815`：

| 阶段 | rows | m*k*n | 对比 Int32 上限 2,147,483,647 |
| --- | --- | --- | --- |
| 预训练 | 2 × 32 = 64 | 1,055,091,200 | 未超，通过 |
| 指令 SFT | 2 × 48 = 96 | 1,582,636,800 | 未超，通过 |
| 工具 SFT | 2 × 128 = 256 | **4,220,364,800** | **超出 → OverflowException** |


这与堆栈"只在 `RunToolSft` 崩溃"完全吻合。

**同一缺陷在本仓库已有先例**：`TensorFlow\Compute\SIMDTensor.vb` 第 211–222 行早已用 `Long` 修过完全相同的表达式，并留有注释说明"10 万级词表下 m*k*n 轻松超过 Int32 上限"。CUDA 后端漏改了同一处 —— 这是唯一直接原因。

## 关键约束（决定修法的事实）

1. **`DeviceBuffer` 的元素计数契约就是 Int32**
`ILCuda\Runtime\DeviceBuffer.vb`：`Public Sub New(count As Integer)`、`Public ReadOnly Property Count As Integer`。因此单算子输出元素数天然被 21.5 亿限制，超出应显式处置而非静默溢出。
2. **`CudaTensor.MatMul` 缺少内核可用性回退**
它直接调 `_engine.GetKernel(TensorKernelNames.GemmDouble)`；而同文件的 `Transpose`（第 629 行）先查 `DoubleKernelRegistry.Available(...)`，`Conv2D` 用 `TryKernel(...)`（第 673–679 行，内部 try/catch 返回 `Nothing`）。
因溢出在 `GetKernel` 之前就抛出，**该 GEMM 内核在当前环境下是否可用从未被验证过**。若只把 Int32 改成 Long，很可能只是把崩溃点从"溢出"挪到"内核加载失败"。
3. **版本号机制健全，无陈旧显存副本风险**
`Tensor.Version` / `MarkHostModified()` / 索引器 setter 均会自增（`Tensor.vb` 99–105、239–255、264–321 行），`DeviceCache.GetBuffer` 以"主机数组引用 + 版本号"为键，版本不符即重新上传。
4. **`DeviceCache`（LRU）容量超限只淘汰不抛异常**，不是崩溃来源。
5. **CUDA 后端是"逐算子主机暂存"设计**：每个算子都 `dOut.Read()` 把结果拷回主机，下一次调用再 `Device()` 重新上传。12.8 万词表下相关张量为 132–264 MB，预计每训练步 PCIe 往返约 2.5–3 GB。这是**性能风险而非正确性风险**，需实测确认。

## 修复策略

**不做"强制 CPU 回避"**。LM head 的 `N × d_model × vocab` 矩阵乘正是整个 demo 的算力大头，也正是 GPU 最该发挥作用之处；用"关掉 CUDA"来绕过 bug 既掩盖了缺陷，也丢掉了加速收益。

采取**定点修复 + 显式边界 + 可观测**三步：

1. **规模计算全部改用 `Long`**——`MatMul` 的 `m*k*n` 阈值判断、`m*n` 输出元素数、`Transpose` 的 `rows*cols`、`ElementCount` 的累乘。这是修复崩溃的充分条件，写法对齐已修好的 `SIMDTensor.MatMul`。
2. **在 Int32 契约边界上显式处置**——元素数超出 `Int32.MaxValue` 时给出可读诊断（而非让 `DeviceBuffer` 深处抛裸溢出异常）。以 `Tensor` 的 `Integer()` 形状表示能力衡量，该分支实际不可达，但把边界写明白胜过依赖巧合。
3. **补齐 GEMM 内核回退**——`MatMul` 改用既有的 `TryKernel(TensorKernelNames.GemmDouble)`，取不到就 `Return MyBase.MatMul(a, b)`，与 `Conv2D`/`Transpose` 策略一致。这样"内核不可用"与"规模太小"统一收敛为 CPU 兜底，符合该类"算子永不失效"的既有设计。
4. **增加 GPU 执行/回退计数**——在 `CudaTensor` 的既有汇聚点（`EwBinary`、`EwUnary`、`RowReduce`、`RowSoftmax`、`ReduceGlobal`、`MatMul`、`Transpose`、`SpMM`）统一埋点，暴露"GPU 算子调用数 / CPU 回退数 / 回退原因"。这既是验收证据，也是后续判断"哪些算子其实没走上 GPU"的依据。

## 性能与可靠性考量

- 修复本身是 O(1) 的算术与分支调整，**不引入任何额外张量运算或内存分配**，对热点路径零开销。
- 计数埋点用 `Interlocked` 自增，相对算子本身的量级可完全忽略。
- 数值一致性：修复不改变任何数学语义，仅改变"是否走 GPU"的判定与 `Long` 溢出行为，因此 CPU/CUDA 结果应保持数值一致（双精度）。
- 若实测 CUDA 反而更慢，根因必在"逐算子主机暂存"的 PCIe 往返（见关键约束 5），届时如实报告并给出结论，不在本次强行做设备常驻重构（那属于架构级改动，超出"修 bug"范围）。

## 改动边界

- 仅改基础库 `cuda\ILCudaTensor\GPUTensor\` 下文件，**不触碰任何 `.cu` 内核源码**，不改 `ILCuda` 核心。
- 新增计数统计为**新增公开只读成员**，向后兼容，不影响 `ILCudaTensor\test` 既有调用方。
- 演示层只增加"读统计并打印"，不改训练与推理逻辑。

## 架构与目录结构

无需引入新架构，属靶向补丁。涉及文件：

```
cuda/ILCudaTensor/
└── GPUTensor/
    ├── CudaTensor.vb          # [MODIFY] 主修复文件
    └── GpuOpStats.vb          # [NEW] GPU 算子执行/回退计数器

TalkBuddy/
├── Pipeline.vb                # [MODIFY] 暴露 CUDA 注册状态与统计读取
├── ConsoleReport.vb           # [MODIFY] 新增"GPU 后端执行统计"打印段
└── test/Program.vb            # [MODIFY] 在验收输出中呈现 CPU/CUDA 对比
```

### `CudaTensor.vb` 的具体修改点

- `MatMul`（约 549–573 行，**崩溃点**）
- `Dim totalOps As Long = CLng(m) * k * n`，阈值判断改用 `totalOps`（对齐 `SIMDTensor.MatMul` 第 211–222 行的写法与注释口径）。
- 输出元素数改用 `Long` 计算并做 Int32 越界检查；越界给出可读诊断。
- `_engine.GetKernel(TensorKernelNames.GemmDouble)` 改为 `TryKernel(TensorKernelNames.GemmDouble)`，为 `Nothing` 时记一次回退并 `Return MyBase.MatMul(a, b)`。
- `Transpose`（约 624–646 行）
- `rows * cols` 改用 `Long` 计算并做同样的越界检查。
- `ElementCount`（约 659–667 行）
- 累乘改用 `Long` 累加器，再收敛到 `DeviceBuffer` 所需的 Int32，越界给出可读诊断。
- 汇聚点埋点：`EwBinary`、`EwUnary`、`RowReduce`、`RowSoftmax`、`ReduceGlobal`、`MatMul`、`Transpose`、`SpMM` 各记一次"走 GPU / 回退 CPU"。

### `GpuOpStats.vb`（新增）

一个轻量静态计数器类型：按算子名记录 GPU 执行次数与 CPU 回退次数，并提供只读快照与格式化输出；用 `Interlocked` 保证线程安全（与 `DoubleKernelRegistry` 的既有风格一致）。

## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 定位 `CudaTensor.vb` 中全部 Int32 规模计算点及其调用方，并核对 `ILCudaTensor\test` 既有调用方是否会受 `MatMul` 行为变化影响；同时确认演示层（`Pipeline.vb` / `ConsoleReport.vb` / `Program.vb`）打印 CUDA 状态与耗时的现有接入点。
- Expected outcome: 产出精确的行号与符号清单，确保同类隐患一次改净、无遗漏、不误伤既有调用方。

### Skill

- **lsp-code-analysis**
- Purpose: 以语义方式核对 `CudaTensor.MatMul` / `Transpose` / `ElementCount` 的全部引用点，确认改用 `TryKernel` 与 Long 规模计算后没有破坏任何调用契约；并确认新增计数器成员的访问可见性正确。
- Expected outcome: 得到完整引用清单与无编译破坏的结论，保证跨后端行为一致。