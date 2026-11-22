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
/// launcher's own settings: a side menu to jump to a section, a search box (Ctrl+F), and Apply / Discard for all of them.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, (GameOption Option, Control Control)> _optionControls = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _optionShown = new(StringComparer.OrdinalIgnoreCase);
    bool _loadingOptions;
    ComboBox? _optionsForBox;
    TextBlock? _optionsForNote;
    List<WowCharacter> _optionCharacters = [];
    int _optionsForShown;   // the entry the boxes were read for (0 = the account)

    /// <summary>Adds the game's own options to TweaksList (after the quick settings), skipping ones already there.</summary>
    void BuildGameOptions()
    {
        _optionControls.Clear();
        HashSet<string> quick = new(Tweaks.All.Select(t => t.CVar), StringComparer.OrdinalIgnoreCase);
        string? group = null, section = null;
        WrapPanel rows = new();
        bool first = true;
        foreach (GameOption o in GameOptions.All)
        {
            if (o.ModifiedClick is null && quick.Contains(o.CVar)) continue;
            if (o.Group != group)
            {
                group = o.Group;
                TweaksList.Children.Add(new TextBlock
                {
                    Text = Translation.T(group).ToUpperInvariant(), Classes = { "title", "group" }, Margin = new Thickness(0, 22, 0, 0),
                });
                // "Settings for" goes under the first group's heading, before its sections, so it never folds away
                if (first) BuildOptionsFor();
                first = false;
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
                Tag = $"{o.Label} {o.Key} {o.Tip} {o.Section} {o.Group}",
            };
            _tweakRows.Add(row);
            string label = o.Label + (o.Restart ? " *" : o.Logout ? " °" : "");
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
            Control control = NewOptionControl(o);
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            string defaultText = o.Default is null ? Translation.T("the game decides")
                : o.IsToggle ? Translation.T((o.Default == "1") != o.Invert ? "on" : "off")
                : o.Choices is { } ch && ch.FindIndex(x => x.Equals(o.Default, StringComparison.OrdinalIgnoreCase)) is int di and >= 0 ? o.ChoiceLabel(di)
                : o.Default;
            TextBlock tip = new()
            {
                Text = o.Tip + (o.Tip.Length > 0 ? " " : "") + string.Format(Translation.T("Default: {0}."), defaultText)
                       + (o.Restart ? " " + Translation.T("Needs a game restart.") : o.Logout ? " " + Translation.T("Needs a log out.") : "")
                       + (GameOptionStore.PerCharacter.Contains(o.CVar) ? " " + Translation.T("Kept per character.") : ""),
                Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            string where = o.ModifiedClick is null ? $"SET {o.CVar}" : $"{o.Key} (bindings-cache.wtf)";
            ToolTip.SetTip(tip, where);
            ToolTip.SetTip(control, where);
            Grid.SetColumn(tip, 2);
            row.Children.Add(tip);
            rows.Children.Add(row);
            _optionControls[o.Key] = (o, control);
        }
        TweaksList.Children.Add(new TextBlock
        {
            Text = Translation.T("* needs a game restart   ° needs a log out. Account settings are also written to WTF\\Account\\<name>\\config-cache.wtf."),
            Classes = { "dim", "keep" }, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        });
        BuildKeyBindings();
        BuildMacros();
    }

    /// <summary>"Settings for": the account, or a character (for the settings the game keeps per character).</summary>
    void BuildOptionsFor()
    {
        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 18, 0, 0), Classes = { "keep" } };   // shown while searching
        top.Children.Add(new TextBlock { Text = Translation.T("Settings for:"), VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, Margin = new Thickness(0, 0, 10, 0) });
        _optionsForBox = new ComboBox { MinWidth = 260, ItemTemplate = RawText };
        _optionsForBox.SelectionChanged += (_, _) => OptionsForChanged();
        Grid.SetColumn(_optionsForBox, 1);
        top.Children.Add(_optionsForBox);
        _optionsForNote = new TextBlock { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(_optionsForNote, 2);
        top.Children.Add(_optionsForNote);
        TweaksList.Children.Add(top);
    }

    /// <summary>The character chosen in "Settings for", null for the account.</summary>
    WowCharacter? OptionsCharacter =>
        _optionsForBox?.SelectedIndex is int i and > 0 && i - 1 < _optionCharacters.Count ? _optionCharacters[i - 1] : null;

    void FillOptionsFor()
    {
        if (_optionsForBox is null) return;
        string? previous = OptionsCharacter?.Folder;
        try { _optionCharacters = GameFolder.LooksLikeClient(Folder) ? WowCharacter.List(Folder) : []; }
        catch (Exception e) { Logger.Warning("Couldn't list the characters", e); _optionCharacters = []; }
        _loadingOptions = true;
        try
        {
            _optionsForBox.ItemsSource = new[] { Translation.T("Account (all characters)") }.Concat(_optionCharacters.Select(c => c.ToString())).ToList();
            int index = previous is null ? 0 : _optionCharacters.FindIndex(c => c.Folder == previous) + 1;
            _optionsForBox.SelectedIndex = Math.Max(0, index);
            _optionsForShown = _optionsForBox.SelectedIndex;
        }
        finally { _loadingOptions = false; }
    }

    void OptionsForChanged()
    {
        if (_loadingOptions || _optionsForBox is null) return;
        if (ChangedGameOptions().Count > 0)
        {
            // the boxes hold changes for the other choice: keep it until they are applied or discarded
            _loadingOptions = true;
            try { _optionsForBox.SelectedIndex = _optionsForShown; }
            finally { _loadingOptions = false; }
            Status(Translation.T("Apply or discard your changes first."));
            return;
        }
        _optionsForShown = _optionsForBox.SelectedIndex;
        LoadGameOptions();
        UpdateTweaksDirty();
    }

    Control NewOptionControl(GameOption o)
    {
        if (o.Choices is { } choices)
        {
            ComboBox box = new()
            {
                ItemsSource = choices.Select((_, i) => o.ChoiceLabel(i)).ToList(), Width = 132, HorizontalAlignment = HorizontalAlignment.Left,
            };
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
        ComboBox b => b.SelectedIndex is int i and >= 0 && i < o.Choices!.Count ? o.Choices[i] : null,
        NumericUpDown n => n.Value?.ToString("0.###", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Fills the boxes from the game's files (after the quick settings are loaded).</summary>
    void LoadGameOptions()
    {
        _optionShown.Clear();
        if (_optionControls.Count == 0 || !GameFolder.LooksLikeClient(Folder)) return;
        GameOptionStore store;
        WowCharacter? character = OptionsCharacter;
        try { store = GameOptionStore.Load(Folder, character); }
        catch (Exception e) { Logger.Warning("Couldn't read the game's settings", e); return; }
        _loadingOptions = true;
        try
        {
            foreach ((GameOption o, Control control) in _optionControls.Values)
            {
                string? value = store.Get(o.Key) ?? o.Default;
                switch (control)
                {
                    case CheckBox c: c.IsChecked = value is not null && (value.Trim() == "1") != o.Invert; break;
                    case ComboBox b: b.SelectedIndex = o.Choices!.FindIndex(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)); break;
                    case NumericUpDown n:
                        n.Value = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                            ? Math.Clamp((decimal)d, n.Minimum, n.Maximum) : null;
                        break;
                }
                if (OptionValue(o, control) is { } shown) _optionShown[o.Key] = shown;
                // for a character, only what the game keeps per character can change here; the rest is the account's
                bool own = store.IsPerCharacter(o.Key);
                control.IsEnabled = (character is null || own);
                string where = o.ModifiedClick is null ? $"SET {o.CVar}" : $"{o.Key} (bindings-cache.wtf)";
                ToolTip.SetTip(control, character is not null && !own ? Translation.T("Kept for the whole account: choose Account above to change it.")
                    : own ? where + " - " + Translation.T("Each character keeps its own value.") : where);
            }
        }
        finally { _loadingOptions = false; }
        if (_optionsForNote is not null)
            _optionsForNote.Text = character is null
                ? (_optionCharacters.Count > 0 ? Translation.T("Some settings are kept per character (nameplates, block trades...): pick a character to change them for it alone.") : "")
                : string.Format(Translation.T("Settings marked per character are saved for {0} only; the rest stay the account's."), character.Name);
    }

    /// <summary>The options changed since they were read: cvar and new value.</summary>
    List<(string CVar, string Value)> ChangedGameOptions() =>
        _optionControls.Values
            .Select(x => (CVar: x.Option.Key, Value: OptionValue(x.Option, x.Control)))
            .Where(x => x.Value is not null && (!_optionShown.TryGetValue(x.CVar, out string? old) || old != x.Value))
            .Select(x => (x.CVar, x.Value!)).ToList();

    /// <summary>Writes the changed options (called by Apply, before the page is read again).</summary>
    void SaveGameOptions()
    {
        List<(string CVar, string Value)> changed = ChangedGameOptions();
        if (changed.Count == 0) return;
        GameOptionStore store = GameOptionStore.Load(Folder, OptionsCharacter);
        foreach ((string cvar, string value) in changed)
            if (TweakRule(cvar) is not { Enforce: true } && !store.Set(cvar, value))
                Log($"{cvar} not saved: start the game and log in once, so it makes the account's folder.");
        store.Save();
        Log($"{Words.Files(changed.Count).Replace("file", "game setting")} saved{(store.Character is { } c ? " for " + c : "")}: {string.Join(", ", changed.Select(c => $"{c.CVar}={c.Value}"))}");
    }

    // ------------------------------------------------------------------------------------------- menu and search
    readonly List<(string Key, TextBlock Head)> _sectionHeads = [];
    HashSet<string>? _openSections;   // null until the page is first built: then the CLIENT sections are open, the rest closed

    /// <summary>Gives every section heading its plain name (in Tag, for the menu) and a click to open or close it.</summary>
    void PrepareSections()
    {
        _sectionHeads.Clear();
        string group = "";
        foreach (Control child in TweaksList.Children)
        {
            if (child is not TextBlock block) continue;
            if (block.Classes.Contains("group")) { group = block.Text ?? ""; continue; }
            if (!block.Classes.Contains("section")) continue;
            block.Tag ??= block.Text ?? "";
            string key = group + "/" + block.Tag;
            _sectionHeads.Add((key, block));
            block.Cursor = new Cursor(StandardCursorType.Hand);
            block.Background = Brushes.Transparent;   // the whole heading line takes the click, not just its letters
            block.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed || IsSearching) return;
                if (!OpenSections.Remove(key)) OpenSections.Add(key);
                FilterTweaks(TweaksSearch.Text);
                e.Handled = true;
            };
        }
        _openSections ??= [.. _sectionHeads.Where(h => h.Key.StartsWith("CLIENT/", StringComparison.Ordinal)).Select(h => h.Key)];
    }

    HashSet<string> OpenSections => _openSections ??= [];

    bool IsSearching => !string.IsNullOrWhiteSpace(TweaksSearch.Text);

    /// <summary>Hides what is under a closed section's heading (the heading stays, with an arrow). While searching
    /// every match shows, opened.</summary>
    void ApplyCollapsed()
    {
        bool searching = IsSearching;
        TextBlock? head = null;
        bool open = true;
        foreach (Control child in TweaksList.Children)
        {
            if (child is TextBlock block && block.Classes.Contains("group")) { head = null; continue; }
            if (child is TextBlock section && _sectionHeads.FindIndex(h => h.Head == section) is int i and >= 0)
            {
                head = section;
                open = searching || OpenSections.Contains(_sectionHeads[i].Key);
                section.Text = (open ? "\u25BE  " : "\u25B8  ") + section.Tag;
                continue;
            }
            if (head is null || child.Classes.Contains("keep")) continue;
            if (!open) child.IsVisible = false;
        }
    }

    /// <summary>The side menu: a link per section of the page, under its group, and Expand all / Collapse all.</summary>
    void BuildTweaksMenu()
    {
        TweaksMenu.Children.Clear();
        List<string> shown = _sectionHeads.Where(h => h.Head.IsVisible).Select(h => h.Key).ToList();
        bool allOpen = shown.Count > 0 && shown.All(OpenSections.Contains);
        Button all = new()
        {
            Content = Translation.T(allOpen ? "Collapse all" : "Expand all"), FontSize = 12, Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 0, 8), IsEnabled = !IsSearching && shown.Count > 0,
        };
        all.Click += (_, _) =>
        {
            if (allOpen) OpenSections.Clear();
            else OpenSections.UnionWith(_sectionHeads.Select(h => h.Key));
            FilterTweaks(TweaksSearch.Text);
        };
        TweaksMenu.Children.Add(all);
        foreach (Control child in TweaksList.Children)
        {
            if (child is not TextBlock { Text: { } text } block || !child.IsVisible) continue;
            if (block.Classes.Contains("group"))
                TweaksMenu.Children.Add(new TextBlock { Text = text, Classes = { "dim" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            else if (block.Classes.Contains("section"))
            {
                string name = block.Tag as string ?? text;
                if (name.Length == 0) continue;
                Button link = new()
                {
                    Content = MenuName(name), Classes = { "nav" }, FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                link.Click += (_, _) =>
                {
                    // open it first, then scroll once the page has laid it out
                    if (_sectionHeads.FirstOrDefault(h => h.Head == block).Key is { } key && OpenSections.Add(key)) FilterTweaks(TweaksSearch.Text);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => block.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
                };
                TweaksMenu.Children.Add(link);
            }
        }
    }

    /// <summary>A section's name for the side menu: "Login screen", "Awesome WotLK", "DXVK" (not all capitals).</summary>
    static string MenuName(string name)
    {
        string upper = name.ToUpperInvariant();
        if (upper is "DXVK" or "WOW.EXE") return upper == "DXVK" ? "DXVK" : "Wow.exe";
        if (upper == "AWESOME WOTLK") return "Awesome WotLK";
        return char.ToUpper(name[0]) + name[1..].ToLowerInvariant();
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
            if (child.Classes.Contains("keep")) { child.IsVisible = true; continue; }
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
        ApplyCollapsed();
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
