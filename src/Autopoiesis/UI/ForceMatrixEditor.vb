Imports System.ComponentModel
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Namespace UI

    ''' <summary>
    ''' 交互式交互矩阵（force matrix）编辑网格，复刻参考实现 <c>interface.gd</c> 的行为。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 网格是 <c>(T+1) × (T+1)</c>：首行与首列是类型颜色色块（点击弹出取色器），
    ''' 内部是力值格。力值的着色沿用原版规则 —— 正为绿、负为红，
    ''' 饱和度与明度都跟着 <c>|f|</c> 走（所以 0 会退化成接近背景色的暗格）。
    ''' </para>
    ''' <para>交互：滚轮在格上 ±0.1（夹到 [-1, 1]）、右键单击归零、左键单击 +0.1、点色块改颜色。</para>
    ''' <para>
    ''' 矩阵按 10×10 步长存储（<c>t_a + t_b * ForceMatrixStride</c>），与参考实现里的
    ''' <c>MAX_PARTICLE_TYPES</c> 一致：类型数滑杆只改变这里显示多少格，不改变底层步长。
    ''' 左上角空格标注 <c>j →</c> / <c>i ↓</c>，于是"非对称"（<c>A[i][j] ≠ A[j][i]</c>）
    ''' 在网格上一眼可辨。
    ''' </para>
    ''' </remarks>
    Public Class ForceMatrixEditor
        Inherits Control

        ''' <summary>滚轮每一格调整的力值</summary>
        Public Const WheelStep As Single = 0.1F

        Private Const CellGap As Integer = 3
        Private Const CornerRadius As Integer = 5
        Private Const MinCellSize As Integer = 14

        Private m_state As Physics.ParticleState
        Private m_types As Integer = 6
        Private m_cellSize As Integer = 28
        Private m_gridLeft As Integer
        Private m_gridTop As Integer
        Private m_hoverRow As Integer = -1
        Private m_hoverColumn As Integer = -1

        Private ReadOnly m_valueFont As New Font("Consolas", 8.5F, FontStyle.Regular)
        Private ReadOnly m_hintFont As New Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular)

        Public Sub New()
            SetStyle(ControlStyles.AllPaintingInWmPaint Or
                     ControlStyles.UserPaint Or
                     ControlStyles.OptimizedDoubleBuffer Or
                     ControlStyles.ResizeRedraw Or
                     ControlStyles.Selectable, True)

            BackColor = Color.FromArgb(18, 23, 34)
            ForeColor = Color.FromArgb(230, 237, 247)
        End Sub

        ''' <summary>力值发生变化（滚轮 / 左键 / 右键归零）</summary>
        Public Event ForceChanged As EventHandler

        ''' <summary>类型颜色发生变化</summary>
        Public Event ColorChanged As EventHandler

        ''' <summary>当前参与交互的类型数量</summary>
        <DefaultValue(6)>
        Public Property Types As Integer
            Get
                Return m_types
            End Get
            Set(value As Integer)
                Dim clamped As Integer = System.Math.Max(1,
                    System.Math.Min(Physics.SimulationParameters.MaxParticleTypes, value))

                If clamped = m_types Then Return

                m_types = clamped
                Relayout()
                Invalidate()
            End Set
        End Property

        ''' <summary>关联的粒子状态（提供力矩阵与类型颜色）</summary>
        Public Sub Bind(state As Physics.ParticleState)
            m_state = state
            Invalidate()
        End Sub

        ''' <summary>把整个交互矩阵归零</summary>
        Public Sub ClearAll()
            If m_state Is Nothing Then Return

            m_state.ClearForceMatrix()
            Invalidate()
            RaiseEvent ForceChanged(Me, EventArgs.Empty)
        End Sub

        ''' <summary>悬停格的语义说明，供状态栏显示</summary>
        Public ReadOnly Property HoverText As String
            Get
                If m_state Is Nothing OrElse Not IsInnerCell(m_hoverRow, m_hoverColumn) Then Return String.Empty

                Dim typeA As Integer = m_hoverRow - 1
                Dim typeB As Integer = m_hoverColumn - 1
                Dim value As Single = m_state.GetForce(typeA, typeB)

                Dim verb As String = If(value > 0.0F, "吸引", If(value < 0.0F, "排斥", "中性"))

                Return $"A[{typeA + 1}][{typeB + 1}] = {value:F2}（类型 {typeA + 1} 被类型 {typeB + 1} {verb}）"
            End Get
        End Property

        ''' <summary>重新计算单元格尺寸与网格原点</summary>
        Public Sub Relayout()
            Dim slots As Integer = m_types + 1
            Dim size As Integer = System.Math.Min(Width, Height) \ slots

            If size < MinCellSize Then size = MinCellSize

            m_cellSize = size
            m_gridLeft = System.Math.Max(0, (Width - size * slots) \ 2)
            m_gridTop = System.Math.Max(0, (Height - size * slots) \ 2)
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            Relayout()
        End Sub

        Protected Overrides Sub OnMouseEnter(e As EventArgs)
            MyBase.OnMouseEnter(e)

            ' 滚轮消息只会送到拥有焦点的控件上
            If Not Focused Then Focus()
        End Sub

        ' ------------------------------------------------------------------
        ' 绘制
        ' ------------------------------------------------------------------

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)

            Dim g As Graphics = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.Clear(BackColor)

            Dim slots As Integer = m_types + 1

            For row As Integer = 0 To slots - 1
                For column As Integer = 0 To slots - 1
                    Dim rect As New Rectangle(
                        m_gridLeft + column * m_cellSize + CellGap \ 2,
                        m_gridTop + row * m_cellSize + CellGap \ 2,
                        m_cellSize - CellGap,
                        m_cellSize - CellGap)

                    If row = 0 AndAlso column = 0 Then
                        DrawCorner(g, rect)
                    ElseIf row = 0 OrElse column = 0 Then
                        DrawColorSwatch(g, rect, row, column)
                    Else
                        DrawForceCell(g, rect, row, column)
                    End If
                Next
            Next
        End Sub

        Private Sub DrawCorner(g As Graphics, rect As Rectangle)
            Using brush As New SolidBrush(Color.FromArgb(13, 17, 25))
                FillRounded(g, brush, rect, CornerRadius)
            End Using

            Using pen As New Pen(Color.FromArgb(38, 46, 64))
                DrawRounded(g, pen, rect, CornerRadius)
            End Using

            Using brush As New SolidBrush(Color.FromArgb(100, 116, 139))
                g.DrawString("j →", m_hintFont, brush, rect.X + 4.0F, rect.Y + 2.0F)
                g.DrawString("i ↓", m_hintFont, brush, rect.X + 4.0F, rect.Bottom - 15.0F)
            End Using
        End Sub

        Private Sub DrawColorSwatch(g As Graphics, rect As Rectangle, row As Integer, column As Integer)
            Dim typeIndex As Integer = If(row = 0, column - 1, row - 1)
            Dim color As Color = If(m_state Is Nothing, Color.White, m_state.ColorOf(typeIndex))
            Dim hovered As Boolean = m_hoverRow = row AndAlso m_hoverColumn = column

            Using brush As New SolidBrush(color)
                FillRounded(g, brush, rect, CornerRadius)
            End Using

            If hovered Then
                Using pen As New Pen(Color.FromArgb(235, 34, 211, 238), 2.0F)
                    DrawRounded(g, pen, rect, CornerRadius)
                End Using
            End If

            ' 类型序号：浅色块上用深色字，深色块上用浅色字
            If m_cellSize < 20 Then Return

            Dim label As String = (typeIndex + 1).ToString()
            Dim size As SizeF = g.MeasureString(label, m_valueFont)
            Dim textColor As Color = If(color.GetBrightness() > 0.6F,
                                        Color.FromArgb(210, 10, 14, 22),
                                        Color.FromArgb(230, 240, 246, 255))

            Using brush As New SolidBrush(textColor)
                g.DrawString(label, m_valueFont, brush,
                             rect.X + (rect.Width - size.Width) * 0.5F,
                             rect.Y + (rect.Height - size.Height) * 0.5F)
            End Using
        End Sub

        Private Sub DrawForceCell(g As Graphics, rect As Rectangle, row As Integer, column As Integer)
            Dim value As Single = If(m_state Is Nothing, 0.0F, m_state.GetForce(row - 1, column - 1))
            Dim color As Color = ForceColor(value)
            Dim hovered As Boolean = m_hoverRow = row AndAlso m_hoverColumn = column

            Using brush As New SolidBrush(color)
                FillRounded(g, brush, rect, CornerRadius)
            End Using

            If hovered Then
                Using pen As New Pen(Color.FromArgb(235, 34, 211, 238), 2.0F)
                    DrawRounded(g, pen, rect, CornerRadius)
                End Using
            Else
                Using pen As New Pen(Color.FromArgb(30, 36, 50))
                    DrawRounded(g, pen, rect, CornerRadius)
                End Using
            End If

            If m_cellSize < 26 Then Return

            Dim label As String = value.ToString("0.00")
            Dim size As SizeF = g.MeasureString(label, m_valueFont)
            Dim textColor As Color = If(color.GetBrightness() > 0.55F,
                                        Color.FromArgb(225, 8, 12, 18),
                                        Color.FromArgb(205, 255, 255, 255))

            Using brush As New SolidBrush(textColor)
                g.DrawString(label, m_valueFont, brush,
                             rect.X + (rect.Width - size.Width) * 0.5F,
                             rect.Y + (rect.Height - size.Height) * 0.5F)
            End Using
        End Sub

        ' ------------------------------------------------------------------
        ' 命中测试与交互
        ' ------------------------------------------------------------------

        ''' <summary>把屏幕坐标换算成网格坐标（行 = y / 栏 = x）</summary>
        Private Function HitTest(screenX As Integer, screenY As Integer,
                                 ByRef gridRow As Integer, ByRef gridColumn As Integer) As Boolean
            gridRow = -1
            gridColumn = -1

            If m_cellSize <= 1 Then Return False
            If screenX < m_gridLeft OrElse screenY < m_gridTop Then Return False

            Dim row As Integer = (screenY - m_gridTop) \ m_cellSize
            Dim column As Integer = (screenX - m_gridLeft) \ m_cellSize

            If row < 0 OrElse column < 0 OrElse row > m_types OrElse column > m_types Then Return False

            gridRow = row
            gridColumn = column

            Return True
        End Function

        Private Shared Function IsInnerCell(row As Integer, column As Integer) As Boolean
            Return row > 0 AndAlso column > 0
        End Function

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            MyBase.OnMouseMove(e)

            Dim row As Integer = -1
            Dim column As Integer = -1

            If Not HitTest(e.X, e.Y, row, column) Then
                row = -1
                column = -1
            End If

            If row = m_hoverRow AndAlso column = m_hoverColumn Then Return

            m_hoverRow = row
            m_hoverColumn = column

            Invalidate()
        End Sub

        Protected Overrides Sub OnMouseLeave(e As EventArgs)
            MyBase.OnMouseLeave(e)

            m_hoverRow = -1
            m_hoverColumn = -1

            Invalidate()
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            MyBase.OnMouseDown(e)

            Dim row As Integer = -1
            Dim column As Integer = -1

            If Not HitTest(e.X, e.Y, row, column) Then Return

            If row = 0 AndAlso column = 0 Then Return

            If Not IsInnerCell(row, column) Then
                ' 色块：左键弹出取色器（首行与首列的同一类型共享同一个颜色）
                If e.Button = MouseButtons.Left Then
                    PickColor(If(row = 0, column - 1, row - 1))
                End If

                Return
            End If

            If e.Button = MouseButtons.Right Then
                SetForce(row - 1, column - 1, 0.0F)
            ElseIf e.Button = MouseButtons.Left Then
                ' 没有滚轮的设备也能编辑：左键点一下 +0.1
                SetForce(row - 1, column - 1, m_state.GetForce(row - 1, column - 1) + WheelStep)
            End If
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            ' 不调用 MyBase：避免滚轮冒泡给外层面板造成误滚动
            Dim row As Integer = -1
            Dim column As Integer = -1

            If Not HitTest(e.X, e.Y, row, column) Then Return
            If Not IsInnerCell(row, column) Then Return

            Dim notches As Single = e.Delta / 120.0F

            SetForce(row - 1, column - 1,
                     m_state.GetForce(row - 1, column - 1) + WheelStep * notches)
        End Sub

        Private Sub SetForce(typeA As Integer, typeB As Integer, value As Single)
            If m_state Is Nothing Then Return

            m_state.SetForce(typeA, typeB, value)

            Invalidate()
            RaiseEvent ForceChanged(Me, EventArgs.Empty)
        End Sub

        Private Sub PickColor(typeIndex As Integer)
            If m_state Is Nothing Then Return

            Using dialog As New ColorDialog()
                dialog.Color = m_state.ColorOf(typeIndex)
                dialog.FullOpen = True
                dialog.AnyColor = True

                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return

                m_state.TypeColors(typeIndex) = dialog.Color

                Invalidate()
                RaiseEvent ColorChanged(Me, EventArgs.Empty)
            End Using
        End Sub

        ' ------------------------------------------------------------------
        ' 颜色与图形工具
        ' ------------------------------------------------------------------

        ''' <summary>
        ''' 力值 → 单元格颜色（与参考实现的 <c>_calc_particle_force_button_color</c> 等价：
        ''' 负值红、正值绿，饱和度与明度都随 <c>|f|</c> 增大）。
        ''' </summary>
        Public Shared Function ForceColor(force As Single) As Color
            Dim intensity As Single = System.Math.Min(System.Math.Abs(force), 1.0F)
            Dim hue As Single = If(force < 0.0F, 0.0F, 0.3333F)

            ' 给零值留一点底色，否则格子在深色面板上"消失"
            Dim value As Single = 0.07F + 0.93F * intensity
            Dim saturation As Single = If(force = 0.0F, 0.0F, 0.35F + 0.65F * intensity)

            Return FromHsv(hue, saturation, value)
        End Function

        ''' <summary>HSV（h ∈ [0,1)，s/v ∈ [0,1]）→ RGB</summary>
        Public Shared Function FromHsv(hue As Single, saturation As Single, value As Single) As Color
            Dim h As Single = hue - CSng(System.Math.Floor(CDbl(hue)))       ' 归一到 [0,1)
            Dim s As Single = Clamp01(saturation)
            Dim v As Single = Clamp01(value)

            If s <= 0.0F Then
                Dim level As Integer = CInt(System.Math.Round(CDbl(v) * 255.0))
                Return Color.FromArgb(255, level, level, level)
            End If

            Dim sector As Single = h * 6.0F
            Dim index As Integer = CInt(System.Math.Floor(CDbl(sector))) Mod 6
            Dim fraction As Single = sector - CSng(System.Math.Floor(CDbl(sector)))

            Dim p As Single = v * (1.0F - s)
            Dim q As Single = v * (1.0F - s * fraction)
            Dim t As Single = v * (1.0F - s * (1.0F - fraction))

            Dim r As Single
            Dim g As Single
            Dim b As Single

            Select Case index
                Case 0
                    r = v : g = t : b = p
                Case 1
                    r = q : g = v : b = p
                Case 2
                    r = p : g = v : b = t
                Case 3
                    r = p : g = q : b = v
                Case 4
                    r = t : g = p : b = v
                Case Else
                    r = v : g = p : b = q
            End Select

            Return Color.FromArgb(255, ToByte(r), ToByte(g), ToByte(b))
        End Function

        Private Shared Function Clamp01(value As Single) As Single
            If value < 0.0F Then Return 0.0F
            If value > 1.0F Then Return 1.0F
            Return value
        End Function

        Private Shared Function ToByte(component As Single) As Integer
            Dim scaled As Integer = CInt(System.Math.Round(CDbl(component) * 255.0))

            If scaled < 0 Then Return 0
            If scaled > 255 Then Return 255

            Return scaled
        End Function

        Private Shared Sub FillRounded(g As Graphics, brush As Brush, rect As Rectangle, radius As Integer)
            Using path As GraphicsPath = RoundedPath(rect, radius)
                g.FillPath(brush, path)
            End Using
        End Sub

        Private Shared Sub DrawRounded(g As Graphics, pen As Pen, rect As Rectangle, radius As Integer)
            Using path As GraphicsPath = RoundedPath(rect, radius)
                g.DrawPath(pen, path)
            End Using
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

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                m_valueFont.Dispose()
                m_hintFont.Dispose()
            End If

            MyBase.Dispose(disposing)
        End Sub

    End Class

End Namespace
