using Avalonia;
using Launcher.Core;

namespace Launcher.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Logger.Start(Path.Combine(UserSettings.DefaultFolder, "launcher.log"));
        // anything that slips through is written to the log before the launcher goes down
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("The launcher crashed", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("A background task failed", e.Exception);
            e.SetObserved();
        };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Logger.Error("The launcher crashed", e);
            throw;
        }
    }

    // Also used by the XAML previewer
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
