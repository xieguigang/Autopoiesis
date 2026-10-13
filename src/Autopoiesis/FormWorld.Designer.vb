<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class FormWorld
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
    '
    'NOTE: 参数实验台与状态栏的控件树全部由 FormWorld.BuildLayout() 以代码方式构建。
    '      控件数量多、层次深（表格布局 + 卡片 + 流式行），用设计器生成器维护
    '      InitializeComponent 会非常脆弱；代码化还能顺手把主题、字体与事件接线
    '      集中在一处，改起来更安全。
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New System.ComponentModel.Container()
        SuspendLayout()
        '
        ' FormWorld
        '
        AutoScaleMode = AutoScaleMode.Font
        BackColor = System.Drawing.Color.FromArgb(11, 14, 20)
        ClientSize = New System.Drawing.Size(1440, 920)
        MinimumSize = New System.Drawing.Size(960, 640)
        KeyPreview = True
        StartPosition = FormStartPosition.CenterScreen
        Text = "AUTOPOIESIS · Particle Life"
        ResumeLayout(False)
    End Sub

End Class
