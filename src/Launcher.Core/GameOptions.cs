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
    /// <summary>What the game's dropdown shows for each of Choices (same order); the values themselves when missing.</summary>
    public List<string>? ChoiceLabels { get; init; }
    /// <summary>A key setting kept in bindings-cache.wtf (<c>modifiedclick SELFCAST ALT</c>), not a CVar.</summary>
    public string? ModifiedClick { get; init; }

    [JsonIgnore] public bool IsToggle => Min is null && Choices is null;
    /// <summary>The setting's name in the files: the CVar, or "modifiedclick ACTION".</summary>
    [JsonIgnore] public string Key => ModifiedClick is null ? CVar : "modifiedclick " + ModifiedClick;
    public string ChoiceLabel(int i) => ChoiceLabels is { } l && i < l.Count ? l[i] : Choices![i];
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
/// Loaded for a character, the settings the game keeps per character (nameplates, block trades...) are read from and
/// written to that character's own config-cache.wtf; the rest stay as for the account.
/// </summary>
public sealed class GameOptionStore
{
    readonly ConfigWtf _config;
    readonly List<ConfigWtf> _accounts;
    readonly List<BindingsFile> _bindings;
    readonly HashSet<BindingsFile> _bindingsChanged = [];
    readonly ConfigWtf? _character;
    bool _characterChanged;

    GameOptionStore(ConfigWtf config, List<ConfigWtf> accounts, List<BindingsFile> bindings, WowCharacter? character = null)
    {
        _config = config; _accounts = accounts; _bindings = bindings;
        Character = character;
        if (character is not null) _character = ConfigWtf.LoadFile(Path.Combine(character.Folder, "config-cache.wtf"));
    }

    const string ModClick = "modifiedclick ";

    /// <summary>The settings the 3.3.5 client keeps in a character's config-cache.wtf, not the account's.</summary>
    public static readonly IReadOnlySet<string> PerCharacter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nameplateShowEnemies", "nameplateShowFriends", "nameplateAllowOverlap", "ShowClassColorInNameplate",
        "showDispelDebuffs", "showRaidRange", "blockTrades", "assistAttack", "mapQuestDifficulty", "previewTalents",
        "equipmentManager", "showBattlefieldMinimap", "showKeyring", "showTokenFrame", "questPOI", "minimapZoom",
        "minimapInsideZoom", "cameraSavedDistance", "cameraSavedPitch",
    };

    /// <summary>The character the settings were loaded for; null for the account.</summary>
    public WowCharacter? Character { get; }

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
        return new GameOptionStore(ConfigWtf.Load(wowFolder), accounts, BindingsFile.Find(wowFolder));
    }

    /// <summary>The settings as one character sees them: its own file, then its account's, then Config.wtf.</summary>
    public static GameOptionStore Load(string wowFolder, WowCharacter? character)
    {
        if (character is null) return Load(wowFolder);
        string accountFile = Path.Combine(character.AccountFolder, "config-cache.wtf");
        List<ConfigWtf> accounts = File.Exists(accountFile) ? [ConfigWtf.LoadFile(accountFile)] : [];
        return new GameOptionStore(ConfigWtf.Load(wowFolder), accounts, BindingsFile.Find(wowFolder), character);
    }

    /// <summary>True when the game keeps this setting per character (known to, or already in the character's file).</summary>
    public bool IsPerCharacter(string cvar) =>
        !cvar.StartsWith(ModClick, StringComparison.OrdinalIgnoreCase)
        && (PerCharacter.Contains(cvar) || _character?.Get(cvar) is not null);

    /// <summary>The value the game uses now: the character's file (for its own settings), the newest account file,
    /// then Config.wtf; null when none has it.</summary>
    public string? Get(string cvar)
    {
        if (cvar.StartsWith(ModClick, StringComparison.OrdinalIgnoreCase))
            return _bindings.Count > 0 ? _bindings[0].GetModifiedClick(cvar[ModClick.Length..]) : null;
        if (_character is not null && IsPerCharacter(cvar) && _character.Get(cvar) is { } own) return own;
        foreach (ConfigWtf account in _accounts)
            if (account.Get(cvar) is { } value) return value;
        return _config.Get(cvar);
    }

    /// <summary>Sets a CVar, or a modified click ("modifiedclick SELFCAST") in the account's bindings file and every
    /// character file that has it. False when there is nowhere to keep a modified click (no account folder yet).
    /// Loaded for a character, a setting the game keeps per character goes to that character's file only.</summary>
    public bool Set(string cvar, string value)
    {
        if (cvar.StartsWith(ModClick, StringComparison.OrdinalIgnoreCase))
        {
            string action = cvar[ModClick.Length..];
            if (_bindings.Count == 0) return false;
            foreach (BindingsFile file in _bindings)
                if (file == _bindings[0] || file.GetModifiedClick(action) is not null)
                {
                    file.SetModifiedClick(action, value);
                    _bindingsChanged.Add(file);
                }
            return true;
        }
        if (_character is not null && IsPerCharacter(cvar))
        {
            _character.Set(cvar, value);
            _characterChanged = true;
            return true;
        }
        _config.Set(cvar, value);
        foreach (ConfigWtf account in _accounts)
            if (account.Get(cvar) is not null) account.Set(cvar, value);
        return true;
    }

    public void Save()
    {
        _config.Save();
        foreach (ConfigWtf account in _accounts) account.Save();
        foreach (BindingsFile file in _bindingsChanged) file.Save();
        if (_characterChanged) _character!.Save();
    }
}
