using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>
/// One option of the game's own Interface, Video or Sound windows. The list (game-options.json) was taken from the
/// 3.3.5 client: names, labels and tooltips from its FrameXML, defaults from the CVar registrations in Wow.exe.
/// </summary>
public sealed record GameOption
{
    public string Group { get; init; } = "";
    public string Section { get; init; } = "";
    public string CVar { get; init; } = "";
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    /// <summary>The game's own value when the setting is not in any file; null when unknown.</summary>
    public string? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public bool Restart { get; init; }
    public bool Logout { get; init; }
    /// <summary>The box is ticked when the value is 0 (Sticky Targeting is deselectOnClick = 0).</summary>
    public bool Invert { get; init; }
    public List<string>? Choices { get; init; }

    [JsonIgnore] public bool IsToggle => Min is null && Choices is null;
}

public static class GameOptions
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<GameOption> All { get; } = Load();

    static List<GameOption> Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Launcher.Core.game-options.json");
        if (stream is null) return [];
        return JsonSerializer.Deserialize<List<GameOption>>(stream, Json) ?? [];
    }
}

/// <summary>
/// The game's settings as the client sees them: WTF\Config.wtf, and the newest WTF\Account\*\config-cache.wtf, where
/// the client keeps account settings (auto loot, nameplates...) and which wins over Config.wtf once logged in.
/// Writing sets Config.wtf, and every account file that already holds that setting.
/// </summary>
public sealed class GameOptionStore
{
    readonly ConfigWtf _config;
    readonly List<ConfigWtf> _accounts;

    GameOptionStore(ConfigWtf config, List<ConfigWtf> accounts) { _config = config; _accounts = accounts; }

    public static GameOptionStore Load(string wowFolder)
    {
        List<ConfigWtf> accounts = [];
        string accountRoot = Path.Combine(wowFolder, "WTF", "Account");
        if (Directory.Exists(accountRoot))
            accounts = Directory.GetDirectories(accountRoot)
                .Select(d => Path.Combine(d, "config-cache.wtf"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)   // the account played last first
                .Select(ConfigWtf.LoadFile)
                .ToList();
        return new GameOptionStore(ConfigWtf.Load(wowFolder), accounts);
    }

    /// <summary>The value the game uses now: the newest account file, then Config.wtf; null when neither has it.</summary>
    public string? Get(string cvar)
    {
        foreach (ConfigWtf account in _accounts)
            if (account.Get(cvar) is { } value) return value;
        return _config.Get(cvar);
    }

    public void Set(string cvar, string value)
    {
        _config.Set(cvar, value);
        foreach (ConfigWtf account in _accounts)
            if (account.Get(cvar) is not null) account.Set(cvar, value);
    }

    public void Save()
    {
        _config.Save();
        foreach (ConfigWtf account in _accounts) account.Save();
    }
}
