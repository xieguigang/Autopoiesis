Imports ILCudaRuntime = Microsoft.VisualBasic.Computing.ILCuda.Runtime

Namespace Physics

    ''' <summary>
    ''' 粒子对力求解器的 CUDA 实现：把 O(N²) 的两遍主循环交给手写内核
    ''' <c>autopoiesisPairForceF32</c>（见 <c>Kernels/autopoiesis_physics.cu</c>）。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 这是唯一能支撑十万粒子实时的路径：滑杆上限 317² ≈ 10 万粒子意味着每帧约
    ''' 10¹⁰ 次粒子对交互，只有全 GPU 暴力并行才可能跑得动。
    ''' </para>
    ''' <para>
    ''' 数据传输设计（目标是每帧零托管分配）：
    ''' </para>
    ''' <list type="bullet">
    ''' <item>输入用同步 <c>Write</c>（内部直接固定托管数组，不产生额外分配）；</item>
    ''' <item>输出用<b>专用</b>页锁定中转区 + <c>ReadAsync</c> + 一次
    ''' <c>engine.Synchronize()</c> + <c>ReadInto</c>，避免 <c>Read()</c> 每帧
    ''' 分配三个 N 长度数组（N=10⁵ 时是 1.2 MB/帧的 LOH 垃圾）；</item>
    ''' <item>粒子类型与交互矩阵只在「静态状态变化」后上传一次。</item>
    ''' </list>
    ''' <para>
    ''' 所有拷贝与内核启动都走 NULL 流，因此彼此的先后顺序由流本身保证；再加上输入
    ''' 是同步拷贝，输出拷贝后紧跟一次上下文同步，主机端读到的一定是本帧的结果。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class CudaPairwiseSolver
        Implements IPairwiseForceSolver

        ''' <summary>内核符号名（与 .cu 中的 <c>extern "C" __global__</c> 名称严格一致）</summary>
        Public Const KernelSymbol As String = "autopoiesisPairForceF32"

        ''' <summary>启动块尺寸；内核是 grid-stride 循环，因此块尺寸只影响占用率</summary>
        Public Const BlockSize As Integer = 256

        Private ReadOnly m_engine As ILCudaRuntime.CudaEngine
        Private ReadOnly m_kernel As ILCudaRuntime.CudaKernel

        ' ---- 设备缓冲 ----
        Private m_posX As ILCudaRuntime.DeviceBuffer(Of Single)
        Private m_posY As ILCudaRuntime.DeviceBuffer(Of Single)
        Private m_particleTypes As ILCudaRuntime.DeviceBuffer(Of Integer)
        Private m_forceMat As ILCudaRuntime.DeviceBuffer(Of Single)
        Private m_forceX As ILCudaRuntime.DeviceBuffer(Of Single)
        Private m_forceY As ILCudaRuntime.DeviceBuffer(Of Single)
        Private m_density As ILCudaRuntime.DeviceBuffer(Of Single)

        ' ---- 页锁定中转区（每帧只被一个拷贝使用，互不干扰） ----
        Private m_stageForceX As ILCudaRuntime.PinnedHostBuffer(Of Single)
        Private m_stageForceY As ILCudaRuntime.PinnedHostBuffer(Of Single)
        Private m_stageDensity As ILCudaRuntime.PinnedHostBuffer(Of Single)

        Private m_capacity As Integer = -1
        Private m_staticStateDirty As Boolean = True
        Private m_disposed As Boolean = False

        ''' <summary>
        ''' 绑定到既有引擎与已编译好的内核。
        ''' </summary>
        ''' <param name="engine">CUDA 引擎（必须与 TensorF 的 GPU 后端共用同一个）</param>
        ''' <exception cref="InvalidOperationException">内核符号不在当前镜像中</exception>
        Public Sub New(engine As ILCudaRuntime.CudaEngine)
            If engine Is Nothing Then Throw New ArgumentNullException(NameOf(engine))

            m_engine = engine

            Dim kernel As ILCudaRuntime.CudaKernel = Nothing

            If Not engine.TryGetKernel(KernelSymbol, kernel) Then
                Throw New InvalidOperationException(
                    $"CUDA 镜像中找不到内核 '{KernelSymbol}'。" &
                    "请确认 Kernels/autopoiesis_physics.cu 已作为内嵌资源打包，" &
                    "并且在 CudaEngine.TryCreate 之前调用过 KernelSources.Register。")
            End If

            m_kernel = kernel
        End Sub

        Public ReadOnly Property Name As String Implements IPairwiseForceSolver.Name
            Get
                Return "CUDA-F32"
            End Get
        End Property

        Public ReadOnly Property IsGpu As Boolean Implements IPairwiseForceSolver.IsGpu
            Get
                Return True
            End Get
        End Property

        ''' <summary>当前分配的显存总量（字节），用于状态栏诊断</summary>
        Public ReadOnly Property DeviceBytes As Long
            Get
                If m_capacity <= 0 Then Return 0

                Dim scalars As Long = CLng(m_capacity) * 4L * 4L      ' posX/posY/forceX/forceY
                Dim densityBytes As Long = CLng(m_capacity) * 4L      ' density
                Dim typeBytes As Long = CLng(m_capacity) * 4L         ' types
                Dim matrixBytes As Long = SimulationParameters.ForceMatrixStride *
                                          SimulationParameters.ForceMatrixStride * 4L
                Dim pinned As Long = CLng(m_capacity) * 4L * 3L       ' 三个页锁定中转区

                Return scalars + densityBytes + typeBytes + matrixBytes + pinned
            End Get
        End Property

        ''' <summary>设备描述（一次性诊断信息）</summary>
        Public Function DescribeDevice() As String
            Try
                Return m_engine.Device.ToString()
            Catch ex As Exception
                Return $"<设备信息不可用: {ex.Message}>"
            End Try
        End Function

        Public Sub Invalidate() Implements IPairwiseForceSolver.Invalidate
            m_staticStateDirty = True
        End Sub

        Public Sub ComputeForces(state As ParticleState, params As SimulationParameters) Implements IPairwiseForceSolver.ComputeForces
            ThrowIfDisposed()

            Dim n As Integer = state.ParticleCount

            If n < 2 Then Return

            EnsureCapacity(n)

            ' ---- 上传：位置（每帧） ----
            m_posX.Write(state.PosX)
            m_posY.Write(state.PosY)

            ' ---- 上传：类型与交互矩阵（仅静态状态变化时） ----
            If m_staticStateDirty Then
                m_particleTypes.Write(state.Types)
                m_forceMat.Write(state.ForceMatrix)
                m_staticStateDirty = False
            End If

            ' ---- 启动内核 ----
            Dim scalars As KernelScalars = params.CreateKernelScalars()

            m_kernel.Launch1D(
                n, BlockSize, 0,
                m_posX,                                     ' const float* posX
                m_posY,                                     ' const float* posY
                m_particleTypes,                            ' const int*   types
                m_forceMat,                                 ' const float* forceMat
                m_forceX,                                   ' float*       forceX
                m_forceY,                                   ' float*       forceY
                m_density,                                  ' float*       density
                n,                                          ' const int    n
                SimulationParameters.ForceMatrixStride,     ' const int    forceStride
                scalars.RegionWidth,                        ' const float  regionW
                scalars.RegionHeight,                       ' const float  regionH
                scalars.RepulsionRadius,                    ' const float  repulsionRadius
                scalars.InteractionRadius,                  ' const float  interactionRadius
                scalars.DensityLimit)                       ' const float  densityLimit

            ' ---- 回读：异步拷贝到页锁定区，同步后拷进宿主数组 ----
            m_forceX.ReadAsync(m_stageForceX)
            m_forceY.ReadAsync(m_stageForceY)
            m_density.ReadAsync(m_stageDensity)

            m_engine.Synchronize()

            m_stageForceX.ReadInto(state.ForceX)
            m_stageForceY.ReadInto(state.ForceY)
            m_stageDensity.ReadInto(state.Density)
        End Sub

        ' ------------------------------------------------------------------
        ' 缓冲管理
        ' ------------------------------------------------------------------

        Private Sub EnsureCapacity(n As Integer)
            If m_capacity = n Then Return

            DisposeBuffers()

            m_posX = New ILCudaRuntime.DeviceBuffer(Of Single)(n)
            m_posY = New ILCudaRuntime.DeviceBuffer(Of Single)(n)
            m_forceX = New ILCudaRuntime.DeviceBuffer(Of Single)(n)
            m_forceY = New ILCudaRuntime.DeviceBuffer(Of Single)(n)
            m_density = New ILCudaRuntime.DeviceBuffer(Of Single)(n)
            m_particleTypes = New ILCudaRuntime.DeviceBuffer(Of Integer)(n)

            Dim matrixSize As Integer = SimulationParameters.ForceMatrixStride * SimulationParameters.ForceMatrixStride
            m_forceMat = New ILCudaRuntime.DeviceBuffer(Of Single)(matrixSize)

            m_stageForceX = New ILCudaRuntime.PinnedHostBuffer(Of Single)(n)
            m_stageForceY = New ILCudaRuntime.PinnedHostBuffer(Of Single)(n)
            m_stageDensity = New ILCudaRuntime.PinnedHostBuffer(Of Single)(n)

            m_capacity = n
            m_staticStateDirty = True
        End Sub

        Private Sub DisposeBuffers()
            DisposeOne(m_posX) : m_posX = Nothing
            DisposeOne(m_posY) : m_posY = Nothing
            DisposeOne(m_forceX) : m_forceX = Nothing
            DisposeOne(m_forceY) : m_forceY = Nothing
            DisposeOne(m_density) : m_density = Nothing
            DisposeOne(m_particleTypes) : m_particleTypes = Nothing
            DisposeOne(m_forceMat) : m_forceMat = Nothing
            DisposeOne(m_stageForceX) : m_stageForceX = Nothing
            DisposeOne(m_stageForceY) : m_stageForceY = Nothing
            DisposeOne(m_stageDensity) : m_stageDensity = Nothing

            m_capacity = -1
        End Sub

        Private Shared Sub DisposeOne(resource As IDisposable)
            If resource Is Nothing Then Return

            Try
                resource.Dispose()
            Catch ex As Exception
                ' 释放阶段的失败不应影响其它缓冲的清理
            End Try
        End Sub

        Private Sub ThrowIfDisposed()
            If m_disposed Then Throw New ObjectDisposedException(GetType(CudaPairwiseSolver).Name)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If m_disposed Then Return

            m_disposed = True
            DisposeBuffers()
        End Sub

    End Class

End Namespace
