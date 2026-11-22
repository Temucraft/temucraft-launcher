using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The login screen picker: one button per expansion; clicking one shows its versions (Wrath: with or without
/// Sindragosa, Legion: release or beta ...), each with its picture and a box to use it. Clicking a picture opens it
/// big, with the login buttons on top if wanted, and previous / next to go through all of them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Expansion, then its versions: (code in loginui.lua, label). Codes the file doesn't have are skipped;
    /// scenes in the file that aren't listed here go under "Races and factions".</summary>
    static readonly (string Name, (string Code, string Label)[] Versions)[] SceneGroups =
    [
        ("Classic", [("cl", "Classic")]),
        ("The Burning Crusade", [("bc", "The Burning Crusade")]),
        ("Wrath of the Lich King", [("lk", "With Sindragosa (World)"), ("ns", "Without Sindragosa (China)")]),
        ("Cataclysm", [("ca", "Cataclysm")]),
        ("Mists of Pandaria", [("mp", "Mists of Pandaria")]),
        ("Warlords of Draenor", [("wd", "Warlords of Draenor")]),
        ("Legion", [("le", "Release"), ("ln", "Beta")]),
        ("Battle for Azeroth", [("ba", "Battle for Azeroth")]),
        ("Shadowlands", [("sl", "Shadowlands")]),
        ("Dragonflight", [("df", "Release"), ("nd", "Beta (without Alexstrasza)")]),
        ("The War Within", [("ww", "Closed"), ("wn", "Open")]),
    ];

    StackPanel? _sceneDetail;
    readonly List<(string Group, string Code, string Label)> _sceneOrder = [];
    readonly Dictionary<string, TextBlock> _sceneGroupCounts = [];

    /// <summary>The expansion buttons for these scenes (each scene's tick box goes in _sceneBoxes).</summary>
    void BuildSceneGroups(IReadOnlyList<LoginScene> scenes)
    {
        _sceneOrder.Clear();
        _sceneGroupCounts.Clear();
        Dictionary<string, LoginScene> byCode = scenes.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        List<(string Name, List<(string Code, string Label)> Versions)> groups = [];
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, (string Code, string Label)[] versions) in SceneGroups)
        {
            List<(string, string)> present = [.. versions.Where(v => byCode.ContainsKey(v.Code))];
            if (present.Count == 0) continue;
            groups.Add((name, present));
            foreach ((string code, string _) in present) placed.Add(code);
        }
        List<(string, string)> rest = [.. scenes.Where(s => !placed.Contains(s.Code)).Select(s => (s.Code, LoginScreens.Title(s.Name)))];
        if (rest.Count > 0) groups.Add(("Races and factions", rest));

        foreach (LoginScene scene in scenes)
        {
            CheckBox box = new() { Content = "Use this background", IsChecked = scene.On, FontSize = 12 };
            box.IsCheckedChanged += (_, _) => RefreshSceneCounts();
            _sceneBoxes[scene.Code] = box;
        }
        foreach ((string name, List<(string Code, string Label)> versions) in groups)
            foreach ((string code, string label) in versions)
                _sceneOrder.Add((name, code, label));

        foreach ((string name, List<(string Code, string Label)> versions) in groups)
        {
            StackPanel tile = new() { Spacing = 2, Width = 184, Margin = new Thickness(0, 0, 12, 8),
                                      Cursor = new Cursor(StandardCursorType.Hand) };
            Image picture = new() { Width = 180, Height = 100, Stretch = Stretch.Fill };
            if (_manifest?.LoginScreenPreviews.TryGetValue(versions[0].Code, out string? url) == true)
                _ = SetSceneImageAsync(picture, url);
            tile.Children.Add(new Border { Child = picture, Width = 180, Height = 100, HorizontalAlignment = HorizontalAlignment.Left,
                                           Background = Brushes.Black });
            tile.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
            TextBlock count = new() { Classes = { "dim" }, FontSize = 11 };
            _sceneGroupCounts[name] = count;
            tile.Children.Add(count);
            List<(string Code, string Label)> mine = versions;
            tile.PointerPressed += (_, _) => ShowSceneGroup(name, mine);
            _scenePanel!.Children.Add(tile);
        }
        _sceneDetail = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 8), IsVisible = false };
        _scenePanel!.Children.Add(_sceneDetail);
        RefreshSceneCounts();
    }

    void RefreshSceneCounts()
    {
        foreach (var group in _sceneOrder.GroupBy(s => s.Group))
            if (_sceneGroupCounts.TryGetValue(group.Key, out TextBlock? count))
            {
                int on = group.Count(s => _sceneBoxes.TryGetValue(s.Code, out CheckBox? b) && b.IsChecked == true);
                int all = group.Count();
                count.Text = all == 1 ? (on == 1 ? "On" : "Off") : $"{on} of {all} on";
            }
    }

    /// <summary>An expansion's versions under the buttons: picture (click for the big view) and its tick box.</summary>
    void ShowSceneGroup(string name, List<(string Code, string Label)> versions)
    {
        if (_sceneDetail is null) return;
        foreach (CheckBox box in _sceneBoxes.Values)
            if (box.Parent is Panel parent) parent.Children.Remove(box);
        _sceneDetail.Children.Clear();
        _sceneDetail.Width = Math.Max(600, _scenePanel?.Bounds.Width - 20 ?? 600);
        StackPanel head = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center });
        Button close = new() { Content = "Close", FontSize = 12, Padding = new Thickness(8, 2) };
        close.Click += (_, _) => { _sceneDetail.IsVisible = false; };
        head.Children.Add(close);
        _sceneDetail.Children.Add(head);
        WrapPanel cards = new();
        foreach ((string code, string label) in versions)
        {
            StackPanel card = new() { Spacing = 3, Width = 324, Margin = new Thickness(0, 0, 14, 6) };
            Grid frame = new() { Width = 320, Height = 180, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Black,
                                 Cursor = new Cursor(StandardCursorType.Hand) };
            Image picture = new() { Stretch = Stretch.Fill };
            if (_manifest?.LoginScreenPreviews.TryGetValue(code, out string? url) == true) _ = SetSceneImageAsync(picture, url);
            frame.Children.Add(picture);
            Control layer = LoginButtonsOverlay.Build();
            layer.IsVisible = _sceneOverlayOn;
            _sceneOverlays.Add(layer);
            frame.Children.Add(layer);
            int index = _sceneOrder.FindIndex(s => s.Code == code);
            frame.PointerPressed += (_, _) => _ = OpenScenePreviewAsync(index);
            ToolTip.SetTip(frame, "Click to see it big");
            card.Children.Add(frame);
            card.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Bold });
            if (_sceneBoxes.TryGetValue(code, out CheckBox? box)) card.Children.Add(box);
            cards.Children.Add(card);
        }
        _sceneDetail.Children.Add(cards);
        _sceneDetail.IsVisible = true;
        _sceneDetail.BringIntoView();
    }

    async Task SetSceneImageAsync(Image picture, string url)
    {
        if (_server is null) return;
        picture.Source = await PictureAsync(url);
    }

    async Task OpenScenePreviewAsync(int index)
    {
        if (index < 0 || _sceneOrder.Count == 0) return;
        ScenePreviewWindow window = new(_sceneOrder, index, _sceneBoxes, _sceneOverlayOn,
            async code => _manifest?.LoginScreenPreviews.TryGetValue(code, out string? url) == true ? await PictureAsync(url) : null);
        await window.ShowDialog(this);
        _sceneOverlayOn = window.OverlayOn;
        foreach (Control layer in _sceneOverlays) layer.IsVisible = _sceneOverlayOn;
        if (_sceneOverlayBox is not null) _sceneOverlayBox.IsChecked = _sceneOverlayOn;
        RefreshSceneCounts();
    }
}

