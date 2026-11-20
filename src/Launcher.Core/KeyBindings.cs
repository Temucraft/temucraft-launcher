using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>A command of the game's Key Bindings window (from the 3.3.5 Bindings.xml and its labels).</summary>
public sealed record KeyCommand
{
    public string Header { get; init; } = "";
    public string Command { get; init; } = "";
    public string Label { get; init; } = "";
}

public static class KeyCommands
{
    public static IReadOnlyList<KeyCommand> All { get; } = Load();

    static List<KeyCommand> Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Launcher.Core.key-bindings.json");
        if (stream is null) return [];
        return JsonSerializer.Deserialize<List<KeyCommand>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}

/// <summary>
/// A bindings-cache.wtf: lines of <c>bind KEY COMMAND</c> (plus BINDINGMODE and modifiedclick lines). The game writes
/// every key there once it has run; the account's file is for all characters, a character's own file wins when it
/// has lines (character specific key bindings).
/// </summary>
public sealed class BindingsFile
{
    readonly List<string> _lines;
    public string File { get; }
    public string Name { get; }

    BindingsFile(string file, string name, List<string> lines) { File = file; Name = name; _lines = lines; }

    public static BindingsFile Load(string file, string name) =>
        new(file, name, System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file).ToList() : []);

    public bool HasBindings => _lines.Any(l => l.StartsWith("bind ", StringComparison.OrdinalIgnoreCase));

    static (string Key, string Command)? Parse(string line)
    {
        if (!line.StartsWith("bind ", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = line[5..].Trim();
        int space = rest.IndexOf(' ');
        if (space <= 0) return null;
        return (rest[..space], rest[(space + 1)..].Trim());
    }

    /// <summary>Every command and its keys, in the file's order.</summary>
    public Dictionary<string, List<string>> Keys()
    {
        Dictionary<string, List<string>> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in _lines)
            if (Parse(line) is (string key, string command))
            {
                if (!keys.TryGetValue(command, out List<string>? list)) keys[command] = list = [];
                list.Add(key);
            }
        return keys;
    }

    /// <summary>Sets the keys of these commands; a key given to one command is taken off any other.</summary>
    public void Set(IReadOnlyDictionary<string, List<string>> changed)
    {
        HashSet<string> commands = new(changed.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<string> taken = new(changed.Values.SelectMany(k => k), StringComparer.OrdinalIgnoreCase);
        _lines.RemoveAll(l => Parse(l) is (string key, string command) && (commands.Contains(command) || taken.Contains(key)));
        foreach ((string command, List<string> keys) in changed)
            foreach (string key in keys.Where(k => k.Length > 0))
                _lines.Add($"bind {key} {command}");
    }

    /// <summary>The key of a modified click (<c>modifiedclick SELFCAST ALT</c>), null when the file doesn't set it.</summary>
    public string? GetModifiedClick(string action)
    {
        foreach (string line in _lines)
        {
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && p[0].Equals("modifiedclick", StringComparison.OrdinalIgnoreCase) && p[1].Equals(action, StringComparison.OrdinalIgnoreCase))
                return p[2];
        }
        return null;
    }

    public void SetModifiedClick(string action, string key)
    {
        string line = $"modifiedclick {action.ToUpperInvariant()} {key.ToUpperInvariant()}";
        int i = _lines.FindIndex(l =>
        {
            string[] p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length >= 2 && p[0].Equals("modifiedclick", StringComparison.OrdinalIgnoreCase) && p[1].Equals(action, StringComparison.OrdinalIgnoreCase);
        });
        if (i >= 0) _lines[i] = line; else _lines.Add(line);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllLines(File, _lines, new UTF8Encoding(false));
    }

    /// <summary>The bindings files of the account played last: the account's own, then each character's with lines.</summary>
    public static List<BindingsFile> Find(string wowFolder)
    {
        string root = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(root)) return [];
        string? account = Directory.GetDirectories(root)
            .OrderByDescending(d => Directory.GetFiles(d, "*.wtf").Select(System.IO.File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max())
            .FirstOrDefault();
        if (account is null) return [];
        List<BindingsFile> files = [Load(Path.Combine(account, "bindings-cache.wtf"), $"{Path.GetFileName(account)} (all characters)")];
        foreach (string realm in Directory.GetDirectories(account))
            foreach (string character in Directory.GetDirectories(realm))
            {
                BindingsFile own = Load(Path.Combine(character, "bindings-cache.wtf"), $"{Path.GetFileName(character)} - {Path.GetFileName(realm)}");
                if (own.HasBindings) files.Add(own);
            }
        return files;
    }

    /// <summary>The game's name for a key with modifiers: ALT-CTRL-SHIFT-F.</summary>
    public static string KeyName(string key, bool alt, bool ctrl, bool shift) =>
        (alt ? "ALT-" : "") + (ctrl ? "CTRL-" : "") + (shift ? "SHIFT-" : "") + key;
}
