using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Macros on the Game settings page, after the key bindings: the account's macros or a character's, from their
/// macros-cache.txt. Edit the name, icon and text, add or delete; Apply writes the file (after a backup of WTF).
/// </summary>
public partial class MainWindow
{
    sealed record MacroScope(string Label, string AccountFolder, WowCharacter? Character)
    {
        public MacroFile Load() => Character is null ? MacroFile.ForAccount(AccountFolder) : MacroFile.ForCharacter(Character.Folder);
    }

    List<MacroScope> _macroScopes = [];
    MacroFile? _macroFile;            // as read from the game
    List<Macro> _macros = [];         // what the boxes show now
    int _macroScopeShown = -1;
    bool _fillingMacro;
    ComboBox? _macroScopeBox;
    TextBlock? _macroNote, _macroCount, _macroBodyCount;
    ListBox? _macroList;
    TextBox? _macroName, _macroIcon, _macroBody;
    Button? _macroAdd, _macroDelete;
    Grid? _macroEditor;

    /// <summary>Shows names as they are: a macro or character called "Help" is not the launcher's word to translate.</summary>
    static readonly Avalonia.Controls.Templates.FuncDataTemplate<string> RawText =
        new((text, _) => new TextBlock { Classes = { "raw" }, Text = text });

    void BuildMacros()
    {
        TweaksList.Children.Add(new TextBlock { Text = Translation.T("MACROS"), Classes = { "title", "group" }, Margin = new Thickness(0, 22, 0, 0) });
        TweaksList.Children.Add(new TextBlock { Text = Translation.T("Account and character macros").ToUpperInvariant(), Classes = { "section" } });

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 4, 0, 6) };
        top.Children.Add(new TextBlock { Text = Translation.T("Macros for:"), VerticalAlignment = VerticalAlignment.Center, Classes = { "text" }, Margin = new Thickness(0, 0, 10, 0) });
        _macroScopeBox = new ComboBox { MinWidth = 260, ItemTemplate = RawText };
        _macroScopeBox.SelectionChanged += (_, _) => MacroScopeChanged();
        Grid.SetColumn(_macroScopeBox, 1);
        top.Children.Add(_macroScopeBox);
        _macroNote = new TextBlock
        {
            Text = Translation.T("Close the game before you press Apply: the game writes this file when you log out."),
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(_macroNote, 2);
        top.Children.Add(_macroNote);
        TweaksList.Children.Add(top);

        _macroEditor = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 0, 16, 4), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };

        // left: the list, with New and Delete under it
        DockPanel left = new() { Margin = new Thickness(0, 0, 14, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        _macroAdd = new Button { Content = Translation.T("New macro") };
        _macroAdd.Click += (_, _) => AddMacro();
        _macroDelete = new Button { Content = Translation.T("Delete") };
        _macroDelete.Click += (_, _) => DeleteMacro();
        buttons.Children.Add(_macroAdd);
        buttons.Children.Add(_macroDelete);
        _macroCount = new TextBlock { Classes = { "dim" }, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_macroCount, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(_macroCount);
        left.Children.Add(buttons);
        _macroList = new ListBox { Height = 250, ItemTemplate = RawText };
        _macroList.SelectionChanged += (_, _) => { if (!_fillingMacro) ShowMacro(); };
        left.Children.Add(_macroList);
        _macroEditor.Children.Add(left);

        // right: name, icon and the text
        Grid right = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(right, 1);
        TextBlock Label(string text, int row)
        {
            TextBlock t = new() { Text = Translation.T(text), Classes = { "text" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) };
            Grid.SetRow(t, row);
            right.Children.Add(t);
            return t;
        }
        Label("Name", 0);
        _macroName = new TextBox { MaxLength = Macro.NameLimit, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        _macroName.TextChanged += (_, _) => MacroEdited();
        Grid.SetColumn(_macroName, 1);
        right.Children.Add(_macroName);
        Label("Icon", 1);
        StackPanel iconRow = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 6) };
        _macroIcon = new TextBox { Width = 220, Watermark = Macro.DefaultIcon };
        _macroIcon.TextChanged += (_, _) => MacroEdited();
        iconRow.Children.Add(_macroIcon);
        iconRow.Children.Add(new TextBlock
        {
            Text = Translation.T("The icon's file name from Interface\\Icons, without the folder."), Classes = { "dim" }, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(iconRow, 1);
        Grid.SetColumn(iconRow, 1);
        right.Children.Add(iconRow);
        TextBlock bodyLabel = Label("Text", 2);
        bodyLabel.VerticalAlignment = VerticalAlignment.Top;
        _macroBody = new TextBox
        {
            AcceptsReturn = true, NewLine = "\n", TextWrapping = TextWrapping.Wrap, MaxLength = Macro.BodyLimit, Height = 190,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), VerticalContentAlignment = VerticalAlignment.Top,
        };
        _macroBody.TextChanged += (_, _) => MacroEdited();
        Grid.SetRow(_macroBody, 2);
        Grid.SetColumn(_macroBody, 1);
        right.Children.Add(_macroBody);
        _macroBodyCount = new TextBlock { Classes = { "dim" }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(_macroBodyCount, 3);
        Grid.SetColumn(_macroBodyCount, 1);
        right.Children.Add(_macroBodyCount);
        _macroEditor.Children.Add(right);
        TweaksList.Children.Add(_macroEditor);
    }

    /// <summary>Reads the scopes (each account, then its characters) and the chosen one's macros.</summary>
    void LoadMacros()
    {
        if (_macroScopeBox is null) return;
        string? previous = _macroScopeShown >= 0 && _macroScopeShown < _macroScopes.Count ? _macroScopes[_macroScopeShown].Label : null;
        _macroScopes = [];
        try
        {
            if (GameFolder.LooksLikeClient(Folder))
            {
                List<WowCharacter> characters = WowCharacter.List(Folder);
                foreach (string account in WowCharacter.Accounts(Folder))
                {
                    string name = Path.GetFileName(account);
                    _macroScopes.Add(new MacroScope(string.Format(Translation.T("{0} (account macros)"), name), account, null));
                    foreach (WowCharacter c in characters.Where(c => c.Account == name))
                        _macroScopes.Add(new MacroScope($"{c.Name} - {c.Realm} ({name})", account, c));
                }
            }
        }
        catch (Exception e) { Logger.Warning("Couldn't list the macro files", e); }
        _fillingMacro = true;
        try
        {
            _macroScopeBox.ItemsSource = _macroScopes.Select(s => s.Label).ToList();
            _macroScopeBox.SelectedIndex = _macroScopes.Count == 0 ? -1 : Math.Max(0, _macroScopes.FindIndex(s => s.Label == previous));
        }
        finally { _fillingMacro = false; }
        LoadSelectedMacros();
    }

    void MacroScopeChanged()
    {
        if (_fillingMacro || _macroScopeBox is null) return;
        if (MacrosChanged())
        {
            _fillingMacro = true;
            try { _macroScopeBox.SelectedIndex = _macroScopeShown; }
            finally { _fillingMacro = false; }
            Status(Translation.T("Apply or discard your changes first."));
            return;
        }
        LoadSelectedMacros();
    }

    void LoadSelectedMacros()
    {
        _macroScopeShown = _macroScopeBox?.SelectedIndex ?? -1;
        _macroFile = null;
        if (_macroScopeShown >= 0 && _macroScopeShown < _macroScopes.Count)
        {
            try { _macroFile = _macroScopes[_macroScopeShown].Load(); }
            catch (Exception e) { Logger.Warning("Couldn't read the macros", e); }
        }
        _macros = _macroFile?.Macros.Select(m => m.Copy()).ToList() ?? [];
        if (_macroNote is not null && _macroScopes.Count == 0)
            _macroNote.Text = Translation.T("No macros yet: start the game once and log in, then they show here.");
        else if (_macroNote is not null)
            _macroNote.Text = Translation.T("Close the game before you press Apply: the game writes this file when you log out.");
        FillMacroList(0);
        UpdateTweaksDirty();
    }

    void FillMacroList(int select)
    {
        if (_macroList is null) return;
        _fillingMacro = true;
        try
        {
            _macroList.ItemsSource = _macros.Select(m => m.Name.Length > 0 ? m.Name : "(" + Translation.T("no name") + ")").ToList();
            _macroList.SelectedIndex = _macros.Count == 0 ? -1 : Math.Clamp(select, 0, _macros.Count - 1);
        }
        finally { _fillingMacro = false; }
        ShowMacro();
    }

    Macro? SelectedMacro => _macroList?.SelectedIndex is int i and >= 0 && i < _macros.Count ? _macros[i] : null;

    void ShowMacro()
    {
        if (_macroName is null || _macroIcon is null || _macroBody is null) return;
        Macro? m = SelectedMacro;
        _fillingMacro = true;
        try
        {
            _macroName.Text = m?.Name ?? "";
            _macroIcon.Text = m?.Icon ?? "";
            _macroBody.Text = m?.Body ?? "";
        }
        finally { _fillingMacro = false; }
        bool any = m is not null;
        _macroName.IsEnabled = _macroIcon.IsEnabled = _macroBody.IsEnabled = any;
        _macroDelete!.IsEnabled = any;
        _macroAdd!.IsEnabled = _macroFile is not null && _macros.Count < _macroFile.Limit;
        ShowMacroCounts();
        if (_macroEditor is not null)
            _macroEditor.Tag = "macros " + string.Join(" ", _macros.Select(x => x.Name));
    }

    void ShowMacroCounts()
    {
        if (_macroCount is not null)
            _macroCount.Text = _macroFile is null ? "" : string.Format(Translation.T("{0} of {1} macros"), _macros.Count, _macroFile.Limit);
        if (_macroBodyCount is not null)
            _macroBodyCount.Text = SelectedMacro is { } m ? $"{MacroFile.CleanBody(m.Body).Length} / {Macro.BodyLimit}" : "";
    }

    void MacroEdited()
    {
        if (_fillingMacro || SelectedMacro is not { } m) return;
        bool renamed = m.Name != (_macroName?.Text ?? "");
        m.Name = _macroName?.Text ?? "";
        m.Icon = _macroIcon?.Text ?? "";
        m.Body = (_macroBody?.Text ?? "").Replace("\r\n", "\n");
        if (renamed && _macroList is not null)
        {
            // show the new name in the list without losing the caret in the name box
            int i = _macroList.SelectedIndex;
            _fillingMacro = true;
            try
            {
                _macroList.ItemsSource = _macros.Select(x => x.Name.Length > 0 ? x.Name : "(" + Translation.T("no name") + ")").ToList();
                _macroList.SelectedIndex = i;
            }
            finally { _fillingMacro = false; }
        }
        ShowMacroCounts();
        UpdateTweaksDirty();
    }

    void AddMacro()
    {
        if (_macroFile is null || _macros.Count >= _macroFile.Limit) return;
        Macro macro = new() { Id = MacroFile.NextId(_macros, _macroFile.IsCharacter), Name = Translation.T("New macro"), Icon = Macro.DefaultIcon };
        if (macro.Name.Length > Macro.NameLimit) macro.Name = macro.Name[..Macro.NameLimit];
        _macros.Add(macro);
        FillMacroList(_macros.Count - 1);
        _macroName?.Focus();
        _macroName?.SelectAll();
        UpdateTweaksDirty();
    }

    void DeleteMacro()
    {
        if (_macroList?.SelectedIndex is not int i || i < 0 || i >= _macros.Count) return;
        _macros.RemoveAt(i);
        FillMacroList(i);
        UpdateTweaksDirty();
    }

    /// <summary>True when the macros differ from the file.</summary>
    bool MacrosChanged() => _macroFile is not null && MacroFile.Write(_macros) != _macroFile.Text;

    /// <summary>Writes the macros (called by Apply), after a backup of WTF.</summary>
    void SaveMacros()
    {
        if (!MacrosChanged() || _macroFile is null) return;
        Backups.Create(Folder, "before-macro-edit", includeAddons: false, keep: 50);
        MacroFile file = MacroFile.Load(_macroFile.File, _macroFile.IsCharacter);
        file.Macros.Clear();
        file.Macros.AddRange(_macros.Select(m => m.Copy()));
        file.Save();
        Log($"{file.Macros.Count} macros saved to {file.File}");
    }
}
