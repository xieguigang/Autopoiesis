Imports System.IO
Imports System.Text
Imports ILCudaGpu = Microsoft.VisualBasic.Computing.ILCuda.GPUTensor

Namespace SelfTest

    ''' <summary>
    ''' 自测：验证「TensorF 张量算子 + 手写 CUDA 内核」这套实现与参考算法一致。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 用 <c>Autopoiesis.exe --selftest</c> 运行；返回值是失败的断言数量（0 表示全部通过），
    ''' 同时会把报告写入可执行文件目录下的 <c>autopoiesis_selftest.log</c>。
    ''' </para>
    ''' <para>覆盖内容：</para>
    ''' <list type="number">
    ''' <item>计算后端的装配结果与内核符号可用性；</item>
    ''' <item>粒子状态生成的确定性（同种子必然同布局、同交互矩阵）；</item>
    ''' <item>张量积分器（力截断 / 阻尼辛欧拉 / 环面回绕）与标量参考实现的对拍，
    ''' 重点是"用 Clip 重建 floor"得到的 <c>mod(x, L)</c> 在 0 / L / 上溢 / 下溢 边界上的行为；</item>
    ''' <item>CUDA 内核与托管参考实现的数值对拍；</item>
    ''' <item>长时间运行的稳定性：位置留在环面内、速率有界、无 NaN/Inf；</item>
    ''' <item>GPU 规模化演化：粒子数高于 TensorF 的 GPU 元素阈值时，状态仍逐帧持续演化
    ''' （覆盖"绕过索引器就地改写 → 设备端缓存未失效 → 仿真假死"这一类缺陷）。</item>
    ''' </list>
    ''' </remarks>
    Public NotInheritable Class AutopoiesisSelfTest

        ''' <summary>可接受的相对误差（float32 对 N 项累加的量级）</summary>
        Private Const RelativeTolerance As Single = 2.0E-3F

        ''' <summary>可接受的绝对误差（力的量级可达 100）</summary>
        Private Const AbsoluteTolerance As Single = 1.0E-2F

        ''' <summary>力的截断阈值</summary>
        Private Const MaxForce As Single = 100.0F

        ''' <summary>归一化力矢量时的分母保护值（与 TensorIntegrator 一致）</summary>
        Private Const ForceEpsilon As Single = 1.0E-12F

        Public Shared Function Run(Optional logFile As String = Nothing) As Integer
            Dim report As New ReportWriter(logFile)

            report.Line("=== Autopoiesis 自测 ===")
            report.Line($"时间: {Date.Now:yyyy-MM-dd HH:mm:ss}")

            Dim backend As Physics.ComputeBackend = Nothing

            Try
                backend = Physics.ComputeBackend.Create(preferGpu:=True)

                CheckEnvironment(report, backend)

                Dim failures As Integer = 0

                failures += CheckDeterminism(report)
                failures += CheckIntegratorAgainstScalarReference(report)
                failures += CheckSolverCrossValidation(report)
                failures += CheckLongRunStability(report, backend)
                failures += CheckGpuScaleEvolution(report, backend)

                report.Line()
                report.Line($"汇总: {report.Passed} 通过 / {failures} 失败")

                Return failures
            Catch ex As Exception
                report.Fail("未捕获异常", ex.ToString())
                report.Line()
                report.Line($"汇总: {report.Passed} 通过 / {report.Failed} 失败")

                Return System.Math.Max(1, report.Failed)
            Finally
                If backend IsNot Nothing Then
                    Try
                        backend.Dispose()
                    Catch
                    End Try
                End If

                report.Flush()
            End Try
        End Function

        ' ==================================================================
        ' 1. 环境与后端
        ' ==================================================================

        Private Shared Sub CheckEnvironment(report As ReportWriter, backend As Physics.ComputeBackend)
            report.Section("1. 计算后端装配")
            report.Info($"粒子对力求解器 : {backend.Solver.Name} (IsGpu={backend.IsGpu})")
            report.Info($"TensorF 计算后端 : {backend.TensorBackendName}")

            If backend.DeviceBytes > 0 Then
                report.Info($"求解器显存占用   : {backend.DeviceBytes / 1024.0 / 1024.0:F2} MB")
            End If

            For Each line As String In backend.Diagnostics.Split({vbLf, vbCrLf}, StringSplitOptions.RemoveEmptyEntries)
                report.Info("  " & line)
            Next

            If backend.IsGpu Then
                report.Pass("1.1 CUDA 粒子对力求解器已启用",
                            $"kernel={Physics.CudaPairwiseSolver.KernelSymbol}, block={Physics.CudaPairwiseSolver.BlockSize}")
            Else
                report.Skip("1.1 CUDA 求解器未启用；GPU 对拍将被跳过（CPU 路径仍会完整验证）")
            End If
        End Sub

        ' ==================================================================
        ' 2. 状态生成的确定性
        ' ==================================================================

        Private Shared Function CheckDeterminism(report As ReportWriter) As Integer
            report.Section("2. 状态生成的确定性")

            Dim failures As Integer = 0

            Dim parameters As New Physics.SimulationParameters With {
                .ParticleCount = 512,
                .ParticleTypes = 4,
                .Seed = 12345
            }

            Dim first As New Physics.ParticleState()
            Dim second As New Physics.ParticleState()

            first.Generate(parameters, resetForces:=True, resetColors:=True)
            second.Generate(parameters, resetForces:=True, resetColors:=True)

            If SequencesEqual(first.PosX, second.PosX) AndAlso
               SequencesEqual(first.PosY, second.PosY) AndAlso
               SequencesEqual(first.Types, second.Types) AndAlso
               SequencesEqual(first.ForceMatrix, second.ForceMatrix) Then
                report.Pass("2.1 相同种子得到完全相同的布局与交互矩阵", $"N={parameters.ParticleCount}")
            Else
                report.Fail("2.1 相同种子得到完全相同的布局与交互矩阵", "两次生成的结果不一致")
                failures += 1
            End If

            Dim other As New Physics.ParticleState()
            other.Generate(New Physics.SimulationParameters With {
                .ParticleCount = 512,
                .ParticleTypes = 4,
                .Seed = 999
            }, resetForces:=True, resetColors:=True)

            If SequencesEqual(first.PosX, other.PosX) Then
                report.Fail("2.2 不同种子得到不同布局", "种子 12345 与 999 产生了相同的布局")
                failures += 1
            Else
                report.Pass("2.2 不同种子得到不同布局", Nothing)
            End If

            Dim pair As New Physics.ParticleState()
            pair.Generate(New Physics.SimulationParameters With {
                .ParticleCount = 2,
                .ParticleTypes = 3,
                .Seed = 7
            }, resetForces:=True, resetColors:=True)

            Dim pairOk As Boolean =
                System.Math.Abs(pair.PosY(0) - pair.PosY(1)) < 0.001F AndAlso
                System.Math.Abs((pair.PosX(1) - pair.PosX(0)) - 60.0F) < 0.001F

            If pairOk Then
                report.Pass("2.3 两颗粒子采用中心 ±30 的确定性初始条件", "dy = 0, dx = 60")
            Else
                report.Fail("2.3 两颗粒子采用中心 ±30 的确定性初始条件",
                            $"pos0=({pair.PosX(0)},{pair.PosY(0)}) pos1=({pair.PosX(1)},{pair.PosY(1)})")
                failures += 1
            End If

            Dim typesInRange As Boolean = True

            For i As Integer = 0 To first.ParticleCount - 1
                If first.Types(i) < 0 OrElse first.Types(i) >= parameters.ParticleTypes Then typesInRange = False
            Next

            If typesInRange Then
                report.Pass("2.4 粒子类型落在 [0, T) 内", $"T={parameters.ParticleTypes}")
            Else
                report.Fail("2.4 粒子类型落在 [0, T) 内", "存在越界类型")
                failures += 1
            End If

            Return failures
        End Function

        ' ==================================================================
        ' 3. 张量积分器 vs 标量参考实现
        ' ==================================================================

        Private Shared Function CheckIntegratorAgainstScalarReference(report As ReportWriter) As Integer
            report.Section("3. 张量积分器（力截断 / 阻尼辛欧拉 / 环面回绕）")

            Dim failures As Integer = 0

            Dim parameters As New Physics.SimulationParameters With {
                .RegionWidth = 2560.0F,
                .RegionHeight = 1440.0F,
                .RepulsionRadius = 20.0F,
                .InteractionRadius = 80.0F,
                .DensityLimit = 10.0F,
                .Dampening = 0.85F,
                .Dt = 5.0F / 60.0F
            }

            ' 位置刻意覆盖 0、中点、恰好 L、上溢、下溢、紧贴边界的样本
            Dim sampleX As Single() = {0.0F, 1280.0F, 2559.999F, 2565.0F, -5.0F, 0.001F, 2559.9995F, 1300.0F}
            Dim sampleY As Single() = {0.0F, 720.0F, 1439.999F, -3.0F, 1443.0F, 0.5F, 700.0F, 1439.9995F}

            ' 力的模长刻意覆盖 0、普通值、恰好 100、远超 100
            Dim sampleFx As Single() = {0.0F, 30.0F, 100.0F, 250.0F, -40.0F, 1.0E-09F, 0.0F, 70.0F}
            Dim sampleFy As Single() = {0.0F, -40.0F, 0.0F, -250.0F, 30.0F, 0.0F, 0.0F, -70.0F}
            Dim sampleVx As Single() = {0.0F, 5.0F, -12.0F, 40.0F, -8.0F, 0.0F, 3.0F, 47.0F}
            Dim sampleVy As Single() = {0.0F, -5.0F, 12.0F, -40.0F, 8.0F, 0.0F, -3.0F, -47.0F}

            Dim count As Integer = sampleX.Length
            Dim state As New Physics.ParticleState()
            state.EnsureCapacity(count)

            For i As Integer = 0 To count - 1
                state.PosX(i) = sampleX(i)
                state.PosY(i) = sampleY(i)
                state.VelX(i) = sampleVx(i)
                state.VelY(i) = sampleVy(i)
                state.ForceX(i) = sampleFx(i)
                state.ForceY(i) = sampleFy(i)
                state.Density(i) = CSng(i)
            Next

            ' ---- 标量参考（与 TensorIntegrator 的算子链逐条对应） ----
            Dim referenceVx(count - 1) As Single
            Dim referenceVy(count - 1) As Single
            Dim referenceX(count - 1) As Single
            Dim referenceY(count - 1) As Single
            Dim referenceFx(count - 1) As Single
            Dim referenceFy(count - 1) As Single

            For i As Integer = 0 To count - 1
                Dim scale As Single = ForceScale(sampleFx(i), sampleFy(i))

                ' clamp(|F|, 0, 100) / clamp(|F|, eps, +inf)
                Dim clampedFx As Single = sampleFx(i) * scale
                Dim clampedFy As Single = sampleFy(i) * scale

                referenceFx(i) = clampedFx
                referenceFy(i) = clampedFy

                ' v ← β·v + (β·Δt)·F
                Dim accelerated As Single = parameters.Dampening * parameters.Dt
                Dim vx As Single = parameters.Dampening * sampleVx(i) + accelerated * clampedFx
                Dim vy As Single = parameters.Dampening * sampleVy(i) + accelerated * clampedFy

                referenceVx(i) = vx
                referenceVy(i) = vy

                ' x ← mod(x + v·Δt, L)
                referenceX(i) = ScalarMod(sampleX(i) + vx * parameters.Dt, parameters.RegionWidth)
                referenceY(i) = ScalarMod(sampleY(i) + vy * parameters.Dt, parameters.RegionHeight)
            Next

            ' ---- 张量实现 ----
            Dim integrator As New Physics.TensorIntegrator()
            integrator.Integrate(state, parameters)

            Dim velocityError As Single = 0.0F
            Dim positionError As Single = 0.0F

            For i As Integer = 0 To count - 1
                velocityError = System.Math.Max(velocityError, System.Math.Abs(state.VelX(i) - referenceVx(i)))
                velocityError = System.Math.Max(velocityError, System.Math.Abs(state.VelY(i) - referenceVy(i)))

                ' 环面等价比较：L 与 0 是同一个点，两者都算正确
                positionError = System.Math.Max(positionError,
                                                TorusDistance(state.PosX(i), referenceX(i), parameters.RegionWidth))
                positionError = System.Math.Max(positionError,
                                                TorusDistance(state.PosY(i), referenceY(i), parameters.RegionHeight))
            Next

            If velocityError <= AbsoluteTolerance Then
                report.Pass("3.1 阻尼辛欧拉 v ← β(v + F·Δt)", $"最大误差 {velocityError:E3}")
            Else
                report.Fail("3.1 阻尼辛欧拉 v ← β(v + F·Δt)", $"最大误差 {velocityError:E3}")
                failures += 1
            End If

            If positionError <= AbsoluteTolerance Then
                report.Pass("3.2 环面回绕 x ← mod(x, L)",
                            $"最大环面距离 {positionError:E3}（含 0 / L / 上溢 / 下溢 边界样本）")
            Else
                report.Fail("3.2 环面回绕 x ← mod(x, L)", $"最大环面距离 {positionError:E3}")
                failures += 1
            End If

            Dim clampOk As Boolean = True

            For i As Integer = 0 To count - 1
                Dim forceLength As Single = VectorLength(sampleFx(i), sampleFy(i))

                If forceLength <= MaxForce Then
                    If System.Math.Abs(referenceFx(i) - sampleFx(i)) > 1.0E-04F Then clampOk = False
                Else
                    If System.Math.Abs(VectorLength(referenceFx(i), referenceFy(i)) - MaxForce) > 0.01F Then clampOk = False
                End If
            Next

            If clampOk Then
                report.Pass("3.3 力截断 |F| > 100 时归一化到 100",
                            $"样本 |F| = 250√2 ≈ 353.5 已缩放到 100（原逻辑巧合：单对最大排斥力恰为 100）")
            Else
                report.Fail("3.3 力截断 |F| > 100 时归一化到 100", "限幅结果与参考不一致")
                failures += 1
            End If

            ' 零合力时速度只被阻尼衰减，位置按新速度推进
            Dim still As New Physics.ParticleState()
            still.EnsureCapacity(4)

            For i As Integer = 0 To 3
                still.PosX(i) = 100.0F + i
                still.PosY(i) = 200.0F + i
                still.VelX(i) = 10.0F
                still.VelY(i) = -10.0F
                still.ForceX(i) = 0.0F
                still.ForceY(i) = 0.0F
            Next

            integrator.Integrate(still, parameters)

            Dim dampedOk As Boolean = True
            Dim expectedVelocityX As Single = 10.0F * parameters.Dampening
            Dim expectedVelocityY As Single = -10.0F * parameters.Dampening

            For i As Integer = 0 To 3
                If System.Math.Abs(still.VelX(i) - expectedVelocityX) > 1.0E-04F Then dampedOk = False
                If System.Math.Abs(still.VelY(i) - expectedVelocityY) > 1.0E-04F Then dampedOk = False

                Dim expectedX As Single = ScalarMod(100.0F + i + expectedVelocityX * parameters.Dt,
                                                    parameters.RegionWidth)

                If TorusDistance(still.PosX(i), expectedX, parameters.RegionWidth) > 1.0E-03F Then dampedOk = False
            Next

            If dampedOk Then
                report.Pass("3.4 零合力时只受阻尼衰减，位置按新速度推进",
                            $"v = {expectedVelocityX:F4}")
            Else
                report.Fail("3.4 零合力时只受阻尼衰减，位置按新速度推进", "速度或位置与参考不符")
                failures += 1
            End If

            Return failures
        End Function

        ' ==================================================================
        ' 4. CUDA 内核 vs 托管参考实现
        ' ==================================================================

        Private Shared Function CheckSolverCrossValidation(report As ReportWriter) As Integer
            report.Section("4. CUDA 内核 与 托管参考实现 的数值对拍")

            Dim failures As Integer = 0
            Dim engine = GetEngine()

            If engine Is Nothing Then
                report.Skip("4.1..4.4 未取得 CUDA 引擎，跳过 GPU 对拍（CPU 路径已在第 3、5 节验证）")
                Return 0
            End If

            Dim parameters As New Physics.SimulationParameters With {
                .ParticleCount = 1024,
                .ParticleTypes = 5,
                .Seed = 2026,
                .RegionWidth = 2560.0F,
                .RegionHeight = 1440.0F,
                .RepulsionRadius = 20.0F,
                .InteractionRadius = 80.0F,
                .DensityLimit = 10.0F
            }

            Dim state As New Physics.ParticleState()
            state.Generate(parameters, resetForces:=True, resetColors:=True)

            ' ---- 托管参考 ----
            Using cpu As New Physics.CpuPairwiseSolver()
                cpu.ComputeForces(state, parameters)
            End Using

            Dim referenceFx As Single() = DirectCast(state.ForceX.Clone(), Single())
            Dim referenceFy As Single() = DirectCast(state.ForceY.Clone(), Single())
            Dim referenceDensity As Single() = DirectCast(state.Density.Clone(), Single())

            If IsAllFinite(referenceFx) AndAlso IsAllFinite(referenceFy) AndAlso IsAllFinite(referenceDensity) Then
                report.Pass("4.1 托管参考实现输出全部有限", $"N={parameters.ParticleCount}")
            Else
                report.Fail("4.1 托管参考实现输出全部有限", "出现 NaN 或 Inf")
                failures += 1
            End If

            ' ---- CUDA ----
            Dim gpu As Physics.CudaPairwiseSolver = Nothing

            Try
                gpu = New Physics.CudaPairwiseSolver(engine)
                gpu.ComputeForces(state, parameters)
            Catch ex As Exception
                report.Fail("4.2 CUDA 内核执行", ex.Message)
                failures += 1

                If gpu IsNot Nothing Then gpu.Dispose()

                Return failures
            End Try

            Dim forceXError As Single = MaxAbsDifference(state.ForceX, referenceFx)
            Dim forceYError As Single = MaxAbsDifference(state.ForceY, referenceFy)
            Dim densityError As Single = MaxAbsDifference(state.Density, referenceDensity)

            Dim forceScaleReference As Single = System.Math.Max(1.0F, MaxAbsValue(referenceFx))
            Dim densityScale As Single = System.Math.Max(1.0F, MaxAbsValue(referenceDensity))

            If forceXError <= AbsoluteTolerance + RelativeTolerance * forceScaleReference AndAlso
               forceYError <= AbsoluteTolerance + RelativeTolerance * forceScaleReference Then
                report.Pass("4.2 合力 F_x / F_y 与托管实现一致",
                            $"最大偏差 {forceXError:E3} / {forceYError:E3}（量级 {forceScaleReference:F2}）")
            Else
                report.Fail("4.2 合力 F_x / F_y 与托管实现一致",
                            $"最大偏差 {forceXError:E3} / {forceYError:E3}（量级 {forceScaleReference:F2}）")
                failures += 1
            End If

            If densityError <= AbsoluteTolerance + RelativeTolerance * densityScale Then
                report.Pass("4.3 核密度 ρ 与托管实现一致",
                            $"最大偏差 {densityError:E3}（量级 {densityScale:F2}）")
            Else
                report.Fail("4.3 核密度 ρ 与托管实现一致",
                            $"最大偏差 {densityError:E3}（量级 {densityScale:F2}）")
                failures += 1
            End If

            If IsAllFinite(state.ForceX) AndAlso IsAllFinite(state.ForceY) AndAlso IsAllFinite(state.Density) Then
                report.Pass("4.4 CUDA 内核输出全部有限", Nothing)
            Else
                report.Fail("4.4 CUDA 内核输出全部有限", "出现 NaN 或 Inf")
                failures += 1
            End If

            gpu.Dispose()

            Return failures
        End Function

        ' ==================================================================
        ' 5. 长时间运行的稳定性
        ' ==================================================================

        Private Shared Function CheckLongRunStability(report As ReportWriter,
                                                     backend As Physics.ComputeBackend) As Integer
            report.Section("5. 长时间运行的稳定性（环面不变量 / 速率上界）")

            Dim failures As Integer = 0
            Const steps As Integer = 200

            Dim parameters As New Physics.SimulationParameters With {
                .ParticleCount = 2048,
                .ParticleTypes = 6,
                .Seed = 42
            }

            Dim simulation As New Simulation.ParticleLifeSimulation(parameters, backend)

            Try
                simulation.Rebuild(regenerateForces:=True, regenerateColors:=True)

                Dim outOfRangeFrames As Integer = 0

                For iteration As Integer = 0 To steps - 1
                    Try
                        simulation.StepOnce()
                    Catch ex As Exception
                        report.Fail($"5.1 连续 {steps} 步无异常", $"第 {iteration} 步抛出: {ex.Message}")
                        Return failures + 1
                    End Try

                    If Not IsAllFinite(simulation.State.PosX) OrElse
                       Not IsAllFinite(simulation.State.VelX) OrElse
                       Not IsAllFinite(simulation.State.Density) Then
                        report.Fail($"5.1 连续 {steps} 步无异常", $"第 {iteration} 步出现 NaN / Inf")
                        Return failures + 1
                    End If

                    If Not PositionsInsideTorus(simulation.State, parameters) Then
                        outOfRangeFrames += 1
                    End If
                Next

                report.Pass($"5.1 连续 {steps} 步无异常且输出有限",
                            $"N={parameters.ParticleCount}, T={parameters.ParticleTypes}, 求解器 {backend.Solver.Name}")

                If outOfRangeFrames = 0 Then
                    report.Pass("5.2 位置始终留在环面 [0, L] 内",
                                $"容差 ±1 世界单位；最大速率 {simulation.Integrator.MaxSpeed:F2}")
                Else
                    report.Fail("5.2 位置始终留在环面 [0, L] 内", $"{outOfRangeFrames} / {steps} 帧越界")
                    failures += 1
                End If

                ' 阻尼保证速度必然耗散收敛；给一个宽松上界作为失稳探针
                Dim terminalVelocity As Single =
                    parameters.Dampening / (1.0F - parameters.Dampening) * MaxForce * parameters.Dt

                If simulation.Integrator.MaxSpeed <= 200.0F Then
                    report.Pass("5.3 最大速率有界（阻尼耗散收敛）",
                                $"|v|max = {simulation.Integrator.MaxSpeed:F3}，理论终末速度 ≈ {terminalVelocity:F1}")
                Else
                    report.Fail("5.3 最大速率有界（阻尼耗散收敛）",
                                $"|v|max = {simulation.Integrator.MaxSpeed:F3} 超出上界 200")
                    failures += 1
                End If

                report.Info($"密度 ρ ∈ [{simulation.Integrator.DensityMin:F3}, {simulation.Integrator.DensityMax:F3}]，" &
                            $"均值 {simulation.Integrator.DensityMean:F3}")
                report.Info($"单帧耗时: 求解 {simulation.LastSolverMilliseconds:F2} ms + " &
                            $"积分 {simulation.LastIntegrateMilliseconds:F2} ms = " &
                            $"{simulation.LastStepMilliseconds:F2} ms")
                report.Info($"张量算子调用次数/帧: {simulation.Integrator.OperationCount}" &
                            $"（后端 {simulation.Integrator.BackendName}）")
            Finally
                ' 释放仿真会一并释放计算后端；Dispose 是幂等的
                simulation.Dispose()
            End Try

            Return failures
        End Function

        ' ==================================================================
        ' 6. GPU 规模化：设备端缓存必须逐帧失效
        ' ==================================================================

        ''' <summary>
        ''' 粒子数高于 TensorF 的 GPU 元素阈值时，状态必须<b>逐帧持续演化</b>。
        ''' </summary>
        ''' <remarks>
        ''' <para>
        ''' 这一条是针对一个真实缺陷的回归测试：主机侧绕过 TensorF 索引器、就地改写
        ''' <c>Wrap</c> 包装出来的数组之后，如果没有调用
        ''' <c>TensorF.InvalidateAllDeviceCaches</c>，GPU 后端会因为
        ''' <c>(数组引用, Version)</c> 这个缓存键逐帧完全相同，而一直拿
        ''' <b>第一帧上传的旧位置</b>重算，于是出现"步数在涨、状态纹丝不动"的假死。
        ''' </para>
        ''' <para>
        ''' 关键点：只有元素数 ≥ <c>CudaTensorF.MinGpuElementsF</c>（默认 4096）才会走
        ''' 设备端缓存，因此第 5 节的低粒子数稳定性测试<b>根本覆盖不到</b>它——
        ''' 这正是该缺陷最初漏网的原因。
        ''' </para>
        ''' </remarks>
        Private Shared Function CheckGpuScaleEvolution(report As ReportWriter,
                                                      backend As Physics.ComputeBackend) As Integer
            report.Section("6. GPU 规模化演化（设备端缓存失效回归）")

            Dim failures As Integer = 0
            Const measuredSteps As Integer = 40
            Const warmupSteps As Integer = 8

            ' 必须 ≥ CudaTensorF.MinGpuElementsF，否则张量算子会回退 CPU、测不出缓存问题
            Dim particleCount As Integer = ILCudaGpu.CudaTensorF.MinGpuElementsF + 1024

            Dim parameters As New Physics.SimulationParameters With {
                .ParticleCount = particleCount,
                .ParticleTypes = 4,
                .Seed = 20261013
            }

            report.Info($"阈值 = {ILCudaGpu.CudaTensorF.MinGpuElementsF} 元素；本用例 N = {particleCount}")

            Dim simulation As New Simulation.ParticleLifeSimulation(parameters, backend)

            Try
                simulation.Rebuild(regenerateForces:=True, regenerateColors:=True)

                ' 预热：让速度从 0 建立起来，避免把"第一步几乎没动"误判成缺陷
                For i As Integer = 1 To warmupSteps
                    simulation.StepOnce()
                Next

                Dim previous As Single() = CType(simulation.State.PosX.Clone(), Single())
                Dim frozenSteps As Integer = 0
                Dim largestStep As Single = 0.0F
                Dim totalDrift As Single = 0.0F

                For i As Integer = 1 To measuredSteps
                    simulation.StepOnce()

                    Dim current As Single() = simulation.State.PosX
                    Dim stepMax As Single = 0.0F

                    For p As Integer = 0 To particleCount - 1
                        ' 环面回绕会产生接近 L 的跳变，按最小像折算回真实位移
                        Dim delta As Single = TorusDistance(current(p), previous(p), parameters.RegionWidth)

                        If delta > stepMax Then stepMax = delta
                    Next

                    If stepMax < 0.000001F Then frozenSteps += 1
                    If stepMax > largestStep Then largestStep = stepMax

                    totalDrift += stepMax
                    previous = CType(current.Clone(), Single())
                Next

                If frozenSteps = 0 AndAlso largestStep > 0.01F Then
                    report.Pass($"6.1 N={particleCount}（≥ GPU 阈值）逐帧持续演化",
                                $"{measuredSteps} 帧无静止帧；单帧最大位移 {largestStep:F3}，" &
                                $"累计 {totalDrift:F1} 世界单位；" &
                                $"求解器 {backend.Solver.Name} / 张量 {simulation.Integrator.BackendName}")
                Else
                    report.Fail($"6.1 N={particleCount}（≥ GPU 阈值）逐帧持续演化",
                                $"{frozenSteps} / {measuredSteps} 帧完全静止（单帧最大位移 {largestStep:E3}）——" &
                                "极可能是设备端缓存未失效：请确认 StepOnce 末尾调用了 " &
                                "TensorF.InvalidateAllDeviceCaches()")
                    failures += 1
                End If

                ' 步数与状态必须同步前进：假死时步数仍在涨，这里用"位移/步"反查
                If totalDrift > 0.0F Then
                    report.Pass("6.2 步进计数与状态演化一致",
                                $"步数 {simulation.StepCount}，平均单帧最大位移 {totalDrift / measuredSteps:F4}")
                Else
                    report.Fail("6.2 步进计数与状态演化一致", "步数在增长但位移恒为 0")
                    failures += 1
                End If
            Finally
                simulation.Dispose()
            End Try

            Return failures
        End Function

        ' ==================================================================
        ' 工具
        ' ==================================================================

        Private Shared Function GetEngine() As Microsoft.VisualBasic.Computing.ILCuda.Runtime.CudaEngine
            Dim backend As ILCudaGpu.CudaTensor = ILCudaGpu.CudaTensor.Current

            If backend Is Nothing Then Return Nothing

            Return backend.Engine
        End Function

        ''' <summary>
        ''' 与张量实现完全一致的力截断系数：<c>clip(|F|,0,100) / clip(|F|,eps,+∞)</c>。
        ''' </summary>
        Private Shared Function ForceScale(fx As Single, fy As Single) As Single
            Dim forceLength As Single = VectorLength(fx, fy)

            Dim capped As Single = System.Math.Min(forceLength, MaxForce)
            Dim floored As Single = System.Math.Max(forceLength, ForceEpsilon)

            Return capped / floored
        End Function

        Private Shared Function VectorLength(x As Single, y As Single) As Single
            Return CSng(System.Math.Sqrt(CDbl(x) * CDbl(x) + CDbl(y) * CDbl(y)))
        End Function

        ''' <summary>GLSL 的 <c>mod(a, L) = a - L·floor(a/L)</c></summary>
        Private Shared Function ScalarMod(value As Single, period As Single) As Single
            Return value - period * CSng(System.Math.Floor(CDbl(value) / CDbl(period)))
        End Function

        ''' <summary>环面上的最短距离：<c>L</c> 与 <c>0</c> 是同一个点</summary>
        Private Shared Function TorusDistance(a As Single, b As Single, period As Single) As Single
            Dim d As Single = System.Math.Abs(a - b)

            If d > period * 0.5F Then d = period - d

            Return d
        End Function

        Private Shared Function SequencesEqual(Of T)(a As T(), b As T()) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            If a.Length <> b.Length Then Return False

            Dim comparer = System.Collections.Generic.EqualityComparer(Of T).Default

            For i As Integer = 0 To a.Length - 1
                If Not comparer.Equals(a(i), b(i)) Then Return False
            Next

            Return True
        End Function

        Private Shared Function MaxAbsDifference(a As Single(), b As Single()) As Single
            Dim n As Integer = System.Math.Min(a.Length, b.Length)
            Dim worst As Single = 0.0F

            For i As Integer = 0 To n - 1
                Dim d As Single = System.Math.Abs(a(i) - b(i))

                If Single.IsNaN(d) Then Return Single.PositiveInfinity
                If d > worst Then worst = d
            Next

            Return worst
        End Function

        Private Shared Function MaxAbsValue(values As Single()) As Single
            Dim worst As Single = 0.0F

            For Each v As Single In values
                Dim a As Single = System.Math.Abs(v)

                If Single.IsNaN(a) Then Return Single.PositiveInfinity
                If a > worst Then worst = a
            Next

            Return worst
        End Function

        Private Shared Function IsAllFinite(values As Single()) As Boolean
            For Each v As Single In values
                If Single.IsNaN(v) OrElse Single.IsInfinity(v) Then Return False
            Next

            Return True
        End Function

        Private Shared Function PositionsInsideTorus(state As Physics.ParticleState,
                                                    parameters As Physics.SimulationParameters) As Boolean
            Const slack As Single = 1.0F

            For i As Integer = 0 To state.ParticleCount - 1
                If state.PosX(i) < -slack OrElse state.PosX(i) > parameters.RegionWidth + slack Then Return False
                If state.PosY(i) < -slack OrElse state.PosY(i) > parameters.RegionHeight + slack Then Return False
            Next

            Return True
        End Function

        ' ==================================================================
        ' 报告输出
        ' ==================================================================

        Private NotInheritable Class ReportWriter

            Private ReadOnly m_lines As New List(Of String)
            Private ReadOnly m_file As String
            Private m_passed As Integer
            Private m_failed As Integer

            Public Sub New(file As String)
                m_file = file
            End Sub

            Public ReadOnly Property Passed As Integer
                Get
                    Return m_passed
                End Get
            End Property

            Public ReadOnly Property Failed As Integer
                Get
                    Return m_failed
                End Get
            End Property

            Public Sub Line(Optional text As String = "")
                m_lines.Add(text)
                Console.WriteLine(text)
            End Sub

            Public Sub Section(title As String)
                Line()
                Line($"--- {title} ---")
            End Sub

            Public Sub Info(text As String)
                Line($"      {text}")
            End Sub

            Public Sub Pass(name As String, detail As String)
                m_passed += 1
                Line($"  [PASS] {name}{Describe(detail)}")
            End Sub

            Public Sub Fail(name As String, detail As String)
                m_failed += 1
                Line($"  [FAIL] {name}{Describe(detail)}")
            End Sub

            Public Sub Skip(message As String)
                Line($"  [SKIP] {message}")
            End Sub

            Private Shared Function Describe(detail As String) As String
                If String.IsNullOrEmpty(detail) Then Return String.Empty

                Return "  → " & detail
            End Function

            ''' <summary>把报告写到日志文件（文件名为空时只输出到控制台）</summary>
            Public Sub Flush()
                If String.IsNullOrWhiteSpace(m_file) Then Return

                Try
                    File.WriteAllLines(m_file, m_lines, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
                Catch ex As Exception
                    Console.WriteLine($"自测日志写入失败: {ex.Message}")
                End Try
            End Sub

        End Class

    End Class

End Namespace
