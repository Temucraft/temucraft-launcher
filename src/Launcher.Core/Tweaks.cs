using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>A settings file the Tweaks page writes to: Config.wtf or dxvk.conf.</summary>
public interface ITweakFile
{
    string? Get(string name);
    /// <summary>Sets a value; null (or "" in dxvk.conf) removes the line so the default is used.</summary>
    void Set(string name, string? value);
    void Save();
}

/// <summary>WTF\Config.wtf: the client's settings, as lines of <c>SET name "value"</c>.</summary>
public sealed class ConfigWtf : ITweakFile
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
        // UTF-8 without a byte order mark, like the client writes it (ASCII would turn accented realm or account names into ?)
        System.IO.File.WriteAllLines(File, _lines, new UTF8Encoding(false));
    }
}

/// <summary>dxvk.conf next to Wow.exe: DXVK's settings, as lines of <c>name = value</c> (read when DXVK is on).</summary>
public sealed class DxvkConf : ITweakFile
{
    readonly List<string> _lines;
    static readonly Regex KeyLine = new(@"^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$");

    public string File { get; }

    DxvkConf(string file, List<string> lines) { File = file; _lines = lines; }

    public static DxvkConf Load(string wowFolder)
    {
        string file = Path.Combine(wowFolder, "dxvk.conf");
        return new DxvkConf(file, System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file).ToList() : []);
    }

    int Find(string name) => _lines.FindIndex(l => KeyLine.Match(l) is { Success: true } m && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string? Get(string name) => Find(name) is int i and >= 0 ? KeyLine.Match(_lines[i]).Groups[2].Value.Trim('"') : null;

    public void Set(string name, string? value)
    {
        int index = Find(name);
        if (string.IsNullOrEmpty(value)) { if (index >= 0) _lines.RemoveAt(index); return; }
        string line = $"{name} = {new string(value.Where(c => c is not ('\r' or '\n')).ToArray())}";
        if (index >= 0) _lines[index] = line; else _lines.Add(line);
    }

    public void Save()
    {
        if (_lines.Count == 0 && !System.IO.File.Exists(File)) return;
        System.IO.File.WriteAllLines(File, _lines, new UTF8Encoding(false));
    }
}

public enum TweakKind { Toggle, Number }

/// <summary>One client setting the Tweaks page offers. Highlighted ones are recommended.
/// <para><see cref="File"/>: "config" (WTF\Config.wtf) or "dxvk" (dxvk.conf). <see cref="Requires"/>: an Options group
/// (e.g. "DXVK") that must be on for the setting to show and be written. A toggle is "1"/"0" unless
/// <see cref="On"/>/<see cref="Off"/> say otherwise ("" removes the line).</para></summary>
public sealed record Tweak(string Section, string Name, string CVar, TweakKind Kind, string Default, string Recommended,
                           string Description, double Min = 0, double Max = 1, bool Highlighted = false,
                           string File = "config", string? Requires = null, string On = "1", string Off = "0", string Compare = "");

