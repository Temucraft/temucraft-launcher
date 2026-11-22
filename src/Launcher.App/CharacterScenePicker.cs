using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Character screens: each race shows its own scene (original with Character models Classic, newer with HD). The
/// player can click a scene, then the races that should use it. Saved with the tweaks into Interface\TemucraftScenes.lua.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, string> _scenePicksDraft = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Border> _sceneTiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WrapPanel> _sceneTileRaces = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ToggleButton> _raceButtons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Image> _sceneTilePictures = new(StringComparer.OrdinalIgnoreCase);
    TextBlock? _characterScenesNote;
    string _pickedScene = "";
    bool _raceRefresh;

    // faction colours (Alliance / Horde) and the death knight class colour; black text on them reads best
    static readonly Color AllianceColor = Color.Parse("#3FACF4"), HordeColor = Color.Parse("#FF653D"), DeathKnightColor = Color.Parse("#DD1B3C");
    static Color FactionColor(string faction) => faction == "Alliance" ? AllianceColor : faction == "Horde" ? HordeColor : DeathKnightColor;

    bool HdModels => _manifest is not null && UpdatePlanner.ChosenOption(_manifest, _settings.Options, "Character models") == "HD";

    /// <summary>The section under Login screen.</summary>
    void BuildCharacterScenes()
    {
        TweaksList.Children.Add(new TextBlock { Text = "CHARACTER SCREENS", Classes = { "section" } });
        _characterScenesNote = new TextBlock { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        TweaksList.Children.Add(_characterScenesNote);

        // the scenes: Alliance | Horde side by side, each race on a row with its Original and HD scene; then the rest
        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < 2; i++)
        {
            StackPanel side = SceneColumn(SceneRows[i].Title, SceneRows[i].Rows);
            Grid.SetColumn(side, i);
            sides.Children.Add(side);
        }
        TweaksList.Children.Add(sides);
        TweaksList.Children.Add(SceneColumn(SceneRows[2].Title, SceneRows[2].Rows, wrap: true));

        // the races, Alliance | Horde, then classes
        TweaksList.Children.Add(new TextBlock { Text = "RACES", Classes = { "section" } });
        Grid palette = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        int column = 0;
        foreach (string faction in new[] { "Alliance", "Horde", "Class" })
        {
            StackPanel list = new() { Spacing = 3, Margin = new Thickness(0, 0, 24, 0) };
            list.Children.Add(new TextBlock { Text = faction == "Class" ? "CLASSES" : faction.ToUpperInvariant(), Classes = { "section" } });
            foreach (CharacterRace race in CharacterScenes.Races.Where(r => r.Faction == faction))
            {
                ToggleButton button = new() { FontSize = 12, Padding = new Thickness(8, 2), MinWidth = 260,
                                              HorizontalContentAlignment = HorizontalAlignment.Left,
                                              BorderBrush = new SolidColorBrush(FactionColor(faction)),
                                              BorderThickness = new Thickness(4, 0, 0, 0) };
                string key = race.Key;
                button.IsCheckedChanged += (_, _) => { if (!_raceRefresh) ToggleRace(key); };
                _raceButtons[key] = button;
                list.Children.Add(button);
            }
            Grid.SetColumn(list, column++);
            palette.Children.Add(list);
        }
        TweaksList.Children.Add(palette);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        Button reset = new() { Content = "Each race its own scene again", FontSize = 12, Padding = new Thickness(8, 2) };
        reset.Click += (_, _) => { _scenePicksDraft.Clear(); RefreshCharacterScenes(); };
        buttons.Children.Add(reset);
        TweaksList.Children.Add(buttons);

        foreach ((string race, string scene) in _settings.ScenePicks) _scenePicksDraft[race] = scene;
        LoadCharacterScenePictures();
    }

    /// <summary>The scene pictures (once the manifest is in) and the Classic / HD default.</summary>
    void LoadCharacterScenePictures()
    {
        foreach (CharacterScene scene in CharacterScenes.Scenes)
            if (_sceneTilePictures.TryGetValue(scene.Folder, out Image? picture) && picture.Source is null
                && _manifest?.LoginScreenPreviews.TryGetValue(scene.Preview, out string? url) == true)
                _ = SetSceneImageAsync(picture, url);
        RefreshCharacterScenes();
    }

    /// <summary>Scene rows: (race, original scene folder, HD scene folder); "" when there is only one.</summary>
    static readonly (string Title, (string Race, string Original, string Hd)[] Rows)[] SceneRows =
    [
        ("Alliance", [("Alliance", "", "Alliance"), ("Human", "Huma2", "Human"), ("Dwarf and Gnome", "Dwar2", "Dwarf"),
                      ("Night Elf", "NightEl2", "NightElf"), ("Gnome", "", "Gnome"), ("Draenei", "", "Draenei"),
                      ("Worgen", "", "Worgen"), ("Pandaren", "", "Pandaren"), ("Void Elf", "", "VoidElf"),
                      ("Lightforged Draenei", "", "LightforgedDraenei"), ("Kul Tiran", "", "KulTiran"),
                      ("Mechagnome", "", "Mechagnome"), ("Earthen", "", "Earthen")]),
        ("Horde", [("Horde", "", "Horde"), ("Orc and Troll", "Or2", "Orc"), ("Undead", "Scourg2", "Scourge"),
                   ("Tauren", "Taure2", "Tauren"), ("Troll", "", "Troll"), ("Blood Elf", "", "BloodElf"),
                   ("Goblin", "", "Goblin"), ("Pandaren (temple)", "", "PandarenCharacterSelect"),
                   ("Nightborne", "", "NightborneElf"), ("Highmountain Tauren", "", "HighmountainTauren"),
                   ("Mag'har Orc", "", "OrcClan"), ("Vulpera", "", "Vulpera")]),
        ("Classes and others", [("Death Knight", "DeathKnight", "DeathKnigh2"), ("Demon Hunter", "", "DemonHunter"),
                                ("Dracthyr", "", "Dracthyr"), ("Pirate", "", "Pirate"), ("Character select", "", "CharacterSelect")]),
    ];

    StackPanel SceneColumn(string title, (string Race, string Original, string Hd)[] rows, bool wrap = false)
    {
        StackPanel column = new() { Spacing = 4, Margin = new Thickness(0, 0, 28, 8) };
        column.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } });
        Panel list = wrap ? new WrapPanel() : new StackPanel { Spacing = 4 };
        foreach ((string race, string original, string hd) in rows)
        {
            StackPanel row = new() { Spacing = 1, Margin = wrap ? new Thickness(0, 0, 16, 4) : new Thickness(0) };
            row.Children.Add(new TextBlock { Text = race, FontWeight = FontWeight.Bold, FontSize = 13 });
            StackPanel pair = new() { Orientation = Orientation.Horizontal };
            if (original.Length > 0) pair.Children.Add(SceneTile(original, "Original"));
            pair.Children.Add(SceneTile(hd, original.Length > 0 ? "HD" : ""));
            row.Children.Add(pair);
            list.Children.Add(row);
        }
        column.Children.Add(list);
        return column;
    }

    /// <summary>A scene's picture, its label and the races using it; click to pick it.</summary>
    Control SceneTile(string folder, string label)
    {
        StackPanel inner = new() { Spacing = 2, Width = 168 };
        Image picture = new() { Width = 168, Height = 94, Stretch = Stretch.UniformToFill };
        _sceneTilePictures[folder] = picture;
        inner.Children.Add(new Border { Child = picture, Background = Brushes.Black, ClipToBounds = true });
        if (label.Length > 0) inner.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.SemiBold });
        WrapPanel races = new();
        inner.Children.Add(races);
        Border tile = new() { Child = inner, Classes = { "tile" }, Margin = new Thickness(0, 0, 6, 4),
                              Cursor = new Cursor(StandardCursorType.Hand) };
        tile.PointerPressed += (_, _) => PickScene(folder);
        ToolTip.SetTip(tile, $"{CharacterScenes.Label(folder)}: click, then click the races below that should use it");
        _sceneTiles[folder] = tile;
        _sceneTileRaces[folder] = races;
        return tile;
    }

    void PickScene(string folder)
    {
        _pickedScene = _pickedScene.Equals(folder, StringComparison.OrdinalIgnoreCase) ? "" : folder;
        RefreshCharacterScenes();
    }

    /// <summary>A race clicked: it uses the picked scene, or goes back to its own when it already did.</summary>
    void ToggleRace(string race)
    {
        if (_pickedScene.Length == 0) { RefreshCharacterScenes(); return; }
        bool hd = HdModels;
        if (_scenePicksDraft.TryGetValue(race, out string? now) && now.Equals(_pickedScene, StringComparison.OrdinalIgnoreCase))
            _scenePicksDraft.Remove(race);
        else if (CharacterScenes.DefaultScene(race, hd).Equals(_pickedScene, StringComparison.OrdinalIgnoreCase))
            _scenePicksDraft.Remove(race);   // its own scene: no pick needed
        else
            _scenePicksDraft[race] = _pickedScene;
        RefreshCharacterScenes();
    }

    string SceneOf(string race, bool hd) =>
        _scenePicksDraft.TryGetValue(race, out string? pick) ? pick : CharacterScenes.DefaultScene(race, hd);

    void RefreshCharacterScenes()
    {
        if (_characterScenesNote is null) return;
        bool hd = HdModels;
        _characterScenesNote.Text = $"Each race shows its own scene on character select and create: the {(hd ? "newer (HD)" : "original")} ones, because Character models is {(hd ? "HD" : "Classic")}. "
            + "To change it, click a scene, then click the races that should use it (click a race again to give it back its own). Press Save to keep your choice."
            + (_pickedScene.Length > 0 ? $"   Picked: {CharacterScenes.Label(_pickedScene)}" : "");
        foreach ((string folder, Border tile) in _sceneTiles)
        {
            tile.Classes.Set("picked", folder.Equals(_pickedScene, StringComparison.OrdinalIgnoreCase));
            WrapPanel races = _sceneTileRaces[folder];
            races.Children.Clear();
            foreach (CharacterRace race in CharacterScenes.Races.Where(r => SceneOf(r.Key, hd).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                bool own = !_scenePicksDraft.ContainsKey(race.Key);
                // a pick of the player's: filled with the colour; the race's own scene: outlined
                Color color = FactionColor(race.Faction);
                races.Children.Add(new Border
                {
                    Background = own ? Brushes.Transparent : new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0), Margin = new Thickness(0, 2, 3, 0),
                    Child = new TextBlock { Text = race.Label, FontSize = 10, FontWeight = FontWeight.SemiBold,
                                            Foreground = own ? new SolidColorBrush(color) : race.Faction == "Class" ? Brushes.White : Brushes.Black },
                });
            }
        }
        _raceRefresh = true;
        foreach ((string race, ToggleButton button) in _raceButtons)
        {
            string scene = SceneOf(race, hd);
            string label = CharacterScenes.Races.First(r => r.Key == race).Label;
            button.Content = $"{label}:  {CharacterScenes.Label(scene)}{(_scenePicksDraft.ContainsKey(race) ? "  (your pick)" : "")}";
            button.IsChecked = _pickedScene.Length > 0 && scene.Equals(_pickedScene, StringComparison.OrdinalIgnoreCase);
            button.IsEnabled = _pickedScene.Length > 0;
        }
        _raceRefresh = false;
    }

    /// <summary>Saves the picks when they changed (from the tweaks' Save button).</summary>
    void SaveCharacterScenes()
    {
        bool same = _scenePicksDraft.Count == _settings.ScenePicks.Count
                    && _scenePicksDraft.All(p => _settings.ScenePicks.TryGetValue(p.Key, out string? v) && v == p.Value);
        if (!same)
        {
            _settings.ScenePicks = new Dictionary<string, string>(_scenePicksDraft, StringComparer.OrdinalIgnoreCase);
            SaveSettings();
        }
        ApplyCharacterScenes(Folder);
    }

    /// <summary>Interface\TemucraftScenes.lua: Classic or HD scenes, plus the player's picks.</summary>
    void ApplyCharacterScenes(string folder)
    {
        if (_manifest is null || !GameFolder.LooksLikeClient(folder)) return;
        try
        {
            if (CharacterScenes.Write(folder, HdModels, _settings.ScenePicks))
                Log($"Character screens: {(HdModels ? "HD" : "original")} scenes" + (_settings.ScenePicks.Count > 0
                    ? ", " + string.Join(", ", _settings.ScenePicks.Select(p => $"{p.Key} = {p.Value}")) : ""));
        }
        catch (Exception error) { Logger.Warning("Couldn't write the character screen scenes", error); }
    }
}
