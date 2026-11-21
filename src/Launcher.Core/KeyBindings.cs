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
/// The 3.3.5 game's own key bindings. bindings-cache.wtf only lists what differs from these (a key on another command,
/// or <c>bind KEY NONE</c> for a key taken off), so the keys a command really has are these, changed by the file.
/// </summary>
public static class KeyDefaults
{
    const string Table = "BUTTON3 MOVEANDSTEER|W MOVEFORWARD|UP MOVEFORWARD|S MOVEBACKWARD|DOWN MOVEBACKWARD|A TURNLEFT|LEFT TURNLEFT|D TURNRIGHT|RIGHT TURNRIGHT|Q STRAFELEFT|E STRAFERIGHT|SPACE JUMP|NUMPAD0 JUMP|X SITORSTAND|Z TOGGLESHEATH|NUMLOCK TOGGLEAUTORUN|BUTTON4 TOGGLEAUTORUN|INSERT PITCHUP|DELETE PITCHDOWN|NUMPADDIVIDE TOGGLERUN|ENTER OPENCHAT|/ OPENCHATSLASH|PAGEUP CHATPAGEUP|PAGEDOWN CHATPAGEDOWN|SHIFT-PAGEDOWN CHATBOTTOM|CTRL-PAGEUP COMBATLOGPAGEUP|CTRL-PAGEDOWN COMBATLOGPAGEDOWN|CTRL-SHIFT-PAGEDOWN COMBATLOGBOTTOM|R REPLY|SHIFT-R REPLY2|1 ACTIONBUTTON1|2 ACTIONBUTTON2|3 ACTIONBUTTON3|4 ACTIONBUTTON4|5 ACTIONBUTTON5|6 ACTIONBUTTON6|7 ACTIONBUTTON7|8 ACTIONBUTTON8|9 ACTIONBUTTON9|0 ACTIONBUTTON10|- ACTIONBUTTON11|= ACTIONBUTTON12|CTRL-F1 SHAPESHIFTBUTTON1|CTRL-F2 SHAPESHIFTBUTTON2|CTRL-F3 SHAPESHIFTBUTTON3|CTRL-F4 SHAPESHIFTBUTTON4|CTRL-F5 SHAPESHIFTBUTTON5|CTRL-F6 SHAPESHIFTBUTTON6|CTRL-F7 SHAPESHIFTBUTTON7|CTRL-F8 SHAPESHIFTBUTTON8|CTRL-F9 SHAPESHIFTBUTTON9|CTRL-F10 SHAPESHIFTBUTTON10|CTRL-1 BONUSACTIONBUTTON1|CTRL-2 BONUSACTIONBUTTON2|CTRL-3 BONUSACTIONBUTTON3|CTRL-4 BONUSACTIONBUTTON4|CTRL-5 BONUSACTIONBUTTON5|CTRL-6 BONUSACTIONBUTTON6|CTRL-7 BONUSACTIONBUTTON7|CTRL-8 BONUSACTIONBUTTON8|CTRL-9 BONUSACTIONBUTTON9|CTRL-0 BONUSACTIONBUTTON10|SHIFT-1 ACTIONPAGE1|SHIFT-2 ACTIONPAGE2|SHIFT-3 ACTIONPAGE3|SHIFT-4 ACTIONPAGE4|SHIFT-5 ACTIONPAGE5|SHIFT-6 ACTIONPAGE6|SHIFT-UP PREVIOUSACTIONPAGE|SHIFT-MOUSEWHEELUP PREVIOUSACTIONPAGE|SHIFT-DOWN NEXTACTIONPAGE|SHIFT-MOUSEWHEELDOWN NEXTACTIONPAGE|TAB TARGETNEARESTENEMY|SHIFT-TAB TARGETPREVIOUSENEMY|CTRL-TAB TARGETNEARESTFRIEND|CTRL-SHIFT-TAB TARGETPREVIOUSFRIEND|F1 TARGETSELF|SHIFT-F1 TARGETPET|F2 TARGETPARTYMEMBER1|F3 TARGETPARTYMEMBER2|F4 TARGETPARTYMEMBER3|F5 TARGETPARTYMEMBER4|SHIFT-F2 TARGETPARTYPET1|SHIFT-F3 TARGETPARTYPET2|SHIFT-F4 TARGETPARTYPET3|SHIFT-F5 TARGETPARTYPET4|G TARGETLASTHOSTILE|F ASSISTTARGET|V NAMEPLATES|SHIFT-V FRIENDNAMEPLATES|CTRL-V ALLNAMEPLATES|T ATTACKTARGET|SHIFT-T PETATTACK|C TOGGLECHARACTER0|B TOGGLEBACKPACK|F12 TOGGLEBACKPACK|F8 TOGGLEBAG1|F9 TOGGLEBAG2|F10 TOGGLEBAG3|F11 TOGGLEBAG4|SHIFT-B OPENALLBAGS|P TOGGLESPELLBOOK|SHIFT-I TOGGLEPETBOOK|SHIFT-P TOGGLECHARACTER3|SHIFT-SPACE TOGGLEWORLDSTATESCORES|SHIFT-M TOGGLEBATTLEFIELDMINIMAP|H TOGGLECHARACTER4|N TOGGLETALENTS|U TOGGLECHARACTER2|K TOGGLECHARACTER1|O TOGGLESOCIAL|L TOGGLEQUESTLOG|Y TOGGLEACHIEVEMENT|I TOGGLELFGPARENT|SHIFT-C TOGGLECOMBATLOG|ESCAPE TOGGLEGAMEMENU|M TOGGLEWORLDMAP|PRINTSCREEN SCREENSHOT|NUMPADPLUS MINIMAPZOOMIN|NUMPADMINUS MINIMAPZOOMOUT|CTRL-M TOGGLEMUSIC|CTRL-S TOGGLESOUND|CTRL-= MASTERVOLUMEUP|CTRL-- MASTERVOLUMEDOWN|ALT-Z TOGGLEUI|MOUSEWHEELUP CAMERAZOOMIN|MOUSEWHEELDOWN CAMERAZOOMOUT|END NEXTVIEW|HOME PREVVIEW|CTRL-R TOGGLEFPS|BUTTON2 TURNORACTION|BUTTON1 CAMERAORSELECTORMOVE|ALT-1 SELFACTIONBUTTON1|ALT-2 SELFACTIONBUTTON2|ALT-3 SELFACTIONBUTTON3|ALT-4 SELFACTIONBUTTON4|ALT-5 SELFACTIONBUTTON5|ALT-6 SELFACTIONBUTTON6|ALT-7 SELFACTIONBUTTON7|ALT-8 SELFACTIONBUTTON8|ALT-9 SELFACTIONBUTTON9|ALT-0 SELFACTIONBUTTON10|ALT-- SELFACTIONBUTTON11|ALT-= SELFACTIONBUTTON12|CTRL-BUTTON1 CAMERAORSELECTORMOVESTICKY";

