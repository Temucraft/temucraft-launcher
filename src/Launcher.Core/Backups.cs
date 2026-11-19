using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>A news post or changelog entry (news.json / changelog.json next to the manifest).</summary>
public sealed class NewsItem
{
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Link { get; set; } = "";

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<NewsItem> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<NewsItem>>(json, Json) ?? [];
}

/// <summary>
/// Backups of the player's own client data: WTF (settings, macros, key bindings, addon settings) and Interface\AddOns.
/// Zips in &lt;launcher data&gt;\Backups, newest first; restoring first backs up what is there now.
/// </summary>
public static class Backups
{
    public static string Folder => Path.Combine(UserSettings.DefaultFolder, "Backups");

    public sealed record Backup(string File, DateTime Created, long Size)
    {
        public override string ToString() => $"{Created:dd-MM-yyyy HH:mm:ss}  ({Size / 1048576.0:F1} MB)  {Path.GetFileName(File)}";
    }

    public static IReadOnlyList<Backup> List() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.zip").Select(f => new FileInfo(f))
                .Select(f => new Backup(f.FullName, f.LastWriteTime, f.Length)).OrderByDescending(b => b.Created).ToList()
            : [];

    static void AddFolder(ZipArchive zip, string wowFolder, string relative, Func<string, bool>? keep = null)
    {
        string root = Path.Combine(wowFolder, relative);
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(wowFolder, file).Replace('\\', '/');
            if (keep is not null && !keep(name)) continue;
            zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
        }
    }

    /// <summary>Makes a backup; keeps the newest <paramref name="keep"/> and deletes older ones.</summary>
    public static string Create(string wowFolder, string reason, bool includeAddons = true, int keep = 10)
    {
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.zip");
        using (ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            AddFolder(zip, wowFolder, "WTF", n => !n.EndsWith("realmlist.wtf", StringComparison.OrdinalIgnoreCase));
            if (includeAddons) AddFolder(zip, wowFolder, Path.Combine("Interface", "AddOns"));
        }
        foreach (Backup old in List().Skip(Math.Max(1, keep))) File.Delete(old.File);
        return file;
    }

    public static void Restore(string wowFolder, string backupFile)
    {
        Create(wowFolder, "before-restore", keep: 50);
        using ZipArchive zip = ZipFile.OpenRead(backupFile);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            string target = PathRules.Resolve(wowFolder, e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    // ------------------------------------------------------------------------------------- macros and key bindings
    static readonly string[] MacroFiles = ["macros-cache.txt", "bindings-cache.wtf"];

    /// <summary>Exports every account's and character's macros and key bindings into one zip.</summary>
    public static int ExportMacros(string wowFolder, string zipFile)
    {
        int count = 0;
        if (File.Exists(zipFile)) File.Delete(zipFile);
        using ZipArchive zip = ZipFile.Open(zipFile, ZipArchiveMode.Create);
        string account = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(account)) return 0;
        foreach (string file in Directory.EnumerateFiles(account, "*", SearchOption.AllDirectories))
            if (MacroFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            {
                zip.CreateEntryFromFile(file, Path.GetRelativePath(wowFolder, file).Replace('\\', '/'));
                count++;
            }
        return count;
    }

    /// <summary>Puts exported macros and key bindings back (after a backup of WTF).</summary>
    public static int ImportMacros(string wowFolder, string zipFile)
    {
        Create(wowFolder, "before-macro-import", includeAddons: false, keep: 50);
        int count = 0;
        using ZipArchive zip = ZipFile.OpenRead(zipFile);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (!MacroFiles.Contains(e.Name, StringComparer.OrdinalIgnoreCase) || !e.FullName.StartsWith("WTF/Account/", StringComparison.OrdinalIgnoreCase)) continue;
            string target = PathRules.Resolve(wowFolder, e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }
}

/// <summary>Patches in Data that neither the stock client nor the server hand out (they can break the game).</summary>
public static class Untracked
{
    static readonly HashSet<string> Stock = new(StringComparer.OrdinalIgnoreCase)
    {
        "common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ",
    };
    static readonly string[] StockLocale = ["locale-{0}.MPQ", "expansion-locale-{0}.MPQ", "lichking-locale-{0}.MPQ",
        "patch-{0}.MPQ", "patch-{0}-2.MPQ", "patch-{0}-3.MPQ", "speech-{0}.MPQ", "expansion-speech-{0}.MPQ",
        "lichking-speech-{0}.MPQ", "base-{0}.MPQ", "backup-{0}.MPQ"];

    public static IReadOnlyList<string> Find(string wowFolder, Manifest manifest)
    {
        HashSet<string> known = manifest.Files.Select(f => f.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> found = [];
        string data = Path.Combine(wowFolder, "Data");
        if (!Directory.Exists(data)) return found;
        foreach (string file in Directory.GetFiles(data, "*.mpq", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
        {
            string name = Path.GetFileName(file);
            if (!Stock.Contains(name) && !known.Contains("Data/" + name)) found.Add("Data/" + name);
        }
        foreach (string locale in GameFolder.InstalledLocales(wowFolder))
            foreach (string file in Directory.GetFiles(Path.Combine(data, locale), "*.mpq", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
            {
                string name = Path.GetFileName(file);
                bool stock = StockLocale.Any(p => string.Format(p, locale).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!stock && !known.Contains($"Data/{locale}/{name}")) found.Add($"Data/{locale}/{name}");
            }
        return found;
    }

    /// <summary>Moves an untracked file into the backup folder.</summary>
    public static void Remove(string wowFolder, string relative)
    {
        string source = PathRules.Resolve(wowFolder, relative);
        string target = Path.Combine(Backups.Folder, "untracked", DateTime.Now.ToString("yyyyMMdd-HHmmss"), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
    }
}
