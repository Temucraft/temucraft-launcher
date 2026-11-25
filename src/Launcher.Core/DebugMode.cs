using System.Text;

namespace Launcher.Core;

/// <summary>The "Debug mode" option: its On option puts TemucraftDebug.txt next to Wow.exe, which tells TemucraftCrash
/// (version.dll) to write Logs\TemucraftDebug.log (Lua errors from the game and the login screens, packets with
/// TemucraftDebug-packets.txt). While it is on the launcher also turns on the game's own error popups and taint log
/// and starts the game with its console (-console, the ` key).</summary>
public static class DebugMode
{
    public const string Group = "Debug mode";
    public const string Marker = "TemucraftDebug.txt";

    static readonly (string CVar, string Value)[] CVars = [("scriptErrors", "1"), ("taintLog", "2")];

    /// <summary>The Config.wtf settings debug mode turns on (each game version keeps its own).</summary>
    public static IEnumerable<string> CVarNames => CVars.Select(c => c.CVar);

    /// <summary>On when the option's file is in the game folder (works before the launcher has talked to the server).</summary>
    public static bool IsOn(string wowFolder) => File.Exists(Path.Combine(wowFolder, Marker));

    /// <summary>Turns the game's debug settings on in Config.wtf, or back off when the launcher had turned them on
    /// (<paramref name="applied"/> remembers that, so a player's own scriptErrors stays when debug mode was never on).
    /// Returns true when Config.wtf changed.</summary>
    public static bool Apply(string wowFolder, bool on, ref bool applied)
    {
        ConfigWtf config = ConfigWtf.Load(wowFolder);
        bool changed = false;
        if (on)
        {
            foreach ((string cvar, string value) in CVars)
                if (config.Get(cvar) != value) { config.Set(cvar, value); changed = true; }
            applied = true;
        }
        else if (applied)
        {
            foreach ((string cvar, _) in CVars)
                if (config.Get(cvar) is not null) { config.Set(cvar, null); changed = true; }
            applied = false;
        }
        if (changed) config.Save();
        return changed;
    }

    /// <summary>Command line for Wow.exe: the console while debug mode is on.</summary>
    public static IReadOnlyList<string> Arguments(bool on) => on ? ["-console"] : [];

    /// <summary>The ends of Logs\TemucraftDebug.log, Logs\TemucraftPerf.log (TemucraftCrash's performance log, written
    /// in debug mode; its last <paramref name="perfLines"/> lines) and Logs\taint.log for Copy diagnostics (nothing for a
    /// log that doesn't exist). The logs are read while the game may still be writing them.</summary>
    public static List<string> LogTails(string wowFolder, int lines = 80, int perfLines = 60)
    {
        List<string> result = [];
        foreach ((string name, string title, int count) in new[]
                 {
                     ("TemucraftDebug.log", "Debug log", lines), ("TemucraftPerf.log", "Performance log", perfLines),
                     ("taint.log", "Taint log", lines),
                 })
        {
            string path = Path.Combine(wowFolder, "Logs", name);
            if (!File.Exists(path)) continue;
            try
            {
                List<string> all = [];
                using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new(stream, Encoding.UTF8))
                    while (reader.ReadLine() is { } line) all.Add(line);
                result.Add("");
                result.Add($"{title} (Logs\\{name}, last {Math.Min(count, all.Count)} of {all.Count} lines):");
                foreach (string line in all.Skip(Math.Max(0, all.Count - count))) result.Add("  " + line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Add($"{title}: can't read ({e.Message})"); }
        }
        return result;
    }
}
