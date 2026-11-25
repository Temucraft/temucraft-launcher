using System.Runtime.InteropServices;

namespace Launcher.Core;

/// <summary>
/// Live and PTR (the test realm). Each has its own install: the PTR folder is made from the Live one (its big MPQs hard
/// linked when both are on the same drive, the rest copied), with its own WTF and its own Interface\AddOns. The
/// server's <c>ptr/</c> folder holds a manifest of only the files that differ from Live (a newer Wow.exe and patches,
/// for example); the PTR's file list is the Live one with those over it (<see cref="MergePtr"/>). The game never shows a
/// realm list: before it starts, the launcher writes the version's realm into its WTF\Config.wtf (<see cref="WriteRealm"/>).
/// </summary>
public static class GameVersions
{
    public const string Live = "live", Ptr = "ptr";

    /// <summary>The PTR's folder on the server, next to the Live manifest.</summary>
    public const string PtrChannel = "ptr/";

    /// <summary>The PTR realm when neither the server nor the realm list names it (realm id <see cref="PtrRealmId"/>).</summary>
    public const string DefaultPtrRealm = "[PTR] Falric";
    public const int PtrRealmId = 3;
    /// <summary>The Live realm's id (for per-realm server rules such as launcherUpdate).</summary>
    public const int LiveRealmId = 1;

    public static bool IsPtr(string? version) => string.Equals(version, Ptr, StringComparison.OrdinalIgnoreCase);

    /// <summary>The PTR's default folder: next to the Live one, with " PTR" after its name (C:\Games\Temucraft PTR).</summary>
    public static string DefaultPtrFolder(string liveFolder)
    {
        string live = liveFolder.Trim().TrimEnd('\\', '/');
        if (live.Length == 0) return "";
        string? parent = Path.GetDirectoryName(live);
        string name = Path.GetFileName(live);
        return parent is null || name.Length == 0 ? live + " PTR" : Path.Combine(parent, name + " PTR");
    }

