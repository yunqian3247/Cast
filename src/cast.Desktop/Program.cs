namespace cast.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "cast.SingleInstance", out var isOwner);
        if (!isOwner)
        {
            MessageBox.Show("串口调试工具已经在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm());
    }
}
