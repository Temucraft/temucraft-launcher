namespace Launcher.Core;

/// <summary>
/// Keeps the player's own game settings the same on every account (and, if wanted, every character) on this PC.
/// WoW saves them per account in WTF\Account\&lt;ACCOUNT&gt;\: config-cache.wtf (auto loot and other account
/// settings), bindings-cache.wtf (key bindings) and macros-cache.txt (general macros); per character in
/// WTF\Account\&lt;ACCOUNT&gt;\&lt;Realm&gt;\&lt;Character&gt;\; addon settings in the account's SavedVariables folder.
/// For each file the newest copy wins and is copied over the older ones. The launcher runs this before the game
/// starts and after it closes, so whatever was changed last on any account reaches the others.
/// (The server can do the same across PCs; see mod-temucraft-account-wide.)
/// </summary>
public static class SharedSettings
{
    public static readonly string[] AccountFiles = ["config-cache.wtf", "bindings-cache.wtf", "macros-cache.txt"];
    public static readonly string[] CharacterFiles = ["bindings-cache.wtf", "macros-cache.txt"];

    public sealed record Result(int Copied, IReadOnlyList<string> Accounts, IReadOnlyList<string> Files);

    /// <summary>
    /// The entry of each shared file in the folder's cache.md5. The game keeps there, per account (3 entries) and per
    /// character (5 entries), the MD5 of each settings file and the time it was saved (20 bytes each: 16 bytes MD5, 4
    /// bytes Unix time). A file whose MD5 doesn't match is not trusted: at login the game takes the server's copy
    /// instead and writes it over the file. So a copied file must get its MD5 and a newer time there too, or the
    /// sharing is undone the next time that account logs in.
    /// </summary>
    static readonly Dictionary<string, int> AccountCacheIndex = new(StringComparer.OrdinalIgnoreCase)
        { ["config-cache.wtf"] = 0, ["bindings-cache.wtf"] = 1, ["macros-cache.txt"] = 2 };
    static readonly Dictionary<string, int> CharacterCacheIndex = new(StringComparer.OrdinalIgnoreCase)
        { ["config-cache.wtf"] = 0, ["bindings-cache.wtf"] = 1, ["macros-cache.txt"] = 2, ["layout-local.txt"] = 3, ["chat-cache.txt"] = 4 };
    const int CacheEntry = 20;

    /// <summary>Whether the game keeps a character file of this name in the character's cache.md5.</summary>
    internal static bool HasCacheEntry(string name) => CharacterCacheIndex.ContainsKey(name);

