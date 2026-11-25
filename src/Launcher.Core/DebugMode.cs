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

    /// <summary>The ends of Logs\TemucraftDebug.log and Logs\taint.log for Copy diagnostics (nothing when neither exists).</summary>
    public static List<string> LogTails(string wowFolder, int lines = 80)
    {
        List<string> result = [];
        foreach ((string name, string title) in new[] { ("TemucraftDebug.log", "Debug log"), ("taint.log", "Taint log") })
        {
            string path = Path.Combine(wowFolder, "Logs", name);
            if (!File.Exists(path)) continue;
            try
            {
                string[] all = File.ReadAllLines(path, Encoding.UTF8);
                result.Add("");
                result.Add($"{title} (Logs\\{name}, last {Math.Min(lines, all.Length)} of {all.Length} lines):");
                foreach (string line in all.Skip(Math.Max(0, all.Length - lines))) result.Add("  " + line);
            }
            catch (IOException e) { result.Add($"{title}: can't read ({e.Message})"); }
        }
        return result;
    }
}
