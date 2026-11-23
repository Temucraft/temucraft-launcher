using System.Runtime.InteropServices;
using System.Text;

namespace Launcher.Core;

/// <summary>
/// "Copy diagnostics": a short text report a player can paste to whoever runs the server when the game won't start.
/// It lists Wow.exe (size, SHA-256, Large Address Aware, Windows compatibility mode), the DLLs next to it and the
/// patch MPQs, each marked against the server's copy when the server lists that file.
/// </summary>
public static class Diagnostics
{
    public static async Task<string> BuildAsync(string wowFolder, Manifest? manifest, IEnumerable<string> extraLines,
                                                HashCache? cache = null, CancellationToken token = default)
    {
        StringBuilder text = new();
        text.AppendLine($"Temucraft Launcher {LauncherInfo.Version}");
        text.AppendLine($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($"Game folder: {wowFolder}");
        foreach (string line in extraLines) text.AppendLine(line);
        foreach (string line in RecentCrashes(wowFolder)) text.AppendLine(line);

        Dictionary<string, ManifestFile> server = (manifest?.Files ?? [])
            .GroupBy(f => f.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string exe = GameFolder.ExePath(wowFolder);
        text.AppendLine();
        if (File.Exists(exe))
        {
            text.AppendLine(await FileLineAsync(wowFolder, exe, server, cache, token));
            try { text.AppendLine($"  Large Address Aware (4 GB): {(LargeAddressAware.IsEnabled(exe) ? "on" : "off")}"); }
            catch (Exception error) when (error is IOException or InvalidDataException) { text.AppendLine($"  Large Address Aware: can't read ({error.Message})"); }
            text.AppendLine($"  Windows compatibility mode: {CompatibilityMode(exe) ?? "off"}");
        }
        else text.AppendLine("Wow.exe: not found");

        List<string> files = [];
        if (Directory.Exists(wowFolder)) files.AddRange(Directory.GetFiles(wowFolder, "*.dll"));
        string data = Path.Combine(wowFolder, "Data");
        if (Directory.Exists(data))
        {
            files.AddRange(Directory.GetFiles(data, "*.mpq", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }));
            foreach (string locale in Directory.GetDirectories(data))
                files.AddRange(Directory.GetFiles(locale, "*.mpq", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }));
        }
        string dxvk = Path.Combine(wowFolder, "dxvk.conf");
        if (File.Exists(dxvk)) files.Add(dxvk);
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
            text.AppendLine(await FileLineAsync(wowFolder, file, server, cache, token));

        // files the server lists that are missing here
        foreach (ManifestFile file in server.Values.Where(f => f.Category is FileCategory.Patch))
            if (!File.Exists(Path.Combine(wowFolder, file.Path.Replace('/', Path.DirectorySeparatorChar))))
                text.AppendLine($"{file.Path}: missing");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The game's last few crash reports (Errors\*.txt): when, the error, and the top of the crashing thread's stack,
    /// so one paste says what went wrong without sending the whole file.
    /// </summary>
    public static List<string> RecentCrashes(string wowFolder, int count = 3)
    {
        List<string> lines = [];
        string errors = Path.Combine(wowFolder, "Errors");
        if (!Directory.Exists(errors)) return lines;
        FileInfo[] reports;
        try
        {
            reports = new DirectoryInfo(errors).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTime).Take(count).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return lines; }
        if (reports.Length == 0) return lines;
        lines.Add("");
        lines.Add($"Last crashes ({reports.Length}, newest first):");
        foreach (FileInfo report in reports)
        {
            string[] all;
            try { all = File.ReadAllLines(report.FullName); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            lines.Add($"- {report.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            int at = Array.FindIndex(all, l => l.StartsWith("ERROR #", StringComparison.Ordinal));
            if (at >= 0)
            {
                lines.Add("  " + all[at].Trim());
                // what follows the error until "WoWBuild": the exception line, or the game's own message
                for (int i = at + 1; i < all.Length && !all[i].StartsWith("WoWBuild", StringComparison.Ordinal); i++)
                {
                    string l = all[i].Trim();
                    if (l.Length == 0 || l.StartsWith("Program:", StringComparison.Ordinal)) continue;
                    lines.Add("  " + l);
                    // "Exception: 0xC0000005 (ACCESS_VIOLATION) at 0023:005CFEA1": name the function it crashed in
                    int colon = l.LastIndexOf(':');
                    if (l.StartsWith("Exception:", StringComparison.Ordinal) && colon > 0 && WowFunctions.Name(l[(colon + 1)..].Trim()) is { } where)
                        lines.Add("  Crashed in: " + where);
                }
            }
            int stack = Array.FindIndex(all, l => l.Contains("[Current Thread]", StringComparison.Ordinal));
            if (stack >= 0)
            {
                List<string> frames = all.Skip(stack + 1).TakeWhile(l => l.Trim().Length > 0).Take(12)
                    .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length >= 4)
                    .Select(p => Frame(p[0], string.Join(' ', p.Skip(3)).Split('\\', '/')[^1]))
                    .ToList();
                lines.Add("  Stack:");
                foreach (string frame in frames) lines.Add("    " + frame);
            }
        }
        return lines;
    }

    /// <summary>"005CFEA1 Wow.exe CGReputationInfo::AddHeader+0x31": a stack frame, named when it's in Wow.exe.</summary>
    static string Frame(string address, string module)
    {
        string line = address + " " + module;
        if (module.Equals("Wow.exe", StringComparison.OrdinalIgnoreCase) && WowFunctions.Name(address) is { } name)
            line += " " + name;
        return line;
    }

    static async Task<string> FileLineAsync(string wowFolder, string file, Dictionary<string, ManifestFile> server, HashCache? cache, CancellationToken token)
    {
        string relative = Path.GetRelativePath(wowFolder, file).Replace('\\', '/');
        long size = new FileInfo(file).Length;
        string sha;
        // the update check's hashes are reused: hashing 60 GB of patches again would take minutes
        try { sha = cache is not null ? await cache.GetAsync(file, token: token) : await Hashing.Sha256Async(file, token: token); }
        catch (IOException error) { return $"{relative}: {Words.Size(size)}, can't read ({error.Message})"; }
        string mark = server.TryGetValue(relative, out ManifestFile? copy)
            ? copy.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase) ? "same as the server" : "DIFFERENT from the server"
            : "not from the server";
        return $"{relative}: {Words.Size(size)}, {sha[..12]}, {mark}";
    }

