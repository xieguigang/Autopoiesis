Imports System.ComponentModel
Imports System.Drawing
Imports System.Windows.Forms
Imports Microsoft.VisualBasic.Drawing.DirectX
Imports Microsoft.VisualBasic.Drawing.DirectX.Scene3D
Imports Img3D = Microsoft.VisualBasic.Imaging.Drawing3D

Namespace Rendering

    ''' <summary>
    ''' 粒子生命的主视窗：把十万级粒子以 GPU 实例化点云呈现，并提供原版的平移 / 缩放操作。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' <b>为什么用外部实例云而不是 <c>Scene.LoadPointCloud</c>：</b>
    ''' <c>Scene</c> 每次装填点云都会 <c>Version++</c>，而 GPU 后端用
    ''' <c>"{scene.Version}|{options.GeometrySignature()}"</c> 作几何缓存键 —— 逐帧重建
    ''' 会把整块实例缓冲连同顶点布局一起扔掉重建。因此动态粒子云走
    ''' <c>Direct3D11SceneRenderer.UploadInstances</c>：场景只保留一份<b>静态</b>几何，
    ''' 每帧只重填一个实例缓冲。
    ''' </para>
    ''' <para>
    ''' <b>场景里那条"哑"线段：</b><c>DxScene3DCanvas.OnSceneRender</c> 只在
    ''' <c>Scene.HasData</c>（面 / 点 / 线任一非空）时才调用渲染后端。这里放一条
    ''' 退化的线段把 <c>HasData</c> 顶起来，同时 <c>ShowConnections = False</c>
    ''' 保证它永远不会被画出来（连线的绘制被该开关直接短路）。
    ''' </para>
    ''' <para>
    ''' <b>精确正交俯视：</b><c>Camera</c> 没有正交模式，投影恒为
    ''' <c>factor = fov / (viewDistance + z)</c>，而且 <c>SceneTransform</c> 会把
    ''' <c>FieldOfView</c> 取整（<c>CInt(...)</c>）。所以这里固定
    ''' <c>FieldOfView = 1024</c>（整数、精确可表示），把所有粒子放在 <c>z = 0</c>，
    ''' 用 <c>ViewDistance = 1024 / zoom</c> 得到 <c>factor ≡ zoom</c>；
    ''' 屏幕平移交给 <c>Camera.Offset</c>。注意 GPU 管线的屏幕 y 轴与世界 y 轴反向
    ''' （投影矩阵的 <c>M22 = -2·fov/height</c>），因此 y 方向的平移量取相反符号。
    ''' </para>
    ''' </remarks>
    Public Class ParticleCloudView
        Inherits DxScene3DCanvas

        ''' <summary>
        ''' 固定的视场角（像素焦距）。
        ''' </summary>
        ''' <remarks>
        ''' 必须是整数：<c>SceneTransform.Create</c> 会执行 <c>CInt(camera.FieldOfView)</c>
        ''' 以复刻 CPU 画师把 fov 传给整数参数的行为。1024 既能被 float32 精确表示，
        ''' 又能让 <c>ViewDistance = 1024 / zoom</c> 在整个缩放范围内都落在舒适区间。
        ''' </remarks>
        Public Const FixedFieldOfView As Single = 1024.0F

        ''' <summary>滚轮每一格（120）的缩放倍率</summary>
        Private Const WheelZoomStep As Single = 1.25F

        ''' <summary>拖拽惯性的衰减系数</summary>
        Private Const InertiaDecay As Single = 0.82F

        ''' <summary>GPU 管线不可用时，软件回退路径允许绘制的最大点数</summary>
        Private Const MaxSoftwareFallbackPoints As Integer = 20000

        Private ReadOnly m_instances As New ParticleInstanceBuffer()
        Private ReadOnly m_renderer As Direct3D11SceneRenderer
        Private ReadOnly m_scenePoints As New List(Of PointCloudPoint)()

        Private m_parameters As Physics.SimulationParameters
        Private m_zoom As Single = 0.35F
        Private m_centerX As Single = 1280.0F
        Private m_centerY As Single = 720.0F
        Private m_pointPixelSize As Integer = 2
        Private m_dragging As Boolean
        Private m_lastMouse As Point
        Private m_inertiaX As Single
        Private m_inertiaY As Single
        Private m_scenePointCloudFallback As Boolean

        Public Sub New()
            MyBase.New()

            AutoClear = True
            BackgroundColor = UI.Theme.CanvasBackground
            DoubleBuffered = False

            ' 逐点嵌入色 + 屏幕对齐方形光点；关闭一切与模型展示相关的叠加层
            Options.UseEmbeddedColor = True
            Options.PointShape = ScenePointShape.Square
            Options.PointSize = m_pointPixelSize
            Options.PointAlpha = 255
            Options.ShowGround = False
            Options.ShowConnections = False
            Options.ShowBackgroundGrid = False
            Options.MultisampleCount = 1
            Options.Mode = SceneRenderMode.PointCloud

            ' 快捷键由主窗体统一处理，避免与画布自带的快捷键冲突
            EnableKeyboardShortcuts = False

            ' 退化线段：仅用于把 Scene.HasData 顶起来（见类注释）
            Scene.LoadLineSegments(New LineSegment() {
                New LineSegment(New Img3D.Point3D(0.0F, 0.0F, 0.0F),
                                New Img3D.Point3D(0.0F, 0.0F, 0.0F))
            })

            m_renderer = TryCast(Renderer, Direct3D11SceneRenderer)
        End Sub

        ''' <summary>实际用于绘制的 D3D11 后端（GPU 管线不可用时为 <c>Nothing</c>）</summary>
        Public ReadOnly Property SceneRenderer As Direct3D11SceneRenderer
            Get
                Return m_renderer
            End Get
        End Property

        ''' <summary>实例缓冲（诊断用：检查逐点位置与打包色是否写对）</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public ReadOnly Property InstanceBuffer As ParticleInstanceBuffer
            Get
                Return m_instances
            End Get
        End Property

        ''' <summary>把当前帧的相机与渲染选项摘要成多行诊断文本</summary>
        Public Function DescribeFrame() As String
            Dim camera As Img3D.Camera = Controller.Camera
            Dim report As New System.Text.StringBuilder()

            report.AppendLine($"画布        : {ClientSize.Width} x {ClientSize.Height}")
            report.AppendLine($"缩放        : zoom={m_zoom:F5}（像素/世界单位）")
            report.AppendLine($"相机        : fov={camera.FieldOfView} vd={camera.ViewDistance:F3} " &
                              $"angle=({camera.AngleX},{camera.AngleY},{camera.AngleZ}) " &
                              $"offset=({camera.Offset.X:F2}, {camera.Offset.Y:F2})")
            report.AppendLine($"世界中心    : ({m_centerX:F2}, {m_centerY:F2})")
            report.AppendLine($"等效 factor : {camera.FieldOfView / System.Math.Max(camera.ViewDistance, 0.001F):F5}")
            report.AppendLine($"管线        : {PipelineDiagnostics}")
            report.AppendLine($"选项        : embeddedColor={Options.UseEmbeddedColor} pointSize={Options.PointSize} " &
                              $"shape={Options.PointShape} mode={Options.Mode} msaa={Options.MultisampleCount}")
            report.AppendLine($"场景        : surfaces={Scene.SurfaceCount} points={Scene.PointCount} lines={Scene.LineCount}")

            If m_renderer IsNot Nothing Then
                report.AppendLine($"外部实例云  : count={m_renderer.ExternalInstanceCount} " &
                                  $"capacity={m_renderer.ExternalInstanceCapacity} gpu={m_renderer.IsGpuAvailable}")
                report.AppendLine($"设备        : {m_renderer.DeviceDiagnostics}")
                report.AppendLine($"上传耗时    : {m_renderer.LastUploadMs:F3} ms")
            End If

            If m_instances.Capacity > 0 Then
                Dim data As Single() = m_instances.Data
                Dim packed As Integer = BitConverter.SingleToInt32Bits(data(7))

                report.AppendLine($"实例[0]     : pos=({data(0):F2},{data(1):F2},{data(2):F2}) " &
                                  $"size={data(6):F2} packedColor=0x{packed:X8}")
                report.AppendLine($"实例[1]     : pos=({data(8):F2},{data(9):F2}) size={data(14):F2} " &
                                  $"packedColor=0x{BitConverter.SingleToInt32Bits(data(15)):X8}")
            End If

            Return report.ToString()
        End Function

        ''' <summary>缩放：屏幕像素 / 世界单位</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public Property Zoom As Single
            Get
                Return m_zoom
            End Get
            Set(value As Single)
                m_zoom = Clamp(value,
                               Physics.SimulationParameters.MinCameraZoom,
                               Physics.SimulationParameters.MaxCameraZoom)
            End Set
        End Property

        ''' <summary>视口中心对应的世界坐标 x</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public Property CenterX As Single
            Get
                Return m_centerX
            End Get
            Set(value As Single)
                m_centerX = value
            End Set
        End Property

        ''' <summary>视口中心对应的世界坐标 y</summary>
        <Browsable(False),
         DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
        Public Property CenterY As Single
            Get
                Return m_centerY
            End Get
            Set(value As Single)
                m_centerY = value
            End Set
        End Property

        ''' <summary>单个粒子的像素尺寸（逐点尺寸因子固定为 1）</summary>
        <DefaultValue(2)>
        Public Property PointPixelSize As Integer
            Get
                Return m_pointPixelSize
            End Get
            Set(value As Integer)
                m_pointPixelSize = System.Math.Max(1, System.Math.Min(32, value))
                Options.PointSize = m_pointPixelSize
            End Set
        End Property

        ''' <summary>
        ''' GPU 实例化管线是否真的可用。
        ''' </summary>
        ''' <remarks>
        ''' 该值只有在画布至少渲染过一帧之后才有意义；由主窗体在首帧之后探测，
        ''' 必要时调用 <see cref="EnableSoftwareFallback"/> 切换到场景点云路径。
        ''' </remarks>
        Public ReadOnly Property IsInstancedPipelineActive As Boolean
            Get
                If m_renderer Is Nothing Then Return False

                Return IsGpuPipelineActive
            End Get
        End Property

        ''' <summary>GPU 管线不可用时的原因描述（可用时为空）</summary>
        Public ReadOnly Property PipelineDiagnostics As String
            Get
                Dim reason As String = RendererFallbackReason

                If IsGpuPipelineActive Then
                    Return $"GPU 实例化管线已启用（MSAA x{ActiveMultisampleCount}）"
                End If

                If String.IsNullOrWhiteSpace(reason) Then reason = "未知原因"

                Return $"GPU 管线不可用（{reason}），已回退到场景点云绘制"
            End Get
        End Property

        ''' <summary>绑定运行参数（世界尺寸）</summary>
        Public Sub Bind(parameters As Physics.SimulationParameters)
            m_parameters = parameters
        End Sub

        ' ==================================================================
        ' 相机
        ' ==================================================================

        ''' <summary>把视口缩放到刚好容纳整个环面世界，并居中</summary>
        Public Sub ResetCamera()
            Dim regionWidth As Single = If(m_parameters Is Nothing, 2560.0F, m_parameters.RegionWidth)
            Dim regionHeight As Single = If(m_parameters Is Nothing, 1440.0F, m_parameters.RegionHeight)

            Dim width As Integer = System.Math.Max(ClientSize.Width, 1)
            Dim height As Integer = System.Math.Max(ClientSize.Height, 1)

            Dim fit As Single = System.Math.Min(width / regionWidth, height / regionHeight)

            If fit <= 0.0F Then fit = 0.35F

            ' 留一点边距，避免最外圈的粒子被裁掉
            m_zoom = Clamp(fit * 0.95F,
                           Physics.SimulationParameters.MinCameraZoom,
                           Physics.SimulationParameters.MaxCameraZoom)

            m_centerX = regionWidth * 0.5F
            m_centerY = regionHeight * 0.5F

            m_inertiaX = 0.0F
            m_inertiaY = 0.0F
        End Sub

        ''' <summary>按屏幕像素平移视图（内容跟随光标，"抓取"手感）</summary>
        Public Sub PanByPixels(deltaX As Single, deltaY As Single)
            If m_zoom <= 0.0F Then Return

            m_centerX -= deltaX / m_zoom
            m_centerY -= deltaY / m_zoom
        End Sub

        ''' <summary>
        ''' 以屏幕上的某个锚点为中心缩放（滚轮手感：光标下的位置保持不动）。
        ''' </summary>
        Public Sub ZoomAt(anchor As Point, factor As Single)
            If factor <= 0.0F Then Return

            Dim previous As Single = m_zoom
            Zoom = m_zoom * factor

            If m_zoom = previous Then Return

            ' 锚点下方的世界坐标（注意 GPU 管线里屏幕 y 与世界 y 反向）
            Dim offsetX As Single = anchor.X - ClientSize.Width * 0.5F
            Dim offsetY As Single = anchor.Y - ClientSize.Height * 0.5F

            Dim worldX As Single = m_centerX + offsetX / previous
            Dim worldY As Single = m_centerY - offsetY / previous

            m_centerX = worldX - offsetX / m_zoom
            m_centerY = worldY + offsetY / m_zoom
        End Sub

        ''' <summary>
        ''' 推进拖拽惯性（由主窗体的帧定时器调用）。
        ''' </summary>
        Public Sub AdvanceInertia()
            If m_dragging Then Return

            If System.Math.Abs(m_inertiaX) < 0.05F AndAlso System.Math.Abs(m_inertiaY) < 0.05F Then
                m_inertiaX = 0.0F
                m_inertiaY = 0.0F
                Return
            End If

            PanByPixels(m_inertiaX, m_inertiaY)

            m_inertiaX *= InertiaDecay
            m_inertiaY *= InertiaDecay
        End Sub

        Private Sub ApplyCamera()
            Dim camera As Img3D.Camera = Controller.Camera

            ' 角度归零 ⇒ 旋转矩阵是单位阵，z = 0 的粒子不受旋转影响
            camera.AngleX = 0.0F
            camera.AngleY = 0.0F
            camera.AngleZ = 0.0F

            ' factor = fov / (viewDistance + z)，z ≡ 0 ⇒ factor ≡ 1024 / (1024/zoom) = zoom
            camera.FieldOfView = FixedFieldOfView
            camera.ViewDistance = FixedFieldOfView / System.Math.Max(m_zoom, 1.0E-06F)

            ' 屏幕平移。x 轴：screenX = w/2 + zoom*(x - centerX)
            '             y 轴：screenY = h/2 - zoom*(y - centerY)（GPU 管线 y 反向）
            camera.Offset = New PointF(-m_centerX * m_zoom, -m_centerY * m_zoom)
            camera.Screen = ClientSize
        End Sub

        ' ==================================================================
        ' 每帧提交
        ' ==================================================================

        ''' <summary>
        ''' 用最新状态刷新粒子云并请求重绘。必须在 UI 线程调用。
        ''' </summary>
        Public Sub RenderState(state As Physics.ParticleState)
            If state Is Nothing OrElse state.ParticleCount < 1 Then Return

            ApplyCamera()

            If m_scenePointCloudFallback Then
                FillScenePointCloud(state)
            Else
                FillInstanceCloud(state)
            End If

            Invalidate()
        End Sub

        Private Sub FillInstanceCloud(state As Physics.ParticleState)
            m_instances.Fill(state, ParticleInstanceBuffer.DefaultSizeScale)

            If m_renderer Is Nothing Then Return

            m_renderer.EnsureInstanceCapacity(state.ParticleCount)
            m_renderer.UploadInstances(m_instances.Data, state.ParticleCount)
        End Sub

        ''' <summary>
        ''' 切换到软件回退路径：把粒子写进 <c>Scene</c> 自身的点云。
        ''' </summary>
        ''' <remarks>
        ''' 逐帧装填点云会让 GPU 后端重建整块几何缓冲，因此这条路径只用于
        ''' GPU 实例化管线不可用的机器，并且限制点数。逐点色由
        ''' <c>PointCloudPoint.Color</c>（HTML 字符串）承载，
        ''' <c>UseEmbeddedColor = True</c> 时会直接返回。
        ''' </remarks>
        Public Sub EnableSoftwareFallback()
            If m_scenePointCloudFallback Then Return

            m_scenePointCloudFallback = True

            If m_renderer IsNot Nothing Then
                m_renderer.ClearInstances()
            End If
        End Sub

        Private Sub FillScenePointCloud(state As Physics.ParticleState)
            Dim count As Integer = System.Math.Min(state.ParticleCount, MaxSoftwareFallbackPoints)

            m_scenePoints.Clear()

            For i As Integer = 0 To count - 1
                Dim color As Color = state.ColorOf(state.Types(i))

                m_scenePoints.Add(New PointCloudPoint(
                    state.PosX(i), state.PosY(i), 0.0R,
                    0.0R,
                    $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                    1.0R))
            Next

            Scene.LoadPointCloud(m_scenePoints)
        End Sub

        ' ==================================================================
        ' 输入：左键拖拽平移 + 滚轮以光标为锚缩放
        ' ==================================================================

        ''' <remarks>
        ''' 刻意<b>不</b>调用 <c>MyBase</c>：基类会把鼠标事件喂给
        ''' <c>OrbitCameraController</c>（左键旋转、右键平移、滚轮改变视距），
        ''' 那套球面轨道交互与二维正交俯视视图是冲突的。
        ''' </remarks>
        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                m_dragging = True
                m_lastMouse = New Point(e.X, e.Y)
                m_inertiaX = 0.0F
                m_inertiaY = 0.0F
                Cursor = Cursors.SizeAll
            End If
        End Sub

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            If Not m_dragging Then Return

            Dim deltaX As Single = e.X - m_lastMouse.X
            Dim deltaY As Single = e.Y - m_lastMouse.Y

            m_lastMouse = New Point(e.X, e.Y)

            PanByPixels(deltaX, deltaY)

            ' 记录瞬时速度，松开后作为惯性继续滑动
            m_inertiaX = deltaX
            m_inertiaY = deltaY
        End Sub

        Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
            If e.Button <> MouseButtons.Left Then Return

            m_dragging = False
            Cursor = Cursors.Default
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            ' 不调用 MyBase：基类会把滚轮解释成 ViewDistance 的变化（球面缩放）
            Dim notches As Single = e.Delta / 120.0F
            Dim factor As Single = CSng(System.Math.Pow(WheelZoomStep, notches))

            ZoomAt(New Point(e.X, e.Y), factor)
        End Sub

        Private Shared Function Clamp(value As Single, low As Single, high As Single) As Single
            If value < low Then Return low
            If value > high Then Return high
            Return value
        End Function

    End Class

End Namespace
