Imports System.ComponentModel
Imports System.Drawing
Imports System.Windows.Forms

Namespace UI

    ''' <summary>
    ''' 「标题 + 轨道 + 数值」一行，供全部标量参数复用。
    ''' </summary>
    ''' <remarks>
    ''' <see cref="System.Windows.Forms.TrackBar"/> 只有整数取值，因此小数参数
    ''' （阻尼、密度上限等）通过 <see cref="Scale"/> 做定点映射：
    ''' <c>实际值 = 滑杆整数 × Scale</c>。
    ''' </remarks>
    Public Class ParameterSliderRow
        Inherits Panel

        Private ReadOnly m_titleLabel As New Label()
        Private ReadOnly m_valueLabel As New Label()
        Private ReadOnly m_track As New TrackBar()

        Private m_scale As Single = 1.0F
        Private m_formatter As Func(Of Single, String) = Nothing
        Private m_suffix As String = String.Empty
        Private m_suppress As Boolean = False
        Private m_warning As Boolean = False
        Private m_warningText As String = String.Empty
        Private m_warningColor As Color = Color.FromArgb(239, 68, 68)

        Public Sub New(title As String, minimum As Integer, maximum As Integer, value As Integer)
            Height = 52
            Margin = New Padding(0, 0, 0, 2)
            BackColor = Color.Transparent

            m_titleLabel.Text = title
            m_titleLabel.AutoSize = True
            m_titleLabel.ForeColor = Color.FromArgb(148, 163, 184)
            m_titleLabel.Font = New Font("Microsoft YaHei UI", 8.75F)
            m_titleLabel.Location = New Point(2, 4)
            m_titleLabel.BackColor = Color.Transparent

            m_valueLabel.AutoSize = False
            m_valueLabel.TextAlign = ContentAlignment.TopRight
            m_valueLabel.ForeColor = Color.FromArgb(230, 237, 247)
            m_valueLabel.Font = New Font("Consolas", 9.5F, FontStyle.Bold)
            m_valueLabel.BackColor = Color.Transparent

            m_track.Minimum = minimum
            m_track.Maximum = System.Math.Max(maximum, minimum + 1)
            m_track.TickStyle = TickStyle.None
            m_track.AutoSize = False
            m_track.Height = 26
            m_track.BackColor = Color.FromArgb(18, 23, 34)
            m_track.Value = System.Math.Max(m_track.Minimum, System.Math.Min(m_track.Maximum, value))
            m_track.Location = New Point(0, 22)

            AddHandler m_track.Scroll, AddressOf OnTrackScroll
            AddHandler m_track.ValueChanged, AddressOf OnTrackScroll

            Controls.Add(m_titleLabel)
            Controls.Add(m_valueLabel)
            Controls.Add(m_track)

            UpdateLabel()
        End Sub

        ''' <summary>用户拖动滑杆（程序化赋值不会触发）</summary>
        Public Event ValueChanged As EventHandler

        ''' <summary>滑杆整数到实际值的比例</summary>
        Public Property Scale As Single
            Get
                Return m_scale
            End Get
            Set(value As Single)
                If value <= 0.0F Then Return

                m_scale = value
                UpdateLabel()
            End Set
        End Property

        ''' <summary>数值显示格式（默认 "0.##"）</summary>
        Public Property ValueFormat As String = "0.##"

        ''' <summary>数值后缀（例如 " px"）</summary>
        Public Property Suffix As String
            Get
                Return m_suffix
            End Get
            Set(value As String)
                m_suffix = If(value, String.Empty)
                UpdateLabel()
            End Set
        End Property

        ''' <summary>自定义数值格式化（优先于 <see cref="ValueFormat"/>）</summary>
        Public Property ValueFormatter As Func(Of Single, String)

        ''' <summary>实际数值（受 <see cref="Scale"/> 缩放）</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public Property NumericValue As Single
            Get
                Return m_track.Value * m_scale
            End Get
            Set(value As Single)
                Dim raw As Integer = CInt(System.Math.Round(CDbl(value) / CDbl(m_scale)))

                If raw < m_track.Minimum Then raw = m_track.Minimum
                If raw > m_track.Maximum Then raw = m_track.Maximum

                If m_track.Value = raw Then
                    UpdateLabel()
                    Return
                End If

                m_suppress = True
                Try
                    m_track.Value = raw
                Finally
                    m_suppress = False
                End Try

                UpdateLabel()
            End Set
        End Property

        ''' <summary>警示色（默认红；可用于「数值偏大」这类琥珀色提示）</summary>
        Public Property WarningColor As Color
            Get
                Return m_warningColor
            End Get
            Set(value As Color)
                m_warningColor = value

                If m_warning Then
                    m_titleLabel.ForeColor = value
                    m_valueLabel.ForeColor = value
                End If
            End Set
        End Property

        ''' <summary>
        ''' 把行标记为需要提醒。
        ''' </summary>
        ''' <param name="active">是否显示提醒</param>
        ''' <param name="message">
        ''' 为 <c>Nothing</c> 时只改变颜色，数值文本仍然按格式化结果显示；
        ''' 给了文本则用它替换数值文本（例如 "r ≥ R"）。
        ''' </param>
        Public Sub SetWarning(active As Boolean, Optional message As String = Nothing)
            If m_warning = active AndAlso message = m_warningText Then Return

            m_warning = active
            m_warningText = If(message, String.Empty)

            m_titleLabel.ForeColor = If(active, m_warningColor, Color.FromArgb(148, 163, 184))
            m_valueLabel.ForeColor = If(active, m_warningColor, Color.FromArgb(230, 237, 247))

            UpdateLabel()
        End Sub

        Private Sub OnTrackScroll(sender As Object, e As EventArgs)
            UpdateLabel()

            If m_suppress Then Return

            RaiseEvent ValueChanged(Me, EventArgs.Empty)
        End Sub

        Private Sub UpdateLabel()
            Dim value As Single = m_track.Value * m_scale

            If m_formatter IsNot Nothing Then
                m_valueLabel.Text = m_formatter(value)
            Else
                m_valueLabel.Text = value.ToString(ValueFormat) & m_suffix
            End If

            If m_warning AndAlso Not String.IsNullOrEmpty(m_warningText) Then
                m_valueLabel.Text = m_warningText
            End If
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)

            m_track.Width = System.Math.Max(40, Width - 4)
            m_valueLabel.Width = 110
            m_valueLabel.Location = New Point(System.Math.Max(0, Width - m_valueLabel.Width - 2), 2)
            m_valueLabel.Height = 18
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                m_titleLabel.Font.Dispose()
                m_valueLabel.Font.Dispose()
            End If

            MyBase.Dispose(disposing)
        End Sub

    End Class

End Namespace
