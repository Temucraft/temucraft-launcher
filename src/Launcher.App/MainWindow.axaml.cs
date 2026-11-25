using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
        // testing mode: Shift + click on PLAY starts the game without updating (to test local files)
        PlayButton.AddHandler(PointerPressedEvent, (_, e) => _shiftPlay = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift),
                              Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        LoadDownloadHistory();
        HookTweaksSearch();
        Logger.Written += ShowLogLine;
        Logger.Written += ToastOnError;
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
        _ = Task.Run(() => SelfCheck.CleanUpAtStartAsync());   // the copy the last self update left behind
        ShowTagLegend();
        ShowFeeds(FeedCache.Load(_feedFile));   // the last news and changelog, until the server sends new ones
        FillLookBoxes();
        BuildScreens();
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
        if (_settings.StartPage == "last" && _settings.LastPage is > 0 and <= 6)
            OnTab(TabButton(_settings.LastPage), new RoutedEventArgs());
        _autoUpdateTimer.Tick += async (_, _) => await AutoUpdateAsync();
        _autoUpdateTimer.Start();
        _launcherTimer.Tick += async (_, _) => await CheckLauncherAsync();
        _launcherTimer.Start();
        TweaksList.SizeChanged += (_, _) => { double w = TweakRowWidth(); foreach (Grid r in _tweakRows) r.Width = w; };
        OptionsList.SizeChanged += (_, _) =>
        {
            double w = OptionCardWidth();
            foreach (Control c in OptionsList.Children)
                if (c is Border) c.Width = w;
                else if (c is TextBlock heading) heading.Width = Math.Max(200, OptionsList.Bounds.Width - 12);   // a heading takes its own line
        };
        Activated += async (_, _) => await CheckLauncherAsync();   // also when the player comes back to the window
        Closed += (_, _) => { Logger.Written -= ShowLogLine; Logger.Info("Launcher closed", toWindow: false); };
        Opened += async (_, _) =>
        {
            LauncherVersionText.Text = $"Temucraft Launcher {LauncherInfo.Version}";
            ReportBugButton.Content = $"🐞 v{LauncherInfo.Version}";
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
        Control[] pages = [NewsPage, TweaksPage, AddonsPage, ModsPage, SettingsPage, LauncherPage, ScreensPage];
        Button[] tabs = [NewsTab, TweaksTab, AddonsTab, ModsTab, SettingsTab, LauncherTab, ScreensTab];
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

    Button TabButton(int page) => new[] { NewsTab, TweaksTab, AddonsTab, ModsTab, SettingsTab, LauncherTab, ScreensTab }[Math.Clamp(page, 0, 6)];

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
        // a sleeping PC drops the download (a locked one is fine): stay awake until the job ends
        using KeepAwake awake = new(what);
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
        catch (LauncherRestartException restart)
        {
            Log("Launcher updated; starting the new one");
            _work = null;
            RestartInto(restart.Exe);
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
            : string.Format(Translation.T("There's no game in your WoW folder yet: press INSTALL to download it there{0}."), InstallSizeHint(_manifest, Choice())));
        return true;
    }

    /// <summary>" (+2.6 GB)": how much a full install with these choices downloads (the game included), or "".</summary>
    static string InstallSizeHint(Manifest? manifest, InstallChoice choice)
    {
        if (manifest is null || !manifest.Files.Any(f => f.Category == FileCategory.Base)) return "";
        long total = InstallSizes.Total(manifest, choice with { BaseFolder = choice.WowFolder });
        return total > 0 ? $" (+{Words.Size(total)})" : "";
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
            RefreshMusicChoices();   // the page was built before the server's music clips were known
            RefreshUntrackedButton();
            MaybeAskConfigPreset();
        });
        if (!launcherOk) Log("Self-check: this launcher differs from the server's copy");
        bool firstCheck = !_launcherChecked;
        _launcherChecked = true;
        if (!launcherOk && manifest.Launcher is not null && manifest.Rules.LauncherUpdateFirst && firstCheck)
        {
            // the server wants the new launcher first: get it now, then start it (nothing else runs before)
            Status("Updating the launcher first…");
            string exe = await SelfCheck.UpdateAsync(client, manifest, new UiProgress<(long Done, long Total)>(p =>
                Progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total), token);
            throw new LauncherRestartException(exe);
        }
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

    // ------------------------------------------------------------------------------------------- play tickets
    /// <summary>
    /// Tells the server this PC is about to play: a request for SERVER_URL/play-ticket, with the player's login.
    /// A server that uses play tickets (server/play-ticket.py) then opens the game's login port to this IP for a few
    /// hours; on a server without them the file is missing (404) and nothing happens. Never stops the game starting.
    /// </summary>
    async Task<bool> RequestPlayTicketAsync(bool quiet = false)
    {
        if (_server is null) return false;
        if (!HaveLogin || !Agreed)
        {
            if (!quiet) Log("No play ticket: not logged in (on a server that needs one, the game can't log in until you log in here)");
            return false;
        }
        ServerClient client = NewClient();   // reads the login boxes: on the window's thread
        try
        {
            using (client)
            {
                string? answer = await client.GetTextAsync("play-ticket");
                if (answer is null) return false;   // this server doesn't use play tickets
                if (!quiet) Log("Play ticket: this PC may log in to the game");
                return true;
            }
        }
        catch (Exception error)
        {
            Logger.Warning("Couldn't get a play ticket", error);
            if (!quiet) Status(string.Format(Translation.T("The server didn't give this PC a play ticket ({0}); the game may not be able to log in."), error.Message));
            return false;
        }
    }

    async Task RenewPlayTicketAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(1), token);
                await RequestPlayTicketAsync(quiet: true);
            }
        }
        catch (OperationCanceledException) { }
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
    bool _launcherLater;     // "Later" pressed: no banner again for that launcher
    string _laterSha = "";   // the server's launcher when "Later" was pressed
    bool _launcherChecked;   // the first check of this run is done (only that one installs a new launcher by itself)
    readonly DispatcherTimer _launcherTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    DateTime _lastLauncherCheck = DateTime.Now;

    /// <summary>While the launcher is open: every 5 minutes, and when the window gets focus again (at most once a minute),
    /// whether the server has a new launcher (a banner asks).</summary>
    async Task CheckLauncherAsync()
    {
        if (Busy || _server is null || !HaveLogin || !Agreed || !_launcherChecked) return;
        if (DateTime.Now - _lastLauncherCheck < TimeSpan.FromMinutes(1)) return;
        _lastLauncherCheck = DateTime.Now;
        try
        {
            using ServerClient client = NewClient();
            Manifest manifest = await client.GetManifestAsync();
            if (manifest.Launcher is null || await SelfCheck.MatchesAsync(manifest)) return;
            _manifest ??= manifest;
            if (_manifest.Launcher?.Sha256 != manifest.Launcher.Sha256) _manifest = manifest;
            if (_launcherLater && _laterSha != manifest.Launcher.Sha256) _launcherLater = false;   // a newer one since "Later"
            Log("A new launcher is on the server");
            ShowLauncherUpdate(true);
        }
        catch (Exception error) { Logger.Warning("Couldn't check for a new launcher", error); }
    }

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

    /// <summary>Mods cards share the page width: as many columns as fit (each at least 250 wide).</summary>
    double OptionCardWidth()
    {
        const double gap = 12, min = 250;   // each card keeps a 12 margin on its right
        double width = OptionsList.Bounds.Width;
        if (width <= 0) return 300;
        int columns = Math.Max(1, (int)(width / (min + gap)));
        return Math.Floor(width / columns) - gap;
    }

    void OnWhatsNew(object? sender, RoutedEventArgs e) => WhatsNewPanel.IsVisible = !WhatsNewPanel.IsVisible;

    void OnLauncherLater(object? sender, RoutedEventArgs e)
    {
        _launcherLater = true;
        _laterSha = _manifest?.Launcher?.Sha256 ?? "";
        WarningBanner.IsVisible = false;
        Log("Launcher update put off until the next start");
    }

    async void OnUpdateLauncher(object? sender, RoutedEventArgs e)
    {
        if (_server is null || _manifest is null || Busy) return;
        if (_gameRunning) { Status(Translation.T("Close the game first, then update the launcher.")); return; }
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
        RestartInto(exe);
    }

    DateTime _progressTextAt = DateTime.MinValue;

    /// <summary>Pads changing numbers ("8.2 MB/s", "34 min left") to a fixed width with figure spaces, so the text
    /// after them doesn't jump left and right while they change (with the digits drawn at one width, tnum).</summary>
    internal static string Steady(string text, int width) => text.Length >= width ? text : new string('\u2007', width - text.Length) + text;

    /// <summary>Starts the new launcher and closes this one.</summary>
    void RestartInto(string exe)
    {
        SaveSettings();
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, ArgumentList = { Program.AfterUpdate } });
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
    sealed record LookChoice(string Value, string Text, bool Recommended = false) { public override string ToString() => Text; }
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
        TryShowWhatsNew(feeds.Changelog);
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
                    ? string.Format(Translation.T("Connected. There's no game in your WoW folder yet: press INSTALL to download it there{0}."), InstallSizeHint(manifest, choice))
                    : "Connected. Pick your WoW folder under Game files, or an empty folder to install the game into.");
                await Dispatcher.UIThread.InvokeAsync(ShowPlayState);
                await Dispatcher.UIThread.InvokeAsync(RefreshMods);
                return;
            }
            IReadOnlyList<PlannedFile> outdated = await PlanAsync(manifest, choice, token);
            // needed addons always; default ones (debugging tools) once, so removing them sticks
            bool withRecommended = _settings.InstallRecommendedNext;
            List<CatalogAddon> required = _catalog.Where(a => choice.Addons && (a.Required
                || (a.Default && !_settings.DefaultAddonsDone.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                || (a.Recommended && withRecommended))).ToList();
            await RefreshCommitsAsync(required, token);
            List<CatalogAddon> addonUpdates = required.Where(a => NeedsInstall(a, folder)).ToList();
            Logger.Info($"{Words.Files(outdated.Count)} and {Words.Addons(addonUpdates.Count)} out of date", toWindow: false);

            if (!download)
            {
                long size = outdated.Where(p => !KeptFiles.Has(folder, p.File.Sha256)).Sum(p => p.File.Size); // kept files need no download
                int pending = outdated.Count + addonUpdates.Count;
                Status(pending == 0 ? "Everything up to date!"
                    : string.Format(Translation.T("Update available! {0}, {1} to download."), Words.Files(pending), Words.Size(size)));
                PendingUpdate described = DescribeUpdate(folder, manifest, outdated, addonUpdates);
                await Dispatcher.UIThread.InvokeAsync(() => { _pendingFiles = pending; SetPendingUpdate(described); ShowPlayState(); });
                return;
            }

            // The game keeps its MPQs and DLLs open, so those need it closed. Addons and loose interface files aren't held
            // open: an update that only has those goes ahead while the game runs (a /reload in game loads them).
            bool onlyUi = outdated.All(p => p.File.Path.Replace('\\', '/').TrimStart('/').StartsWith("Interface/", StringComparison.OrdinalIgnoreCase));
            bool gameOpen = haveGame && GameRunning(folder);
            if (gameOpen && !onlyUi && !await Dispatcher.UIThread.InvokeAsync(() => MakeSureGameClosedAsync(folder))) return;
            gameOpen = gameOpen && onlyUi;

            if (_settings.BackupBeforeUpdate && (outdated.Count > 0 || addonUpdates.Count > 0))
            {
                Status("Backing up your settings and addons…");
                Log($"Backup: {Path.GetFileName(Backups.Create(folder, "before-update", keep: _settings.BackupsToKeep))}");
            }
            if (!gameOpen)   // these move MPQs around: next update with the game closed
            {
                foreach (string removed in GameFolder.RemoveObsolete(folder, manifest.Obsolete))
                    Log($"Removed {removed} (no longer on the server)");
                // files of options turned off are kept (not deleted), so switching back needs no download
                HashCache keptCache = HashCache.Load(_cacheFile);
                foreach (string kept in await KeptFiles.KeepAsync(folder, UpdatePlanner.Unchosen(manifest, choice), keptCache, token))
                    Log($"Kept {kept} aside (option not picked; it comes back without a download)");
                outdated = KeptFiles.Restore(folder, outdated, keptCache, Log);
                keptCache.Save();
            }
            await InstallAsync(client, outdated, folder, token);
            foreach (CatalogAddon addon in addonUpdates) await InstallCatalogAsync(addon, folder, token);
            foreach (CatalogAddon addon in required.Where(a => a.Default && !a.Required))
                if (!_settings.DefaultAddonsDone.Contains(addon.Name)) _settings.DefaultAddonsDone.Add(addon.Name);
            if (withRecommended) _settings.InstallRecommendedNext = false;
            GameFolder.WriteRealmlist(folder, manifest.Realmlist);
            ApplyEnforcedToGame(folder);
            ApplyLoginScenes(folder);
            ApplyCharacterScenes(folder);
            if (!gameOpen) await SyncHdModelsAsync(folder, askToCloseGame: false);
            Status(gameOpen ? "Updated while the game runs: type /reload in game to load the new addons." : "Everything up to date!");
            await Dispatcher.UIThread.InvokeAsync(() => { _pendingFiles = 0; SetPendingUpdate(null); ShowPlayState(); RefreshInstalledAddons(); RefreshCatalog(); });
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
        // the list is made of controls: built on the window's thread (this runs in the background)
        Dictionary<PlannedFile, DownloadRow> rows = await Dispatcher.UIThread.InvokeAsync(() => StartDownloadList(outdated));
        DownloadRow? currentRow = null;
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
                    // the file before this one is finished; a copy is finished when this is called
                    if (currentRow is not null) SetRow(currentRow, RowState.Done);
                    currentRow = null;
                    if (rows.TryGetValue(planned, out DownloadRow? row))
                    {
                        if (copy) SetRow(row, RowState.Copied);
                        else { SetRow(row, RowState.Downloading, 0, planned.File.Size); currentRow = row; }
                    }
                    UpdateDownloadsButton();
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
                    // the numbers change once a second (the bar keeps moving), so they can be read
                    if (p.Done < p.Total && DateTime.UtcNow - _progressTextAt < TimeSpan.FromSeconds(1)) return;
                    _progressTextAt = DateTime.UtcNow;
                    string rate = speed > 0 ? $"  ·  {Steady(SpeedLimit.Format(speed, _settings.SpeedInBits), 11)}" : "";
                    string left = Steady(SpeedLimit.TimeLeft(total - done, speed), 12);
                    if (rows.TryGetValue(planned, out DownloadRow? row))
                        SetRow(row, RowState.Downloading, p.Done, p.Total, SpeedLimit.TimeLeft(p.Total - p.Done, speed));
                    StatusText.Text = string.Format(Translation.T("Downloading {0} ({1}/{2})"), planned.File.Path, Index(planned), outdated.Count)
                                      + $"  {Steady(Words.ShortSize(p.Done), 9)} / {Words.ShortSize(p.Total)}{rate}" + (left.Trim().Length > 0 ? $"  ·  {left}" : "");
                    ToolTip.SetTip(Progress, $"{Words.ShortSize(done)} / {Words.Size(total)}");
                }),
                token: token);
            if (result.Copied > 0)
                Log($"{Words.Files(result.Copied)} ({Words.Size(result.BytesCopied)}) copied from your own disk instead of downloaded");
            // after the queued file updates: the last file is done too
            Dispatcher.UIThread.Post(() =>
            {
                if (currentRow is not null) SetRow(currentRow, RowState.Done);
                currentRow = null;
            });
        }
        catch (Exception error)
        {
            bool stopped = error is OperationCanceledException;
            Dispatcher.UIThread.Post(() =>
            {
                if (currentRow is not null) SetRow(currentRow, stopped ? RowState.Stopped : RowState.Failed);
                currentRow = null;
                foreach (DownloadRow row in rows.Values.Where(r => r.Entry.State == RowState.Waiting)) SetRow(row, RowState.Stopped);
            });
            throw;
        }
        finally
        {
            cache.Save();
            Dispatcher.UIThread.Post(() => { UpdateDownloadsButton(); SaveDownloadHistory(); });
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
    bool _installRecommended = true;

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
        {
            // the server's recommended pick for a group the player never chose
            if (!g.Enforce && g.Recommended.Length > 0 && !_settings.Options.ContainsKey(g.Name)
                && g.Options.Contains(g.Recommended, StringComparer.OrdinalIgnoreCase))
                _installOptions[g.Name] = g.Recommended;
            else if (UpdatePlanner.ChosenOption(manifest, _settings.Options, g.Name) is { } chosen) _installOptions[g.Name] = chosen;
        }
        _installAddons = AddonsBox.IsChecked == true;
        BuildInstallParts(manifest);
        InstallOverlay.IsVisible = true;
    }

    Grid PartRow(Control left, long bytes, bool strong = false)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
        row.Children.Add(left);
        TextBlock size = new() { Text = Words.Size(bytes), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                                 MinWidth = 56, TextAlignment = TextAlignment.Right, Classes = { strong ? "text" : "soft" } };
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
                    using (Look.Untranslated()) box = new CheckBox { Content = NameWithTag(group.Name, group.Recommended.Equals(on, StringComparison.OrdinalIgnoreCase)) };
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
                                              .. group.Options.Select(o => new LookChoice(o, $"{o}  ({Words.ShortSize(InstallSizes.Option(manifest, group.Name, o))})",
                                                                                          group.Recommended.Equals(o, StringComparison.OrdinalIgnoreCase)))];
                    ComboBox combo = new() { ItemsSource = items, MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !group.Enforce,
                                             ItemTemplate = new FuncDataTemplate<LookChoice>((c, _) => NameWithTag(c?.Text ?? "", c?.Recommended == true)) };
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
        int recommended = _catalog.Count(a => a.Recommended);
        if (recommended > 0)
        {
            CheckBox rec = new() { Content = string.Format(Translation.T("Also install the {0} recommended addons (quality of life)"), recommended), IsChecked = _installRecommended };
            ToolTip.SetTip(rec, string.Join(", ", _catalog.Where(a => a.Recommended).Select(a => a.Name)));
            rec.IsCheckedChanged += (_, _) => _installRecommended = rec.IsChecked == true;
            InstallParts.Children.Add(rec);
        }
        ShowInstallTotal(manifest);
    }

    /// <summary>
    /// A description cut to its first sentence, with "more" to show the rest (and "less" to fold it again), so a card
    /// says in one line what it does. A short description shows whole.
    /// </summary>
    static Control ShortDescription(string text)
    {
        text = text.Trim();
        int end = -1;
        for (int i = 0; i < text.Length - 1; i++)
            if (text[i] is '.' or '!' or '?' or ':' && text[i + 1] == ' ') { end = i + 1; break; }
        if (end < 0 || text.Length - end < 25)
            return new TextBlock { Text = text, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        string first = text[..end];
        TextBlock shown = new() { Text = first, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Button toggle = new()
        {
            Content = Translation.T("more"), FontSize = 11, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.Classes.Add("link");
        bool open = false;
        toggle.Click += (_, _) =>
        {
            open = !open;
            shown.Text = open ? text : first;
            toggle.Content = Translation.T(open ? "less" : "more");
        };
        return new StackPanel { Spacing = 0, Children = { shown, toggle } };
    }

    /// <summary>A name, followed by a dimmed "recommended" when the server recommends it.</summary>
    static Control NameWithTag(string name, bool recommended)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        if (recommended)
            line.Children.Add(new TextBlock { Text = Translation.T("recommended"), Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return line;
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
            _settings.InstallRecommendedNext = _installRecommended;
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
    bool _shiftPlay;

    async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if ((NoGameYet || Folder.Length == 0) && _server is not null)
        {
            // no game yet: the install window (where, what, how big)
            await OpenInstallAsync();
            return;
        }
        if (NeedFolder()) return;
        bool skipUpdate = _shiftPlay && TestingFiles.Enabled(AppContext.BaseDirectory);
        _shiftPlay = false;
        if (skipUpdate && _pendingFiles > 0) Log("Testing mode: playing without the update (Shift + PLAY)");
        if (_pendingFiles > 0 && _server is not null && !skipUpdate)
        {
            // first what the update will change, unless the player said not to ask
            if (!_settings.SkipUpdateInfo && _pendingUpdate is not null) { ShowUpdateInfo(fromUpdate: true); return; }
            await CheckAsync(download: true);
            return;
        }
        SaveSettings();
        try
        {
            if (ClearCacheBox.IsChecked == true) GameFolder.ClearCache(Folder);
            if (_manifest is not null) GameFolder.WriteRealmlist(Folder, _manifest.Realmlist);
            ShareSettings(Folder);
            EnableRequiredAddons(Folder);
            ApplyLoginScenes(Folder);
            ApplyCharacterScenes(Folder);
            await SyncHdModelsAsync(Folder, askToCloseGame: false);
            // testing mode (testing.env next to the launcher): the owner's test files go in, old ones come out
            foreach (string line in TestingFiles.Apply(AppContext.BaseDirectory, Folder, SelectedLocales())) Log(line);
            if (Diagnostics.RemoveCompatibilityMode(GameFolder.ExePath(Folder)) is { } compat)
                Log($"Windows compatibility mode was on for Wow.exe ({compat}); turned it off");
            await RequestPlayTicketAsync();
            bool debug = DebugMode.IsOn(Folder);
            bool debugApplied = _settings.DebugSettingsApplied;
            if (DebugMode.Apply(Folder, debug, ref debugApplied))
                Log(debug ? "Debug mode: error popups and the taint log turned on in WTF\\Config.wtf" : "Debug mode off: the game's debug settings turned off again");
            if (debugApplied != _settings.DebugSettingsApplied) { _settings.DebugSettingsApplied = debugApplied; SaveSettings(); }
            Process? game = GameFolder.Start(Folder, DebugMode.Arguments(debug));
            if (debug) Log("Debug mode is on: the game logs to Logs\\TemucraftDebug.log, and the ` key opens the console");
            DateTime started = DateTime.Now;
            Log("Started the game");
            if (game is null) return;
            switch (_settings.OnGameLaunch)
            {
                case "exit": _quitting = true; Close(); return;
                case "minimize": WindowState = WindowState.Minimized; break;
                case "hide": HideToTray(); break;
            }
            _gameRunning = true;
            // the ticket is renewed every hour while the game runs, so a relog late at night still gets in
            using (CancellationTokenSource playing = new())
            {
                _ = RenewPlayTicketAsync(playing.Token);
                await game.WaitForExitAsync();
                playing.Cancel();
            }
            _gameRunning = false;
            Log($"The game closed (exit code {game.ExitCode})");
            if (game.ExitCode != 0 && DateTime.Now - started < TimeSpan.FromSeconds(90))
            {
                // closed with an error right after starting: most often an extra under Mods, or a broken file
                if (_settings.OnGameLaunch is "minimize" or "hide") BringBack();
                Status(Translation.T("The game closed right after starting. Try turning off the extras under Mods, then Verify game files. Still broken? Press Copy diagnostics on the Launcher page and send it to the server's admin."));
            }
            ShareSettings(Folder);
            if (_settings.OnGameLaunch is "minimize" or "hide" && ReopenBox.IsChecked == true) BringBack();
        }
        catch (Exception error)
        {
            Status(error.Message);
            Logger.Error("Couldn't start the game", error);
        }
    }

    /// <summary>The addons the server needs are turned back on in the game's AddOns.txt files (the game's
    /// character screen can turn them off).</summary>
    void EnableRequiredAddons(string folder)
    {
        if (_manifest is null) return;
        IEnumerable<string> needed = _manifest.Files.Where(f => f.Category == FileCategory.Addon)
            .Select(f => f.Path.Split('/')).Where(p => p.Length > 2).Select(p => p[2])
            .Concat(_catalog.Where(a => a.Required).SelectMany(a => a.Folders));
        int changed = AddonsTxt.EnableRequired(folder, needed);
        if (changed > 0) Log($"Turned {changed} needed addon(s) back on");
    }

    const string DefaultReportBugUrl = "https://github.com/Temucraft/temucraft-launcher/issues/new";

    /// <summary>The bug icon: the diagnostics report on the clipboard, then the server's bug report page (GitHub when it has none).</summary>
    void OnReportBug(object? sender, RoutedEventArgs e)
    {
        OnCopyDiagnostics(sender, e);
        // the server's page for bug reports, or the launcher's GitHub issues
        string url = _policy.Setting("reportBugUrl") is { Value.ValueKind: System.Text.Json.JsonValueKind.String } rule
                     && Uri.TryCreate(rule.Value.GetString(), UriKind.Absolute, out Uri? link) && link.Scheme is "https" or "http"
            ? link.ToString() : DefaultReportBugUrl;
        OpenUrl(url);
        Toast(Translation.T("A bug report is on your clipboard: paste it in the page that just opened."));
    }

    async void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string folder = Folder;
        Manifest? manifest = _manifest;
        List<string> extra = [$"Mods: {(_settings.Options.Count == 0 ? "(none)" : string.Join(", ", _settings.Options.Select(o => $"{o.Key} = {o.Value}")))}"];
        try
        {
            DiagnosticsButton.IsEnabled = false;
            DiagnosticsButton.Content = Translation.T("Making the report…");
            HashCache cache = HashCache.Load(_cacheFile);
            string text = await Task.Run(() => Diagnostics.BuildAsync(folder, manifest, extra, cache));
            try { cache.Save(); } catch (IOException) { }
            await clipboard.SetTextAsync(text);
            DiagnosticsButton.Content = Translation.T("Copied");
            Status(Translation.T("Diagnostics copied. Paste them in a message to the server's admin."));
            await Task.Delay(1500);
            DiagnosticsButton.Content = Translation.T("Copy diagnostics");
            // the crash helper's minidump is too big for the clipboard: offer the folder with it
            if (Diagnostics.NewestCrashDump(folder, TimeSpan.FromDays(7)) is { } dump
                && await ConfirmAsync("Crash dump", Translation.T("The newest crash report has a crash dump (.dmp file) next to it. The server's admin may ask for it. Open the folder with it?"), "Open folder"))
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dump}\"") { UseShellExecute = true })?.Dispose();
                else OpenFolder(Path.GetDirectoryName(dump) ?? folder);
            }
        }
        catch (Exception error)
        {
            Logger.Warning("Couldn't make the diagnostics", error);
            Status(error.Message);
            DiagnosticsButton.Content = Translation.T("Copy diagnostics");
        }
        finally { DiagnosticsButton.IsEnabled = true; }
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
        try
        {
            Directory.CreateDirectory(folder);
            ProcessStartInfo start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(Path.GetFullPath(folder));
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) { Logger.Warning($"Couldn't open the folder {folder}", e); }
    }

    // ------------------------------------------------------------------------------------------- tweaks
    readonly List<Grid> _tweakRows = [];

    /// <summary>Game settings sit in two columns when the page is wide enough (each row at least 640 wide).</summary>
    double TweakRowWidth()
    {
        double width = TweaksList.Bounds.Width;
        if (width <= 0) return double.NaN;
        int columns = width >= 2 * 640 ? 2 : 1;
        return Math.Floor(width / columns) - 16;
    }

    void BuildTweaks()
    {
        TweaksList.Children.Clear();
        _tweakControls.Clear();
        _gatedTweakRows.Clear();
        _tweakRows.Clear();
        _sectionHeads.Clear();
        string? section = null;
        WrapPanel rows = new();
        // the game's exe and the mods that change how it runs first, then the game's own settings
        // (the login and character screens have their own page: Screens)
        TweaksList.Children.Add(new TextBlock { Text = "CLIENT", Classes = { "title", "group" }, Margin = new Thickness(0, 4, 0, 0) });
        TweaksList.Children.Add(new TextBlock { Text = "WOW.EXE", Classes = { "section" } });
        _laaBox = new CheckBox { Content = "Large Address Aware - lets the game use 4 GB of memory instead of 2 GB (Wow.exe is backed up as Wow.exe.bak)" };
        TweaksList.Children.Add(_laaBox);
        foreach (Tweak t in Tweaks.All.Where(t => t.Requires is not null)) AddTweak(t);
        _serverTweaks.Clear();
        List<string> extra = _policy.Tweaks.Keys.Where(k => !Tweaks.All.Any(t => t.CVar.Equals(k, StringComparison.OrdinalIgnoreCase))).Order().ToList();
        if (extra.Count > 0)
        {
            TweaksList.Children.Add(new TextBlock { Text = "FROM THE SERVER", Classes = { "section" } });
            foreach (string cvar in extra)
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("230,150,*"), Margin = new Thickness(0, 3) };
                row.Children.Add(new TextBlock { Text = cvar, VerticalAlignment = VerticalAlignment.Center, Classes = { "text" } });
                TextBox box = new() { Width = 132, HorizontalAlignment = HorizontalAlignment.Left,
                                      Watermark = $"Server recommends: {_policy.Tweaks[cvar].CVarText}" };
                Grid.SetColumn(box, 1);
                row.Children.Add(box);
                TextBlock note = new() { Text = "Set by the server. Empty uses the game's own value.", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                Grid.SetColumn(note, 2);
                row.Children.Add(note);
                TweaksList.Children.Add(row);
                _tweakControls[cvar] = box;
                _serverTweaks.Add(cvar);
            }
        }
        TweaksList.Children.Add(new TextBlock { Text = "IN-GAME SETTINGS", Classes = { "title", "group" }, Margin = new Thickness(0, 22, 0, 0) });
        section = null;
        foreach (Tweak t in Tweaks.All.Where(t => t.Requires is null)) AddTweak(t);
        BuildGameOptions();
        PrepareSections();
        ShowGatedTweaks();
        LoadTweaks();
        FilterTweaks(TweaksSearch.Text);

        void AddTweak(Tweak t)
        {
            if (t.Section != section)
            {
                section = t.Section;
                TextBlock header = new() { Text = section.ToUpperInvariant(), Classes = { "section" } };
                TweaksList.Children.Add(header);
                if (t.Requires is not null) _gatedTweakRows.Add((t.Requires, header));
                rows = new WrapPanel();
                TweaksList.Children.Add(rows);
                if (t.Requires is not null) _gatedTweakRows.Add((t.Requires, rows));
            }
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("210,150,*"), Margin = new Thickness(0, 3, 16, 3), Width = TweakRowWidth() };
            _tweakRows.Add(row);
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
            TextBlock description = new() { Text = t.Description, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Control info = description;
            // how other WoW versions do it: on hover, so the row stays short
            if (t.Compare.Length > 0) { ToolTip.SetTip(description, t.Compare); ToolTip.SetTip(control, t.Compare); }
            Grid.SetColumn(info, 2);
            row.Children.Add(info);
            rows.Children.Add(row);
            _tweakControls[t.CVar] = control;
        }
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

    // ------------------------------------------------------------------------------------- login screen
    readonly Dictionary<string, CheckBox> _sceneBoxes = [];
    WrapPanel? _scenePanel;
    StackPanel? _sceneButtons;
    // drawn login buttons over the scene pictures ("Preview with the login buttons")
    readonly List<Control> _sceneOverlays = [];
    bool _sceneOverlayOn;
    CheckBox? _sceneOverlayBox;

    /// <summary>The login screen section: one box per scene in the game's loginui.lua (one is picked at random).</summary>
    void BuildLoginScenes(Panel target)
    {
        target.Children.Add(new TextBlock { Text = "Pick the backgrounds your login screen can show. Each time the game starts it shows one of the ticked ones at random. Click a picture to see its versions.",
                                                Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        _scenePanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        Button all = new() { Content = "Tick all", FontSize = 12, Padding = new Thickness(8, 2) };
        all.Click += (_, _) => { foreach (CheckBox box in _sceneBoxes.Values) box.IsChecked = true; };
        Button server = new() { Content = "Use the server's picks", FontSize = 12, Padding = new Thickness(8, 2) };
        ToolTip.SetTip(server, "Go back to the backgrounds the server picked (they come back with the next update)");
        server.Click += (_, _) => ResetLoginScenes();
        CheckBox overlay = new() { Content = "Preview with the login buttons", FontSize = 12, IsChecked = _sceneOverlayOn };
        _sceneOverlayBox = overlay;
        ToolTip.SetTip(overlay, "Shows the pictures with the login screen's buttons and boxes on top, as you'll see them in the game");
        overlay.IsCheckedChanged += (_, _) =>
        {
            _sceneOverlayOn = overlay.IsChecked == true;
            foreach (Control layer in _sceneOverlays) layer.IsVisible = _sceneOverlayOn;
        };
        buttons.Children.Add(all);
        buttons.Children.Add(server);
        buttons.Children.Add(overlay);
        _sceneButtons = buttons;
        target.Children.Add(buttons);
        target.Children.Add(_scenePanel);   // the pictures under the buttons
    }

    int _sceneLoad;

    void LoadLoginScenes() => _ = LoadLoginScenesAsync();

    /// <summary>The scenes of the installed game's loginui.lua, or of the server's copy when no game is installed yet,
    /// each with its picture when the server has one.</summary>
    async Task LoadLoginScenesAsync()
    {
        if (_scenePanel is null) return;
        int ticket = ++_sceneLoad;
        bool local = GameFolder.LooksLikeClient(Folder);
        IReadOnlyList<LoginScene> scenes = local ? LoginScreens.Read(Folder) : [];
        if (scenes.Count == 0 && _server is not null
            && _manifest?.Files.FirstOrDefault(f => f.Path.Equals(LoginScreens.FilePath, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            try
            {
                using ServerClient client = NewClient();
                byte[]? bytes = await client.GetBytesAsync(file.Url);
                if (bytes is not null) scenes = LoginScreens.Parse(System.Text.Encoding.UTF8.GetString(bytes).Split('\n'));
            }
            catch (Exception error) { Logger.Warning("Couldn't read the server's login screens", error); }
        }
        if (ticket != _sceneLoad) return;
        LoadCharacterScenePictures();
        if (!local && _settings.LoginScenes is { Count: > 0 } mine)
            scenes = [.. scenes.Select(x => x with { On = mine.Contains(x.Code) })];
        _scenePanel.Children.Clear();
        _sceneBoxes.Clear();
        _sceneOverlays.Clear();
        if (_sceneButtons is not null) _sceneButtons.IsVisible = scenes.Count > 0;
        if (scenes.Count == 0)
        {
            _scenePanel.Children.Add(new TextBlock { Text = local
                ? "The list of backgrounds comes with the next update."
                : "Connect to the server to see the backgrounds.", Classes = { "dim" }, FontSize = 12 });
            return;
        }
        BuildSceneGroups(scenes);
        if (!local)
            _scenePanel.Children.Add(new TextBlock { Text = "No game installed yet: your picks are used when it is.", Classes = { "dim" },
                                                     FontSize = 12, Width = 600, TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>The player's scenes into loginui.lua; the file still counts as the server's (it isn't downloaded again).</summary>
    void ApplyLoginScenes(string folder)
    {
        ApplyLoginMusic(folder);   // the update may have put a fresh loginui.lua in place
        if (_settings.LoginScenes is not { Count: > 0 } scenes) return;
        try
        {
            if (!LoginScreens.Write(folder, scenes)) return;
            if (_manifest?.Files.FirstOrDefault(f => f.Path.Equals(LoginScreens.FilePath, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                HashCache cache = HashCache.Load(_cacheFile);
                cache.Remember(LoginScreens.PathIn(folder), file.Sha256);
                cache.Save();
            }
            Log($"Login screen scenes set: {string.Join(", ", scenes)}");
        }
        catch (Exception error) { Logger.Warning("Couldn't set the login screen", error); }
    }

    void ResetLoginScenes()
    {
        _settings.LoginScenes = null;
        SaveSettings();
        HashCache cache = HashCache.Load(_cacheFile);
        cache.Forget(LoginScreens.PathIn(Folder));
        cache.Save();
        _pendingFiles = Math.Max(_pendingFiles, 1);
        ShowPlayState();
        Status(Translation.T("The server's login screens come back with the next update."));
    }

    void LoadTweaks()
    {
        TweaksHint.IsVisible = !GameFolder.LooksLikeClient(Folder);
        PresetChanges.IsVisible = false;
        LoadLoginScenes();
        if (TweaksHint.IsVisible) return;
        ConfigWtf config = ConfigWtf.Load(Folder);
        DxvkConf dxvk = DxvkConf.Load(Folder);
        _fillingTweaks = true;
        foreach (Tweak t in Tweaks.All)
            SetTweak(t.CVar, (t.File == "dxvk" ? dxvk.Get(t.CVar) : config.Get(t.CVar)) ?? t.Default);
        _fillingTweaks = false;
        foreach (string cvar in _serverTweaks)
            SetTweak(cvar, config.Get(cvar) ?? TweakRule(cvar)?.CVarText ?? "");
        if (_laaBox is not null)
        {
            // no readable Wow.exe (not installed yet, or not a Windows program): the box waits, quietly
            try { _laaBox.IsChecked = LargeAddressAware.IsEnabled(GameFolder.ExePath(Folder)); _laaBox.IsEnabled = true; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _laaBox.IsEnabled = false;
                ToolTip.SetTip(_laaBox, "Needs the game's Wow.exe: " + e.Message);
                Logger.Debug("Large Address Aware: " + e.Message);
            }
        }
        // a fresh game (none of these set yet) starts on the server's recommendation, or the defaults; a game with its
        // own settings keeps them and shows whichever preset they match
        _savedBoxes = CurrentBoxes();   // what the game has now: Apply / Discard show when the boxes differ
        bool fresh = Tweaks.All.Where(t => t.File == "config" && t.Requires is null).All(t => config.Get(t.CVar) is null);
        if (fresh && !_settings.TweakPresetChosen) FillPreset(_policy.Tweaks.Count > 0 ? "server" : "default");
        LockEnforcedTweaks();
        MarkPreset(MatchingPreset());
        FillOptionsFor();
        LoadGameOptions();
        LoadBindings();
        LoadMacros();
        UpdateTweaksDirty();
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

    /// <summary>Each box's value now, by name, to show what a preset changes.</summary>
    Dictionary<string, string> CurrentBoxes()
    {
        Dictionary<string, string> values = [];
        foreach (Tweak t in Tweaks.All)
            if (t.Requires is null || OptionOn(t.Requires))
            {
                string value = BoxValue(t, _tweakControls.GetValueOrDefault(t.CVar)) ?? "";
                values[t.Name] = t.Kind == TweakKind.Toggle ? (value == t.On ? "on" : "off") : value;
            }
        foreach (string cvar in _serverTweaks)
            if (_tweakControls.GetValueOrDefault(cvar) is TextBox box) values[cvar] = box.Text?.Trim() ?? "";
        if (_laaBox is not null) values["Large Address Aware"] = _laaBox.IsChecked == true ? "on" : "off";
        return values;
    }

    static string Shown(string value) => value.Length == 0 ? "-" : value;

    void FillPreset(string preset)
    {
        Dictionary<string, string> before = CurrentBoxes();
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
        // what the preset changed, until Save (or the next preset)
        // each change as: Name: `old` → `new`
        List<(string Name, string Old, string New)> changes = CurrentBoxes()
            .Where(v => before.TryGetValue(v.Key, out string? old) && old != v.Value)
            .Select(v => (Translation.T(v.Key), Translation.T(Shown(before[v.Key])), Translation.T(Shown(v.Value)))).ToList();
        PresetChanges.Inlines?.Clear();
        PresetChanges.Text = null;
        if (changes.Count == 0) PresetChanges.Text = Translation.T("Nothing changes: your settings already match.");
        else
        {
            FontFamily mono = new("Consolas, Cascadia Mono, monospace");
            IBrush? field = (IBrush?)this.FindResource("T.field"), soft = (IBrush?)this.FindResource("T.textSoft"),
                    accent = (IBrush?)this.FindResource("T.accent");
            Avalonia.Controls.Documents.InlineCollection inlines = [];
            inlines.Add(new Avalonia.Controls.Documents.Run(Translation.T("Press Apply to keep these changes:")));
            foreach ((string name, string oldValue, string newValue) in changes)
            {
                inlines.Add(new Avalonia.Controls.Documents.LineBreak());
                inlines.Add(new Avalonia.Controls.Documents.Run($"{name}: "));
                inlines.Add(new Avalonia.Controls.Documents.Run($" {oldValue} ") { FontFamily = mono, Background = field, Foreground = soft });
                inlines.Add(new Avalonia.Controls.Documents.Run(" → "));
                inlines.Add(new Avalonia.Controls.Documents.Run($" {newValue} ") { FontFamily = mono, Background = field, Foreground = accent, FontWeight = FontWeight.SemiBold });
            }
            PresetChanges.Inlines = inlines;
        }
        PresetChanges.IsVisible = true;
        UpdateTweaksDirty();
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
        UpdateTweaksDirty();
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
            SaveGameOptions();   // the game's own options and keys, before anything else can fail
            SaveBindings();
            SaveMacros();
            // a Wow.exe that can't be read must not stop the rest of the settings from being saved
            if (_laaBox is not null)
                try { LargeAddressAware.Set(GameFolder.ExePath(Folder), _laaBox.IsChecked == true); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                { Logger.Warning("Large Address Aware wasn't changed", error); }
            LoadTweaks();
            PresetChanges.IsVisible = false;
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

    string _catalogCategory = "";   // "" = all, "\u0001" = needed only
    bool _catalogHooked;

    static string LetterOf(string name)
    {
        char c = name.TrimStart('_', '!', ' ', '-').FirstOrDefault();
        return char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : "#";
    }

    /// <summary>The server's addons: a search box, category buttons with counts, needed addons first, then the rest
    /// under letter headings.</summary>
    void RefreshCatalog()
    {
        RefreshInstallRecommended();
        if (!_catalogHooked)
        {
            _catalogHooked = true;
            CatalogSearch.TextChanged += (_, _) => RefreshCatalog();
        }
        CatalogList.Children.Clear();
        CatalogCategories.Children.Clear();
        CatalogTitle.Text = $"FROM THE SERVER ({_catalog.Count})";
        if (_catalog.Count == 0)
        {
            CatalogList.Children.Add(new TextBlock { Text = "The server lists no addons (addons.json).", Classes = { "dim" } });
            return;
        }
        string query = CatalogSearch.Text?.Trim() ?? "";
        bool Matches(CatalogAddon a) => query.Length == 0
            || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || a.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || a.Category.Contains(query, StringComparison.OrdinalIgnoreCase);
        List<CatalogAddon> found = [.. _catalog.Where(Matches)];

        void AddCategory(string key, string label, int count)
        {
            Button b = new() { Content = $"{label} ({count})", FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 6),
                               Classes = { "preset" } };
            if (_catalogCategory == key) b.Classes.Add("active");
            b.Click += (_, _) => { _catalogCategory = _catalogCategory == key ? "" : key; RefreshCatalog(); };
            CatalogCategories.Children.Add(b);
        }
        AddCategory("", "All", found.Count);
        if (found.Any(a => a.Required)) AddCategory("\u0001", "Needed", found.Count(a => a.Required));
        if (found.Any(a => a.IsTemucraft)) AddCategory("\u0003", "Temucraft", found.Count(a => a.IsTemucraft));
        if (found.Any(a => a.Recommended)) AddCategory("\u0002", "Recommended", found.Count(a => a.Recommended));
        foreach (var group in found.Where(a => a.Category.Length > 0).GroupBy(a => a.Category, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            AddCategory(group.Key, group.Key, group.Count());

        List<CatalogAddon> shown = [.. found.Where(a => _catalogCategory.Length == 0
            || (_catalogCategory == "\u0001" ? a.Required : _catalogCategory == "\u0002" ? a.Recommended : _catalogCategory == "\u0003" ? a.IsTemucraft
                : a.Category.Equals(_catalogCategory, StringComparison.OrdinalIgnoreCase)))];
        if (shown.Count == 0)
        {
            CatalogList.Children.Add(new TextBlock { Text = "No addons match.", Classes = { "dim" } });
            return;
        }
        string heading = "";
        // needed first, then ours (made or changed for 3.3.5 / Temucraft), then A to Z
        string GroupOf(CatalogAddon a) => a.Required ? "Needed" : a.IsTemucraft ? "Temucraft" : LetterOf(a.Name);
        foreach (CatalogAddon addon in shown.OrderByDescending(a => a.Required).ThenByDescending(a => a.IsTemucraft)
                     .ThenBy(a => a.Name.TrimStart('_', '!', ' ', '-'), StringComparer.OrdinalIgnoreCase))
        {
            string group = GroupOf(addon);
            if (group != heading)
            {
                heading = group;
                int count = shown.Count(a => GroupOf(a) == group);
                CatalogList.Children.Add(new TextBlock { Text = $"{group}  ({count})", Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) });
            }
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("2*,3*,Auto"), Margin = new Thickness(0, 2), Background = Brushes.Transparent };
            // the Temucraft label first, then the name (shortened with … when the column is narrow)
            Grid name = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            string tag = addon.Required ? "needed" : addon.Recommended ? "recommended" : addon.Default ? "default" : "";
            TextBlock title = new()
            {
                FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Classes = { addon.Required ? "good" : "text" }, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.Inlines = [new Avalonia.Controls.Documents.Run(addon.Name)];
            if (tag.Length > 0)
                title.Inlines.Add(new Avalonia.Controls.Documents.Run($"  ({tag})")
                {
                    // recommended in green; default dimmed
                    Foreground = (IBrush?)this.FindResource(tag == "default" ? "T.dim" : "T.good"), FontWeight = FontWeight.Normal,
                });
            ToolTip.SetTip(title, addon.Name + (tag.Length > 0 ? $" ({tag})" : ""));
            // the label only where the Temucraft heading doesn't already say it (a needed addon, listed under Needed)
            if (addon.IsTemucraft && GroupOf(addon) != "Temucraft")
            {
                Border badge = new()
                {
                    Background = (IBrush?)this.FindResource("T.accent"), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = "TEMUCRAFT", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.Black },
                };
                ToolTip.SetTip(badge, "Made or changed for 3.3.5 and Temucraft");
                name.Children.Add(badge);
            }
            Grid.SetColumn(title, 1);
            name.Children.Add(title);
            row.Children.Add(name);
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

    /// <summary>The recommended addons from the server's list that aren't in the game folder yet.</summary>
    List<CatalogAddon> MissingRecommended(string folder) =>
        GameFolder.LooksLikeClient(folder) ? [.. _catalog.Where(a => a.Recommended && !IsInstalled(a, folder))] : [];

    /// <summary>"Install recommended" is on while a recommended addon is missing; its tip names them.</summary>
    void RefreshInstallRecommended()
    {
        List<CatalogAddon> missing = MissingRecommended(Folder);
        InstallRecommendedButton.IsEnabled = missing.Count > 0;
        ToolTip.SetTip(InstallRecommendedButton, missing.Count > 0
            ? string.Format(Translation.T("Installs: {0}"), string.Join(", ", missing.Select(a => a.Name)))
            : _catalog.Any(a => a.Recommended) ? Translation.T("Every recommended addon is installed.") : Translation.T("The server recommends no addons."));
    }

    async void OnInstallRecommended(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        string folder = Folder;
        List<CatalogAddon> missing = MissingRecommended(folder);
        if (missing.Count == 0)
        {
            Status("Every recommended addon is installed.");
            RefreshCatalog();
            return;
        }
        InstallRecommendedButton.IsEnabled = false;
        await RunAsync("Recommended addons", async token =>
        {
            // one after another, with the same progress and messages as each addon's own Install button
            foreach (CatalogAddon addon in missing)
                await InstallCatalogAsync(addon, folder, token);
            Status(string.Format(Translation.T("Installed the recommended addons: {0}."), string.Join(", ", missing.Select(a => a.Name))));
        });
        RefreshInstallRecommended();
    }

    void OnClearKept(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        long size = KeptFiles.Size(Folder);
        KeptFiles.Clear(Folder);
        Log($"Removed kept option files ({Words.Size(size)})");
        Status(string.Format(Translation.T("Removed kept option files ({0})."), Words.Size(size)));
    }

    void OnOpenGameFolder(object? sender, RoutedEventArgs e)
    {
        // works before the game is installed too: the folder is made if it isn't there yet
        if (Folder.Length == 0)
        {
            Status("Pick your WoW folder under Game files, or an empty folder to install the game into.");
            return;
        }
        Log($"Opened the game folder: {Folder}");
        OpenFolder(Folder);
    }

    void OnOpenAddons(object? sender, RoutedEventArgs e)
    {
        if (!NeedFolder()) OpenFolder(AddonFolder.Path(Folder));
    }

    // ------------------------------------------------------------------------------------------- mods / options
    void RefreshMods()
    {
        OptionsList.Children.Clear();
        _previewImages.Clear();
        _previewTiles.Clear();
        if (_manifest is null || _manifest.OptionGroups.Count == 0)
            OptionsList.Children.Add(new TextBlock { Text = "The server offers no cosmetic patches (or the launcher hasn't connected yet).", Classes = { "dim" } });
        else
        {
            // one preset for everything first (Classic | HD | Custom), as wide as the page
            Control presets = PresetBar();
            if (presets is Layoutable l) l.Width = Math.Max(200, OptionsList.Bounds.Width - 12);
            OptionsList.Children.Add(presets);
        }
        // cards under their category's heading (Characters, World, Interface, Performance and fixes, then others, Other last)
        string[] categoryOrder = ["Characters", "World", "Interface", "Performance and fixes"];
        int Rank(OptionGroup g) => g.Category.Length == 0 ? 1000
            : Array.IndexOf(categoryOrder, g.Category) is int i and >= 0 ? i
            : 100 + (_manifest?.OptionGroups ?? []).FindIndex(x => x.Category == g.Category);
        List<OptionGroup> ordered = [.. (_manifest?.OptionGroups ?? []).OrderBy(Rank)];
        string? heading = null;
        foreach (OptionGroup group in ordered)
        {
            string category = group.Category.Length > 0 ? group.Category : "Other";
            if (ordered.Any(g => g.Category.Length > 0) && category != heading)
            {
                heading = category;
                using (Look.Untranslated())
                    OptionsList.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "section" },
                                                             Width = Math.Max(200, OptionsList.Bounds.Width - 12), Margin = new Thickness(0, 6, 0, 8) });
            }
            // a card per group, in a grid that fills the width
            StackPanel panel = new() { Spacing = 6 };
            Border card = new()
            {
                Child = panel, Width = OptionCardWidth(), MinHeight = 120, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12),
                Classes = { "card" },
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            };
            using (Look.Untranslated())   // the server's names and descriptions stay as it wrote them
            {
                panel.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 });
                if (group.Description.Length > 0)
                    panel.Children.Add(ShortDescription(group.Description));
            }
            // how big each choice is ("On" / "Off" groups: only the size of On)
            if (group.Recommended.Length > 0)
                panel.Children.Add(new TextBlock { Text = string.Format(Translation.T("Recommended: {0}"), group.Recommended), Classes = { "dim" }, FontSize = 11 });
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
            // which choices are already on this PC: switching between those is instant
            if (_manifest!.Files.Any(f => f.Group == group.Name) && group.Options.Count >= 2
                && !group.Options.SequenceEqual(["On", "Off"], StringComparer.OrdinalIgnoreCase))
            {
                // choices with files of their own that are on this PC (Classic has none, so it only counts beside HD)
                List<string> onDisk = OptionsOnDisk(group);
                bool anyDownloaded = onDisk.Any(o => _manifest.Files.Any(f => f.Group == group.Name && f.Option == o));
                if (anyDownloaded && onDisk.Count >= 2)
                    panel.Children.Add(new TextBlock
                    {
                        Classes = { "good" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                        Text = $"Installed: {string.Join(", ", onDisk)}. Switch between them any time: no download, no UPDATE.",
                    });
            }
            bool met = UpdatePlanner.RequirementsMet(_manifest!, _settings.Options, group);
            bool locked = group.Enforce || !met;

            void Pick(string value, string shown)
            {
                _settings.Options[group.Name] = value;
                SaveSettings();
                if (PresetGroups().Contains(group)) Dispatcher.UIThread.Post(RefreshMods);   // the preset may turn Custom
                if (group.Previews.Count > 0) { _ = ShowPreviewAsync(group, value); MarkPicked(group.Name, value); }
                // groups that need this one turn usable (or not)
                if (_manifest!.OptionGroups.Any(g => g.Requires.ContainsKey(group.Name)))
                    Dispatcher.UIThread.Post(RefreshMods);
                ShowGatedTweaks();
                if (!_manifest.Files.Any(f => f.Group == group.Name))
                {
                    // a choice without files (Character screens) is a setting: it takes effect now, nothing to download
                    ApplyCharacterScenes(Folder);
                    RefreshCharacterScenes();
                    Status($"{group.Name}: {shown}.");
                    return;
                }
                if (value.Length > 0 && OptionOnDisk(group, value) || value.Length == 0)
                {
                    // already on this PC (installed before, kept aside since): switch now, no download, no UPDATE
                    Status($"{group.Name}: switching to {shown}…");
                    _ = Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (await SwapOptionAsync(group, value))
                        {
                            Status($"{group.Name}: {shown}. Switched (already on this PC, nothing to download).");
                            if (group.Name == ModelsGroup) await SyncHdModelsAsync(Folder, askToCloseGame: false);
                            RefreshMods();
                        }
                        else
                        {
                            _pendingFiles = Math.Max(_pendingFiles, 1);
                            ShowPlayState();
                            Status($"{group.Name}: {shown}. Press UPDATE to install it.");
                        }
                    });
                    return;
                }
                _pendingFiles = Math.Max(_pendingFiles, 1);
                ShowPlayState();
                Status($"{group.Name}: {shown}. Press UPDATE to install it.");
            }

            // on/off groups ("On"/"Off", or one option that may be left out) get a switch; real choices a dropdown
            bool onOff = group.Options.SequenceEqual(["On", "Off"], StringComparer.OrdinalIgnoreCase);
            bool single = group.Optional && group.Options.Count == 1;
            Control control;
            Action<string>? choose = null;   // picks a choice as if the switch or the dropdown had been used
            if (onOff || single)
            {
                string on = onOff ? group.Options[0] : group.Options[0], off = onOff ? group.Options[1] : "";
                ToggleSwitch toggle = new()
                {
                    IsChecked = string.Equals(chosen, on, StringComparison.OrdinalIgnoreCase),
                    OnContent = single ? on : "On", OffContent = "Off", IsEnabled = !locked,
                };
                toggle.IsCheckedChanged += (_, _) => Pick(toggle.IsChecked == true ? on : off, toggle.IsChecked == true ? "on" : "off");
                choose = v => toggle.IsChecked = string.Equals(v, on, StringComparison.OrdinalIgnoreCase);
                control = toggle;
            }
            else
            {
                List<string> items = [.. (group.Optional ? new[] { "(none)" } : []), .. group.Options];
                ComboBox box = new() { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !locked };
                box.SelectedItem = chosen ?? (group.Optional ? "(none)" : items.FirstOrDefault());
                box.SelectionChanged += (_, _) =>
                {
                    string picked = box.SelectedItem as string ?? "";
                    Pick(picked == "(none)" ? "" : picked, picked);
                };
                choose = v => box.SelectedItem = v.Length == 0 ? "(none)" : v;
                control = box;
            }
            if (group.Enforce) ToolTip.SetTip(control, "Set by the server");
            if (group.Name == ModelsGroup) panel.Children.Add(HdRacePanel(group));
            if (group.Name == "Character screens")
            {
                // the character screens: each race's scene big, Classic or HD (starts on the choice shown in the box)
                Button look = new() { Content = "Preview", FontSize = 12 };
                OptionGroup screens = group;
                look.Click += async (_, _) => await OpenCharacterScenePreviewAsync(
                    CharacterScenes.ModeOf(UpdatePlanner.ChosenOption(_manifest!, _settings.Options, screens.Name)));
                panel.Children.Add(look);
            }
            if (group.Gallery.Count > 0)
            {
                // more pictures of the choices (character screens for Character models, for example)
                Button more = new() { Content = group.GalleryTitle.Length > 0 ? group.GalleryTitle : "More pictures", FontSize = 12 };
                OptionGroup mine = group;
                more.Click += async (_, _) => await new OptionGalleryWindow(mine, UpdatePlanner.ChosenOption(_manifest!, _settings.Options, mine.Name), PictureAsync).ShowDialog(this);
                panel.Children.Add(more);
            }
            if (group.Previews.Count > 0)
            {
                // a picture of each choice side by side (Off and On for a switch): click one to pick it, hover for a bigger one
                List<(string Value, string Label)> choices = onOff ? [(group.Options[1], "Off"), (group.Options[0], "On")]
                    : single ? [("", "Off"), (group.Options[0], "On")]
                    : [.. (group.Optional ? new[] { ("", "(none)") } : []), .. group.Options.Select(o => (o, o))];
                List<(string Value, string Label, string Url)> pictured = [];
                foreach ((string value, string label) in choices)
                    if (PreviewUrl(group, value) is { } url) pictured.Add((value, label, url));
                if (pictured.Count < 2)
                {
                    // one picture only (of the group, or of one choice)
                    Image picture = new() { MaxHeight = 150, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                    panel.Children.Add(picture);
                    _previewImages[group.Name] = picture;
                    _ = ShowPreviewAsync(group, chosen);
                }
                else
                {
                    // the choices side by side (up to three a row), each picture as wide as its column
                    Avalonia.Controls.Primitives.UniformGrid tiles = new() { Columns = Math.Min(pictured.Count, 3) };
                    List<(string Value, Border Frame)> frames = [];
                    foreach ((string value, string label, string url) in pictured)
                    {
                        Image picture = new() { Stretch = Stretch.Uniform, IsVisible = false };
                        StackPanel inner = new() { Spacing = 2 };
                        inner.Children.Add(picture);
                        using (Look.Untranslated()) inner.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                        Border frame = new() { Child = inner, Classes = { "tile" }, Margin = new Thickness(0, 0, 4, 4),
                                               Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                        frame.PointerPressed += (_, _) => { if (!locked) choose?.Invoke(value); };
                        frames.Add((value, frame));
                        tiles.Children.Add(frame);
                        _ = SetPictureAsync(picture, url);
                    }
                    _previewTiles[group.Name] = frames;
                    panel.Children.Add(tiles);
                    MarkPicked(group.Name, chosen);
                }
            }
            panel.Children.Add(control);
            if (!met)
                panel.Children.Add(new TextBlock
                {
                    Text = "Needs " + string.Join(", ", group.Requires.Select(r => $"{r.Key}: {r.Value}")) + " first.",
                    Classes = { "accent" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
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

    readonly Dictionary<string, Image> _previewImages = [];
    readonly Dictionary<string, Bitmap?> _previewCache = [];
    readonly Dictionary<string, List<(string Value, Border Frame)>> _previewTiles = [];

    /// <summary>The picture of one choice: "" (off or none) also goes by "Off" or "none".</summary>
    static string? PreviewUrl(OptionGroup group, string value)
    {
        IEnumerable<string> keys = value.Length == 0 || value.Equals("Off", StringComparison.OrdinalIgnoreCase)
            ? ["Off", "off", "none", "None", value] : [value];
        foreach (string key in keys)
            if (group.Previews.TryGetValue(key, out string? url)) return url;
        return group.Previews.FirstOrDefault(p => p.Key.Equals(value, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Frames the picture of the picked choice.</summary>
    void MarkPicked(string group, string? chosen)
    {
        if (!_previewTiles.TryGetValue(group, out var frames)) return;
        static bool Off(string? v) => string.IsNullOrEmpty(v) || v.Equals("Off", StringComparison.OrdinalIgnoreCase);
        foreach ((string value, Border frame) in frames)
            frame.Classes.Set("picked", Off(value) ? Off(chosen) : string.Equals(value, chosen, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A picture from the server, loaded once.</summary>
    async Task<Bitmap?> PictureAsync(string url)
    {
        if (_previewCache.TryGetValue(url, out Bitmap? cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            using ServerClient client = NewClient();
            byte[]? bytes = await client.GetBytesAsync(url);
            bitmap = bytes is null ? null : new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception error) { Logger.Warning($"Couldn't load the picture {url}", error); }
        _previewCache[url] = bitmap;
        return bitmap;
    }

    async Task SetPictureAsync(Image picture, string url, bool withOverlay = false)
    {
        if (_server is null) return;
        Bitmap? bitmap = await PictureAsync(url);
        picture.Source = bitmap;
        picture.IsVisible = bitmap is not null;
        if (bitmap is null) return;
        if (!withOverlay)
        {
            ToolTip.SetTip(picture, new Image { Source = bitmap, MaxWidth = 900, MaxHeight = 600, Stretch = Stretch.Uniform });
            return;
        }
        // the big picture on hover, with the login buttons on top when "Preview with the login buttons" is ticked
        Grid big = new() { Width = 800, Height = 450 };
        big.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        Control layer = LoginButtonsOverlay.Build();
        layer.IsVisible = _sceneOverlayOn;
        _sceneOverlays.Add(layer);
        big.Children.Add(layer);
        ToolTip.SetTip(picture, big);
    }

    /// <summary>The preview picture for an option (its own, else the group's), loaded once from the server.</summary>
    async Task ShowPreviewAsync(OptionGroup group, string? option)
    {
        if (!_previewImages.TryGetValue(group.Name, out Image? picture)) return;
        string? url = option is not null && group.Previews.TryGetValue(option, out string? own) ? own
                    : group.Previews.TryGetValue("", out string? all) ? all
                    : group.Previews.Values.FirstOrDefault();
        if (url is null || _server is null) { picture.IsVisible = false; return; }
        if (!_previewCache.TryGetValue(url, out Bitmap? bitmap))
        {
            try
            {
                using ServerClient client = NewClient();
                byte[]? bytes = await client.GetBytesAsync(url);
                bitmap = bytes is null ? null : new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception error) { Logger.Warning($"Couldn't load the preview of {group.Name}", error); bitmap = null; }
            _previewCache[url] = bitmap;
        }
        picture.Source = bitmap;
        picture.IsVisible = bitmap is not null;
        if (bitmap is not null)
            ToolTip.SetTip(picture, new Image { Source = bitmap, MaxWidth = 900, MaxHeight = 600, Stretch = Stretch.Uniform });
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

    void OnUntracked(object? sender, RoutedEventArgs e)
    {
        // the list is under the mod cards: open the page and scroll down to it (also when the page is already open)
        OnTab(ModsTab, e);
        Dispatcher.UIThread.Post(() => UntrackedList.BringIntoView(), DispatcherPriority.Background);
    }

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
        IReadOnlyList<Backups.Backup> list = Backups.List();
        BackupList.ItemsSource = list;
        BackupList.SelectedIndex = BackupList.ItemCount > 0 ? 0 : -1;
        try
        {
            (long zips, long moved) = Backups.Sizes();
            BackupTotal.Text = $"Total: {Words.Size(zips + moved)} ({list.Count} backup{(list.Count == 1 ? "" : "s")}: {Words.Size(zips)}"
                               + (moved > 0 ? $"; patches moved out of the game: {Words.Size(moved)}" : "") + ")";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { BackupTotal.Text = ""; }
    }

    async void OnDeleteBackup(object? sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not Backups.Backup backup) return;
        if (!await ConfirmAsync("Delete this backup?", $"{backup}\n\nIt can't be brought back.", "Delete")) return;
        try { Backups.Delete(backup.File); Log($"Deleted the backup {backup.File}"); Status("Backup deleted."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Logger.Warning("Couldn't delete the backup", error); }
        RefreshBackups();
    }

    async void OnDeleteAllBackups(object? sender, RoutedEventArgs e)
    {
        (long zips, long moved) = Backups.Sizes();
        if (zips + moved == 0) { Status("There are no backups."); return; }
        if (!await ConfirmAsync("Delete all backups?", $"Every backup and the patches moved out of the game ({Words.Size(zips + moved)}) are deleted. They can't be brought back.", "Delete all")) return;
        try { Backups.DeleteAll(); Log("Deleted all backups"); Status($"Deleted all backups ({Words.Size(zips + moved)} freed)."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Logger.Warning("Couldn't delete the backups", error); }
        RefreshBackups();
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

    void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        if (Logger.File is { } log && File.Exists(log) && OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"") { UseShellExecute = true });
        else OpenFolder(Logger.File is { } file ? Path.GetDirectoryName(file) ?? UserSettings.DefaultFolder : UserSettings.DefaultFolder);
    }

    void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (Logger.File is { } log && File.Exists(log)) OpenUrl(log);
        else OpenFolder(UserSettings.DefaultFolder);
    }
}
