namespace Launcher.Core;

/// <summary>
/// Testing mode, for the server owner: a testing.env next to the launcher lists local files (test MPQs, a test addon)
/// that are copied into the game folder every time Play is pressed. Each line is
///     &lt;file on this computer&gt; = &lt;path inside the WoW folder&gt;
/// for example
///     F:\TemuCraft Server\_dev\login-tools\patch-enUS-Z.MPQ = Data\{locale}\patch-enUS-Z.MPQ
/// "{locale}" is replaced by every game language the player picked (enUS, deDE ...), and in the file name too.
/// Lines starting with # are comments. The files the launcher put in place are remembered; when testing.env is gone
/// (or a line is removed), Play takes them out of the game folder again. testing.env is never in git.
/// Use names the server doesn't send (patch-enUS-Z, not patch-enUS-T), or the update check will want to replace them.
/// </summary>
public static class TestingFiles
{
    public const string FileName = "testing.env";

    static string RecordOf(string wowFolder) => Path.Combine(wowFolder, "_Temucraft", "testing-installed.txt");

    /// <summary>The game-folder paths testing mode put in place last time (not "untracked": the launcher owns them).</summary>
    public static HashSet<string> Installed(string wowFolder)
    {
        string record = RecordOf(wowFolder);
        return File.Exists(record)
            ? File.ReadAllLines(record).Where(l => l.Length > 0).Select(l => l.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static bool Enabled(string launcherFolder) => File.Exists(Path.Combine(launcherFolder, FileName));

    /// <summary>The (source, target inside the game folder) pairs from testing.env; bad lines are reported in errors.</summary>
    public static List<(string Source, string Target)> Read(string envFile, IEnumerable<string> locales, List<string> errors)
    {
        List<(string, string)> pairs = [];
        string[] picked = locales.Where(l => l.Length == 4).DefaultIfEmpty("enUS").ToArray();
        int number = 0;
        foreach (string raw in File.ReadAllLines(envFile))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int split = line.LastIndexOf('=');
            if (split <= 0)
            {
                errors.Add($"testing.env line {number}: expected <file> = <path in the WoW folder>");
                continue;
            }
            string source = line[..split].Trim().Trim('"');
            string target = line[(split + 1)..].Trim().Trim('"').Replace('\\', '/');
            foreach (string locale in target.Contains("{locale}") || source.Contains("{locale}") ? picked : [picked[0]])
            {
                string s = source.Replace("{locale}", locale), t = target.Replace("{locale}", locale);
                try { PathRules.CheckRelative(t); }
                catch (InvalidDataException) { errors.Add($"testing.env line {number}: {t} is not a path inside the WoW folder"); continue; }
                if (!File.Exists(s)) { errors.Add($"testing.env line {number}: {s} does not exist"); continue; }
                pairs.Add((s, t));
            }
        }
        return pairs;
    }

    /// <summary>
    /// Copies the testing files into the game folder (only when changed) and removes the ones from last time that are
    /// no longer listed. Returns what it did, one line each, for the log.
    /// </summary>
    public static List<string> Apply(string launcherFolder, string wowFolder, IEnumerable<string> locales)
    {
        List<string> log = [];
        string record = RecordOf(wowFolder);
        HashSet<string> before = File.Exists(record)
            ? File.ReadAllLines(record).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<(string Source, string Target)> pairs = [];
        string env = Path.Combine(launcherFolder, FileName);
        if (File.Exists(env))
        {
            List<string> errors = [];
            pairs = Read(env, locales, errors);
            log.AddRange(errors.Select(e => "Testing: " + e));
        }

        HashSet<string> now = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string source, string target) in pairs)
        {
            string full = PathRules.Resolve(wowFolder, target);
            now.Add(target);
            FileInfo from = new(source), to = new(full);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // a new file moved over the old name, never written into it: in a PTR folder the old one may be a Live file's hard link
            File.Copy(source, full + ".tmp", overwrite: true);
            File.SetLastWriteTimeUtc(full + ".tmp", from.LastWriteTimeUtc);
            File.Move(full + ".tmp", full, overwrite: true);
            log.Add($"Testing: copied {source} to {target}");
        }
        foreach (string old in before.Where(b => !now.Contains(b)))
        {
            try
            {
                string full = PathRules.Resolve(wowFolder, old);
                if (File.Exists(full)) { File.Delete(full); log.Add($"Testing: removed {old} (no longer in testing.env)"); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log.Add($"Testing: could not remove {old}: {e.Message}");
            }
        }

        if (now.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllLines(record, now);
        }
        else if (File.Exists(record))
            File.Delete(record);
        return log;
    }
}
