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
/// A colour may also be a top to bottom gradient: <c>"#3A5878 &gt; #1B2C44"</c> (two or more colours). Besides colours a
/// theme may set a few style lines (see <see cref="StyleKeys"/>): a drawn texture, a font for titles, the corner radius
/// and the border width. They follow the same "dark." / "light." rule.
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
        "sectionBar",   // a bar behind section headings (see-through: no bar)
        "frame",        // a second, outer frame around panels (see-through: none)
        "playBorder",   // the line around the PLAY and UPDATE buttons (see-through: none)
    ];

    /// <summary>Style lines a theme may set, with what they mean when no file sets them.</summary>
    public static readonly Dictionary<string, string> StyleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["texture"] = "none",           // drawn over the window background: none, stone, parchment, mist, ember, wood
        ["textureStrength"] = "0.5",    // 0 (none) to 1 (strong)
        ["panelTexture"] = "none",      // drawn inside the panels, in the panel colour: same names
        ["panelTextureStrength"] = "0.3",
        ["titleFont"] = "",             // fonts for titles, headings and tabs, first one found wins ("Cinzel, Georgia, serif")
        ["radius"] = "0",               // corner radius of panels and big buttons, 0 to 16
        ["borderWidth"] = "1",          // line width around panels, 0 to 4
    };

    /// <summary>The drawn textures the launcher knows.</summary>
    public static readonly string[] Textures = ["none", "stone", "parchment", "mist", "ember", "wood"];

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

    /// <summary>The colours of a value: one for a plain colour, two or more for a gradient ("#111 &gt; #222"); empty
    /// when it is neither.</summary>
    public static string[] Stops(string text)
    {
        string[] parts = text.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 1 and <= 8 && parts.All(IsColour) && text.Count(c => c == '>') == parts.Length - 1 ? parts : [];
    }

    /// <summary>A plain colour or a gradient.</summary>
    public static bool IsBrush(string text) => Stops(text).Length > 0;

    /// <summary>The first colour of a plain colour or gradient (for the places that need one colour).</summary>
    public static string FirstColour(string text) => Stops(text) is { Length: > 0 } s ? s[0] : text;

    /// <summary>
    /// The style lines for a mode and a theme, every <see cref="StyleKeys"/> key set: the built-in value, then the
    /// mode file's, the theme's own and the theme's "&lt;mode&gt;." lines. Values that don't fit are skipped.
    /// </summary>
    public static Dictionary<string, string> ResolveStyle(Theme? modeFile, Theme? theme, string mode)
    {
        Dictionary<string, string> style = new(StyleKeys, StringComparer.OrdinalIgnoreCase);
        void Take(Theme? file, string prefix)
        {
            if (file is null) return;
            foreach ((string key, string value) in file.Lines)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = key[prefix.Length..];
                if (name.Contains('.') || !StyleKeys.ContainsKey(name) || !StyleFits(name, value.Trim())) continue;
                style[name] = value.Trim();
            }
        }
        Take(modeFile, "");
        Take(theme, "");
        Take(theme, mode + ".");
        return style;
    }

    static bool StyleFits(string key, string value) => key.ToLowerInvariant() switch
    {
        "texture" or "paneltexture" => Textures.Contains(value.ToLowerInvariant()) || IsImageFile(value),
        "texturestrength" or "paneltexturestrength" => Number(value) is >= 0 and <= 1,
        "radius" => Number(value) is >= 0 and <= 16,
        "borderwidth" => Number(value) is >= 0 and <= 4,
        "titlefont" => value.Length <= 200 && !value.Contains('/') && !value.Contains('\\'),
        _ => false,
    };

    /// <summary>A picture next to the theme files ("themes/marble.png"): a plain file name, png or jpg.</summary>
    public static bool IsImageFile(string value) =>
        value.Length is > 4 and <= 80 && value.IndexOfAny(['/', '\\', ':']) < 0 && !value.StartsWith('.')
        && (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));

    public static double? Number(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;

    /// <summary>
    /// The colours for a mode and a theme: the mode file's, then the theme's own, then the theme's "&lt;mode&gt;."
    /// lines. Anything that isn't a colour or gradient is skipped; a colour no file sets falls back to the built-in dark one.
    /// </summary>
    public static Dictionary<string, string> Resolve(Theme? modeFile, Theme? theme, string mode, Theme? fallback = null)
    {
        Dictionary<string, string> colours = new(StringComparer.OrdinalIgnoreCase);
        void Take(Theme? file, string prefix)
        {
            if (file is null) return;
            foreach ((string key, string value) in file.Lines)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !key[prefix.Length..].Contains('.')
                    && Keys.Contains(key[prefix.Length..], StringComparer.OrdinalIgnoreCase) && IsBrush(value))
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
