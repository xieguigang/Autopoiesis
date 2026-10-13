Imports System.Diagnostics
Imports System.Drawing
Imports System.Windows.Forms

''' <summary>
''' 主窗体：粒子生命（Particle Life）实时模拟视窗。
''' </summary>
''' <remarks>
''' <para>
''' 布局分三块：顶部标题带（应用名 / 后端徽章 / 运行控制）、中部全幅的粒子宇宙画布
''' （DirectX，随窗体大小自动铺满并刷新渲染目标），以及底部状态栏。参数实验台被抽取到
''' 独立的 <see cref="FormParameters"/> 工具窗口中。
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
    ' 主题（亮色，参考 PanelPlayerLeft 浅色 UI）
    ' ------------------------------------------------------------------
    Private Shared ReadOnly ThemeBackground As Color = Color.White
    Private Shared ReadOnly ThemeCanvasTop As Color = Color.FromArgb(249, 250, 252)
    Private Shared ReadOnly ThemePanel As Color = Color.FromArgb(248, 250, 252)
    Private Shared ReadOnly ThemeText As Color = Color.FromArgb(30, 41, 59)
    Private Shared ReadOnly ThemeMuted As Color = Color.FromArgb(71, 85, 105)
    Private Shared ReadOnly ThemeCyan As Color = Color.FromArgb(14, 165, 233)
    Private Shared ReadOnly ThemeGreen As Color = Color.FromArgb(16, 185, 129)
    Private Shared ReadOnly ThemeAmber As Color = Color.FromArgb(245, 158, 11)
    Private Shared ReadOnly ThemeRed As Color = Color.FromArgb(239, 68, 68)
    Private Shared ReadOnly ThemeBlue As Color = Color.FromArgb(37, 99, 235)

    ' ------------------------------------------------------------------
    ' 状态
    ' ------------------------------------------------------------------
    Private ReadOnly m_parameters As New Physics.SimulationParameters()

    Private m_backend As Physics.ComputeBackend
    Private m_simulation As Simulation.ParticleLifeSimulation
    Private m_view As Rendering.ParticleCloudView
    Private m_parametersForm As FormParameters

    Private m_running As Boolean = True
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
    Private m_statusLabel As Label
    Private m_titleLabel As Label
    Private m_backendBadge As Label
    Private m_stateBadge As Label
    Private m_buttonPlay As Button
    Private m_buttonStep As Button

    ' ==================================================================
    ' 生命周期
    ' ==================================================================

    Public Sub New()
        MyBase.New()

        InitializeComponent()

        BuildLayout()

        m_parametersForm = New FormParameters(m_parameters, m_view, Me)

        m_fpsWatch = Stopwatch.StartNew()

        m_stepTimer = New Timer With {.Interval = 16}
        AddHandler m_stepTimer.Tick, AddressOf OnStepTick
    End Sub

    ''' <summary>暴露给参数工具窗口：运行态切换</summary>
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property IsRunning As Boolean
        Get
            Return m_running
        End Get
        Set(value As Boolean)
            m_running = value
        End Set
    End Property

    ''' <summary>暴露给参数工具窗口：状态栏附加提示</summary>
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property StatusSuffix As String
        Get
            Return m_statusSuffix
        End Get
        Set(value As String)
            m_statusSuffix = If(value, String.Empty)
        End Set
    End Property

    ''' <summary>暴露给参数工具窗口：参数是否合法</summary>
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property ParametersValid As Boolean
        Get
            Return m_parametersValid
        End Get
        Set(value As Boolean)
            m_parametersValid = value
        End Set
    End Property

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)

        ' 先把工具窗口摆到主窗口右侧再显示
        PositionParametersForm()
        m_parametersForm.Show(Me)

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

        m_parameters.Seed = CInt(m_parametersForm.SeedBox.Value)
        m_parameters.Validate()

        m_simulation = New Simulation.ParticleLifeSimulation(m_parameters, m_backend)
        m_simulation.Rebuild(regenerateForces:=True, regenerateColors:=True)

        If StartupOptions.Calibrate Then ApplyCalibrationState()

        m_view.Bind(m_parameters)
        m_view.ResetCamera()

        m_parametersForm.AttachSimulation(m_simulation, m_backend)

        m_backendBadge.Text = If(m_backend.IsGpu, "GPU", "CPU")
        m_backendBadge.ForeColor = If(m_backend.IsGpu, ThemeCyan, ThemeAmber)
        m_backendBadge.BackColor = If(m_backend.IsGpu,
                                      Color.FromArgb(224, 242, 254),
                                      Color.FromArgb(254, 243, 219))

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

        m_parametersForm.SliderPointSize.NumericValue = 6.0F
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        m_running = False

        If m_stepTimer IsNot Nothing Then
            m_stepTimer.Stop()
            m_stepTimer.Dispose()
            m_stepTimer = Nothing
        End If

        ' 参数工具窗口是主窗体的 owned form，会随主窗体自动关闭，无需手动 Close
        ' （手动 Close 会触发其 UserClosing 守卫而改为隐藏，反而关不掉）。

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
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))     ' 画布（铺满）
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))     ' 状态栏

        ' ---- 标题带 ----
        m_titleBar = BuildTitleBar()

        ' ---- 主体：仅 DirectX 画布，随窗体大小自动铺满 ----
        m_view = New Rendering.ParticleCloudView With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0),
            .BackColor = ThemeCanvasTop
        }

        ' ---- 状态栏 ----
        Dim statusBar As New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = ThemePanel,
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
        root.Controls.Add(m_view, 0, 1)
        root.Controls.Add(statusBar, 0, 2)

        Controls.Add(root)

        ResumeLayout(True)
    End Sub

    Private Function BuildTitleBar() As Panel
        Dim bar As New Panel With {
            .Dock = DockStyle.Fill,
            .BackColor = ThemePanel,
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
            .BackColor = Color.FromArgb(224, 242, 254),
            .Font = New Font("Consolas", 9.0F, FontStyle.Bold)
        }

        m_stateBadge = New Label With {
            .Text = "▶ 运行中",
            .AutoSize = False,
            .Size = New Size(84, 22),
            .TextAlign = ContentAlignment.MiddleCenter,
            .ForeColor = ThemeGreen,
            .BackColor = Color.FromArgb(209, 250, 229),
            .Font = New Font("Microsoft YaHei UI", 8.75F)
        }

        m_buttonPlay = CreateToolButton("⏸ 暂停", 96)
        m_buttonStep = CreateToolButton("⏭ 单步", 84)
        Dim buttonReset As Button = CreateToolButton("↺ 重置", 84)
        Dim buttonCamera As Button = CreateToolButton("⌖ 视角", 84)
        Dim buttonFullscreen As Button = CreateToolButton("⛶ 全屏", 84)

        AddHandler m_buttonPlay.Click, AddressOf OnToggleRunning
        AddHandler m_buttonStep.Click, AddressOf OnSingleStep
        AddHandler buttonReset.Click, AddressOf OnTitleReset
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

    Private Function CreateToolButton(caption As String, width As Integer) As Button
        Dim button As New Button With {
            .Text = caption,
            .AutoSize = False,
            .Size = New Size(width, 26),
            .FlatStyle = FlatStyle.Flat,
            .ForeColor = ThemeText,
            .BackColor = Color.White,
            .Font = New Font("Microsoft YaHei UI", 8.75F),
            .TabStop = False,
            .Cursor = Cursors.Hand
        }

        button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225)
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249)

        Return button
    End Function

    ' ==================================================================
    ' 运行控制（标题栏）
    ' ==================================================================

    Private Sub OnTitleReset(sender As Object, e As EventArgs)
        If m_parametersForm IsNot Nothing Then m_parametersForm.RequestReset()
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

        PositionParametersForm()
    End Sub

    Public Sub UpdatePlayState()
        If m_running Then
            m_stateBadge.Text = "▶ 运行中"
            m_stateBadge.ForeColor = ThemeGreen
            m_stateBadge.BackColor = Color.FromArgb(209, 250, 229)
            m_buttonPlay.Text = "⏸ 暂停"
        Else
            m_stateBadge.Text = "⏸ 已暂停"
            m_stateBadge.ForeColor = ThemeAmber
            m_stateBadge.BackColor = Color.FromArgb(254, 243, 219)
            m_buttonPlay.Text = "▶ 继续"
        End If
    End Sub

    ''' <summary>把参数工具窗口贴到主窗口右侧，随主窗口移动</summary>
    Private Sub PositionParametersForm()
        If m_parametersForm Is Nothing OrElse m_parametersForm.IsDisposed Then Return

        m_parametersForm.Location = New Point(Location.X + Width, Location.Y)
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
                OnTitleReset(Me, EventArgs.Empty)

            Case Keys.S
                OnSingleStep(Me, EventArgs.Empty)

            Case Keys.C
                OnRecenterCamera(Me, EventArgs.Empty)

            Case Keys.H
                If m_parametersForm IsNot Nothing Then
                    m_parametersForm.Visible = Not m_parametersForm.Visible
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