/// <summary>One login background big: previous / next through all of them, "Use this background", and the login
/// buttons on top if wanted. Left / Right arrows work too.</summary>
public sealed class ScenePreviewWindow : Window
{
    readonly List<(string Group, string Code, string Label)> _order;
    readonly Dictionary<string, CheckBox> _boxes;
    readonly Func<string, Task<Bitmap?>> _picture;
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Control _layer = LoginButtonsOverlay.Build();
    readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _use = new() { Content = "Use this background" };
    readonly CheckBox _buttons = new() { Content = "Show the login buttons" };
    int _index;
    bool _filling;

    public bool OverlayOn => _buttons.IsChecked == true;

    public ScenePreviewWindow(List<(string Group, string Code, string Label)> order, int start, Dictionary<string, CheckBox> boxes,
                              bool overlayOn, Func<string, Task<Bitmap?>> picture)
    {
        _order = order; _boxes = boxes; _picture = picture; _index = start;
        Title = "Login screen";
        Width = 1000; Height = 680; CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10));

        _buttons.IsChecked = overlayOn;
        _layer.IsVisible = overlayOn;
        _buttons.IsCheckedChanged += (_, _) => _layer.IsVisible = _buttons.IsChecked == true;
        _use.IsCheckedChanged += (_, _) =>
        {
            if (_filling) return;
            if (_boxes.TryGetValue(_order[_index].Code, out CheckBox? box)) box.IsChecked = _use.IsChecked == true;
        };

        Button previous = new() { Content = "< Previous", Padding = new Thickness(12, 4) };
        previous.Click += (_, _) => Go(-1);
        Button next = new() { Content = "Next >", Padding = new Thickness(12, 4) };
        next.Click += (_, _) => Go(1);
        Button done = new() { Content = "Done", Padding = new Thickness(14, 4) };
        done.Click += (_, _) => Close();

        Grid pictureArea = new() { Margin = new Thickness(12) };
        _image.Width = 1920; _image.Height = 1080;
        pictureArea.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = new Grid { Width = 1920, Height = 1080, Background = Brushes.Black,
            Children = { _image, Wrap(_layer) } } });

        DockPanel top = new() { Margin = new Thickness(12, 10, 12, 0) };
        top.Children.Add(_title);
        StackPanel bottom = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0, 12, 12),
                                    HorizontalAlignment = HorizontalAlignment.Center };
        bottom.Children.Add(previous);
        bottom.Children.Add(_use);
        bottom.Children.Add(_buttons);
        bottom.Children.Add(next);
        bottom.Children.Add(done);
        foreach (Control c in bottom.Children) c.VerticalAlignment = VerticalAlignment.Center;

        DockPanel layout = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(pictureArea);
        Content = layout;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) { Go(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Go(1); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
        _ = ShowAsync();
    }

    // the overlay is built for any size; inside the 1920 x 1080 grid it fills it exactly
    static Control Wrap(Control layer) { layer.Width = 1920; layer.Height = 1080; return layer; }

    void Go(int step)
    {
        _index = ((_index + step) % _order.Count + _order.Count) % _order.Count;
        _ = ShowAsync();
    }

    async Task ShowAsync()
    {
        (string group, string code, string label) = _order[_index];
        _title.Text = group == label ? $"{group}   ({_index + 1} of {_order.Count})" : $"{group}: {label}   ({_index + 1} of {_order.Count})";
        _filling = true;
        _use.IsChecked = _boxes.TryGetValue(code, out CheckBox? box) && box.IsChecked == true;
        _filling = false;
        int shown = _index;
        Bitmap? bitmap = await _picture(code);
        if (shown == _index) _image.Source = bitmap;
    }
}