public static class Tweaks
{
    /// <summary>Settings that are plain Config.wtf values in 3.3.5a (no change to Wow.exe).</summary>
    public static readonly IReadOnlyList<Tweak> All =
    [
        new("General", "Always auto-loot", "autoLootDefault", TweakKind.Toggle, "0", "1", "Loot everything with one click. Default: off."),
        new("General", "Interact on left click", "interactOnLeftClick", TweakKind.Toggle, "0", "1", "Left click talks to NPCs and loots. Default: off."),
        new("General", "Max foreground FPS", "maxFPS", TweakKind.Number, "200", "144", "Highest frame rate while you play. Default: 200.", 30, 300, true),
        new("General", "Max background FPS", "maxFPSBk", TweakKind.Number, "30", "30", "Highest frame rate while the game is in the background. Default: 30.", 8, 200),
        new("Display", "Windowed mode", "gxWindow", TweakKind.Toggle, "0", "1", "Play in a window. Default: off."),
        new("Display", "Maximized window", "gxMaximize", TweakKind.Toggle, "0", "1", "Fill the screen when playing in a window. Default: off."),
        new("Display", "Vertical sync", "gxVSync", TweakKind.Toggle, "1", "0", "Stops screen tearing, adds a little input lag. Default: on."),
        new("Display", "Render distance", "farclip", TweakKind.Number, "777", "1277", "How far away land is drawn. Default: 777. Max: 1277.", 177, 1277, true),
        new("Display", "Ground clutter distance", "groundEffectDist", TweakKind.Number, "70", "140", "How far away grass is drawn. Default: 70.", 0, 140),
        new("Display", "Ground clutter density", "groundEffectDensity", TweakKind.Number, "16", "64", "How thick the grass is. Default: 16.", 16, 256),
        new("Display", "Spell detail", "spellEffectLevel", TweakKind.Number, "2", "2", "How many spell effects you see. Default: 2.", 0, 10),
        new("Camera", "Camera distance", "cameraDistanceMax", TweakKind.Number, "15", "50", "How far the camera zooms out. Default: 15. Max: 50.", 15, 50, true),
        new("Camera", "Camera distance factor", "cameraDistanceMaxFactor", TweakKind.Number, "1", "2", "Multiplies the zoom out distance. Default: 1.", 1, 4, true),
        new("Camera", "Smooth camera", "cameraSmoothStyle", TweakKind.Number, "0", "0", "How the camera follows you: 0 never, 1 smart, 2 always, 4 when moving. Default: 0.", 0, 4),
        new("Sound", "Background sounds", "Sound_EnableSoundWhenGameIsInBG", TweakKind.Toggle, "0", "1", "Keep the sound on when the game is minimized. Default: off.", Highlighted: true),
        new("Sound", "Error speech", "Sound_EnableErrorSpeech", TweakKind.Toggle, "1", "0", "Voice lines like \"I can't do that yet\". Default: on."),
        // Awesome WotLK's own settings (shown when that option is on)
        // the game's own defaults; the other versions are only shown for comparison
        new("Awesome WotLK", "Nameplate distance", "nameplateDistance", TweakKind.Number, "41", "41", "How far away nameplates appear. Default: 41 yards.", 10, 100,
            Requires: "Awesome WotLK",
            Compare: "Other versions: Retail 60 yards, TBC and Wrath Classic 41, Classic Era 20."),
        new("Awesome WotLK", "Field of view", "cameraFov", TweakKind.Number, "100", "100", "How wide the camera sees. Higher looks like a fisheye. Default: 100.", 1, 200,
            Requires: "Awesome WotLK",
            Compare: "Other versions: Retail 90 (you can pick 50 to 90). 100 here keeps the game's own view."),
        // DXVK's settings, in dxvk.conf (shown when DXVK is on)
        new("DXVK", "Frame rate cap", "d3d9.maxFrameRate", TweakKind.Number, "0", "0", "A smoother frame rate cap than the game's. 0 means no cap. Default: 0.", 0, 500,
            File: "dxvk", Requires: "DXVK"),
        new("DXVK", "Texture filtering", "d3d9.samplerAnisotropy", TweakKind.Number, "-1", "16", "Sharper ground and walls far away. -1 lets the game choose. Default: -1.", -1, 16,
            Highlighted: true, File: "dxvk", Requires: "DXVK"),
        new("DXVK", "Vertical sync", "d3d9.presentInterval", TweakKind.Number, "-1", "-1", "Stops screen tearing. -1 lets the game choose, 0 off, 1 on. Default: -1.", -1, 1,
            File: "dxvk", Requires: "DXVK"),
        new("DXVK", "Show FPS", "dxvk.hud", TweakKind.Toggle, "", "", "Shows the frame rate in the top left corner. Default: off.",
            File: "dxvk", Requires: "DXVK", On: "fps", Off: ""),
    ];

    /// <summary>Which file a setting lives in: the Tweaks list's own setting, or Config.wtf for any other name.</summary>
    public static string FileOf(string cvar) =>
        All.FirstOrDefault(t => t.CVar.Equals(cvar, StringComparison.OrdinalIgnoreCase))?.File ?? "config";

    public static ITweakFile Load(string wowFolder, string file) =>
        file == "dxvk" ? DxvkConf.Load(wowFolder) : ConfigWtf.Load(wowFolder);

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
