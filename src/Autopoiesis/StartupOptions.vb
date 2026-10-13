''' <summary>
''' 命令行启动选项。
''' </summary>
''' <remarks>
''' <para>
''' 支持三种模式：
''' </para>
''' <list type="bullet">
''' <item><c>--selftest</c>：只跑自测并结束进程（由 <c>MyApplication_Startup</c> 处理，
''' 不会创建主窗体）；</item>
''' <item><c>--snapshot &lt;文件&gt;</c>：正常运行若干帧后把画布截图存成 PNG 并退出，
''' 用于无人值守的渲染验证与出图；</item>
''' <item><c>--frames &lt;n&gt;</c>：配合 <c>--snapshot</c> 指定在第几帧截图（默认 45，
''' 留足时间让 D3D 设备创建完成、粒子云也演化出可见的结构）。</item>
''' </list>
''' </remarks>
Friend Module StartupOptions

    ''' <summary>截图输出文件；为空表示正常交互模式</summary>
    Public SnapshotPath As String = String.Empty

    ''' <summary>在第几帧截图</summary>
    Public SnapshotFrame As Integer = 45

End Module
