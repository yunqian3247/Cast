namespace cast.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ApplicationConfiguration.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        { if (e.ExceptionObject is Exception exception) DiagnosticLog.Write("未处理异常", exception); };
        Application.ThreadException += (_, e) =>
        {
            DiagnosticLog.Write("界面异常", e.Exception);
            MessageBox.Show("界面操作发生异常，详细信息已保存到诊断目录。\n" + e.Exception.Message, "cast", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        using var mutex = new Mutex(true, "cast.SingleInstance", out var isOwner);
        if (!isOwner)
        {
            MessageBox.Show("串口调试工具已经在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm());
    }
}
