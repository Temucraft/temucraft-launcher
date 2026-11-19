using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// The launcher's look, in two parts like the database website: a mode (dark or light, the base colours) and a theme
/// (Temucraft, or one per expansion: the accent colours). Both are small YAML files, built into the launcher; a
/// "themes" folder next to the launcher can add more or replace them.
/// <list type="bullet">
/// <item><c>dark.yml</c> / <c>light.yml</c>: every colour (see <see cref="Keys"/>)</item>
/// <item><c>&lt;theme&gt;.yml</c>: <c>name</c>, <c>order</c>, any colour for both modes, and <c>dark.&lt;colour&gt;</c> /
/// <c>light.&lt;colour&gt;</c> for one mode only</item>
/// </list>
/// The server picks the default with "theme" and "themeMode" in launcher-policy.json; the player may pick another
/// unless the server enforces it.
/// </summary>
public static class Themes
{
    /// <summary>The colours a mode file sets. Colours are #RRGGBB or #AARRGGBB.</summary>
    public static readonly string[] Keys =
    [
        "background",   // the window
        "panel",        // the boxes the pages sit in (over the background picture)
        "overlay",      // behind the login window
        "shade",        // darkens the background picture
        "accent",       // titles, the active tab and preset, links that need to stand out
        "section",      // section headings (LANGUAGES, BACKUPS...)
        "text",         // normal text
        "textSoft",     // longer text (notices, the addon page)
        "dim",          // explanations under settings
        "dimmer",       // small grey notes
        "good",         // recommended, from the server, on
        "bad",          // errors
        "border",       // lines around panels and cards
        "card",         // option cards
        "cardStrong",   // the login notice box
        "field",        // value boxes on the Launcher page
        "play",         // the PLAY button
        "playText",     // text on the PLAY button
        "update",       // the UPDATE button
        "warning",      // the banner when the launcher is out of date
    ];

    public static readonly string[] Modes = ["dark", "light"];

    /// <summary>A theme file: its id (file name), the name shown, its place in the list and its lines.</summary>
    public sealed record Theme(string Id, string Name, int Order, Dictionary<string, string> Lines);

    public static Theme FromYaml(string id, string yaml)
    {
        Dictionary<string, string> lines = MiniYaml.Parse(yaml);
        string name = lines.Remove("name", out string? n) ? n : id;
        int order = lines.Remove("order", out string? o) && int.TryParse(o, out int i) ? i : 1000;
        return new Theme(id.ToLowerInvariant(), name, order, lines);
    }

    static readonly Regex Colour = new("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");

    public static bool IsColour(string text) => Colour.IsMatch(text);

    /// <summary>
    /// The colours for a mode and a theme: the mode file's, then the theme's own, then the theme's "&lt;mode&gt;."
    /// lines. Anything that isn't a colour is skipped; a colour no file sets falls back to the built-in dark one.
    /// </summary>
    public static Dictionary<string, string> Resolve(Theme? modeFile, Theme? theme, string mode, Theme? fallback = null)
    {
        Dictionary<string, string> colours = new(StringComparer.OrdinalIgnoreCase);
        void Take(Theme? file, string prefix)
        {
            if (file is null) return;
            foreach ((string key, string value) in file.Lines)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !key[prefix.Length..].Contains('.')
                    && Keys.Contains(key[prefix.Length..], StringComparer.OrdinalIgnoreCase) && IsColour(value))
                    colours[key[prefix.Length..]] = value;
        }
        Take(fallback, "");
        Take(modeFile, "");
        Take(theme, "");
        Take(theme, mode + ".");
        return colours;
    }

    /// <summary>The mode and theme to use: the player's pick unless the server enforces its own, then the server's
    /// default, then dark and the first theme in the list.</summary>
    public static (string Mode, string Theme) Pick(string? playerMode, string? playerTheme, ServerLook server, IEnumerable<string> themes)
    {
        List<string> have = themes.Select(t => t.ToLowerInvariant()).ToList();
        string? Valid(string? id, IEnumerable<string> list) =>
            id?.Trim().ToLowerInvariant() is { Length: > 0 } x && list.Contains(x) ? x : null;
        string? sMode = Valid(server.Mode, Modes);
        string? sTheme = Valid(server.Theme, have);
        string mode = (server.ModeEnforced ? sMode : null) ?? Valid(playerMode, Modes) ?? sMode ?? "dark";
        string theme = (server.ThemeEnforced ? sTheme : null) ?? Valid(playerTheme, have) ?? sTheme ?? have.FirstOrDefault() ?? "";
        return (mode, theme);
    }

    /// <summary>The server's say over the look ("theme" and "themeMode" in launcher-policy.json), kept in the player's
    /// settings so the next start already looks right before the launcher has talked to the server.</summary>
    public sealed record ServerLook(string Theme = "", bool ThemeEnforced = false, string Mode = "", bool ModeEnforced = false)
    {
        public static ServerLook From(LauncherPolicy policy) => new(
            policy.Setting("theme")?.Strings.FirstOrDefault() ?? "", policy.Setting("theme")?.Enforce == true,
            policy.Setting("themeMode")?.Strings.FirstOrDefault() ?? "", policy.Setting("themeMode")?.Enforce == true);
    }
}
