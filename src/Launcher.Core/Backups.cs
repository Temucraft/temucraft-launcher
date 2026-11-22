using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>
/// The last news, changelog and their panel rules the launcher received, kept in the launcher's folder so they show
/// at once on the next start, before logging in or when the server can't be reached.
/// </summary>
public sealed class FeedCache
{
    public string News { get; set; } = "";
    public string Changelog { get; set; } = "";
    public FeedRule NewsRule { get; set; } = FeedRule.Default;
    public FeedRule ChangelogRule { get; set; } = FeedRule.Default;
    public DateTime? Saved { get; set; }

    public static FeedCache Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<FeedCache>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return new(); }
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Warning("Couldn't keep the news for next time", e); }
    }
}

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

    /// <summary>The tags news and changelog lines can carry, in square brackets, with their colours.</summary>
    public static readonly IReadOnlyList<(string Tag, string Color)> Tags =
    [
        ("New", "#8FC24A"), ("Changed", "#F2C14E"), ("Fixed", "#5FB8D9"), ("Removed", "#E06C5C"),
        ("Planned", "#B48CE0"), ("Not live yet", "#E0894A"),
    ];

    /// <summary>Better and worse values: {+350 mana} in green, {-335 mana} in red.</summary>
    public const string Better = "#6FD06F", Worse = "#E06C5C";

    /// <summary>
    /// Splits text into plain parts, [Tag] parts (only the known tags, in their colour) and {+better} / {-worse}
    /// parts (green / red, braces and sign dropped). Bold is set for tags.
    /// </summary>
    public static IReadOnlyList<(string Text, string? Color)> Segments(string text)
    {
        List<(string, string?)> parts = [];
        int at = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\[([^\]\n]{2,20})\]|\{([+-])([^{}\n]+)\}"))
        {
            string shown; string color;
            if (m.Groups[1].Success)
            {
                (string Tag, string Color) known = Tags.FirstOrDefault(t => t.Tag.Equals(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase));
                if (known.Tag is null) continue;
                shown = $"[{known.Tag}]"; color = known.Color;
            }
            else
            {
                shown = m.Groups[3].Value; color = m.Groups[2].Value == "+" ? Better : Worse;
            }
            if (m.Index > at) parts.Add((text[at..m.Index], null));
            parts.Add((shown, color));
            at = m.Index + m.Length;
        }
        if (at < text.Length) parts.Add((text[at..], null));
        return parts;
    }

    /// <summary>
    /// The text's lines with their details: a line followed by indented lines ("  - Human: ...") gets those as
    /// details, which the launcher shows folded under it (click to open). Bullets ("- ", "• ") are dropped from details.
    /// </summary>
    public static IReadOnlyList<(string Line, IReadOnlyList<string> Details)> Lines(string text)
    {
        List<(string, List<string>)> lines = [];
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]) && raw.Trim().Length > 0 && lines.Count > 0)
            {
                string detail = raw.Trim();
                if (detail.StartsWith("- ") || detail.StartsWith("• ")) detail = detail[2..];
                lines[^1].Item2.Add(detail);
            }
            else if (raw.Trim().Length > 0 || lines.Count > 0)
                lines.Add((raw, []));
        }
        return lines.Select(l => (l.Item1, (IReadOnlyList<string>)l.Item2)).ToList();
    }

    /// <summary>"18th of November 2026 (18/11/2026)"; the date as written when it isn't YYYY-MM-DD.</summary>
    public static string FormatDate(string date)
    {
        if (!DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateOnly d))
            return date;
        string suffix = (d.Day % 100) is 11 or 12 or 13 ? "th" : (d.Day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{d.Day}{suffix} of {d.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture)} {d.Year} ({d:dd/MM/yyyy})";
    }

    /// <summary>
    /// The date line of each item (newest first, as the files list them): days with several entries number them,
    /// #1 being the day's earliest (the lowest in the list), e.g. "#2 · 19th of November 2026 (19/11/2026)".
    /// </summary>
    public static IReadOnlyList<string> DateLines(IReadOnlyList<NewsItem> items)
    {
        // the date once per day, above that day's first (newest) entry
        List<string> lines = [];
        string? last = null;
        foreach (NewsItem item in items)
        {
            string day = item.Date.Trim();
            lines.Add(day.Length > 0 && day != last ? FormatDate(day) : "");
            if (day.Length > 0) last = day;
        }
        return lines;
    }

    /// <summary>Where each entry sits in its day, from the top: (1, 3), (2, 3)...; (0, 0) when it is the day's only one.</summary>
    public static IReadOnlyList<(int Number, int Of)> DayNumbers(IReadOnlyList<NewsItem> items)
    {
        Dictionary<string, int> perDay = items.Where(i => i.Date.Trim().Length > 0).GroupBy(i => i.Date.Trim()).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> seen = [];
        List<(int, int)> numbers = [];
        foreach (NewsItem item in items)
        {
            string day = item.Date.Trim();
            if (!perDay.TryGetValue(day, out int count) || count < 2) { numbers.Add((0, 0)); continue; }
            int n = seen.GetValueOrDefault(day) + 1;
            seen[day] = n;
            numbers.Add((n, count));
        }
        return numbers;
    }
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
        public override string ToString() => $"{Created:dd-MM-yyyy HH:mm:ss}  ({Words.Size(Size)})  {Path.GetFileName(File)}";
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
        for (int n = 2; File.Exists(file); n++)   // two backups in the same second
            file = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}-{n}.zip");
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
        known.UnionWith(TestingFiles.Installed(wowFolder));   // testing mode's own files
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
