using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Launcher.App;

/// <summary>Only one launcher runs: a second start asks the first (over a named pipe) to show its window, then exits.</summary>
static class SingleInstance
{
    const string Pipe = "TemucraftLauncher-show";

    public static void SignalFirst()
    {
        try
        {
            using NamedPipeClientStream client = new(".", Pipe, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch (Exception error) { Launcher.Core.Logger.Warning("Couldn't reach the launcher that is already open", error); }
    }

    public static void Listen()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using NamedPipeServerStream server = new(Pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    server.ReadByte();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window w })
                        {
                            w.Show();
                            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                            w.Activate();
                            w.Topmost = true;
                            w.Topmost = false;
                        }
                    });
                }
                catch (Exception error)
                {
                    Launcher.Core.Logger.Warning("Single-instance listener", error);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
