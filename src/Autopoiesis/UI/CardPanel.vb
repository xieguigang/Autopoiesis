Imports System.ComponentModel
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Namespace UI

    ''' <summary>
    ''' 参数实验台里的分组卡片：圆角面板 + 顶部标题 + 一条竖向强调色。
    ''' </summary>
    ''' <remarks>
    ''' 子项一律加进 <see cref="Body"/>（一个纵向 <see cref="FlowLayoutPanel"/>），
    ''' 因此卡片高度由调用方按内容行数直接指定，不依赖 AutoSize 的连锁计算。
    ''' </remarks>
    Public Class CardPanel
        Inherits Panel

        Private Const CaptionHeight As Integer = 30
        Private Const CornerRadius As Integer = 10

        Private ReadOnly m_caption As String
        Private ReadOnly m_accent As Color
        Private ReadOnly m_hint As String
        Private ReadOnly m_body As New FlowLayoutPanel()

        Private Shared ReadOnly s_captionFont As New Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        Private Shared ReadOnly s_hintFont As New Font("Microsoft YaHei UI", 8.0F)

        Public Sub New(caption As String, accent As Color, Optional hint As String = Nothing)
            m_caption = caption
            m_accent = accent
            m_hint = hint

            BackColor = Theme.CardBackground
            Padding = New Padding(12, CaptionHeight, 12, 10)

            Body.FlowDirection = FlowDirection.TopDown
            Body.WrapContents = False
            Body.AutoScroll = False
            Body.Dock = DockStyle.Fill
            Body.BackColor = Theme.CardBackground
            Body.Margin = New Padding(0)

            Controls.Add(Body)

            SetStyle(ControlStyles.AllPaintingInWmPaint Or
                     ControlStyles.UserPaint Or
                     ControlStyles.OptimizedDoubleBuffer Or
                     ControlStyles.ResizeRedraw, True)
        End Sub

        ''' <summary>卡片主体的纵向流式容器</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public ReadOnly Property Body As FlowLayoutPanel
            Get
                Return m_body
            End Get
        End Property

        ''' <summary>按内容行数估算卡片高度：标题 + 若干行 + 内边距</summary>
        Public Shared Function SuggestHeight(contentHeight As Integer) As Integer
            Return CaptionHeight + contentHeight + 12
        End Function

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)

            Dim g As Graphics = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias

            Dim rect As New Rectangle(0, 0, System.Math.Max(1, Width - 1), System.Math.Max(1, Height - 1))

            Using path As GraphicsPath = RoundedPath(rect, CornerRadius)
                Using brush As New SolidBrush(Theme.CardBackground)
                    g.FillPath(brush, path)
                End Using

                Using pen As New Pen(Theme.CardBorder)
                    g.DrawPath(pen, path)
                End Using
            End Using

            ' 左侧强调竖条 + 标题
            Using brush As New SolidBrush(m_accent)
                g.FillRectangle(brush, 12, 11, 3, 13)
            End Using

            Using brush As New SolidBrush(Theme.Text)
                g.DrawString(m_caption, s_captionFont, brush, 21.0F, 9.0F)
            End Using

            If Not String.IsNullOrEmpty(m_hint) Then
                Dim size As SizeF = g.MeasureString(m_hint, s_hintFont)
                Using brush As New SolidBrush(Theme.Hint)
                    g.DrawString(m_hint, s_hintFont, brush,
                                 System.Math.Max(21.0F, Width - size.Width - 14.0F), 12.0F)
                End Using
            End If
        End Sub

        Private Shared Function RoundedPath(rect As Rectangle, radius As Integer) As GraphicsPath
            Dim path As New GraphicsPath()
            Dim diameter As Integer = System.Math.Max(1,
                System.Math.Min(radius * 2, System.Math.Min(rect.Width, rect.Height)))

            If diameter <= 2 Then
                path.AddRectangle(rect)
                Return path
            End If

            Dim arc As New Rectangle(rect.X, rect.Y, diameter, diameter)

            path.AddArc(arc, 180, 90)
            arc.X = rect.Right - diameter
            path.AddArc(arc, 270, 90)
            arc.Y = rect.Bottom - diameter
            path.AddArc(arc, 0, 90)
            arc.X = rect.Left
            path.AddArc(arc, 90, 90)
            path.CloseFigure()

            Return path
        End Function

    End Class

End Namespace
