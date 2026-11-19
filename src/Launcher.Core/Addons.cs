using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>An addon folder in Interface\AddOns, as its .toc describes it.</summary>
public sealed record InstalledAddon(string Folder, string Title, string Notes, string Version, string Author, bool FromServer);

/// <summary>An addon the server recommends (addons.json next to the manifest), hosted on GitHub.</summary>
public sealed class CatalogAddon
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>owner/repository on GitHub.</summary>
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "master";
    /// <summary>The addon folders to take from the repository (empty: every folder with a .toc).</summary>
    public List<string> Folders { get; set; } = [];
    /// <summary>A sub folder of the repository that holds the addon folders (e.g. "AddOns").</summary>
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Needed to play on the server: installed and kept up to date automatically.</summary>
    public bool Required { get; set; }

    public string Url => $"https://github.com/{Repo}";
    public string ReadmeUrl => $"https://raw.githubusercontent.com/{Repo}/{Branch}/README.md";
    public string ZipUrl => $"https://codeload.github.com/{Repo}/zip/refs/heads/{Branch}";
}

public static class AddonFolder
{
    static readonly Regex ColorCodes = new(@"\|c[0-9a-fA-F]{8}|\|r", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string Path(string wowFolder) => System.IO.Path.Combine(wowFolder, "Interface", "AddOns");

    public static IReadOnlyList<CatalogAddon> ParseCatalog(string json)
    {
        List<CatalogAddon> list = JsonSerializer.Deserialize<List<CatalogAddon>>(json, Json) ?? [];
        foreach (CatalogAddon addon in list)
        {
            if (!Regex.IsMatch(addon.Repo, @"^[\w.-]+/[\w.-]+$") || !Regex.IsMatch(addon.Branch, @"^[\w./-]+$") || addon.Branch.Contains(".."))
                throw new InvalidDataException($"addons.json: bad repository for {addon.Name}.");
            foreach (string folder in addon.Folders) PathRules.CheckRelative(folder);
            if (addon.Path.Length > 0) PathRules.CheckRelative(addon.Path);
        }
        return list;
    }

    /// <summary>Every addon folder with its .toc's title, notes, version and author.</summary>
    public static IReadOnlyList<InstalledAddon> Installed(string wowFolder, Manifest? manifest)
    {
        string root = Path(wowFolder);
        if (!Directory.Exists(root)) return [];
        HashSet<string> fromServer = manifest?.Files.Where(f => f.Category == FileCategory.Addon)
            .Select(f => f.Path.Split('/')).Where(p => p.Length > 2).Select(p => p[2]).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        List<InstalledAddon> list = [];
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
            string toc = System.IO.Path.Combine(dir, name + ".toc");
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(toc))
                foreach (string line in File.ReadLines(toc).Take(60))
                    if (line.StartsWith("## ") && line.IndexOf(':') is var colon and > 3)
                        fields.TryAdd(line[3..colon].Trim(), ColorCodes.Replace(line[(colon + 1)..].Trim(), ""));
            list.Add(new InstalledAddon(name, fields.GetValueOrDefault("Title", name), fields.GetValueOrDefault("Notes", ""),
                fields.GetValueOrDefault("Version", ""), fields.GetValueOrDefault("Author", ""), fromServer.Contains(name)));
        }
        return list;
    }

    /// <summary>Moves an addon folder into the launcher's backup folder (nothing is deleted outright).</summary>
    public static string Remove(string wowFolder, string folder, string backupRoot)
    {
        PathRules.CheckRelative(folder);
        string source = System.IO.Path.Combine(Path(wowFolder), folder);
        string target = System.IO.Path.Combine(backupRoot, "removed-addons", $"{folder}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        return target;
    }

    /// <summary>The README of a catalog addon, for the preview (plain text; empty when it has none).</summary>
    public static async Task<string> ReadmeAsync(HttpClient http, CatalogAddon addon, CancellationToken token = default)
    {
        using HttpResponseMessage response = await http.GetAsync(addon.ReadmeUrl, token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : "";
    }

    /// <summary>The newest commit of the addon's branch (to tell whether an update is out).</summary>
    public static async Task<string?> LatestCommitAsync(HttpClient http, CatalogAddon addon, CancellationToken token = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{addon.Repo}/commits/{addon.Branch}");
        request.Headers.Accept.ParseAdd("application/vnd.github.sha");
        using HttpResponseMessage response = await http.SendAsync(request, token);
        return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(token)).Trim() : null;
    }

    /// <summary>
    /// Installs the addon's folders into Interface\AddOns, replacing older copies. An addon kept in a sub folder of a
    /// bigger repository (<see cref="CatalogAddon.Path"/>) downloads only its own files, through GitHub's file list;
    /// otherwise (or when GitHub's API can't be used, e.g. its hourly limit) the repository's zip is downloaded.
    /// </summary>
    public static async Task<IReadOnlyList<string>> InstallAsync(HttpClient http, string wowFolder, CatalogAddon addon,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        status?.Report($"Downloading {addon.Name}…");
        IReadOnlyList<AddonFile>? files = null;
        if (addon.Path.Length > 0)
        {
            try { files = await FromTreeAsync(http, addon, token); }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or JsonException)
            {
                Logger.Warning($"{addon.Name}: couldn't list the files on GitHub, downloading the whole repository instead", e);
            }
        }
        files ??= await FromZipAsync(http, addon, token);
        return await InstallFilesAsync(wowFolder, addon, files, status, token);
    }

    /// <summary>One file of an addon: its addon folder ("" when the repository itself is the addon) and where in it.</summary>
    public sealed record AddonFile(string Folder, string Relative, Func<CancellationToken, Task<byte[]>> Read);

    /// <summary>Splits paths below the addon root into addon files: folders holding &lt;name&gt;/&lt;name&gt;.toc.</summary>
    static List<AddonFile> Pick(CatalogAddon addon, IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>> Read)> entries)
    {
        List<(string Path, Func<CancellationToken, Task<byte[]>> Read)> all = entries.ToList();
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        bool rootIsAddon = false;
        foreach ((string path, _) in all)
        {
            string[] parts = path.Split('/');
            if (parts.Length == 2 && parts[1].Equals(parts[0] + ".toc", StringComparison.OrdinalIgnoreCase)) folders.Add(parts[0]);
            if (parts.Length == 1 && parts[0].EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) rootIsAddon = true;
        }
        if (addon.Folders.Count > 0) folders.IntersectWith(addon.Folders);
        List<AddonFile> picked = [];
        if (folders.Count > 0)
        {
            foreach ((string path, var read) in all)
            {
                int slash = path.IndexOf('/');
                if (slash > 0 && folders.Contains(path[..slash]))
                    picked.Add(new AddonFile(folders.First(f => f.Equals(path[..slash], StringComparison.OrdinalIgnoreCase)), path[(slash + 1)..], read));
            }
        }
        else if (rootIsAddon && addon.Folders.Count == 0)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(all.First(e => !e.Path.Contains('/') && e.Path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)).Path);
            picked.AddRange(all.Select(e => new AddonFile(name, e.Path, e.Read)));
        }
        if (picked.Count == 0) throw new InvalidDataException($"{addon.Name}: no addon folder found in the repository.");
        return picked;
    }

    static string Root(CatalogAddon addon) => addon.Path.Length > 0 ? addon.Path.Trim('/') + "/" : "";

    static async Task<IReadOnlyList<AddonFile>> FromZipAsync(HttpClient http, CatalogAddon addon, CancellationToken token)
    {
        byte[] zipBytes = await http.GetByteArrayAsync(addon.ZipUrl, token);
        Logger.Info($"{addon.Name}: downloaded the repository zip ({Words.Size(zipBytes.Length)})", toWindow: false);
        ZipArchive zip = new(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        // GitHub zips have one top folder (repo-branch)
        string prefix = zip.Entries.FirstOrDefault()?.FullName.Split('/')[0] + "/" + Root(addon);
        return Pick(addon, zip.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
            .Select(e => (e.FullName[prefix.Length..], (Func<CancellationToken, Task<byte[]>>)(async t =>
            {
                await using Stream s = e.Open();
                using MemoryStream m = new();
                await s.CopyToAsync(m, t);
                return m.ToArray();
            }))).ToList());
    }


    /// <summary>Lists the repository's files at the branch's newest commit and keeps the ones below the addon's path.</summary>
    static async Task<IReadOnlyList<AddonFile>> FromTreeAsync(HttpClient http, CatalogAddon addon, CancellationToken token)
    {
        string commit = await LatestCommitAsync(http, addon, token) ?? throw new HttpRequestException("no commit for the branch");
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{addon.Repo}/git/trees/{commit}?recursive=1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (doc.RootElement.TryGetProperty("truncated", out JsonElement truncated) && truncated.GetBoolean())
            throw new InvalidDataException("the file list is too long");
        string root = Root(addon);
        List<string> paths = doc.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString()!)
            .Where(p => p.StartsWith(root, StringComparison.Ordinal))
            .ToList();
        Logger.Info($"{addon.Name}: {Words.Files(paths.Count)} below {root} at {commit[..Math.Min(7, commit.Length)]}", toWindow: false);
        return Pick(addon, paths.Select(p => (p[root.Length..], (Func<CancellationToken, Task<byte[]>>)(t =>
            http.GetByteArrayAsync($"https://raw.githubusercontent.com/{addon.Repo}/{commit}/" +
                                   string.Join('/', p.Split('/').Select(Uri.EscapeDataString)), t)))).ToList());
    }

    /// <summary>Writes each addon folder to a hidden staging folder, then swaps it in for the old copy.</summary>
    public static async Task<IReadOnlyList<string>> InstallFilesAsync(string wowFolder, CatalogAddon addon, IReadOnlyList<AddonFile> files,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        string root = Path(wowFolder);
        Directory.CreateDirectory(root);
        RemoveLeftovers(root);
        List<string> installed = [];
        foreach (IGrouping<string, AddonFile> folder in files.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase))
        {
            string name = folder.Key;
            PathRules.CheckRelative(name);
            string staging = System.IO.Path.Combine(root, "." + name + ".new");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            int done = 0, total = folder.Count();
            foreach (AddonFile file in folder)
            {
                token.ThrowIfCancellationRequested();
                string target = PathRules.Resolve(staging, file.Relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, await file.Read(token), token);
                if (++done % 10 == 0 || done == total) status?.Report($"{addon.Name}: {done} / {Words.Files(total)}");
            }
            string final = System.IO.Path.Combine(root, name);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(staging, final);
            installed.Add(name);
            status?.Report($"Installed {name}");
        }
        return installed;
    }

    /// <summary>Hidden ".Name.new" staging folders left by an install that was cut off.</summary>
    static void RemoveLeftovers(string root)
    {
        foreach (string dir in Directory.GetDirectories(root, ".*.new"))
            try { Directory.Delete(dir, true); Logger.Info($"Removed the unfinished install {System.IO.Path.GetFileName(dir)}", toWindow: false); }
            catch (IOException e) { Logger.Warning($"Couldn't remove {dir}", e); }
    }
}

