using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Live and PTR: the "Game version" box above PLAY picks which install everything works on (updates, Mods, Tweaks,
/// Addons, PLAY). The PTR has its own folder (Settings page: Live folder + " PTR" unless the player picks another),
/// made from the Live install (<see cref="PtrInstall"/>), with its own WTF and Interface\AddOns. Its file list is the
/// server's Live manifest with ptr/manifest.json over it; its addon list is Live's addons.json with ptr/addons.json over
/// it. The realm is written into the install's Config.wtf before every start, so the game never shows a realm list.
/// See <see cref="GameVersions"/>.
/// </summary>
public partial class MainWindow
{
    Manifest? _liveManifest;     // the server's Live manifest (also in PTR mode, for the Live realm's name)
    Manifest? _ptrManifest;      // ptr/manifest.json as the server sends it; null when the server has none
    HashSet<string> _ptrOnlyAddons = new(StringComparer.OrdinalIgnoreCase);
    RealmStatus? _realmStatus;   // the last realm list read for the realm block (realm names and ids)
    string _ptrFolderValue = ""; // the PTR folder, readable from background work too
    bool _fillingGameVersion;

    bool IsPtr => GameVersions.IsPtr(_settings.GameVersion);

    /// <summary>The Live install: the WoW folder box (call on the window's thread).</summary>
    string LiveFolder => WowFolderBox.Text?.Trim() ?? "";

    /// <summary>The PTR install: the PTR folder box, or Live's folder + " PTR".</summary>
    string PtrFolder => _ptrFolderValue;

    /// <summary>The folder of the game version picked above PLAY.</summary>
    string Folder => IsPtr ? PtrFolder : LiveFolder;

    void UpdatePtrFolderValue() =>
        _ptrFolderValue = PtrFolderBox.Text?.Trim() is { Length: > 0 } own ? own : GameVersions.DefaultPtrFolder(LiveFolder);

    /// <summary>The installed-commit list of the install a folder belongs to (each version updates its addons itself).</summary>
    Dictionary<string, string> CommitsFor(string folder) =>
        folder.Length > 0 && _ptrFolderValue.Length > 0 && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(_ptrFolderValue).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? _settings.PtrAddonCommits : _settings.AddonCommits;

    void HookGameVersions()
    {
        PtrFolderBox.Text = _settings.PtrFolder;
        UpdatePtrFolderValue();
        PtrFolderBox.TextChanged += (_, _) => { UpdatePtrFolderValue(); if (IsPtr) GameVersionChanged(); else ShowPtrPanel(); };
        WowFolderBox.TextChanged += (_, _) => { UpdatePtrFolderValue(); ShowPtrPanel(); };
        List<LookChoice> versions = [new(GameVersions.Live, Translation.T("Live")), new(GameVersions.Ptr, Translation.T("PTR (test realm)"))];
        _fillingGameVersion = true;
        GameVersionBox.ItemsSource = versions;
        GameVersionBox.SelectedItem = versions.FirstOrDefault(v => v.Value == (IsPtr ? GameVersions.Ptr : GameVersions.Live)) ?? versions[0];
        _fillingGameVersion = false;
        GameVersionBox.SelectionChanged += OnGameVersionPicked;
        ShowPtrPanel();
    }

    async void OnGameVersionPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingGameVersion || GameVersionBox.SelectedItem is not LookChoice picked) return;
        if (picked.Value == (IsPtr ? GameVersions.Ptr : GameVersions.Live)) return;
        if (Busy)
        {
            // one install at a time: the box goes back until the job is done
            _fillingGameVersion = true;
            GameVersionBox.SelectedItem = ((IEnumerable<LookChoice>)GameVersionBox.ItemsSource!).First(v => v.Value == (IsPtr ? GameVersions.Ptr : GameVersions.Live));
            _fillingGameVersion = false;
            Status($"Wait for {_job} to finish (or press Cancel).");
            return;
        }
        _settings.GameVersion = picked.Value;
        SaveSettings();
        Log(IsPtr ? $"Game version: PTR ({PtrFolder})" : $"Game version: Live ({LiveFolder})");
        GameVersionChanged();
        // the other version's file list and addons
        if (_server is not null && HaveLogin && Agreed) await CheckAsync(download: false);
    }

    /// <summary>Everything that shows the picked install: the button, Tweaks, Addons, Mods.</summary>
    void GameVersionChanged()
    {
        _pendingFiles = 0;
        SetPendingUpdate(null);
        if (!IsPtr && _liveManifest is not null) _manifest = _liveManifest;
        ShowPlayState();
        LoadTweaks();
        RefreshInstalledAddons();
        RefreshCatalog();
        RefreshMods();
        ShowPtrPanel();
    }

    /// <summary>The PTR parts of the window: the folder note, the copy buttons and the Addons page's copy button.</summary>
    void ShowPtrPanel()
    {
        bool installed = PtrFolder.Length > 0 && GameFolder.LooksLikeClient(PtrFolder);
        PtrFolderBox.Watermark = GameVersions.DefaultPtrFolder(LiveFolder) is { Length: > 0 } d ? d : Translation.T("Live folder + \" PTR\"");
        PtrStateText.Text = PtrFolder.Length == 0 ? Translation.T("Pick your Live WoW folder first.")
            : installed ? string.Format(Translation.T("The PTR is installed in {0}."), PtrFolder)
            : string.Format(Translation.T("Not installed yet: pick PTR above PLAY and press INSTALL. It is made from your Live game in {0}, so it downloads only what's new."), PtrFolder);
        CopyAccountDataButton.IsEnabled = CopyKeyBindingsButton.IsEnabled = installed;
        CopyAddonsFromLiveButton.IsVisible = IsPtr;
        CopyAddonsFromLiveButton.IsEnabled = installed;
        GameVersionText.Text = IsPtr ? Translation.T("PTR: the test realm") : "";
        GameVersionText.IsVisible = false;   // the dropdown above already says Live or PTR
    }

    // ------------------------------------------------------------------------------------------- server data
    /// <summary>The manifest for the picked version: Live's, or (PTR) Live's with ptr/manifest.json over it. Also fills
    /// the addon list: Live's addons.json, and for the PTR ptr/addons.json over it. Runs in the background.</summary>
    async Task<Manifest> ManifestForVersionAsync(ServerClient client, Manifest live, bool ptr, CancellationToken token)
    {
        _liveManifest = live;
        string? catalog = await client.GetTextAsync("addons.json", token);
        IReadOnlyList<CatalogAddon> liveCatalog = catalog is null ? [] : AddonFolder.ParseCatalog(catalog);
        if (!ptr)
        {
            _catalog = liveCatalog;
            _ptrOnlyAddons = new(StringComparer.OrdinalIgnoreCase);
            return live;
        }
        string? ptrText = await client.GetTextAsync(GameVersions.PtrChannel + "manifest.json", token);
        _ptrManifest = ptrText is null ? null : Manifest.Parse(ptrText);
        if (_ptrManifest is null) Log("The server has no PTR files (ptr/manifest.json): the PTR gets the Live files");
        else Log($"PTR: {Words.Files(_ptrManifest.Files.Count)} differ from Live");
        string? ptrCatalog = await client.GetTextAsync(GameVersions.PtrChannel + "addons.json", token);
        _catalog = GameVersions.MergeCatalog(liveCatalog, ptrCatalog is null ? [] : AddonFolder.ParseCatalog(ptrCatalog), out HashSet<string> ptrOnly);
        _ptrOnlyAddons = ptrOnly;
        return _ptrManifest is null ? GameVersions.MergePtr(live, new Manifest()) : GameVersions.MergePtr(live, _ptrManifest);
    }

    /// <summary>The realm the picked version logs into, and its login server; written to its Config.wtf before PLAY.</summary>
    void WriteGameRealm(string folder)
    {
        try
        {
            if (IsPtr && LiveFolder.Length > 0 && GameVersions.SeedConfigFromLive(LiveFolder, folder))
                Log("PTR: the game's settings (resolution, graphics, sound...) copied from Live");
            string? realm = IsPtr ? GameVersions.PtrRealmName(_ptrManifest, _policy, _realmStatus)
                                  : GameVersions.LiveRealmName(_liveManifest ?? _manifest, _policy, _realmStatus);
            if (GameVersions.WriteRealm(folder, realm, _manifest?.Realmlist))
                Log($"Realm for the game: {realm ?? "(as before)"}" + (_manifest?.Realmlist is { Length: > 0 } host ? $" on {host}" : ""));
        }
        catch (Exception error) { Logger.Warning("Couldn't write the realm into WTF\\Config.wtf", error); }
    }

    // ------------------------------------------------------------------------------------------- PTR install
    /// <summary>Makes the PTR install from the Live one (links or copies), then the update brings the PTR's own files.</summary>
    async Task<bool> InstallPtrAsync()
    {
        if (Busy) return false;
        string live = LiveFolder, ptr = PtrFolder;
        if (!GameFolder.LooksLikeClient(live))
        {
            Status(Translation.T("The PTR is made from your Live game: install Live first (pick Live above PLAY)."));
            return false;
        }
        if (ptr.Length == 0 || GameVersions.Overlap(live, ptr))
        {
            Status(Translation.T("Pick a PTR folder of its own (Settings page), not the Live folder or a folder inside it."));
            return false;
        }
        if (!await ConfirmAsync("Install the PTR", string.Format(Translation.T(
                "The PTR (test realm) goes into {0}. It shares your Live game's files where it can (no extra space on the same drive) and downloads only what is new. It starts with fresh settings and no addons: copy yours from Live afterwards if you want them.\n\nYour Live game is not changed."), ptr), "Install"))
            return false;
        HashCache cache = HashCache.Load(_cacheFile);
        bool done = await RunAsync("PTR install", token =>
        {
            try
            {
                Status(Translation.T("Making the PTR from your Live game…"));
                PtrInstall.Result result = PtrInstall.Prepare(live, ptr, cache, new UiProgress<string>(text => StatusText.Text = text), token);
                Log($"PTR made from Live in {ptr}: {Words.Files(result.Linked)} shared ({Words.Size(result.BytesLinked)}), {Words.Files(result.Copied)} copied ({Words.Size(result.BytesCopied)})");
            }
            finally { cache.Save(); }
            return Task.CompletedTask;
        });
        ShowPtrPanel();
        ShowPlayState();
        LoadTweaks();
        return done;
    }

    async void OnBrowsePtr(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the PTR (test realm)" });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            if (LiveFolder.Length > 0 && GameVersions.Overlap(LiveFolder, path))
            {
                Status(Translation.T("Pick a PTR folder of its own, not the Live folder or a folder inside it."));
                return;
            }
            PtrFolderBox.Text = path;
            SaveSettings();
        }
    }

    void OnOpenPtrFolder(object? sender, RoutedEventArgs e)
    {
        if (PtrFolder.Length == 0 || !Directory.Exists(PtrFolder)) { Status(Translation.T("The PTR isn't installed yet: pick PTR above PLAY and press INSTALL.")); return; }
        OpenFolder(PtrFolder);
    }

    // ------------------------------------------------------------------------------------------- copy from Live
    string? LiveRealm => GameVersions.LiveRealmName(_liveManifest ?? (IsPtr ? null : _manifest), _policy, _realmStatus);
    string PtrRealm => GameVersions.PtrRealmName(_ptrManifest, _policy, _realmStatus);

    /// <summary>One copy from Live to the PTR, after the player said yes and the PTR game is closed.</summary>
    async Task CopyFromLiveAsync(string title, string question, string what, Func<string, string, string?, string, PtrCopy.Result> copy)
    {
        if (Busy) { Status($"Wait for {_job} to finish (or press Cancel)."); return; }
        string live = LiveFolder, ptr = PtrFolder;
        if (!GameFolder.LooksLikeClient(ptr)) { Status(Translation.T("The PTR isn't installed yet: pick PTR above PLAY and press INSTALL.")); return; }
        if (!await ConfirmAsync(title, Translation.T(question), "Copy")) return;
        // the game writes its settings when it closes: a copy made while it runs would be undone
        if (!await MakeSureGameClosedAsync(ptr)) return;
        string? liveRealm = LiveRealm;
        string ptrRealm = PtrRealm;
        try
        {
            PtrCopy.Result result = await Task.Run(() => copy(live, ptr, liveRealm, ptrRealm));
            foreach (string note in result.Notes) Log(note);
            Log($"Copied {what} from Live to the PTR: {Words.Files(result.Files)}" + (result.Addons > 0 ? $", {Words.Addons(result.Addons)}" : "")
                + (result.BackupFolder is { } b ? $" (the PTR's old files are in {b})" : ""));
            Status(result.Files == 0 && result.Notes.Count > 0 ? result.Notes[0]
                : string.Format(Translation.T("Copied {0} from Live to the PTR ({1})."), Translation.T(what), Words.Files(result.Files)));
        }
        catch (Exception error)
        {
            Status(error.Message);
            Logger.Error($"Copying {what} to the PTR", error);
        }
    }

    async void OnCopyAccountData(object? sender, RoutedEventArgs e) =>
        await CopyFromLiveAsync("Copy Account Data", "Are you sure you want to copy your LIVE account data to this TEST account?\n\nYour Live settings, macros and addon settings (per account and per character) replace the PTR's. The PTR's old files are kept in its _Temucraft\\Backups folder; Live is not changed.",
            "account data", PtrCopy.CopyAccountData);

    async void OnCopyKeyBindings(object? sender, RoutedEventArgs e) =>
        await CopyFromLiveAsync("Copy Key Bindings", "Are you sure you want to copy your LIVE key bindings to this TEST account?\n\nYour Live key bindings (per account and per character) replace the PTR's. The PTR's old files are kept in its _Temucraft\\Backups folder; Live is not changed.",
            "key bindings", PtrCopy.CopyKeyBindings);

    async void OnCopyAddonsFromLive(object? sender, RoutedEventArgs e)
    {
        await CopyFromLiveAsync("Copy my addons from Live", "Copy every addon in your LIVE Interface\\AddOns, with its settings (WTF SavedVariables), to the PTR?\n\nAn addon the PTR already has is replaced (the old one is kept in the PTR's _Temucraft\\Backups folder). Live is not changed.",
            "addons", PtrCopy.CopyAddons);
        // what was installed on Live from the server's list counts as installed on the PTR too
        foreach ((string name, string commit) in _settings.AddonCommits)
            if (_catalog.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } addon && IsInstalled(addon, PtrFolder))
                _settings.PtrAddonCommits.TryAdd(name, commit);
        SaveSettings();
        RefreshInstalledAddons();
        RefreshCatalog();
    }

    /// <summary>"Also install on Live" for an addon only the PTR's list has: installs it from GitHub into the Live
    /// folder (the one thing on the PTR's pages that writes into Live, and only when asked).</summary>
    async Task InstallOnLiveAsync(CatalogAddon addon)
    {
        string live = LiveFolder;
        if (!GameFolder.LooksLikeClient(live)) { Status(Translation.T("There's no Live game to install it on.")); return; }
        if (!await ConfirmAsync("Also install on Live", string.Format(Translation.T("Install {0} on your LIVE game too ({1})?"), addon.Name, live), "Install")) return;
        await RunAsync($"Addon {addon.Name} (Live)", token => InstallCatalogAsync(addon, live, token));
    }
}
