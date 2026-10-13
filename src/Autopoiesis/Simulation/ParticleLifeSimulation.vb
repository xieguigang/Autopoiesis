Imports System.Diagnostics

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

            m_stepCount += 1
        End Sub

        Private Sub ThrowIfDisposed()
            If m_disposed Then Throw New ObjectDisposedException(GetType(ParticleLifeSimulation).Name)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If m_disposed Then Return

            m_disposed = True
            m_backend.Dispose()
        End Sub

    End Class

End Namespace
