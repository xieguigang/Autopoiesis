Namespace Physics

    ''' <summary>
    ''' 粒子对力求解器的托管参考实现（CPU）。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 与 <c>Kernels/autopoiesis_physics.cu</c> 的 <c>autopoiesisPairForceF32</c>
    ''' **逐条语义等价**：同样的遍历顺序、同样的最小像约定、同样的密度核与权重、
    ''' 同样的密度抑制、同样的三角形吸引核、同样的 <c>-50</c> 系数、同样的退化抖动。
    ''' 它承担两个角色：
    ''' </para>
    ''' <list type="number">
    ''' <item>无可用的 NVIDIA 设备 / NVRTC 时作为降级路径，保证程序仍可运行；</item>
    ''' <item>作为数值对拍基准，用于验证 CUDA 内核的正确性。</item>
    ''' </list>
    ''' <para>
    ''' 复杂度是 O(N²) 且无法回避（这正是参考实现只能在 GPU 上跑十万粒子的原因）。
    ''' 这里用 <see cref="System.Threading.Tasks.Parallel"/> 把粒子维度并行化：
    ''' 每个线程只写自己下标处的输出，不存在写冲突，也不需要锁。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class CpuPairwiseSolver
        Implements IPairwiseForceSolver

        Private Const BlockSize As Integer = 256

        Public ReadOnly Property Name As String Implements IPairwiseForceSolver.Name
            Get
                Return "CPU-F32"
            End Get
        End Property

        Public ReadOnly Property IsGpu As Boolean Implements IPairwiseForceSolver.IsGpu
            Get
                Return False
            End Get
        End Property

        Public Sub Invalidate() Implements IPairwiseForceSolver.Invalidate
            ' 托管实现直接读宿主数组，没有需要失效的镜像
        End Sub

        Public Sub ComputeForces(state As ParticleState, params As SimulationParameters) Implements IPairwiseForceSolver.ComputeForces
            Dim n As Integer = state.ParticleCount

            If n < 2 Then Return

            Dim posX As Single() = state.PosX
            Dim posY As Single() = state.PosY
            Dim particleTypes As Integer() = state.Types
            Dim mat As Single() = state.ForceMatrix
            Dim outForceX As Single() = state.ForceX
            Dim outForceY As Single() = state.ForceY
            Dim outDensity As Single() = state.Density

            Dim stride As Integer = SimulationParameters.ForceMatrixStride
            Dim regionW As Single = params.RegionWidth
            Dim regionH As Single = params.RegionHeight
            Dim repulsionRadius As Single = params.RepulsionRadius
            Dim interactionRadius As Single = params.InteractionRadius
            Dim densityLimit As Single = params.DensityLimit

            Dim maxRadiusSq As Single = interactionRadius * interactionRadius
            Dim width As Single = interactionRadius - repulsionRadius
            Dim midpoint As Single = (repulsionRadius + interactionRadius) * 0.5F
            Dim halfWidth As Single = width * 0.5F

            System.Threading.Tasks.Parallel.For(
                0, n,
                Sub(i As Integer)
                    Dim posAX As Single = posX(i)
                    Dim posAY As Single = posY(i)
                    Dim typeA As Integer = particleTypes(i)

                    ' --------------------------------------------------
                    ' 第一遍：核密度估计 rho_i
                    ' 必须独立成一遍：若与力求值合并，早处理的邻居与晚处理的邻居
                    ' 会看到不同的 rho_i，更新就不再是「对旧状态的纯函数」。
                    ' --------------------------------------------------
                    Dim localDensity As Single = 0.0F

                    For j As Integer = 0 To n - 1
                        If j = i Then Continue For

                        Dim dx As Single = posAX - posX(j)
                        Dim dy As Single = posAY - posY(j)

                        dx -= WrapShift(dx, regionW)
                        dy -= WrapShift(dy, regionH)

                        Dim distSq As Single = dx * dx + dy * dy

                        If distSq > maxRadiusSq Then Continue For

                        Dim dist As Single = CSng(System.Math.Sqrt(CDbl(distSq)))

                        If dist > 0.0F Then
                            If particleTypes(j) = typeA Then
                                localDensity += 1.0F - dist / interactionRadius
                            Else
                                localDensity += (1.0F - dist / interactionRadius) * 0.5F
                            End If
                        End If
                    Next

                    ' --------------------------------------------------
                    ' 第二遍：逐对力矢量叠加
                    ' --------------------------------------------------
                    Dim totalForceX As Single = 0.0F
                    Dim totalForceY As Single = 0.0F

                    For j As Integer = 0 To n - 1
                        If j = i Then Continue For

                        Dim dx As Single = posAX - posX(j)
                        Dim dy As Single = posAY - posY(j)

                        dx -= WrapShift(dx, regionW)
                        dy -= WrapShift(dy, regionH)

                        Dim distSq As Single = dx * dx + dy * dy

                        If distSq > maxRadiusSq Then Continue For

                        Dim dist As Single = CSng(System.Math.Sqrt(CDbl(distSq)))

                        If dist > 0.0F Then
                            Dim typeB As Integer = particleTypes(j)
                            Dim attraction As Single = mat(typeA + typeB * stride)

                            ' 密度抑制只作用于吸引部分
                            If attraction > 0.0F Then
                                Dim over As Single = localDensity - densityLimit

                                If over < 0.0F Then
                                    over = 0.0F
                                ElseIf over > 1.005F Then
                                    over = 1.005F
                                End If

                                attraction *= 1.0F - over
                            End If

                            Dim force As Single

                            If dist < repulsionRadius Then
                                force = (dist / repulsionRadius - 1.0F) * 2.0F
                            Else
                                Dim t As Single = CSng(System.Math.Abs(CDbl(dist - midpoint))) / halfWidth
                                force = attraction * (1.0F - t)
                            End If

                            ' n_ij 从邻居 j 指向 i
                            Dim scale As Single = force * -50.0F

                            totalForceX += dx / dist * scale
                            totalForceY += dy / dist * scale
                        Else
                            ' 完全重合：对称性破缺用的确定性抖动
                            totalForceX += RandomHash(CSng(i) + posAX) - 0.5F
                            totalForceY += RandomHash(CSng(i) + posAY) - 0.5F
                        End If
                    Next

                    outForceX(i) = totalForceX
                    outForceY(i) = totalForceY
                    outDensity(i) = localDensity
                End Sub)
        End Sub

        ''' <summary>
        ''' 环面回绕的位移项 <c>round(v / L) * L</c>。
        ''' </summary>
        ''' <remarks>
        ''' GLSL 的 <c>round</c> 是「四舍五入、半数远离零」，CUDA 的 <c>roundf</c> 与
        ''' <see cref="System.Math.Round(Double, MidpointRounding)"/> 的
        ''' <c>AwayFromZero</c> 模式都与之相同，因此三者的取整结果一致。
        ''' </remarks>
        Private Shared Function WrapShift(value As Single, period As Single) As Single
            If period <= 0.0F Then Return 0.0F

            Return CSng(System.Math.Round(CDbl(value) / CDbl(period), MidpointRounding.AwayFromZero)) * period
        End Function

        ''' <summary>
        ''' GLSL sin-hash 的逐字复刻：
        ''' <c>fract(sin(dot(vec2(v, v/10), vec2(12.9898, 78.233))) * 43758.5453)</c>。
        ''' </summary>
        Private Shared Function RandomHash(v As Single) As Single
            Const kDotScale As Single = 12.9898F + 78.233F / 10.0F

            Dim arg As Single = v * kDotScale
            Dim t As Single = CSng(System.Math.Sin(CDbl(arg))) * 43758.5453F

            Return t - CSng(System.Math.Floor(CDbl(t)))
        End Function

        ''' <summary>建议的并行块大小（与 CUDA 内核的 block 尺寸对齐，供界面展示）</summary>
        Public ReadOnly Property SuggestedBlockSize As Integer
            Get
                Return BlockSize
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            ' 托管实现没有非托管资源
        End Sub

    End Class

End Namespace
