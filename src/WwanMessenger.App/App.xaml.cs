using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WwanMessenger.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log("DOMAIN", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => Log("TASK", ex.Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("DISPATCHER", e.Exception);
        e.Handled = true; // keep the console alive; the error is visible in the log and status line
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WwanMessenger");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { /* nothing left to do */ }
    }
}
