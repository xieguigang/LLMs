<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class FormLLMUI
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        WebView2llmui1 = New WebView2LLMUI()
        SuspendLayout()
        ' 
        ' WebView2llmui1
        ' 
        WebView2llmui1.Dock = DockStyle.Fill
        WebView2llmui1.Location = New Point(0, 0)
        WebView2llmui1.Name = "WebView2llmui1"
        WebView2llmui1.Size = New Size(800, 450)
        WebView2llmui1.TabIndex = 0
        ' 
        ' FormLLMUI
        ' 
        AutoScaleDimensions = New SizeF(7F, 15F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(800, 450)
        Controls.Add(WebView2llmui1)
        Name = "FormLLMUI"
        Text = "WebView2 LLM UI"
        ResumeLayout(False)
    End Sub

    Friend WithEvents WebView2llmui1 As WebView2LLMUI

End Class
