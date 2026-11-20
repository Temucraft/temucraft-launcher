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
            foreach (string target in group)
            {
                if (target.Equals(newest.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(content)) continue;
                // only into folders that exist: a character or account folder is never made up
                if (!Directory.Exists(Path.GetDirectoryName(target))) continue;
                if (!backedUp) { beforeChange?.Invoke(); backedUp = true; }
                File.WriteAllBytes(target, content);
                File.SetLastWriteTimeUtc(target, newest.LastWriteTimeUtc);   // the same age: not "newer" next time
                copied++;
                changed.Add(Path.GetRelativePath(wowFolder, target));
            }
        }
        return new Result(copied, [.. accounts.Select(f => Path.GetFileName(f)!)], changed);
    }

    static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, pattern).Select(f => Path.GetFileName(f)!) : [];
}
