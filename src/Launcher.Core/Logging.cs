using System.Reflection;
using System.Runtime.InteropServices;

namespace Launcher.Core;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>One line of the log, as the window shows it (no stack traces: those only go to the file).</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// The launcher's log: %AppData%\TemucraftLauncher\launcher.log, safe to use from any thread. Errors are written with
/// their full details (exception type and stack trace) so a problem can be found afterwards. When the file passes
/// <see cref="MaxBytes"/> it is renamed to launcher.1.log (then .2, .3) and a new one is started, so it never grows
/// without end. Passwords are never logged.
/// </summary>
public static class Logger
{
    static readonly object Gate = new();
    static string? _file;

    public static long MaxBytes { get; set; } = 1 << 20;
    public static int KeepOldFiles { get; set; } = 3;
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for each line at or above <see cref="MinimumLevel"/>, on the thread that logged it.</summary>
    public static event Action<LogEntry>? Written;

    public static string? File => _file;

    /// <summary>Starts logging to a file and writes a header with the launcher version and system.</summary>
    public static void Start(string file)
    {
        lock (Gate) _file = file;
        Info("──────── Temucraft Launcher " + LauncherInfo.Version + " started ────────", toWindow: false);
        Info($"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}, " +
             $"exe {Environment.ProcessPath}", toWindow: false);
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null, true);
    public static void Info(string message, bool toWindow = true) => Write(LogLevel.Info, message, null, toWindow);
    public static void Warning(string message, Exception? error = null) => Write(LogLevel.Warning, message, error, true);
    public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error, true);

    static void Write(LogLevel level, string message, Exception? error, bool toWindow)
    {
        if (level < MinimumLevel) return;
        DateTime now = DateTime.Now;
        string shown = error is null ? message : $"{message}: {error.Message}";
        string tag = level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warning => "WARN ", _ => "ERROR" };
        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {shown}";
        if (error is not null) line += Environment.NewLine + Indent(error.ToString());

        lock (Gate)
        {
            if (_file is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    Rotate(_file);
                    System.IO.File.AppendAllText(_file, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (toWindow)
            try { Written?.Invoke(new LogEntry(now, level, shown)); } catch (Exception) { }
    }

    static string Indent(string text) => "    " + text.Replace(Environment.NewLine, Environment.NewLine + "    ");

    static void Rotate(string file)
    {
        if (!System.IO.File.Exists(file) || new FileInfo(file).Length < MaxBytes) return;
        string Old(int n) => Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.{n}{Path.GetExtension(file)}");
        if (System.IO.File.Exists(Old(KeepOldFiles))) System.IO.File.Delete(Old(KeepOldFiles));
        for (int n = KeepOldFiles - 1; n >= 1; n--)
            if (System.IO.File.Exists(Old(n))) System.IO.File.Move(Old(n), Old(n + 1));
        System.IO.File.Move(file, Old(1));
    }
}

public static class LauncherInfo
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.1.0";

    /// <summary>The same User-Agent for the server and GitHub.</summary>
    public static string UserAgent => "TemucraftLauncher/" + Version;
}
