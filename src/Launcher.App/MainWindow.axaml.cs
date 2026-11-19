using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

public partial class MainWindow : Window
{
    public static readonly string SettingsFile = Path.Combine(UserSettings.DefaultFolder, "settings.json");
    readonly string _settingsFile = SettingsFile;
    readonly string _cacheFile = Path.Combine(UserSettings.DefaultFolder, "hash-cache.json");
    readonly string _feedFile = Path.Combine(UserSettings.DefaultFolder, "news-cache.json");
    readonly UserSettings _settings;
    readonly ServerSettings? _server;
    static readonly HttpClient Web = NewWebClient();
    Manifest? _manifest;
    IReadOnlyList<CatalogAddon> _catalog = [];
    // filled from background work and read by the window, so it has to be safe for both
    readonly ConcurrentDictionary<string, string> _latestCommits = new(StringComparer.OrdinalIgnoreCase);
    CatalogAddon? _previewed;
    int _pendingFiles;
    CancellationTokenSource? _work;
    readonly Dictionary<string, Control> _tweakControls = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Group, Control Row)> _gatedTweakRows = [];   // rows shown only while an option (DXVK...) is on
    CheckBox? _laaBox;
    LauncherPolicy _policy = new();   // the server's rules, from the last manifest
    readonly List<string> _serverTweaks = [];   // Config.wtf values the server sets that the Tweaks list doesn't have

    static HttpClient NewWebClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherInfo.UserAgent);
        return http;
    }

    public MainWindow()
    {
        InitializeComponent();
        Logger.Written += ShowLogLine;
        _settings = UserSettings.Load(_settingsFile);
        if (_settings.InstallId.Length == 0) _settings.InstallId = Guid.NewGuid().ToString("N");

        try
        {
            _server = ServerSettings.Load(Path.Combine(AppContext.BaseDirectory, "launcher.env"));
            ServerText.Text = _server.ServerUrl.Host;
            ServerText.IsVisible = _server.ShowIp;
            AccountText.IsVisible = _server.ShowUsername;
            // the server's websites (register page, database...) as links in the header
            foreach (NavLink nav in _server.Nav)
            {
                Button link = new() { Content = nav.Name, Classes = { "link" }, FontSize = 12, Padding = new Thickness(6, 0, 0, 0) };
                SetNavTip(link, nav);
                link.Click += (_, _) => OpenUrl(nav.Url.ToString());
                NavLinks.Children.Add(link);
            }
            // the same links under the login (a new player needs Register before they have a login)
            foreach (NavLink nav in _server.Nav)
            {
                Button link = new() { Content = nav.Name, Classes = { "link" }, FontSize = 12 };
                SetNavTip(link, nav);
                link.Click += (_, _) => OpenUrl(nav.Url.ToString());
                LoginLinks.Children.Add(link);
            }
        }
        catch (Exception e)
        {
            Logger.Warning("No server settings", e);
            ServerText.Text = e.Message;
            UpdateButton.IsEnabled = ClientButton.IsEnabled = false;
        }

        UsernameBox.Text = _settings.Username;
        RememberBox.IsChecked = _settings.RememberPassword;
        if (_settings.RememberPassword)
            PasswordBox.Text = CredentialStore.Load();
        RememberBox.IsEnabled = CredentialStore.IsSupported;
        RememberBox.Content = CredentialStore.IsSupported ? $"Remember password ({CredentialStore.StoreName})" : "Remember password (no password store on this system)";
        WowFolderBox.Text = _settings.WowFolder;
        WowFolderBox.TextChanged += (_, _) => { ShowPlayState(); LoadTweaks(); ShowLocales(_manifest?.Locales ?? []); };
        AddonsBox.IsChecked = _settings.InstallAddons;
        ClearCacheBox.IsChecked = _settings.ClearCacheOnPlay;
        ReopenBox.IsChecked = _settings.ReopenAfterGame;
        RememberWindowBox.IsChecked = _settings.RememberWindow;
        BackupBox.IsChecked = _settings.BackupBeforeUpdate;
        AccountText.Text = _settings.Username;
        ShowLocales(_settings.Locales);
        ApplyBackground();
        if (_settings.RememberWindow && _settings.WindowX is double x && _settings.WindowY is double y
            && double.IsFinite(x) && double.IsFinite(y) && _settings.WindowWidth > 100 && _settings.WindowHeight > 100)
        {
            Position = new PixelPoint((int)x, (int)y);
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }
        ShowTagLegend();
        ShowFeeds(FeedCache.Load(_feedFile));   // the last news and changelog, until the server sends new ones
        FillLookBoxes();
        BuildTweaks();
        RefreshInstalledAddons();
        RefreshBackups();
        ShowPlayState();
        // closing must never fail: anything that goes wrong while saving is logged and the window still closes
        Closing += (_, e) =>
        {
            // "When closing the window: keep running in the tray" (unless the launcher is really quitting)
            if (!_quitting && _settings.OnClose == "tray" && HideToTray()) { e.Cancel = true; return; }
            SaveSettings();
            _work?.Cancel();
            _tray?.Dispose();
        };
        FillWindowSettings();
        if (_settings.StartPage == "last" && _settings.LastPage is > 0 and <= 5)
            OnTab(TabButton(_settings.LastPage), new RoutedEventArgs());
        _autoUpdateTimer.Tick += async (_, _) => await AutoUpdateAsync();
        _autoUpdateTimer.Start();
        Closed += (_, _) => { Logger.Written -= ShowLogLine; Logger.Info("Launcher closed", toWindow: false); };
        Opened += async (_, _) =>
        {
            LauncherVersionText.Text = $"Temucraft Launcher {LauncherInfo.Version}";
            ShareSettings(Folder);   // the game may have been played with the launcher closed
            ShowPrivacy();
            // with a login, connect right away: news, addons and options show even before a game is installed
            // (nothing is downloaded until Update)
            if (_server is null)
                Status("launcher.env is missing next to the launcher: get it from the server's owner.");
            else if (HaveLogin && Agreed)
            {
                await CheckAsync(download: false);
                await AutoUpdateAsync();
            }
            else
                AskForLogin();
        };
    }

    // ------------------------------------------------------------------------------------------- basics
    static void Log(string line) => Logger.Info(line);

    const int LogBoxLines = 400;
    readonly Queue<string> _logLines = new();

    /// <summary>Shows a log line in the Settings page's log box (the last <see cref="LogBoxLines"/> lines).</summary>
    void ShowLogLine(LogEntry entry)
    {
        string text = $"[{entry.Time:HH:mm:ss}] " + (entry.Level >= LogLevel.Warning ? entry.Level.ToString().ToUpperInvariant() + ": " : "") + entry.Message;
        Dispatcher.UIThread.Post(() =>
        {
            _logLines.Enqueue(text);
            while (_logLines.Count > LogBoxLines) _logLines.Dequeue();
            LogBox.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }

    /// <summary>Progress reported from background work, run on the window's thread (controls only work there).</summary>
    sealed class UiProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => Dispatcher.UIThread.Post(() => report(value));
    }

    void Status(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);

    string Folder => WowFolderBox.Text?.Trim() ?? "";

    /// <summary>Saves the settings and the password; never throws (a failure is logged and shown).</summary>
    bool SaveSettings()
    {
        try
        {
            SaveSettingsOrThrow();
            return true;
        }
        catch (Exception e)
        {
            Logger.Error("Couldn't save the settings", e);
            StatusText.Text = $"Couldn't save the settings: {e.Message}";
            return false;
        }
    }

    void SaveSettingsOrThrow()
    {
        _settings.Username = UsernameBox.Text?.Trim() ?? "";
        _settings.RememberPassword = RememberBox.IsChecked == true;
        _settings.WowFolder = Folder;
        _settings.InstallAddons = AddonsBox.IsChecked == true;
        _settings.ClearCacheOnPlay = ClearCacheBox.IsChecked == true;
        _settings.ReopenAfterGame = ReopenBox.IsChecked == true;
        _settings.RememberWindow = RememberWindowBox.IsChecked == true;
        _settings.BackupBeforeUpdate = BackupBox.IsChecked == true;
        _settings.Locales = SelectedLocales();
        if (_settings.RememberWindow && WindowState == WindowState.Normal)
        {
            _settings.WindowX = Position.X; _settings.WindowY = Position.Y;
            _settings.WindowWidth = Width; _settings.WindowHeight = Height;
        }
        _settings.Save(_settingsFile);
        AccountText.Text = _settings.Username;

        if (_settings.RememberPassword && !string.IsNullOrEmpty(PasswordBox.Text))
            CredentialStore.Save(_settings.Username, PasswordBox.Text);   // throws if Windows refuses: caught above
        else if (!_settings.RememberPassword)
            CredentialStore.Delete();
    }

    void OnTab(object? sender, RoutedEventArgs e)
    {
        int page = int.Parse((string)((Button)sender!).Tag!);
        Control[] pages = [NewsPage, TweaksPage, AddonsPage, ModsPage, SettingsPage, LauncherPage];
        Button[] tabs = [NewsTab, TweaksTab, AddonsTab, ModsTab, SettingsTab, LauncherTab];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].IsVisible = i == page;
            tabs[i].Classes.Set("selected", i == page);
        }
        if (page == 1) LoadTweaks();
        if (page == 2) RefreshInstalledAddons();
        if (page == 3) RefreshMods();
        if (page == 4) RefreshBackups();
        _settings.LastPage = page;
    }

    Button TabButton(int page) => new[] { NewsTab, TweaksTab, AddonsTab, ModsTab, SettingsTab, LauncherTab }[Math.Clamp(page, 0, 5)];

    void ApplyBackground()
    {
        try
        {
            BackgroundImage.Source = File.Exists(_settings.Background) ? new Bitmap(_settings.Background) : null;
        }
        catch (Exception)
        {
            BackgroundImage.Source = null;
        }
    }

    void ShowLocales(IEnumerable<string> available)
    {
        List<string> chosen = _settings.Locales.Count > 0 ? _settings.Locales : GameFolder.InstalledLocales(Folder).ToList();
        Dictionary<string, string> locked = LockedLocales(Folder);
        LocalePanel.Children.Clear();
        foreach (string locale in available.Concat(chosen).Concat(locked.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool must = locked.TryGetValue(locale, out string? why);
            // the code with its size next to it ("deDE  1.9 GB"); the code is kept in Tag
            StackPanel label = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(new TextBlock { Text = locale });
            if (_manifest is { } sized && InstallSizes.Locale(sized, locale) is > 0 and var localeBytes)
                label.Children.Add(new TextBlock { Text = Words.Size(localeBytes), Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            CheckBox box = new()
            {
                Content = label,
                Tag = locale,
                Margin = new Thickness(0, 0, 16, 4),
                IsChecked = must || chosen.Contains(locale, StringComparer.OrdinalIgnoreCase),
                IsEnabled = !must,
            };
            // hover: the language's name (in the launcher's language and its own), and why it's locked
            ToolTip.SetTip(box, GameFolder.LocaleName(locale) + (must ? "\n" + Translation.T(why!) : ""));
            LocalePanel.Children.Add(box);
        }
    }

    /// <summary>Languages that are always installed, with why: the game's own, the server's default, and any the
    /// server requires.</summary>
    Dictionary<string, string> LockedLocales(string folder)
    {
        Dictionary<string, string> locked = new(StringComparer.OrdinalIgnoreCase);
        foreach (string l in _policy.RequiredLocales) locked[l] = "Everyone gets this one (set by the server)";
        locked[_policy.DefaultLocale] = "The server's default language (its game files are in it)";
        if (GameFolder.ClientLocale(folder) is { } own) locked[own] = "Your game's language";
        return locked;
    }

    List<string> SelectedLocales() => LocalePanel.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true).Select(box => box.Tag as string ?? box.Content?.ToString() ?? "").Where(l => l.Length > 0).ToList();

    void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = ClientButton.IsEnabled = !busy && _server is not null;
        PlayButton.IsEnabled = PreviewInstall.IsEnabled = !busy;
        CancelButton.IsVisible = busy;
        if (!busy) Progress.Value = 0;
    }

    bool Busy => _work is not null;

    /// <summary>
    /// Runs one job in the background (only one at a time). The work must not touch any control: read what it needs
    /// before calling this, and report back with Status, Log, <see cref="UiProgress{T}"/> or Dispatcher.UIThread.
    /// </summary>
    async Task<bool> RunAsync(string what, Func<CancellationToken, Task> work)
    {
        if (Busy)
        {
            Status($"Wait for {_job} to finish (or press Cancel).");
            return false;
        }
        SaveSettings();
        SetBusy(true);
        CancellationTokenSource cts = new();
        _work = cts;
        _job = what;
        Logger.Info($"{what}: started", toWindow: false);
        try
        {
            await Task.Run(() => work(cts.Token));
            Logger.Info($"{what}: done", toWindow: false);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status("Cancelled.");
            Log($"{what}: cancelled");
        }
        catch (LoginFailedException e)
        {
            Logger.Warning($"{what}: {e.Message}");
            ShowLogin(e.Message);   // wrong password, or an address the server doesn't know: ask again, saying why
        }
        catch (Exception e)
        {
            Status(e.Message);
            Logger.Error(what, e);
        }
        finally
        {
            _work = null;
            _job = "";
            cts.Dispose();
            SetBusy(false);
        }
        return false;
    }

    string _job = "";

    bool NeedFolder()
    {
        if (GameFolder.LooksLikeClient(Folder)) return false;
        Status(Folder.Length == 0
            ? "Pick your WoW folder under Game files, or an empty folder to install the game into."
            : "There's no game in your WoW folder yet: press INSTALL to download it there.");
        return true;
    }

    /// <summary>A server connection with the login typed in the window (call on the window's thread).</summary>
    ServerClient NewClient() => new(_server!, UsernameBox.Text?.Trim() ?? "", PasswordBox.Text ?? "",
        clientInfo: Agreed ? ClientInfo.Read(_server!.Collect, _settings.InstallId) : null);

    /// <summary>The player agreed to everything this server collects (at the login window); otherwise nothing is sent.</summary>
    bool Agreed => _server is not null && _settings.CollectAgreedServer == _server.ServerUrl.ToString()
                   && _settings.CollectAgreedNote == _server.CollectNote
                   && _server.Collect.All(k => _settings.CollectAgreed.Contains(k, StringComparer.OrdinalIgnoreCase));

    /// <summary>The login window's notice, short enough to fit beside the login: each item this server collects,
    /// tagged (Default) or (Enabled in &lt;server&gt;), the owner's note, and that logging in is agreeing.</summary>
    string CollectText()
    {
        static string Short(string key) => Translation.T(key switch
        {
            "launcherVersion" => "Launcher version", "os" => "Operating system, version and build",
            "installId" => "A random ID for this install", "computerName" => "Computer name", "osUser" => "Operating system username",
            "mac" => "MAC addresses", "localIp" => "Local IP addresses (IPv4 and IPv6)", "timezone" => "Time zone",
            "language" => "System language", "hardware" => "Processor and memory", _ => ClientInfo.Describe(key),
        });
        IReadOnlyList<string> collect = _server?.Collect ?? ClientInfo.Normalize(null);
        string enabled = _server is { Name.Length: > 0 } ? string.Format(Translation.T("Enabled in {0}"), _server.Name) : Translation.T("Enabled by this server");
        System.Text.StringBuilder text = new();
        if (collect.Count == 0)
            text.Append(Translation.T("This server collects nothing from your PC besides your login."));
        else
        {
            text.Append(Translation.T("This server collects:"));
            foreach (string key in collect)
                text.Append($"\n  • {Short(key)}  ({(ClientInfo.Defaults.Contains(key) ? Translation.T("Default") : enabled)})");
            bool everything = ClientInfo.Optional.All(i => collect.Contains(i.Key));
            text.Append("\n" + (everything ? Translation.T("Other servers may collect less: only the defaults, or nothing.")
                                   : Translation.T("Other servers may also ask for:") + " " + string.Join(", ",
                                         ClientInfo.Optional.Where(i => !collect.Contains(i.Key)).Select(i => Short(i.Key).ToLower())) + "."));
        }
        if (_server?.CollectNote.Length > 0) text.Append("\n\n" + _server.CollectNote);
        text.Append("\n\n" + Translation.T("Any server also sees your public IP address (IPv4 or IPv6) and records whether it let you in."));
        if (collect.Count > 0)
            text.Append(" " + Translation.T("By logging in, you agree to this being sent with every request the launcher makes; you can read it again on the Launcher page."));
        return text.ToString();
    }

    /// <summary>Every player has their own login for the game files (and the server only lets in known addresses).</summary>
    bool HaveLogin => _server is not null && !string.IsNullOrWhiteSpace(UsernameBox.Text) && !string.IsNullOrEmpty(PasswordBox.Text);

    /// <summary>No login yet (or the server refused it): the login window, like a website's login prompt.</summary>
    void AskForLogin(string? error = null) => ShowLogin(error);

    void ShowLogin(string? error)
    {
        LoginUser.Text = UsernameBox.Text;
        LoginPass.Text = error is null ? PasswordBox.Text : "";
        LoginRemember.IsChecked = RememberBox.IsChecked;
        LoginRemember.IsEnabled = CredentialStore.IsSupported;
        ToolTip.SetTip(LoginRemember, CredentialStore.IsSupported ? $"Saved in {CredentialStore.StoreName}, never in a file" : "This system has no password store the launcher can use");
        CollectNotice.Text = CollectText();
        LoginError.Text = error ?? "";
        LoginError.IsVisible = error is not null;
        LoginButton.IsEnabled = true;
        LoginOverlay.IsVisible = true;
        (string.IsNullOrWhiteSpace(LoginUser.Text) ? LoginUser : LoginPass).Focus();
        StatusText.Text = "Log in to get the game files.";
    }

    async void OnLogin(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LoginUser.Text) || string.IsNullOrEmpty(LoginPass.Text))
        {
            LoginError.Text = "Enter your username and password.";
            LoginError.IsVisible = true;
            return;
        }
        UsernameBox.Text = LoginUser.Text.Trim();
        PasswordBox.Text = LoginPass.Text;
        // logging in is agreeing to what this server collects (shown in the window); a longer list asks again
        if (_server is not null)
        {
            _settings.CollectAgreed = [.. _server.Collect];
            _settings.CollectAgreedServer = _server.ServerUrl.ToString();
            _settings.CollectAgreedNote = _server.CollectNote;
            _settings.CollectAgreedAt = DateTime.Now;
            Logger.Info($"Agreed to send: {(_server.Collect.Count == 0 ? "nothing" : string.Join(", ", _server.Collect))}", toWindow: false);
        }
        ShowPrivacy();
        RememberBox.IsChecked = LoginRemember.IsChecked;
        LoginButton.IsEnabled = false;
        LoginError.Text = "Logging in…";
        LoginError.IsVisible = true;
        LoginOverlay.IsVisible = false;   // a refused login brings it back with the reason
        SaveSettings();
        await CheckAsync(download: false);
    }

    /// <summary>The Launcher page: what this server collects and the exact values sent from this PC.</summary>
    void ShowPrivacy()
    {
        // the same notice as the login window, then the exact values from this PC (in a code box) and when they agreed
        PrivacyText.Text = CollectText();
        bool any = _server is { Collect.Count: > 0 };
        PrivacyValuesTitle.IsVisible = PrivacyValuesBox.IsVisible = any;
        if (any)
        {
            Dictionary<string, string> values = ClientInfo.Read(_server!.Collect, _settings.InstallId);
            // a title, then the value in a code box (one adapter per line)
            PrivacyValuesBox.Children.Clear();
            PrivacyValuesBox.RowDefinitions.Clear();
            int row = 0;
            foreach (string key in _server.Collect)
            {
                PrivacyValuesBox.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                TextBlock title = new() { Text = ClientInfo.Title(key), Classes = { "soft" }, FontSize = 12,
                                          VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
                Grid.SetRow(title, row);
                PrivacyValuesBox.Children.Add(title);
                Border code = new()
                {
                    Classes = { "field" },
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 3), Margin = new Thickness(0, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new SelectableTextBlock
                    {
                        Text = values.GetValueOrDefault(key, "(not available)").Replace(ClientInfo.AdapterSeparator, "\n"),
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12,
                        Classes = { "text" }, TextWrapping = TextWrapping.Wrap,
                    },
                };
                Grid.SetRow(code, row);
                Grid.SetColumn(code, 2);
                PrivacyValuesBox.Children.Add(code);
                row++;
            }
        }
        PrivacyAgreed.Text = Agreed && _settings.CollectAgreedAt is DateTime when
            ? $"You agreed on {when:dd-MM-yyyy HH:mm:ss}. If the server changes what it collects or its note, you'll be asked again."
            : "You haven't agreed yet: nothing is sent until you log in.";
    }

    /// <summary>Copies what this PC sends, as "Title: value" lines, e.g. to paste into a message to the server's owner.</summary>
    // ------------------------------------------------------------------------------------------- copy / import settings
    static readonly (string Key, string Name)[] LauncherSwitches =
        [("installAddons", "AddonsBox"), ("clearCache", "ClearCacheBox"), ("reopenAfterGame", "ReopenBox"),
         ("rememberWindow", "RememberWindowBox"), ("backupBeforeUpdate", "BackupBox")];

    CheckBox Switch(string name) => this.FindControl<CheckBox>(name)!;

    /// <summary>The Tweaks boxes and the launcher's own choices as text (no login, folder or install ID).</summary>
    string SettingsAsText()
    {
        List<KeyValuePair<string, string>> tweaks = [];
        foreach (Tweak t in Tweaks.All)
            if (BoxValue(t, _tweakControls.GetValueOrDefault(t.CVar)) is { } value) tweaks.Add(new(t.CVar, value));
        foreach (string cvar in _serverTweaks)
            if (_tweakControls.GetValueOrDefault(cvar) is TextBox { Text: { Length: > 0 } text }) tweaks.Add(new(cvar, text.Trim()));
        if (_laaBox is not null) tweaks.Add(new("largeAddressAware", _laaBox.IsChecked == true ? "1" : "0"));
        List<KeyValuePair<string, string>> launcher =
        [
            new("theme", Look.Theme), new("mode", Look.Mode), new("language", _settings.Language is { Length: > 0 } l ? l : "auto"),
            .. LauncherSwitches.Select(s => new KeyValuePair<string, string>(s.Key, Switch(s.Name).IsChecked == true ? "1" : "0")),
            new("backupsToKeep", _settings.BackupsToKeep.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("locales", string.Join(",", SelectedLocales())),
            .. _settings.Options.OrderBy(o => o.Key).Select(o => new KeyValuePair<string, string>("options." + o.Key, o.Value)),
        ];
        return SettingsText.Write(tweaks, launcher);
    }

    async void OnCopySettings(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(SettingsAsText());
            Status("Settings copied: paste them in a message, or press Import settings on the other PC.");
        }
        catch (Exception error) { Logger.Warning("Couldn't copy to the clipboard", error); }
    }

    /// <summary>Fills in settings from the clipboard. Settings the server enforces stay as the server says; the tweaks
    /// are filled in and still need Save (so they can be looked at first).</summary>
    async void OnImportSettings(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text;
        try { text = await clipboard.GetTextAsync(); }
        catch (Exception error) { Logger.Warning("Couldn't read the clipboard", error); return; }
        if (SettingsText.Parse(text) is not var (tweaks, launcher))
        {
            Status("The clipboard has no launcher settings: copy them with Copy settings first.");
            return;
        }
        int count = 0;
        // tweaks: into the boxes (enforced ones are locked again right after)
        _fillingTweaks = true;
        try
        {
            foreach ((string cvar, string value) in tweaks)
            {
                if (cvar.Equals("largeAddressAware", StringComparison.OrdinalIgnoreCase))
                {
                    if (_laaBox is { IsEnabled: true }) { _laaBox.IsChecked = value == "1"; count++; }
                    continue;
                }
                if (!_tweakControls.TryGetValue(cvar, out Control? control) || !control.IsEnabled) continue;
                SetTweak(cvar, value);
                count++;
            }
            LockEnforcedTweaks();
        }
        finally { _fillingTweaks = false; }
        _settings.TweakPresetChosen = true;
        MarkPreset(MatchingPreset());

        // the launcher's own choices
        foreach ((string key, string name) in LauncherSwitches)
            if (launcher.TryGetValue(key, out string? on) && Switch(name) is { IsEnabled: true } box) { box.IsChecked = on == "1"; count++; }
        if (launcher.TryGetValue("backupsToKeep", out string? keep) && int.TryParse(keep, out int k) && k > 0 && _policy.Setting("backupsToKeep") is not { Enforce: true })
        { _settings.BackupsToKeep = k; count++; }
        foreach ((string key, string value) in launcher.Where(l => l.Key.StartsWith("options.", StringComparison.OrdinalIgnoreCase)))
        {
            string group = key["options.".Length..];
            if (_manifest?.OptionGroups.FirstOrDefault(g => g.Name.Equals(group, StringComparison.OrdinalIgnoreCase)) is { Enforce: true }) continue;
            _settings.Options[group] = value;
            count++;
        }
        if (launcher.TryGetValue("locales", out string? locales))
        {
            _settings.Locales = locales.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length == 4).ToList();
            ShowLocales(_manifest?.Locales ?? []);
            count++;
        }
        if (launcher.TryGetValue("theme", out string? theme)) _settings.Theme = theme;
        if (launcher.TryGetValue("mode", out string? mode)) _settings.ThemeMode = mode;
        if (launcher.TryGetValue("language", out string? language) && language.Length > 0 && language != _settings.Language)
        {
            _settings.Language = language;
            Look.UseLanguage(_settings.Language, _server?.Language);
            LanguageNote.IsVisible = true;
        }
        Look.UseTheme(_settings);
        ShowTagLegend();
        FillLookBoxes();
        RefreshMods();
        ShowGatedTweaks();
        SaveSettings();
        _pendingFiles = Math.Max(_pendingFiles, launcher.Keys.Any(k => k.StartsWith("options.") || k == "locales") ? 1 : 0);
        ShowPlayState();
        Status($"{count} setting(s) imported. Press Save on the Game settings page to write the game settings.");
    }

    async void OnCopyValues(object? sender, RoutedEventArgs e)
    {
        if (_server is not { Collect.Count: > 0 } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        Dictionary<string, string> values = ClientInfo.Read(_server.Collect, _settings.InstallId);
        string text = string.Join(Environment.NewLine, _server.Collect.Select(k =>
            $"{ClientInfo.Title(k)}: {values.GetValueOrDefault(k, "(not available)").Replace(ClientInfo.AdapterSeparator, "; ")}"));
        try
        {
            await clipboard.SetTextAsync(text);
            CopyValuesButton.Content = "Copied";
            await Task.Delay(1500);
            CopyValuesButton.Content = "Copy to clipboard";
        }
        catch (Exception error) { Logger.Warning("Couldn't copy to the clipboard", error); }
    }

    void OnLoginLater(object? sender, RoutedEventArgs e)
    {
        LoginOverlay.IsVisible = false;
        StatusText.Text = "Not logged in: you can play, but updates need your login (Launcher page, or press UPDATE).";
    }

    /// <summary>What to install, read from the window (call on the window's thread).</summary>
    InstallChoice Choice() =>
        new(Folder, SelectedLocales(), AddonsBox.IsChecked == true, Options: new Dictionary<string, string>(_settings.Options));

    // ------------------------------------------------------------------------------------------- server data
    async Task<Manifest> FetchManifestAsync(ServerClient client, CancellationToken token)
    {
        Status("Connecting…");
        Manifest manifest = await client.GetManifestAsync(token);
        _manifest = manifest;
        Log($"Server lists {manifest.Files.Count} files");

        bool launcherOk = await SelfCheck.MatchesAsync(manifest, token);
        string? newsText = await client.GetTextAsync("news.json", token);
        string? changelogText = await client.GetTextAsync("changelog.json", token);
        string? catalog = await client.GetTextAsync("addons.json", token);
        _catalog = catalog is null ? [] : AddonFolder.ParseCatalog(catalog);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyPolicy(manifest);
            ShowLocales(manifest.Locales);
            ClientButton.IsVisible = manifest.Files.Any(f => f.Category == FileCategory.Base);
            RefreshMods();
            ShowGatedTweaks();
            VersionText.Text = manifest.ClientVersion.Length > 0 ? $"Version: {manifest.ClientVersion}" : "";
            ShowLauncherUpdate(!launcherOk && manifest.Launcher is not null);
            // shown, and kept for the next start (before logging in, or when the server can't be reached)
            FeedCache feeds = new() { News = newsText ?? "", Changelog = changelogText ?? "", NewsRule = manifest.Rules.News,
                                      ChangelogRule = manifest.Rules.Changelog, Saved = DateTime.Now };
            ShowFeeds(feeds);
            feeds.Save(_feedFile);
            RefreshCatalog();
            RefreshUntrackedButton();
        });
        if (!launcherOk) Log("Self-check: this launcher differs from the server's copy");
        return manifest;
    }

    // ------------------------------------------------------------------------------------------- downloads, updates, window
    bool _quitting;      // closing for real (not to the tray)
    bool _gameRunning;   // automatic updates wait while the game runs
    bool _fillingWindowSettings;
    TrayIcon? _tray;
    readonly DispatcherTimer _autoUpdateTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    bool _windowSettingsHooked;

    /// <summary>The Downloads, Updates and Window boxes from the player's settings.</summary>
    void FillWindowSettings()
    {
        if (!_windowSettingsHooked)
        {
            _windowSettingsHooked = true;
            LimitBox.IsCheckedChanged += OnDownloadSettingChanged;
            BitsBox.IsCheckedChanged += OnDownloadSettingChanged;
            LimitNumber.ValueChanged += OnDownloadSettingChanged;
            AutoUpdateBox.IsCheckedChanged += OnUpdateSettingChanged;
            HoursBox.IsCheckedChanged += OnUpdateSettingChanged;
            FromBox.SelectionChanged += OnUpdateSettingChanged;
            ToBox.SelectionChanged += OnUpdateSettingChanged;
            GameLaunchBox.SelectionChanged += OnWindowSettingChanged;
            CloseBox.SelectionChanged += OnWindowSettingChanged;
            StartPageBox.SelectionChanged += OnWindowSettingChanged;
            ShareBox.IsCheckedChanged += OnShareSettingChanged;
            ShareAddonsBox.IsCheckedChanged += OnShareSettingChanged;
            ShareCharsBox.IsCheckedChanged += OnShareSettingChanged;
            InstallFolderBox.TextChanged += (_, _) => { if (InstallOverlay.IsVisible && _manifest is { } m) ShowInstallTotal(m); };
        }
        _fillingWindowSettings = true;
        try
        {
            LimitBox.IsChecked = _settings.LimitDownloads;
            BitsBox.IsChecked = _settings.SpeedInBits;
            ShowLimitNumber();
            ShareBox.IsChecked = _settings.ShareAccounts;
            ShareAddonsBox.IsChecked = _settings.ShareAddonSettings;
            ShareCharsBox.IsChecked = _settings.ShareCharacters;
            ShareAddonsBox.IsEnabled = ShareCharsBox.IsEnabled = _settings.ShareAccounts;
            AutoUpdateBox.IsChecked = _settings.AutoUpdate;
            HoursBox.IsChecked = _settings.AutoUpdateHoursOnly;
            List<LookChoice> hours = [.. Enumerable.Range(0, 24).Select(h => new LookChoice(h.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{h:00}:00"))];
            FromBox.ItemsSource = hours;
            ToBox.ItemsSource = hours.ToList();
            FromBox.SelectedIndex = Math.Clamp(_settings.AutoUpdateFrom, 0, 23);
            ToBox.SelectedIndex = Math.Clamp(_settings.AutoUpdateTo, 0, 23);
            ShowHoursNote();
            List<LookChoice> launch =
            [
                new("keep", Translation.T("Keep the launcher open")), new("minimize", Translation.T("Minimize the launcher")),
                new("hide", Translation.T("Hide the launcher to the system tray")), new("exit", Translation.T("Close the launcher completely")),
            ];
            GameLaunchBox.ItemsSource = launch;
            GameLaunchBox.SelectedItem = launch.FirstOrDefault(c => c.Value == _settings.OnGameLaunch) ?? launch[2];
            List<LookChoice> close = [new("tray", Translation.T("Keep running in the system tray")), new("exit", Translation.T("Close the launcher completely"))];
            CloseBox.ItemsSource = close;
            CloseBox.SelectedItem = close.FirstOrDefault(c => c.Value == _settings.OnClose) ?? close[1];
            List<LookChoice> start = [new("news", Translation.T("News")), new("last", Translation.T("The last page I used"))];
            StartPageBox.ItemsSource = start;
            StartPageBox.SelectedItem = start.FirstOrDefault(c => c.Value == _settings.StartPage) ?? start[0];
        }
        finally { _fillingWindowSettings = false; }
    }

    /// <summary>The limit box in KB/s, or in kbit/s with speeds in bits; never above the server's cap.</summary>
    void ShowLimitNumber()
    {
        bool bits = _settings.SpeedInBits;
        long cap = _policy.DownloadLimitKBps;
        LimitUnit.Text = bits ? "kbit/s" : "KB/s";
        // kbit/s are 1000 bits, KB/s 1024 bytes: 1 KB/s = 8.192 kbit/s
        LimitNumber.Maximum = cap > 0 ? Math.Round(cap * (bits ? KbitPerKB : 1)) : 10_000_000;
        LimitNumber.Value = Math.Round(Math.Min(_settings.DownloadLimitKBps, cap > 0 ? cap : long.MaxValue) * (bits ? KbitPerKB : 1));
        LimitNumber.IsEnabled = _settings.LimitDownloads;
        ShowLimitEquivalent();
        ShowSpeedTestText();
        ServerCapNote.IsVisible = cap > 0;
        ServerCapNote.Text = cap > 0 ? string.Format(Translation.T("This server caps downloads at {0}."), SpeedLimit.Format(cap * 1024, bits)) : "";
    }

    const decimal KbitPerKB = 8.192m;

    /// <summary>The limit in bigger units next to the box: "= 3.9 MB/s" (or "= 31.3 Mbit/s").</summary>
    void ShowLimitEquivalent()
    {
        long kb = LimitNumber.Value is decimal v && v > 0 ? (long)Math.Round(v / (_settings.SpeedInBits ? KbitPerKB : 1)) : 0;
        LimitEquivalent.Text = kb >= 1024 ? "= " + SpeedLimit.Format(kb * 1024, _settings.SpeedInBits) : "";
    }

    void ApplySpeedLimit()
    {
        long kb = SpeedLimit.EffectiveKBps(_settings.LimitDownloads, _settings.DownloadLimitKBps, _policy.DownloadLimitKBps);
        SpeedLimit.Set(kb);
        if (kb > 0) Log($"Download speed limit: {kb} KB/s");
    }

    void OnDownloadSettingChanged(object? sender, EventArgs e)
    {
        if (_fillingWindowSettings) return;
        _settings.LimitDownloads = LimitBox.IsChecked == true;
        bool bitsBefore = _settings.SpeedInBits;
        _settings.SpeedInBits = BitsBox.IsChecked == true;
        if (sender == LimitNumber && LimitNumber.Value is decimal v && v > 0)
            _settings.DownloadLimitKBps = (long)Math.Max(1, Math.Round(v / (_settings.SpeedInBits ? KbitPerKB : 1)));
        _fillingWindowSettings = true;
        try
        {
            if (sender != LimitNumber || bitsBefore != _settings.SpeedInBits) ShowLimitNumber();
            else { LimitNumber.IsEnabled = _settings.LimitDownloads; ShowLimitEquivalent(); }
        }
        finally { _fillingWindowSettings = false; }
        ApplySpeedLimit();   // also for a download that is running now
        SaveSettings();
    }

    void ShowHoursNote()
    {
        int from = _settings.AutoUpdateFrom, to = _settings.AutoUpdateTo;
        int span = ((to - from) % 24 + 24) % 24;
        HoursNote.Text = span == 0 ? Translation.T("(all day)") : string.Format(Translation.T("({0} hours)"), span);
        FromBox.IsEnabled = ToBox.IsEnabled = HoursBox.IsChecked == true;
        HoursBox.IsEnabled = AutoUpdateBox.IsChecked == true;
    }

    void OnUpdateSettingChanged(object? sender, EventArgs e)
    {
        if (_fillingWindowSettings) return;
        _settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
        _settings.AutoUpdateHoursOnly = HoursBox.IsChecked == true;
        if (FromBox.SelectedIndex >= 0) _settings.AutoUpdateFrom = FromBox.SelectedIndex;
        if (ToBox.SelectedIndex >= 0) _settings.AutoUpdateTo = ToBox.SelectedIndex;
        ShowHoursNote();
        SaveSettings();
    }

    // ------------------------------------------------------------------------------------------- shared settings
    void OnShareSettingChanged(object? sender, EventArgs e)
    {
        if (_fillingWindowSettings) return;
        _settings.ShareAccounts = ShareBox.IsChecked == true;
        _settings.ShareAddonSettings = ShareAddonsBox.IsChecked == true;
        _settings.ShareCharacters = ShareCharsBox.IsChecked == true;
        ShareAddonsBox.IsEnabled = ShareCharsBox.IsEnabled = _settings.ShareAccounts;
        SaveSettings();
    }

    /// <summary>Copies the newest settings, key bindings and macros to the other accounts (and characters); quiet
    /// unless something changed. Backs up WTF first.</summary>
    void ShareSettings(string folder, bool force = false)
    {
        if ((!_settings.ShareAccounts && !force) || !GameFolder.LooksLikeClient(folder)) return;
        try
        {
            SharedSettings.Result result = SharedSettings.Sync(folder, _settings.ShareAddonSettings, _settings.ShareCharacters,
                () => Backups.Create(folder, "before-sharing-settings", includeAddons: false, keep: _settings.BackupsToKeep));
            if (result.Copied > 0)
            {
                Log($"Shared settings: {Words.Files(result.Copied)} copied between {result.Accounts.Count} account(s): {string.Join(", ", result.Files.Take(10))}");
                Status(string.Format(Translation.T("Settings shared: {0} brought up to date."), Words.Files(result.Copied)));
            }
            else if (force) Status(Translation.T("Settings are already the same everywhere."));
        }
        catch (Exception error)
        {
            Logger.Warning("Couldn't share the settings between accounts (is the game running?)", error);
            if (force) Status(error.Message);
        }
    }

    void OnShareNow(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        ShareSettings(Folder, force: true);
        RefreshBackups();
    }

    // ------------------------------------------------------------------------------------------- speed test
    long _suggestedKBps;
    double _measuredBytesPerSecond;

    /// <summary>The speed test's result in the units chosen now (bits or bytes); empty before a test.</summary>
    void ShowSpeedTestText()
    {
        if (_measuredBytesPerSecond <= 0) return;
        SpeedTestText.Text = string.Format(Translation.T("Your speed to this server: {0}. Suggested limit: {1} (leaves room for games, calls and video)."),
            SpeedLimit.Format(_measuredBytesPerSecond, _settings.SpeedInBits), SpeedLimit.Format(_suggestedKBps * 1024, _settings.SpeedInBits));
    }

    /// <summary>Downloads part of the server's biggest file for a few seconds (no limit) and suggests a limit that
    /// leaves room for everything else on the connection.</summary>
    async void OnSpeedTest(object? sender, RoutedEventArgs e)
    {
        if (_server is null || Busy) return;
        if (!HaveLogin || !Agreed) { AskForLogin(); return; }
        ServerClient client = NewClient();
        Manifest? manifest = _manifest;
        double bytesPerSecond = 0;
        SpeedTestButton.IsEnabled = false;
        SpeedTestText.Text = Translation.T("Testing…");
        bool done = await RunAsync("Speed test", async token =>
        {
            using ServerClient _ = client;
            manifest ??= await client.GetManifestAsync(token);
            ManifestFile? big = manifest.Files.Where(f => f.Size > 0).MaxBy(f => f.Size);
            if (big is null) throw new InvalidOperationException("The server has no file to test with.");
            bytesPerSecond = await client.SpeedTestAsync(big, TimeSpan.FromSeconds(8), 200L * 1024 * 1024, token);
        });
        SpeedTestButton.IsEnabled = true;
        if (!done || bytesPerSecond <= 0) { SpeedTestText.Text = ""; return; }
        _suggestedKBps = SpeedLimit.Suggest(bytesPerSecond, _policy.DownloadLimitKBps);
        _measuredBytesPerSecond = bytesPerSecond;
        ShowSpeedTestText();
        UseSuggestedButton.IsVisible = true;
        Log($"Speed test: {SpeedLimit.Format(bytesPerSecond, false)} ({SpeedLimit.Format(bytesPerSecond, true)}); suggested limit {_suggestedKBps} KB/s");
    }

    void OnUseSuggestedLimit(object? sender, RoutedEventArgs e)
    {
        if (_suggestedKBps <= 0) return;
        _settings.LimitDownloads = true;
        _settings.DownloadLimitKBps = _suggestedKBps;
        FillWindowSettings();
        ApplySpeedLimit();
        SaveSettings();
        Status(Translation.T("The suggested limit is on. Turn off Limit download speed for full speed again."));
    }

    /// <summary>"Update by itself": when there is something new, within the chosen hours, never while the game runs or
    /// another job is busy, and only with a login.</summary>
    async Task AutoUpdateAsync()
    {
        if (!_settings.AutoUpdate || _gameRunning || Busy || _server is null || !HaveLogin || !Agreed || NoGameYet) return;
        if (_settings.AutoUpdateHoursOnly && !UpdateHours.Inside(DateTime.Now.Hour, _settings.AutoUpdateFrom, _settings.AutoUpdateTo)) return;
        if (_pendingFiles == 0) await CheckAsync(download: false);
        if (_pendingFiles > 0 && !Busy && !_gameRunning)
        {
            Log("Updating by itself (automatic updates are on)");
            await CheckAsync(download: true);
        }
    }

    void OnWindowSettingChanged(object? sender, EventArgs e)
    {
        if (_fillingWindowSettings) return;
        if (GameLaunchBox.SelectedItem is LookChoice launch) _settings.OnGameLaunch = launch.Value;
        if (CloseBox.SelectedItem is LookChoice close) _settings.OnClose = close.Value;
        if (StartPageBox.SelectedItem is LookChoice start) _settings.StartPage = start.Value;
        SaveSettings();
    }

    /// <summary>Hides the window, with an icon in the system tray to bring it back (or quit). False where there is no tray.</summary>
    bool HideToTray()
    {
        try
        {
            if (_tray is null)
            {
                NativeMenu menu = [];
                NativeMenuItem open = new(Translation.T("Open the launcher")), quit = new(Translation.T("Quit"));
                open.Click += (_, _) => BringBack();
                quit.Click += (_, _) => { _quitting = true; BringBack(); Close(); };
                menu.Add(open);
                menu.Add(quit);
                _tray = new TrayIcon { Icon = Icon, ToolTipText = "Temucraft Launcher", Menu = menu };
                _tray.Clicked += (_, _) => BringBack();
                TrayIcon.SetIcons(Application.Current!, [_tray]);
            }
            _tray.IsVisible = true;
            Hide();
            return true;
        }
        catch (Exception e)
        {
            Logger.Warning("No system tray here; minimizing instead", e);
            WindowState = WindowState.Minimized;
            return false;
        }
    }

    void BringBack()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_tray is not null) _tray.IsVisible = false;
    }

    /// <summary>Forgets which files were checked (the hash cache) and removes unfinished downloads (.part files).</summary>
    void OnClearDownloadCache(object? sender, RoutedEventArgs e)
    {
        if (Busy) { Status(string.Format(Translation.T("Wait for {0} to finish (or press Cancel)."), _job)); return; }
        int removed = 0;
        try
        {
            if (File.Exists(_cacheFile)) { File.Delete(_cacheFile); removed++; }
            if (GameFolder.LooksLikeClient(Folder) || Directory.Exists(Folder))
                foreach (string part in Directory.EnumerateFiles(Folder, "*.part", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    try { File.Delete(part); removed++; } catch (IOException) { }
                }
            SelfCheck.CleanUp();
            Log($"Download cache cleared ({removed} file(s) removed)");
            Status(Translation.T("Download cache cleared. The next update checks every file again."));
        }
        catch (Exception error)
        {
            Logger.Warning("Couldn't clear the download cache", error);
            Status(error.Message);
        }
    }

    /// <summary>A shortcut to the launcher on the desktop (Windows: .lnk, Linux: .desktop).</summary>
    void OnDesktopShortcut(object? sender, RoutedEventArgs e)
    {
        string? exe = SelfCheck.CurrentExe;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (exe is null || desktop.Length == 0) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string link = Path.Combine(desktop, "Temucraft Launcher.lnk");
                string script = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:TC_LINK);$s.TargetPath=$env:TC_EXE;" +
                                "$s.WorkingDirectory=$env:TC_DIR;$s.IconLocation=$env:TC_EXE;$s.Save()";
                ProcessStartInfo start = new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
                    { CreateNoWindow = true, UseShellExecute = false };
                start.Environment["TC_LINK"] = link;
                start.Environment["TC_EXE"] = exe;
                start.Environment["TC_DIR"] = AppContext.BaseDirectory;
                using Process? p = Process.Start(start);
                p?.WaitForExit(15000);
                if (!File.Exists(link)) throw new IOException("Windows didn't make the shortcut.");
            }
            else if (OperatingSystem.IsLinux())
                File.WriteAllText(Path.Combine(desktop, "temucraft-launcher.desktop"),
                    $"[Desktop Entry]\nType=Application\nName=Temucraft Launcher\nExec=\"{exe}\"\nPath={AppContext.BaseDirectory}\nTerminal=false\n");
            else throw new PlatformNotSupportedException("Desktop shortcuts are made on Windows and Linux only.");
            Status(Translation.T("Shortcut made on your desktop."));
        }
        catch (Exception error)
        {
            Logger.Warning("Couldn't make the desktop shortcut", error);
            Status(error.Message);
        }
    }

    // ------------------------------------------------------------------------------------------- launcher updates
    bool _launcherLater;   // "Later" pressed: no banner again until the next start

    /// <summary>The server hands out a different launcher (normally a newer one): a banner that recommends updating,
    /// with what changed (the changelog's newest "Launcher ..." entry). Not forced: "Later" hides it until the next start.</summary>
    void ShowLauncherUpdate(bool differs)
    {
        WarningBanner.IsVisible = differs && !_launcherLater;
        LauncherUpdateButtons.IsVisible = differs;
        if (!differs) { WhatsNewPanel.IsVisible = false; return; }
        WarningText.Text = Translation.T("A new launcher is available. Updating is strongly recommended: it takes a few seconds and keeps your settings.");
        NewsItem? news = null;
        try { news = SelfCheck.WhatsNew(NewsItem.Parse(FeedCache.Load(_feedFile).Changelog)); }
        catch (System.Text.Json.JsonException) { }
        WhatsNewButton.IsVisible = news is not null;
        if (news is not null) ShowNews(WhatsNewList, [news], new FeedRule(true, 1, 1));
    }

    void OnWhatsNew(object? sender, RoutedEventArgs e) => WhatsNewPanel.IsVisible = !WhatsNewPanel.IsVisible;

    void OnLauncherLater(object? sender, RoutedEventArgs e)
    {
        _launcherLater = true;
        WarningBanner.IsVisible = false;
        Log("Launcher update put off until the next start");
    }

    async void OnUpdateLauncher(object? sender, RoutedEventArgs e)
    {
        if (_server is null || _manifest is null || Busy) return;
        Manifest manifest = _manifest;
        string? exe = null;
        ServerClient client = NewClient();   // reads the login boxes: on the window's thread
        bool done = await RunAsync("Launcher update", async token =>
        {
            using ServerClient _ = client;
            Status("Downloading the new launcher…");
            exe = await SelfCheck.UpdateAsync(client, manifest, new UiProgress<(long Done, long Total)>(p =>
                Progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total), token);
        });
        if (!done || exe is null) return;
        Log("Launcher updated; starting the new one");
        SaveSettings();
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            _quitting = true;
            Close();
        }
        catch (Exception error)
        {
            Logger.Error("Couldn't start the new launcher", error);
            Status(Translation.T("The new launcher is ready: close this one and open it again."));
        }
    }

    /// <summary>
    /// The server's enforced settings: set and greyed out, so the player can't change them. Settings the server
    /// doesn't enforce stay the player's choice.
    /// </summary>
    void ApplyPolicy(Manifest manifest)
    {
        (string Key, CheckBox Box)[] settings =
        [
            ("installAddons", AddonsBox), ("clearCache", ClearCacheBox), ("reopenAfterGame", ReopenBox),
            ("rememberWindow", RememberWindowBox), ("backupBeforeUpdate", BackupBox),
        ];
        _policy = manifest.Rules;
        // the server's default theme (or the one it enforces), kept for the next start
        Themes.ServerLook look = Themes.ServerLook.From(_policy);
        if (look != _settings.ServerLook)
        {
            _settings.ServerLook = look;
            SaveSettings();
            Look.UseTheme(_settings);
            ShowTagLegend();
            FillLookBoxes();
        }
        foreach ((string key, CheckBox box) in settings)
        {
            bool? enforced = _policy.EnforcedBool(key);
            box.IsEnabled = enforced is null;
            if (enforced is bool value) box.IsChecked = value;
            ToolTip.SetTip(box, enforced is null ? null : "Set by the server");
        }
        // no box for these: the server's value is used whether it's enforced or only recommended
        if (_policy.Setting("backupsToKeep")?.Int is int keep and > 0) _settings.BackupsToKeep = keep;
        foreach (string unknown in _policy.Unknown)
            Logger.Warning($"launcher-policy.json: \"{unknown}\" isn't a setting this launcher knows; skipped");
        SaveSettings();
        BuildTweaks();
    }

    PolicyRule? TweakRule(string cvar) => _policy.Tweaks.GetValueOrDefault(cvar);

    /// <summary>The changelog's tag colours, as a legend under its title (the server's tag names, untranslated).</summary>
    void ShowTagLegend()
    {
        TagLegend.Inlines ??= [];
        TagLegend.Inlines.Clear();
        using IDisposable serverText = Look.Untranslated();
        foreach ((string tag, string color) in NewsItem.Tags)
        {
            TagLegend.Inlines.Add(new Avalonia.Controls.Documents.Run($"[{tag}]") { Foreground = new SolidColorBrush(Look.Readable(color)), FontWeight = FontWeight.SemiBold });
            TagLegend.Inlines.Add(new Avalonia.Controls.Documents.Run("  "));
        }
    }

    // ------------------------------------------------------------------------------------------- language and theme
    sealed record LookChoice(string Value, string Text) { public override string ToString() => Text; }
    bool _fillingLook;

    /// <summary>The Language, Theme and Mode boxes on the Launcher page (greyed out when the server enforces its look).</summary>
    void FillLookBoxes()
    {
        _fillingLook = true;
        try
        {
            List<LookChoice> languages = [new("", Translation.T("Automatic (the computer's language)")),
                                      .. Look.Languages.Select(l => new LookChoice(l.Code, $"{l.Name} ({l.Code})"))];
            LanguageBox.ItemsSource = languages;
            string pickedLanguage = _settings.Language is { Length: > 0 } own ? own : _server?.Language is { Length: > 0 } env && env != "auto" ? env : "";
            // a set language shows as the one in use (LANGUAGE=en shows English); auto shows Automatic
            LanguageBox.SelectedItem = pickedLanguage is "" or "auto" ? languages[0]
                : languages.FirstOrDefault(c => c.Value.Equals(Translation.Code, StringComparison.OrdinalIgnoreCase)) ?? languages[0];

            List<LookChoice> themes = [.. Look.ThemeList.Select(t => new LookChoice(t.Id, t.Name))];
            ThemeBox.ItemsSource = themes;
            ThemeBox.SelectedItem = themes.FirstOrDefault(c => c.Value == Look.Theme);
            List<LookChoice> modes = [new("dark", Translation.T("Dark")), new("light", Translation.T("Light"))];
            ModeBox.ItemsSource = modes;
            ModeBox.SelectedItem = modes.FirstOrDefault(c => c.Value == Look.Mode);
            Themes.ServerLook server = _settings.ServerLook ?? new();
            ThemeBox.IsEnabled = !server.ThemeEnforced;
            ModeBox.IsEnabled = !server.ModeEnforced;
            ToolTip.SetTip(ThemeBox, server.ThemeEnforced ? Translation.T("Set by the server") : null);
            ToolTip.SetTip(ModeBox, server.ModeEnforced ? Translation.T("Set by the server") : null);
        }
        finally { _fillingLook = false; }
    }

    void OnLanguagePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingLook || LanguageBox.SelectedItem is not LookChoice choice) return;
        _settings.Language = choice.Value.Length > 0 ? choice.Value : "auto";
        SaveSettings();
        Look.UseLanguage(_settings.Language, _server?.Language);
        LanguageNote.IsVisible = true;
    }

    void OnThemePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingLook) return;
        if (ThemeBox.SelectedItem is LookChoice theme) _settings.Theme = theme.Value;
        if (ModeBox.SelectedItem is LookChoice mode) _settings.ThemeMode = mode.Value;
        SaveSettings();
        Look.UseTheme(_settings);
        ShowTagLegend();
        ShowFeeds(FeedCache.Load(_feedFile));   // the coloured parts of the news follow the mode
    }

    /// <summary>A line of news text with its [Tags] and {+better}/{-worse} parts in colour.</summary>
    static TextBlock Rich(string line, double size = 14)
    {
        TextBlock block = new() { TextWrapping = TextWrapping.Wrap, FontSize = size, Classes = { "text" } };
        block.Inlines ??= [];
        foreach ((string part, string? color) in NewsItem.Segments(line))
            block.Inlines.Add(color is null ? new Avalonia.Controls.Documents.Run(part)
                : new Avalonia.Controls.Documents.Run(part) { Foreground = new SolidColorBrush(Look.Readable(color)), FontWeight = FontWeight.SemiBold });
        return block;
    }

    /// <summary>News and changelog as the server wants them: which panels show, how many entries, "Load more".</summary>
    void ShowFeeds(FeedCache feeds)
    {
        bool news = feeds.NewsRule.Show, changelog = feeds.ChangelogRule.Show;
        NewsPanel.IsVisible = news;
        ChangelogPanel.IsVisible = changelog;
        // one panel shown: it takes the whole page
        Grid.SetColumn(ChangelogPanel, news ? 2 : 0);
        Grid.SetColumnSpan(NewsPanel, changelog ? 1 : 3);
        Grid.SetColumnSpan(ChangelogPanel, news ? 1 : 3);
        NewsTab.IsVisible = news || changelog;
        IReadOnlyList<NewsItem> newsItems = [], changelogItems = [];
        try { newsItems = NewsItem.Parse(feeds.News); changelogItems = NewsItem.Parse(feeds.Changelog); }
        catch (System.Text.Json.JsonException e) { Logger.Warning("The news or changelog isn't valid JSON", e); }
        ShowNews(NewsList, newsItems, feeds.NewsRule);
        ShowNews(ChangelogList, changelogItems, feeds.ChangelogRule);
    }

    static void ShowNews(StackPanel list, IReadOnlyList<NewsItem> items, FeedRule? rule = null, int? shown = null)
    {
        rule ??= FeedRule.Default;
        int count = Math.Min(items.Count, shown ?? rule.Entries);
        list.Children.Clear();
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = "Nothing yet.", Classes = { "dim" } });
        using IDisposable serverText = Look.Untranslated();   // the server's words stay as it wrote them
        IReadOnlyList<string> dates = NewsItem.DateLines(items);
        IReadOnlyList<(int Number, int Of)> numbers = NewsItem.DayNumbers(items);
        for (int i = 0; i < count; i++)
        {
            NewsItem item = items[i];
            StackPanel card = new() { Spacing = 4 };
            if (dates[i].Length > 0) card.Children.Add(new TextBlock { Text = dates[i], Classes = { "dim" }, FontSize = 12 });
            if (item.Title.Length > 0)
            {
                // the title, then where it sits in its day ("1 of 3") when there are several that day
                TextBlock title = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, Classes = { "accent" }, TextWrapping = TextWrapping.Wrap };
                title.Inlines!.Add(new Avalonia.Controls.Documents.Run(item.Title));
                if (numbers[i].Of > 1)
                    title.Inlines.Add(new Avalonia.Controls.Documents.Run("   " + string.Format(Translation.T("{0} of {1} that day"), numbers[i].Number, numbers[i].Of))
                        { FontSize = 12, FontWeight = FontWeight.Normal, Foreground = (IBrush?)Application.Current!.FindResource("T.dim") });
                card.Children.Add(title);
            }
            // each line, with [Tags] and {+better}/{-worse} in colour; a line with details folds them under it
            StackPanel body = new() { Spacing = 1 };
            foreach ((string line, IReadOnlyList<string> details) in NewsItem.Lines(item.Text))
            {
                if (details.Count == 0)
                {
                    // a bullet line: the bullet in the same narrow column as the folding arrows, so the text lines up
                    bool bullet = line.StartsWith("• ") || line.StartsWith("- ");
                    if (!bullet) { body.Children.Add(Rich(line)); continue; }
                    Grid plain = new() { ColumnDefinitions = new ColumnDefinitions("14,*") };
                    plain.Children.Add(new TextBlock { Text = "•", Classes = { "text" }, HorizontalAlignment = HorizontalAlignment.Center });
                    TextBlock rest = Rich(line[2..]);
                    Grid.SetColumn(rest, 1);
                    plain.Children.Add(rest);
                    body.Children.Add(plain);
                    continue;
                }
                StackPanel inner = new() { Spacing = 1, Margin = new Thickness(14, 2, 0, 4) };
                foreach (string detail in details) inner.Children.Add(Rich("• " + detail, 13));
                TextBlock arrow = new() { Text = "▸", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
                Grid head = new() { ColumnDefinitions = new ColumnDefinitions("14,*"), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent };
                head.Children.Add(arrow);
                // the arrow takes the bullet's place
                string headLine = line.StartsWith("• ") ? line[2..] : line.StartsWith("- ") ? line[2..] : line;
                TextBlock text = Rich(headLine + $"  ({details.Count})");
                Grid.SetColumn(text, 1);
                head.Children.Add(text);
                inner.IsVisible = false;
                ToolTip.SetTip(head, Translation.T("Click to show or hide the details"));
                head.PointerPressed += (_, _) => { inner.IsVisible = !inner.IsVisible; arrow.Text = inner.IsVisible ? "▾" : "▸"; };
                body.Children.Add(head);
                body.Children.Add(inner);
            }
            card.Children.Add(body);
            if (Uri.TryCreate(item.Link, UriKind.Absolute, out Uri? link) && link.Scheme is "https" or "http")
            {
                Button open = new() { Content = Translation.T("Read more"), Classes = { "link" } };
                open.Click += (_, _) => OpenUrl(link.ToString());
                card.Children.Add(open);
            }
            list.Children.Add(card);
        }
        if (rule.MoreLabel(items.Count - count) is { } label)
        {
            Button more = new() { Content = Translation.T(label), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            more.Click += (_, _) => ShowNews(list, items, rule, rule.NextCount(count, items.Count));
            list.Children.Add(more);
        }
    }

    async Task<IReadOnlyList<PlannedFile>> PlanAsync(Manifest manifest, InstallChoice choice, CancellationToken token)
    {
        IReadOnlyList<PlannedFile> wanted = UpdatePlanner.Wanted(manifest, choice);
        HashCache cache = HashCache.Load(_cacheFile);
        try { return await UpdatePlanner.OutdatedAsync(wanted, cache, new Progress<string>(Status), token); }
        finally { cache.Save(); }
    }

    /// <summary>Reads the manifest and either only reports what is out of date or installs it.</summary>
    async Task CheckAsync(bool download)
    {
        if (_server is null || Busy) return;
        if (!HaveLogin || !Agreed) { AskForLogin(); return; }
        bool haveGame = GameFolder.LooksLikeClient(Folder);
        if (download && NoGameYet)
        {
            if (await InstallGameFilesAsync(Folder)) await CheckAsync(download: true);
            return;
        }
        if (download && NeedFolder()) return;
        // everything the work needs from the window, read here on the window's thread
        InstallChoice choice = Choice();
        string folder = choice.WowFolder;
        ServerClient client = NewClient();
        await RunAsync(download ? "Update" : "Check", async token =>
        {
            using ServerClient _ = client;
            Manifest manifest = await FetchManifestAsync(client, token);
            if (manifest.Enforced("installAddons") is bool addonsEnforced) choice = choice with { Addons = addonsEnforced };
            // the game's own language, the server's default and any it requires are always installed
            IEnumerable<string> always = manifest.Rules.RequiredLocales.Append(manifest.Rules.DefaultLocale);
            if (GameFolder.ClientLocale(folder) is { } own) always = always.Append(own);
            choice = choice with { Locales = choice.Locales.Concat(always).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            if (manifest.Enforced("backupBeforeUpdate") is bool backupEnforced) _settings.BackupBeforeUpdate = backupEnforced;
            if (!haveGame)
            {
                // connected, but no game yet: the tabs are filled in; the game files come from Install game files…
                Status(folder.Length > 0   // the value read before the job: the folder box only works on the window's thread
                    ? "Connected. There's no game in your WoW folder yet: press INSTALL to download it there."
                    : "Connected. Pick your WoW folder under Game files, or an empty folder to install the game into.");
                await Dispatcher.UIThread.InvokeAsync(ShowPlayState);
                await Dispatcher.UIThread.InvokeAsync(RefreshMods);
                return;
            }
            IReadOnlyList<PlannedFile> outdated = await PlanAsync(manifest, choice, token);
            List<CatalogAddon> required = _catalog.Where(a => a.Required && choice.Addons).ToList();
            await RefreshCommitsAsync(required, token);
            List<CatalogAddon> addonUpdates = required.Where(a => NeedsInstall(a, folder)).ToList();
            Logger.Info($"{Words.Files(outdated.Count)} and {Words.Addons(addonUpdates.Count)} out of date", toWindow: false);

            if (!download)
            {
                long size = outdated.Sum(p => p.File.Size);
                int pending = outdated.Count + addonUpdates.Count;
                Status(pending == 0 ? "Everything up to date!"
                    : string.Format(Translation.T("Update available! {0}, {1} to download."), Words.Files(pending), Words.Size(size)));
                await Dispatcher.UIThread.InvokeAsync(() => { _pendingFiles = pending; ShowPlayState(); });
                return;
            }

            if (_settings.BackupBeforeUpdate && (outdated.Count > 0 || addonUpdates.Count > 0))
            {
                Status("Backing up your settings and addons…");
                Log($"Backup: {Path.GetFileName(Backups.Create(folder, "before-update", keep: _settings.BackupsToKeep))}");
            }
            foreach (string removed in GameFolder.RemoveObsolete(folder, manifest.Obsolete))
                Log($"Removed {removed} (no longer on the server)");
            foreach (string removed in GameFolder.RemoveObsolete(folder, UpdatePlanner.Unchosen(manifest, choice)))
                Log($"Removed {removed} (option not picked)");
            await InstallAsync(client, outdated, folder, token);
            foreach (CatalogAddon addon in addonUpdates) await InstallCatalogAsync(addon, folder, token);
            GameFolder.WriteRealmlist(folder, manifest.Realmlist);
            ApplyEnforcedToGame(folder);
            Status("Everything up to date!");
            await Dispatcher.UIThread.InvokeAsync(() => { _pendingFiles = 0; ShowPlayState(); RefreshInstalledAddons(); RefreshCatalog(); });
        });
    }

    /// <summary>After an update: the server's enforced Config.wtf values and Large Address Aware (runs in the background).</summary>
    void ApplyEnforcedToGame(string folder)
    {
        try
        {
            ConfigWtf config = ConfigWtf.Load(folder);
            int changed = _policy.ApplyEnforcedTweaks(config);
            if (changed > 0) { config.Save(); Log($"{changed} game setting(s) set by the server written to WTF\\Config.wtf"); }
            DxvkConf dxvk = DxvkConf.Load(folder);
            int dxvkChanged = _policy.ApplyEnforcedTweaks(dxvk, "dxvk");
            if (dxvkChanged > 0) { dxvk.Save(); Log($"{dxvkChanged} DXVK setting(s) set by the server written to dxvk.conf"); }
            if (_policy.EnforcedBool("largeAddressAware") is bool laa && File.Exists(GameFolder.ExePath(folder))
                && LargeAddressAware.IsEnabled(GameFolder.ExePath(folder)) != laa)
            {
                LargeAddressAware.Set(GameFolder.ExePath(folder), laa);
                Log($"Large Address Aware turned {(laa ? "on" : "off")} (set by the server)");
            }
        }
        catch (Exception e) { Logger.Warning("Couldn't apply the server's game settings (is the game running?)", e); }
    }

    void ShowPlayState()
    {
        bool install = (NoGameYet || Folder.Length == 0) && _server is not null;
        PlayButton.Content = install ? "INSTALL" : _pendingFiles > 0 ? "UPDATE" : "PLAY";
        PlayButton.Classes.Set("update", install || _pendingFiles > 0);
    }

    async Task InstallAsync(ServerClient client, IReadOnlyList<PlannedFile> outdated, string? wowFolder, CancellationToken token)
    {
        HashCache cache = HashCache.Load(_cacheFile);
        try
        {
            long total = outdated.Sum(p => p.File.Size);
            Log(outdated.Count == 0 ? "All server files are up to date" : $"{Words.Files(outdated.Count)} to install, {Words.Size(total)}");
            ApplySpeedLimit();
            SpeedMeter meter = new();
            int Index(PlannedFile planned) => outdated is List<PlannedFile> list ? list.IndexOf(planned) + 1 : outdated.ToList().IndexOf(planned) + 1;
            UpdateInstaller.Result result = await UpdateInstaller.InstallAsync(client, outdated, cache, wowFolder,
                onFile: (planned, before, copy) => Dispatcher.UIThread.Post(() =>
                {
                    Progress.Value = total == 0 ? 1 : (double)before / total;
                    StatusText.Text = string.Format(Translation.T(copy ? "Copying {0} ({1}/{2}) (already on your disk)" : "Downloading {0} ({1}/{2})"),
                                                    planned.File.Path, Index(planned), outdated.Count);
                }),
                progress: (planned, before) => new UiProgress<(long Done, long Total)>(p =>
                {
                    long done = before + p.Done;
                    Progress.Value = total == 0 ? 1 : (double)done / total;
                    // this file and the whole update, the speed and the time left
                    double speed = meter.Add(done);
                    string rate = speed > 0 ? $"  ·  {SpeedLimit.Format(speed, _settings.SpeedInBits)}" : "";
                    string left = SpeedLimit.TimeLeft(total - done, speed);
                    StatusText.Text = string.Format(Translation.T("Downloading {0} ({1}/{2})"), planned.File.Path, Index(planned), outdated.Count)
                                      + $"  {Words.ShortSize(p.Done)} / {Words.ShortSize(p.Total)}{rate}" + (left.Length > 0 ? $"  ·  {left}" : "");
                    ToolTip.SetTip(Progress, $"{Words.ShortSize(done)} / {Words.Size(total)}");
                }),
                token: token);
            if (result.Copied > 0)
                Log($"{Words.Files(result.Copied)} ({Words.Size(result.BytesCopied)}) copied from your own disk instead of downloaded");
        }
        finally
        {
            cache.Save();
        }
    }

    async void OnUpdate(object? sender, RoutedEventArgs e) => await CheckAsync(download: true);

    async void OnConnect(object? sender, RoutedEventArgs e) => await CheckAsync(download: false);

    async void OnVerify(object? sender, RoutedEventArgs e)
    {
        if (Busy) { Status($"Wait for {_job} to finish (or press Cancel)."); return; }
        // forget every remembered hash so each file is read again
        try { if (File.Exists(_cacheFile)) File.Delete(_cacheFile); }
        catch (IOException error) { Logger.Warning("Couldn't clear the hash cache", error); }
        Log("Verify: re-checking every file");
        await CheckAsync(download: true);
    }

    async void OnDownloadClient(object? sender, RoutedEventArgs e) => await OpenInstallAsync();

    // ------------------------------------------------------------------------------------------- install window
    readonly HashSet<string> _installLocales = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _installOptions = [];
    bool _installAddons = true;

    static string DefaultInstallFolder() => OperatingSystem.IsWindows()
        ? @"C:\Games\Temucraft"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Temucraft");

    /// <summary>The install window: where to install, which languages, options and addons, and how big each part
    /// and the whole install is (the game and the server's required files included), against the free space.</summary>
    async Task OpenInstallAsync()
    {
        if (_server is null || Busy) return;
        if (!HaveLogin || !Agreed) { AskForLogin(); return; }
        if (_manifest is null) await CheckAsync(download: false);
        if (_manifest is not { } manifest) return;
        if (!manifest.Files.Any(f => f.Category == FileCategory.Base))
        {
            Status(Translation.T("This server doesn't hand out the game itself: pick your WoW 3.3.5a folder under Game files."));
            return;
        }
        InstallFolderBox.Text = Folder.Length > 0 && !GameFolder.LooksLikeClient(Folder) ? Folder : DefaultInstallFolder();
        _installLocales.Clear();
        foreach (string l in SelectedLocales().Concat(LockedLocales(Folder).Keys)) _installLocales.Add(l);
        _installOptions.Clear();
        foreach (OptionGroup g in manifest.OptionGroups)
            if (UpdatePlanner.ChosenOption(manifest, _settings.Options, g.Name) is { } chosen) _installOptions[g.Name] = chosen;
        _installAddons = AddonsBox.IsChecked == true;
        BuildInstallParts(manifest);
        InstallOverlay.IsVisible = true;
    }

    Grid PartRow(Control left, long bytes, bool strong = false)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
        row.Children.Add(left);
        TextBlock size = new() { Text = Words.Size(bytes), VerticalAlignment = VerticalAlignment.Center, Classes = { strong ? "text" : "soft" } };
        Grid.SetColumn(size, 1);
        row.Children.Add(size);
        return row;
    }

    void BuildInstallParts(Manifest manifest)
    {
        InstallParts.Children.Clear();
        InstallParts.Children.Add(PartRow(new TextBlock { Text = Translation.T("World of Warcraft 3.3.5a (the game)") }, InstallSizes.Of(manifest, FileCategory.Base), true));
        InstallParts.Children.Add(PartRow(new TextBlock { Text = string.Format(Translation.T("{0} files (needed)"), _server?.Name is { Length: > 0 } n ? n : "Server") },
                                          InstallSizes.Of(manifest, FileCategory.Patch), true));

        Dictionary<string, string> locked = LockedLocales(Folder);
        if (manifest.Locales.Count > 0)
        {
            InstallParts.Children.Add(new TextBlock { Text = Translation.T("LANGUAGES"), Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) });
            foreach (string locale in manifest.Locales)
            {
                bool must = locked.ContainsKey(locale);
                CheckBox box = new() { Content = $"{locale}  ·  {GameFolder.LocaleName(locale)}", IsChecked = must || _installLocales.Contains(locale), IsEnabled = !must };
                if (must) ToolTip.SetTip(box, Translation.T(locked[locale]));
                string code = locale;
                box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) _installLocales.Add(code); else _installLocales.Remove(code); ShowInstallTotal(manifest); };
                InstallParts.Children.Add(PartRow(box, InstallSizes.Locale(manifest, locale)));
            }
        }

        if (manifest.OptionGroups.Count > 0)
        {
            InstallParts.Children.Add(new TextBlock { Text = Translation.T("OPTIONS"), Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) });
            foreach (OptionGroup group in manifest.OptionGroups)
            {
                bool onOff = group.Options.SequenceEqual(["On", "Off"], StringComparer.OrdinalIgnoreCase);
                string current = _installOptions.GetValueOrDefault(group.Name, "");
                if (onOff || (group.Optional && group.Options.Count == 1))
                {
                    string on = group.Options[0], off = onOff ? group.Options[1] : "";
                    CheckBox box;
                    using (Look.Untranslated()) box = new CheckBox { Content = group.Name };
                    box.IsChecked = current.Equals(on, StringComparison.OrdinalIgnoreCase);
                    box.IsEnabled = !group.Enforce;
                    ToolTip.SetTip(box, group.Description.Length > 0 ? group.Description : null);
                    box.IsCheckedChanged += (_, _) => { _installOptions[group.Name] = box.IsChecked == true ? on : off; ShowInstallTotal(manifest); };
                    InstallParts.Children.Add(PartRow(box, InstallSizes.Option(manifest, group.Name, on)));
                }
                else
                {
                    // a real choice: the group's name, then each option with its size
                    using (Look.Untranslated()) InstallParts.Children.Add(new TextBlock { Text = group.Name, Margin = new Thickness(0, 4, 0, 0) });
                    List<LookChoice> items = [.. (group.Optional ? new[] { new LookChoice("", Translation.T("(none)")) } : []),
                                              .. group.Options.Select(o => new LookChoice(o, $"{o}  ({Words.ShortSize(InstallSizes.Option(manifest, group.Name, o))})"))];
                    ComboBox combo = new() { ItemsSource = items, MinWidth = 260, IsEnabled = !group.Enforce };
                    combo.SelectedItem = items.FirstOrDefault(c => c.Value.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? items[0];
                    Grid row = PartRow(combo, InstallSizes.Option(manifest, group.Name, current));
                    combo.SelectionChanged += (_, _) =>
                    {
                        string picked = (combo.SelectedItem as LookChoice)?.Value ?? "";
                        _installOptions[group.Name] = picked;
                        if (row.Children[1] is TextBlock size) size.Text = Words.Size(InstallSizes.Option(manifest, group.Name, picked));
                        ShowInstallTotal(manifest);
                    };
                    InstallParts.Children.Add(row);
                }
            }
        }

        long addons = InstallSizes.Of(manifest, FileCategory.Addon);
        if (addons > 0)
        {
            InstallParts.Children.Add(new TextBlock { Text = Translation.T("ADDONS"), Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) });
            CheckBox box = new() { Content = Translation.T("Install the addons the server needs"), IsChecked = _installAddons, IsEnabled = AddonsBox.IsEnabled };
            box.IsCheckedChanged += (_, _) => { _installAddons = box.IsChecked == true; ShowInstallTotal(manifest); };
            InstallParts.Children.Add(PartRow(box, addons));
        }
        ShowInstallTotal(manifest);
    }

    InstallChoice InstallPlan(string folder) =>
        new(folder, _installLocales.ToList(), _installAddons, folder, new Dictionary<string, string>(_installOptions));

    void ShowInstallTotal(Manifest manifest)
    {
        string folder = InstallFolderBox.Text?.Trim() ?? "";
        long total = InstallSizes.Total(manifest, InstallPlan(folder.Length > 0 ? folder : DefaultInstallFolder()));
        InstallTotal.Text = Words.Size(total);
        long? free = folder.Length > 0 ? InstallSizes.FreeSpace(folder) : null;
        bool fits = free is null || free.Value >= total;
        InstallFree.Text = free is null ? "" : string.Format(Translation.T(fits ? "Free on that drive: {0}" : "Not enough space: that drive has {0} free"), Words.Size(free.Value));
        InstallFree.Classes.Set("dim", fits);
        InstallFree.Foreground = fits ? null : (IBrush?)this.FindResource("T.bad");
        InstallGoButton.IsEnabled = fits && folder.Length > 0;
    }

    async void OnInstallBrowse(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the game files" });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } folder)
        {
            InstallFolderBox.Text = folder;
            if (_manifest is { } m) ShowInstallTotal(m);
        }
    }

    void OnInstallCancel(object? sender, RoutedEventArgs e) => InstallOverlay.IsVisible = false;

    async void OnInstallGo(object? sender, RoutedEventArgs e)
    {
        string folder = InstallFolderBox.Text?.Trim() ?? "";
        if (folder.Length == 0 || _manifest is null) return;
        if (GameFolder.LooksLikeClient(folder))
        {
            Status(Translation.T("That folder already has the game: it is now your WoW folder."));
            WowFolderBox.Text = folder;
        }
        // the choices become the player's settings, then the game and the server's files are installed
        _settings.Locales = _installLocales.ToList();
        foreach ((string group, string option) in _installOptions) _settings.Options[group] = option;
        if (AddonsBox.IsEnabled) AddonsBox.IsChecked = _installAddons;
        WowFolderBox.Text = folder;
        ShowLocales(_manifest.Locales);
        RefreshMods();
        SaveSettings();
        InstallOverlay.IsVisible = false;
        if (GameFolder.LooksLikeClient(folder) || await InstallGameFilesAsync(folder)) await CheckAsync(download: true);
    }

    /// <summary>Downloads the full client (the server's base files) into a folder, then makes it the WoW folder.</summary>
    async Task<bool> InstallGameFilesAsync(string folder)
    {
        if (Busy) return false;
        if (!HaveLogin) { AskForLogin(); return false; }
        ServerClient client = NewClient();
        bool done = await RunAsync("Game files", async token =>
        {
            using ServerClient _ = client;
            Manifest manifest = await FetchManifestAsync(client, token);
            if (!manifest.Files.Any(f => f.Category == FileCategory.Base))
                throw new InvalidOperationException("Nothing to install.");
            List<PlannedFile> wanted = UpdatePlanner.Wanted(manifest, new InstallChoice(folder, [], false, folder))
                .Where(p => p.File.Category == FileCategory.Base).ToList();
            HashCache cache = HashCache.Load(_cacheFile);
            IReadOnlyList<PlannedFile> outdated;
            try { outdated = await UpdatePlanner.OutdatedAsync(wanted, cache, new Progress<string>(Status), token); }
            finally { cache.Save(); }
            await InstallAsync(client, outdated, folder, token);
            Status("Game files installed; now the Temucraft files…");
        });
        if (done)
        {
            WowFolderBox.Text = folder;
            SaveSettings();
            _pendingFiles = 1;
            ShowPlayState();
            LoadTweaks();
        }
        return done;
    }

    /// <summary>The WoW folder box names a folder without a game (empty or not made yet): offer to install there.</summary>
    bool NoGameYet => Folder.Length > 0 && !GameFolder.LooksLikeClient(Folder);

    // ------------------------------------------------------------------------------------------- play
    async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if ((NoGameYet || Folder.Length == 0) && _server is not null)
        {
            // no game yet: the install window (where, what, how big)
            await OpenInstallAsync();
            return;
        }
        if (NeedFolder()) return;
        if (_pendingFiles > 0 && _server is not null)
        {
            await CheckAsync(download: true);
            return;
        }
        SaveSettings();
        try
        {
            if (ClearCacheBox.IsChecked == true) GameFolder.ClearCache(Folder);
            if (_manifest is not null) GameFolder.WriteRealmlist(Folder, _manifest.Realmlist);
            ShareSettings(Folder);
            Process? game = GameFolder.Start(Folder);
            Log("Started the game");
            if (game is null) return;
            switch (_settings.OnGameLaunch)
            {
                case "exit": _quitting = true; Close(); return;
                case "minimize": WindowState = WindowState.Minimized; break;
                case "hide": HideToTray(); break;
            }
            _gameRunning = true;
            await game.WaitForExitAsync();
            _gameRunning = false;
            Log($"The game closed (exit code {game.ExitCode})");
            ShareSettings(Folder);
            if (_settings.OnGameLaunch is "minimize" or "hide" && ReopenBox.IsChecked == true) BringBack();
        }
        catch (Exception error)
        {
            Status(error.Message);
            Logger.Error("Couldn't start the game", error);
        }
    }

    async void OnBrowseWow(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Your World of Warcraft 3.3.5a folder",
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            WowFolderBox.Text = path;
            SaveSettings();
            LoadTweaks();
            RefreshInstalledAddons();
            ShowPlayState();
            Status(GameFolder.LooksLikeClient(path) ? "Game folder set." : "No game in that folder yet: press INSTALL to download it there.");
        }
    }

    void OnCancel(object? sender, RoutedEventArgs e) => _work?.Cancel();

    /// <summary>The server's hover text for a link, with the address under it in grey (NAV_SHOW_URL).</summary>
    void SetNavTip(Control link, NavLink nav)
    {
        if (nav.Tooltip(_server!.NavShowUrl) is null)
            return;
        TextBlock tip = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        if (nav.Tip.Length > 0)
            tip.Inlines!.Add(new Avalonia.Controls.Documents.Run(nav.Tip));
        if (_server.NavShowUrl)
        {
            if (nav.Tip.Length > 0)
                tip.Inlines!.Add(new Avalonia.Controls.Documents.LineBreak());
            tip.Inlines!.Add(new Avalonia.Controls.Documents.Run(nav.Url.ToString())
                { Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas, Menlo, monospace") });
        }
        ToolTip.SetTip(link, tip);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }
    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenUrl(folder);
    }

    // ------------------------------------------------------------------------------------------- tweaks
    void BuildTweaks()
    {
        TweaksList.Children.Clear();
        _tweakControls.Clear();
        _gatedTweakRows.Clear();
        string? section = null;
        foreach (Tweak t in Tweaks.All)
        {
            if (t.Section != section)
            {
                section = t.Section;
                TextBlock header = new() { Text = section.ToUpperInvariant(), Classes = { "section" } };
                TweaksList.Children.Add(header);
                if (t.Requires is not null) _gatedTweakRows.Add((t.Requires, header));
            }
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("230,150,*"), Margin = new Thickness(0, 3) };
            if (t.Requires is not null) _gatedTweakRows.Add((t.Requires, row));
            row.Children.Add(new TextBlock
            {
                Text = t.Name, VerticalAlignment = VerticalAlignment.Center,
                Classes = { t.Highlighted ? "good" : "text" },
            });
            // numbers: a number box that only takes numbers and keeps them in the setting's range
            Control control = t.Kind == TweakKind.Toggle
                ? new CheckBox()
                : new NumericUpDown
                {
                    Width = 132, Height = 32, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Minimum = (decimal)t.Min, Maximum = (decimal)t.Max, ClipValueToMinMax = true,
                    Increment = t.Max - t.Min <= 4 ? 0.5m : 1m, FormatString = "0.###",
                    ParsingNumberStyle = System.Globalization.NumberStyles.Float,
                };
            if (control is NumericUpDown range) ToolTip.SetTip(range, $"Between {Tweaks.Format(t.Min)} and {Tweaks.Format(t.Max)}");
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            if (control is CheckBox check) check.IsCheckedChanged += (_, _) => TweakEdited();
            else if (control is NumericUpDown number)
                number.ValueChanged += (_, e) =>
                {
                    // never outside the setting's range, however the value got there
                    if (e.NewValue is decimal v && (v < number.Minimum || v > number.Maximum)) { number.Value = Math.Clamp(v, number.Minimum, number.Maximum); return; }
                    TweakEdited();
                };
            TextBlock description = new() { Text = t.Description, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Control info = description;
            if (t.Compare.Length > 0)
            {
                // how other WoW versions do it, under the description (for comparison only)
                StackPanel both = new() { VerticalAlignment = VerticalAlignment.Center };
                both.Children.Add(description);
                both.Children.Add(new TextBlock { Text = t.Compare, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush?)this.FindResource("T.dimmer") });
                info = both;
            }
            Grid.SetColumn(info, 2);
            row.Children.Add(info);
            TweaksList.Children.Add(row);
            _tweakControls[t.CVar] = control;
        }
        _serverTweaks.Clear();
        List<string> extra = _policy.Tweaks.Keys.Where(k => !Tweaks.All.Any(t => t.CVar.Equals(k, StringComparison.OrdinalIgnoreCase))).Order().ToList();
        if (extra.Count > 0)
        {
            TweaksList.Children.Add(new TextBlock { Text = "FROM THE SERVER", Classes = { "section" } });
            foreach (string cvar in extra)
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("230,150,*"), Margin = new Thickness(0, 3) };
                row.Children.Add(new TextBlock { Text = cvar, VerticalAlignment = VerticalAlignment.Center, Classes = { "text" } });
                TextBox box = new() { Width = 132, HorizontalAlignment = HorizontalAlignment.Left };
                Grid.SetColumn(box, 1);
                row.Children.Add(box);
                TextBlock note = new() { Text = "A Config.wtf value the server sets.", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                Grid.SetColumn(note, 2);
                row.Children.Add(note);
                TweaksList.Children.Add(row);
                _tweakControls[cvar] = box;
                _serverTweaks.Add(cvar);
            }
        }
        TweaksList.Children.Add(new TextBlock { Text = "CLIENT", Classes = { "section" } });
        _laaBox = new CheckBox { Content = "Large Address Aware - lets the game use 4 GB of memory instead of 2 GB (Wow.exe is backed up as Wow.exe.bak)" };
        TweaksList.Children.Add(_laaBox);
        ShowGatedTweaks();
        LoadTweaks();
    }

    /// <summary>Whether an Options group (DXVK, Awesome WotLK...) is on. Before the launcher has talked to the server
    /// it goes by the player's last pick, and shows the settings when it can't tell.</summary>
    bool OptionOn(string group)
    {
        string? chosen = _manifest is not null
            ? UpdatePlanner.ChosenOption(_manifest, _settings.Options, group)
            : _settings.Options.TryGetValue(group, out string? picked) ? picked : "On";
        return !string.IsNullOrEmpty(chosen) && !chosen.Equals("Off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Settings of an option (DXVK's, Awesome WotLK's) only show while that option is on.</summary>
    void ShowGatedTweaks()
    {
        foreach ((string group, Control row) in _gatedTweakRows) row.IsVisible = OptionOn(group);
    }

    static Tweak? TweakOf(string cvar) => Tweaks.All.FirstOrDefault(t => t.CVar.Equals(cvar, StringComparison.OrdinalIgnoreCase));

    /// <summary>A box's value as it goes in the file ("1"/"0", or the toggle's own On/Off values).</summary>
    static string? BoxValue(Tweak t, Control? control) => control switch
    {
        CheckBox box => box.IsChecked == true ? t.On : t.Off,
        NumericUpDown { Value: decimal v } => Tweaks.Format(Math.Clamp((double)v, t.Min, t.Max)),
        _ => null,   // an empty number box leaves the game's own value alone
    };

    void LoadTweaks()
    {
        TweaksHint.IsVisible = !GameFolder.LooksLikeClient(Folder);
        if (TweaksHint.IsVisible) return;
        ConfigWtf config = ConfigWtf.Load(Folder);
        DxvkConf dxvk = DxvkConf.Load(Folder);
        _fillingTweaks = true;
        foreach (Tweak t in Tweaks.All)
            SetTweak(t.CVar, (t.File == "dxvk" ? dxvk.Get(t.CVar) : config.Get(t.CVar)) ?? t.Default);
        _fillingTweaks = false;
        foreach (string cvar in _serverTweaks)
            SetTweak(cvar, config.Get(cvar) ?? TweakRule(cvar)?.CVarText ?? "");
        try { if (_laaBox is not null) _laaBox.IsChecked = LargeAddressAware.IsEnabled(GameFolder.ExePath(Folder)); }
        catch (Exception e) { Logger.Warning("Large Address Aware", e); }
        // a fresh game (none of these set yet) starts on the server's recommendation, or the defaults; a game with its
        // own settings keeps them and shows whichever preset they match
        bool fresh = Tweaks.All.Where(t => t.File == "config" && t.Requires is null).All(t => config.Get(t.CVar) is null);
        if (fresh && !_settings.TweakPresetChosen) FillPreset(_policy.Tweaks.Count > 0 ? "server" : "default");
        LockEnforcedTweaks();
        MarkPreset(MatchingPreset());
    }

    // ------------------------------------------------------------------------------------- presets
    bool _fillingTweaks;   // true while the launcher fills the boxes itself (not the player editing)

    /// <summary>The value a preset gives a tweak: the server's recommendation, the launcher's, or its default.</summary>
    string PresetValue(string preset, Tweak t) => preset switch
    {
        "server" => TweakRule(t.CVar)?.CVarText ?? t.Default,
        "recommended" => t.Recommended,
        _ => t.Default,
    };

    void FillPreset(string preset)
    {
        _fillingTweaks = true;
        try
        {
            foreach (Tweak t in Tweaks.All) SetTweak(t.CVar, PresetValue(preset, t));
            foreach (string cvar in _serverTweaks) SetTweak(cvar, preset == "server" ? TweakRule(cvar)?.CVarText ?? "" : "");
            if (_laaBox is not null && preset != "default") _laaBox.IsChecked = _policy.Setting("largeAddressAware")?.Bool ?? true;
            LockEnforcedTweaks();
        }
        finally { _fillingTweaks = false; }
        MarkPreset(preset);
    }

    /// <summary>The preset the boxes match (server first), or "custom".</summary>
    string MatchingPreset()
    {
        string Current(Tweak t) => BoxValue(t, _tweakControls.GetValueOrDefault(t.CVar)) ?? "";
        bool Same(string a, string b) => a == b || (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y) && x == y);
        foreach (string preset in _policy.Tweaks.Count > 0 ? new[] { "server", "recommended", "default" } : ["recommended", "default"])
            if (Tweaks.All.All(t => Same(Current(t), PresetValue(preset, t)))) return preset;
        return "custom";
    }

    /// <summary>Gold border on the active preset; "Custom" when the boxes match none.</summary>
    void MarkPreset(string preset)
    {
        _settings.TweakPreset = preset;
        PresetServer.Classes.Set("active", preset == "server");
        PresetRecommended.Classes.Set("active", preset == "recommended");
        PresetDefault.Classes.Set("active", preset == "default");
        PresetCustom.IsVisible = preset == "custom";
        PresetServer.IsEnabled = _policy.Tweaks.Count > 0;
        ToolTip.SetTip(PresetServer, _policy.Tweaks.Count > 0 ? "The server's values; the rest at the launcher's defaults" : "This server recommends nothing");
    }

    void OnServerTweaks(object? sender, RoutedEventArgs e)
    {
        _settings.TweakPresetChosen = true;
        FillPreset("server");
        Status("The server's recommended values are filled in; press Save.");
    }

    /// <summary>The player changed a box: no preset any more (unless it happens to match one).</summary>
    void TweakEdited()
    {
        if (_fillingTweaks) return;
        _settings.TweakPresetChosen = true;
        MarkPreset(MatchingPreset());
    }

    /// <summary>Tweaks the server enforces: shown with the server's value and greyed out.</summary>
    void LockEnforcedTweaks()
    {
        foreach ((string cvar, Control control) in _tweakControls)
        {
            PolicyRule? rule = TweakRule(cvar);
            bool locked = rule is { Enforce: true };
            control.IsEnabled = !locked;
            ToolTip.SetTip(control, locked ? "Set by the server" : rule is not null ? $"The server recommends {rule.CVarText}" : null);
            if (locked) SetTweak(cvar, rule!.CVarText!);
        }
        if (_laaBox is not null)
        {
            bool? laa = _policy.EnforcedBool("largeAddressAware");
            _laaBox.IsEnabled = laa is null;
            ToolTip.SetTip(_laaBox, laa is null ? null : "Set by the server");
            if (laa is bool on) _laaBox.IsChecked = on;
        }
    }

    void SetTweak(string cvar, string value)
    {
        switch (_tweakControls.GetValueOrDefault(cvar))
        {
            case CheckBox box: box.IsChecked = value == (TweakOf(cvar)?.On ?? "1"); break;
            case NumericUpDown number:
                number.Value = decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal v)
                    ? Math.Clamp(v, number.Minimum, number.Maximum) : null;
                break;
            case TextBox text: text.Text = value; break;
        }
    }

    void OnSaveTweaks(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        try
        {
            ConfigWtf config = ConfigWtf.Load(Folder);
            DxvkConf dxvk = DxvkConf.Load(Folder);
            foreach (Tweak t in Tweaks.All)
            {
                if (t.Requires is not null && !OptionOn(t.Requires)) continue;   // DXVK off: leave dxvk.conf alone
                string? value = BoxValue(t, _tweakControls[t.CVar]);
                if (value is null) continue;
                if (t.File == "dxvk") dxvk.Set(t.CVar, value); else config.Set(t.CVar, value);
            }
            foreach (string cvar in _serverTweaks)
                if (_tweakControls[cvar] is TextBox { Text: { Length: > 0 } text })
                {
                    string clean = new string(text.Trim().Where(c => c is not ('"' or '\r' or '\n')).ToArray());
                    if (clean.Length > 0) config.Set(cvar, clean);
                }
            _policy.ApplyEnforcedTweaks(config);   // enforced values win, whatever the boxes say
            _policy.ApplyEnforcedTweaks(dxvk, "dxvk");
            config.Save();
            dxvk.Save();
            if (_laaBox is not null) LargeAddressAware.Set(GameFolder.ExePath(Folder), _laaBox.IsChecked == true);
            LoadTweaks();
            Status("Tweaks saved.");
            Log("Tweaks saved to WTF\\Config.wtf" + (OptionOn("DXVK") ? " and dxvk.conf" : ""));
        }
        catch (Exception error)
        {
            Logger.Error("Saving tweaks", error);
            Status($"Couldn't save the tweaks: {error.Message} (is the game running?)");
        }
    }

    void OnRecommendedTweaks(object? sender, RoutedEventArgs e)
    {
        _settings.TweakPresetChosen = true;
        FillPreset("recommended");
        Status("The launcher's recommended values are filled in; press Save.");
    }

    void OnDefaultTweaks(object? sender, RoutedEventArgs e)
    {
        _settings.TweakPresetChosen = true;
        FillPreset("default");
        Status("The launcher's default values are filled in; press Save.");
    }

    // ------------------------------------------------------------------------------------------- addons
    void RefreshInstalledAddons()
    {
        InstalledList.Children.Clear();
        if (!GameFolder.LooksLikeClient(Folder)) return;
        foreach (InstalledAddon addon in AddonFolder.Installed(Folder, _manifest))
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("2*,3*,Auto,Auto"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = addon.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            TextBlock notes = new() { Text = addon.Notes, Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(notes, 1);
            row.Children.Add(notes);
            TextBlock version = new()
            {
                Text = addon.FromServer ? "server" : addon.Version.Length > 0 ? addon.Version : "not versioned",
                Classes = { addon.FromServer ? "good" : "accent" },
                Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            };
            Grid.SetColumn(version, 2);
            row.Children.Add(version);
            Button remove = new() { Content = "Remove", FontSize = 12, IsEnabled = !addon.FromServer };
            remove.Click += (_, _) =>
            {
                if (remove.Tag is not true) { remove.Tag = true; remove.Content = "Sure?"; return; }
                try
                {
                    string moved = AddonFolder.Remove(Folder, addon.Folder, Backups.Folder);
                    Log($"Removed addon {addon.Folder} (moved to {moved})");
                    RefreshInstalledAddons();
                }
                catch (Exception error) { Status(error.Message); Logger.Error($"Removing {addon.Folder}", error); }
            };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            InstalledList.Children.Add(row);
        }
    }

    // These take the WoW folder as a value so background work can use them (the folder box only works on the window's thread)
    bool IsInstalled(CatalogAddon addon, string folder) =>
        GameFolder.LooksLikeClient(folder) && (addon.Folders.Count > 0
            ? addon.Folders.All(f => Directory.Exists(Path.Combine(AddonFolder.Path(folder), f)))
            : _settings.AddonCommits.ContainsKey(addon.Name));

    bool NeedsInstall(CatalogAddon addon, string folder) =>
        !IsInstalled(addon, folder) || (_latestCommits.TryGetValue(addon.Name, out string? latest)
                                        && (!_settings.AddonCommits.TryGetValue(addon.Name, out string? have) || have != latest));

    void RefreshCatalog()
    {
        CatalogList.Children.Clear();
        if (_catalog.Count == 0)
            CatalogList.Children.Add(new TextBlock { Text = "The server lists no addons (addons.json).", Classes = { "dim" } });
        foreach (CatalogAddon addon in _catalog.OrderByDescending(a => a.Required).ThenBy(a => a.Name))
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("2*,3*,Auto"), Margin = new Thickness(0, 2), Background = Brushes.Transparent };
            row.Children.Add(new TextBlock
            {
                Text = addon.Name + (addon.Required ? "  (needed)" : ""), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Classes = { addon.Required ? "good" : "text" },
            });
            TextBlock description = new() { Text = addon.Description, Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            string folder = Folder;
            bool installed = IsInstalled(addon, folder), update = installed && NeedsInstall(addon, folder);
            Button action = new() { Content = !installed ? "Install" : update ? "Update" : "Installed", FontSize = 12, IsEnabled = !installed || update };
            action.Click += async (_, _) =>
            {
                string target = Folder;
                await RunAsync($"Addon {addon.Name}", token => InstallCatalogAsync(addon, target, token));
            };
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
            row.PointerPressed += async (_, _) => await PreviewAsync(addon);
            CatalogList.Children.Add(row);
        }
    }

    async Task PreviewAsync(CatalogAddon addon)
    {
        _previewed = addon;
        PreviewTitle.Text = addon.Name.ToUpperInvariant();
        PreviewInstall.IsVisible = PreviewOpen.IsVisible = true;
        PreviewInstall.Content = IsInstalled(addon, Folder) ? "Reinstall" : "Install";
        PreviewText.Text = $"{addon.Description}\n\n{addon.Url}\n\nLoading the page…";
        try
        {
            string readme = await AddonFolder.ReadmeAsync(Web, addon);
            if (_previewed == addon)
                PreviewText.Text = $"{addon.Description}\n\n{(readme.Length > 0 ? Markdown.ToPlainText(readme) : "This repository has no README.")}";
        }
        catch (Exception error)
        {
            if (_previewed == addon) PreviewText.Text = $"{addon.Description}\n\nCouldn't load the page: {error.Message}";
        }
    }

    async void OnPreviewInstall(object? sender, RoutedEventArgs e)
    {
        if (_previewed is not { } addon) return;
        string folder = Folder;
        await RunAsync($"Addon {addon.Name}", token => InstallCatalogAsync(addon, folder, token));
    }

    void OnPreviewOpen(object? sender, RoutedEventArgs e)
    {
        if (_previewed is not null) OpenUrl(_previewed.Url);
    }

    async Task RefreshCommitsAsync(IEnumerable<CatalogAddon> addons, CancellationToken token)
    {
        foreach (CatalogAddon addon in addons)
        {
            try
            {
                if (await AddonFolder.LatestCommitAsync(Web, addon, token) is { Length: > 0 } sha)
                    _latestCommits[addon.Name] = sha;
            }
            catch (HttpRequestException error) { Logger.Warning($"{addon.Name}: couldn't check GitHub for updates", error); }
        }
    }

    /// <summary>Installs a catalog addon (runs in the background: takes the WoW folder as a value).</summary>
    async Task InstallCatalogAsync(CatalogAddon addon, string folder, CancellationToken token)
    {
        if (!GameFolder.LooksLikeClient(folder)) throw new InvalidOperationException("Pick your WoW folder first.");
        IReadOnlyList<string> folders = await AddonFolder.InstallAsync(Web, folder, addon, new UiProgress<string>(text => StatusText.Text = text), token);
        if (!_latestCommits.ContainsKey(addon.Name)) await RefreshCommitsAsync([addon], token);
        string commit = _latestCommits.GetValueOrDefault(addon.Name, "installed");
        Log($"Installed {addon.Name}: {string.Join(", ", folders)}");
        Status($"{addon.Name} installed.");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _settings.AddonCommits[addon.Name] = commit;
            SaveSettings();
            RefreshInstalledAddons();
            RefreshCatalog();
        });
    }

    async void OnCheckAddonUpdates(object? sender, RoutedEventArgs e)
    {
        string folder = Folder;
        await RunAsync("Addon updates", async token =>
        {
            await RefreshCommitsAsync(_catalog.Where(a => IsInstalled(a, folder)), token);
            int updates = _catalog.Count(a => IsInstalled(a, folder) && NeedsInstall(a, folder));
            Status(updates == 0 ? "All addons are up to date." : string.Format(Translation.T("Updates available for {0}."), Words.Addons(updates)));
            await Dispatcher.UIThread.InvokeAsync(RefreshCatalog);
        });
    }

    async void OnUpdateAllAddons(object? sender, RoutedEventArgs e)
    {
        string folder = Folder;
        await RunAsync("Addon updates", async token =>
        {
            await RefreshCommitsAsync(_catalog.Where(a => IsInstalled(a, folder)), token);
            foreach (CatalogAddon addon in _catalog.Where(a => IsInstalled(a, folder) && NeedsInstall(a, folder)).ToList())
                await InstallCatalogAsync(addon, folder, token);
            Status("Addons up to date.");
        });
    }

    void OnOpenAddons(object? sender, RoutedEventArgs e)
    {
        if (!NeedFolder()) OpenFolder(AddonFolder.Path(Folder));
    }

    // ------------------------------------------------------------------------------------------- mods / options
    void RefreshMods()
    {
        OptionsList.Children.Clear();
        if (_manifest is null || _manifest.OptionGroups.Count == 0)
            OptionsList.Children.Add(new TextBlock { Text = "The server offers no cosmetic patches (or the launcher hasn't connected yet).", Classes = { "dim" } });
        foreach (OptionGroup group in _manifest?.OptionGroups ?? [])
        {
            // a card per group, in a grid that fills the width
            StackPanel panel = new() { Spacing = 6 };
            Border card = new()
            {
                Child = panel, Width = 300, MinHeight = 120, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12),
                Classes = { "card" },
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            };
            using (Look.Untranslated())   // the server's names and descriptions stay as it wrote them
            {
                panel.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 });
                if (group.Description.Length > 0)
                    panel.Children.Add(new TextBlock { Text = group.Description, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }
            // how big each choice is ("On" / "Off" groups: only the size of On)
            List<string> sized = [.. group.Options.Where(o => InstallSizes.Option(_manifest!, group.Name, o) > 0)];
            if (sized.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Classes = { "dim" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Text = sized.Count == 1 && group.Options.Count <= 2
                        ? Words.Size(InstallSizes.Option(_manifest!, group.Name, sized[0]))
                        : string.Join("  ·  ", sized.Select(o => $"{o}: {Words.ShortSize(InstallSizes.Option(_manifest!, group.Name, o))}")),
                });
            string? chosen = UpdatePlanner.ChosenOption(_manifest!, _settings.Options, group.Name);

            void Pick(string value, string shown)
            {
                _settings.Options[group.Name] = value;
                SaveSettings();
                ShowGatedTweaks();
                _pendingFiles = Math.Max(_pendingFiles, 1);
                ShowPlayState();
                Status($"{group.Name}: {shown}. Press UPDATE to install it.");
            }

            // on/off groups ("On"/"Off", or one option that may be left out) get a switch; real choices a dropdown
            bool onOff = group.Options.SequenceEqual(["On", "Off"], StringComparer.OrdinalIgnoreCase);
            bool single = group.Optional && group.Options.Count == 1;
            Control control;
            if (onOff || single)
            {
                string on = onOff ? group.Options[0] : group.Options[0], off = onOff ? group.Options[1] : "";
                ToggleSwitch toggle = new()
                {
                    IsChecked = string.Equals(chosen, on, StringComparison.OrdinalIgnoreCase),
                    OnContent = single ? on : "On", OffContent = "Off", IsEnabled = !group.Enforce,
                };
                toggle.IsCheckedChanged += (_, _) => Pick(toggle.IsChecked == true ? on : off, toggle.IsChecked == true ? "on" : "off");
                control = toggle;
            }
            else
            {
                List<string> items = [.. (group.Optional ? new[] { "(none)" } : []), .. group.Options];
                ComboBox box = new() { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !group.Enforce };
                box.SelectedItem = chosen ?? (group.Optional ? "(none)" : items.FirstOrDefault());
                box.SelectionChanged += (_, _) =>
                {
                    string picked = box.SelectedItem as string ?? "";
                    Pick(picked == "(none)" ? "" : picked, picked);
                };
                control = box;
            }
            if (group.Enforce) ToolTip.SetTip(control, "Set by the server");
            panel.Children.Add(control);
            // the server's links for this option (GitHub, website...), bottom right of the card
            List<OptionLink> links = [.. group.Links.Where(l => l.IsWeb && l.Name.Length > 0)];
            if (links.Count > 0)
            {
                StackPanel row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                         VerticalAlignment = VerticalAlignment.Bottom, Spacing = 2 };
                foreach (OptionLink link in links)
                {
                    Button open;
                    using (Look.Untranslated()) open = new Button { Content = link.Name, Classes = { "link" }, FontSize = 12 };
                    ToolTip.SetTip(open, link.Url);
                    open.Click += (_, _) => OpenUrl(link.Url);
                    row.Children.Add(open);
                }
                Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
                card.Child = null;
                layout.Children.Add(panel);
                Grid.SetRow(row, 1);
                layout.Children.Add(row);
                card.Child = layout;
            }
            OptionsList.Children.Add(card);
        }
        RefreshUntracked();
    }

    void RefreshUntracked()
    {
        UntrackedList.Children.Clear();
        if (_manifest is null || !GameFolder.LooksLikeClient(Folder)) return;
        IReadOnlyList<string> files = Untracked.Find(Folder, _manifest);
        if (files.Count == 0) UntrackedList.Children.Add(new TextBlock { Text = "None.", Classes = { "dim" } });
        foreach (string file in files)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = file, VerticalAlignment = VerticalAlignment.Center, Classes = { "accent" } });
            Button remove = new() { Content = "Remove", FontSize = 12 };
            remove.Click += (_, _) =>
            {
                try { Untracked.Remove(Folder, file); Log($"Moved untracked {file} to the backup folder"); RefreshUntracked(); RefreshUntrackedButton(); }
                catch (Exception error) { Status(error.Message); Logger.Error($"Moving {file}", error); }
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            UntrackedList.Children.Add(row);
        }
    }

    void RefreshUntrackedButton()
    {
        int count = _manifest is not null && GameFolder.LooksLikeClient(Folder) ? Untracked.Find(Folder, _manifest).Count : 0;
        UntrackedButton.IsVisible = count > 0;
        UntrackedButton.Content = $"⚠ Untracked files ({count})";
    }

    void OnUntracked(object? sender, RoutedEventArgs e) => OnTab(ModsTab, e);

    // ------------------------------------------------------------------------------------------- settings
    async void OnPickBackground(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Launcher background",
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _settings.Background = path;
            SaveSettings();
            ApplyBackground();
        }
    }

    void OnDefaultBackground(object? sender, RoutedEventArgs e)
    {
        _settings.Background = "";
        SaveSettings();
        ApplyBackground();
    }

    void RefreshBackups()
    {
        BackupList.ItemsSource = Backups.List();
        BackupList.SelectedIndex = BackupList.ItemCount > 0 ? 0 : -1;
    }

    async void OnBackupNow(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        string folder = Folder;
        await RunAsync("Backup", _ =>
        {
            Status("Backing up…");
            string file = Backups.Create(folder, "manual", keep: _settings.BackupsToKeep);
            Log($"Backup: {file}");
            Status("Backup made.");
            return Task.CompletedTask;
        });
        RefreshBackups();
    }

    async void OnRestoreBackup(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder() || BackupList.SelectedItem is not Backups.Backup backup) return;
        string folder = Folder;
        await RunAsync("Restore", _ =>
        {
            Status("Restoring…");
            Backups.Restore(folder, backup.File);
            Log($"Restored {backup.File}");
            Status("Backup restored (what was there before was backed up first).");
            return Task.CompletedTask;
        });
        RefreshBackups();
        RefreshInstalledAddons();
    }

    void OnOpenBackups(object? sender, RoutedEventArgs e) => OpenFolder(Backups.Folder);

    async void OnExportMacros(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export macros and key bindings",
            SuggestedFileName = $"temucraft-macros-{DateTime.Now:yyyyMMdd}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { Status(string.Format(Translation.T("Exported {0}."), Words.Files(Backups.ExportMacros(Folder, path)))); }
        catch (Exception error) { Status(error.Message); Logger.Error("Exporting macros", error); }
    }

    async void OnImportMacros(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import macros and key bindings",
            FileTypeFilter = [new FilePickerFileType("Zip") { Patterns = ["*.zip"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try { Status(string.Format(Translation.T("Imported {0}. Close the game first if it was running."), Words.Files(Backups.ImportMacros(Folder, path)))); RefreshBackups(); }
        catch (Exception error) { Status(error.Message); Logger.Error("Importing macros", error); }
    }

    void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (Logger.File is { } log && File.Exists(log)) OpenUrl(log);
        else OpenFolder(UserSettings.DefaultFolder);
    }
}
