Imports System.Drawing

Namespace Rendering

    ''' <summary>
    ''' 预分配的实例化点云缓冲：每个粒子 8 个 <see cref="Single"/>（32 字节）。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 内存布局必须与 <c>DXApi</c> 的 <c>GpuSceneGeometry.PointInstance</c> 结构、
    ''' 也就是 <c>Scene3DInputLayout.PointInstanceStride</c>（32 字节）严格一致：
    ''' </para>
    ''' <code>
    ''' [0..2] X, Y, Z      世界坐标（Z 恒为 0：所有粒子同深，正交俯视投影才恒定）
    ''' [3..5] NX, NY, NZ   法线，调色板模式下未用，这里填 0
    ''' [6]    Heat         嵌入色模式下被顶点着色器复用为「逐点尺寸因子」，填 1
    ''' [7]    R G B A      打包色（4 字节，小端顺序 R|G&lt;&lt;8|B&lt;&lt;16|A&lt;&lt;24）
    ''' </code>
    ''' <para>
    ''' 为什么第 8 个 WORD 能当颜色用：<c>PS_Point</c> 在
    ''' <c>PointMode = Heat</c>（场景无面片）且 <c>UseEmbeddedColor = True</c> 且
    ''' <c>alpha &gt; 0</c> 时会直接返回逐点嵌入色；同一条件下 <c>VS_Point</c> 把
    ''' <c>heat</c> 槽当作尺寸因子。这正是我们需要的——原版每种粒子类型一个颜色，
    ''' 而 <c>SceneRenderOptions.ColorScheme</c> 是被硬编码成枚举的调色板，
    ''' 无法注入任意 10 个离散类别色。
    ''' </para>
    ''' <para>
    ''' 复用同一段数组、逐帧只做写入，可消除每帧的 LOH 分配：N=10⁵ 时是 3.2 MB/帧。
    ''' </para>
    ''' </remarks>
    Public NotInheritable Class ParticleInstanceBuffer

        ''' <summary>每个粒子占用的 float 个数</summary>
        Public Const WordsPerParticle As Integer = 8

        ''' <summary>每个粒子占用的字节数（= <c>PointInstanceStride</c>）</summary>
        Public Const BytesPerParticle As Integer = WordsPerParticle * 4

        ''' <summary>逐点尺寸因子的默认值（填 0 会被着色器夹到 0.05，粒子会缩到看不见）</summary>
        Public Const DefaultSizeScale As Single = 1.0F

        Private m_data As Single()

        Public Sub New()
            m_data = New Single(WordsPerParticle - 1) {}
        End Sub

        ''' <summary>实例缓冲本体（长度 = <c>Capacity * 8</c>）</summary>
        Public ReadOnly Property Data As Single()
            Get
                Return m_data
            End Get
        End Property

        ''' <summary>当前容量（可容纳的粒子数）</summary>
        Public ReadOnly Property Capacity As Integer
            Get
                Return m_data.Length \ WordsPerParticle
            End Get
        End Property

        Public Sub EnsureCapacity(count As Integer)
            If count <= 0 Then count = 1
            If m_data.Length >= count * WordsPerParticle Then Return

            m_data = New Single(count * WordsPerParticle - 1) {}
        End Sub

        ''' <summary>
        ''' 按粒子状态填充实例数据。
        ''' </summary>
        ''' <param name="state">粒子状态</param>
        ''' <param name="sizeScale">逐点尺寸因子（1 表示使用 <c>Options.PointSize</c>）</param>
        Public Sub Fill(state As Physics.ParticleState, Optional sizeScale As Single = DefaultSizeScale)
            Dim count As Integer = state.ParticleCount

            EnsureCapacity(count)

            Dim positionsX As Single() = state.PosX
            Dim positionsY As Single() = state.PosY
            Dim particleTypes As Integer() = state.Types
            Dim colors As Color() = state.TypeColors
            Dim data As Single() = m_data

            ' 颜色按类型先打包成一个很小的查找表，避免每个粒子都做一次位打包
            Dim packed(Physics.SimulationParameters.MaxParticleTypes - 1) As Single

            For t As Integer = 0 To packed.Length - 1
                packed(t) = PackColor(If(t < colors.Length, colors(t), Color.White))
            Next

            For i As Integer = 0 To count - 1
                Dim offset As Integer = i * WordsPerParticle

                data(offset + 0) = positionsX(i)
                data(offset + 1) = positionsY(i)
                data(offset + 2) = 0.0F
                data(offset + 3) = 0.0F
                data(offset + 4) = 0.0F
                data(offset + 5) = 0.0F
                data(offset + 6) = sizeScale

                Dim particleType As Integer = particleTypes(i)
                data(offset + 7) = If(particleType >= 0 AndAlso particleType < packed.Length,
                                      packed(particleType), packed(0))
            Next
        End Sub

        ''' <summary>把 <see cref="Color"/> 打包成 <c>R|G&lt;&lt;8|B&lt;&lt;16|A&lt;&lt;24</c> 的位模式</summary>
        Private Shared Function PackColor(color As Color) As Single
            Dim packed As Integer = color.R Or (color.G << 8) Or (color.B << 16) Or (color.A << 24)
            Return BitConverter.Int32BitsToSingle(packed)
        End Function

    End Class

End Namespace
