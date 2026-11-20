using System.Text;

namespace Launcher.Core;

/// <summary>A character the game has a folder for: WTF\Account\&lt;account&gt;\&lt;realm&gt;\&lt;name&gt;.</summary>
public sealed record WowCharacter(string Account, string Realm, string Name, string Folder)
{
    /// <summary>The account's own folder: WTF\Account\&lt;account&gt;.</summary>
    public string AccountFolder => Path.GetDirectoryName(Path.GetDirectoryName(Folder)!)!;

    public override string ToString() => $"{Name} - {Realm} ({Account})";

    /// <summary>The accounts of the game folder, the one played last first.</summary>
    public static List<string> Accounts(string wowFolder)
    {
        string root = Path.Combine(wowFolder, "WTF", "Account");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .OrderByDescending(LastUsed)
            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every character of every account: accounts played last first, then by realm and name.</summary>
    public static List<WowCharacter> List(string wowFolder)
    {
        List<WowCharacter> found = [];
        foreach (string account in Accounts(wowFolder))
            foreach (string realm in Directory.GetDirectories(account).Where(d => !Path.GetFileName(d).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                foreach (string character in Directory.GetDirectories(realm).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(character, "config-cache.wtf")) || File.Exists(Path.Combine(character, "macros-cache.txt"))
                        || File.Exists(Path.Combine(character, "layout-local.txt")))
                        found.Add(new WowCharacter(Path.GetFileName(account), Path.GetFileName(realm), Path.GetFileName(character), character));
        return found;
    }

    static DateTime LastUsed(string folder) =>
        Directory.GetFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
}

/// <summary>One macro of the game's Macros window.</summary>
public sealed class Macro
{
    public const int NameLimit = 16;
    public const int BodyLimit = 255;
    public const string DefaultIcon = "INV_Misc_QuestionMark";

    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = DefaultIcon;
    /// <summary>The macro's text, lines split by \n.</summary>
    public string Body { get; set; } = "";

    public Macro Copy() => new() { Id = Id, Name = Name, Icon = Icon, Body = Body };
    public override string ToString() => Name;
}

/// <summary>
/// A macros-cache.txt: the account's macros (WTF\Account\&lt;account&gt;) or a character's (in its own folder). Each
/// macro is <c>MACRO &lt;id&gt; "&lt;name&gt;" &lt;icon&gt;</c>, its lines, then <c>END</c>. Account macros have small ids,
/// a character's start at 16777217. The game allows 36 account and 18 character macros.
/// </summary>
public sealed class MacroFile
{
    public const long CharacterIdBase = 16777216;
    public const string FileName = "macros-cache.txt";

    public string File { get; }
    public bool IsCharacter { get; }
    public List<Macro> Macros { get; }

    MacroFile(string file, bool isCharacter, List<Macro> macros) { File = file; IsCharacter = isCharacter; Macros = macros; }

    public int Limit => IsCharacter ? 18 : 36;

    public static MacroFile ForAccount(string accountFolder) => Load(Path.Combine(accountFolder, FileName), false);
    public static MacroFile ForCharacter(string characterFolder) => Load(Path.Combine(characterFolder, FileName), true);

    public static MacroFile Load(string file, bool isCharacter) =>
        new(file, isCharacter, System.IO.File.Exists(file) ? Parse(System.IO.File.ReadAllText(file)) : []);

    public static List<Macro> Parse(string text)
    {
        List<Macro> macros = [];
        Macro? current = null;
        List<string> body = [];
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (current is null)
            {
                if (ParseHeader(line) is { } header) { current = header; body.Clear(); }
                continue;
            }
            if (line == "END")
            {
                current.Body = string.Join("\n", body);
                macros.Add(current);
                current = null;
                continue;
            }
            body.Add(line);
        }
        if (current is not null) { current.Body = string.Join("\n", body); macros.Add(current); }   // a file cut short
        return macros;
    }

    static Macro? ParseHeader(string line)
    {
        if (!line.StartsWith("MACRO ", StringComparison.Ordinal)) return null;
        string rest = line[6..].TrimStart();
        int space = rest.IndexOf(' ');
        if (space <= 0 || !long.TryParse(rest[..space], out long id)) return null;
        rest = rest[(space + 1)..].TrimStart();
        string name, icon;
        if (rest.StartsWith('"') && rest.LastIndexOf('"') is int close and > 0)
        {
            name = rest[1..close];
            icon = rest[(close + 1)..].Trim();
        }
        else
        {
            int s = rest.IndexOf(' ');
            name = s < 0 ? rest : rest[..s];
            icon = s < 0 ? "" : rest[(s + 1)..].Trim();
        }
        return new Macro { Id = id, Name = name, Icon = icon.Length == 0 ? Macro.DefaultIcon : icon };
    }

    /// <summary>The file's text, as the game writes it (CRLF line ends).</summary>
    public static string Write(IEnumerable<Macro> macros)
    {
        StringBuilder text = new();
        foreach (Macro m in macros)
        {
            text.Append($"MACRO {m.Id} \"{CleanName(m.Name)}\" {CleanIcon(m.Icon)}\r\n");
            string body = CleanBody(m.Body);
            if (body.Length > 0)
                foreach (string line in body.Split('\n')) text.Append(line).Append("\r\n");
            text.Append("END\r\n");
        }
        return text.ToString();
    }

    public static string CleanName(string name)
    {
        string clean = new(name.Where(c => c is not ('"' or '\r' or '\n')).ToArray());
        clean = clean.Trim();
        if (clean.Length > Macro.NameLimit) clean = clean[..Macro.NameLimit];
        return clean.Length == 0 ? "Macro" : clean;
    }

    public static string CleanIcon(string icon)
    {
        string clean = new(icon.Where(c => !char.IsWhiteSpace(c) && c != '"').ToArray());
        return clean.Length == 0 ? Macro.DefaultIcon : clean;
    }

    /// <summary>The text as it is saved: \n line ends, a line of just END (which would end the macro) as "END ",
    /// at most 255 letters.</summary>
    public static string CleanBody(string body)
    {
        string clean = string.Join("\n", body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n').Select(l => l == "END" ? "END " : l));
        if (clean.Length > Macro.BodyLimit) clean = clean[..Macro.BodyLimit];
        if (clean == "END" || clean.EndsWith("\nEND", StringComparison.Ordinal)) clean = clean[..^1];
        return clean;
    }

    /// <summary>A new macro with the next free id of this file.</summary>
    public Macro Add(string name, string icon = Macro.DefaultIcon, string body = "")
    {
        Macro macro = new() { Id = NextId(Macros, IsCharacter), Name = name, Icon = icon, Body = body };
        Macros.Add(macro);
        return macro;
    }

    public static long NextId(IEnumerable<Macro> macros, bool isCharacter)
    {
        long first = isCharacter ? CharacterIdBase + 1 : 1;
        HashSet<long> used = macros.Select(m => m.Id).ToHashSet();
        long id = first;
        while (used.Contains(id)) id++;
        return id;
    }

    public string Text => Write(Macros);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, Text, new UTF8Encoding(false));
    }
}
