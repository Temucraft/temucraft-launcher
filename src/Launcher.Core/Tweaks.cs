using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>WTF\Config.wtf: the client's settings, as lines of <c>SET name "value"</c>.</summary>
public sealed class ConfigWtf
{
    readonly List<string> _lines;
    static readonly Regex SetLine = new(@"^\s*SET\s+(\S+)\s+""(.*)""\s*$", RegexOptions.IgnoreCase);

    public string File { get; }

    ConfigWtf(string file, List<string> lines) { File = file; _lines = lines; }

    public static ConfigWtf Load(string wowFolder)
    {
        string file = Path.Combine(wowFolder, "WTF", "Config.wtf");
        return new ConfigWtf(file, System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file).ToList() : []);
    }

    public string? Get(string name)
    {
        foreach (string line in _lines)
        {
            Match m = SetLine.Match(line);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return m.Groups[2].Value;
        }
        return null;
    }

    /// <summary>Sets a value; null removes the line so the client uses its default.</summary>
    public void Set(string name, string? value)
    {
        int index = _lines.FindIndex(l => SetLine.Match(l) is { Success: true } m && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (value is null) { if (index >= 0) _lines.RemoveAt(index); return; }
        string line = $"SET {name} \"{value.Replace("\"", "")}\"";
        if (index >= 0) _lines[index] = line; else _lines.Add(line);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllLines(File, _lines, Encoding.ASCII);
    }
}

public enum TweakKind { Toggle, Number }

/// <summary>One client setting the Tweaks page offers. Highlighted ones are recommended.</summary>
public sealed record Tweak(string Section, string Name, string CVar, TweakKind Kind, string Default, string Recommended,
                           string Description, double Min = 0, double Max = 1, bool Highlighted = false);

public static class Tweaks
{
    /// <summary>Settings that are plain Config.wtf values in 3.3.5a (no change to Wow.exe).</summary>
    public static readonly IReadOnlyList<Tweak> All =
    [
        new("General", "Always auto-loot", "autoLootDefault", TweakKind.Toggle, "0", "1", "Loot everything without holding the auto-loot key (the key then picks single items)."),
        new("General", "Interact on left click", "interactOnLeftClick", TweakKind.Toggle, "0", "1", "Talk to NPCs and loot with the left mouse button."),
        new("General", "Max foreground FPS", "maxFPS", TweakKind.Number, "200", "144", "Frame rate cap while the game window is active.", 30, 300, true),
        new("General", "Max background FPS", "maxFPSBk", TweakKind.Number, "30", "30", "Frame rate cap while the game is in the background.", 8, 200),
        new("Display", "Windowed mode", "gxWindow", TweakKind.Toggle, "0", "1", "Run in a window (with Maximized, a borderless full screen window)."),
        new("Display", "Maximized window", "gxMaximize", TweakKind.Toggle, "0", "1", "Maximize the window when windowed mode is on."),
        new("Display", "Vertical sync", "gxVSync", TweakKind.Toggle, "1", "0", "Wait for the monitor refresh (no tearing, some input lag)."),
        new("Display", "Render distance", "farclip", TweakKind.Number, "777", "1277", "How far terrain is drawn. [Default: 777] [Max: 1277]", 177, 1277, true),
        new("Display", "Ground clutter distance", "groundEffectDist", TweakKind.Number, "70", "140", "How far grass and small plants are drawn. [Default: 70] [Max: 140]", 0, 140),
        new("Display", "Ground clutter density", "groundEffectDensity", TweakKind.Number, "16", "64", "How thick the ground clutter is. [Default: 16] [Max: 256]", 16, 256),
        new("Display", "Spell detail", "spellEffectLevel", TweakKind.Number, "2", "2", "Amount of spell effects. [0-2]", 0, 2),
        new("Camera", "Camera distance", "cameraDistanceMax", TweakKind.Number, "15", "50", "Maximum zoom out distance. [Default: 15] [Max: 50]", 15, 50, true),
        new("Camera", "Camera distance factor", "cameraDistanceMaxFactor", TweakKind.Number, "1", "2", "Multiplies the zoom out distance. [Default: 1] [Max: 4]", 1, 4, true),
        new("Camera", "Smooth camera", "cameraSmoothStyle", TweakKind.Number, "0", "0", "Camera follow style. [0 never, 1 smart, 2 always, 4 when moving]", 0, 4),
        new("Sound", "Background sounds", "Sound_EnableSoundWhenGameIsInBG", TweakKind.Toggle, "0", "1", "Keep playing game sounds while the game is minimized.", Highlighted: true),
        new("Sound", "Error speech", "Sound_EnableErrorSpeech", TweakKind.Toggle, "1", "0", "The character's \"I can't do that yet\" voice lines."),
    ];

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Large Address Aware: one bit in Wow.exe's header that lets the 32-bit client use 4 GB of memory instead of 2 GB
/// (fewer out-of-memory crashes with big patches). A copy of the original exe is kept as Wow.exe.bak.
/// </summary>
public static class LargeAddressAware
{
    const ushort Flag = 0x0020;

    static long CharacteristicsOffset(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Wow.exe isn't a Windows program.");
        stream.Position = 0x3C;
        int pe = reader.ReadInt32();
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Wow.exe has no PE header.");
        return pe + 4 + 18;   // IMAGE_FILE_HEADER.Characteristics
    }

    public static bool IsEnabled(string exe)
    {
        using FileStream stream = System.IO.File.OpenRead(exe);
        long offset = CharacteristicsOffset(stream);
        stream.Position = offset;
        return (new BinaryReader(stream).ReadUInt16() & Flag) != 0;
    }

    public static void Set(string exe, bool enabled)
    {
        if (IsEnabled(exe) == enabled) return;
        string backup = exe + ".bak";
        if (!System.IO.File.Exists(backup)) System.IO.File.Copy(exe, backup);
        using FileStream stream = new(exe, FileMode.Open, FileAccess.ReadWrite);
        long offset = CharacteristicsOffset(stream);
        stream.Position = offset;
        ushort value = new BinaryReader(stream, Encoding.ASCII, true).ReadUInt16();
        value = enabled ? (ushort)(value | Flag) : (ushort)(value & ~Flag);
        stream.Position = offset;
        new BinaryWriter(stream, Encoding.ASCII, true).Write(value);
    }
}
