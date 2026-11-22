using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    TextBlock? _pickedNote;
    string _pickedScene = "";
    bool _raceRefresh;

    // faction colours (Alliance / Horde) and the death knight class colour; black text on them reads best
    static readonly Color AllianceColor = Color.Parse("#3FACF4"), HordeColor = Color.Parse("#FF653D"), DeathKnightColor = Color.Parse("#DD1B3C");
    static Color FactionColor(string faction) => faction == "Alliance" ? AllianceColor : faction == "Horde" ? HordeColor : DeathKnightColor;

    /// <summary>"old" / "new" / "faction" from the "Character screens" option (servers without it: Character models HD
    /// gives "new").</summary>
    string SceneMode => _manifest is null ? "old"
        : _manifest.OptionGroups.Any(g => g.Name == "Character screens")
            ? CharacterScenes.ModeOf(UpdatePlanner.ChosenOption(_manifest, _settings.Options, "Character screens"))
            : UpdatePlanner.ChosenOption(_manifest, _settings.Options, "Character models") == "HD" ? "new" : "old";

    /// <summary>The big preview: each race's scene, Classic or HD, with the player's picks.</summary>
    Task OpenCharacterScenePreviewAsync(string mode) => new CharacterScenePreviewWindow(mode,
        (race, mode) => _scenePicksDraft.TryGetValue(race, out string? pick) ? pick : CharacterScenes.DefaultScene(race, mode),
        async folder =>
        {
            string? code = CharacterScenes.Scenes.FirstOrDefault(x => x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Preview;
            return code is not null && _manifest?.LoginScreenPreviews.TryGetValue(code, out string? url) == true ? await PictureAsync(url) : null;
        }).ShowDialog(this);

    /// <summary>The section under Login screen.</summary>
    void BuildCharacterScenes(Panel target)
    {
        _characterScenesNote = new TextBlock { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        target.Children.Add(_characterScenesNote);

        // the scenes: Alliance | Horde side by side, each race on a row with its Classic and HD scene; on the right the
        // race buttons; under them the classes and other scenes
        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < 2; i++)
        {
            StackPanel side = SceneColumn(SceneRows[i].Title, SceneRows[i].Rows);
            Grid.SetColumn(side, i);
            sides.Children.Add(side);
        }
        StackPanel palette = new() { Spacing = 3, Margin = new Thickness(4, 0, 0, 0), Width = 236 };
        _pickedNote = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        palette.Children.Add(_pickedNote);
        foreach (string faction in new[] { "Alliance", "Horde", "Class" })
        {
            palette.Children.Add(new TextBlock { Text = faction == "Class" ? "Classes" : faction, FontSize = 12, FontWeight = FontWeight.SemiBold,
                                                 Foreground = new SolidColorBrush(FactionColor(faction)), Margin = new Thickness(0, 6, 0, 0) });
            WrapPanel chips = new();
            foreach (CharacterRace race in CharacterScenes.Races.Where(r => r.Faction == faction))
            {
                ToggleButton button = new() { FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4),
                                              Content = race.Label, CornerRadius = new CornerRadius(12),
                                              BorderBrush = new SolidColorBrush(FactionColor(faction)), BorderThickness = new Thickness(1.5) };
                string key = race.Key;
                button.IsCheckedChanged += (_, _) => { if (!_raceRefresh) ToggleRace(key); };
                _raceButtons[key] = button;
                chips.Children.Add(button);
            }
            palette.Children.Add(chips);
        }
        Grid.SetColumn(palette, 2);
        sides.Children.Add(palette);
        target.Children.Add(sides);
        target.Children.Add(SceneColumn(SceneRows[2].Title, SceneRows[2].Rows, wrap: true));

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        Button preview = new() { Content = "Preview", FontSize = 12, Padding = new Thickness(8, 2) };
        ToolTip.SetTip(preview, "See each race's character screen big, with Classic or HD and your picks");
        preview.Click += async (_, _) => await OpenCharacterScenePreviewAsync(SceneMode);
        buttons.Children.Add(preview);
        Button reset = new() { Content = "Each race its own scene again", FontSize = 12, Padding = new Thickness(8, 2) };
        reset.Click += (_, _) => { _scenePicksDraft.Clear(); RefreshCharacterScenes(); };
        buttons.Children.Add(reset);
        target.Children.Insert(target.Children.IndexOf(_characterScenesNote!) + 1, buttons);   // Preview and Reset at the top

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
        // only playable races; each row named for what its file really shows in this client (UI_Human is Boralus,
        // UI_Tauren Highmountain, UI_Troll Zandalar, UI_Dwarf and UI_NightElf the Dark Iron and Nightborne scenes)
        ("Alliance", [("Alliance", "", "Alliance"), ("Human", "", "Huma2"), ("Dwarf and Gnome", "", "Dwar2"),
                      ("Night Elf", "", "NightEl2"), ("Gnome", "", "Gnome"), ("Draenei and Lightforged", "", "Draenei"),
                      ("Worgen", "", "Worgen"), ("Pandaren", "", "Pandaren"), ("Void Elf", "", "VoidElf"),
                      ("Kul Tiran (Boralus)", "", "Human")]),
        ("Horde", [("Horde", "", "Horde"), ("Orc and Troll", "Or2", "Orc"), ("Undead", "Scourg2", "Scourge"),
                   ("Tauren (Mulgore)", "", "Taure2"), ("Blood Elf", "", "BloodElf"), ("Goblin", "", "Goblin"),
                   ("Pandaren (temple)", "", "PandarenCharacterSelect"), ("Nightborne (Suramar)", "", "NightborneElf"),
                   ("Highmountain Tauren", "", "Tauren"), ("Zandalari Troll (Zandalar)", "", "Troll")]),
        ("Classes and others", [("Death Knight", "DeathKnight", "DeathKnigh2"), ("Character select", "", "CharacterSelect")]),
    ];

    StackPanel SceneColumn(string title, (string Race, string Original, string Hd)[] rows, bool wrap = false)
    {
        StackPanel column = new() { Spacing = 4, Margin = new Thickness(0, 0, 18, 8) };
        column.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } });
        Panel list = wrap ? new WrapPanel() : new StackPanel { Spacing = 4 };
        foreach ((string race, string original, string hd) in rows)
        {
            StackPanel row = new() { Spacing = 1, Margin = wrap ? new Thickness(0, 0, 16, 4) : new Thickness(0) };
            row.Children.Add(new TextBlock { Text = race, FontWeight = FontWeight.Bold, FontSize = 13 });
            StackPanel pair = new() { Orientation = Orientation.Horizontal };
            if (original.Length > 0) pair.Children.Add(SceneTile(original, "Classic"));
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
        StackPanel inner = new() { Spacing = 2, Width = 150 };
        Image picture = new() { Width = 150, Height = 84, Stretch = Stretch.UniformToFill };
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
        string mode = SceneMode;
        if (_scenePicksDraft.TryGetValue(race, out string? now) && now.Equals(_pickedScene, StringComparison.OrdinalIgnoreCase))
            _scenePicksDraft.Remove(race);
        else if (CharacterScenes.DefaultScene(race, mode).Equals(_pickedScene, StringComparison.OrdinalIgnoreCase))
            _scenePicksDraft.Remove(race);   // its own scene: no pick needed
        else
            _scenePicksDraft[race] = _pickedScene;
        RefreshCharacterScenes();
    }

    string SceneOf(string race, string mode) =>
        _scenePicksDraft.TryGetValue(race, out string? pick) ? pick : CharacterScenes.DefaultScene(race, mode);

    void RefreshCharacterScenes()
    {
        if (_characterScenesNote is null) return;
        string hd = SceneMode;
        string name = CharacterScenes.ModeName(hd);
        _characterScenesNote.Text = (hd == "faction"
                ? "Every race shows the Alliance or Horde scene, because the Character screens option is Faction. "
                : $"Each race shows its own scene on character select and create: {name} scenes, because the Character screens option is {name}. ")
            + "To change it, click a scene, then click the races that should use it (click a race again to give it back its own). Press Save at the bottom to keep your choice."
            ;
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
            ToolTip.SetTip(button, $"{label} uses {(scene.Length == 0 ? "Alliance or Horde, by the race" : CharacterScenes.Label(scene))}{(_scenePicksDraft.ContainsKey(race) ? " (your pick)" : " (its own)")}");
            button.IsChecked = _pickedScene.Length > 0 && scene.Equals(_pickedScene, StringComparison.OrdinalIgnoreCase);
            button.IsEnabled = _pickedScene.Length > 0;
        }
        _raceRefresh = false;
        if (_pickedNote is not null)
            _pickedNote.Text = _pickedScene.Length == 0 ? "1. Click a scene\n2. Click the races that should use it"
                : $"{CharacterScenes.Label(_pickedScene)}: click the races that should use it";
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
            if (CharacterScenes.Write(folder, SceneMode, _settings.ScenePicks, _settings.MusicPicks))
                Log($"Character screens: {CharacterScenes.ModeName(SceneMode)} scenes" + (_settings.ScenePicks.Count > 0
                    ? ", " + string.Join(", ", _settings.ScenePicks.Select(p => $"{p.Key} = {p.Value}")) : ""));
        }
        catch (Exception error) { Logger.Warning("Couldn't write the character screen scenes", error); }
    }
}

