Imports System.Diagnostics
Imports System.Drawing
Imports System.Windows.Forms

''' <summary>
''' 主窗体：粒子生命（Particle Life）实时模拟视窗。
''' </summary>
''' <remarks>
''' <para>
''' 布局分四块：顶部标题带（应用名 / 后端徽章 / 运行控制）、中部左侧的粒子宇宙画布、
''' 左侧右侧的参数实验台（种群 / 动力学 / 重置策略 / 交互矩阵），以及底部状态栏。
''' </para>
''' <para>
''' 帧循环由单条 <see cref="Timer"/> 驱动，每帧的执行顺序是：
''' 物理步进 → 拖拽惯性推进 → 提交点云并请求重绘 → 节流刷新状态栏。
''' 物理步进是<b>同步</b>的（内核入队后立刻回读力数据），所以粒子数很大时帧率会
''' 自然下降；好处是渲染的永远是当帧算完的状态，不会出现撕裂或"看到上一帧"。
''' </para>
''' </remarks>
Public Class FormWorld

    ' ------------------------------------------------------------------
    ' 主题
    ' ------------------------------------------------------------------
    Private Shared ReadOnly ThemeBackground As Color = Color.FromArgb(11, 14, 20)
    Private Shared ReadOnly ThemeCanvasTop As Color = Color.FromArgb(10, 14, 21)
    Private Shared ReadOnly ThemePanel As Color = Color.FromArgb(15, 19, 28)
    Private Shared ReadOnly ThemeText As Color = Color.FromArgb(230, 237, 247)
    Private Shared ReadOnly ThemeMuted As Color = Color.FromArgb(148, 163, 184)
    Private Shared ReadOnly ThemeCyan As Color = Color.FromArgb(34, 211, 238)
    Private Shared ReadOnly ThemeGreen As Color = Color.FromArgb(52, 211, 153)
    Private Shared ReadOnly ThemeAmber As Color = Color.FromArgb(245, 158, 11)
    Private Shared ReadOnly ThemeRed As Color = Color.FromArgb(239, 68, 68)
    Private Shared ReadOnly ThemeBlue As Color = Color.FromArgb(56, 189, 248)

    Private Const SettingsPanelWidth As Integer = 356
    ''' <summary>卡片宽度：为纵向滚动条预留 17px，避免出现横向滚动条</summary>
    Private Const CardWidth As Integer = SettingsPanelWidth - 46

    ''' <summary>交互矩阵卡片的操作提示（未悬停时恢复这句话）</summary>
    Private Const MatrixHintDefault As String =
        "首行/首列点色块改颜色 · 格内滚轮 ±0.1 · 右键归零 · 纵列为 i、横行为 j"

    ' ------------------------------------------------------------------
    ' 状态
    ' ------------------------------------------------------------------
    Private ReadOnly m_parameters As New Physics.SimulationParameters()

    Private m_backend As Physics.ComputeBackend
    Private m_simulation As Simulation.ParticleLifeSimulation
    Private m_view As Rendering.ParticleCloudView
    Private m_matrixEditor As UI.ForceMatrixEditor

    Private m_running As Boolean = True
    Private m_suppressParameterEvents As Boolean = False
    Private m_initialised As Boolean = False

    Private m_stepTimer As Timer
    Private m_fpsWatch As Stopwatch
    Private m_framesInWindow As Integer
    Private m_fps As Double
    Private m_statusSuffix As String = String.Empty
    Private m_lastStatusUpdate As Long
    Private m_renderedFrames As Integer
    Private m_pipelineProbed As Boolean
    Private m_parametersValid As Boolean = True

    ' ------------------------------------------------------------------
    ' 控件
    ' ------------------------------------------------------------------
    Private m_titleBar As Panel
    Private m_settingsHost As Panel
    Private m_statusLabel As Label
    Private m_titleLabel As Label
    Private m_backendBadge As Label
    Private m_stateBadge As Label
    Private m_matrixHint As Label

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

    Private m_buttonPlay As Button
    Private m_buttonStep As Button

    ' ==================================================================
    ' 生命周期
    ' ==================================================================

    Public Sub New()
        MyBase.New()

        InitializeComponent()

        BuildLayout()

        m_fpsWatch = Stopwatch.StartNew()

        m_stepTimer = New Timer With {.Interval = 16}
        AddHandler m_stepTimer.Tick, AddressOf OnStepTick
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)

        ' 先让窗口画出来，再去做可能耗时的计算后端初始化（NVRTC 编译内核）
        SetStatus("正在初始化计算后端…")
        BeginInvoke(New Action(AddressOf InitialiseSimulation))
    End Sub

    Private Sub InitialiseSimulation()
        Try
            m_backend = Physics.ComputeBackend.Create(preferGpu:=True)
        Catch ex As Exception
            MessageBox.Show(Me,
                            $"计算后端初始化失败：{Environment.NewLine}{ex.Message}",
                            "Autopoiesis",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error)
            Close()
            Return
        End Try

        m_parameters.Seed = CInt(m_seedBox.Value)
        m_parameters.Validate()

        m_simulation = New Simulation.ParticleLifeSimulation(m_parameters, m_backend)
        m_simulation.Rebuild(regenerateForces:=True, regenerateColors:=True)

        If StartupOptions.Calibrate Then ApplyCalibrationState()

        SyncUiFromParameters()

        m_view.Bind(m_parameters)
        m_view.ResetCamera()
        m_view.PointPixelSize = CInt(m_sliderPointSize.NumericValue)

        m_matrixEditor.Bind(m_simulation.State)

        m_backendBadge.Text = If(m_backend.IsGpu, "GPU", "CPU")
        m_backendBadge.ForeColor = If(m_backend.IsGpu, ThemeCyan, ThemeAmber)

        m_titleLabel.Text = If(m_backend.IsGpu,
                               "AUTOPOIESIS — Particle Life · F32 / CUDA",
                               "AUTOPOIESIS — Particle Life · F32 / CPU")

        m_initialised = True
        m_fpsWatch.Restart()

        UpdatePlayState()
        m_view.RenderState(m_simulation.State)

        m_stepTimer.Start()
    End Sub

    ''' <summary>
    ''' <c>--calibrate</c>：把状态替换成三颗已知世界坐标的粒子。
    ''' </summary>
    ''' <remarks>
    ''' 三颗粒子相距 256 / 144 世界单位（远大于被压到 2 的交互半径），因此受力为零、
    ''' 保持静止；颜色分别取默认调色板的前三种（青 / 红 / 绿），于是可以在截图里按
    ''' 颜色精确量出投影的缩放与 y 轴方向。
    ''' </remarks>
    Private Sub ApplyCalibrationState()
        m_parameters.RepulsionRadius = 1.0F
        m_parameters.InteractionRadius = 2.0F
        m_parameters.ParticleRadius = 7.0F
        m_parameters.Dampening = 0.85F

        Dim state As Physics.ParticleState = m_simulation.State

        state.EnsureCapacity(3)

        ' 世界中心
        state.PosX(0) = m_parameters.RegionWidth * 0.5F
        state.PosY(0) = m_parameters.RegionHeight * 0.5F
        state.Types(0) = 0

        ' 向 +x 偏移 256 世界单位
        state.PosX(1) = state.PosX(0) + 256.0F
        state.PosY(1) = state.PosY(0)
        state.Types(1) = 1

        ' 向 -y 偏移 144 世界单位
        state.PosX(2) = state.PosX(0)
        state.PosY(2) = state.PosY(0) - 144.0F
        state.Types(2) = 2

        For i As Integer = 0 To 2
            state.VelX(i) = 0.0F
            state.VelY(i) = 0.0F
            state.ForceX(i) = 0.0F
            state.ForceY(i) = 0.0F
            state.Density(i) = 0.0F
        Next

        m_backend.Solver.Invalidate()

        m_sliderPointSize.NumericValue = 6.0F
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        m_running = False

        If m_stepTimer IsNot Nothing Then
            m_stepTimer.Stop()
            m_stepTimer.Dispose()
            m_stepTimer = Nothing
        End If

        ' ParticleLifeSimulation 不再释放后端（谁创建谁释放），所以这里两件都要做
        If m_simulation IsNot Nothing Then
            m_simulation.Dispose()
            m_simulation = Nothing
        End If

        If m_backend IsNot Nothing Then
            m_backend.Dispose()
            m_backend = Nothing
        End If

        MyBase.OnFormClosing(e)
    End Sub

    ' ==================================================================
    ' 布局
    ' ==================================================================

    Private Sub BuildLayout()
        SuspendLayout()

        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .BackColor = ThemeBackground
        }

        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 46.0F))     ' 标题带
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))     ' 主体
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))     ' 状态栏

        ' ---- 标题带 ----
        m_titleBar = BuildTitleBar()

        ' ---- 主体：画布 + 参数实验台 ----
        Dim body As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .Margin = New Padding(0)
        }

        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, SettingsPanelWidth))

        m_view = New Rendering.ParticleCloudView With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0),
            .BackColor = ThemeCanvasTop
        }

        m_settingsHost = BuildSettingsPanel()

        body.Controls.Add(m_view, 0, 0)
        body.Controls.Add(m_settingsHost, 1, 0)

        ' ---- 状态栏 ----
        Dim statusBar As New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = Color.FromArgb(9, 12, 18),
            .Margin = New Padding(0)
        }

        m_statusLabel = New Label With {
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleLeft,
            .ForeColor = ThemeMuted,
            .BackColor = Color.Transparent,
            .Padding = New Padding(12, 0, 12, 0),
            .Font = New Font("Consolas", 8.75F),
            .AutoEllipsis = True
        }

        statusBar.Controls.Add(m_statusLabel)

        root.Controls.Add(m_titleBar, 0, 0)
        root.Controls.Add(body, 0, 1)
        root.Controls.Add(statusBar, 0, 2)

        Controls.Add(root)

        ResumeLayout(True)
    End Sub

    Private Function BuildTitleBar() As Panel
        Dim bar As New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = Color.FromArgb(9, 12, 18),
            .Margin = New Padding(0)
        }

        m_titleLabel = New Label With {
            .Text = "AUTOPOIESIS — Particle Life",
            .AutoSize = True,
            .Location = New Point(16, 13),
            .ForeColor = ThemeText,
            .BackColor = Color.Transparent,
            .Font = New Font("Microsoft YaHei UI", 11.0F, FontStyle.Bold)
        }

        m_backendBadge = New Label With {
            .Text = "GPU",
            .AutoSize = False,
            .Size = New Size(46, 22),
            .Location = New Point(330, 12),
            .TextAlign = ContentAlignment.MiddleCenter,
            .ForeColor = ThemeCyan,
            .BackColor = Color.FromArgb(20, 32, 44),
            .Font = New Font("Consolas", 9.0F, FontStyle.Bold)
        }

        m_stateBadge = New Label With {
            .Text = "▶ 运行中",
            .AutoSize = False,
            .Size = New Size(84, 22),
            .TextAlign = ContentAlignment.MiddleCenter,
            .ForeColor = ThemeGreen,
            .BackColor = Color.FromArgb(18, 34, 30),
            .Font = New Font("Microsoft YaHei UI", 8.75F)
        }

        m_buttonPlay = CreateToolButton("⏸ 暂停", 96)
        m_buttonStep = CreateToolButton("⏭ 单步", 84)
        Dim buttonReset As Button = CreateToolButton("↺ 重置", 84)
        Dim buttonCamera As Button = CreateToolButton("⌖ 视角", 84)
        Dim buttonFullscreen As Button = CreateToolButton("⛶ 全屏", 84)

        AddHandler m_buttonPlay.Click, AddressOf OnToggleRunning
        AddHandler m_buttonStep.Click, AddressOf OnSingleStep
        AddHandler buttonReset.Click, AddressOf OnResetClicked
        AddHandler buttonCamera.Click, AddressOf OnRecenterCamera
        AddHandler buttonFullscreen.Click, AddressOf OnToggleFullscreen

        ' 从右往左依次摆放
        Dim right As Integer = 1440
        Dim buttons As Button() = {buttonFullscreen, buttonCamera, buttonReset, m_buttonStep, m_buttonPlay}

        For Each button As Button In buttons
            right -= button.Width + 8
            button.Location = New Point(right, 10)
            bar.Controls.Add(button)
        Next

        right -= 16
        m_stateBadge.Location = New Point(right - m_stateBadge.Width, 12)
        bar.Controls.Add(m_stateBadge)

        bar.Controls.Add(m_titleLabel)
        bar.Controls.Add(m_backendBadge)

        AddHandler bar.Resize, Sub()
                                   ' 标题带宽度变化时把控件重新贴到右侧
                                   m_backendBadge.Location = New Point(m_titleLabel.Right + 10,
                                                                       (bar.ClientSize.Height - m_backendBadge.Height) \ 2)

                                   Dim cursor As Integer = bar.ClientSize.Width - 16
                                   Dim ordered As Button() = {buttonFullscreen, buttonCamera, buttonReset, m_buttonStep, m_buttonPlay}

                                   For Each button As Button In ordered
                                       cursor -= button.Width + 8
                                       button.Location = New Point(cursor, (bar.ClientSize.Height - button.Height) \ 2)
                                   Next

                                   cursor -= 12
                                   m_stateBadge.Location = New Point(cursor - m_stateBadge.Width,
                                                                     (bar.ClientSize.Height - m_stateBadge.Height) \ 2)
                               End Sub

        Return bar
    End Function

    Private Shared Function CreateToolButton(caption As String, width As Integer) As Button
        Dim button As New Button With {
            .Text = caption,
            .AutoSize = False,
            .Size = New Size(width, 26),
            .FlatStyle = FlatStyle.Flat,
            .ForeColor = Color.FromArgb(203, 213, 225),
            .BackColor = Color.FromArgb(21, 27, 38),
            .Font = New Font("Microsoft YaHei UI", 8.75F),
            .TabStop = False,
            .Cursor = Cursors.Hand
        }

        button.FlatAppearance.BorderColor = Color.FromArgb(45, 55, 72)
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 41, 55)

        Return button
    End Function

    ' ==================================================================
    ' 参数实验台
    ' ==================================================================

    Private Function BuildSettingsPanel() As Panel
        Dim host As New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = ThemePanel,
            .Margin = New Padding(0),
            .AutoScroll = True
        }

        Dim stack As New FlowLayoutPanel With {
            .Dock = DockStyle.Top,
            .FlowDirection = FlowDirection.TopDown,
            .WrapContents = False,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .BackColor = ThemePanel,
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

        ' contentHeight 只算卡片内部内容（不含标题带），高度由 CardPanel.SuggestHeight 补齐
        Dim populationCard As UI.CardPanel = CreateCard("种群", ThemeCyan, "population", 52 * 2 + 36 + 6)
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

        Dim dynamicsCard As UI.CardPanel = CreateCard("动力学", ThemeGreen, "dynamics", 52 * 5 + 6)
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
        resetButton.ForeColor = ThemeCyan
        resetButton.FlatAppearance.BorderColor = Color.FromArgb(34, 90, 110)
        AddHandler resetButton.Click, AddressOf OnResetClicked

        Dim resetCard As UI.CardPanel = CreateCard("重置策略", ThemeAmber, "reset", 26 * 2 + 36 + 6)
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
            .ForeColor = ThemeMuted,
            .Font = New Font("Microsoft YaHei UI", 7.5F),
            .BackColor = Color.Transparent
        }

        Dim matrixCard As UI.CardPanel = CreateCard("交互矩阵 A[i][j]", ThemeBlue, "force matrix", 300 + 4 + 30 + 30 + 6)
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

        host.Controls.Add(stack)

        Return host
    End Function

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
            .ForeColor = ThemeMuted,
            .BackColor = Color.Transparent,
            .Font = New Font("Microsoft YaHei UI", 8.75F)
        }

        m_seedBox = New NumericUpDown With {
            .Minimum = 0,
            .Maximum = 32768,
            .Value = 0,
            .Location = New Point(108, 3),
            .Width = CardWidth - 24 - 108 - 92,
            .BackColor = Color.FromArgb(21, 27, 38),
            .ForeColor = ThemeText,
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
            .ForeColor = Color.FromArgb(203, 213, 225),
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
        Dim button As Button = CreateToolButton(caption, width)
        button.Height = height
        Return button
    End Function

    ' ==================================================================
    ' 参数同步
    ' ==================================================================

    ''' <summary>把参数值写回滑杆 / 复选 / 种子框（不触发事件）</summary>
    Private Sub SyncUiFromParameters()
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

        m_parametersValid = Not invalidRepulsion

        m_sliderRepulsion.SetWarning(invalidRepulsion, If(invalidRepulsion, "r ≥ R", Nothing))
        m_sliderInteraction.SetWarning(invalidRepulsion, If(invalidRepulsion, "需 > r", Nothing))

        Dim count As Integer = CInt(m_sliderCount.NumericValue * m_sliderCount.NumericValue)

        m_sliderCount.WarningColor = ThemeAmber
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

        If Not m_initialised Then Return

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

        If Not m_initialised Then Return

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

        If Not m_initialised Then Return

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

    Private Sub OnResetClicked(sender As Object, e As EventArgs)
        If m_simulation Is Nothing Then Return

        ReadDynamicsFromUi()

        Dim notes As String() = m_parameters.Validate()

        If notes.Length > 0 Then m_statusSuffix = String.Join(" / ", notes)

        m_simulation.Rebuild(regenerateForces:=m_checkRegenerateForces.Checked,
                             regenerateColors:=m_checkRegenerateColors.Checked)

        SyncUiFromParameters()
        m_matrixEditor.Bind(m_simulation.State)

        m_running = True
        UpdatePlayState()
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

    Private Sub OnToggleRunning(sender As Object, e As EventArgs)
        m_running = Not m_running
        UpdatePlayState()
    End Sub

    Private Sub OnSingleStep(sender As Object, e As EventArgs)
        m_running = False
        UpdatePlayState()

        ExecuteOneStep()
        CommitFrame()
    End Sub

    Private Sub OnRecenterCamera(sender As Object, e As EventArgs)
        If m_view Is Nothing Then Return

        m_view.ResetCamera()
    End Sub

    Private Sub OnToggleFullscreen(sender As Object, e As EventArgs)
        If FormBorderStyle = FormBorderStyle.None Then
            FormBorderStyle = FormBorderStyle.Sizable
            WindowState = FormWindowState.Normal
        Else
            FormBorderStyle = FormBorderStyle.None
            WindowState = FormWindowState.Maximized
        End If
    End Sub

    Private Sub UpdatePlayState()
        If m_running Then
            m_stateBadge.Text = "▶ 运行中"
            m_stateBadge.ForeColor = ThemeGreen
            m_stateBadge.BackColor = Color.FromArgb(18, 34, 30)
            m_buttonPlay.Text = "⏸ 暂停"
        Else
            m_stateBadge.Text = "⏸ 已暂停"
            m_stateBadge.ForeColor = ThemeAmber
            m_stateBadge.BackColor = Color.FromArgb(38, 30, 14)
            m_buttonPlay.Text = "▶ 继续"
        End If
    End Sub

    ' ==================================================================
    ' 帧循环
    ' ==================================================================

    Private Sub OnStepTick(sender As Object, e As EventArgs)
        If Not m_initialised Then Return

        If m_running Then
            ExecuteOneStep()
        End If

        CommitFrame()
    End Sub

    Private Sub ExecuteOneStep()
        If m_simulation Is Nothing Then Return

        ' 参数非法时只是暂停推进，提示由状态栏按需给出（不污染常驻提示）
        If Not m_parametersValid Then Return

        Try
            m_simulation.StepOnce()
        Catch ex As Exception
            m_running = False
            UpdatePlayState()

            m_statusSuffix = $"步进失败: {ex.Message}"
            Return
        End Try
    End Sub

    Private Sub CommitFrame()
        If m_view Is Nothing OrElse m_simulation Is Nothing Then Return

        m_view.AdvanceInertia()
        m_view.RenderState(m_simulation.State)

        m_framesInWindow += 1
        m_renderedFrames += 1

        If m_fpsWatch.ElapsedMilliseconds >= 1000 Then
            Dim seconds As Double = m_fpsWatch.Elapsed.TotalSeconds

            If seconds > 0.0 Then m_fps = m_framesInWindow / seconds

            m_framesInWindow = 0
            m_fpsWatch.Restart()
        End If

        ' 首帧之后探测 GPU 实例化管线是否真的可用
        If Not m_pipelineProbed AndAlso m_renderedFrames >= 4 Then
            m_pipelineProbed = True

            If Not m_view.IsInstancedPipelineActive Then
                m_view.EnableSoftwareFallback()
                m_statusSuffix = m_view.PipelineDiagnostics
            End If
        End If

        UpdateStatus()

        CaptureSnapshotIfRequested()
    End Sub

    ''' <summary>
    ''' <c>--snapshot &lt;文件&gt;</c> 模式：跑够帧数后把画布截图存盘并退出。
    ''' </summary>
    ''' <remarks>
    ''' 截图只能在 UI 线程、且在渲染回调之外调用（<c>CaptureFrame</c> 会同步重绘，
    ''' 在 Render 事件里调用会抛 <c>InvalidOperationException</c>）。
    ''' 帧定时器的 Tick 正好满足这两个条件。
    ''' </remarks>
    Private Sub CaptureSnapshotIfRequested()
        Dim path As String = StartupOptions.SnapshotPath

        If String.IsNullOrWhiteSpace(path) Then Return
        If m_renderedFrames < StartupOptions.SnapshotFrame Then Return

        ' 只截一次：先清空请求，避免保存失败时反复尝试
        StartupOptions.SnapshotPath = String.Empty

        Console.WriteLine("---- 渲染诊断 ----")
        Console.WriteLine(m_view.DescribeFrame())

        Dim state As Physics.ParticleState = m_simulation.State
        Dim minX As Single = Single.MaxValue, maxX As Single = Single.MinValue
        Dim minY As Single = Single.MaxValue, maxY As Single = Single.MinValue

        For i As Integer = 0 To state.ParticleCount - 1
            If state.PosX(i) < minX Then minX = state.PosX(i)
            If state.PosX(i) > maxX Then maxX = state.PosX(i)
            If state.PosY(i) < minY Then minY = state.PosY(i)
            If state.PosY(i) > maxY Then maxY = state.PosY(i)
        Next

        Console.WriteLine($"状态包围盒  : x∈[{minX:F1},{maxX:F1}] (跨度 {maxX - minX:F1}) " &
                          $"y∈[{minY:F1},{maxY:F1}] (跨度 {maxY - minY:F1})")

        For t As Integer = 0 To System.Math.Min(2, state.TypeColors.Length - 1)
            Dim c As Color = state.TypeColors(t)
            Dim packed As Integer = Rendering.ParticleInstanceBuffer.PackColorBits(c)

            Console.WriteLine($"颜色[{t}]      : A{c.A} R{c.R} G{c.G} B{c.B} ToArgb=0x{c.ToArgb():X8} " &
                              $"packed=0x{packed:X8}")
        Next

        Console.WriteLine($"步数={m_simulation.StepCount} 求解={m_simulation.LastSolverMilliseconds:F2}ms " &
                          $"张量={m_simulation.LastIntegrateMilliseconds:F2}ms 后端={m_backend.StatusLabel()}")
        Console.WriteLine($"|F|max={MaxAbs(state.ForceX, state.ParticleCount):E3} " &
                          $"|v|max={MaxAbs(state.VelX, state.ParticleCount):E3}/" &
                          $"{MaxAbs(state.VelY, state.ParticleCount):E3} " &
                          $"|x|首={state.PosX(0):F3} F[0]=({state.ForceX(0):E3},{state.ForceY(0):E3}) " &
                          $"v[0]=({state.VelX(0):E3},{state.VelY(0):E3})")
        Console.WriteLine($"dt={m_parameters.Dt:F6} β={m_parameters.Dampening:F3} " &
                          $"r={m_parameters.RepulsionRadius:F2} R={m_parameters.InteractionRadius:F2} " &
                          $"ρ_limit={m_parameters.DensityLimit:F2}")
        Console.WriteLine($"力矩阵[0..5]={state.ForceMatrix(0):F3},{state.ForceMatrix(1):F3}," &
                          $"{state.ForceMatrix(2):F3},{state.ForceMatrix(3):F3}," &
                          $"{state.ForceMatrix(4):F3},{state.ForceMatrix(5):F3}")
        Console.WriteLine("------------------")

        Dim saved As Boolean = False
        Dim errorMessage As String = Nothing

        Try
            saved = m_view.SaveSnapshot(path)
        Catch ex As Exception
            errorMessage = ex.Message
        End Try

        Console.WriteLine(If(saved,
                            $"快照已保存: {path}",
                            $"快照保存失败: {If(errorMessage, "SaveSnapshot 返回 False")}"))

        Close()
    End Sub

    ' ==================================================================
    ' 状态栏
    ' ==================================================================

    Private Sub UpdateStatus()
        Dim now As Long = Environment.TickCount64

        If now - m_lastStatusUpdate < 250 Then Return

        m_lastStatusUpdate = now

        If m_simulation Is Nothing Then
            m_statusLabel.Text = "正在初始化…"
            Return
        End If

        Dim integrator As Physics.TensorIntegrator = m_simulation.Integrator
        Dim state As Physics.ParticleState = m_simulation.State

        Dim text As String =
            $"FPS {m_fps,5:F1} · N={state.ParticleCount:N0} · T={m_parameters.ParticleTypes} · " &
            $"dt={m_parameters.Dt:F4} · 步 {m_simulation.StepCount:N0} · " &
            $"ρ∈[{integrator.DensityMin:F2},{integrator.DensityMax:F2}] μ={integrator.DensityMean:F2} · " &
            $"|v|max={integrator.MaxSpeed:F1} · " &
            $"求解 {m_simulation.LastSolverMilliseconds:F2}ms + 张量 {m_simulation.LastIntegrateMilliseconds:F2}ms" &
            $"({integrator.OperationCount} ops) · {m_backend.StatusLabel()}"

        If m_backend.DeviceBytes > 0 Then
            text &= $" · 显存 {m_backend.DeviceBytes / 1048576.0:F1}MB"
        End If

        If m_view IsNot Nothing AndAlso m_view.SceneRenderer IsNot Nothing Then
            text &= $" · 上传 {m_view.SceneRenderer.LastUploadMs:F2}ms"
        End If

        If Not m_parametersValid Then
            text &= "  ⚠ 排斥半径必须小于交互半径（已暂停推进）"
        End If

        If Not String.IsNullOrEmpty(m_statusSuffix) Then
            text &= "  ⚠ " & m_statusSuffix
        End If

        m_statusLabel.Text = text
    End Sub

    ''' <summary>数组前 <paramref name="count"/> 个元素的绝对值最大值（诊断用）</summary>
    Private Shared Function MaxAbs(values As Single(), count As Integer) As Single
        Dim result As Single = 0.0F

        For i As Integer = 0 To System.Math.Min(count, values.Length) - 1
            Dim v As Single = System.Math.Abs(values(i))
            If v > result Then result = v
        Next

        Return result
    End Function

    Private Sub SetStatus(message As String)
        If m_statusLabel Is Nothing Then Return

        m_statusLabel.Text = message
    End Sub

    ' ==================================================================
    ' 快捷键
    ' ==================================================================

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        MyBase.OnKeyDown(e)

        Select Case e.KeyCode
            Case Keys.Escape
                If FormBorderStyle = FormBorderStyle.None Then
                    OnToggleFullscreen(Me, EventArgs.Empty)
                Else
                    Close()
                End If

            Case Keys.Space
                OnToggleRunning(Me, EventArgs.Empty)

            Case Keys.R
                OnResetClicked(Me, EventArgs.Empty)

            Case Keys.S
                OnSingleStep(Me, EventArgs.Empty)

            Case Keys.C
                OnRecenterCamera(Me, EventArgs.Empty)

            Case Keys.H
                If m_settingsHost IsNot Nothing Then
                    m_settingsHost.Visible = Not m_settingsHost.Visible
                End If

            Case Keys.F11
                OnToggleFullscreen(Me, EventArgs.Empty)

            Case Else
                Return
        End Select

        e.Handled = True
        e.SuppressKeyPress = True
    End Sub

End Class
