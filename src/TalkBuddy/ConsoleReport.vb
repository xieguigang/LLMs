' ---------------------------------------------------------------------------
' ConsoleReport —— 控制台输出的小工具集
'
' demo 的"可观测性"全部落在这里：把 loss、学习率、梯度范数、专家负载、
' 缓存占用这些数字排版成能一眼看趋势的形式。算法层不做任何打印，
' 保证它作为库被复用时不会污染调用方的输出。
' ---------------------------------------------------------------------------

Imports System.Text

''' <summary>控制台排版辅助。</summary>
Public Module ConsoleReport

    ''' <summary>
    ''' 把控制台切到 UTF-8：控制台里会打印大量中文、特殊标记符号（<c>&lt;｜…｜&gt;</c>）
    ''' 与方框字符，不切编码会全是问号。
    ''' </summary>
    Public Sub EnableUtf8()
        Try
            Console.OutputEncoding = System.Text.Encoding.UTF8
        Catch
            ' 某些重定向场景下不允许改编码，忽略即可
        End Try
    End Sub

    ''' <summary>打印一级/二级标题。</summary>
    Public Sub Section(title As String, Optional level As Integer = 1)
        Dim line As New String("="c, 78)

        Call Console.WriteLine()

        If level <= 1 Then
            Call Console.WriteLine(line)
            Call Console.WriteLine("  " & title)
            Call Console.WriteLine(line)
        Else
            Call Console.WriteLine("-- " & title & " " & New String("-"c, System.Math.Max(0, 74 - title.Length)))
        End If
    End Sub

    ''' <summary>打印一条 <c>键 : 值</c>。</summary>
    Public Sub KeyValue(key As String, value As Object, Optional indent As Integer = 2)
        Call Console.WriteLine(New String(" "c, indent) & key.PadRight(30) & ": " & value)
    End Sub

    ''' <summary>打印一条缩进的说明。</summary>
    Public Sub Note(text As String, Optional indent As Integer = 2)
        Call Console.WriteLine(New String(" "c, indent) & text)
    End Sub

    ''' <summary>把字节数渲染成人类可读形式。</summary>
    Public Function Human(byteCount As Long) As String
        If byteCount < 1024 Then Return $"{byteCount} B"
        If byteCount < 1024L * 1024L Then Return $"{byteCount / 1024.0:F1} KB"
        If byteCount < 1024L * 1024L * 1024L Then Return $"{byteCount / 1024.0 / 1024.0:F2} MB"

        Return $"{byteCount / 1024.0 / 1024.0 / 1024.0:F2} GB"
    End Function

    ''' <summary>把占比数组渲染成横向直方图。</summary>
    ''' <param name="values">每个柱子的数值</param>
    ''' <param name="labels">柱子标签</param>
    ''' <param name="width">单个柱子的最大宽度（字符数）</param>
    Public Function Histogram(values As Double(), labels As String(),
                              Optional width As Integer = 40) As String

        Dim sb As New StringBuilder()
        Dim max As Double = 0.0

        For Each v In values
            If v > max Then max = v
        Next

        If max <= 0 Then max = 1.0

        Dim total As Double = 0.0

        For Each v In values
            total += v
        Next

        For i As Integer = 0 To values.Length - 1
            Dim filled = CInt(System.Math.Round(values(i) / max * width))
            Dim label = If(labels IsNot Nothing AndAlso i < labels.Length, labels(i), i.ToString())
            Dim share = If(total > 0, values(i) / total, 0.0)

            Call sb.AppendLine($"  {label,-10}|{New String("#"c, filled).PadRight(width)}| {share:P1}")
        Next

        Return sb.ToString()
    End Function

    ''' <summary>把一组 loss 渲染成简易曲线（用字符高低表示相对大小）。</summary>
    Public Function LossCurve(losses As IEnumerable(Of Double), Optional height As Integer = 8) As String
        Dim values = losses.ToArray()

        If values.Length = 0 Then Return "  (no data)"

        Dim min = values.Min()
        Dim max = values.Max()

        If max - min < 1.0E-09 Then max = min + 1.0

        Dim rows(height - 1) As Char()

        For r As Integer = 0 To height - 1
            rows(r) = New Char(values.Length - 1) {}
        Next

        For i As Integer = 0 To values.Length - 1
            Dim level = CInt(System.Math.Round((values(i) - min) / (max - min) * (height - 1)))
            level = height - 1 - System.Math.Max(0, System.Math.Min(height - 1, level))

            For r As Integer = 0 To height - 1
                rows(r)(i) = If(r = level, "*"c, If(r > level, " "c, " "c))
            Next
        Next

        Dim sb As New StringBuilder()

        For r As Integer = 0 To height - 1
            Call sb.AppendLine("  " & New String(rows(r)))
        Next

        Call sb.AppendLine($"  min={min:F4}  max={max:F4}  last={values(values.Length - 1):F4}")

        Return sb.ToString()
    End Function

End Module
