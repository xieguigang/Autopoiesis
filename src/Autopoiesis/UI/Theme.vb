Imports System.Drawing

Namespace UI

    ''' <summary>
    ''' 全局亮色主题调色板，参考 <c>G:\Moira\win32_desktop\Pages\PanelPlayerLeft.Designer.vb</c>
    ''' 的浅色 UI 风格（白底、深石板灰文字、低饱和提示、蓝色主操作按钮、青色强调）。
    ''' </summary>
    Public NotInheritable Class Theme

        Private Sub New()
        End Sub

        ' ---- 表面 ----
        Public Shared ReadOnly WindowBackground As Color = Color.White
        Public Shared ReadOnly PanelBackground As Color = Color.FromArgb(248, 250, 252)
        Public Shared ReadOnly CardBackground As Color = Color.White
        Public Shared ReadOnly CardBorder As Color = Color.FromArgb(226, 232, 240)
        Public Shared ReadOnly TrackBackground As Color = Color.FromArgb(241, 245, 249)
        Public Shared ReadOnly ControlBackground As Color = Color.FromArgb(248, 250, 252)
        Public Shared ReadOnly ControlBorder As Color = Color.FromArgb(203, 213, 225)
        Public Shared ReadOnly HoverBackground As Color = Color.FromArgb(241, 245, 249)

        ' ---- 文本 ----
        Public Shared ReadOnly Text As Color = Color.FromArgb(30, 41, 59)
        Public Shared ReadOnly Muted As Color = Color.FromArgb(71, 85, 105)
        Public Shared ReadOnly Hint As Color = Color.FromArgb(100, 116, 139)

        ' ---- 强调色（与 PanelPlayerLeft 一致）----
        Public Shared ReadOnly Accent As Color = Color.FromArgb(14, 165, 233)   ' 青
        Public Shared ReadOnly Primary As Color = Color.FromArgb(37, 99, 235)   ' 蓝（主操作）
        Public Shared ReadOnly Success As Color = Color.FromArgb(16, 185, 129)  ' 绿
        Public Shared ReadOnly Warning As Color = Color.FromArgb(245, 158, 11)  ' 琥珀
        Public Shared ReadOnly Danger As Color = Color.FromArgb(239, 68, 68)    ' 红

        ' ---- 画布 ----
        Public Shared ReadOnly CanvasBackground As Color = Color.Black

    End Class

End Namespace
