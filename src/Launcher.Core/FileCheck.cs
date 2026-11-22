using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core;

public static class Hashing
{
    public static async Task<string> Sha256Async(string file, IProgress<long>? progress = null, CancellationToken token = default)
    {
        await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        int read;
        long done = 0;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(done);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>
/// Remembers the hash of each checked file with its size and modified time, so a 16 GB patch is hashed once instead
/// of at every launch. A file whose size or time changed is hashed again.
/// </summary>
public sealed class HashCache
{
    // Path: the file's own spelling (the key is lower case); missing in caches written by older launchers
    sealed record Entry(long Size, long ModifiedTicks, string Sha256, string? Path = null);

    readonly Dictionary<string, Entry> _entries;
    readonly string _file;

    HashCache(string file, Dictionary<string, Entry> entries)
    {
        _file = file;
        _entries = entries;
    }

    public static HashCache Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return new HashCache(file, JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file))
                    ?? new Dictionary<string, Entry>());
        }
        catch (JsonException)
        {
        }
        return new HashCache(file, new Dictionary<string, Entry>());
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
    }

    public async Task<string> GetAsync(string path, IProgress<long>? progress = null, CancellationToken token = default)
    {
        FileInfo info = new(path);
        string key = Path.GetFullPath(path).ToLowerInvariant();
        if (_entries.TryGetValue(key, out Entry? entry) && entry.Size == info.Length
            && entry.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
            return entry.Sha256;

        string sha = await Hashing.Sha256Async(path, progress, token);
        _entries[key] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha, Path.GetFullPath(path));
        return sha;
    }

    /// <summary>Records a file just downloaded and verified, so the next check doesn't hash it again.</summary>
    public void Remember(string path, string sha256)
    {
        FileInfo info = new(path);
        _entries[Path.GetFullPath(path).ToLowerInvariant()] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha256, Path.GetFullPath(path));
    }

    /// <summary>Forgets a file, so the next check hashes it again.</summary>
    public void Forget(string path) => _entries.Remove(Path.GetFullPath(path).ToLowerInvariant());

    /// <summary>Files on disk this cache knows hold exactly this content (same hash and size, unchanged since).</summary>
    public IEnumerable<string> FilesWith(string sha256, long size)
    {
        foreach ((string key, Entry entry) in _entries.ToList())
        {
            if (entry.Sha256 != sha256 || entry.Size != size) continue;
            string path = entry.Path ?? key;
            FileInfo info = new(path);
            if (info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == entry.ModifiedTicks)
                yield return path;
        }
    }
}

/// <summary>What the player picked: which optional parts to install, and where.</summary>
public sealed record InstallChoice(string WowFolder, IReadOnlyCollection<string> Locales, bool Addons,
                                   string? BaseFolder = null, IReadOnlyDictionary<string, string>? Options = null);

public sealed record PlannedFile(ManifestFile File, string Target);

public static class UpdatePlanner
{
    /// <summary>The manifest files this choice installs, with where each one goes.</summary>
    public static IReadOnlyList<PlannedFile> Wanted(Manifest manifest, InstallChoice choice)
    {
        List<PlannedFile> wanted = [];
        foreach (ManifestFile file in manifest.Files)
        {
            string? root = file.Category switch
            {
                FileCategory.Patch => choice.WowFolder,
                FileCategory.Locale when file.Locale is not null
                    && choice.Locales.Contains(file.Locale, StringComparer.OrdinalIgnoreCase) => choice.WowFolder,
                FileCategory.Addon when choice.Addons => choice.WowFolder,
                FileCategory.Base when !string.IsNullOrEmpty(choice.BaseFolder) => choice.BaseFolder,
                FileCategory.Option when IsChosen(manifest, choice, file) => choice.WowFolder,
                _ => null,
            };
            if (root is not null)
                wanted.Add(new PlannedFile(file, PathRules.Resolve(root, file.Path)));
        }
        return wanted;
    }

    /// <summary>The option the player picked in a group (the first option when nothing was picked and it isn't optional).</summary>
    public static string? ChosenOption(Manifest manifest, IReadOnlyDictionary<string, string>? options, string group) =>
        ChosenOption(manifest, options, group, 0);

    static string? ChosenOption(Manifest manifest, IReadOnlyDictionary<string, string>? options, string group, int depth)
    {
        OptionGroup? info = manifest.OptionGroups.FirstOrDefault(g => g.Name == group);
        // a pick the server no longer offers (a renamed or removed choice) counts as no pick: the default again
        if (info is not { Enforce: true } && options is not null && options.TryGetValue(group, out string? picked)
            && RequirementsMet(manifest, options, info, depth)
            && (picked.Length == 0 || info is null || info.Options.Contains(picked, StringComparer.OrdinalIgnoreCase)))
            return picked.Length == 0 ? null : info?.Options.First(o => o.Equals(picked, StringComparison.OrdinalIgnoreCase)) ?? picked;
        return info is null || info.Optional ? null : info.Options.FirstOrDefault();
    }

    /// <summary>Whether the groups a group needs ("requires") have the option it needs picked.</summary>
    public static bool RequirementsMet(Manifest manifest, IReadOnlyDictionary<string, string>? options, OptionGroup? info, int depth = 0) =>
        info is null || depth > 8 || info.Requires.All(r =>
            string.Equals(ChosenOption(manifest, options, r.Key, depth + 1), r.Value, StringComparison.OrdinalIgnoreCase));

    static bool IsChosen(Manifest manifest, InstallChoice choice, ManifestFile file) =>
        string.Equals(ChosenOption(manifest, choice.Options, file.Group!), file.Option, StringComparison.OrdinalIgnoreCase);

    /// <summary>Files of the options that are not picked (to remove), unless a picked option uses the same path.</summary>
    public static IReadOnlyList<string> Unchosen(Manifest manifest, InstallChoice choice)
    {
        HashSet<string> keep = Wanted(manifest, choice).Select(p => p.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest.Files.Where(f => f.Category == FileCategory.Option && !IsChosen(manifest, choice, f) && !keep.Contains(f.Path))
            .Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The wanted files that are missing or differ from the server's copy.</summary>
    public static async Task<IReadOnlyList<PlannedFile>> OutdatedAsync(IEnumerable<PlannedFile> wanted, HashCache cache,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        List<PlannedFile> outdated = [];
        foreach (PlannedFile planned in wanted)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(planned.Target) || new FileInfo(planned.Target).Length != planned.File.Size)
            {
                outdated.Add(planned);
                continue;
            }
            status?.Report($"Checking {planned.File.Path}");
            if (await cache.GetAsync(planned.Target, token: token) != planned.File.Sha256)
                outdated.Add(planned);
        }
        return outdated;
    }
}
