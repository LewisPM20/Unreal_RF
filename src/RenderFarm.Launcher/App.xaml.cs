using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RenderFarm.Launcher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

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

        try
        {
            var window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void ShowFatalError(Exception exception)
    {
        MessageBox.Show(
            "RenderFarm Launcher hit an unexpected startup error.\n\n" + exception,
            "RenderFarm Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}
