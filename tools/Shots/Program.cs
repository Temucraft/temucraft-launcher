using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Launcher.App;

// Renders the launcher's pages to PNGs (headless): Shots <out folder> [width height]
string outDir = args.Length > 0 ? args[0] : "shots";
int width = args.Length > 2 ? int.Parse(args[1]) : 1280, height = args.Length > 2 ? int.Parse(args[2]) : 800;
Directory.CreateDirectory(outDir);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

MainWindow w = new() { Width = width, Height = height };
w.Show();
void Pump(double seconds)
{
    DateTime end = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < end) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(20); }
}
void Shot(string name) { Pump(0.6); w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png")); Console.WriteLine("shot " + name); }
T? Find<T>(string n) where T : Control => w.FindControl<T>(n);
object? Call(string method, params object?[] a) =>
    typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(w, a);

Pump(2);
Shot("00-start");
if (Find<Grid>("LoginOverlay") is { IsVisible: true })
{
    Find<TextBox>("LoginUser")!.Text = "ryan";
    Find<TextBox>("LoginPass")!.Text = "x";
    Call("OnLogin", Find<Button>("LoginButton"), new RoutedEventArgs());
}
Pump(8);
Shot("01-news");
string[] tabs = ["NewsTab", "ModsTab", "AddonsTab", "ScreensTab", "TweaksTab", "SettingsTab", "LauncherTab"];
foreach (string extra in args.Skip(3)) { }
for (int i = 0; i < tabs.Length; i++)
{
    Button tab = Find<Button>(tabs[i])!;
    Call("OnTab", tab, new RoutedEventArgs());
    Pump(1.5);
    Shot($"{i + 2:00}-{tabs[i].Replace("Tab", "").ToLowerInvariant()}");
}
// screens: character view, scrolled
Call("OnTab", Find<Button>("ScreensTab"), new RoutedEventArgs());
if (typeof(MainWindow).GetMethod("ShowScreensView", BindingFlags.Instance | BindingFlags.NonPublic) is { } sv0)
{
    sv0.Invoke(w, [false]);
    Shot("20-screens-characters");
    if (w.FindControl<StackPanel>("ScreensCharList")?.Parent?.Parent is ScrollViewer ssv)
        for (int k = 1; k <= 3; k++) { ssv.Offset = new Vector(0, k * (height - 300)); Shot($"21-screens-characters-scroll{k}"); }
    sv0.Invoke(w, [true]);
}
// settings page with everything open, then scrolled
Call("OnTab", Find<Button>("TweaksTab"), new RoutedEventArgs());
if (w.FindControl<StackPanel>("TweaksMenu")?.Children.OfType<Button>().FirstOrDefault() is Button all)
{
    all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump(1.5);
    Shot("10-settings-expanded");
    if (w.FindControl<StackPanel>("TweaksList")?.Parent is ScrollViewer sv)
        for (int k = 1; k <= 6; k++) { sv.Offset = new Vector(0, k * (height - 250)); Shot($"11-settings-scroll{k}"); }
}
Call("OnTab", Find<Button>("ModsTab"), new RoutedEventArgs());
if (w.FindControl<WrapPanel>("OptionsList")?.Parent?.Parent is ScrollViewer msv)
    for (int k = 1; k <= 3; k++) { msv.Offset = new Vector(0, k * (height - 250)); Shot($"12-mods-scroll{k}"); }
w.Close();
