using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Every option of the game's Interface, Video and Sound windows on the Game settings page, below the launcher's
/// quick settings: a side menu to jump to a section, a search box (Ctrl+F), and Apply / Discard for all of them.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, (GameOption Option, Control Control)> _optionControls = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _optionShown = new(StringComparer.OrdinalIgnoreCase);
    bool _loadingOptions;

    /// <summary>Adds the game's own options to TweaksList (after the quick settings), skipping ones already there.</summary>
    void BuildGameOptions()
    {
        _optionControls.Clear();
        HashSet<string> quick = new(Tweaks.All.Select(t => t.CVar), StringComparer.OrdinalIgnoreCase);
        string? group = null, section = null;
        WrapPanel rows = new();
        foreach (GameOption o in GameOptions.All)
        {
            if (quick.Contains(o.CVar)) continue;
            if (o.Group != group)
            {
                group = o.Group;
                TweaksList.Children.Add(new TextBlock
                {
                    Text = Translation.T(group).ToUpperInvariant(), Classes = { "title", "group" }, Margin = new Thickness(0, 22, 0, 0),
                });
                section = null;
            }
            if (o.Section != section)
            {
                section = o.Section;
                TweaksList.Children.Add(new TextBlock { Text = Translation.T(section).ToUpperInvariant(), Classes = { "section" } });
                rows = new WrapPanel();
                TweaksList.Children.Add(rows);
            }
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("270,150,*"), Margin = new Thickness(0, 3, 16, 3), Width = TweakRowWidth(),
                Tag = $"{o.Label} {o.CVar} {o.Tip} {o.Section} {o.Group}",
            };
            _tweakRows.Add(row);
            string label = o.Label + (o.Restart ? " *" : o.Logout ? " °" : "");
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
            Control control = NewOptionControl(o);
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            string defaultText = o.Default is null ? Translation.T("the game decides")
                : o.IsToggle ? Translation.T((o.Default == "1") != o.Invert ? "on" : "off") : o.Default;
            TextBlock tip = new()
            {
                Text = o.Tip + (o.Tip.Length > 0 ? " " : "") + string.Format(Translation.T("Default: {0}."), defaultText)
                       + (o.Restart ? " " + Translation.T("Needs a game restart.") : o.Logout ? " " + Translation.T("Needs a log out.") : ""),
                Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            ToolTip.SetTip(tip, $"SET {o.CVar}");
            ToolTip.SetTip(control, $"SET {o.CVar}");
            Grid.SetColumn(tip, 2);
            row.Children.Add(tip);
            rows.Children.Add(row);
            _optionControls[o.CVar] = (o, control);
        }
        TweaksList.Children.Add(new TextBlock
        {
            Text = Translation.T("* needs a game restart   ° needs a log out. Account settings are also written to WTF\\Account\\<name>\\config-cache.wtf."),
            Classes = { "dim" }, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        });
        BuildKeyBindings();
    }

    Control NewOptionControl(GameOption o)
    {
        if (o.Choices is { } choices)
        {
            ComboBox box = new() { ItemsSource = choices, Width = 132, HorizontalAlignment = HorizontalAlignment.Left };
            box.SelectionChanged += (_, _) => OptionEdited();
            return box;
        }
        if (o.IsToggle)
        {
            CheckBox check = new();
            check.IsCheckedChanged += (_, _) => OptionEdited();
            return check;
        }
        double step = o.Step is > 0 ? o.Step.Value : 1;
        NumericUpDown number = new()
        {
            Width = 132, Height = 32, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0),
            VerticalContentAlignment = VerticalAlignment.Center, Minimum = (decimal)o.Min!.Value, Maximum = (decimal)o.Max!.Value,
            ClipValueToMinMax = true, Increment = (decimal)step, FormatString = "0.###", ParsingNumberStyle = NumberStyles.Float,
        };
        number.ValueChanged += (_, e) =>
        {
            if (e.NewValue is decimal v && (v < number.Minimum || v > number.Maximum)) { number.Value = Math.Clamp(v, number.Minimum, number.Maximum); return; }
            OptionEdited();
        };
        return number;
    }

    void OptionEdited()
    {
        if (_loadingOptions) return;
        UpdateTweaksDirty();
    }

    static string? OptionValue(GameOption o, Control control) => control switch
    {
        CheckBox c => (c.IsChecked == true) != o.Invert ? "1" : "0",
        ComboBox b => b.SelectedItem as string,
        NumericUpDown n => n.Value?.ToString("0.###", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Fills the boxes from the game's files (after the quick settings are loaded).</summary>
    void LoadGameOptions()
    {
        _optionShown.Clear();
        if (_optionControls.Count == 0 || !GameFolder.LooksLikeClient(Folder)) return;
        GameOptionStore store;
        try { store = GameOptionStore.Load(Folder); }
        catch (Exception e) { Logger.Warning("Couldn't read the game's settings", e); return; }
        _loadingOptions = true;
        try
        {
            foreach ((GameOption o, Control control) in _optionControls.Values)
            {
                string? value = store.Get(o.CVar) ?? o.Default;
                switch (control)
                {
                    case CheckBox c: c.IsChecked = value is not null && (value.Trim() == "1") != o.Invert; break;
                    case ComboBox b: b.SelectedItem = o.Choices!.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)); break;
                    case NumericUpDown n:
                        n.Value = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                            ? Math.Clamp((decimal)d, n.Minimum, n.Maximum) : null;
                        break;
                }
                if (OptionValue(o, control) is { } shown) _optionShown[o.CVar] = shown;
            }
        }
        finally { _loadingOptions = false; }
    }

    /// <summary>The options changed since they were read: cvar and new value.</summary>
    List<(string CVar, string Value)> ChangedGameOptions() =>
        _optionControls.Values
            .Select(x => (x.Option.CVar, Value: OptionValue(x.Option, x.Control)))
            .Where(x => x.Value is not null && (!_optionShown.TryGetValue(x.CVar, out string? old) || old != x.Value))
            .Select(x => (x.CVar, x.Value!)).ToList();

    /// <summary>Writes the changed options (called by Apply, before the page is read again).</summary>
    void SaveGameOptions()
    {
        List<(string CVar, string Value)> changed = ChangedGameOptions();
        if (changed.Count == 0) return;
        GameOptionStore store = GameOptionStore.Load(Folder);
        foreach ((string cvar, string value) in changed)
            if (TweakRule(cvar) is not { Enforce: true }) store.Set(cvar, value);
        store.Save();
        Log($"{Words.Files(changed.Count).Replace("file", "game setting")} saved: {string.Join(", ", changed.Select(c => $"{c.CVar}={c.Value}"))}");
    }

    // ------------------------------------------------------------------------------------------- menu and search
    /// <summary>The side menu: a link per section of the page, under its group.</summary>
    void BuildTweaksMenu()
    {
        TweaksMenu.Children.Clear();
        TweaksMenu.Children.Add(new TextBlock { Text = Translation.T("QUICK SETTINGS"), Classes = { "dim" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        foreach (Control child in TweaksList.Children)
        {
            if (child is not TextBlock { Text: { } text } block || !child.IsVisible) continue;
            if (block.Classes.Contains("group"))
                TweaksMenu.Children.Add(new TextBlock { Text = text, Classes = { "dim" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            else if (block.Classes.Contains("section"))
            {
                Button link = new()
                {
                    Content = char.ToUpper(text[0]) + text[1..].ToLowerInvariant(), Classes = { "link" }, FontSize = 13,
                    Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                link.Click += (_, _) => block.BringIntoView();
                TweaksMenu.Children.Add(link);
            }
        }
    }

    /// <summary>Shows only the rows whose name, setting or description has the search words; empty sections hide.</summary>
    void FilterTweaks(string? query)
    {
        string[] words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(Control row)
        {
            if (words.Length == 0) return true;
            string text = row.Tag as string
                ?? string.Join(" ", ((row as Panel)?.Children ?? []).OfType<TextBlock>().Select(t => t.Text));
            return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        TextBlock? header = null;
        foreach (Control child in TweaksList.Children)
        {
            if (child is TextBlock { } t && (t.Classes.Contains("section") || t.Classes.Contains("group"))) { header = t; continue; }
            if (child is not WrapPanel)
            {
                // other entries (the login screens, Large Address Aware, the server's values): by their text
                string text = child is TextBlock { Text: { } tb } ? tb : child is CheckBox { Content: string c } ? c : child is ContentControl { Content: string cc } ? cc
                    : string.Join(" ", (child as Panel)?.Children.OfType<TextBlock>().Select(x => x.Text) ?? []);
                child.IsVisible = words.Length == 0 || words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (header is not null && header.Classes.Contains("section") && words.Length > 0) header.IsVisible = child.IsVisible;
                else if (header is not null && words.Length == 0) header.IsVisible = true;
                continue;
            }
            if (child is WrapPanel panel)
            {
                int shown = 0;
                foreach (Control row in panel.Children)
                {
                    bool gated = _gatedTweakRows.Any(g => g.Row == row && !OptionOn(g.Group));
                    row.IsVisible = !gated && Matches(row);
                    if (row.IsVisible) shown++;
                }
                panel.IsVisible = shown > 0 && !_gatedTweakRows.Any(g => g.Row == panel && !OptionOn(g.Group));
                if (header is not null && header.Classes.Contains("section")) header.IsVisible = panel.IsVisible;
            }
        }
        // a group heading stays while any of its sections shows
        TextBlock? group = null;
        bool any = false;
        foreach (Control child in TweaksList.Children)
        {
            if (child is TextBlock g && g.Classes.Contains("group")) { if (group is not null) group.IsVisible = any || words.Length == 0; group = g; any = false; }
            else if (child is WrapPanel { IsVisible: true }) any = true;
        }
        if (group is not null) group.IsVisible = any || words.Length == 0;
        TweaksSearchCount.Text = words.Length == 0 ? "" : string.Format(Translation.T("{0} found"),
            TweaksList.Children.OfType<WrapPanel>().Where(p => p.IsVisible).Sum(p => p.Children.Count(r => r.IsVisible)));
        BuildTweaksMenu();
    }

    void HookTweaksSearch()
    {
        TweaksSearch.TextChanged += (_, _) => FilterTweaks(TweaksSearch.Text);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!TweaksPage.IsVisible) OnTab(TweaksTab, new RoutedEventArgs());
                TweaksSearch.Focus();
                TweaksSearch.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && TweaksSearch.IsFocused && !string.IsNullOrEmpty(TweaksSearch.Text))
            {
                TweaksSearch.Text = "";
                e.Handled = true;
            }
        };
    }
}