/// <summary>Turns a README's Markdown into readable plain text for the addon preview.</summary>
public static class Markdown
{
    static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)\s]+)[^)]*\)", RegexOptions.Compiled);
    static readonly Regex Html = new(@"<[^>]+>", RegexOptions.Compiled);
    static readonly Regex Emphasis = new(@"(\*\*|__|\*|`)", RegexOptions.Compiled);
    static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);

    public static string ToPlainText(string markdown)
    {
        List<string> lines = [];
        bool code = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```")) { code = !code; continue; }
            if (code) { lines.Add("    " + raw); continue; }
            string line = Image.Replace(raw, "");
            line = Link.Replace(line, m => m.Groups[1].Value == m.Groups[2].Value ? m.Groups[2].Value : $"{m.Groups[1].Value} ({m.Groups[2].Value})");
            line = Html.Replace(line, "");
            bool heading = Heading.IsMatch(line);
            line = Emphasis.Replace(Heading.Replace(line, ""), "");
            if (heading) line = line.ToUpperInvariant();
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ")) line = line.Replace("- ", "• ").Replace("* ", "• ");
            if (line.Trim().Length == 0 && raw.Trim().Length > 0) continue;   // a line that was only a badge or tag
            lines.Add(line);
        }
        return string.Join('\n', lines).Trim();
    }
}
