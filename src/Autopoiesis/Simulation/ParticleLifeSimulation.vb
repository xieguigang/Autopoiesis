Imports System.Diagnostics
Imports tf = Microsoft.VisualBasic.MachineLearning.TensorFlow

Namespace Simulation

    ''' <summary>
    ''' 帧编排：把「GPU 粒子对力内核」与「TensorF 张量积分」串成一帧的同步更新。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 一帧的执行顺序严格对应 <c>algorithm/readme.md</c> 第 0 节的显式映射
    ''' <c>(x, v)^{t+1} = Φ[(x, v)^t]</c>：
    ''' </para>
    ''' <list type="number">
    ''' <item><b>两遍粒子对计算</b>（GPU 内核或托管回退）：算出 ρ 与未限幅的 F；
    ''' 结果只写 <c>ForceX/ForceY/Density</c>，不读旧力，因此更新是对旧状态的纯函数；</item>
    ''' <item><b>张量积分</b>（TensorF）：力截断 → 阻尼辛欧拉 → 环面回绕。</item>
    ''' </list>
    ''' <para>
    ''' 原版实现用「双缓冲乒乓」保证所有线程读到同一份完整旧状态；这里的等价做法是
    ''' 求解器只读位置、只写力缓冲——积分发生之后才把新位置写回，因此同样与线程调度
    ''' 顺序无关（GPU 内核内部靠 <c>__restrict__</c> 与「先读后写」的分离保证这一点）。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class ParticleLifeSimulation
        Implements IDisposable

        Private ReadOnly m_parameters As Physics.SimulationParameters
        Private ReadOnly m_state As New Physics.ParticleState()
        Private ReadOnly m_integrator As New Physics.TensorIntegrator()
        Private ReadOnly m_backend As Physics.ComputeBackend
        Private ReadOnly m_stopwatch As New Stopwatch()

        Private m_stepCount As Long
        Private m_lastSolverMs As Double
        Private m_lastIntegrateMs As Double
        Private m_disposed As Boolean

        Public Sub New(parameters As Physics.SimulationParameters, backend As Physics.ComputeBackend)
            If parameters Is Nothing Then Throw New ArgumentNullException(NameOf(parameters))
            If backend Is Nothing Then Throw New ArgumentNullException(NameOf(backend))

            m_parameters = parameters
            m_backend = backend
        End Sub

        ''' <summary>运行参数（滑杆直接写这里）</summary>
        Public ReadOnly Property Parameters As Physics.SimulationParameters
            Get
                Return m_parameters
            End Get
        End Property

        ''' <summary>宿主侧粒子状态</summary>
        Public ReadOnly Property State As Physics.ParticleState
            Get
                Return m_state
            End Get
        End Property

        ''' <summary>张量积分器（携带后端名与统计量）</summary>
        Public ReadOnly Property Integrator As Physics.TensorIntegrator
            Get
                Return m_integrator
            End Get
        End Property

        ''' <summary>计算后端</summary>
        Public ReadOnly Property Backend As Physics.ComputeBackend
            Get
                Return m_backend
            End Get
        End Property

        ''' <summary>已完成的步数</summary>
        Public ReadOnly Property StepCount As Long
            Get
                Return m_stepCount
            End Get
        End Property

        ''' <summary>最近一帧粒子对计算的耗时（毫秒）</summary>
        Public ReadOnly Property LastSolverMilliseconds As Double
            Get
                Return m_lastSolverMs
            End Get
        End Property

        ''' <summary>最近一帧张量积分的耗时（毫秒）</summary>
        Public ReadOnly Property LastIntegrateMilliseconds As Double
            Get
                Return m_lastIntegrateMs
            End Get
        End Property

        ''' <summary>最近一帧的总耗时（毫秒）</summary>
        Public ReadOnly Property LastStepMilliseconds As Double
            Get
                Return m_lastSolverMs + m_lastIntegrateMs
            End Get
        End Property

        ''' <summary>
        ''' 重新生成整个种群（粒子数 / 类型数 / 种子变化，或用户点了重置）。
        ''' </summary>
        ''' <param name="regenerateForces">是否重新随机交互矩阵</param>
        ''' <param name="regenerateColors">是否恢复默认调色板</param>
        Public Sub Rebuild(Optional regenerateForces As Boolean = False,
                           Optional regenerateColors As Boolean = False)
            m_parameters.Validate()
            m_state.Generate(m_parameters, regenerateForces, regenerateColors)
            m_backend.Solver.Invalidate()
            m_stepCount = 0
        End Sub

        ''' <summary>只重新随机交互矩阵（保持粒子布局不变）</summary>
        Public Sub RandomizeForceMatrix()
            m_state.RandomizeForceMatrix(m_parameters.Seed)
            m_backend.Solver.Invalidate()
        End Sub

        ''' <summary>只把交互矩阵归零（系统随即静止）</summary>
        Public Sub ClearForceMatrix()
            m_state.ClearForceMatrix()
            m_backend.Solver.Invalidate()
        End Sub

        ''' <summary>只恢复默认调色板</summary>
        Public Sub RestoreColors()
            System.Array.Copy(Physics.ParticleState.DefaultColors(), m_state.TypeColors, m_state.TypeColors.Length)
        End Sub

        ''' <summary>
        ''' 推进一帧。
        ''' </summary>
        ''' <remarks>
        ''' <para>
        ''' <b>帧末必须让张量的设备端缓存失效</b>，否则 GPU 上的仿真会在第一步之后<b>完全静止</b>
        ''' （这不是理论风险，而是本项目已经踩过并修掉的真实缺陷）。原因：
        ''' <c>CudaTensorF.Device(t)</c> 以 <c>(t.Data 主机数组引用, t.Version)</c> 为键缓存显存副本，
        ''' 而 <c>Version = 张量自身写入计数 + TensorF.DeviceEpoch</c>。
        ''' 本仿真每帧都用 <c>TensorF.Wrap(state.PosX, …)</c> 现做一个<b>全新</b>的包装器
        ''' （自身写入计数恒为 0），同时又<b>绕过索引器</b>就地把新位置/新速度/新力写回
        ''' <c>ParticleState</c> 的同几个数组（<c>Array.Copy</c> / <c>PinnedHostBuffer.ReadInto</c>）——
        ''' 于是缓存键逐帧完全相同，GPU 会一直用<b>第一帧上传的旧位置</b>重算，
        ''' 得到与上一帧逐位相同的力、再写回逐位相同的位置：状态被钉死在初始条件上。
        ''' </para>
        ''' <para>
        ''' <see cref="tf.TensorF.InvalidateAllDeviceCaches"/> 正是框架为这种
        ''' "Wrap 零拷贝包装 + 主机侧就地推进" 的用法提供的失效手段（见
        ''' <c>TensorF.vb</c> 的 <c>DeviceEpoch</c> 注释）。它在帧末把全局纪元号 +1，
        ''' 使下一帧所有张量的缓存键都改变，从而强制重新上传。
        ''' 代价可忽略：本仿真的每个张量每帧内容都会变，本来就必须重新上传。
        ''' </para>
        ''' </remarks>
        Public Sub StepOnce()
            ThrowIfDisposed()

            If m_state.ParticleCount < 2 Then Return

            m_stopwatch.Restart()
            m_backend.Solver.ComputeForces(m_state, m_parameters)
            m_stopwatch.Stop()
            m_lastSolverMs = m_stopwatch.Elapsed.TotalMilliseconds

            m_stopwatch.Restart()
            m_integrator.Integrate(m_state, m_parameters)
            m_stopwatch.Stop()
            m_lastIntegrateMs = m_stopwatch.Elapsed.TotalMilliseconds

            ' 求解器与积分器都就地改写了 TensorF 包装着的宿主数组，必须让设备端缓存失效
            tf.TensorF.InvalidateAllDeviceCaches()

            m_stepCount += 1
        End Sub

        Private Sub ThrowIfDisposed()
            If m_disposed Then Throw New ObjectDisposedException(GetType(ParticleLifeSimulation).Name)
        End Sub

        ''' <summary>
        ''' 结束本仿真对象。
        ''' </summary>
        ''' <remarks>
        ''' <b>刻意不释放计算后端</b>：后端是构造时由调用方传入的，生命周期由创建者掌握。
        ''' 早期版本在这里顺手 <c>m_backend.Dispose()</c>，结果一个进程里只能有<b>一个</b>
        ''' 仿真对象——第二个（例如自测的第 6 节）一启动就会拿到已被释放的引擎并抛
        ''' <c>ObjectDisposedException</c>。而且 CUDA 引擎一旦释放，全进程的
        ''' <c>CudaTensor</c> / <c>CudaTensorF</c> 单例都会指向失效上下文，无法重建。
        ''' 因此这里的约定是：<b>谁创建，谁释放</b>。
        ''' </remarks>
        Public Sub Dispose() Implements IDisposable.Dispose
            If m_disposed Then Return

            m_disposed = True
        End Sub

    End Class

End Namespace
