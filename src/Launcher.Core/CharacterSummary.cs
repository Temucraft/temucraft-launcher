using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>What the character select card shows beyond the game's own list: the spec (talent tree with the most
/// points), professions, gold (copper) and the senders of unread mail. Any of it may be missing.</summary>
public sealed record CharacterSummaryEntry(string? Spec, IReadOnlyList<string> Professions, long? Money, IReadOnlyList<string> Mail);

/// <summary>One realm of the server's answer: its id in the realmlist, its name there and the characters by name.</summary>
public sealed record CharacterSummaryRealm(int Id, string Name, IReadOnlyDictionary<string, CharacterSummaryEntry> Characters);

/// <summary>
/// Interface\TemucraftCharSummary.lua: the character select screen's extra card lines (TemucraftCharSelect.lua in
/// patch-^, which ships an empty copy). Before PLAY, and when the player switches between Live and PTR, the launcher
/// asks the server for SERVER_URL/char-summary (server/char-summary.py, with the player's login: only their own
/// characters) and writes the picked version's realm into that version's game folder, as
/// <c>TEMUCRAFT_CHAR_SUMMARY = { ["realm"] = { ["name"] = { spec = ..., professions = {...}, money = ..., mail = {...} } } }</c>.
/// The realm key is the name the game shows (GetServerName): the realm name the launcher writes into Config.wtf, and
/// the server's realmlist name too when that is different. When anything goes wrong the old file stays as it was.
/// </summary>
public static class CharacterSummary
{
    public const string FilePath = "Interface/TemucraftCharSummary.lua";
    public const string ServerPath = "char-summary";

    public enum Outcome { Written, Unchanged, NotOnServer, NoRealm, Failed }

    public static IReadOnlyList<CharacterSummaryRealm> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        List<CharacterSummaryRealm> realms = [];
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("realms", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("The character summary has no realms list.");
        foreach (JsonElement r in list.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            int id = r.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt32(out int n) ? n : 0;
            string name = Text(r, "name") ?? "";
            Dictionary<string, CharacterSummaryEntry> characters = new(StringComparer.Ordinal);
            if (r.TryGetProperty("characters", out JsonElement chars) && chars.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty c in chars.EnumerateObject())
                {
                    if (c.Value.ValueKind != JsonValueKind.Object || c.Name.Length == 0) continue;
                    long? money = c.Value.TryGetProperty("money", out JsonElement m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out long copper) && copper >= 0 ? copper : null;
                    characters[c.Name] = new CharacterSummaryEntry(Text(c.Value, "spec"), Texts(c.Value, "professions"), money, Texts(c.Value, "mail"));
                }
            realms.Add(new CharacterSummaryRealm(id, name, characters));
        }
        return realms;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static List<string> Texts(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    /// <summary>The picked version's realm: by realm id, else by name; null when the server didn't send it.</summary>
    public static CharacterSummaryRealm? Pick(IReadOnlyList<CharacterSummaryRealm> realms, int realmId, string? realmName) =>
        realms.FirstOrDefault(r => r.Id != 0 && r.Id == realmId)
        ?? (realmName is { Length: > 0 } ? realms.FirstOrDefault(r => string.Equals(r.Name.Trim(), realmName.Trim(), StringComparison.OrdinalIgnoreCase)) : null);

    /// <summary>A Lua string literal: quotes, backslashes and control characters escaped; other text (UTF-8 names) as it is.</summary>
    public static string LuaString(string value)
    {
        StringBuilder text = new(value.Length + 2);
        text.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\': text.Append("\\\\"); break;
                case '"': text.Append("\\\""); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    // \ddd always with three digits, so a digit after it can't be read as part of it
                    if (ch < ' ' || ch == '\x7f') text.Append('\\').Append(((int)ch).ToString("D3", CultureInfo.InvariantCulture));
                    else text.Append(ch);
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    /// <summary>The file: the realm's characters under each of the realm keys (the names the game may show for it).</summary>
    public static string Text(CharacterSummaryRealm realm, IEnumerable<string?> realmKeys)
    {
        StringBuilder text = new();
        text.Append("-- Written by the Temucraft launcher before the game starts: spec, professions, gold and unread mail of your\r\n");
        text.Append("-- characters for the character select screen, from the server (written again at every PLAY).\r\n");
        text.Append("TEMUCRAFT_CHAR_SUMMARY = {\r\n");
        foreach (string key in realmKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!.Trim()).Distinct(StringComparer.Ordinal))
        {
            text.Append('\t').Append('[').Append(LuaString(key)).Append("] = {\r\n");
            foreach ((string name, CharacterSummaryEntry c) in realm.Characters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                List<string> fields = [];
                if (c.Spec is { Length: > 0 }) fields.Add("spec = " + LuaString(c.Spec));
                if (c.Professions.Count > 0) fields.Add("professions = { " + string.Join(", ", c.Professions.Select(LuaString)) + " }");
                if (c.Money is { } money) fields.Add("money = " + money.ToString(CultureInfo.InvariantCulture));
                if (c.Mail.Count > 0) fields.Add("mail = { " + string.Join(", ", c.Mail.Select(LuaString)) + " }");
                text.Append("\t\t[").Append(LuaString(name)).Append("] = { ").Append(string.Join(", ", fields)).Append(fields.Count > 0 ? " },\r\n" : "},\r\n");
            }
            text.Append("\t},\r\n");
        }
        text.Append("};\r\n");
        return text.ToString();
    }

    /// <summary>Writes the file when it changed (UTF-8 without a BOM, through a temporary file); true when it did.</summary>
    public static bool Write(string wowFolder, string text)
    {
        string path = Path.Combine(wowFolder, "Interface", "TemucraftCharSummary.lua");
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        if (File.Exists(path) && File.ReadAllText(path, utf8) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, utf8);
            // a move, not a write into the file: a PTR made from Live with hard links never writes into Live's copy
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return true;
    }

    /// <summary>After "Log out": an empty table, so the character screen shows nothing extra (only when the file is there).</summary>
    public static bool Clear(string wowFolder)
    {
        string path = Path.Combine(wowFolder, "Interface", "TemucraftCharSummary.lua");
        return File.Exists(path) && Write(wowFolder,
            "-- Written by the Temucraft launcher: empty after Log out (Settings page, Character summary).\r\nTEMUCRAFT_CHAR_SUMMARY = {\r\n};\r\n");
    }

    /// <summary>
    /// Asks the server (<paramref name="fetch"/>: the answer, or null when the server has no char-summary) and writes the
    /// realm's file into <paramref name="wowFolder"/>. Never throws: on any failure (no answer, a refused login, bad
    /// JSON, the realm missing from the answer, a file that can't be written) the old file is kept as it was.
    /// </summary>
    public static async Task<(Outcome Outcome, int Characters, string? Error)> RefreshAsync(Func<CancellationToken, Task<string?>> fetch,
        string wowFolder, int realmId, string? realmName, CancellationToken token = default)
    {
        try
        {
            string? json = await fetch(token);
            if (json is null) return (Outcome.NotOnServer, 0, null);
            CharacterSummaryRealm? realm = Pick(Parse(json), realmId, realmName);
            if (realm is null) return (Outcome.NoRealm, 0, null);
            string text = Text(realm, [realmName, realm.Name]);
            return (Write(wowFolder, text) ? Outcome.Written : Outcome.Unchanged, realm.Characters.Count, null);
        }
        catch (Exception error)
        {
            return (Outcome.Failed, 0, error.Message);
        }
    }
}
