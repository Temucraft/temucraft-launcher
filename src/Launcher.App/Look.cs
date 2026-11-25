using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The launcher's language and theme. Both come from small YAML files built into the launcher ("lang/pt.yml",
/// "themes/wotlk.yml"); a "lang" or "themes" folder next to the launcher adds files or replaces built-in ones.
/// </summary>
public static class Look
{
    // ------------------------------------------------------------------------------------------ files
    static string Folder(string name) => Path.Combine(AppContext.BaseDirectory, name);

    /// <summary>The built-in files of a folder ("lang", "themes"), then the ones next to the launcher (which win).</summary>
    static Dictionary<string, string> Files(string folder)
    {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        Assembly assembly = typeof(Look).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            files[Path.GetFileNameWithoutExtension(name)] = reader.ReadToEnd();
        }
        try
        {
            if (Directory.Exists(Folder(folder)))
                foreach (string file in Directory.GetFiles(Folder(folder), "*.yml"))
                    files[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
        }
        catch (Exception e) { Logger.Warning($"Couldn't read the {folder} folder", e); }
        return files;
    }

    // ------------------------------------------------------------------------------------------ language
    static List<Translation.Language>? _languages;

    /// <summary>Every language the launcher has, English first.</summary>
    public static IReadOnlyList<Translation.Language> Languages => _languages ??=
        [new Translation.Language(Translation.English, "English", []),
         .. Files("lang").Where(f => !f.Key.Equals(Translation.English, StringComparison.OrdinalIgnoreCase))
                         .Select(f => Translation.FromYaml(f.Key, f.Value)).OrderBy(l => l.Code, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Picks and loads the language (the player's, else launcher.env's LANGUAGE, else the computer's).</summary>
    public static void UseLanguage(string? player, string? server)
    {
        string code = Translation.Pick(player, server, CultureInfo.CurrentUICulture, Languages.Select(l => l.Code));
        Translation.Use(Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));
        Logger.Info($"Language: {Translation.Code}");
    }

    static bool _hooked;

    /// <summary>
    /// Translates the launcher's own text wherever it is set: in the window's layout or later from code. Text the
    /// server wrote sits in controls with the "raw" class (and their children) and is left alone.
    /// </summary>
    public static void HookTranslation()
    {
        if (_hooked) return;
        _hooked = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Swap(block, TextBlock.TextProperty, e.NewValue));
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control, e) => Swap(control, ContentControl.ContentProperty, e.NewValue));
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((box, e) => Swap(box, TextBox.WatermarkProperty, e.NewValue));
        ToggleSwitch.OnContentProperty.Changed.AddClassHandler<ToggleSwitch>((t, e) => Swap(t, ToggleSwitch.OnContentProperty, e.NewValue));
        ToggleSwitch.OffContentProperty.Changed.AddClassHandler<ToggleSwitch>((t, e) => Swap(t, ToggleSwitch.OffContentProperty, e.NewValue));
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) => Swap(control, ToolTip.TipProperty, e.NewValue));
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) => Swap(window, Window.TitleProperty, e.NewValue));
        Run.TextProperty.Changed.AddClassHandler<Run>((run, e) =>
        {
            if (_untranslated == 0 && e.NewValue is string text && Translation.Find(text) is { } translated && translated != text) run.Text = translated;
        });
    }

    static int _untranslated;

    /// <summary>While this is open (using), nothing is translated: for building the server's own text (news,
    /// changelog, option and addon names), which stays as the server wrote it.</summary>
    public static IDisposable Untranslated()
    {
        _untranslated++;
        return new Scope();
    }

    sealed class Scope : IDisposable
    {
        bool _done;
        public void Dispose() { if (!_done) { _done = true; _untranslated--; } }
    }

    static void Swap(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (_untranslated > 0 || value is not string text || text.Length == 0 || IsRaw(target)) return;
        if (Translation.Find(text) is { } translated && translated != text) target.SetValue(property, translated);
    }

    /// <summary>Server text: a control with the "raw" class, or inside one (set before the text, or the text is
    /// translated first).</summary>
    static bool IsRaw(AvaloniaObject target)
    {
        for (StyledElement? e = target as StyledElement; e is not null; e = e.Parent)
            if (e.Classes.Contains("raw")) return true;
        return false;
    }

    // ------------------------------------------------------------------------------------------ theme
    static List<Themes.Theme>? _themes;
    static Dictionary<string, Themes.Theme>? _modes;

    static void LoadThemes()
    {
        if (_themes is not null) return;
        Dictionary<string, string> files = Files("themes");
        _modes = Themes.Modes.Where(files.ContainsKey).ToDictionary(m => m, m => Themes.FromYaml(m, files[m]));
        _themes = files.Where(f => !Themes.Modes.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
                       .Select(f => Themes.FromYaml(f.Key, f.Value)).OrderBy(t => t.Order).ThenBy(t => t.Name).ToList();
    }

    /// <summary>Every theme (Temucraft, then the expansions, then any added ones).</summary>
    public static IReadOnlyList<Themes.Theme> ThemeList { get { LoadThemes(); return _themes!; } }

    public static string Mode { get; private set; } = "dark";
    public static string Theme { get; private set; } = "";

    /// <summary>Picks the mode and theme (the server's enforced ones, the player's, the server's default) and applies them.</summary>
    public static void UseTheme(UserSettings settings)
    {
        LoadThemes();
        (string mode, string theme) = Themes.Pick(settings.ThemeMode, settings.Theme, settings.ServerLook ?? new(), _themes!.Select(t => t.Id));
        Apply(mode, theme);
    }

    /// <summary>Puts the colours in the app's resources ("T.accent"...): the whole window follows straight away.</summary>
    public static void Apply(string mode, string theme)
    {
        LoadThemes();
        if (Application.Current is not { } app) return;
        Mode = mode;
        Theme = theme;
        Themes.Theme? themeFile = _themes!.FirstOrDefault(t => t.Id == theme);
        Dictionary<string, string> colours = Themes.Resolve(_modes!.GetValueOrDefault(mode), themeFile, mode, _modes!.GetValueOrDefault("dark"));
        foreach ((string key, string value) in colours)
            app.Resources["T." + key] = MakeBrush(value);
        ApplyStyle(app, Themes.ResolveStyle(_modes!.GetValueOrDefault(mode), themeFile, mode), colours);
        app.RequestedThemeVariant = mode == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
        // the switches, check boxes and sliders in the theme's colour
        if (colours.TryGetValue("accent", out string? accent))
        {
            Color c = Color.Parse(Themes.FirstColour(accent));
            app.Resources["SystemAccentColor"] = c;
            for (int i = 1; i <= 3; i++)
            {
                app.Resources[$"SystemAccentColorDark{i}"] = Shade(c, 1 - 0.15 * i);
                app.Resources[$"SystemAccentColorLight{i}"] = Shade(c, 1 + 0.15 * i);
            }
            // text on accent-coloured buttons (picked presets, race chips, checked toggles): dark on a bright
            // accent like the gold, white on a dark one, so it stays readable
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            IBrush onAccent = new SolidColorBrush(lum > 0.55 ? Color.Parse("#1E1208") : Colors.White);
            foreach (string key in new[] { "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver",
                                           "ToggleButtonForegroundCheckedPressed", "AccentButtonForeground",
                                           "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" })
                app.Resources[key] = onAccent;
        }
    }

    /// <summary>A plain colour, or a top to bottom gradient for "#111 > #222".</summary>
    static IBrush MakeBrush(string value)
    {
        string[] stops = Themes.Stops(value);
        if (stops.Length <= 1) return new SolidColorBrush(Color.Parse(stops.Length == 1 ? stops[0] : value));
        LinearGradientBrush brush = new() { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        for (int i = 0; i < stops.Length; i++)
            brush.GradientStops.Add(new GradientStop(Color.Parse(stops[i]), i / (double)(stops.Length - 1)));
        return brush;
    }

    /// <summary>The fonts built into the launcher (Assets/Fonts, open font licence), by family name.</summary>
    static readonly string[] BundledFonts = ["Cinzel", "Marcellus"];

    /// <summary>
    /// The theme's style lines as resources: "T.texture" (over the window), the panel texture (replaces "T.panel"),
    /// "T.titleFont", "T.radius", "T.borderWidth", "T.frameShadow" (the outer frame) and "T.sectionPadding".
    /// </summary>
    static void ApplyStyle(Application app, Dictionary<string, string> style, Dictionary<string, string> colours)
    {
        double strength = Themes.Number(style["textureStrength"]) ?? 0.5;
        app.Resources["T.texture"] = Texture(style["texture"], strength, null) ?? Brushes.Transparent;
        if (colours.TryGetValue("panel", out string? panel)
            && Texture(style["panelTexture"], Themes.Number(style["panelTextureStrength"]) ?? 0.3,
                       Color.Parse(Themes.FirstColour(panel))) is { } panelTexture)
            app.Resources["T.panel"] = panelTexture;

        List<string> fonts = [];
        foreach (string name in style["titleFont"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            fonts.Add(BundledFonts.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } bundled
                ? $"avares://TemucraftLauncher/Assets/Fonts#{bundled}" : name);
        app.Resources["T.titleFont"] = fonts.Count > 0 ? new FontFamily(string.Join(", ", fonts)) : FontFamily.Default;

        double radius = Themes.Number(style["radius"]) ?? 0, width = Themes.Number(style["borderWidth"]) ?? 1;
        app.Resources["T.radius"] = new CornerRadius(radius);
        app.Resources["T.borderWidth"] = new Thickness(width);
        // the outer frame: a solid ring drawn as a shadow just outside the border, then a soft drop shadow
        string frame = colours.TryGetValue("frame", out string? f) ? Themes.FirstColour(f) : "#00000000";
        app.Resources["T.frameShadow"] = Color.Parse(frame).A == 0
            ? new BoxShadows()
            : BoxShadows.Parse($"0 0 0 3 {frame}, 0 6 16 0 #80000000");
        string bar = colours.TryGetValue("sectionBar", out string? b) ? Themes.FirstColour(b) : "#00000000";
        app.Resources["T.sectionPadding"] = Color.Parse(bar).A == 0 ? new Thickness(0) : new Thickness(8, 3, 8, 3);
    }

    /// <summary>A drawn texture by name, or a picture from the "themes" folder next to the launcher.</summary>
    static IBrush? Texture(string name, double strength, Color? baseColour)
    {
        if (!Themes.IsImageFile(name)) return DrawnTextures.Brush(name, strength, baseColour);
        try
        {
            string file = Path.Combine(Folder("themes"), name);
            return File.Exists(file) ? DrawnTextures.Tiled(new Avalonia.Media.Imaging.Bitmap(file)) : null;
        }
        catch (Exception e) { Logger.Warning($"Couldn't read the theme picture {name}", e); return null; }
    }

    /// <summary>A fixed colour (news tags, {+better} values) made darker in light mode so it reads on white.</summary>
    public static Color Readable(string colour)
    {
        Color c = Color.Parse(colour);
        return Mode == "light" ? Shade(c, 0.6) : c;
    }

    static Color Shade(Color c, double factor)
    {
        byte Part(byte v) => (byte)Math.Clamp(factor <= 1 ? v * factor : v + (255 - v) * (factor - 1), 0, 255);
        return Color.FromRgb(Part(c.R), Part(c.G), Part(c.B));
    }
}
