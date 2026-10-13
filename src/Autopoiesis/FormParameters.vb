Imports System.Drawing
Imports System.Windows.Forms

''' <summary>
''' 参数控制工具窗口（tool window）：承载原本主窗口右侧的"参数实验台"
''' （种群 / 动力学 / 重置策略 / 交互矩阵 / 视图）。从 <see cref="FormWorld"/> 抽取，
''' 主窗口只保留 DirectX 画布，画布随窗体大小自动铺满。
''' </summary>
Public Class FormParameters
    Inherits Form

    Private ReadOnly m_parameters As Physics.SimulationParameters
    Private ReadOnly m_view As Rendering.ParticleCloudView
    Private ReadOnly m_owner As FormWorld

    Private m_simulation As Simulation.ParticleLifeSimulation
    Private m_backend As Physics.ComputeBackend

    Private m_suppressParameterEvents As Boolean = False

    ' ------------------------------------------------------------------
    ' 控件
    ' ------------------------------------------------------------------
    Private m_settingsHost As Panel

    Private m_sliderCount As UI.ParameterSliderRow
    Private m_sliderTypes As UI.ParameterSliderRow
    Private m_sliderRadius As UI.ParameterSliderRow
    Private m_sliderDamping As UI.ParameterSliderRow
    Private m_sliderRepulsion As UI.ParameterSliderRow
    Private m_sliderInteraction As UI.ParameterSliderRow
    Private m_sliderDensityLimit As UI.ParameterSliderRow
    Private m_sliderPointSize As UI.ParameterSliderRow

    Private m_seedBox As NumericUpDown
    Private m_checkRegenerateColors As CheckBox
    Private m_checkRegenerateForces As CheckBox

    Private m_matrixEditor As UI.ForceMatrixEditor
    Private m_matrixHint As Label

    Private Const SettingsPanelWidth As Integer = 356
    ''' <summary>卡片宽度：为纵向滚动条预留 17px，避免出现横向滚动条</summary>
    Private Const CardWidth As Integer = SettingsPanelWidth - 46

    ''' <summary>交互矩阵卡片的操作提示（未悬停时恢复这句话）</summary>
    Private Const MatrixHintDefault As String =
        "首行/首列点色块改颜色 · 格内滚轮 ±0.1 · 右键归零 · 纵列为 i、横行为 j"

    ' ==================================================================
    ' 生命周期
    ' ==================================================================

    Public Sub New(parameters As Physics.SimulationParameters,
                   view As Rendering.ParticleCloudView,
                   owner As FormWorld)
        MyBase.New()

        m_parameters = parameters
        m_view = view
        m_owner = owner

        FormBorderStyle = FormBorderStyle.SizableToolWindow
        Text = "参数实验台"
        BackColor = UI.Theme.WindowBackground
        StartPosition = FormStartPosition.Manual
        ClientSize = New Size(SettingsPanelWidth, 720)
        MinimumSize = New Size(SettingsPanelWidth, 360)
        ShowInTaskbar = False
        DoubleBuffered = True

        BuildSettingsPanel()
    End Sub

    ''' <summary>关联已初始化的模拟与计算后端，并同步 UI 状态</summary>
    Public Sub AttachSimulation(simulation As Simulation.ParticleLifeSimulation,
                               backend As Physics.ComputeBackend)
        m_simulation = simulation
        m_backend = backend

        SyncFromParameters()
        m_matrixEditor.Bind(simulation.State)
        m_view.PointPixelSize = CInt(m_sliderPointSize.NumericValue)
    End Sub

    ' ==================================================================
    ' 控件访问（供主窗体调用）
    ' ==================================================================

    ''' <summary>随机种子框（主窗体初始化时读取初值）</summary>
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public ReadOnly Property SeedBox As NumericUpDown
        Get
            Return m_seedBox
        End Get
    End Property

    ''' <summary>粒子像素尺寸滑杆（校准模式会直接写值）</summary>
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public ReadOnly Property SliderPointSize As UI.ParameterSliderRow
        Get
            Return m_sliderPointSize
        End Get
    End Property

    ' ==================================================================
    ' 布局
    ' ==================================================================

    Private Sub BuildSettingsPanel()
        m_settingsHost = New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = UI.Theme.PanelBackground,
            .Margin = New Padding(0),
            .AutoScroll = True
        }

        Dim stack As New FlowLayoutPanel With {
            .Dock = DockStyle.Top,
            .FlowDirection = FlowDirection.TopDown,
            .WrapContents = False,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .BackColor = UI.Theme.PanelBackground,
            .Padding = New Padding(0, 8, 0, 12)
        }

        ' ---------------- 种群 ----------------
        m_sliderCount = New UI.ParameterSliderRow("粒子数（滑杆 = √count）", 2, 317, 64) With {
            .Width = CardWidth - 24,
            .Suffix = String.Empty
        }

        m_sliderCount.ValueFormatter =
            Function(value As Single) CInt(value * value).ToString("N0")

        m_sliderTypes = New UI.ParameterSliderRow("类型数", 1, Physics.SimulationParameters.MaxParticleTypes, 6) With {
            .Width = CardWidth - 24
        }

        Dim populationCard As UI.CardPanel = CreateCard("种群", UI.Theme.Accent, "population", 52 * 2 + 36 + 6)
        populationCard.Body.Controls.Add(m_sliderCount)
        populationCard.Body.Controls.Add(m_sliderTypes)
        populationCard.Body.Controls.Add(BuildSeedRow(populationCard))

        AddHandler m_sliderCount.ValueChanged, AddressOf OnCountChanged
        AddHandler m_sliderTypes.ValueChanged, AddressOf OnTypesChanged

        ' ---------------- 动力学 ----------------
        m_sliderRadius = New UI.ParameterSliderRow("粒子半径（绘制）", 1, 30, 3) With {.Width = CardWidth - 24}
        m_sliderDamping = New UI.ParameterSliderRow("阻尼 β", 50, 99, 85) With {.Width = CardWidth - 24}
        m_sliderDamping.Scale = 0.01F
        m_sliderDamping.ValueFormat = "0.00"

        m_sliderRepulsion = New UI.ParameterSliderRow("硬核排斥半径 r", 1, 100, 20) With {.Width = CardWidth - 24}
        m_sliderInteraction = New UI.ParameterSliderRow("交互半径 R", 2, 300, 80) With {.Width = CardWidth - 24}
        m_sliderDensityLimit = New UI.ParameterSliderRow("密度上限 ρ_limit", 0, 50, 10) With {.Width = CardWidth - 24}
        m_sliderDensityLimit.Scale = 1.0F

        Dim dynamicsCard As UI.CardPanel = CreateCard("动力学", UI.Theme.Success, "dynamics", 52 * 5 + 6)
        dynamicsCard.Body.Controls.Add(m_sliderRadius)
        dynamicsCard.Body.Controls.Add(m_sliderDamping)
        dynamicsCard.Body.Controls.Add(m_sliderRepulsion)
        dynamicsCard.Body.Controls.Add(m_sliderInteraction)
        dynamicsCard.Body.Controls.Add(m_sliderDensityLimit)

        AddHandler m_sliderRadius.ValueChanged, AddressOf OnDynamicsChanged
        AddHandler m_sliderDamping.ValueChanged, AddressOf OnDynamicsChanged
        AddHandler m_sliderRepulsion.ValueChanged, AddressOf OnDynamicsChanged
        AddHandler m_sliderInteraction.ValueChanged, AddressOf OnDynamicsChanged
        AddHandler m_sliderDensityLimit.ValueChanged, AddressOf OnDynamicsChanged

        ' ---------------- 重置策略 ----------------
        m_checkRegenerateColors = CreateCheckBox("重新生成颜色（重置时生效）")
        m_checkRegenerateForces = CreateCheckBox("重新生成力矩阵（重置时生效）")

        Dim resetButton As Button = CreateActionButton("↺ 应用参数并重置", CardWidth - 24, 30)
        resetButton.ForeColor = UI.Theme.Primary
        resetButton.FlatAppearance.BorderColor = UI.Theme.Primary
        AddHandler resetButton.Click, AddressOf OnResetClicked

        Dim resetCard As UI.CardPanel = CreateCard("重置策略", UI.Theme.Warning, "reset", 26 * 2 + 36 + 6)
        resetCard.Body.Controls.Add(m_checkRegenerateColors)
        resetCard.Body.Controls.Add(m_checkRegenerateForces)
        resetCard.Body.Controls.Add(resetButton)

        ' ---------------- 交互矩阵 ----------------
        m_matrixEditor = New UI.ForceMatrixEditor With {
            .Width = CardWidth - 24,
            .Height = 300,
            .Types = 6
        }

        Dim clearButton As Button = CreateActionButton("全部归零", (CardWidth - 24 - 8) \ 2, 30)
        Dim randomButton As Button = CreateActionButton("重新随机", (CardWidth - 24 - 8) \ 2, 30)
        randomButton.Location = New Point(clearButton.Width + 8, 0)

        AddHandler clearButton.Click, AddressOf OnClearForceMatrix
        AddHandler randomButton.Click, AddressOf OnRandomizeForceMatrix
        AddHandler m_matrixEditor.ForceChanged, AddressOf OnForceMatrixEdited
        AddHandler m_matrixEditor.ColorChanged, AddressOf OnForceMatrixEdited

        Dim matrixButtons As New Panel With {
            .Width = CardWidth - 24,
            .Height = 30,
            .BackColor = Color.Transparent
        }

        matrixButtons.Controls.Add(clearButton)
        matrixButtons.Controls.Add(randomButton)

        m_matrixHint = New Label With {
            .Text = MatrixHintDefault,
            .Width = CardWidth - 24,
            .Height = 30,
            .ForeColor = UI.Theme.Muted,
            .Font = New Font("Microsoft YaHei UI", 7.5F),
            .BackColor = Color.Transparent
        }

        Dim matrixCard As UI.CardPanel = CreateCard("交互矩阵 A[i][j]", UI.Theme.Primary, "force matrix", 300 + 4 + 30 + 30 + 6)
        matrixCard.Body.Controls.Add(m_matrixEditor)
        matrixCard.Body.Controls.Add(matrixButtons)
        matrixCard.Body.Controls.Add(m_matrixHint)

        ' ---------------- 视图 ----------------
        m_sliderPointSize = New UI.ParameterSliderRow("粒子像素尺寸", 1, 16, 2) With {.Width = CardWidth - 24}
        AddHandler m_sliderPointSize.ValueChanged, AddressOf OnViewChanged

        Dim viewCard As UI.CardPanel = CreateCard("视图", Color.FromArgb(167, 139, 250), "view", 52 + 6)
        viewCard.Body.Controls.Add(m_sliderPointSize)

        stack.Controls.Add(populationCard)
        stack.Controls.Add(dynamicsCard)
        stack.Controls.Add(resetCard)
        stack.Controls.Add(matrixCard)
        stack.Controls.Add(viewCard)

        m_settingsHost.Controls.Add(stack)
        Controls.Add(m_settingsHost)
    End Sub

    Private Function BuildSeedRow(host As UI.CardPanel) As Panel
        Dim row As New Panel With {
            .Width = CardWidth - 24,
            .Height = 32,
            .BackColor = Color.Transparent
        }

        Dim caption As New Label With {
            .Text = "随机种子",
            .AutoSize = True,
            .Location = New Point(2, 7),
            .ForeColor = UI.Theme.Muted,
            .BackColor = Color.Transparent,
            .Font = New Font("Microsoft YaHei UI", 8.75F)
        }

        m_seedBox = New NumericUpDown With {
            .Minimum = 0,
            .Maximum = 32768,
            .Value = 0,
            .Location = New Point(108, 3),
            .Width = CardWidth - 24 - 108 - 92,
            .BackColor = UI.Theme.ControlBackground,
            .ForeColor = UI.Theme.Text,
            .BorderStyle = BorderStyle.FixedSingle,
            .Font = New Font("Consolas", 9.0F)
        }

        AddHandler m_seedBox.ValueChanged, AddressOf OnSeedChanged

        Dim dice As Button = CreateActionButton("🎲 掷骰", 84, 26)
        dice.Location = New Point(CardWidth - 24 - 84, 3)
        AddHandler dice.Click, AddressOf OnRollSeed

        row.Controls.Add(caption)
        row.Controls.Add(m_seedBox)
        row.Controls.Add(dice)

        Return row
    End Function

    Private Shared Function CreateCard(caption As String, accent As Color,
                                       Optional hint As String = Nothing,
                                       Optional contentHeight As Integer = 120) As UI.CardPanel
        Return New UI.CardPanel(caption, accent, hint) With {
            .Width = CardWidth,
            .Height = UI.CardPanel.SuggestHeight(contentHeight),
            .Margin = New Padding(14, 6, 14, 0)
        }
    End Function

    Private Shared Function CreateCheckBox(caption As String) As CheckBox
        Return New CheckBox With {
            .Text = caption,
            .Width = CardWidth - 24,
            .Height = 24,
            .ForeColor = UI.Theme.Text,
            .BackColor = Color.Transparent,
            .FlatStyle = FlatStyle.Flat,
            .Font = New Font("Microsoft YaHei UI", 8.5F),
            .Margin = New Padding(0)
        }
    End Function

    ''' <remarks>
    ''' 刻意做成独立的函数名（而不是 <c>Optional height</c> 重载）：两个重载会在
    ''' 只传两个实参时产生歧义。
    ''' </remarks>
    Private Shared Function CreateActionButton(caption As String, width As Integer, height As Integer) As Button
        Dim button As New Button With {
            .Text = caption,
            .AutoSize = False,
            .Size = New Size(width, height),
            .FlatStyle = FlatStyle.Flat,
            .ForeColor = UI.Theme.Text,
            .BackColor = Color.White,
            .Font = New Font("Microsoft YaHei UI", 8.75F),
            .TabStop = False,
            .Cursor = Cursors.Hand
        }

        button.FlatAppearance.BorderColor = UI.Theme.ControlBorder
        button.FlatAppearance.MouseOverBackColor = UI.Theme.HoverBackground

        Return button
    End Function

    ' ==================================================================
    ' 参数同步
    ' ==================================================================

    ''' <summary>把参数值写回滑杆 / 复选 / 种子框（不触发事件）</summary>
    Private Sub SyncFromParameters()
        m_suppressParameterEvents = True

        Try
            m_sliderCount.NumericValue = CSng(System.Math.Sqrt(System.Math.Max(2, m_parameters.ParticleCount)))
            m_sliderTypes.NumericValue = m_parameters.ParticleTypes

            m_sliderRadius.NumericValue = m_parameters.ParticleRadius
            m_sliderDamping.NumericValue = m_parameters.Dampening
            m_sliderRepulsion.NumericValue = m_parameters.RepulsionRadius
            m_sliderInteraction.NumericValue = m_parameters.InteractionRadius
            m_sliderDensityLimit.NumericValue = m_parameters.DensityLimit

            If m_seedBox.Value <> m_parameters.Seed Then m_seedBox.Value = m_parameters.Seed

            m_matrixEditor.Types = m_parameters.ParticleTypes
        Finally
            m_suppressParameterEvents = False
        End Try

        UpdateParameterWarnings()
    End Sub

    ''' <summary>把动态参数（半径/阻尼/密度上限）写进运行参数</summary>
    Private Sub ReadDynamicsFromUi()
        m_parameters.ParticleRadius = m_sliderRadius.NumericValue
        m_parameters.Dampening = m_sliderDamping.NumericValue
        m_parameters.RepulsionRadius = m_sliderRepulsion.NumericValue
        m_parameters.InteractionRadius = m_sliderInteraction.NumericValue
        m_parameters.DensityLimit = m_sliderDensityLimit.NumericValue
        m_parameters.Seed = CInt(m_seedBox.Value)
    End Sub

    ''' <summary>校验并刷新界面上的告警状态</summary>
    Private Sub UpdateParameterWarnings()
        Dim invalidRepulsion As Boolean = m_parameters.RepulsionRadius >= m_parameters.InteractionRadius

        m_owner.ParametersValid = Not invalidRepulsion

        m_sliderRepulsion.SetWarning(invalidRepulsion, If(invalidRepulsion, "r ≥ R", Nothing))
        m_sliderInteraction.SetWarning(invalidRepulsion, If(invalidRepulsion, "需 > r", Nothing))

        Dim count As Integer = CInt(m_sliderCount.NumericValue * m_sliderCount.NumericValue)

        m_sliderCount.WarningColor = UI.Theme.Warning
        m_sliderCount.SetWarning(count > 50000)
    End Sub

    ' ==================================================================
    ' 事件
    ' ==================================================================

    Private Sub OnCountChanged(sender As Object, e As EventArgs)
        If m_suppressParameterEvents Then Return

        ReadDynamicsFromUi()

        m_parameters.ParticleCount = CInt(m_sliderCount.NumericValue * m_sliderCount.NumericValue)

        UpdateParameterWarnings()

        If m_simulation Is Nothing Then Return

        ' 粒子数变化会改变整个种群，直接重建
        m_simulation.Rebuild(regenerateForces:=m_checkRegenerateForces.Checked,
                             regenerateColors:=m_checkRegenerateColors.Checked)

        m_matrixEditor.Bind(m_simulation.State)
    End Sub

    Private Sub OnTypesChanged(sender As Object, e As EventArgs)
        If m_suppressParameterEvents Then Return

        ReadDynamicsFromUi()

        m_parameters.ParticleTypes = CInt(m_sliderTypes.NumericValue)

        UpdateParameterWarnings()

        If m_simulation Is Nothing Then Return

        m_simulation.Rebuild(regenerateForces:=m_checkRegenerateForces.Checked,
                             regenerateColors:=m_checkRegenerateColors.Checked)

        m_matrixEditor.Types = m_parameters.ParticleTypes
        m_matrixEditor.Bind(m_simulation.State)
    End Sub

    Private Sub OnDynamicsChanged(sender As Object, e As EventArgs)
        If m_suppressParameterEvents Then Return

        ReadDynamicsFromUi()
        UpdateParameterWarnings()
    End Sub

    Private Sub OnSeedChanged(sender As Object, e As EventArgs)
        If m_suppressParameterEvents Then Return

        m_parameters.Seed = CInt(m_seedBox.Value)

        If m_simulation Is Nothing Then Return

        ' 种子本身就是随机源，换种子等价于重新生成一套世界
        m_simulation.Rebuild(regenerateForces:=True, regenerateColors:=m_checkRegenerateColors.Checked)
        m_matrixEditor.Bind(m_simulation.State)
    End Sub

    Private Sub OnRollSeed(sender As Object, e As EventArgs)
        m_seedBox.Value = CDec(New System.Random().Next(0, 32769))
    End Sub

    Private Sub OnViewChanged(sender As Object, e As EventArgs)
        If m_suppressParameterEvents Then Return
        If m_view Is Nothing Then Return

        m_view.PointPixelSize = CInt(m_sliderPointSize.NumericValue)
        m_view.RenderState(If(m_simulation Is Nothing, Nothing, m_simulation.State))
    End Sub

    ''' <summary>重置按钮（标题栏与重置卡片共用）</summary>
    Public Sub RequestReset()
        OnResetClicked(Me, EventArgs.Empty)
    End Sub

    Private Sub OnResetClicked(sender As Object, e As EventArgs)
        If m_simulation Is Nothing Then Return

        ReadDynamicsFromUi()

        Dim notes As String() = m_parameters.Validate()

        If notes.Length > 0 Then m_owner.StatusSuffix = String.Join(" / ", notes)

        m_simulation.Rebuild(regenerateForces:=m_checkRegenerateForces.Checked,
                             regenerateColors:=m_checkRegenerateColors.Checked)

        SyncFromParameters()
        m_matrixEditor.Bind(m_simulation.State)

        m_owner.IsRunning = True
        m_owner.UpdatePlayState()
    End Sub

    Private Sub OnClearForceMatrix(sender As Object, e As EventArgs)
        m_matrixEditor.ClearAll()
    End Sub

    Private Sub OnRandomizeForceMatrix(sender As Object, e As EventArgs)
        If m_simulation Is Nothing Then Return

        ' 用种子 + 当前步数派生，保证连续点击得到不同的矩阵
        m_simulation.State.RandomizeForceMatrix(m_parameters.Seed + CInt(m_simulation.StepCount And &H7FFFFFF))
        m_backend.Solver.Invalidate()

        m_matrixEditor.Invalidate()
    End Sub

    Private Sub OnForceMatrixEdited(sender As Object, e As EventArgs)
        If m_matrixHint Is Nothing Then Return

        Dim hover As String = m_matrixEditor.HoverText

        m_matrixHint.Text = If(String.IsNullOrEmpty(hover), MatrixHintDefault, hover)
    End Sub

    ' ==================================================================
    ' 关闭：随主窗体一起释放
    ' ==================================================================

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' 工具窗口只隐藏，关闭交给主窗体统一处理，避免重复释放
        If e.CloseReason = CloseReason.UserClosing AndAlso m_owner IsNot Nothing AndAlso Not m_owner.IsDisposed Then
            e.Cancel = True
            Visible = False
            Return
        End If

        MyBase.OnFormClosing(e)
    End Sub

End Class
