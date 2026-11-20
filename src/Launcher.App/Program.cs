using Avalonia;
using Launcher.Core;

namespace Launcher.App;

internal static class Program
{
    static Mutex? _single;

    [STAThread]
    public static int Main(string[] args)
    {
        // one launcher at a time: a second start brings the first one to the front and closes
        _single = new Mutex(true, @"Local\TemucraftLauncher", out bool first);
        if (!first)
        {
            SingleInstance.SignalFirst();
            return 0;
        }
        SingleInstance.Listen();
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
