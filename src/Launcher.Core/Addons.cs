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
            .Select(f => f.Path.Split('/')[2]).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        List<InstalledAddon> list = [];
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>Downloads the repository and copies its addon folders into Interface\AddOns, replacing older copies.</summary>
    public static async Task<IReadOnlyList<string>> InstallAsync(HttpClient http, string wowFolder, CatalogAddon addon,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        status?.Report($"Downloading {addon.Name}…");
        byte[] zipBytes = await http.GetByteArrayAsync(addon.ZipUrl, token);
        using ZipArchive zip = new(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        // GitHub zips have one top folder (repo-branch); addon folders are the folders holding <name>/<name>.toc
        string prefix = zip.Entries.FirstOrDefault()?.FullName.Split('/')[0] + "/";
        if (addon.Path.Length > 0) prefix += addon.Path.Trim('/') + "/";
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string[] parts = e.FullName[prefix.Length..].Split('/');
            if (parts.Length == 2 && parts[1].Equals(parts[0] + ".toc", StringComparison.OrdinalIgnoreCase)) folders.Add(parts[0]);
            if (parts.Length == 1 && parts[0].EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) folders.Add("");   // the repo is the addon
        }
        if (addon.Folders.Count > 0) folders.IntersectWith(addon.Folders);
        if (folders.Count == 0) throw new InvalidDataException($"{addon.Name}: no addon folder found in the repository.");

        List<string> installed = [];
        string root = Path(wowFolder);
        foreach (string folder in folders)
        {
            string name = folder.Length > 0 ? folder
                : zip.Entries.First(e => e.FullName.StartsWith(prefix) && e.Name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)
                                         && !e.FullName[prefix.Length..].Contains('/')).Name[..^4];
            PathRules.CheckRelative(name);
            string staging = System.IO.Path.Combine(root, "." + name + ".new");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            string from = prefix + (folder.Length > 0 ? folder + "/" : "");
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (!e.FullName.StartsWith(from, StringComparison.Ordinal) || e.FullName.EndsWith('/')) continue;
                string relative = e.FullName[from.Length..];
                string target = PathRules.Resolve(staging, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }
            string final = System.IO.Path.Combine(root, name);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(staging, final);
            installed.Add(name);
            status?.Report($"Installed {name}");
        }
        return installed;
    }
}