    /// <summary>
    /// Takes Windows compatibility mode off Wow.exe for this Windows user (Windows can turn it on by itself after a
    /// crash). The 3.3.5a client runs best without it. Returns what was removed, or null.
    /// </summary>
    public static string? RemoveCompatibilityMode(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string key = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        string full = Path.GetFullPath(exe);
        try
        {
            using Microsoft.Win32.RegistryKey? layers = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, writable: true);
            if (layers is null) return null;
            foreach (string name in layers.GetValueNames())
                if (string.Equals(name, full, StringComparison.OrdinalIgnoreCase) && layers.GetValue(name) is string value)
                {
                    layers.DeleteValue(name, throwOnMissingValue: false);
                    return value.Trim();
                }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>The Windows compatibility settings on Wow.exe ("Run this program in compatibility mode for..."), or null.</summary>
    public static string? CompatibilityMode(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string key = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        string full = Path.GetFullPath(exe);
        List<string> found = [];
        foreach (Microsoft.Win32.RegistryKey root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using Microsoft.Win32.RegistryKey? layers = root.OpenSubKey(key);
                if (layers is null) continue;
                foreach (string name in layers.GetValueNames())
                    if (string.Equals(name, full, StringComparison.OrdinalIgnoreCase) && layers.GetValue(name) is string value && value.Trim().Length > 0)
                        found.Add(value.Trim());
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return found.Count > 0 ? string.Join("; ", found) : null;
    }
}

/// <summary>
/// The game's own list of turned off addons: WTF\Account\NAME\AddOns.txt and, per character,
/// WTF\Account\NAME\REALM\CHARACTER\AddOns.txt, with lines like "Questie: disabled".
/// </summary>
public static class AddonsTxt
{
    /// <summary>Turns the given addon folders back on in every AddOns.txt; returns how many lines changed.</summary>
    public static int EnableRequired(string wowFolder, IEnumerable<string> folders)
    {
        HashSet<string> needed = folders.Where(f => f.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string accounts = Path.Combine(wowFolder, "WTF", "Account");
        if (needed.Count == 0 || !Directory.Exists(accounts)) return 0;
        int changed = 0;
        foreach (string file in Directory.EnumerateFiles(accounts, "AddOns.txt",
                     new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }))
        {
            try
            {
                string[] lines = File.ReadAllLines(file);
                int before = changed;
                for (int i = 0; i < lines.Length; i++)
                {
                    int colon = lines[i].LastIndexOf(':');
                    if (colon <= 0) continue;
                    string name = lines[i][..colon].Trim();
                    if (needed.Contains(name) && lines[i][(colon + 1)..].Trim().Equals("disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{name}: enabled";
                        changed++;
                    }
                }
                if (changed != before) File.WriteAllLines(file, lines);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.Warning($"Couldn't check {file}", error);
            }
        }
        return changed;
    }
}