/// <summary>How the character screens will look: one race at a time, big, with Classic / HD and the player's picks.</summary>
public sealed class CharacterScenePreviewWindow : Window
{
    readonly Func<string, string, string> _sceneOf;
    readonly Func<string, Task<Bitmap?>> _picture;
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton _classic = new() { Content = "Classic" }, _hd = new() { Content = "HD" }, _faction = new() { Content = "Faction" };
    readonly WrapPanel _raceButtons = new();
    int _index;
    string _mode;

    public CharacterScenePreviewWindow(string mode, Func<string, string, string> sceneOf, Func<string, Task<Bitmap?>> picture)
    {
        _sceneOf = sceneOf; _picture = picture; _mode = mode;
        Title = "Character screens";
        Width = 1100; Height = 760; CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10));

        _classic.Click += (_, _) => { _mode = "old"; _ = ShowAsync(); };
        _hd.Click += (_, _) => { _mode = "new"; _ = ShowAsync(); };
        _faction.Click += (_, _) => { _mode = "faction"; _ = ShowAsync(); };
        for (int i = 0; i < CharacterScenes.Races.Length; i++)
        {
            CharacterRace race = CharacterScenes.Races[i];
            int index = i;
            Button b = new() { Content = race.Label, FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 4, 4),
                               BorderThickness = new Thickness(0, 0, 0, 3),
                               BorderBrush = new SolidColorBrush(race.Faction == "Alliance" ? Color.Parse("#3FACF4")
                                                                 : race.Faction == "Horde" ? Color.Parse("#FF653D") : Color.Parse("#DD1B3C")) };
            b.Click += (_, _) => { _index = index; _ = ShowAsync(); };
            _raceButtons.Children.Add(b);
        }
        Button previous = new() { Content = "Previous" }, next = new() { Content = "Next" }, done = new() { Content = "Done" };
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        done.Click += (_, _) => Close();

        StackPanel modes = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _classic, _hd, _faction } };
        DockPanel head = new() { Margin = new Thickness(12, 10, 12, 4) };
        DockPanel.SetDock(modes, Dock.Right);
        head.Children.Add(modes);
        head.Children.Add(_title);
        StackPanel top = new() { Children = { head, new Border { Child = _raceButtons, Margin = new Thickness(12, 0, 12, 6) } } };
        StackPanel bottom = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                                    Margin = new Thickness(0, 8, 0, 12), Children = { previous, next, done } };
        DockPanel layout = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(new Border { Child = _image, Margin = new Thickness(12, 0), Background = Brushes.Black });
        Content = layout;
        // tunnel: arrow keys reach the window before a focused button or tick box uses them to move focus
        AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Left) { Step(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        }, RoutingStrategies.Tunnel);
        _ = ShowAsync();
    }

    void Step(int by)
    {
        int count = CharacterScenes.Races.Length;
        _index = ((_index + by) % count + count) % count;
        _ = ShowAsync();
    }

    async Task ShowAsync()
    {
        _classic.IsChecked = _mode == "old";
        _hd.IsChecked = _mode == "new";
        _faction.IsChecked = _mode == "faction";
        for (int i = 0; i < _raceButtons.Children.Count; i++)
            if (_raceButtons.Children[i] is Button b) b.FontWeight = i == _index ? FontWeight.Bold : FontWeight.Normal;
        CharacterRace race = CharacterScenes.Races[_index];
        string scene = _sceneOf(race.Key, _mode);
        _title.Text = $"{race.Label} ({race.Faction}):  {(scene.Length == 0 ? "Alliance or Horde, by the race" : CharacterScenes.Label(scene))}   ({_index + 1} / {CharacterScenes.Races.Length})";
        int shown = _index;
        string mode = _mode;
        Bitmap? bitmap = scene.Length == 0 ? null : await _picture(scene);
        if (shown == _index && mode == _mode) _image.Source = bitmap;
    }
}