    /// <summary>
    /// The PTR's file list: the Live manifest with the PTR manifest's files over it (same path: the PTR's copy wins; a new
    /// path is added). The PTR's files are downloaded from the ptr/ folder, so their addresses get "ptr/" in front.
    /// A file the PTR no longer lists but Live still has is not obsolete.
    /// </summary>
    public static Manifest MergePtr(Manifest live, Manifest ptr)
    {
        Dictionary<string, ManifestFile> own = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in ptr.Files)
            own[file.Path.Replace('\\', '/')] = new ManifestFile
            {
                Path = file.Path, Url = PtrChannel + file.Url, Size = file.Size, Sha256 = file.Sha256, Category = file.Category,
                Locale = file.Locale, Group = file.Group, Option = file.Option,
            };
        List<ManifestFile> files = [.. live.Files.Where(f => !own.ContainsKey(f.Path.Replace('\\', '/'))), .. own.Values];
        HashSet<string> paths = files.Select(f => f.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, System.Text.Json.JsonElement> policy = new(live.Policy, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, System.Text.Json.JsonElement value) in ptr.Policy) policy[key] = value;
        Manifest merged = new()
        {
            Version = live.Version,
            Realmlist = ptr.Realmlist.Length > 0 ? ptr.Realmlist : live.Realmlist,
            RealmName = ptr.RealmName,
            WindowsLauncher = live.WindowsLauncher,
            Launchers = live.Launchers,
            ClientVersion = ptr.ClientVersion.Length > 0 ? ptr.ClientVersion : live.ClientVersion,
            OptionGroups = [.. live.OptionGroups, .. ptr.OptionGroups.Where(g => live.OptionGroups.All(l => l.Name != g.Name))],
            LoginScreenPreviews = live.LoginScreenPreviews,
            MusicPreviews = live.MusicPreviews,
            Files = files,
            Obsolete = [.. live.Obsolete.Concat(ptr.Obsolete).Where(o => !paths.Contains(o.Replace('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase)],
            Policy = policy,
        };
        return merged;
    }

    /// <summary>The PTR's addon list: Live's with the PTR's addons.json over it (same name: the PTR's entry).
    /// <paramref name="ptrOnly"/> gets the names Live doesn't list (offered with "Also install on Live").</summary>
    public static IReadOnlyList<CatalogAddon> MergeCatalog(IReadOnlyList<CatalogAddon> live, IReadOnlyList<CatalogAddon> ptr, out HashSet<string> ptrOnly)
    {
        HashSet<string> liveNames = live.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ptrNames = ptr.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ptrOnly = new HashSet<string>(ptrNames.Where(n => !liveNames.Contains(n)), StringComparer.OrdinalIgnoreCase);
        return [.. live.Where(a => !ptrNames.Contains(a.Name)), .. ptr];
    }

    static bool IsPtrName(string name) => name.TrimStart().StartsWith("[PTR]", StringComparison.OrdinalIgnoreCase);

    static string? PolicyText(LauncherPolicy policy, string key) =>
        policy.Setting(key) is { Value.ValueKind: System.Text.Json.JsonValueKind.String } rule && rule.Value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    /// <summary>
    /// The Live realm: the Live manifest's realmName (realm-name.txt on the server), else the policy's "liveRealmName",
    /// else the realm list's only realm that isn't the PTR (realm id 1 when there are several). Null when it isn't
    /// known: then Config.wtf's realm is left as it is.
    /// </summary>
    public static string? LiveRealmName(Manifest? live, LauncherPolicy policy, RealmStatus? realms)
    {
        if (live?.RealmName is { Length: > 0 } named) return named.Trim();
        if (PolicyText(policy, "liveRealmName") is { } set) return set;
        List<RealmInfo> others = realms?.Realms.Where(r => r.Id != PtrRealmId && !IsPtrName(r.Name) && r.Name.Length > 0).ToList() ?? [];
        if (others.Count == 1) return others[0].Name;
        return others.FirstOrDefault(r => r.Id == 1)?.Name;
    }

    /// <summary>The PTR realm: the PTR manifest's realmName, else the policy's "ptrRealmName", else the realm list's realm
    /// id 3 (or one named "[PTR] ..."), else "[PTR] Falric".</summary>
    public static string PtrRealmName(Manifest? ptr, LauncherPolicy policy, RealmStatus? realms)
    {
        if (ptr?.RealmName is { Length: > 0 } named) return named.Trim();
        if (PolicyText(policy, "ptrRealmName") is { } set) return set;
        if (realms?.Realms.FirstOrDefault(r => r.Id == PtrRealmId && r.Name.Length > 0) is { } byId) return byId.Name;
        if (realms?.Realms.FirstOrDefault(r => IsPtrName(r.Name)) is { } byName) return byName.Name;
        return DefaultPtrRealm;
    }

    /// <summary>
    /// Writes the realm the game goes to into the install's WTF\Config.wtf: SET realmName (the game then logs straight
    /// into that realm, no realm list) and SET realmList (the login server). Null or empty values are left as they are.
    /// Returns true when the file changed.
    /// </summary>
    public static bool WriteRealm(string folder, string? realmName, string? realmList)
    {
        ConfigWtf config = ConfigWtf.Load(folder);
        bool changed = false;
        if (realmName is { Length: > 0 } && config.Get("realmName") != realmName) { config.Set("realmName", realmName); changed = true; }
        if (realmList is { Length: > 0 } && config.Get("realmList") != realmList) { config.Set("realmList", realmList); changed = true; }
        if (changed) config.Save();
        return changed;
    }

    /// <summary>The Config.wtf settings that mark the game's first-start agreement screens as seen.</summary>
    public static readonly string[] AgreementCVars = ["readEULA", "readTOS", "readScanning", "readContest"];

    /// <summary>
    /// The "skipGameAgreements" setting: marks the game's first-start agreement screens as seen in the install's
    /// WTF\Config.wtf, so the game goes straight to the login. Returns true when the file changed.
    /// </summary>
    public static bool SkipAgreements(string folder)
    {
        ConfigWtf config = ConfigWtf.Load(folder);
        bool changed = false;
        foreach (string seen in AgreementCVars)
            if (config.Get(seen) != "1") { config.Set(seen, "1"); changed = true; }
        if (changed) config.Save();
        return changed;
    }

    /// <summary>Settings each game version keeps for itself: never copied from Live (its realm, and the debug mode
    /// settings, which follow each version's own Debug mode option).</summary>
    static bool OwnSetting(string name) =>
        name.Equals("realmName", StringComparison.OrdinalIgnoreCase) || name.Equals("realmList", StringComparison.OrdinalIgnoreCase)
        || DebugMode.CVarNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The "ptrCopyLiveSettings" setting: the PTR starts with the Live game's own settings (resolution, graphics, sound,
    /// interface CVars, saved accounts), copied from Live's WTF\Config.wtf while the PTR's has never been set up (no
    /// gxResolution yet). Only settings the PTR doesn't have yet are added, so what the launcher already wrote there
    /// (the server's enforced tweaks, the player's Tweaks) stays. With <paramref name="force"/> (Copy Account Data) every
    /// Live setting replaces the PTR's; <paramref name="keep"/> is called first with the file, to back it up. The realm
    /// and debug lines are never copied. Only the PTR's file is written. Returns true when settings were copied.
    /// </summary>
    public static bool SeedConfigFromLive(string liveFolder, string ptrFolder, bool force = false, Action<string>? keep = null)
    {
        ConfigWtf live = ConfigWtf.Load(liveFolder), ptr = ConfigWtf.Load(ptrFolder);
        if (!File.Exists(live.File) || (!force && ptr.Get("gxResolution") is not null)) return false;
        List<(string Name, string Value)> copy = [.. live.All().Where(s => !OwnSetting(s.Name) && (force ? ptr.Get(s.Name) != s.Value : ptr.Get(s.Name) is null))];
        if (copy.Count == 0) return false;
        if (File.Exists(ptr.File)) keep?.Invoke(ptr.File);
        foreach ((string name, string value) in copy) ptr.Set(name, value);
        ptr.Save();
        return true;
    }

    /// <summary>True when the two folders are the same, or one is inside the other (the PTR must never share files
    /// with Live that way).</summary>
    public static bool Overlap(string a, string b)
    {
        string x = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string y = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Makes the PTR install from the Live one, so the PTR only downloads what differs. Every MPQ under Data (and the kept
/// option files) is hard linked when the two folders are on the same drive: no extra space, nothing to copy. The
/// launcher never writes into an MPQ in place (updates write a new file and move it over the old name, which leaves the
/// Live file alone), so a link is safe. Everything else is copied. Left out: WTF (the PTR gets its own; see <see cref="GameVersions.SeedConfigFromLive"/>),
/// Interface\AddOns (the PTR has its own addons; see <see cref="PtrCopy.CopyAddons"/>), Cache, Logs, Screenshots, Errors
/// and unfinished downloads. Nothing is written into the Live folder.
/// </summary>
public static class PtrInstall
{
    public sealed record Result(int Linked, int Copied, int Skipped, long BytesLinked, long BytesCopied);

    static readonly string[] SkippedTop = ["WTF", "Cache", "Logs", "Screenshots", "Errors"];
    static readonly string[] SkippedEndings = [".part", ".upload", ".tmp", ".old", ".new", ".building"];

    /// <summary>Whether a file (relative to the game folder) is left out of the PTR.</summary>
    public static bool Skipped(string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length > 1 && SkippedTop.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        if (parts.Length > 2 && parts[0].Equals("Interface", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("AddOns", StringComparison.OrdinalIgnoreCase)) return true;
        return SkippedEndings.Any(e => relative.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a file may be hard linked: the MPQs under Data and the kept option files (only ever moved or
    /// deleted, never written in place). Wow.exe, DLLs, realmlist.wtf and the rest are copied.</summary>
    public static bool Linkable(string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length > 1 && parts[0].Equals("Data", StringComparison.OrdinalIgnoreCase)
            && relative.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase)) return true;
        return parts.Length > 2 && parts[0].Equals("_Temucraft", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Kept", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills the PTR folder from the Live one. A file the PTR already has is left alone, so running it again only adds
    /// what's missing (and never undoes the PTR's own newer files). Known hashes are carried over to the PTR's paths, so the PTR's first
    /// check doesn't read every file again.
    /// </summary>
    public static Result Prepare(string liveFolder, string ptrFolder, HashCache? cache = null, IProgress<string>? status = null,
        CancellationToken token = default)
    {
        if (!GameFolder.LooksLikeClient(liveFolder)) throw new InvalidOperationException("The Live game folder has no game to make the PTR from.");
        if (GameVersions.Overlap(liveFolder, ptrFolder)) throw new InvalidOperationException("The PTR folder must be a folder of its own, not the Live folder or inside it.");
        Directory.CreateDirectory(ptrFolder);
        int linked = 0, copied = 0, skipped = 0;
        long bytesLinked = 0, bytesCopied = 0;
        bool tryLinks = true;
        // Wow.exe goes last: until it is there the PTR folder isn't taken for a game, so an install that was cancelled
        // or failed halfway is made from Live again (and not downloaded from the server) the next time
        string exe = GameFolder.ExePath(liveFolder);
        IEnumerable<string> sources = Directory.EnumerateFiles(liveFolder, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)).Append(exe);
        foreach (string source in sources)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(liveFolder, source);
            if (Skipped(relative)) { skipped++; continue; }
            string target = Path.Combine(ptrFolder, relative);
            FileInfo from = new(source);
            // what the PTR already has stays: its own newer files (from its update) are never undone
            if (File.Exists(target)) { Carry(cache, source, target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (tryLinks && Linkable(relative))
            {
                status?.Report($"Linking {relative}");
                if (HardLink.TryCreate(source, target))
                {
                    linked++; bytesLinked += from.Length;
                    Carry(cache, source, target);
                    continue;
                }
                tryLinks = false;   // another drive (or a file system without links): copies from here on
                Logger.Info("The PTR folder can't share files with the Live one (another drive?): copying them instead", toWindow: false);
            }
            status?.Report($"Copying {relative} ({Words.Size(from.Length)})");
            File.Copy(source, target + ".part", overwrite: true);
            File.SetLastWriteTimeUtc(target + ".part", from.LastWriteTimeUtc);
            File.Move(target + ".part", target);
            copied++; bytesCopied += from.Length;
            Carry(cache, source, target);
        }
        return new Result(linked, copied, skipped, bytesLinked, bytesCopied);
    }

    /// <summary>Gives the PTR's file the hash the cache knows for the Live one, when it is the same file (same size and time).</summary>
    static void Carry(HashCache? cache, string source, string target)
    {
        FileInfo from = new(source), to = new(target);
        if (cache?.Known(source) is { } sha && cache.Known(target) is null && from.Length == to.Length && from.LastWriteTimeUtc == to.LastWriteTimeUtc)
            cache.Remember(target, sha);
    }
}

/// <summary>Hard links: a second name for the same file (on the same drive), on Windows and Unix.</summary>
public static class HardLink
{
    /// <summary>Makes <paramref name="link"/> another name of <paramref name="existing"/>; false when the system refuses
    /// (another drive, FAT32, no rights...).</summary>
    public static bool TryCreate(string existing, string link)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : UnixLink(existing, link) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    static extern int UnixLink([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}

/// <summary>
/// Copies the player's own Live data to the PTR, on request: addons, account data and key bindings. Only the PTR folder
/// is written to; every file it replaces is first copied to the PTR's _Temucraft\Backups\&lt;time&gt;-&lt;what&gt;\.
/// Character folders are matched by name: Live's WTF\Account\NAME\&lt;Live realm&gt;\Char goes to
/// WTF\Account\NAME\&lt;PTR realm&gt;\Char. Copied account and character files get a fresh entry in the folder's cache.md5
/// (see <see cref="SharedSettings"/>), so the game keeps them and sends them to the PTR realm instead of taking the
/// realm's (empty) copy.
/// </summary>
public static class PtrCopy
{
    public sealed record Result(int Files, int Addons, string? BackupFolder, IReadOnlyList<string> Notes);

    /// <summary>Account data: everything but the key bindings.</summary>
    static readonly string[] AccountDataFiles = ["config-cache.wtf", "macros-cache.txt"];
    static readonly string[] BindingFiles = ["bindings-cache.wtf"];

    sealed class Writer(string ptrFolder, string what)
    {
        public int Files;
        public string? Backup;
        readonly string _stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{what}";

        string BackupRoot => Path.Combine(ptrFolder, "_Temucraft", "Backups", _stamp);

        /// <summary>Keeps a copy of a PTR file before it is replaced.</summary>
        public void Keep(string target)
        {
            if (!File.Exists(target)) return;
            string backup = Path.Combine(BackupRoot, Path.GetRelativePath(ptrFolder, target));
            if (File.Exists(backup)) return;   // the first version is the one to keep
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup);
            Backup = BackupRoot;
        }

        /// <summary>Moves a whole PTR folder (an addon about to be replaced) into the backup.</summary>
        public void KeepFolder(string folder)
        {
            if (!Directory.Exists(folder)) return;
            string backup = Path.Combine(BackupRoot, Path.GetRelativePath(ptrFolder, folder));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Directory.Move(folder, backup);
            Backup = BackupRoot;
        }

        public void Copy(string source, string target, bool cacheEntry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] content = File.ReadAllBytes(source);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(content) && !cacheEntry) return;
            Keep(target);
            if (cacheEntry) Keep(Path.Combine(Path.GetDirectoryName(target)!, "cache.md5"));
            File.WriteAllBytes(target, content);
            if (cacheEntry)
                SharedSettings.SetCacheEntry(target, content, (uint)Math.Clamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, uint.MaxValue));
            Files++;
        }
    }

    static void CheckFolders(string liveFolder, string ptrFolder)
    {
        if (!GameFolder.LooksLikeClient(liveFolder)) throw new InvalidOperationException("The Live game folder has no game.");
        if (!GameFolder.LooksLikeClient(ptrFolder)) throw new InvalidOperationException("The PTR isn't installed yet: pick PTR above PLAY and press INSTALL.");
        if (GameVersions.Overlap(liveFolder, ptrFolder)) throw new InvalidOperationException("The PTR folder must be a folder of its own.");
    }

    /// <summary>The Live realm folder of an account: the named realm when it has a folder there, else the only realm
    /// folder the account has; null when it can't be told.</summary>
    public static string? LiveRealmFolder(string account, string? liveRealm)
    {
        List<string> realms = [.. Directory.GetDirectories(account).Where(d => !Path.GetFileName(d).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))];
        if (liveRealm is { Length: > 0 } && realms.FirstOrDefault(r => Path.GetFileName(r).Equals(liveRealm, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        return realms.Count == 1 ? realms[0] : null;
    }

    /// <summary>A file name Windows can keep as a folder (the game turns a realm's name into its WTF folder name as it is).</summary>
    static string FolderName(string realm) => string.Concat(realm.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c)).Trim();

    /// <summary>
    /// Copies the Live account data to the PTR: per account config-cache.wtf, macros-cache.txt and the SavedVariables
    /// folder; per character (Live realm to PTR realm) every file but the key bindings, and its SavedVariables.
    /// </summary>
    public static Result CopyAccountData(string liveFolder, string ptrFolder, string? liveRealm, string ptrRealm) =>
        CopyWtf(liveFolder, ptrFolder, liveRealm, ptrRealm, "account-data", bindings: false, writer =>
        {
            // and the game's own settings (resolution, graphics, sound, interface) from WTF\Config.wtf, whatever the
            // ptrCopyLiveSettings setting says (this is an explicit button); the PTR's old file goes to the backup
            if (GameVersions.SeedConfigFromLive(liveFolder, ptrFolder, force: true, keep: writer.Keep)) writer.Files++;
        });

    /// <summary>Copies the Live key bindings to the PTR: each account's bindings-cache.wtf and each character's own.</summary>
    public static Result CopyKeyBindings(string liveFolder, string ptrFolder, string? liveRealm, string ptrRealm) =>
        CopyWtf(liveFolder, ptrFolder, liveRealm, ptrRealm, "key-bindings", bindings: true);

    static Result CopyWtf(string liveFolder, string ptrFolder, string? liveRealm, string ptrRealm, string what, bool bindings,
        Action<Writer>? more = null)
    {
        CheckFolders(liveFolder, ptrFolder);
        Writer writer = new(ptrFolder, "before-copy-" + what);
        List<string> notes = [];
        IReadOnlyList<string> accounts = SharedSettings.Accounts(liveFolder);
        if (accounts.Count == 0) notes.Add("Live has no account settings yet (log in once on Live first).");
        string[] accountFiles = bindings ? BindingFiles : AccountDataFiles;
        foreach (string account in accounts)
        {
            string name = Path.GetFileName(account);
            string target = Path.Combine(ptrFolder, "WTF", "Account", name);
            foreach (string file in accountFiles)
                if (File.Exists(Path.Combine(account, file)))
                    writer.Copy(Path.Combine(account, file), Path.Combine(target, file), cacheEntry: true);
            if (!bindings) CopyTree(writer, Path.Combine(account, "SavedVariables"), Path.Combine(target, "SavedVariables"));

            string? realm = LiveRealmFolder(account, liveRealm);
            if (realm is null)
            {
                if (Directory.GetDirectories(account).Any(d => !Path.GetFileName(d).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)))
                    notes.Add($"{name}: characters on several realms and none named {liveRealm ?? "(unknown)"}; only the account's own settings were copied.");
                continue;
            }
            string ptrRealmFolder = Path.Combine(target, FolderName(ptrRealm));
            foreach (string character in Directory.GetDirectories(realm))
            {
                string to = Path.Combine(ptrRealmFolder, Path.GetFileName(character));
                foreach (string file in Directory.GetFiles(character))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("cache.md5", StringComparison.OrdinalIgnoreCase)) continue;   // written per file below
                    bool isBindings = fileName.Equals("bindings-cache.wtf", StringComparison.OrdinalIgnoreCase);
                    if (isBindings != bindings) continue;
                    writer.Copy(file, Path.Combine(to, fileName), cacheEntry: SharedSettings.HasCacheEntry(fileName));
                }
                if (!bindings) CopyTree(writer, Path.Combine(character, "SavedVariables"), Path.Combine(to, "SavedVariables"));
            }
        }
        more?.Invoke(writer);
        return new Result(writer.Files, 0, writer.Backup, notes);
    }

    static void CopyTree(Writer writer, string from, string to, Func<string, bool>? keep = null)
    {
        if (!Directory.Exists(from)) return;
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            if (keep?.Invoke(Path.GetFileName(file)) != false)
                writer.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), cacheEntry: false);
    }

    /// <summary>
    /// Copies every addon in Live's Interface\AddOns to the PTR (an addon the PTR already has is replaced; the old one
    /// goes to the backup), with their settings: the SavedVariables files named after them, per account and per
    /// character (Live realm to PTR realm).
    /// </summary>
    public static Result CopyAddons(string liveFolder, string ptrFolder, string? liveRealm, string ptrRealm)
    {
        CheckFolders(liveFolder, ptrFolder);
        Writer writer = new(ptrFolder, "before-copy-addons");
        string from = AddonFolder.Path(liveFolder), to = AddonFolder.Path(ptrFolder);
        List<string> names = Directory.Exists(from)
            ? [.. Directory.GetDirectories(from).Select(d => Path.GetFileName(d)!).Where(n => !n.StartsWith('.'))]
            : [];
        foreach (string name in names)
        {
            string target = Path.Combine(to, name);
            writer.KeepFolder(target);
            CopyTree(writer, Path.Combine(from, name), target);
        }
        HashSet<string> addons = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // AddonName.lua and AddonName.lua.bak
        bool Saved(string file) => addons.Contains(file.EndsWith(".lua.bak", StringComparison.OrdinalIgnoreCase) ? file[..^8]
            : file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? file[..^4] : "\u0000");
        foreach (string account in SharedSettings.Accounts(liveFolder))
        {
            string target = Path.Combine(ptrFolder, "WTF", "Account", Path.GetFileName(account));
            CopyTree(writer, Path.Combine(account, "SavedVariables"), Path.Combine(target, "SavedVariables"), Saved);
            if (LiveRealmFolder(account, liveRealm) is not { } realm) continue;
            foreach (string character in Directory.GetDirectories(realm))
                CopyTree(writer, Path.Combine(character, "SavedVariables"),
                    Path.Combine(target, FolderName(ptrRealm), Path.GetFileName(character), "SavedVariables"), Saved);
        }
        return new Result(writer.Files, names.Count, writer.Backup, names.Count == 0 ? ["Live has no addons in Interface\\AddOns."] : []);
    }
}
