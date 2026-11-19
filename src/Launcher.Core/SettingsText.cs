namespace Launcher.Core;

/// <summary>
/// The player's settings as plain text, to copy to a friend or another PC and import there:
/// <code>
/// # Temucraft Launcher settings
/// [tweaks]
/// farclip = 1277
/// d3d9.samplerAnisotropy = 16
/// [launcher]
/// theme = wotlk
/// options.DXVK = On
/// </code>
/// Never the login, the password, the game folder or the install ID. Unknown lines are skipped, so text from a newer
/// launcher still imports.
/// </summary>
public static class SettingsText
{
    public const string Header = "# Temucraft Launcher settings";

    public static string Write(IEnumerable<KeyValuePair<string, string>> tweaks, IEnumerable<KeyValuePair<string, string>> launcher)
    {
        List<string> lines = [Header, "[tweaks]"];
        lines.AddRange(tweaks.Select(t => $"{t.Key} = {Clean(t.Value)}"));
        lines.Add("[launcher]");
        lines.AddRange(launcher.Select(l => $"{l.Key} = {Clean(l.Value)}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    static string Clean(string value) => new(value.Where(c => c is not ('\r' or '\n')).ToArray());

    /// <summary>The two sections; null when the text doesn't look like launcher settings at all.</summary>
    public static (Dictionary<string, string> Tweaks, Dictionary<string, string> Launcher)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Dictionary<string, string> tweaks = new(StringComparer.OrdinalIgnoreCase), launcher = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        bool any = false;
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Equals("[tweaks]", StringComparison.OrdinalIgnoreCase)) { section = tweaks; any = true; continue; }
            if (line.Equals("[launcher]", StringComparison.OrdinalIgnoreCase)) { section = launcher; any = true; continue; }
            int equals = line.IndexOf('=');
            if (section is null || equals <= 0) continue;
            string key = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
            if (key.Length > 0) section[key] = value;   // option groups may have spaces ("options.Night sky")
        }
        return any ? (tweaks, launcher) : null;
    }
}
