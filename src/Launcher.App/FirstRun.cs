using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Things shown once or on their own: error popups in the top right (Copy text, Close), "What's new" after the
/// launcher updates, the configuration preset picker on the first start, and Apply / Discard on Game settings.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------------------------------- error popups
    const int MaxToasts = 4;

    void ToastOnError(LogEntry entry)
    {
        if (entry.Level < LogLevel.Error) return;
        Dispatcher.UIThread.Post(() => Toast(entry.Message));
    }

    /// <summary>A popup in the top right that stays until closed.</summary>
    void Toast(string message)
    {
        // the same message twice in a row stays one popup
        if (ToastHost.Children.OfType<Border>().FirstOrDefault()?.Tag as string == message) return;
        TextBlock text = new()
        {
            Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, FontSize = 14,
            Foreground = (IBrush?)Application.Current!.FindResource("T.text"), VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock icon = new() { Text = "⚠", FontSize = 16, Foreground = new SolidColorBrush(Color.Parse("#E5484D")), Margin = new Thickness(0, 0, 10, 0) };
        Button copy = new() { Content = Translation.T("Copy text"), Classes = { "link" }, Margin = new Thickness(0, 0, 14, 0) };
        Button close = new() { Content = "✕ " + Translation.T("Close"), Classes = { "link" } };
        Border toast = new()
        {
            Tag = message, Background = (IBrush?)Application.Current!.FindResource("T.background"),
            BorderBrush = (IBrush?)Application.Current!.FindResource("T.border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 10), BoxShadow = BoxShadows.Parse("0 4 14 0 #80000000"),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, close } },
                },
            },
        };
        copy.Click += async (_, _) => { if (Clipboard is { } board) await board.SetTextAsync(message); };
        close.Click += (_, _) => ToastHost.Children.Remove(toast);
        ToastHost.Children.Insert(0, toast);
        while (ToastHost.Children.Count > MaxToasts) ToastHost.Children.RemoveAt(ToastHost.Children.Count - 1);
    }

    // ------------------------------------------------------------------------------------------- what's new
    bool _whatsNewDone;

    /// <summary>Once after the launcher updated: the changelog of this launcher version.</summary>
    void TryShowWhatsNew(string changelogJson)
    {
        if (_whatsNewDone || changelogJson.Length == 0) return;
        string version = LauncherInfo.Version;
        if (_settings.LastSeenLauncherVersion == version) { _whatsNewDone = true; return; }
        if (_settings.LastSeenLauncherVersion.Length == 0)
        {
            // a new install: nothing to tell yet
            _settings.LastSeenLauncherVersion = version;
            SaveSettings();
            _whatsNewDone = true;
            return;
        }
        IReadOnlyList<NewsItem> items;
        try { items = NewsItem.Parse(changelogJson); }
        catch (Exception) { return; }
        NewsItem? entry = items.FirstOrDefault(i => i.Title.Equals($"Launcher {version}", StringComparison.OrdinalIgnoreCase))
                          ?? items.FirstOrDefault(i => i.Title.StartsWith("Launcher ", StringComparison.OrdinalIgnoreCase));
        _whatsNewDone = true;
        if (entry is null) { MarkWhatsNewSeen(); return; }
        StartupNewsVersion.Text = entry.Title.Replace("Launcher ", "", StringComparison.OrdinalIgnoreCase);
        StartupNewsList.Children.Clear();
        foreach (string raw in entry.Text.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            bool sub = raw.StartsWith("  ");
            string line = raw.Trim().TrimStart('•', '-').Trim();
            StartupNewsList.Children.Add(ChangeLine(line, sub));
        }
        StartupNewsOverlay.IsVisible = true;
    }

    static readonly Dictionary<string, string> TagColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["New"] = "#8FD16A", ["Changed"] = "#F2C14E", ["Fixed"] = "#6EC1E4", ["Removed"] = "#E57373",
        ["Planned"] = "#B39DDB", ["Not live yet"] = "#E0894A",
    };

    static Control ChangeLine(string line, bool sub)
    {
        TextBlock text = new()
        {
            TextWrapping = TextWrapping.Wrap, FontSize = sub ? 13 : 15, Margin = new Thickness(sub ? 34 : 14, 1, 0, 1),
            Foreground = (IBrush?)Application.Current!.FindResource(sub ? "T.dim" : "T.text"),
        };
        InlineCollection inlines = [new Run(sub ? "– " : "• ")];
        if (line.StartsWith('[') && line.IndexOf(']') is var close and > 1 && TagColors.TryGetValue(line[1..close], out string? color))
        {
            inlines.Add(new Run(line[..(close + 1)]) { Foreground = new SolidColorBrush(Color.Parse(color)), FontWeight = FontWeight.SemiBold });
            line = line[(close + 1)..];
        }
        inlines.Add(new Run(line));
        text.Inlines = inlines;
        return text;
    }

    void MarkWhatsNewSeen()
    {
        _settings.LastSeenLauncherVersion = LauncherInfo.Version;
        SaveSettings();
    }

    void OnWhatsNewContinue(object? sender, RoutedEventArgs e)
    {
        StartupNewsOverlay.IsVisible = false;
        MarkWhatsNewSeen();
    }

    // ------------------------------------------------------------------------------------------- config presets
    sealed record ConfigPreset(string Name, string[] Mods, (string CVar, string Value)[] Settings);

    /// <summary>The first-start presets: Config.wtf values for 3.3.5, and the mods they turn on.</summary>
    static readonly Dictionary<string, ConfigPreset> ConfigPresets = new()
    {
        ["performance"] = new("Performance", ["DXVK"],
        [
            ("farclip", "477"), ("environmentDetail", "0.5"), ("groundEffectDensity", "16"), ("groundEffectDist", "40"),
            ("extShadowQuality", "0"), ("particleDensity", "0.3"), ("weatherDensity", "0"), ("spellEffectLevel", "0"),
            ("projectedTextures", "0"), ("ffxGlow", "0"), ("ffxDeath", "0"), ("ffxNetherWorld", "0"),
            ("SkyCloudLOD", "0"), ("SkySunGlare", "0"), ("detailDoodadAlpha", "0"), ("textureFilteringMode", "0"),
            ("gxMultisampleQuality", "0"), ("gxVSync", "1"), ("maxFPS", "144"), ("maxFPSBk", "30"),
        ]),
        ["fidelity"] = new("Fidelity", ["DXVK"],
        [
            ("farclip", "1277"), ("environmentDetail", "1.5"), ("groundEffectDensity", "256"), ("groundEffectDist", "140"),
            ("extShadowQuality", "4"), ("particleDensity", "1"), ("weatherDensity", "3"), ("spellEffectLevel", "2"),
            ("projectedTextures", "1"), ("ffxGlow", "1"), ("ffxDeath", "1"), ("ffxNetherWorld", "1"),
            ("SkyCloudLOD", "3"), ("SkySunGlare", "1"), ("detailDoodadAlpha", "100"), ("textureFilteringMode", "5"),
            ("gxVSync", "1"), ("maxFPS", "200"), ("maxFPSBk", "30"),
        ]),
    };

    /// <summary>On the first start with a game folder: Skip, Performance or Fidelity.</summary>
    void MaybeAskConfigPreset()
    {
        if (_settings.ConfigPresetAsked || !GameFolder.LooksLikeClient(Folder)) return;
        if (LoginOverlay.IsVisible || InstallOverlay.IsVisible || StartupNewsOverlay.IsVisible) return;
        ShowConfigPresets();
    }

    void ShowConfigPresets()
    {
        if (!GameFolder.LooksLikeClient(Folder)) { Status("Pick your WoW folder under Game files first."); return; }
        ConfigPresetOverlay.IsVisible = true;
    }

    void OnChangeConfigPreset(object? sender, RoutedEventArgs e) => ShowConfigPresets();

    void OnConfigPresetSkip(object? sender, RoutedEventArgs e)
    {
        _settings.ConfigPresetAsked = true;
        SaveSettings();
        ConfigPresetOverlay.IsVisible = false;
    }

    void OnConfigPresetPick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string key || !ConfigPresets.TryGetValue(key, out ConfigPreset? preset)) return;
        try
        {
            ConfigWtf config = ConfigWtf.Load(Folder);
            foreach ((string cvar, string value) in preset.Settings)
                if (TweakRule(cvar) is not { Enforce: true }) config.Set(cvar, value);   // the server's enforced values stay
            _policy.ApplyEnforcedTweaks(config);
            config.Save();
            bool modsChanged = false;
            foreach (string mod in preset.Mods)
                if (_manifest?.OptionGroups.Any(g => g.Name.Equals(mod, StringComparison.OrdinalIgnoreCase)) != false && !OptionOn(mod))
                {
                    _settings.Options[mod] = "On";
                    modsChanged = true;
                }
            _settings.ConfigPresetAsked = true;
            _settings.TweakPresetChosen = true;
            SaveSettings();
            LoadTweaks();
            RefreshMods();
            if (modsChanged) { _pendingFiles = Math.Max(_pendingFiles, 1); ShowPlayState(); }
            ConfigPresetOverlay.IsVisible = false;
            Log($"{preset.Name} preset written to WTF\\Config.wtf" + (modsChanged ? $"; turned on {string.Join(", ", preset.Mods)} (press Update)" : ""));
            Status(string.Format(Translation.T("{0} preset applied."), Translation.T(preset.Name)) + (modsChanged ? " " + Translation.T("Press UPDATE to get the mods it turned on.") : ""));
        }
        catch (Exception error)
        {
            Logger.Error("Applying the preset (is the game running?)", error);
        }
    }

    void OnConfigPresetInfo(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string key || !ConfigPresets.TryGetValue(key, out ConfigPreset? preset)) return;
        IncludedTitle.Text = Translation.T(preset.Name);
        IncludedMods.Text = string.Join(Environment.NewLine, preset.Mods);
        IncludedSettings.Text = string.Join(Environment.NewLine, preset.Settings.Select(s => $"SET {s.CVar} \"{s.Value}\""));
        IncludedOverlay.IsVisible = true;
    }

    void OnIncludedClose(object? sender, RoutedEventArgs e) => IncludedOverlay.IsVisible = false;

    // ------------------------------------------------------------------------------------------- apply / discard
    Dictionary<string, string> _savedBoxes = [];

    /// <summary>Apply and Discard show only while the boxes differ from what was read from the game.</summary>
    void UpdateTweaksDirty()
    {
        Dictionary<string, string> now = CurrentBoxes();
        bool dirty = now.Count != _savedBoxes.Count || now.Any(v => !_savedBoxes.TryGetValue(v.Key, out string? old) || old != v.Value);
        ApplyTweaksButton.IsVisible = dirty;
        DiscardTweaksButton.IsVisible = dirty;
    }

    void OnDiscardTweaks(object? sender, RoutedEventArgs e)
    {
        LoadTweaks();
        PresetChanges.IsVisible = false;
        Status("Changes discarded.");
    }
}
