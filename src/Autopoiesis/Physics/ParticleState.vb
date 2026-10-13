Imports System.Drawing

Namespace Physics

    ''' <summary>
    ''' 模拟的宿主侧完整状态：位置、速度、类型、交互矩阵、类型颜色，以及内核的输出缓冲。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 所有数组都是按分量拆开的连续 <see cref="Single"/> 数组（SoA），这样可以直接
    ''' <c>Write</c> 到 <c>DeviceBuffer(Of Single)</c>，也不需要在主机侧做交织/解交织。
    ''' </para>
    ''' <para>
    ''' 交互矩阵 <see cref="ForceMatrix"/> 的长度恒为 <c>ForceMatrixStride²</c>（100），
    ''' 索引 <c>t_a + t_b * ForceMatrixStride</c>，即「i 被 j 吸引的程度」；矩阵一般不对称，
    ''' 这正是系统非互易性的来源。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class ParticleState

        Private m_posX As Single()
        Private m_posY As Single()
        Private m_velX As Single()
        Private m_velY As Single()
        Private m_types As Integer()
        Private m_forceX As Single()
        Private m_forceY As Single()
        Private m_density As Single()

        Private m_forceMatrixSeeded As Boolean = False
        Private m_colorsSeeded As Boolean = False

        Public Sub New()
            m_posX = New Single(1) {}
            m_posY = New Single(1) {}
            m_velX = New Single(1) {}
            m_velY = New Single(1) {}
            m_types = New Integer(1) {}
            m_forceX = New Single(1) {}
            m_forceY = New Single(1) {}
            m_density = New Single(1) {}

            ForceMatrix = New Single(SimulationParameters.ForceMatrixStride * SimulationParameters.ForceMatrixStride - 1) {}
            TypeColors = DefaultColors()
            ParticleCount = 0
        End Sub

        ''' <summary>位置 x（长度 = <see cref="ParticleCount"/>）</summary>
        Public ReadOnly Property PosX As Single()
            Get
                Return m_posX
            End Get
        End Property

        ''' <summary>位置 y</summary>
        Public ReadOnly Property PosY As Single()
            Get
                Return m_posY
            End Get
        End Property

        ''' <summary>速度 vx</summary>
        Public ReadOnly Property VelX As Single()
            Get
                Return m_velX
            End Get
        End Property

        ''' <summary>速度 vy</summary>
        Public ReadOnly Property VelY As Single()
            Get
                Return m_velY
            End Get
        End Property

        ''' <summary>粒子类型（取值 [0, T)）</summary>
        Public ReadOnly Property Types As Integer()
            Get
                Return m_types
            End Get
        End Property

        ''' <summary>内核返回值：未限幅的合力 x</summary>
        Public ReadOnly Property ForceX As Single()
            Get
                Return m_forceX
            End Get
        End Property

        ''' <summary>内核返回值：未限幅的合力 y</summary>
        Public ReadOnly Property ForceY As Single()
            Get
                Return m_forceY
            End Get
        End Property

        ''' <summary>内核返回值：核密度估计 ρ</summary>
        Public ReadOnly Property Density As Single()
            Get
                Return m_density
            End Get
        End Property

        ''' <summary>
        ''' 交互矩阵（长度 = <c>ForceMatrixStride²</c>）。
        ''' </summary>
        ''' <remarks>
        ''' 「i 被 j 吸引的程度」：元素 <c>t_a + t_b * ForceMatrixStride</c>；
        ''' 一般不对称（<c>A_ij ≠ A_ji</c>），从而打破牛顿第三定律、使动量不守恒。
        ''' </remarks>
        Public ReadOnly Property ForceMatrix As Single()

        ''' <summary>每种类型的颜色（长度 = <c>MaxParticleTypes</c>）</summary>
        Public ReadOnly Property TypeColors As Color()

        ''' <summary>当前粒子数量</summary>
        Public Property ParticleCount As Integer

        ''' <summary>
        ''' 按参考实现 <c>particles.gd</c> 的默认调色板创建 10 个类型颜色。
        ''' </summary>
        Public Shared Function DefaultColors() As Color()
            Dim colors(SimulationParameters.MaxParticleTypes - 1) As Color

            colors(0) = Color.FromArgb(255, 0, 255, 255)    ' 青
            colors(1) = Color.FromArgb(255, 255, 0, 0)      ' 红
            colors(2) = Color.FromArgb(255, 0, 255, 0)      ' 绿
            colors(3) = Color.FromArgb(255, 255, 0, 255)    ' 品红
            colors(4) = Color.FromArgb(255, 255, 255, 0)    ' 黄
            colors(5) = Color.FromArgb(255, 0, 0, 255)      ' 蓝
            colors(6) = Color.FromArgb(255, 255, 128, 0)    ' 橙
            colors(7) = Color.FromArgb(255, 128, 0, 255)    ' 紫
            colors(8) = Color.FromArgb(255, 0, 255, 128)    ' 青绿
            colors(9) = Color.FromArgb(255, 255, 255, 255)  ' 白

            Return colors
        End Function

        ''' <summary>按类型取颜色；类型越界时回退到白色</summary>
        Public Function ColorOf(particleType As Integer) As Color
            If particleType < 0 OrElse particleType >= TypeColors.Length Then
                Return Color.White
            End If

            Return TypeColors(particleType)
        End Function

        ''' <summary>读取交互矩阵元素 <c>A[t_a][t_b]</c>（i 被 j 吸引的程度）</summary>
        Public Function GetForce(typeA As Integer, typeB As Integer) As Single
            Return ForceMatrix(IndexOfForce(typeA, typeB))
        End Function

        ''' <summary>写入交互矩阵元素（自动夹到 [-1, 1]）</summary>
        Public Sub SetForce(typeA As Integer, typeB As Integer, value As Single)
            ForceMatrix(IndexOfForce(typeA, typeB)) = Clamp(value, -1.0F, 1.0F)
        End Sub

        ''' <summary>交互矩阵的下标：<c>t_a + t_b * ForceMatrixStride</c></summary>
        Public Shared Function IndexOfForce(typeA As Integer, typeB As Integer) As Integer
            Return typeA + typeB * SimulationParameters.ForceMatrixStride
        End Function

        ''' <summary>
        ''' 按参考实现 <c>particles.gd</c> 的 <c>gen_data</c> 重新生成整个状态。
        ''' </summary>
        ''' <param name="params">运行参数（决定世界尺寸、粒子数量、类型数量与种子）</param>
        ''' <param name="resetForces">是否重新随机交互矩阵</param>
        ''' <param name="resetColors">是否恢复默认调色板</param>
        ''' <remarks>
        ''' 参考实现在每次生成前重新 <c>seed(...)</c>，因此同一个种子必然得到同一套撒点与
        ''' 力矩阵；这里沿用同样的做法，保证「重置」是可复现的。
        ''' </remarks>
        Public Sub Generate(params As SimulationParameters, resetForces As Boolean, resetColors As Boolean)
            Dim count As Integer = System.Math.Max(2, params.ParticleCount)
            Dim nTypes As Integer = Clamp(params.ParticleTypes, 1, SimulationParameters.MaxParticleTypes)

            EnsureCapacity(count)

            Dim rng As New System.Random(params.Seed)

            ' ---- 撒点 ----
            If count = 2 Then
                ' 参考实现为「两颗粒子」准备了中心 ±30 的确定性初始条件
                m_posX(0) = params.RegionWidth * 0.5F - 30.0F
                m_posY(0) = params.RegionHeight * 0.5F
                m_types(0) = rng.Next(0, nTypes)

                m_posX(1) = params.RegionWidth * 0.5F + 30.0F
                m_posY(1) = params.RegionHeight * 0.5F
                m_types(1) = rng.Next(0, nTypes)
            Else
                For i As Integer = 0 To count - 1
                    m_posX(i) = CSng(rng.NextDouble()) * params.RegionWidth
                    m_posY(i) = CSng(rng.NextDouble()) * params.RegionHeight
                    m_types(i) = rng.Next(0, nTypes)
                Next
            End If

            ' ---- 速度与输出缓冲归零 ----
            System.Array.Clear(m_velX, 0, count)
            System.Array.Clear(m_velY, 0, count)
            System.Array.Clear(m_forceX, 0, count)
            System.Array.Clear(m_forceY, 0, count)
            System.Array.Clear(m_density, 0, count)

            ' ---- 交互矩阵 ----
            If resetForces OrElse Not m_forceMatrixSeeded Then
                m_forceMatrixSeeded = True
                RandomizeForceMatrix(params.Seed)
            End If

            ' ---- 类型颜色 ----
            If resetColors OrElse Not m_colorsSeeded Then
                m_colorsSeeded = True
                System.Array.Copy(DefaultColors(), TypeColors, TypeColors.Length)
            End If
        End Sub

        ''' <summary>重新随机交互矩阵（界面上的「重新生成力矩阵」）</summary>
        Public Sub RandomizeForceMatrix(seed As Integer)
            Dim rng As New System.Random(seed)

            For i As Integer = 0 To ForceMatrix.Length - 1
                ForceMatrix(i) = CSng(rng.NextDouble()) * 2.0F - 1.0F
            Next
        End Sub

        ''' <summary>把交互矩阵全部归零</summary>
        Public Sub ClearForceMatrix()
            System.Array.Clear(ForceMatrix, 0, ForceMatrix.Length)
        End Sub

        ''' <summary>交互矩阵是否已经完全归零（全零矩阵会让整个系统静止不动）</summary>
        Public Function IsForceMatrixAllZero() As Boolean
            For i As Integer = 0 To ForceMatrix.Length - 1
                If ForceMatrix(i) <> 0.0F Then Return False
            Next

            Return True
        End Function

        ''' <summary>
        ''' 把数组长度精确调整为 <paramref name="count"/>。
        ''' </summary>
        ''' <remarks>
        ''' 刻意**不做**超额分配：设备缓冲与页锁定中转区都要求长度与粒子数严格一致
        ''' （<c>DeviceBuffer.Write</c> / <c>PinnedHostBuffer.ReadInto</c> 会按数组长度
        ''' 决定拷贝字节数），精确长度可以彻底消除长度不匹配的隐患。容量的变化只发生
        ''' 在参数调整时，不在每帧的热路径上。
        ''' </remarks>
        Public Sub EnsureCapacity(count As Integer)
            ParticleCount = count

            If m_posX.Length = count Then Return

            m_posX = Grow(m_posX, count)
            m_posY = Grow(m_posY, count)
            m_velX = Grow(m_velX, count)
            m_velY = Grow(m_velY, count)
            m_forceX = Grow(m_forceX, count)
            m_forceY = Grow(m_forceY, count)
            m_density = Grow(m_density, count)
            m_types = Grow(m_types, count)
        End Sub

        Private Shared Function Grow(buffer As Single(), capacity As Integer) As Single()
            If buffer.Length = capacity Then Return buffer

            Dim grown(capacity - 1) As Single
            System.Array.Copy(buffer, grown, System.Math.Min(buffer.Length, capacity))
            Return grown
        End Function

        Private Shared Function Grow(buffer As Integer(), capacity As Integer) As Integer()
            If buffer.Length = capacity Then Return buffer

            Dim grown(capacity - 1) As Integer
            System.Array.Copy(buffer, grown, System.Math.Min(buffer.Length, capacity))
            Return grown
        End Function

        Private Shared Function Clamp(value As Single, low As Single, high As Single) As Single
            If value < low Then Return low
            If value > high Then Return high
            Return value
        End Function

    End Class

End Namespace