    /// <summary>Key -> command.</summary>
    public static IReadOnlyDictionary<string, string> ByKey { get; } = Table.Split('|')
        .Select(p => p.Split(' ', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

    /// <summary>Command -> its default keys, in the game's order.</summary>
    public static IReadOnlyDictionary<string, List<string>> ByCommand { get; } = Table.Split('|')
        .Select(p => p.Split(' ', 2)).GroupBy(p => p[1], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(p => p[0]).ToList(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys every command has once a file's lines are put over the defaults.</summary>
    public static Dictionary<string, List<string>> Effective(IEnumerable<(string Key, string Command)> fileLines)
    {
        Dictionary<string, string> byKey = new(ByKey, StringComparer.OrdinalIgnoreCase);
        List<string> order = [.. ByKey.Keys];
        foreach ((string key, string command) in fileLines)
        {
            if (command.Equals("NONE", StringComparison.OrdinalIgnoreCase)) { byKey.Remove(key); continue; }
            if (!byKey.ContainsKey(key)) order.Add(key);
            byKey[key] = command;
        }
        Dictionary<string, List<string>> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in order)
            if (byKey.TryGetValue(key, out string? command))
            {
                if (!keys.TryGetValue(command, out List<string>? list)) keys[command] = list = [];
                if (!list.Contains(key, StringComparer.OrdinalIgnoreCase)) list.Add(key);
            }
        return keys;
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

    /// <summary>Every command and the keys it really has: the game's defaults with this file's lines put over them.</summary>
    public Dictionary<string, List<string>> Keys() =>
        KeyDefaults.Effective(_lines.Select(Parse).Where(p => p is not null).Select(p => p!.Value));

    /// <summary>Only the lines of this file, command -> keys (no defaults).</summary>
    public Dictionary<string, List<string>> FileKeys()
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
        // a default key of a changed command that no command has now: the game would give it back without a NONE line
        foreach (string command in commands)
            foreach (string key in KeyDefaults.ByCommand.GetValueOrDefault(command) ?? [])
                if (!taken.Contains(key) && !_lines.Any(l => Parse(l) is (string k, _) && k.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    _lines.Add($"bind {key} NONE");
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
