using System.Text;

namespace Launcher.Core;

/// <summary>A character screen scene: its folder (Interface\Glues\Models\UI_&lt;Folder&gt;), a name and the code of its
/// picture in previews/login.</summary>
public sealed record CharacterScene(string Folder, string Label, string Preview);

/// <summary>A race (or class) of the character screens: its file string in upper case, as the game gives it.</summary>
public sealed record CharacterRace(string Key, string Label, string Faction);

/// <summary>
/// The character select / create scenes. Each race shows its own scene: the original 3.3.5 one with Character models
/// Classic, the newer one with HD. The player can pick another scene per race; the launcher writes it all to
/// Interface\TemucraftScenes.lua next to LoginUI.lua, which the game's GlueParent.lua reads.
/// </summary>
public static class CharacterScenes
{
    public const string FilePath = "Interface/TemucraftScenes.lua";

    public static readonly CharacterScene[] Scenes =
    [
        new("Alliance", "Alliance", "al"), new("Horde", "Horde", "ho"),
        new("Huma2", "Human (Classic)", "hu"), new("Human", "Human (HD)", "hm"),
        new("Dwar2", "Dwarf and Gnome (Classic)", "dw"), new("Dwarf", "Dwarf (HD)", "dg"),
        new("NightEl2", "Night Elf (Classic)", "nl"), new("NightElf", "Night Elf (HD)", "ne"),
        new("Draenei", "Draenei", "dr"), new("Gnome", "Gnome (HD)", "gn"), new("Worgen", "Worgen", "wr"),
        new("Or2", "Orc and Troll (Classic)", "or"), new("Orc", "Orc (HD)", "ot"),
        new("Scourg2", "Undead (Classic)", "sc"), new("Scourge", "Undead (HD)", "ud"),
        new("Taure2", "Tauren (Classic)", "ta"), new("Tauren", "Tauren (HD)", "tr"),
        new("Troll", "Troll (HD)", "tl"), new("BloodElf", "Blood Elf", "be"), new("Goblin", "Goblin", "gb"),
        new("Pandaren", "Pandaren", "pd"), new("PandarenCharacterSelect", "Pandaren (temple)", "pn"),
        new("VoidElf", "Void Elf", "ve"), new("LightforgedDraenei", "Lightforged Draenei", "ld"),
        new("KulTiran", "Kul Tiran", "kt"), new("Mechagnome", "Mechagnome", "mg"),
        new("NightborneElf", "Nightborne", "nf"), new("HighmountainTauren", "Highmountain Tauren", "hr"),
        new("OrcClan", "Mag'har Orc", "oc"), new("Vulpera", "Vulpera", "vp"), new("Earthen", "Earthen", "ea"),
        new("DeathKnight", "Death Knight (Classic)", "dk"), new("DeathKnigh2", "Death Knight (HD)", "dt"),
        new("DemonHunter", "Demon Hunter", "dh"), new("Dracthyr", "Dracthyr", "dy"), new("Pirate", "Pirate", "tp"),
        new("CharacterSelect", "Character select", "cs"),
    ];

    /// <summary>The playable races (ChrRaces 1-20 with their file strings), then death knights.</summary>
    public static readonly CharacterRace[] Races =
    [
        new("HUMAN", "Human", "Alliance"), new("DWARF", "Dwarf", "Alliance"), new("NIGHTELF", "Night Elf", "Alliance"),
        new("GNOME", "Gnome", "Alliance"), new("DRAENEI", "Draenei", "Alliance"), new("WORGEN", "Worgen", "Alliance"),
        new("ALLIANCEPANDAREN", "Pandaren", "Alliance"), new("VOIDELF", "Void Elf", "Alliance"),
        new("LIGHTFORGEDDRAENEI", "Lightforged Draenei", "Alliance"), new("KULTIRAN", "Kul Tiran", "Alliance"),
        new("ORC", "Orc", "Horde"), new("SCOURGE", "Undead", "Horde"), new("TAUREN", "Tauren", "Horde"),
        new("TROLL", "Troll", "Horde"), new("GOBLIN", "Goblin", "Horde"), new("BLOODELF", "Blood Elf", "Horde"),
        new("HORDEPANDAREN", "Pandaren", "Horde"), new("NIGHTBORNE", "Nightborne", "Horde"),
        new("HIGHMOUNTAINTAUREN", "Highmountain Tauren", "Horde"), new("ZANDALARITROLL", "Zandalari Troll", "Horde"),
        new("DEATHKNIGHT", "Death Knight", "Class"),
    ];

