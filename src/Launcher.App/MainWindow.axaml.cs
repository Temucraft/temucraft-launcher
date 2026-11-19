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
    readonly string _settingsFile = Path.Combine(UserSettings.DefaultFolder, "settings.json");
    readonly string _cacheFile = Path.Combine(UserSettings.DefaultFolder, "hash-cache.json");
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
        WowFolderBox.Text = _settings.WowFolder;
        WowFolderBox.TextChanged += (_, _) => { ShowPlayState(); LoadTweaks(); };
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
        BuildTweaks();
        RefreshInstalledAddons();
        RefreshBackups();
        ShowPlayState();
        // closing must never fail: anything that goes wrong while saving is logged and the window still closes
        Closing += (_, _) => { SaveSettings(); _work?.Cancel(); };
        Closed += (_, _) => { Logger.Written -= ShowLogLine; Logger.Info("Launcher closed", toWindow: false); };
        Opened += async (_, _) =>
        {
            LauncherVersionText.Text = $"Temucraft Launcher {LauncherInfo.Version}";
            ShowPrivacy();
            // with a login, connect right away: news, addons and options show even before a game is installed
            // (nothing is downloaded until Update)
            if (_server is null)
                Status("launcher.env is missing next to the launcher: get it from the server's owner.");
            else if (HaveLogin && Agreed)
                await CheckAsync(download: false);
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
    }

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
        IReadOnlyList<string> required = _policy.RequiredLocales;
        LocalePanel.Children.Clear();
        foreach (string locale in available.Concat(chosen).Concat(required).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool must = required.Contains(locale, StringComparer.OrdinalIgnoreCase);
            CheckBox box = new()
            {
                Content = locale,
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = must || chosen.Contains(locale, StringComparer.OrdinalIgnoreCase),
                IsEnabled = !must,
            };
            if (must) ToolTip.SetTip(box, "Everyone gets this one (set by the server)");
            LocalePanel.Children.Add(box);
        }
    }

    List<string> SelectedLocales() => LocalePanel.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true).Select(box => box.Content!.ToString()!).ToList();

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
            ? "Pick your WoW folder under Settings, or an empty folder to install the game into."
            : "There's no game in your WoW folder yet: press INSTALL to download it there.");
        return true;
    }

    /// <summary>A server connection with the login typed in the window (call on the window's thread).</summary>
    ServerClient NewClient() => new(_server!, UsernameBox.Text?.Trim() ?? "", PasswordBox.Text ?? "",
        clientInfo: Agreed ? ClientInfo.Read(_server!.Collect, _settings.InstallId) : null);

    /// <summary>The player agreed to everything this server collects (at the login window); otherwise nothing is sent.</summary>
    bool Agreed => _server is not null && _settings.CollectAgreedServer == _server.ServerUrl.ToString()
                   && _server.Collect.All(k => _settings.CollectAgreed.Contains(k, StringComparer.OrdinalIgnoreCase));

    /// <summary>The login window's notice: what every server gets, what a server may add, what this one adds and why.</summary>
    string CollectText()
    {
        static string Lower(string d) => char.ToLowerInvariant(d[0]) + d[1..];
        IReadOnlyList<string> collect = _server?.Collect ?? ClientInfo.Normalize(null);
        System.Text.StringBuilder text = new();
        if (collect.Count == 0)
            text.AppendLine("This server collects nothing from your PC besides your login.");
        else
        {
            List<string> defaults = ClientInfo.Defaults.Where(collect.Contains).ToList();
            if (defaults.Count > 0)
                text.AppendLine("Every server gets: " + string.Join("; ", defaults.Select(k => Lower(ClientInfo.Describe(k)))) + ".");
            text.AppendLine();
            text.AppendLine("Depending on the server, the launcher may also send: "
                            + string.Join("; ", ClientInfo.Optional.Select(i => Lower(i.Description))) + ".");
            List<string> extra = collect.Where(k => !ClientInfo.Defaults.Contains(k)).ToList();
            text.AppendLine();
            if (extra.Count == 0)
                text.AppendLine("This server collects nothing more.");
            else
            {
                text.AppendLine("This server also collects:");
                foreach (string key in extra) text.AppendLine("  • " + ClientInfo.Describe(key));
            }
            if (_server?.CollectNote.Length > 0) { text.AppendLine(); text.AppendLine(_server.CollectNote); }
        }
        text.AppendLine();
        text.Append("Like any server, it also sees the public IP address (IPv4 or IPv6) you connect from, and it records whether it let you in (its answer code).");
        if (collect.Count > 0)
            text.Append("\n\nBy logging in, you agree to all of the above being sent to this server with every request the launcher makes.");
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
            Logger.Info($"Agreed to send: {(_server.Collect.Count == 0 ? "nothing" : string.Join(", ", _server.Collect))}", toWindow: false);
        }
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
        if (_server is null || _server.Collect.Count == 0) { PrivacyText.Text = "Nothing besides your login."; return; }
        Dictionary<string, string> values = ClientInfo.Read(_server.Collect, _settings.InstallId);
        PrivacyText.Text = string.Join("\n", _server.Collect.Select(k => $"{ClientInfo.Describe(k)}: {values.GetValueOrDefault(k, "(not available)")}"))
                           + (Agreed ? "" : "\n\nNothing is sent until you log in and agree.");
    }

    void OnLoginLater(object? sender, RoutedEventArgs e)
    {
        LoginOverlay.IsVisible = false;
        StatusText.Text = "Not logged in: you can play, but updates need your login (Settings, or press UPDATE).";
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
        IReadOnlyList<NewsItem> news = NewsItem.Parse(await client.GetTextAsync("news.json", token));
        IReadOnlyList<NewsItem> changelog = NewsItem.Parse(await client.GetTextAsync("changelog.json", token));
        string? catalog = await client.GetTextAsync("addons.json", token);
        _catalog = catalog is null ? [] : AddonFolder.ParseCatalog(catalog);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyPolicy(manifest);
            ShowLocales(manifest.Locales);
            ClientButton.IsVisible = manifest.Files.Any(f => f.Category == FileCategory.Base);
            RefreshMods();
            VersionText.Text = manifest.ClientVersion.Length > 0 ? $"Version: {manifest.ClientVersion}" : "";
            WarningBanner.IsVisible = !launcherOk;
            WarningText.Text = launcherOk ? "" :
                "This launcher isn't the one the server hands out (it's older, or it was changed). Download the current launcher from the website.";
            ShowNews(NewsList, news);
            ShowNews(ChangelogList, changelog);
            RefreshCatalog();
            RefreshUntrackedButton();
        });
        if (!launcherOk) Log("Self-check: this launcher differs from the server's copy");
        return manifest;
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

    static void ShowNews(StackPanel list, IReadOnlyList<NewsItem> items)
    {
        list.Children.Clear();
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = "Nothing yet.", Classes = { "dim" } });
        foreach (NewsItem item in items)
        {
            StackPanel card = new() { Spacing = 4 };
            if (item.Date.Length > 0) card.Children.Add(new TextBlock { Text = item.Date, Classes = { "dim" }, FontSize = 12 });
            if (item.Title.Length > 0) card.Children.Add(new TextBlock { Text = item.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F2C14E")), TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new SelectableTextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E8E2D0")) });
            if (Uri.TryCreate(item.Link, UriKind.Absolute, out Uri? link) && link.Scheme is "https" or "http")
            {
                Button open = new() { Content = "Read more", Classes = { "link" } };
                open.Click += (_, _) => OpenUrl(link.ToString());
                card.Children.Add(open);
            }
            list.Children.Add(card);
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
            if (manifest.Rules.RequiredLocales.Count > 0)
                choice = choice with { Locales = choice.Locales.Concat(manifest.Rules.RequiredLocales).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            if (manifest.Enforced("backupBeforeUpdate") is bool backupEnforced) _settings.BackupBeforeUpdate = backupEnforced;
            if (!haveGame)
            {
                // connected, but no game yet: the tabs are filled in; the game files come from Install game files…
                Status(Folder.Length > 0
                    ? "Connected. There's no game in your WoW folder yet: press INSTALL to download it there."
                    : "Connected. Pick your WoW folder under Settings, or an empty folder to install the game into.");
                await Dispatcher.UIThread.InvokeAsync(ShowPlayState);
                await Dispatcher.UIThread.InvokeAsync(RefreshMods);
                return;
            }
            IReadOnlyList<PlannedFile> outdated = await PlanAsync(manifest, choice, token);
            List<CatalogAddon> required = _catalog.Where(a => a.Required && choice.Addons).ToList();
            await RefreshCommitsAsync(required, token);
            List<CatalogAddon> addonUpdates = required.Where(a => NeedsInstall(a, folder)).ToList();
            Logger.Info($"{outdated.Count} file(s) and {addonUpdates.Count} required addon(s) out of date", toWindow: false);

            if (!download)
            {
                long size = outdated.Sum(p => p.File.Size);
                int pending = outdated.Count + addonUpdates.Count;
                Status(pending == 0 ? "Everything up to date!"
                    : $"Update available! {pending} file(s), {size / 1048576.0:F1} MB to download.");
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
        bool install = NoGameYet && _server is not null;
        PlayButton.Content = install ? "INSTALL" : _pendingFiles > 0 ? "UPDATE" : "PLAY";
        PlayButton.Classes.Set("update", install || _pendingFiles > 0);
    }

    async Task InstallAsync(ServerClient client, IReadOnlyList<PlannedFile> outdated, string? wowFolder, CancellationToken token)
    {
        HashCache cache = HashCache.Load(_cacheFile);
        try
        {
            long total = outdated.Sum(p => p.File.Size);
            Log(outdated.Count == 0 ? "All server files are up to date" : $"{outdated.Count} file(s) to install, {total / 1048576.0:F1} MB");
            UpdateInstaller.Result result = await UpdateInstaller.InstallAsync(client, outdated, cache, wowFolder,
                onFile: (planned, before, copy) => Dispatcher.UIThread.Post(() =>
                {
                    Progress.Value = total == 0 ? 1 : (double)before / total;
                    StatusText.Text = copy ? $"Copying {planned.File.Path} (already on your disk)" : $"Downloading {planned.File.Path}";
                }),
                progress: (planned, before) => new UiProgress<(long Done, long Total)>(p =>
                {
                    Progress.Value = total == 0 ? 1 : (double)(before + p.Done) / total;
                    StatusText.Text = $"Downloading {planned.File.Path}  {p.Done / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB";
                }),
                token: token);
            if (result.Copied > 0)
                Log($"{result.Copied} file(s) ({result.BytesCopied / 1048576.0:F1} MB) copied from your own disk instead of downloaded");
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

    async void OnDownloadClient(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for the game files",
        });
        string? folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (folder is null || Busy) return;
        if (await InstallGameFilesAsync(folder)) await CheckAsync(download: true);
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
        if (NoGameYet && _server is not null)
        {
            // an empty folder is picked: the whole game goes there, then the Temucraft files
            if (await InstallGameFilesAsync(Folder)) await CheckAsync(download: true);
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
            Process? game = GameFolder.Start(Folder);
            Log("Started the game");
            if (game is not null && ReopenBox.IsChecked == true)
            {
                Hide();
                await game.WaitForExitAsync();
                Show();
                Activate();
                Log($"The game closed (exit code {game.ExitCode})");
            }
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
        string? section = null;
        foreach (Tweak t in Tweaks.All)
        {
            if (t.Section != section)
            {
                section = t.Section;
                TweaksList.Children.Add(new TextBlock { Text = section.ToUpperInvariant(), Classes = { "section" } });
            }
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("230,110,*"), Margin = new Thickness(0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = t.Name, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(t.Highlighted ? "#8FC24A" : "#E8E2D0")),
            });
            Control control = t.Kind == TweakKind.Toggle
                ? new CheckBox()
                : new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            TextBlock description = new() { Text = t.Description, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(description, 2);
            row.Children.Add(description);
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
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("230,110,*"), Margin = new Thickness(0, 3) };
                row.Children.Add(new TextBlock { Text = cvar, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#E8E2D0")) });
                TextBox box = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
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
        LoadTweaks();
    }

    void LoadTweaks()
    {
        TweaksHint.IsVisible = !GameFolder.LooksLikeClient(Folder);
        if (TweaksHint.IsVisible) return;
        ConfigWtf config = ConfigWtf.Load(Folder);
        foreach (Tweak t in Tweaks.All)
            SetTweak(t.CVar, config.Get(t.CVar) ?? t.Default);
        foreach (string cvar in _serverTweaks)
            SetTweak(cvar, config.Get(cvar) ?? TweakRule(cvar)?.CVarText ?? "");
        try { if (_laaBox is not null) _laaBox.IsChecked = LargeAddressAware.IsEnabled(GameFolder.ExePath(Folder)); }
        catch (Exception e) { Logger.Warning("Large Address Aware", e); }
        LockEnforcedTweaks();
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
            case CheckBox box: box.IsChecked = value == "1"; break;
            case TextBox text: text.Text = value; break;
        }
    }

    void OnSaveTweaks(object? sender, RoutedEventArgs e)
    {
        if (NeedFolder()) return;
        try
        {
            ConfigWtf config = ConfigWtf.Load(Folder);
            foreach (Tweak t in Tweaks.All)
            {
                string? value = _tweakControls[t.CVar] switch
                {
                    CheckBox box => box.IsChecked == true ? "1" : "0",
                    TextBox text when double.TryParse(text.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double number) => Tweaks.Format(Math.Clamp(number, t.Min, t.Max)),
                    _ => null,
                };
                if (value is not null) config.Set(t.CVar, value);
            }
            foreach (string cvar in _serverTweaks)
                if (_tweakControls[cvar] is TextBox { Text: { Length: > 0 } text }) config.Set(cvar, text.Trim());
            _policy.ApplyEnforcedTweaks(config);   // enforced values win, whatever the boxes say
            config.Save();
            if (_laaBox is not null) LargeAddressAware.Set(GameFolder.ExePath(Folder), _laaBox.IsChecked == true);
            LoadTweaks();
            Status("Tweaks saved.");
            Log("Tweaks saved to WTF\\Config.wtf");
        }
        catch (Exception error)
        {
            Logger.Error("Saving tweaks", error);
            Status($"Couldn't save the tweaks: {error.Message} (is the game running?)");
        }
    }

    void OnRecommendedTweaks(object? sender, RoutedEventArgs e)
    {
        // the server's recommendation first, then the launcher's own
        foreach (Tweak t in Tweaks.All) SetTweak(t.CVar, TweakRule(t.CVar)?.CVarText ?? t.Recommended);
        foreach (string cvar in _serverTweaks) SetTweak(cvar, TweakRule(cvar)?.CVarText ?? "");
        if (_laaBox is not null) _laaBox.IsChecked = _policy.Setting("largeAddressAware")?.Bool ?? true;
        LockEnforcedTweaks();
        Status("Recommended values filled in; press Save.");
    }

    void OnDefaultTweaks(object? sender, RoutedEventArgs e)
    {
        foreach (Tweak t in Tweaks.All) SetTweak(t.CVar, t.Default);
        LockEnforcedTweaks();
        Status("Default values filled in; press Save.");
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
                Foreground = new SolidColorBrush(Color.Parse(addon.FromServer ? "#8FC24A" : "#F2C14E")),
                Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            };
            Grid.SetColumn(version, 2);
            row.Children.Add(version);
            Button remove = new() { Content = "Remove", FontSize = 12, IsEnabled = !addon.FromServer };
            remove.Click += (_, _) =>
            {
                if ((string?)remove.Content != "Sure?") { remove.Content = "Sure?"; return; }
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
                Foreground = new SolidColorBrush(Color.Parse(addon.Required ? "#8FC24A" : "#E8E2D0")),
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
            Status(updates == 0 ? "All addons are up to date." : $"{updates} addon update(s) available.");
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
            StackPanel panel = new() { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeight.SemiBold });
            if (group.Description.Length > 0) panel.Children.Add(new TextBlock { Text = group.Description, Classes = { "dim" }, FontSize = 12 });
            List<string> items = [.. (group.Optional ? new[] { "(none)" } : []), .. group.Options];
            ComboBox box = new() { ItemsSource = items, MinWidth = 260, IsEnabled = !group.Enforce };
            if (group.Enforce) ToolTip.SetTip(box, "Set by the server");
            string? chosen = UpdatePlanner.ChosenOption(_manifest!, _settings.Options, group.Name);
            box.SelectedItem = chosen ?? (group.Optional ? "(none)" : items.FirstOrDefault());
            box.SelectionChanged += (_, _) =>
            {
                string picked = box.SelectedItem as string ?? "";
                _settings.Options[group.Name] = picked == "(none)" ? "" : picked;
                SaveSettings();
                _pendingFiles = Math.Max(_pendingFiles, 1);
                ShowPlayState();
                Status($"{group.Name}: {picked}. Press UPDATE to install it.");
            };
            panel.Children.Add(box);
            OptionsList.Children.Add(panel);
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
            row.Children.Add(new TextBlock { Text = file, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#F2C14E")) });
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
        try { Status($"Exported {Backups.ExportMacros(Folder, path)} file(s)."); }
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
        try { Status($"Imported {Backups.ImportMacros(Folder, path)} file(s). Close the game first if it was running."); RefreshBackups(); }
        catch (Exception error) { Status(error.Message); Logger.Error("Importing macros", error); }
    }

    void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (Logger.File is { } log && File.Exists(log)) OpenUrl(log);
        else OpenFolder(UserSettings.DefaultFolder);
    }
}
