using System.IO;
using System.Windows;

namespace PhotoView;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        try
        {
            string? initialPath = e.Args.FirstOrDefault(arg =>
                File.Exists(arg) || Directory.Exists(arg));

            var window = new MainWindow(initialPath);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }

    private static void Log(Exception? ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photoview_error.log"),
                $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { }
    }
}
