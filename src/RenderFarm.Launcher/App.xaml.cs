using System.Runtime.InteropServices;
using System.Windows;

namespace RenderFarm.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            Shutdown(RenderFarmLauncher.Run(e.Args));
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            FreeConsole();
        }

        var window = new MainWindow();
        window.Show();
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}