    /// <summary>The account folders (WTF\Account\NAME), without the shared SavedVariables folder.</summary>
    public static IReadOnlyList<string> Accounts(string wowFolder)
    {
        string root = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The character folders of an account: Account\NAME\Realm\Character (those with any file in them).</summary>
    static IEnumerable<string> Characters(string account) =>
        Directory.GetDirectories(account)
            .Where(realm => !Path.GetFileName(realm).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
            .SelectMany(Directory.GetDirectories);

    /// <summary>
    /// Copies the newest copy of each shared file over the older ones. Returns how many files were replaced.
    /// <paramref name="beforeChange"/> runs once before the first file is replaced (a backup).
    /// </summary>
    public static Result Sync(string wowFolder, bool addonSettings, bool characters, Action? beforeChange = null)
    {
        IReadOnlyList<string> accounts = Accounts(wowFolder);
        List<List<string>> groups = [];
        // account files: the same name in every account
        foreach (string name in AccountFiles)
            groups.Add([.. accounts.Select(a => Path.Combine(a, name))]);
        // addon settings: each SavedVariables\Addon.lua across the accounts
        if (addonSettings)
            foreach (string name in accounts.SelectMany(a => Files(Path.Combine(a, "SavedVariables"), "*.lua")).Distinct(StringComparer.OrdinalIgnoreCase))
                groups.Add([.. accounts.Select(a => Path.Combine(a, "SavedVariables", name))]);
        // character files: every character of every account
        if (characters)
        {
            List<string> all = [.. accounts.SelectMany(Characters)];
            foreach (string name in CharacterFiles)
                groups.Add([.. all.Select(c => Path.Combine(c, name))]);
        }

        int copied = 0;
        bool backedUp = false;
        List<string> changed = [];
        foreach (List<string> group in groups)
        {
            List<FileInfo> present = [.. group.Select(p => new FileInfo(p)).Where(f => f.Exists)];
            if (present.Count == 0) continue;
            FileInfo newest = present.MaxBy(f => f.LastWriteTimeUtc)!;
            byte[] content = File.ReadAllBytes(newest.FullName);
            uint savedAt = CacheTime(newest.FullName, content);
            foreach (string target in group)
            {
                if (target.Equals(newest.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                {
                    // the same file, but copied by an older launcher without its cache.md5 entry: the game would
                    // still replace it with the server's copy, so the entry is put right
                    if (CacheStale(target, content))
                    {
                        if (!backedUp) { beforeChange?.Invoke(); backedUp = true; }
                        SetCacheEntry(target, content, savedAt);
                        copied++;
                        changed.Add(Path.GetRelativePath(wowFolder, target) + " (cache.md5)");
                    }
                    continue;
                }
                // only into folders that exist: a character or account folder is never made up
                if (!Directory.Exists(Path.GetDirectoryName(target))) continue;
                if (!backedUp) { beforeChange?.Invoke(); backedUp = true; }
                File.WriteAllBytes(target, content);
                File.SetLastWriteTimeUtc(target, newest.LastWriteTimeUtc);   // the same age: not "newer" next time
                SetCacheEntry(target, content, savedAt);
                copied++;
                changed.Add(Path.GetRelativePath(wowFolder, target));
            }
        }
        return new Result(copied, [.. accounts.Select(f => Path.GetFileName(f)!)], changed);
    }

    /// <summary>Which cache.md5 entry a file has (account or character folder), or -1 (addon settings have none).</summary>
    static int CacheIndex(string file, out int entries)
    {
        string name = Path.GetFileName(file);
        string folder = Path.GetDirectoryName(file)!;
        // an account folder is WTF\Account\NAME; a character folder is two below it
        bool account = Path.GetFileName(Path.GetDirectoryName(folder) ?? "").Equals("Account", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, int> map = account ? AccountCacheIndex : CharacterCacheIndex;
        entries = account ? 3 : 5;
        return map.TryGetValue(name, out int index) ? index : -1;
    }

    /// <summary>The time the game saved a file, from its own cache.md5 when the MD5 there matches; else the file's time.</summary>
    static uint CacheTime(string file, byte[] content)
    {
        uint fileTime = (uint)Math.Clamp(new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds(), 0, uint.MaxValue);
        int index = CacheIndex(file, out _);
        string cache = Path.Combine(Path.GetDirectoryName(file)!, "cache.md5");
        if (index < 0 || !File.Exists(cache)) return fileTime;
        byte[] data = File.ReadAllBytes(cache);
        int at = index * CacheEntry;
        if (data.Length < at + CacheEntry) return fileTime;
        byte[] md5 = System.Security.Cryptography.MD5.HashData(content);
        if (!data.AsSpan(at, 16).SequenceEqual(md5)) return fileTime;
        return Math.Max(BitConverter.ToUInt32(data, at + 16), 1);
    }

    /// <summary>True when the folder's cache.md5 has an entry for this file with another file's MD5 (an empty entry
    /// or no cache.md5 at all is not stale: the game hasn't saved that file there).</summary>
    static bool CacheStale(string file, byte[] content)
    {
        int index = CacheIndex(file, out _);
        if (index < 0) return false;
        string cache = Path.Combine(Path.GetDirectoryName(file)!, "cache.md5");
        if (!File.Exists(cache)) return false;
        byte[] data = File.ReadAllBytes(cache);
        int at = index * CacheEntry;
        if (data.Length < at + CacheEntry) return false;
        ReadOnlySpan<byte> md5 = data.AsSpan(at, 16);
        return md5.IndexOfAnyExcept((byte)0) >= 0 && !md5.SequenceEqual(System.Security.Cryptography.MD5.HashData(content));
    }

    /// <summary>Writes a copied file's MD5 and save time into its folder's cache.md5 (made when missing), with a time
    /// newer than the one there, so the game keeps the file and sends it to the server instead of replacing it.</summary>
    internal static void SetCacheEntry(string file, byte[] content, uint savedAt)
    {
        int index = CacheIndex(file, out int entries);
        if (index < 0) return;
        string cache = Path.Combine(Path.GetDirectoryName(file)!, "cache.md5");
        byte[] data = File.Exists(cache) ? File.ReadAllBytes(cache) : [];
        if (data.Length < entries * CacheEntry) Array.Resize(ref data, entries * CacheEntry);
        int at = index * CacheEntry;
        uint old = BitConverter.ToUInt32(data, at + 16);
        uint time = Math.Max(savedAt, old == uint.MaxValue ? old : old + 1);
        System.Security.Cryptography.MD5.HashData(content).CopyTo(data, at);
        BitConverter.GetBytes(time).CopyTo(data, at + 16);
        File.WriteAllBytes(cache, data);
    }

    static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, pattern).Select(f => Path.GetFileName(f)!) : [];
}
