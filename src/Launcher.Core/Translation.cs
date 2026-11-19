using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// The small part of YAML the launcher's text and theme files use: one <c>key: value</c> per line, keys and values
/// optionally in double quotes (<c>\"</c>, <c>\n</c> and <c>\\</c> inside them), <c>#</c> comments. Nothing nested.
/// </summary>
public static class MiniYaml
{
    public static Dictionary<string, string> Parse(string text)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string key, rest;
            if (line[0] == '"')
            {
                int end = Quoted(line, 0, out key);
                if (end < 0) continue;
                rest = line[end..].TrimStart();
                if (!rest.StartsWith(':')) continue;
                rest = rest[1..];
            }
            else
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0 && line.EndsWith(':')) colon = line.Length - 1;
                if (colon <= 0) continue;
                key = line[..colon].Trim();
                rest = line[(colon + 1)..];
            }
            rest = rest.Trim();
            string value;
            if (rest.StartsWith('"')) { if (Quoted(rest, 0, out value) < 0) continue; }
            else
            {
                int hash = rest.IndexOf(" #", StringComparison.Ordinal);
                value = (hash >= 0 ? rest[..hash] : rest).Trim();
            }
            values[key] = value;
        }
        return values;
    }

    /// <summary>Reads a "quoted" string starting at <paramref name="start"/>; returns the index after it (-1: no end).</summary>
    static int Quoted(string line, int start, out string text)
    {
        StringBuilder b = new();
        for (int i = start + 1; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { text = b.ToString(); return i + 1; }
            if (c == '\\' && i + 1 < line.Length)
            {
                char next = line[++i];
                b.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
            }
            else b.Append(c);
        }
        text = "";
        return -1;
    }

    public static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}

/// <summary>
/// The launcher's own words in other languages. The English text is the key, so a missing line just stays English.
/// Text with changing parts uses {0}, {1}...: "Downloading {0}" in the file matches "Downloading patch-A.MPQ".
/// Only the launcher's own text is translated; what the server writes (news, option names) is shown as it is.
/// </summary>
public static class Translation
{
    static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    static List<(Regex Pattern, string Value)> _templates = [];

    /// <summary>The language in use (enUS when nothing is translated).</summary>
    public static string Code { get; private set; } = English;

    /// <summary>A language the launcher has: its code, the game's locale ("deDE"), its own name ("Deutsch") and its lines.</summary>
    public sealed record Language(string Code, string Name, Dictionary<string, string> Lines);

    public const string English = "enUS";

    public static Language FromYaml(string code, string yaml)
    {
        Dictionary<string, string> lines = MiniYaml.Parse(yaml);
        string name = lines.Remove("_name", out string? n) ? n : code;
        return new Language(code, name, lines);
    }

    /// <summary>"de-DE", "dede", "deDE" all become "dede".</summary>
    static string Key(string code) => code.Replace("-", "").Replace("_", "").ToLowerInvariant();

    /// <summary>
    /// The language to use: the player's pick, else launcher.env's LANGUAGE ("auto" or empty: the computer's own
    /// language), else English. Codes are the game's locales (deDE, zhTW...); "de" or "de-AT" also find deDE.
    /// Only languages in <paramref name="have"/> count; the answer is one of them, or <see cref="English"/>.
    /// </summary>
    public static string Pick(string? player, string? server, CultureInfo system, IEnumerable<string> have)
    {
        List<string> codes = [English, .. have.Where(c => Key(c) != Key(English))];
        string? Find(string wanted)
        {
            string key = Key(wanted);
            return codes.FirstOrDefault(c => Key(c) == key)
                   ?? (key.Length >= 2 ? codes.FirstOrDefault(c => Key(c).StartsWith(key[..2], StringComparison.Ordinal)) : null);
        }
        foreach (string? choice in new[] { player, server })
        {
            if (string.IsNullOrWhiteSpace(choice)) continue;
            if (choice.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) break;
            if (Find(choice.Trim()) is { } found) return found;
        }
        // the computer's language; Chinese picks traditional for Taiwan, Hong Kong and Macau
        string name = system.Name.ToLowerInvariant();
        if (name.StartsWith("zh"))
            return Find(name.Contains("tw") || name.Contains("hk") || name.Contains("mo") || name.Contains("hant") ? "zhTW" : "zhCN") ?? English;
        return codes.FirstOrDefault(c => Key(c) == Key(name)) ?? Find(system.TwoLetterISOLanguageName) ?? English;
    }

    public static void Use(Language? language)
    {
        Code = language?.Code ?? English;
        _exact = new(StringComparer.Ordinal);
        _templates = [];
        if (language is null || Key(language.Code) == Key(English)) return;
        foreach ((string key, string value) in language.Lines)
        {
            if (value.Length == 0) continue;
            if (Regex.IsMatch(key, @"\{\d\}"))
            {
                // "Downloading {0}  {1} / {2} MB" → ^Downloading (.+?)  (.+?) / (.+?) MB$
                string pattern = "^" + Regex.Replace(Regex.Escape(key), @"\\\{(\d)}", m => $"(?<a{m.Groups[1].Value}>.+?)") + "$";
                _templates.Add((new Regex(pattern, RegexOptions.Singleline), value));
            }
            else _exact[key] = value;
        }
        // the most specific first: "Downloading {0}  {1} / {2} MB" before "Downloading {0}"
        _templates = _templates.OrderByDescending(t => Regex.Replace(t.Pattern.ToString(), @"\(\?<a\d>\.\+\?\)", "").Length).ToList();
    }

    /// <summary>The text in the current language, or null when there is no line for it.</summary>
    public static string? Find(string text)
    {
        if (_exact.Count == 0 && _templates.Count == 0) return null;
        if (_exact.TryGetValue(text, out string? exact)) return exact;
        foreach ((Regex pattern, string value) in _templates)
        {
            Match m = pattern.Match(text);
            if (!m.Success) continue;
            return Regex.Replace(value, @"\{(\d)\}", v => m.Groups["a" + v.Groups[1].Value] is { Success: true } g ? g.Value : v.Value);
        }
        return null;
    }

    /// <summary>The text in the current language (English when there is no line for it).</summary>
    public static string T(string english) => Find(english) ?? english;
}