    // the same tables as GlueParent.lua (for showing which scene a race gets when the player picked nothing)
    static readonly Dictionary<string, string> Hd = new()
    {
        ["HUMAN"] = "Human", ["DWARF"] = "Dwarf", ["NIGHTELF"] = "NightElf", ["GNOME"] = "Gnome", ["DRAENEI"] = "Draenei",
        ["WORGEN"] = "Worgen", ["BLOODELF"] = "BloodElf", ["ORC"] = "Orc", ["TROLL"] = "Troll", ["SCOURGE"] = "Scourge",
        ["TAUREN"] = "Tauren", ["GOBLIN"] = "Goblin", ["LIGHTFORGEDDRAENEI"] = "LightforgedDraenei",
        ["NIGHTBORNE"] = "NightborneElf", ["MAGHAR"] = "OrcClan", ["HIGHMOUNTAINTAUREN"] = "HighmountainTauren",
        ["KULTIRAN"] = "KulTiran", ["VULPERA"] = "Vulpera", ["VOIDELF"] = "VoidElf", ["MECHAGNOME"] = "Mechagnome",
        ["DARKIRONDWARF"] = "Dwarf", ["ZANDALARITROLL"] = "Troll", ["ALLIANCEPANDAREN"] = "Pandaren",
        ["HORDEPANDAREN"] = "Pandaren", ["ALLIANCEEARTHEN"] = "Earthen", ["HORDEEARTHEN"] = "Earthen",
        ["DEATHKNIGHT"] = "DeathKnigh2", ["HIGHELF"] = "Human",
    };
    static readonly Dictionary<string, string> Original = new()
    {
        ["HUMAN"] = "Huma2", ["DWARF"] = "Dwar2", ["GNOME"] = "Dwar2", ["NIGHTELF"] = "NightEl2", ["ORC"] = "Or2",
        ["TROLL"] = "Or2", ["SCOURGE"] = "Scourg2", ["TAUREN"] = "Taure2", ["DARKIRONDWARF"] = "Dwar2",
        ["DRAENEI"] = "Draenei", ["BLOODELF"] = "BloodElf", ["DEATHKNIGHT"] = "DeathKnight", ["HIGHELF"] = "Human",
    };

    /// <summary>The scene a race gets without a pick of the player's.</summary>
    public static string DefaultScene(string race, bool hd) =>
        (hd ? Hd.GetValueOrDefault(race) : Original.GetValueOrDefault(race) ?? Hd.GetValueOrDefault(race)) ?? "";

    public static string Label(string folder) =>
        Scenes.FirstOrDefault(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Label ?? folder;

    static bool Safe(string text) => text.Length is > 0 and < 64 && text.All(char.IsAsciiLetterOrDigit);

    /// <summary>The file's text: the mode, then the player's picks.</summary>
    public static string Text(bool hd, IReadOnlyDictionary<string, string> picks)
    {
        StringBuilder text = new();
        text.Append("-- Written by the Temucraft launcher (Login screen > Character screens). See GlueParent.lua.\r\n");
        text.Append($"TEMUCRAFT_CHARACTER_SCENES = \"{(hd ? "new" : "old")}\";\r\n");
        text.Append("TEMUCRAFT_SCENE_PICKS = {\r\n");
        foreach ((string race, string scene) in picks.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (Safe(race) && Safe(scene)) text.Append($"\t[\"{race.ToUpperInvariant()}\"] = \"{scene}\",\r\n");
        text.Append("};\r\n");
        return text.ToString();
    }

    /// <summary>Writes Interface\TemucraftScenes.lua when it changed; true when it did.</summary>
    public static bool Write(string wowFolder, bool hd, IReadOnlyDictionary<string, string> picks)
    {
        string path = Path.Combine(wowFolder, "Interface", "TemucraftScenes.lua");
        string text = Text(hd, picks);
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }
}
