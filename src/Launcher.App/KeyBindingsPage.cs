using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Key bindings on the Game settings page: every command of the game's Key Bindings window with two keys each.
/// Click a key, then press a key, mouse button or wheel; right-click a key to clear it; Escape cancels.
/// </summary>
public partial class MainWindow
{
    const int KeySlots = 2;
    readonly Dictionary<string, Button[]> _keyButtons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Grid> _keyRows = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<string>> _bindKeys = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _bindShown = new(StringComparer.OrdinalIgnoreCase);
    List<BindingsFile> _bindFiles = [];
    ComboBox? _bindFileBox;
    TextBlock? _bindNote;
    (string Command, int Slot, Button Button)? _capture;

    void BuildKeyBindings()
    {
        _keyButtons.Clear();
        _keyRows.Clear();
        TweaksList.Children.Add(new TextBlock { Text = Translation.T("CONTROLS"), Classes = { "title", "group" }, Margin = new Thickness(0, 22, 0, 0) });
        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 4, 0, 4) };
        top.Children.Add(new TextBlock { Text = Translation.T("Key bindings for:"), VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, Margin = new Thickness(0, 0, 10, 0) });
        _bindFileBox = new ComboBox { MinWidth = 260 };
        _bindFileBox.SelectionChanged += (_, _) => { if (!_loadingOptions) LoadSelectedBindings(); };
        Grid.SetColumn(_bindFileBox, 1);
        top.Children.Add(_bindFileBox);
        _bindNote = new TextBlock { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(_bindNote, 2);
        top.Children.Add(_bindNote);
        TweaksList.Children.Add(top);
        string? header = null;
        WrapPanel rows = new();
        foreach (KeyCommand c in KeyCommands.All)
        {
            if (c.Header != header)
            {
                header = c.Header;
                TweaksList.Children.Add(new TextBlock { Text = Translation.T(header).ToUpperInvariant(), Classes = { "section" } });
                rows = new WrapPanel();
                TweaksList.Children.Add(rows);
            }
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("270,140,140,*"), Margin = new Thickness(0, 2, 16, 2), Width = TweakRowWidth() };
            _tweakRows.Add(row);
            row.Children.Add(new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
            Button[] buttons = new Button[KeySlots];
            for (int slot = 0; slot < KeySlots; slot++)
            {
                int s = slot;
                Button key = new() { Width = 130, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12 };
                ToolTip.SetTip(key, Translation.T("Click, then press a key, mouse button or wheel. Right-click to clear. Escape cancels."));
                key.Click += (_, _) => StartCapture(c.Command, s, key);
                key.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton == MouseButton.Right && _capture is null) { SetKey(c.Command, s, ""); e.Handled = true; }
                };
                Grid.SetColumn(key, slot + 1);
                row.Children.Add(key);
                buttons[slot] = key;
            }
            rows.Children.Add(row);
            _keyButtons[c.Command] = buttons;
            _keyRows[c.Command] = row;
        }
        HookKeyCapture();
    }

    void LoadBindings()
    {
        if (_bindFileBox is null) return;
        _bindFiles = GameFolder.LooksLikeClient(Folder) ? BindingsFile.Find(Folder) : [];
        string? previous = _bindFileBox.SelectedItem as string;
        _loadingOptions = true;
        try
        {
            _bindFileBox.ItemsSource = _bindFiles.Select(f => f.Name).ToList();
            _bindFileBox.SelectedIndex = _bindFiles.Count == 0 ? -1 : Math.Max(0, _bindFiles.FindIndex(f => f.Name == previous));
        }
        finally { _loadingOptions = false; }
        LoadSelectedBindings();
    }

    BindingsFile? SelectedBindings => _bindFileBox?.SelectedIndex is int i and >= 0 && i < _bindFiles.Count ? _bindFiles[i] : null;

    void LoadSelectedBindings()
    {
        BindingsFile? file = SelectedBindings is { } f ? BindingsFile.Load(f.File, f.Name) : null;
        // no file yet: the game's own keys, shown greyed out (the boxes are off until the game has made the file)
        _bindKeys = file?.Keys() ?? KeyDefaults.Effective([]);
        _bindShown = _bindKeys.ToDictionary(k => k.Key, k => string.Join(" ", k.Value), StringComparer.OrdinalIgnoreCase);
        // the account folder exists once the game has been logged in to; the file itself only lists changed keys
        bool usable = file is not null && Directory.Exists(Path.GetDirectoryName(file.File));
        if (_bindNote is not null)
            _bindNote.Text = !usable ? Translation.T("These are the game's own keys. Start the game once and log in, then you can change them here.")
                : _bindFiles.Count > 1 ? Translation.T("A character with its own key bindings uses them instead of the account's.")
                : Translation.T("Saved in the account's bindings-cache.wtf. Close the game before you press Apply.");
        foreach ((string command, Button[] buttons) in _keyButtons)
        {
            foreach (Button b in buttons) b.IsEnabled = usable;
            ShowKeys(command);
        }
        UpdateTweaksDirty();
    }

    void ShowKeys(string command)
    {
        List<string> keys = _bindKeys.GetValueOrDefault(command) ?? [];
        Button[] buttons = _keyButtons[command];
        for (int slot = 0; slot < KeySlots; slot++)
            buttons[slot].Content = slot < keys.Count && keys[slot].Length > 0 ? keys[slot] : Translation.T("Not bound");
        Grid row = _keyRows[command];
        KeyCommand c = KeyCommands.All.First(k => k.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        row.Tag = $"{c.Label} {c.Command} {c.Header} {string.Join(" ", keys)}";
    }

    void StartCapture(string command, int slot, Button button)
    {
        if (_capture is { } old) ShowKeys(old.Command);
        _capture = (command, slot, button);
        button.Content = Translation.T("Press a key…");
    }

    /// <summary>Gives the key to the command (taking it off any other) and shows the change.</summary>
    void SetKey(string command, int slot, string key)
    {
        if (key.Length > 0)
            foreach ((string other, List<string> keys) in _bindKeys)
                if (!other.Equals(command, StringComparison.OrdinalIgnoreCase) && keys.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    if (_keyButtons.ContainsKey(other)) ShowKeys(other);
                    Status(string.Format(Translation.T("{0} was on {1}; it is on {2} now."), key, LabelOf(other), LabelOf(command)));
                }
        if (!_bindKeys.TryGetValue(command, out List<string>? list)) _bindKeys[command] = list = [];
        while (list.Count <= slot) list.Add("");
        list[slot] = key;
        while (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        ShowKeys(command);
        UpdateTweaksDirty();
    }

    static string LabelOf(string command) =>
        KeyCommands.All.FirstOrDefault(k => k.Command.Equals(command, StringComparison.OrdinalIgnoreCase))?.Label ?? command;

    /// <summary>The commands whose keys differ from the file's.</summary>
    Dictionary<string, List<string>> ChangedBindings() =>
        _bindKeys.Where(k => _bindShown.GetValueOrDefault(k.Key, "") != string.Join(" ", k.Value))
                 .ToDictionary(k => k.Key, k => k.Value.Where(x => x.Length > 0).ToList(), StringComparer.OrdinalIgnoreCase);

    void SaveBindings()
    {
        Dictionary<string, List<string>> changed = ChangedBindings();
        if (changed.Count == 0 || SelectedBindings is not { } selected) return;
        BindingsFile file = BindingsFile.Load(selected.File, selected.Name);
        file.Set(changed);
        file.Save();
        Log($"Key bindings saved to {file.File}: {string.Join(", ", changed.Select(c => $"{c.Key}={string.Join("/", c.Value)}"))}");
    }

    void HookKeyCapture()
    {
        if (_keyCaptureHooked) return;
        _keyCaptureHooked = true;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_capture is not { } c) return;
            e.Handled = true;
            if (e.Key == Key.Escape) { _capture = null; ShowKeys(c.Command); return; }
            if (WowKey(e.Key) is not { } name) return;   // a modifier alone: wait for the real key
            KeyModifiers m = e.KeyModifiers;
            _capture = null;
            SetKey(c.Command, c.Slot, BindingsFile.KeyName(name, m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Shift)));
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_capture is not { } c) return;
            PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            string? name = kind switch
            {
                PointerUpdateKind.MiddleButtonPressed => "BUTTON3",
                PointerUpdateKind.XButton1Pressed => "BUTTON4",
                PointerUpdateKind.XButton2Pressed => "BUTTON5",
                _ => null,
            };
            if (name is null) return;
            e.Handled = true;
            KeyModifiers m = e.KeyModifiers;
            _capture = null;
            SetKey(c.Command, c.Slot, BindingsFile.KeyName(name, m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Shift)));
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (_capture is not { } c || e.Delta.Y == 0) return;
            e.Handled = true;
            KeyModifiers m = e.KeyModifiers;
            _capture = null;
            SetKey(c.Command, c.Slot, BindingsFile.KeyName(e.Delta.Y > 0 ? "MOUSEWHEELUP" : "MOUSEWHEELDOWN",
                m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Shift)));
        }, RoutingStrategies.Tunnel);
    }

    bool _keyCaptureHooked;

    /// <summary>The game's name for a keyboard key; null for modifiers and keys the game can't bind.</summary>
    static string? WowKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NUMPAD" + (int)(key - Key.NumPad0),
        Key.Add => "NUMPADPLUS", Key.Subtract => "NUMPADMINUS", Key.Multiply => "NUMPADMULTIPLY",
        Key.Divide => "NUMPADDIVIDE", Key.Decimal => "NUMPADDECIMAL",
        Key.Space => "SPACE", Key.Tab => "TAB", Key.Enter => "ENTER", Key.Back => "BACKSPACE",
        Key.Insert => "INSERT", Key.Delete => "DELETE", Key.Home => "HOME", Key.End => "END",
        Key.PageUp => "PAGEUP", Key.PageDown => "PAGEDOWN",
        Key.Up => "UP", Key.Down => "DOWN", Key.Left => "LEFT", Key.Right => "RIGHT",
        Key.NumLock => "NUMLOCK", Key.Scroll => "SCROLLLOCK", Key.Pause => "PAUSE", Key.PrintScreen => "PRINTSCREEN",
        Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.OemQuestion => "/", Key.OemPipe => "\\", Key.OemBackslash => "\\", Key.OemTilde => "`",
        _ => null,
    };
}
