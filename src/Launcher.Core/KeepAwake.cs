using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher.Core;

/// <summary>
/// Keeps the computer from going to sleep while the launcher works (downloads, installs, checks): locking the
/// screen is fine, but a PC that sleeps drops the download. The screen may still turn off. Dispose to let the
/// computer sleep again.
/// <list type="bullet">
/// <item>Windows: SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) on the calling thread (the window's);
/// dispose on that same thread.</item>
/// <item>macOS: <c>caffeinate -i -w &lt;launcher pid&gt;</c> while it runs.</item>
/// <item>Linux: <c>systemd-inhibit --what=sleep ... sleep infinity</c> while it runs.</item>
/// </list>
/// Where none of these is available it quietly does nothing.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    readonly Process? _helper;
    bool _windows, _done;

    public bool Active { get; }

    public KeepAwake(string why)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _windows = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
                Active = _windows;
            }
            else if (OperatingSystem.IsMacOS())
            {
                _helper = Start("caffeinate", ["-i", "-w", Environment.ProcessId.ToString()]);
                Active = _helper is not null;
            }
            else if (OperatingSystem.IsLinux())
            {
                _helper = Start("systemd-inhibit", ["--what=sleep:idle", "--who=Temucraft Launcher", $"--why={why}", "--mode=block", "sleep", "infinity"]);
                Active = _helper is not null;
            }
        }
        catch (Exception e) { Logger.Warning("Couldn't keep the computer awake", e); }
        if (Active) Logger.Info($"Keeping the computer awake: {why}", toWindow: false);
    }

    static Process? Start(string file, string[] args)
    {
        try
        {
            ProcessStartInfo start = new(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            return Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // the tool isn't installed
    }

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        try
        {
            if (_windows) SetThreadExecutionState(EsContinuous);   // back to normal: the computer may sleep again
            if (_helper is { HasExited: false }) _helper.Kill();
            _helper?.Dispose();
        }
        catch (Exception e) { Logger.Warning("Couldn't let the computer sleep again", e); }
    }
}
