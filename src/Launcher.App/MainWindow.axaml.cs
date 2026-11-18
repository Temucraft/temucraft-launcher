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
    readonly string _logFile = Path.Combine(UserSettings.DefaultFolder, "launcher.log");
    readonly UserSettings _settings;
    readonly ServerSettings? _server;
    static readonly HttpClient Web = NewWebClient();
    Manifest? _manifest;
    IReadOnlyList<CatalogAddon> _catalog = [];
    readonly Dictionary<string, string> _latestCommits = new(StringComparer.OrdinalIgnoreCase);
    CatalogAddon? _previewed;
    int _pendingFiles;
    CancellationTokenSource? _work;
    readonly Dictionary<string, Control> _tweakControls = new(StringComparer.OrdinalIgnoreCase);
    CheckBox? _laaBox;

    static HttpClient NewWebClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TemucraftLauncher/1.1");
        return http;
    }

    public MainWindow()
    {
        InitializeComponent();
        _settings = UserSettings.Load(_settingsFile);

        try
        {
            _server = ServerSettings.Load(Path.Combine(AppContext.BaseDirectory, "launcher.env"));
            ServerText.Text = _server.ServerUrl.Host;
        }
        catch (Exception e)
        {
            ServerText.Text = e.Message;
            UpdateButton.IsEnabled = ClientButton.IsEnabled = false;
        }

        UsernameBox.Text = _settings.Username;
        RememberBox.IsChecked = _settings.RememberPassword;
        if (_settings.RememberPassword)
            PasswordBox.Text = CredentialStore.Load();
        RememberBox.IsEnabled = CredentialStore.IsSupported;
        WowFolderBox.Text = _settings.WowFolder;
        AddonsBox.IsChecked = _settings.InstallAddons;
        ClearCacheBox.IsChecked = _settings.ClearCacheOnPlay;
        ReopenBox.IsChecked = _settings.ReopenAfterGame;
        RememberWindowBox.IsChecked = _settings.RememberWindow;
        BackupBox.IsChecked = _settings.BackupBeforeUpdate;
        AccountText.Text = _settings.Username;
        ShowLocales(_settings.Locales);
        ApplyBackground();
        if (_settings.RememberWindow && !double.IsNaN(_settings.WindowX))
        {
            Position = new PixelPoint((int)_settings.WindowX, (int)_settings.WindowY);
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }
        BuildTweaks();
        RefreshInstalledAddons();
        RefreshBackups();
        Closing += (_, _) => SaveSettings();
        Opened += async (_, _) =>
        {
            // with a remembered login, look for updates right away (nothing is downloaded until Update)
            if (_server is not null && !string.IsNullOrEmpty(PasswordBox.Text) && GameFolder.LooksLikeClient(WowFolderBox.Text ?? ""))
                await CheckAsync(download: false);
            else
                Status("Set your account and WoW folder under Settings.");
        };
    }

    // ------------------------------------------------------------------------------------------- basics
    void Log(string line)
    {
        string text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { Directory.CreateDirectory(UserSettings.DefaultFolder); File.AppendAllText(_logFile, text + Environment.NewLine); } catch (IOException) { }
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += text + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }

    void Status(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);

    string Folder => WowFolderBox.Text?.Trim() ?? "";

    void SaveSettings()
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
            CredentialStore.Save(_settings.Username, PasswordBox.Text);
        else if (!_settings.RememberPassword)
            CredentialStore.Delete();
    }

    void OnTab(object? sender, RoutedEventArgs e)
    {
        int page = int.Parse((string)((Button)sender!).Tag!);
        Control[] pages = [NewsPage, TweaksPage, AddonsPage, ModsPage, SettingsPage];
        Button[] tabs = [NewsTab, TweaksTab, AddonsTab, ModsTab, SettingsTab];
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
        LocalePanel.Children.Clear();
        foreach (string locale in available.Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase))
            LocalePanel.Children.Add(new CheckBox
            {
                Content = locale,
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = chosen.Contains(locale, StringComparer.OrdinalIgnoreCase),
            });
    }

    List<string> SelectedLocales() => LocalePanel.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true).Select(box => box.Content!.ToString()!).ToList();

    void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = ClientButton.IsEnabled = !busy && _server is not null;
        PlayButton.IsEnabled = !busy;
        CancelButton.IsVisible = busy;
        if (!busy) Progress.Value = 0;
    }

    async Task<bool> RunAsync(string what, Func<CancellationToken, Task> work)
    {
        SaveSettings();
        SetBusy(true);
        _work = new CancellationTokenSource();
        try
        {
            await Task.Run(() => work(_work.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            Status("Cancelled.");
            Log($"{what}: cancelled");
        }
        catch (Exception e)
        {
            Status(e.Message);
            Log($"{what}: {e.Message}");
        }
        finally
        {
            SetBusy(false);
            _work.Dispose();
            _work = null;
        }
        return false;
    }

    bool NeedFolder()
    {
        if (GameFolder.LooksLikeClient(Folder)) return false;
        Status("Pick your WoW 3.3.5a folder under Settings (the one with Wow.exe and Data).");
        return true;
    }

    ServerClient NewClient() => new(_server!, UsernameBox.Text?.Trim() ?? "", PasswordBox.Text ?? "");

    InstallChoice Choice(List<string> locales) =>
        new(Folder, locales, AddonsBox.IsChecked == true, Options: _settings.Options);

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
            ShowLocales(manifest.Locales);
            ClientButton.IsVisible = manifest.Files.Any(f => f.Category == FileCategory.Base);
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

    async Task<IReadOnlyList<PlannedFile>> PlanAsync(Manifest manifest, CancellationToken token)
    {
        List<string> locales = await Dispatcher.UIThread.InvokeAsync(SelectedLocales);
        IReadOnlyList<PlannedFile> wanted = UpdatePlanner.Wanted(manifest, Choice(locales));
        HashCache cache = HashCache.Load(_cacheFile);
        try { return await UpdatePlanner.OutdatedAsync(wanted, cache, new Progress<string>(Status), token); }
        finally { cache.Save(); }
    }

    /// <summary>Reads the manifest and either only reports what is out of date or installs it.</summary>
    async Task CheckAsync(bool download)
    {
        if (NeedFolder() || _server is null) return;
        await RunAsync(download ? "Update" : "Check", async token =>
        {
            using ServerClient client = NewClient();
            Manifest manifest = await FetchManifestAsync(client, token);
            IReadOnlyList<PlannedFile> outdated = await PlanAsync(manifest, token);
            List<CatalogAddon> required = _catalog.Where(a => a.Required && AddonsWanted()).ToList();
            await RefreshCommitsAsync(required, token);
            List<CatalogAddon> addonUpdates = required.Where(NeedsInstall).ToList();

            if (!download)
            {
                long size = outdated.Sum(p => p.File.Size);
                _pendingFiles = outdated.Count + addonUpdates.Count;
                Status(_pendingFiles == 0 ? "Everything up to date!"
                    : $"Update available! {_pendingFiles} file(s), {size / 1048576.0:F1} MB to download.");
                await Dispatcher.UIThread.InvokeAsync(ShowPlayState);
                return;
            }

            if (_settings.BackupBeforeUpdate && (outdated.Count > 0 || addonUpdates.Count > 0))
            {
                Status("Backing up your settings and addons…");
                Log($"Backup: {Path.GetFileName(Backups.Create(Folder, "before-update", keep: _settings.BackupsToKeep))}");
            }
            foreach (string removed in GameFolder.RemoveObsolete(Folder, manifest.Obsolete))
                Log($"Removed {removed} (no longer on the server)");
            List<string> locales = await Dispatcher.UIThread.InvokeAsync(SelectedLocales);
            foreach (string removed in GameFolder.RemoveObsolete(Folder, UpdatePlanner.Unchosen(manifest, Choice(locales))))
                Log($"Removed {removed} (option not picked)");
            await InstallAsync(client, outdated, token);
            foreach (CatalogAddon addon in addonUpdates) await InstallCatalogAsync(addon, token);
            GameFolder.WriteRealmlist(Folder, manifest.Realmlist);
            _pendingFiles = 0;
            Status("Everything up to date!");
            await Dispatcher.UIThread.InvokeAsync(() => { ShowPlayState(); RefreshInstalledAddons(); RefreshCatalog(); });
        });
    }

    bool AddonsWanted() => Dispatcher.UIThread.CheckAccess() ? AddonsBox.IsChecked == true : _settings.InstallAddons;

    void ShowPlayState()
    {
        PlayButton.Content = _pendingFiles > 0 ? "UPDATE" : "PLAY";
        PlayButton.Classes.Set("update", _pendingFiles > 0);
    }

    async Task InstallAsync(ServerClient client, IReadOnlyList<PlannedFile> outdated, CancellationToken token)
    {
        HashCache cache = HashCache.Load(_cacheFile);
        try
        {
            long total = outdated.Sum(p => p.File.Size), before = 0;
            Log(outdated.Count == 0 ? "All server files are up to date" : $"{outdated.Count} file(s) to download, {total / 1048576.0:F1} MB");
            foreach (PlannedFile planned in outdated)
            {
                long start = before;
                Progress<(long Done, long Total)> progress = new(p =>
                {
                    Progress.Value = total == 0 ? 1 : (double)(start + p.Done) / total;
                    StatusText.Text = $"Downloading {planned.File.Path}  {p.Done / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB";
                });
                await client.DownloadAsync(planned.File, planned.Target, progress, token);
                cache.Remember(planned.Target, planned.File.Sha256);
                before += planned.File.Size;
                Log($"Installed {planned.File.Path}");
            }
        }
        finally
        {
            cache.Save();
        }
    }

    async void OnUpdate(object? sender, RoutedEventArgs e) => await CheckAsync(download: true);

    async void OnVerify(object? sender, RoutedEventArgs e)
    {
        // forget every remembered hash so each file is read again
        if (File.Exists(_cacheFile)) File.Delete(_cacheFile);
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
        if (folder is null) return;

        bool done = await RunAsync("Game files", async token =>
        {
            using ServerClient client = NewClient();
            Manifest manifest = await FetchManifestAsync(client, token);
            if (!manifest.Files.Any(f => f.Category == FileCategory.Base))
                throw new InvalidOperationException("Nothing to install.");
            List<PlannedFile> wanted = UpdatePlanner.Wanted(manifest, new InstallChoice(folder, [], false, folder))
                .Where(p => p.File.Category == FileCategory.Base).ToList();
            HashCache cache = HashCache.Load(_cacheFile);
            IReadOnlyList<PlannedFile> outdated;
            try { outdated = await UpdatePlanner.OutdatedAsync(wanted, cache, new Progress<string>(Status), token); }
            finally { cache.Save(); }
            await InstallAsync(client, outdated, token);
            Status("Game files installed. Press UPDATE to finish.");
        });
        if (done)
        {
            WowFolderBox.Text = folder;
            _pendingFiles = 1;
            ShowPlayState();
        }
    }

    // ------------------------------------------------------------------------------------------- play
    async void OnPlay(object? sender, RoutedEventArgs e)
    {
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
                Log("The game closed");
            }
        }
        catch (Exception error)
        {
            Status(error.Message);
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
            if (!GameFolder.LooksLikeClient(path)) Status("That folder has no Wow.exe and Data folder.");
            else { SaveSettings(); LoadTweaks(); RefreshInstalledAddons(); }
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
        TweaksList.Children.Add(new TextBlock { Text = "CLIENT", Classes = { "section" } });
        _laaBox = new CheckBox { Content = "Large Address Aware — lets the game use 4 GB of memory instead of 2 GB (Wow.exe is backed up as Wow.exe.bak)" };
        TweaksList.Children.Add(_laaBox);
        LoadTweaks();
    }

    void LoadTweaks()
    {
        if (!GameFolder.LooksLikeClient(Folder)) return;
        ConfigWtf config = ConfigWtf.Load(Folder);
        foreach (Tweak t in Tweaks.All)
            SetTweak(t, config.Get(t.CVar) ?? t.Default);
        try { if (_laaBox is not null) _laaBox.IsChecked = LargeAddressAware.IsEnabled(GameFolder.ExePath(Folder)); }
        catch (Exception e) { Log($"Large Address Aware: {e.Message}"); }
    }

    void SetTweak(Tweak t, string value)
    {
        switch (_tweakControls[t.CVar])
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
            config.Save();
            if (_laaBox is not null) LargeAddressAware.Set(GameFolder.ExePath(Folder), _laaBox.IsChecked == true);
            LoadTweaks();
            Status("Tweaks saved.");
            Log("Tweaks saved to WTF\\Config.wtf");
        }
        catch (Exception error)
        {
            Status($"Couldn't save the tweaks: {error.Message} (is the game running?)");
        }
    }

    void OnRecommendedTweaks(object? sender, RoutedEventArgs e)
    {
        foreach (Tweak t in Tweaks.All) SetTweak(t, t.Recommended);
        if (_laaBox is not null) _laaBox.IsChecked = true;
        Status("Recommended values filled in; press Save.");
    }

    void OnDefaultTweaks(object? sender, RoutedEventArgs e)
    {
        foreach (Tweak t in Tweaks.All) SetTweak(t, t.Default);
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
                catch (Exception error) { Status(error.Message); }
            };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            InstalledList.Children.Add(row);
        }
    }

    bool IsInstalled(CatalogAddon addon) =>
        GameFolder.LooksLikeClient(Folder) && (addon.Folders.Count > 0
            ? addon.Folders.All(f => Directory.Exists(Path.Combine(AddonFolder.Path(Folder), f)))
            : _settings.AddonCommits.ContainsKey(addon.Name));

    bool NeedsInstall(CatalogAddon addon) =>
        !IsInstalled(addon) || (_latestCommits.TryGetValue(addon.Name, out string? latest)
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
            bool installed = IsInstalled(addon), update = installed && NeedsInstall(addon);
            Button action = new() { Content = !installed ? "Install" : update ? "Update" : "Installed", FontSize = 12, IsEnabled = !installed || update };
            action.Click += async (_, _) => await RunAsync($"Addon {addon.Name}", token => InstallCatalogAsync(addon, token));
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
        PreviewInstall.Content = IsInstalled(addon) ? "Reinstall" : "Install";
        PreviewText.Text = $"{addon.Description}\n\n{addon.Url}\n\nLoading the page…";
        try
        {
            string readme = await AddonFolder.ReadmeAsync(Web, addon);
            if (_previewed == addon)
                PreviewText.Text = $"{addon.Description}\n\n{(readme.Length > 0 ? readme : "This repository has no README.")}";
        }
        catch (Exception error)
        {
            if (_previewed == addon) PreviewText.Text = $"{addon.Description}\n\nCouldn't load the page: {error.Message}";
        }
    }

    async void OnPreviewInstall(object? sender, RoutedEventArgs e)
    {
        if (_previewed is { } addon) await RunAsync($"Addon {addon.Name}", token => InstallCatalogAsync(addon, token));
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
            catch (HttpRequestException error) { Log($"{addon.Name}: {error.Message}"); }
        }
    }

    async Task InstallCatalogAsync(CatalogAddon addon, CancellationToken token)
    {
        if (!GameFolder.LooksLikeClient(Folder)) throw new InvalidOperationException("Pick your WoW folder first.");
        IReadOnlyList<string> folders = await AddonFolder.InstallAsync(Web, Folder, addon, new Progress<string>(Status), token);
        if (!_latestCommits.ContainsKey(addon.Name)) await RefreshCommitsAsync([addon], token);
        _settings.AddonCommits[addon.Name] = _latestCommits.GetValueOrDefault(addon.Name, "installed");
        _settings.Save(_settingsFile);
        Log($"Installed {addon.Name}: {string.Join(", ", folders)}");
        Status($"{addon.Name} installed.");
        await Dispatcher.UIThread.InvokeAsync(() => { RefreshInstalledAddons(); RefreshCatalog(); });
    }

    async void OnCheckAddonUpdates(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Addon updates", async token =>
        {
            await RefreshCommitsAsync(_catalog.Where(IsInstalled), token);
            int updates = _catalog.Count(a => IsInstalled(a) && NeedsInstall(a));
            Status(updates == 0 ? "All addons are up to date." : $"{updates} addon update(s) available.");
            await Dispatcher.UIThread.InvokeAsync(RefreshCatalog);
        });
    }

    async void OnUpdateAllAddons(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Addon updates", async token =>
        {
            await RefreshCommitsAsync(_catalog.Where(IsInstalled), token);
            foreach (CatalogAddon addon in _catalog.Where(a => IsInstalled(a) && NeedsInstall(a)).ToList())
                await InstallCatalogAsync(addon, token);
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
            ComboBox box = new() { ItemsSource = items, MinWidth = 260 };
            string? chosen = UpdatePlanner.ChosenOption(_manifest!, _settings.Options, group.Name);
            box.SelectedItem = chosen ?? (group.Optional ? "(none)" : items.FirstOrDefault());
            box.SelectionChanged += (_, _) =>
            {
                string picked = box.SelectedItem as string ?? "";
                _settings.Options[group.Name] = picked == "(none)" ? "" : picked;
                _settings.Save(_settingsFile);
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
                catch (Exception error) { Status(error.Message); }
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
            _settings.Save(_settingsFile);
            ApplyBackground();
        }
    }

    void OnDefaultBackground(object? sender, RoutedEventArgs e)
    {
        _settings.Background = "";
        _settings.Save(_settingsFile);
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
        catch (Exception error) { Status(error.Message); }
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
        catch (Exception error) { Status(error.Message); }
    }

    void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (File.Exists(_logFile)) OpenUrl(_logFile);
        else OpenFolder(UserSettings.DefaultFolder);
    }
}
