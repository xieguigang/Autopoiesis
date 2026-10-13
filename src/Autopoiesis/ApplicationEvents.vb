Imports Microsoft.VisualBasic.ApplicationServices

Namespace My
    ' The following events are available for MyApplication:
    ' Startup: Raised when the application starts, before the startup form is created.
    ' Shutdown: Raised after all application forms are closed. This event is not raised if the application terminates abnormally.
    ' UnhandledException: Raised if the application encounters an unhandled exception.
    ' StartupNextInstance: Raised when launching a single-instance application and the application is already active.
    ' NetworkAvailabilityChanged: Raised when the network connection is connected or disconnected.

    ' **NEW** ApplyApplicationDefaults: Raised when the application queries default values to be set for the application.

    ' Example:
    ' Private Sub MyApplication_ApplyApplicationDefaults(sender As Object, e As ApplyApplicationDefaultsEventArgs) Handles Me.ApplyApplicationDefaults
    '
    '   ' Setting the application-wide default Font:
    '   e.Font = New Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular)
    '
    '   ' Setting the HighDpiMode for the Application:
    '   e.HighDpiMode = HighDpiMode.PerMonitorV2
    '
    '   ' If a splash dialog is used, this sets the minimum display time:
    '   e.MinimumSplashScreenDisplayTime = 4000
    ' End Sub

    Partial Friend Class MyApplication

        ''' <summary>
        ''' 命令行模式：带 <c>--selftest</c> 时只运行自测并直接结束进程。
        ''' </summary>
        ''' <remarks>
        ''' Startup 事件早于 <c>OnCreateMainForm</c>，因此在事件里结束进程就不会创建主窗体，
        ''' 这样 <c>Autopoiesis.exe --selftest</c> 就是一个纯粹的控制台模式（把标准输出重定向
        ''' 到文件即可看到完整报告，报告同时落盘到可执行文件目录）。
        ''' </remarks>
        Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
            Dim runSelfTest As Boolean = False

            For Each argument As String In e.CommandLine
                If String.Equals(argument, "--selftest", StringComparison.OrdinalIgnoreCase) Then
                    runSelfTest = True
                End If
            Next

            If Not runSelfTest Then Return

            Dim logFile As String = IO.Path.Combine(AppContext.BaseDirectory, "autopoiesis_selftest.log")
            Dim failures As Integer = Global.Autopoiesis.SelfTest.AutopoiesisSelfTest.Run(logFile)

            Environment.Exit(failures)
        End Sub

    End Class
End Namespace
