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
        foreach (string line in TemucraftCrash(wowFolder)) text.AppendLine(line);
        foreach (string line in RecentCrashes(wowFolder)) text.AppendLine(line);
        foreach (string line in UiErrors(wowFolder)) text.AppendLine(line);

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
            // Temucraft-*.txt are the crash helper's own reports (TemucraftCrash below), not the game's
            reports = new DirectoryInfo(errors).GetFiles("*.txt")
                .Where(f => !f.Name.StartsWith(CrashHelperPrefix, StringComparison.OrdinalIgnoreCase))
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

    const string CrashHelperPrefix = "Temucraft-";

    /// <summary>
    /// The newest report of the crash helper (the "Crash reports" mod: version.dll next to Wow.exe writes
    /// Errors\Temucraft-date-time.txt and a .dmp when the game crashes): what crashed, the top of the stack, the last
    /// packets and Lua errors. Empty when there is none.
    /// </summary>
    public static List<string> TemucraftCrash(string wowFolder)
    {
        List<string> lines = [];
        FileInfo? report = NewestCrashHelperReport(wowFolder);
        if (report is null) return lines;
        string[] all;
        try { all = File.ReadAllLines(report.FullName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return lines; }
        lines.Add("");
        lines.Add($"Newest Temucraft crash report: {report.Name} ({report.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
        string[] keep = ["What happened:", "Crashed at:", "Called from:", "It tried to", "Game's message:", "Hooks:"];
        foreach (string l in all)
            if (keep.Any(k => l.StartsWith(k, StringComparison.Ordinal))) lines.Add("  " + l.Trim());
        AddSection(lines, all, "Stack of the crashing thread", "  Stack:", 10);
        AddSection(lines, all, "Last server packets received", "  Last server packets:", 6);
        AddSection(lines, all, "Last client packets sent", "  Last client packets:", 4);
        AddSection(lines, all, "Last Lua errors", "  Last Lua errors:", 3);
        string dump = Path.ChangeExtension(report.FullName, ".dmp");
        lines.Add(File.Exists(dump) ? $"  Minidump: {dump} ({Words.Size(new FileInfo(dump).Length)})" : "  Minidump: none");
        return lines;
    }

    /// <summary>The first lines of a report section ("Last Lua errors (...):" and the lines under it).</summary>
    static void AddSection(List<string> lines, string[] all, string title, string heading, int max)
    {
        int at = Array.FindIndex(all, l => l.StartsWith(title, StringComparison.Ordinal));
        if (at < 0) return;
        List<string> found = all.Skip(at + 1).TakeWhile(l => l.Trim().Length > 0)
            .Select(l => l.Trim()).Where(l => l != "(none)").Take(max).ToList();
        if (found.Count == 0) return;
        lines.Add(heading);
        foreach (string l in found) lines.Add("    " + (l.Length > 300 ? l[..300] + "..." : l));
    }

    static FileInfo? NewestCrashHelperReport(string wowFolder)
    {
        string errors = Path.Combine(wowFolder, "Errors");
        if (!Directory.Exists(errors)) return null;
        try
        {
            return new DirectoryInfo(errors).GetFiles(CrashHelperPrefix + "*.txt")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The .dmp of the newest crash helper report, when it is newer than maxAge; else null.</summary>
    public static string? NewestCrashDump(string wowFolder, TimeSpan maxAge)
    {
        FileInfo? report = NewestCrashHelperReport(wowFolder);
        if (report is null || DateTime.Now - report.LastWriteTime > maxAge) return null;
        string dump = Path.ChangeExtension(report.FullName, ".dmp");
        return File.Exists(dump) ? dump : null;
    }

    /// <summary>
    /// The newest UI errors and blocked actions from !TemucraftCompat's error log (the saved variable
    /// TemucraftErrorLog, one file per account), so a broken frame that shows no error still says why.
    /// </summary>
    public static List<string> UiErrors(string wowFolder, int count = 8)
    {
        List<string> lines = [];
        string accounts = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(accounts)) return lines;
        List<(string Account, string Message, string Count, string Last)> found = [];
        try
        {
            foreach (string account in Directory.GetDirectories(accounts))
            {
                string file = Path.Combine(account, "SavedVariables", "!TemucraftCompat.lua");
                if (!File.Exists(file)) continue;
                string? message = null, times = null, last = null;
                foreach (string raw in File.ReadLines(file))
                {
                    string l = raw.Trim();
                    if (l.StartsWith("[\"msg\"] = ", StringComparison.Ordinal)) message = LuaString(l);
                    else if (l.StartsWith("[\"count\"] = ", StringComparison.Ordinal)) times = l[12..].TrimEnd(',');
                    else if (l.StartsWith("[\"last\"] = ", StringComparison.Ordinal)) last = LuaString(l);
                    else if (l.StartsWith('}'))
                    {
                        if (message is not null) found.Add((Path.GetFileName(account), message, times ?? "1", last ?? ""));
                        message = times = last = null;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return lines; }
        string? textFile = ExportUiErrorText(wowFolder);
        if (found.Count == 0) return lines;
        lines.Add("");
        lines.Add($"UI errors ({found.Count} logged, newest {Math.Min(count, found.Count)}; saved at logout, /reload or /tcerrors save):");
        if (textFile is not null) lines.Add($"  The whole log with stacks: {textFile}");
        foreach (var e in found.OrderByDescending(e => e.Last, StringComparer.Ordinal).Take(count))
            lines.Add($"- {e.Last} x{e.Count}: {(e.Message.Length > 300 ? e.Message[..300] + "..." : e.Message)}");
        return lines;
    }

    /// <summary>
    /// The plain text copy of the UI error log (TemucraftErrorLog.text, stored by /tcerrors save and at logout) of every
    /// account, written to Logs\TemucraftUiErrors.txt so it can be opened and sent as a file. Returns that path, or
    /// null when no account has a text copy.
    /// </summary>
    public static string? ExportUiErrorText(string wowFolder)
    {
        string accounts = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(accounts)) return null;
        StringBuilder all = new();
        try
        {
            foreach (string account in Directory.GetDirectories(accounts))
            {
                string file = Path.Combine(account, "SavedVariables", "!TemucraftCompat.lua");
                if (!File.Exists(file)) continue;
                foreach (string raw in File.ReadLines(file))
                {
                    string l = raw.Trim();
                    if (!l.StartsWith("[\"text\"] = ", StringComparison.Ordinal)) continue;
                    all.AppendLine($"===== Account {Path.GetFileName(account)} =====");
                    all.AppendLine(LuaString(l, keepLines: true).Replace("\n", Environment.NewLine));
                    all.AppendLine();
                }
            }
            if (all.Length == 0) return null;
            string logs = Path.Combine(wowFolder, "Logs");
            Directory.CreateDirectory(logs);
            string path = Path.Combine(logs, "TemucraftUiErrors.txt");
            File.WriteAllText(path, all.ToString());
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The text of a saved-variable line such as ["msg"] = "a \"b\"\nc",</summary>
    static string LuaString(string line, bool keepLines = false)
    {
        int start = line.IndexOf("= \"", StringComparison.Ordinal);
        if (start < 0) return "";
        System.Text.StringBuilder text = new();
        for (int i = start + 3; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') break;
            if (c == '\\' && i + 1 < line.Length)
            {
                char n = line[++i];
                if (keepLines && n == 'n') text.Append('\n');
                else if (char.IsDigit(n))
                {
                    // \ddd: a byte by its number (the game writes other control characters this way)
                    int end = i;
                    while (end + 1 < line.Length && end - i < 2 && char.IsDigit(line[end + 1])) end++;
                    int code = int.Parse(line.AsSpan(i, end - i + 1));
                    i = end;
                    text.Append(keepLines && code == 10 ? '\n' : code < 32 ? ' ' : (char)code);
                }
                else text.Append(n switch { 'n' => ' ', 't' => ' ', _ => n });
            }
            else text.Append(c);
        }
        return text.ToString();
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
