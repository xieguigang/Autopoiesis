Imports ILCudaRuntime = Microsoft.VisualBasic.Computing.ILCuda.Runtime
Imports ILCudaKernels = Microsoft.VisualBasic.Computing.ILCuda.Kernels
Imports ILCudaGpu = Microsoft.VisualBasic.Computing.ILCuda.GPUTensor
Imports tf = Microsoft.VisualBasic.MachineLearning.TensorFlow
Imports tfCompute = Microsoft.VisualBasic.MachineLearning.TensorFlow.Compute

Namespace Physics

    ''' <summary>
    ''' 计算后端的装配与降级。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 本类只做一件事：把「单精度张量算子」与「手写粒子对力内核」<b>接到同一个
    ''' CUDA 引擎</b>上，失败时整体降级到纯 CPU。
    ''' </para>
    ''' <para>
    ''' <b>为什么必须复用同一个引擎：</b><see cref="ILCudaRuntime.CudaKernel"/> 在
    ''' 启动前只调用无参的 <c>CudaRuntime.EnsureCurrent()</c>，绑定的是进程级的
    ''' 「最近创建的上下文」，而 <c>CudaEngine.TryCreate</c> 每次都会新建一个上下文。
    ''' 一旦同进程存在两个引擎，从较早那个引擎取到的内核就可能绑错上下文并抛出
    ''' <c>CUDA_ERROR_INVALID_HANDLE</c>。因此这里刻意<b>只用</b>
    ''' <c>CudaTensor.Register</c> 建立引擎（它内部完成双精度与单精度的内核注入并
    ''' 只建一个上下文），再用公开的 <c>CudaTensor.Current.Engine</c> 取回该引擎
    ''' 交给自定义内核使用。
    ''' </para>
    ''' <para>
    ''' <b>注册时序：</b>内核源码必须在 <c>CudaEngine.TryCreate</c> 之前注入
    ''' （NVRTC 在创建上下文的同一步就把全部源码编成一个镜像），所以
    ''' <see cref="ILCudaKernels.KernelSources.Register"/> 放在最前面。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class ComputeBackend
        Implements IDisposable

        Private ReadOnly m_solver As IPairwiseForceSolver
        Private ReadOnly m_gpu As Boolean
        Private ReadOnly m_diagnostics As String

        Private Sub New(solver As IPairwiseForceSolver, gpu As Boolean, diagnostics As String)
            m_solver = solver
            m_gpu = gpu
            m_diagnostics = diagnostics
        End Sub

        ''' <summary>粒子对力求解器（GPU 内核或 CPU 参考实现）</summary>
        Public ReadOnly Property Solver As IPairwiseForceSolver
            Get
                Return m_solver
            End Get
        End Property

        ''' <summary>粒子对主循环是否跑在 GPU 上</summary>
        Public ReadOnly Property IsGpu As Boolean
            Get
                Return m_gpu
            End Get
        End Property

        ''' <summary>当前 TensorF 计算后端名称（CUDA-F32 / SIMD-F32 / ...）</summary>
        Public ReadOnly Property TensorBackendName As String
            Get
                Dim ops As tfCompute.ITensorComputeF = tf.TensorF.computeKernelF
                Return If(ops Is Nothing, "<无后端>", ops.Name)
            End Get
        End Property

        ''' <summary>装配过程中收集的诊断信息（一次性打印）</summary>
        Public ReadOnly Property Diagnostics As String
            Get
                Return m_diagnostics
            End Get
        End Property

        ''' <summary>求解器占用的显存量（字节）；CPU 路径为 0</summary>
        Public ReadOnly Property DeviceBytes As Long
            Get
                Dim cuda As CudaPairwiseSolver = TryCast(m_solver, CudaPairwiseSolver)
                Return If(cuda Is Nothing, 0L, cuda.DeviceBytes)
            End Get
        End Property

        ''' <summary>状态栏用的简短后端标签</summary>
        Public Function StatusLabel() As String
            If m_gpu Then
                Return $"GPU · {m_solver.Name} · {TensorBackendName}"
            End If

            Return $"CPU · {m_solver.Name} · {TensorBackendName}"
        End Function

        ''' <summary>
        ''' 装配计算后端。
        ''' </summary>
        ''' <param name="preferGpu">
        ''' 是否优先尝试 CUDA；<c>False</c> 时直接使用 SIMD CPU 张量后端与托管求解器。
        ''' </param>
        ''' <remarks>
        ''' 本方法<b>绝不抛异常</b>：任何 CUDA 侧的失败都会被记录进
        ''' <see cref="Diagnostics"/> 并降级到 CPU，保证界面总是可用（只是规模需要下调）。
        ''' </remarks>
        Public Shared Function Create(Optional preferGpu As Boolean = True) As ComputeBackend
            Dim log As New List(Of String)
            Dim engine As ILCudaRuntime.CudaEngine = Nothing
            Dim reason As String = Nothing

            If preferGpu Then
                If TryAttachGpu(engine, reason, log) Then
                    Try
                        Dim solver As New CudaPairwiseSolver(engine)

                        log.Add($"粒子对力内核: {CudaPairwiseSolver.KernelSymbol}" &
                                $" (grid-stride, block={CudaPairwiseSolver.BlockSize})")
                        log.Add($"CUDA 设备: {solver.DescribeDevice()}")

                        AppendFloatKernelReport(log)

                        Return New ComputeBackend(solver, True, String.Join(Environment.NewLine, log))
                    Catch ex As Exception
                        reason = $"CUDA 粒子对力求解器初始化失败: {ex.Message}"
                        log.Add(reason)
                    End Try
                ElseIf Not String.IsNullOrWhiteSpace(reason) Then
                    log.Add($"GPU 不可用: {reason}")
                End If
            Else
                log.Add("已按配置仅使用 CPU 计算")
            End If

            ' ------------------------------------------------------------------
            ' 降级：SIMD CPU 张量后端 + 托管粒子对求解器
            ' ------------------------------------------------------------------
            Try
                tfCompute.SIMDTensorF.Register()
            Catch ex As Exception
                log.Add($"切换 SIMD 后端失败（保持现有后端）: {ex.Message}")
            End Try

            Dim cpuSolver As New CpuPairwiseSolver()
            log.Add($"粒子对力求解器: {cpuSolver.Name}（托管 O(N²)，建议粒子数 ≤ 4096）")
            log.Add($"TensorF 后端: {tf.TensorF.computeKernelF.Name}")

            Return New ComputeBackend(cpuSolver, False, String.Join(Environment.NewLine, log))
        End Function

        ''' <summary>
        ''' 注入自定义内核源码并取得「唯一的」CUDA 引擎。
        ''' </summary>
        Private Shared Function TryAttachGpu(ByRef engine As ILCudaRuntime.CudaEngine,
                                            ByRef reason As String,
                                            log As List(Of String)) As Boolean
            Try
                ' 必须在 CudaEngine.TryCreate 之前完成
                ILCudaKernels.KernelSources.Register(GetType(ComputeBackend).Assembly)
                log.Add("已注入自定义内核源码: Kernels/autopoiesis_physics.cu")

                ' 已经注册过（例如界面重启仿真）时直接复用既有引擎，绝不新建第二个上下文
                Dim existing As ILCudaGpu.CudaTensor = ILCudaGpu.CudaTensor.Current

                If existing IsNot Nothing Then
                    engine = existing.Engine
                    log.Add("复用已注册的 CUDA 引擎（未新建上下文）")
                    Return True
                End If

                Dim options As New ILCudaRuntime.EngineOptions()

                If Not ILCudaGpu.CudaTensor.Register(options) Then
                    reason = FirstNonEmpty(ILCudaGpu.CudaTensor.LastError, options.ErrorMessage, "CudaTensor.Register 返回 False")

                    ' 尝试更轻量的单精度注册（虽然拿不到引擎句柄，但至少让张量栈走上 GPU）
                    If ILCudaGpu.CudaTensorF.Register(options) Then
                        Dim floatName As String = If(ILCudaGpu.CudaTensorF.Current Is Nothing,
                                                     "CUDA-F32", ILCudaGpu.CudaTensorF.Current.Name)

                        log.Add($"双精度栈注册失败，但单精度栈 {floatName} 已启用；" &
                                "由于无法取得引擎句柄，粒子对力仍走 CPU")
                        reason &= "（单精度栈已启用，但自定义内核无法取得引擎句柄）"
                    End If

                    Return False
                End If

                Dim current As ILCudaGpu.CudaTensor = ILCudaGpu.CudaTensor.Current

                If current Is Nothing Then
                    reason = "CudaTensor.Register 成功但没有返回当前后端实例"
                    Return False
                End If

                engine = current.Engine
                log.Add($"CUDA 引擎就绪；TensorF 单精度后端 = {tf.TensorF.computeKernelF.Name}")
                Return True
            Catch ex As Exception
                reason = $"{ex.GetType().Name}: {ex.Message}"
                Return False
            End Try
        End Function

        Private Shared Sub AppendFloatKernelReport(log As List(Of String))
            Dim backend As ILCudaGpu.CudaTensorF = ILCudaGpu.CudaTensorF.Current

            If backend Is Nothing Then Return

            Try
                For Each line As String In backend.DescribeKernels().Split({vbLf, vbCrLf}, StringSplitOptions.RemoveEmptyEntries)
                    log.Add("  " & line.Trim())
                Next
            Catch ex As Exception
                log.Add($"  <内核清单不可用: {ex.Message}>")
            End Try
        End Sub

        Private Shared Function FirstNonEmpty(ParamArray values As String()) As String
            For Each value As String In values
                If Not String.IsNullOrWhiteSpace(value) Then Return value
            Next

            Return String.Empty
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            ' 引擎由 ILCudaTensor 的后端持有（CudaTensor.Current），这里绝不释放；
            ' 只需要释放本对象创建的求解器（它会释放自己的显存缓冲）。
            If m_solver Is Nothing Then Return

            Try
                m_solver.Dispose()
            Catch ex As Exception
                ' 释放失败不应影响退出流程
            End Try
        End Sub

    End Class

End Namespace
