Imports tf = Microsoft.VisualBasic.MachineLearning.TensorFlow
Imports tfCompute = Microsoft.VisualBasic.MachineLearning.TensorFlow.Compute

Namespace Physics

    ''' <summary>
    ''' 用 <see cref="tf.TensorF"/> 的单精度张量算子完成一帧的「非粒子对」数学。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 对应 <c>algorithm/compute.glsl</c> 中 <c>apply_physics</c> 的尾部与
    ''' <c>algorithm/readme.md</c> 的第 5、6 节：
    ''' </para>
    ''' <list type="number">
    ''' <item><b>力截断</b>：<c>|F| &gt; 100</c> 时归一化到 100；</item>
    ''' <item><b>阻尼辛欧拉</b>：<c>v ← β(v + F·Δt)</c>，先更新速度再用新速度推进位置；</item>
    ''' <item><b>环面回绕</b>：<c>x ← mod(x, L)</c>，保持与最小像度量自洽；</item>
    ''' <item><b>密度统计</b>：ρ 的极值与均值，供状态栏展示。</item>
    ''' </list>
    ''' <para>
    ''' 所有算术都必须经过 <c>TensorF.computeKernelF</c> 这个可插拔后端，因此把
    ''' 计算后端切换成 <c>CudaTensorF</c> 之后，这一整段就会自动跑在 GPU 上。
    ''' </para>
    ''' <para>
    ''' <b>为什么这里需要"造"出 round / floor：</b><c>ITensorComputeF</c> 没有
    ''' <c>Round</c>/<c>Floor</c>/<c>Sign</c>，也没有比较运算。环面回绕
    ''' <c>mod(x, L) = x - L·floor(x/L)</c> 于是只能靠饱和函数重建阶跃：
    ''' <c>step(t) ≈ Clip(t·G, 0, 1)</c>（<c>G</c> 取 1e20，远大于 float32 在 1 附近的
    ''' 量化间隔 1.19e-7）。在 <c>u = x/L ∈ (-1, 2)</c> 上
    ''' <c>floor(u) = step(u) + step(u-1) - 1</c>，其中唯一不精确的取值点是
    ''' <c>u</c> 恰好等于 0 或 1：此时得到 <c>L</c> 而不是 <c>0</c>——
    ''' 而在环面上 <c>L ≡ 0</c>，两者是<b>同一个点</b>，对最小像距离、内核计算与
    ''' 渲染都完全等价，因此这个近似不会引入任何物理误差。
    ''' </para>
    ''' <para>
    ''' 单步位移有界（<c>|v|·Δt ≲ 50 ≪ L = 2560</c>），所以 <c>u</c> 永远落在
    ''' <c>(-1, 2)</c> 内，<c>floor</c> 只有 -1/0/1 三种取值，上面的两段阶跃足够。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class TensorIntegrator

        ''' <summary>力的截断阈值（与 GLSL 的 <c>MAX_FORCE</c> 一致）</summary>
        Public Const MaxForce As Single = 100.0F

        ''' <summary>归一化力矢量时的分母保护值</summary>
        Private Const ForceEpsilon As Single = 1.0E-12F

        ''' <summary>用于重建阶跃函数的饱和增益</summary>
        ''' <remarks>
        ''' float32 在 1 附近的量化间隔约 1.19e-7，因此只要增益远大于 1e7
        ''' 就能让所有可表示的非零输入都饱和；1e20 同时远离溢出边界（3.4e38）。
        ''' </remarks>
        Private Const StepGain As Single = 1.0E+20F

        Private m_backendName As String = "<未运行>"
        Private m_densityMin As Single
        Private m_densityMax As Single
        Private m_densityMean As Double
        Private m_maxSpeed As Single
        Private m_operationCount As Integer

        ''' <summary>最近一帧实际使用的张量后端名称（例如 CUDA-F32 / SIMD-F32）</summary>
        Public ReadOnly Property BackendName As String
            Get
                Return m_backendName
            End Get
        End Property

        ''' <summary>最近一帧中所有粒子的最大速率 |v|</summary>
        ''' <remarks>
        ''' 阻尼使系统必然耗散收敛：恒力下的终末速度为
        ''' <c>v* = β/(1-β)·F·Δt</c>（β=0.85、F≤100、Δt≈0.083 时约 47）。
        ''' 该值长期显著高于 v* 即为数值失稳的早期信号。
        ''' </remarks>
        Public ReadOnly Property MaxSpeed As Single
            Get
                Return m_maxSpeed
            End Get
        End Property

        ''' <summary>最近一帧的核密度最小值</summary>
        Public ReadOnly Property DensityMin As Single
            Get
                Return m_densityMin
            End Get
        End Property

        ''' <summary>最近一帧的核密度最大值</summary>
        Public ReadOnly Property DensityMax As Single
            Get
                Return m_densityMax
            End Get
        End Property

        ''' <summary>最近一帧的核密度平均值</summary>
        Public ReadOnly Property DensityMean As Double
            Get
                Return m_densityMean
            End Get
        End Property

        ''' <summary>最近一帧经过的张量算子调用次数（诊断用）</summary>
        Public ReadOnly Property OperationCount As Integer
            Get
                Return m_operationCount
            End Get
        End Property

        ''' <summary>
        ''' 对 <paramref name="state"/> 执行力截断 + 阻尼辛欧拉 + 环面回绕，结果就地写回。
        ''' </summary>
        Public Sub Integrate(state As ParticleState, params As SimulationParameters)
            Dim n As Integer = state.ParticleCount

            If n < 2 Then Return

            ' 一次性快照后端：同一帧内不允许出现两个不同的后端
            Dim ops As tfCompute.ITensorComputeF = tf.TensorF.computeKernelF

            m_backendName = If(ops Is Nothing, "<无后端>", ops.Name)

            If ops Is Nothing Then
                Throw New InvalidOperationException("TensorF.computeKernelF 未设置，无法执行张量积分。")
            End If

            Dim opCount As Integer = 0

            Dim forceX As tf.TensorF = tf.TensorF.Wrap(state.ForceX, New Integer() {n})
            Dim forceY As tf.TensorF = tf.TensorF.Wrap(state.ForceY, New Integer() {n})
            Dim velX As tf.TensorF = tf.TensorF.Wrap(state.VelX, New Integer() {n})
            Dim velY As tf.TensorF = tf.TensorF.Wrap(state.VelY, New Integer() {n})
            Dim posX As tf.TensorF = tf.TensorF.Wrap(state.PosX, New Integer() {n})
            Dim posY As tf.TensorF = tf.TensorF.Wrap(state.PosY, New Integer() {n})

            ' ==============================================================
            ' 1. 力截断：|F| > 100 时沿原方向归一化到 100
            '    注意一个精巧的巧合：单对粒子的最大排斥力恰好是 2 × 50 = 100，
            '    所以限幅实际只约束「多邻居合力叠加」的情形。
            ' ==============================================================
            Dim squaredX As tf.TensorF = ops.Square(forceX)
            Dim squaredY As tf.TensorF = ops.Square(forceY)
            Dim magnitudeSq As tf.TensorF = ops.Add(squaredX, squaredY)

            Release(squaredX) : Release(squaredY)

            Dim magnitude As tf.TensorF = ops.Sqrt(magnitudeSq)
            Release(magnitudeSq)

            ' ITensorComputeF 的 Minimum / Maximum 只接受两个张量，标量边界改用 Clip：
            '   capped  = clip(|F|, 0, 100)            -> 限幅后的模长
            '   floored = clip(|F|, eps, +∞)           -> 除零保护
            Dim capped As tf.TensorF = ops.Clip(magnitude, 0.0F, MaxForce)
            Dim floored As tf.TensorF = ops.Clip(magnitude, ForceEpsilon, Single.MaxValue)
            Release(magnitude)

            Dim scale As tf.TensorF = ops.Divide(capped, floored)
            Release(capped) : Release(floored)

            Dim clampedForceX As tf.TensorF = ops.Multiply(forceX, scale)
            Dim clampedForceY As tf.TensorF = ops.Multiply(forceY, scale)

            Release(scale)
            opCount += 9

            ' ==============================================================
            ' 2. 阻尼辛欧拉：v ← β·(v + F·Δt)
            '    参考实现写成两次乘法（先乘 Δt 再乘 β），这里合并成
            '    v' = β·v + (β·Δt)·F，少一次逐元素乘法、数值等价。
            ' ==============================================================
            Dim dt As Single = params.Dt
            Dim beta As Single = params.Dampening
            Dim accelerated As Single = beta * dt

            Dim newVelX As tf.TensorF = ops.Add(
                ops.MultiplyScalar(velX, beta),
                ops.MultiplyScalar(clampedForceX, accelerated))
            Dim newVelY As tf.TensorF = ops.Add(
                ops.MultiplyScalar(velY, beta),
                ops.MultiplyScalar(clampedForceY, accelerated))

            Release(clampedForceX) : Release(clampedForceY)
            opCount += 6

            ' ==============================================================
            ' 3. 位置推进：x ← x + v·Δt（用新速度，这是半隐式/辛欧拉的关键）
            ' ==============================================================
            Dim advancedX As tf.TensorF = ops.Add(posX, ops.MultiplyScalar(newVelX, dt))
            Dim advancedY As tf.TensorF = ops.Add(posY, ops.MultiplyScalar(newVelY, dt))
            opCount += 4

            ' ==============================================================
            ' 4. 环面回绕：x ← mod(x, L)
            ' ==============================================================
            Dim wrappedX As tf.TensorF = WrapTorus(ops, advancedX, params.RegionWidth, opCount)
            Dim wrappedY As tf.TensorF = WrapTorus(ops, advancedY, params.RegionHeight, opCount)

            Release(advancedX) : Release(advancedY)

            ' ==============================================================
            ' 5. 写回宿主状态
            ' ==============================================================
            CopyInto(wrappedX, state.PosX, n)
            CopyInto(wrappedY, state.PosY, n)
            CopyInto(newVelX, state.VelX, n)
            CopyInto(newVelY, state.VelY, n)

            ' ==============================================================
            ' 6. 最大速率（稳定性观测）
            '    理论终末速度 v* = β/(1-β)·F·Δt，β=0.85 时约 47；实测值长期显著
            '    超过它就意味着阻尼或限幅失效了。
            ' ==============================================================
            Dim speedSquared As tf.TensorF = ops.Add(ops.Square(newVelX), ops.Square(newVelY))
            Dim speed As tf.TensorF = ops.Sqrt(speedSquared)
            Release(speedSquared)

            m_maxSpeed = ops.MaxAll(speed)
            Release(speed)
            opCount += 4

            ' ==============================================================
            ' 7. 密度统计（状态栏 / 稳定性观测）
            ' ==============================================================
            Dim density As tf.TensorF = tf.TensorF.Wrap(state.Density, New Integer() {n})

            m_densityMin = ops.MinAll(density)
            m_densityMax = ops.MaxAll(density)
            m_densityMean = ops.MeanAll(density)
            opCount += 3

            m_operationCount = opCount

            ' 输入包装器与被释放的中间量都不需要显式 Dispose：Wrap 是零拷贝视图
            ' （宿主数组由 ParticleState 持有），中间量的宿主数组是短期垃圾，
            ' 设备侧缓冲由计算后端自己的 LRU 预算管理。
        End Sub

        ''' <summary>
        ''' <c>mod(x, L)</c> 的张量实现：<c>x - L·(step(u) + step(u−1) − 1)</c>，<c>u = x/L</c>。
        ''' </summary>
        Private Shared Function WrapTorus(ops As tfCompute.ITensorComputeF,
                                          x As tf.TensorF,
                                          period As Single,
                                          ByRef operationCount As Integer) As tf.TensorF
            Dim u As tf.TensorF = ops.DivideScalar(x, period)

            Dim probe0 As tf.TensorF = ops.MultiplyScalar(u, StepGain)
            Dim step0 As tf.TensorF = ops.Clip(probe0, 0.0F, 1.0F)
            Release(probe0)

            Dim shifted As tf.TensorF = ops.AddScalar(u, -1.0F)
            Dim probe1 As tf.TensorF = ops.MultiplyScalar(shifted, StepGain)
            Dim step1 As tf.TensorF = ops.Clip(probe1, 0.0F, 1.0F)
            Release(shifted) : Release(probe1) : Release(u)

            Dim steps As tf.TensorF = ops.Add(step0, step1)
            Release(step0) : Release(step1)

            ' floor(u) = step(u) + step(u − 1) − 1
            Dim floorU As tf.TensorF = ops.AddScalar(steps, -1.0F)
            Release(steps)

            Dim offset As tf.TensorF = ops.MultiplyScalar(floorU, period)
            Release(floorU)

            Dim result As tf.TensorF = ops.Subtract(x, offset)
            Release(offset)

            operationCount += 10

            Return result
        End Function

        ''' <summary>把张量的宿主数据拷进目标数组</summary>
        Private Shared Sub CopyInto(source As tf.TensorF, target As Single(), count As Integer)
            Dim data As Single() = source.Data

            System.Array.Copy(data, 0, target, 0, count)
        End Sub

        ''' <summary>
        ''' 尽力释放一个中间张量的托管数组。
        ''' </summary>
        ''' <remarks>
        ''' 只对本方法链内部产生的张量调用，绝不用于 <c>Wrap</c> 出来的输入视图
        ''' （那些视图与 <see cref="ParticleState"/> 的数组共享同一段内存）。
        ''' 释放失败不影响仿真：数组最终仍会被 GC 回收。
        ''' </remarks>
        Private Shared Sub Release(ByRef tensor As tf.TensorF)
            If tensor Is Nothing Then Return

            Dim releasing As tf.TensorF = tensor
            tensor = Nothing

            Try
                releasing.Dispose()
            Catch ex As Exception
                ' 张量释放不应中断仿真
            End Try
        End Sub

    End Class

End Namespace
