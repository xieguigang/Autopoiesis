Imports System.Drawing

Namespace Physics

    ''' <summary>
    ''' 粒子生命（Particle Life）模拟的全部运行参数。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 这套参数与 <c>algorithm/readme.md</c> 描述的耗散动力系统一一对应，默认值与
    ''' 参考实现 <c>algorithm/interface.gd</c> / <c>algorithm/particles.gd</c> 保持一致。
    ''' </para>
    ''' <para>
    ''' 世界是一个 <see cref="RegionWidth"/> × <see cref="RegionHeight"/> 的二维环面，
    ''' 时间步长取 5 倍帧间隔（60 FPS 时 ≈ 0.083），见 <see cref="Dt"/>。
    ''' </para>
    ''' </remarks>
    Public Class SimulationParameters

        ''' <summary>
        ''' 力矩阵的行/列步长，恒为 10（与参考实现里 <c>MAX_PARTICLE_TYPES</c> 一致）。
        ''' </summary>
        ''' <remarks>
        ''' 参考实现的交互矩阵始终按 10×10 分配（<c>forces.clear()</c> 后压入
        ''' <c>MAX_PARTICLE_TYPES ** 2</c> 项），索引方式是 <c>t_a + t_b * 10</c>；
        ''' 类型数滑杆只影响界面如何映射按钮到矩阵元素，不改变步长。
        ''' </remarks>
        Public Const ForceMatrixStride As Integer = 10

        ''' <summary>粒子类型的数量上限</summary>
        Public Const MaxParticleTypes As Integer = ForceMatrixStride

        ' ------------------------------------------------------------------
        ' 世界
        ' ------------------------------------------------------------------

        ''' <summary>世界宽度（世界单位，同时是环面的水平周期）</summary>
        Public Property RegionWidth As Single = 2560.0F

        ''' <summary>世界高度（世界单位，同时是环面的垂直周期）</summary>
        Public Property RegionHeight As Single = 1440.0F

        ' ------------------------------------------------------------------
        ' 种群
        ' ------------------------------------------------------------------

        ''' <summary>粒子数量</summary>
        Public Property ParticleCount As Integer = 4096

        ''' <summary>粒子类型数量，有效范围 [1, <see cref="MaxParticleTypes"/>]</summary>
        Public Property ParticleTypes As Integer = 6

        ''' <summary>随机种子（决定撒点位置、类型与力矩阵）</summary>
        Public Property Seed As Integer = 0

        ' ------------------------------------------------------------------
        ' 动力学
        ' ------------------------------------------------------------------

        ''' <summary>绘制半径（世界单位），只影响观感</summary>
        Public Property ParticleRadius As Single = 3.0F

        ''' <summary>阻尼系数 β，<c>v ← β(v + F·Δt)</c>；默认 0.85</summary>
        Public Property Dampening As Single = 0.85F

        ''' <summary>硬核排斥半径 r（世界单位），默认 20</summary>
        Public Property RepulsionRadius As Single = 20.0F

        ''' <summary>交互半径 R（世界单位），必须大于 <see cref="RepulsionRadius"/>；默认 80</summary>
        Public Property InteractionRadius As Single = 80.0F

        ''' <summary>密度上限 ρ_limit，超过后吸引力线性衰减；默认 10</summary>
        Public Property DensityLimit As Single = 10.0F

        ''' <summary>
        ''' 时间步长 Δt。
        ''' </summary>
        ''' <remarks>
        ''' 参考实现取 <c>5 × 帧间隔</c>，60 FPS 时约 0.083。这里固定为该常量以保证
        ''' 仿真的可重复性（也让 CPU 参考实现与 CUDA 内核可以逐位对拍）。
        ''' </remarks>
        Public Property Dt As Single = 5.0F / 60.0F

        ' ------------------------------------------------------------------
        ' 相机（正交俯视，见 Rendering/ParticleCloudView.vb）
        ' ------------------------------------------------------------------

        ''' <summary>缩放：屏幕像素 / 世界单位</summary>
        Public Property CameraZoom As Single = 0.35F

        ''' <summary>相机平移（世界单位，环面上无限滚动）</summary>
        Public Property CameraOriginX As Single = 0.0F

        ''' <summary>相机平移（世界单位，环面上无限滚动）</summary>
        Public Property CameraOriginY As Single = 0.0F

        ' ------------------------------------------------------------------
        ' 派生量
        ' ------------------------------------------------------------------

        ''' <summary>力廓形三角形的中点 <c>m = (r + R) / 2</c></summary>
        Public ReadOnly Property ForceMidpoint As Single
            Get
                Return (RepulsionRadius + InteractionRadius) * 0.5F
            End Get
        End Property

        ''' <summary>力廓形三角形的半宽 <c>W/2 = (R - r) / 2</c></summary>
        Public ReadOnly Property ForceHalfWidth As Single
            Get
                Dim w As Single = InteractionRadius - RepulsionRadius
                Return If(w > 0.0F, w * 0.5F, 1.0E-6F)
            End Get
        End Property

        ''' <summary>与后端共享的快照数组（避免每次内核调用都重新装箱参数）</summary>
        Public Function CreateKernelScalars() As KernelScalars
            Return New KernelScalars With {
                .RegionWidth = RegionWidth,
                .RegionHeight = RegionHeight,
                .RepulsionRadius = RepulsionRadius,
                .InteractionRadius = InteractionRadius,
                .DensityLimit = DensityLimit
            }
        End Function

        ''' <summary>
        ''' 把参数夹到有效范围内，并返回被修正的项的说明（无修正时返回空数组）。
        ''' </summary>
        Public Function Validate() As String()
            Dim notes As New List(Of String)

            If RegionWidth <= 0.0F Then
                RegionWidth = 2560.0F
                notes.Add("世界宽度已重置为 2560")
            End If

            If RegionHeight <= 0.0F Then
                RegionHeight = 1440.0F
                notes.Add("世界高度已重置为 1440")
            End If

            If ParticleCount < 2 Then
                ParticleCount = 2
                notes.Add("粒子数下限为 2")
            End If

            If ParticleTypes < 1 Then
                ParticleTypes = 1
                notes.Add("类型数下限为 1")
            ElseIf ParticleTypes > MaxParticleTypes Then
                ParticleTypes = MaxParticleTypes
                notes.Add($"类型数上限为 {MaxParticleTypes}")
            End If

            If ParticleRadius < 0.5F Then
                ParticleRadius = 0.5F
                notes.Add("粒子半径下限为 0.5")
            End If

            If Dampening < 0.0F Then
                Dampening = 0.0F
                notes.Add("阻尼下限为 0")
            ElseIf Dampening > 0.999F Then
                Dampening = 0.999F
                notes.Add("阻尼上限为 0.999")
            End If

            If RepulsionRadius < 0.0F Then
                RepulsionRadius = 0.0F
                notes.Add("排斥半径下限为 0")
            End If

            ' 排斥半径必须严格小于交互半径，否则三角形吸引核的宽度为零
            If InteractionRadius <= RepulsionRadius Then
                InteractionRadius = RepulsionRadius + 1.0F
                notes.Add("交互半径必须大于排斥半径，已自动上调")
            End If

            If DensityLimit < 0.0F Then
                DensityLimit = 0.0F
                notes.Add("密度上限下限为 0")
            End If

            If Dt <= 0.0F Then
                Dt = 5.0F / 60.0F
                notes.Add("时间步长已重置为 5/60")
            End If

            If CameraZoom < MinCameraZoom Then
                CameraZoom = MinCameraZoom
            ElseIf CameraZoom > MaxCameraZoom Then
                CameraZoom = MaxCameraZoom
            End If

            Return notes.ToArray()
        End Function

        ''' <summary>缩放的下限（像素 / 世界单位）</summary>
        Public Const MinCameraZoom As Single = 0.02F

        ''' <summary>缩放的上限（像素 / 世界单位），对应原版 zoom ∈ [1, 10] 的观感上限</summary>
        Public Const MaxCameraZoom As Single = 20.0F

        ''' <summary>创建一份拷贝</summary>
        Public Function Clone() As SimulationParameters
            Return DirectCast(MemberwiseClone(), SimulationParameters)
        End Function

        Public Overrides Function ToString() As String
            Return $"N={ParticleCount} T={ParticleTypes} r={RepulsionRadius:G4} R={InteractionRadius:G4} " &
                   $"beta={Dampening:F3} limit={DensityLimit:G4} dt={Dt:F4}"
        End Function

    End Class

    ''' <summary>
    ''' 传给 GPU 内核的标量参数集合。
    ''' </summary>
    ''' <remarks>
    ''' 单独成类型是为了让 CUDA 求解器可以一次性缓存这组只读标量，
    ''' 而不必在每次启动内核时重新从 <see cref="SimulationParameters"/> 读取。
    ''' </remarks>
    Public Structure KernelScalars

        Public RegionWidth As Single
        Public RegionHeight As Single
        Public RepulsionRadius As Single
        Public InteractionRadius As Single
        Public DensityLimit As Single

    End Structure

End Namespace
