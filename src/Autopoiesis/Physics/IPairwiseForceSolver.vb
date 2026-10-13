Namespace Physics

    ''' <summary>
    ''' 粒子对力求解器契约。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 负责 <c>algorithm/compute.glsl</c> 里 <c>apply_physics</c> 的两遍 O(N²) 主循环：
    ''' 第一遍核密度估计、第二遍分段线性力场，结果写回
    ''' <see cref="ParticleState.ForceX"/> / <see cref="ParticleState.ForceY"/> /
    ''' <see cref="ParticleState.Density"/>（**未限幅**）。
    ''' </para>
    ''' <para>
    ''' 力的截断、时间积分与环面回绕一律**不**属于求解器的职责，它们由 TensorF
    ''' 单精度张量算子完成（见 <see cref="TensorIntegrator"/>）。这样做的意义在于：
    ''' GPU 内核只需要承载唯一无法用张量算子表达的部分，其余数学全部经由
    ''' <c>TensorF.computeKernelF</c> 这个可插拔后端，从而可以整体切换到 CUDA 加速。
    ''' </para>
    ''' </remarks>
    Public Interface IPairwiseForceSolver
        Inherits IDisposable

        ''' <summary>求解器名称（用于状态栏与诊断）</summary>
        ReadOnly Property Name As String

        ''' <summary>是否运行在 GPU 上</summary>
        ReadOnly Property IsGpu As Boolean

        ''' <summary>
        ''' 宣告静态状态（粒子类型、交互矩阵）已变化，下一次
        ''' <see cref="ComputeForces"/> 需要重新上传。
        ''' </summary>
        ''' <remarks>粒子数量变化由求解器自行按长度检测，无需调用本方法。</remarks>
        Sub Invalidate()

        ''' <summary>
        ''' 计算全部粒子的合力与核密度，结果就地写入 <paramref name="state"/>。
        ''' </summary>
        ''' <remarks>
        ''' 必须是「对旧状态的纯函数」：只读取 <c>PosX/PosY/Types/ForceMatrix</c>，
        ''' 只写入 <c>ForceX/ForceY/Density</c>，绝不读取上一帧的力（否则更新会与
        ''' 线程调度顺序相关）。
        ''' </remarks>
        Sub ComputeForces(state As ParticleState, params As SimulationParameters)
    End Interface

End Namespace
