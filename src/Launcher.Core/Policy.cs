using System.Globalization;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>One rule of the server: a value, and whether the player may change it.</summary>
public sealed record PolicyRule(JsonElement Value, bool Enforce)
{
    public bool? Bool => Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => Value.GetDouble() != 0,
        JsonValueKind.String => Value.GetString() is "1" or "true" or "on" ? true : Value.GetString() is "0" or "false" or "off" ? false : null,
        _ => null,
    };

    public int? Int => Value.ValueKind == JsonValueKind.Number && Value.TryGetInt32(out int n) ? n
        : Value.ValueKind == JsonValueKind.String && int.TryParse(Value.GetString(), CultureInfo.InvariantCulture, out int m) ? m : null;

    public IReadOnlyList<string> Strings => Value.ValueKind == JsonValueKind.Array
        ? Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
        : Value.ValueKind == JsonValueKind.String ? [Value.GetString()!] : [];

    /// <summary>The value as Config.wtf writes it: true/false become 1/0, numbers without a culture's commas.</summary>
    public string? CVarText => Value.ValueKind switch
    {
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Number => Tweaks.Format(Value.GetDouble()),
        JsonValueKind.String => Value.GetString(),
        _ => null,
    };
}

/// <summary>
/// launcher-policy.json: the server's say over any launcher setting. Each entry is
/// <c>{"value": ..., "enforce": true|false}</c>. An enforced setting is set by the server and the player can't change
/// it; one that isn't enforced is the server's recommendation (a default where the launcher has no box for it).
/// <list type="bullet">
/// <item>On/off: installAddons, clearCache, reopenAfterGame, rememberWindow, backupBeforeUpdate, largeAddressAware</item>
/// <item>backupsToKeep: a number</item>
/// <item>defaultLocale: the server's default language (its base client's), always installed; enUS when not set</item>
/// <item>requiredLocales: extra languages everyone gets (players may add more)</item>
/// <item>tweaks: any Config.wtf value by its CVar name, the Tweaks page's or any other, e.g. "farclip"</item>
/// </list>
/// Names the launcher doesn't know are kept in <see cref="Unknown"/> (and logged), never an error.
/// </summary>
public sealed class LauncherPolicy
{
    public static readonly string[] OnOff = ["installAddons", "clearCache", "reopenAfterGame", "rememberWindow", "backupBeforeUpdate", "largeAddressAware"];
    public static readonly string[] Known = [.. OnOff, "backupsToKeep", "requiredLocales", "defaultLocale", "tweaks", "news", "changelog"];

    /// <summary>How the News or Changelog panel shows (the "news" / "changelog" entries); shown, 20 then 20 more, by default.</summary>
    public FeedRule News { get; private set; } = FeedRule.Default;
    public FeedRule Changelog { get; private set; } = FeedRule.Default;

    public Dictionary<string, PolicyRule> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PolicyRule> Tweaks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Unknown { get; } = [];

    public static LauncherPolicy From(IReadOnlyDictionary<string, JsonElement>? policy)
    {
        LauncherPolicy rules = new();
        foreach ((string key, JsonElement value) in policy ?? new Dictionary<string, JsonElement>())
        {
            if (key.Equals("news", StringComparison.OrdinalIgnoreCase) || key.Equals("changelog", StringComparison.OrdinalIgnoreCase))
            {
                FeedRule feed = FeedRule.From(value);
                if (key.Equals("news", StringComparison.OrdinalIgnoreCase)) rules.News = feed; else rules.Changelog = feed;
            }
            else if (key.Equals("tweaks", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.Object) { rules.Unknown.Add(key); continue; }
                foreach (JsonProperty cvar in value.EnumerateObject())
                    if (Rule(cvar.Value) is { } rule && rule.CVarText is not null) rules.Tweaks[cvar.Name] = rule;
                    else rules.Unknown.Add("tweaks." + cvar.Name);
            }
            else if (Known.Contains(key, StringComparer.OrdinalIgnoreCase) && Rule(value) is { } rule)
                rules.Settings[key] = rule;
            else
                rules.Unknown.Add(key);
        }
        return rules;
    }

    /// <summary>{"value": x, "enforce": b}, or a bare value (a recommendation).</summary>
    static PolicyRule? Rule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : new PolicyRule(element.Clone(), false);
        if (!element.TryGetProperty("value", out JsonElement value)) return null;
        bool enforce = element.TryGetProperty("enforce", out JsonElement e) && e.ValueKind == JsonValueKind.True;
        return new PolicyRule(value.Clone(), enforce);
    }

    public PolicyRule? Setting(string key) => Settings.GetValueOrDefault(key);

    public bool? EnforcedBool(string key) => Setting(key) is { Enforce: true } rule ? rule.Bool : null;

    /// <summary>Extra languages everyone gets (on top of <see cref="DefaultLocale"/> and the game's own language).</summary>
    public IReadOnlyList<string> RequiredLocales => Setting("requiredLocales")?.Strings ?? [];

    /// <summary>The server's default language, the language of its base client: enUS unless the server says. Always
    /// installed (ticked and greyed out).</summary>
    public string DefaultLocale => Setting("defaultLocale")?.Strings.FirstOrDefault() is { Length: 4 } locale ? locale : "enUS";

    public IEnumerable<KeyValuePair<string, PolicyRule>> EnforcedTweaks => Tweaks.Where(t => t.Value.Enforce);

    /// <summary>Writes the enforced tweaks into Config.wtf; returns how many changed.</summary>
    public int ApplyEnforcedTweaks(ConfigWtf config)
    {
        int changed = 0;
        foreach ((string cvar, PolicyRule rule) in EnforcedTweaks)
            if (config.Get(cvar) != rule.CVarText) { config.Set(cvar, rule.CVarText); changed++; }
        return changed;
    }
}

/// <summary>
/// The News or Changelog panel: whether it shows, how many entries at first, and how many "Load more" adds
/// (-1: the button loads all of them).
/// </summary>
public sealed record FeedRule(bool Show, int Entries, int More)
{
    public static readonly FeedRule Default = new(true, 20, 20);

    public static FeedRule From(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.False) return Default with { Show = false };
        if (element.ValueKind != JsonValueKind.Object) return Default;
        bool show = !element.TryGetProperty("show", out JsonElement s) || s.ValueKind != JsonValueKind.False;
        int entries = element.TryGetProperty("entries", out JsonElement e) && e.TryGetInt32(out int n) && n > 0 ? n : Default.Entries;
        int more = element.TryGetProperty("more", out JsonElement m) && m.TryGetInt32(out int k) && (k > 0 || k == -1) ? k : Default.More;
        return new FeedRule(show, entries, more);
    }

    /// <summary>The "Load more" button's text for how many entries are left; null when none are.</summary>
    public string? MoreLabel(int left) => left <= 0 ? null : More == -1 || More >= left ? $"Load all ({left})" : $"Load {More} more ({left} left)";

    public int NextCount(int shown, int total) => More == -1 ? total : Math.Min(total, shown + More);
}
