Imports System.ComponentModel

''' <summary>
''' <see cref="WebView2LLMUI.ViewFileRequested"/> 事件的事件参数：用户在网页界面上点击了
''' 某个文件附件的文件名，请求查看该附件的内容。
''' </summary>
Public Class FileViewRequestEventArgs : Inherits CancelEventArgs

    ''' <summary>
    ''' 被请求查看的文件附件
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property File As FileReference

    Sub New(file As FileReference)
        Me.File = file
    End Sub

    ''' <summary>
    ''' 宿主应用程序是否已经自行处理了该文件查看请求。
    ''' 置为 True 时控件不再执行默认的打开行为（调用系统默认程序打开磁盘文件，
    ''' 或者将内存数据引用的文本内容推送到网页模态框中显示）。
    ''' </summary>
    ''' <returns></returns>
    Public Property Handled As Boolean
        Get
            Return Cancel
        End Get
        Set(value As Boolean)
            Cancel = value
        End Set
    End Property

End Class
