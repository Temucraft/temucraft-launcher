namespace Launcher.Core;

/// <summary>Files of options the player turned off are kept in the game folder (by their SHA-256), so switching back
/// (Classic to HD and back, say) moves them into place again instead of downloading them.</summary>
public static class KeptFiles
{
    public static string FolderOf(string wowFolder) => Path.Combine(wowFolder, "_Temucraft", "Kept");

    static string PathOf(string wowFolder, string sha256) => Path.Combine(FolderOf(wowFolder), sha256.ToLowerInvariant());

    public static bool Has(string wowFolder, string sha256) => File.Exists(PathOf(wowFolder, sha256));

    /// <summary>Moves the files out of the game (into the kept folder). Returns the ones moved.</summary>
    public static async Task<IReadOnlyList<string>> KeepAsync(string wowFolder, IEnumerable<string> relatives, HashCache cache,
                                                              CancellationToken token = default)
    {
        List<string> kept = [];
        foreach (string relative in relatives)
        {
            string full = PathRules.Resolve(wowFolder, relative);
            if (!File.Exists(full)) continue;
            string sha = await cache.GetAsync(full, token: token);
            string target = PathOf(wowFolder, sha);
            Directory.CreateDirectory(FolderOf(wowFolder));
            if (File.Exists(target)) File.Delete(full);
            else File.Move(full, target);
            cache.Forget(full);
            kept.Add(relative);
        }
        return kept;
    }

    /// <summary>Puts kept files back where the update wants them. Returns the files that still need downloading.</summary>
    public static List<PlannedFile> Restore(string wowFolder, IEnumerable<PlannedFile> planned, HashCache cache, Action<string>? log = null)
    {
        List<PlannedFile> left = [];
        foreach (PlannedFile p in planned)
        {
            string source = PathOf(wowFolder, p.File.Sha256);
            if (!File.Exists(source)) { left.Add(p); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(p.Target)!);
            if (File.Exists(p.Target)) File.Delete(p.Target);
            File.Move(source, p.Target);
            cache.Remember(p.Target, p.File.Sha256);
            log?.Invoke($"Put back {p.File.Path} (kept from before, no download)");
        }
        return left;
    }

    /// <summary>Size of everything kept (shown with the button that clears them).</summary>
    public static long Size(string wowFolder)
    {
        string dir = FolderOf(wowFolder);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length) : 0;
    }

    public static void Clear(string wowFolder)
    {
        string dir = FolderOf(wowFolder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
