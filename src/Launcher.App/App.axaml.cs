using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // an error in a button handler or a window update is logged and shown, instead of closing the launcher
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Logger.Error("Something went wrong", e.Exception);
            e.Handled = true;
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
