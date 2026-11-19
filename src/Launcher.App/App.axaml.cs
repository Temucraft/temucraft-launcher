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
        // the language and theme before the window is built, so it opens in them
        try
        {
            UserSettings settings = UserSettings.Load(MainWindow.SettingsFile);
            string? envLanguage = null;
            try { envLanguage = ServerSettings.Load(Path.Combine(AppContext.BaseDirectory, "launcher.env")).Language; }
            catch (Exception) { /* the window says what is wrong with launcher.env */ }
            Look.UseLanguage(settings.Language, envLanguage);
            Look.HookTranslation();
            Look.UseTheme(settings);
        }
        catch (Exception e) { Logger.Warning("Couldn't set the language or theme", e); }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
