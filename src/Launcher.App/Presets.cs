using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// One preset for everything at the top of the Mods page: Classic (every Classic | HD choice on Classic, the original
/// character screens and music) or HD (every one on HD, every race HD). As soon as the player changes something on
/// their own it shows Custom.
/// </summary>
public partial class MainWindow
{
    /// <summary>The groups that have both a Classic and an HD choice (Character models, Character screens, Reforged packs).</summary>
    List<OptionGroup> PresetGroups() => [.. (_manifest?.OptionGroups ?? []).Where(g => !g.Enforce
        && g.Options.Contains("Classic", StringComparer.OrdinalIgnoreCase) && g.Options.Contains("HD", StringComparer.OrdinalIgnoreCase))];

    /// <summary>"Classic", "HD" or "Custom".</summary>
    string CurrentPreset()
    {
        if (_manifest is null) return "Custom";
        bool customised = _settings.ScenePicks.Count > 0 || _settings.MusicPicks.Count > 0;
        foreach (string preset in new[] { "Classic", "HD" })
        {
            bool all = PresetGroups().All(g => string.Equals(UpdatePlanner.ChosenOption(_manifest, _settings.Options, g.Name), preset,
                                                              StringComparison.OrdinalIgnoreCase));
            if (all && !customised && (preset == "Classic" || _settings.HdClassicParts.Count == 0)) return preset;
        }
        return "Custom";
    }

    Control PresetBar()
    {
        StackPanel bar = new() { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        bar.Children.Add(new TextBlock { Text = "PRESET", Classes = { "section" } });
        bar.Children.Add(new TextBlock
        {
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Classic: the original look and sound everywhere. HD: every newer model, scene and pack. Change anything on your own and it shows Custom.",
        });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        string current = CurrentPreset();
        foreach (string name in new[] { "Classic", "HD", "Custom" })
        {
            ToggleButton b = new() { Content = name, Classes = { "seg" }, IsChecked = name == current, IsHitTestVisible = name != "Custom" };
            if (name != "Custom") b.Click += async (_, _) => await UsePresetAsync(name);
            buttons.Children.Add(b);
        }
        bar.Children.Add(buttons);
        long download = PresetDownload("HD");
        if (current != "HD" && download > 0)
            bar.Children.Add(new TextBlock { Classes = { "dim" }, FontSize = 11, Text = $"HD downloads about {Words.Size(download)} more." });
        return bar;
    }

    /// <summary>Bytes the preset still has to download (files not on this PC and not kept aside).</summary>
    long PresetDownload(string preset)
    {
        if (_manifest is null || !GameFolder.LooksLikeClient(Folder)) return 0;
        long total = 0;
        foreach (OptionGroup g in PresetGroups())
            if (!OptionOnDisk(g, preset)) total += OptionFiles(g, preset).Sum(p => p.File.Size);
        return total;
    }

    async Task UsePresetAsync(string preset)
    {
        if (_manifest is null) return;
        _settings.ScenePicks.Clear();
        _scenePicksDraft.Clear();
        _settings.MusicPicks.Clear();
        _settings.HdClassicParts.Clear();
        List<OptionGroup> toSwap = [];
        int pending = 0;
        foreach (OptionGroup g in PresetGroups())
        {
            string value = g.Options.First(o => o.Equals(preset, StringComparison.OrdinalIgnoreCase));
            _settings.Options[g.Name] = value;
            if (!_manifest.Files.Any(f => f.Group == g.Name)) continue;   // a setting (Character screens): nothing to move
            if (OptionOnDisk(g, value)) toSwap.Add(g); else pending++;
        }
        SaveSettings();
        foreach (OptionGroup g in toSwap) await SwapOptionAsync(g, _settings.Options[g.Name]);
        ApplyCharacterScenes(Folder);
        await SyncHdModelsAsync(Folder);
        if (pending > 0) { _pendingFiles = Math.Max(_pendingFiles, 1); ShowPlayState(); }
        Status(pending > 0 ? $"{preset} preset picked. Press UPDATE to download the rest ({Words.Size(PresetDownload(preset))})."
                           : $"{preset} preset picked.");
        Dispatcher.UIThread.Post(() => { RefreshMods(); BuildScreens(); });
    }
}
