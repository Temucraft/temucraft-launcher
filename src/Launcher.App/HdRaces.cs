using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Character models: HD, with any race back on Classic (a Classic Blood Elf among HD races, say). The race buttons sit
/// on the Character models card; the launcher builds patch-K and patch-_ from the HD pieces (see HdModels).
/// </summary>
public partial class MainWindow
{
    const string ModelsGroup = "Character models";

    /// <summary>The races (parts) that should be HD now: none unless Character models is HD.</summary>
    List<string> HdParts()
    {
        if (_manifest is null) return [];
        string? chosen = UpdatePlanner.ChosenOption(_manifest, _settings.Options, ModelsGroup);
        if (!string.Equals(chosen, "HD", StringComparison.OrdinalIgnoreCase)) return [];
        return [.. HdModels.Parts.Where(p => !_settings.HdClassicParts.Contains(p, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>Builds or removes the HD files to match the picks (in the background). Closes the game first if asked.</summary>
    async Task SyncHdModelsAsync(string folder, bool askToCloseGame = true)
    {
        if (_manifest is null || !GameFolder.LooksLikeClient(folder)) return;
        List<string> hd = HdParts();
        if (HdModels.UpToDate(folder, [.. hd.Where(p => File.Exists(HdModels.PiecePath(folder, p)))])) return;
        if (askToCloseGame && !await Dispatcher.UIThread.InvokeAsync(() => MakeSureGameClosedAsync(folder))) return;
        try
        {
            string? line = await Task.Run(() => HdModels.Sync(folder, hd, text => Status(text)));
            if (line is not null) { Log(line); Status(line + "."); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Warning("Couldn't build the HD character models (is the game running?)", error);
            Status("Couldn't build the HD character models. Close the game and press UPDATE.");
        }
    }

    /// <summary>The race buttons under the Character models choice: lit = HD, dark = Classic.</summary>
    Control HdRacePanel(OptionGroup group)
    {
        StackPanel box = new() { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        TextBlock note = new()
        {
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "With HD: click a race to keep it Classic (dark) or HD (lit). Each race is all one or the other; NPCs of that race follow it.",
        };
        WrapPanel chips = new();
        foreach (string part in HdModels.Parts)
        {
            ToggleButton chip = new()
            {
                Content = HdModels.PartNames[part], FontSize = 12, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3),
                IsChecked = !_settings.HdClassicParts.Contains(part, StringComparer.OrdinalIgnoreCase),
            };
            ToolTip.SetTip(chip, "Lit: HD. Dark: Classic.");
            string mine = part;
            chip.IsCheckedChanged += async (_, _) =>
            {
                _settings.HdClassicParts.RemoveAll(p => p.Equals(mine, StringComparison.OrdinalIgnoreCase));
                if (chip.IsChecked != true) _settings.HdClassicParts.Add(mine);
                SaveSettings();
                await SyncHdModelsAsync(Folder);
            };
            chips.Children.Add(chip);
        }
        Button look = new() { Content = "Preview races", FontSize = 12 };
        look.Click += async (_, _) => await new ModelsPreviewWindow(group, part => !_settings.HdClassicParts.Contains(part, StringComparer.OrdinalIgnoreCase),
                                                                     PictureAsync).ShowDialog(this);
        box.Children.Add(note);
        box.Children.Add(chips);
        box.Children.Add(look);
        void Show() => box.IsVisible = HdParts().Count > 0 || string.Equals(
            UpdatePlanner.ChosenOption(_manifest!, _settings.Options, ModelsGroup), "HD", StringComparison.OrdinalIgnoreCase);
        Show();
        return box;
    }
}

/// <summary>Each race big, Classic or HD: pictures preview-&lt;part&gt;-Classic.jpg / preview-&lt;part&gt;-HD.jpg next to the
/// Character models group.json. Left / Right arrow go through the races.</summary>
public sealed class ModelsPreviewWindow : Window
{
    readonly OptionGroup _group;
    readonly Func<string, Task<Bitmap?>> _picture;
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _missing = new() { Classes = { "dim" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                          Text = "No picture of this one yet." };
    readonly ToggleButton _classic = new() { Content = "Classic" }, _hd = new() { Content = "HD" };
    readonly WrapPanel _races = new();
    int _index;
    bool _showHd;

    public ModelsPreviewWindow(OptionGroup group, Func<string, bool> isHd, Func<string, Task<Bitmap?>> picture)
    {
        _group = group; _picture = picture;
        Title = "Character models";
        Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _showHd = isHd(HdModels.Parts[0]);
        foreach ((string part, int i) in HdModels.Parts.Select((p, i) => (p, i)))
        {
            Button b = new() { Content = HdModels.PartNames[part], FontSize = 12, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2) };
            b.Click += (_, _) => { _index = i; Refresh(); };
            _races.Children.Add(b);
        }
        _classic.Click += (_, _) => { _showHd = false; Refresh(); };
        _hd.Click += (_, _) => { _showHd = true; Refresh(); };
        Button prev = new() { Content = "Previous" }, next = new() { Content = "Next" }, done = new() { Content = "Done" };
        prev.Click += (_, _) => Move(-1);
        next.Click += (_, _) => Move(1);
        done.Click += (_, _) => Close();
        DockPanel top = new() { Margin = new Thickness(14, 10) };
        StackPanel modes = new() { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _classic, _hd } };
        DockPanel.SetDock(modes, Dock.Right);
        top.Children.Add(modes);
        top.Children.Add(_title);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                                     Margin = new Thickness(0, 8), Children = { prev, next, done } };
        Grid pictureArea = new() { Margin = new Thickness(14, 0), Children = { _missing, _image } };
        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        StackPanel racesRow = new() { Margin = new Thickness(14, 0, 14, 8), Children = { _races } };
        DockPanel.SetDock(racesRow, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(racesRow);
        root.Children.Add(buttons);
        root.Children.Add(pictureArea);
        Content = root;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Left) { Move(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Move(1); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    void Move(int step)
    {
        _index = (_index + step + HdModels.Parts.Length) % HdModels.Parts.Length;
        Refresh();
    }

    async void Refresh()
    {
        string part = HdModels.Parts[_index];
        string mode = _showHd ? "HD" : "Classic";
        _classic.IsChecked = !_showHd;
        _hd.IsChecked = _showHd;
        _title.Text = $"{HdModels.PartNames[part]}: {mode}   ({_index + 1} / {HdModels.Parts.Length})";
        for (int i = 0; i < _races.Children.Count; i++)
            if (_races.Children[i] is Button b) b.FontWeight = i == _index ? FontWeight.Bold : FontWeight.Normal;
        string key = $"{part}-{mode}";
        string? url = _group.Previews.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        int asked = _index;
        bool hd = _showHd;
        Bitmap? bitmap = url is null ? null : await _picture(url);
        if (asked != _index || hd != _showHd) return;   // the player moved on while it loaded
        _image.Source = bitmap;
        _missing.IsVisible = bitmap is null;
    }
}
