using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>One login screen scene in Interface\loginui.lua: its code ("lk"), its name ("lich king") and whether it's on.</summary>
public sealed record LoginScene(string Code, string Name, bool On);

/// <summary>
/// The login screen: Interface\loginui.lua's "SceneList" picks a scene at random from the lines that aren't
/// commented out. The launcher reads the list (so it follows the server's file) and turns scenes on or off.
/// </summary>
public static partial class LoginScreens
{
    public const string FilePath = "Interface/loginui.lua";

    [GeneratedRegex(@"^(?<indent>\s*)(?<off>--)?\s*""(?<code>[a-z]{2})""\s*,\s*--\s*(?<name>.+?)\s*$")]
    private static partial Regex SceneLine();

    public static string PathIn(string wowFolder) => Path.Combine(wowFolder, "Interface", "loginui.lua");

    /// <summary>The scenes in the file's SceneList, in its order.</summary>
    public static IReadOnlyList<LoginScene> Read(string wowFolder)
    {
        string file = PathIn(wowFolder);
        return File.Exists(file) ? Parse(File.ReadAllLines(file)) : [];
    }

    /// <summary>The scenes in a loginui.lua's lines (the installed file, or the server's copy).</summary>
    public static IReadOnlyList<LoginScene> Parse(IEnumerable<string> lines)
    {
        List<LoginScene> scenes = [];
        foreach ((int _, Match m) in SceneLines([.. lines.Select(l => l.TrimEnd('\r'))]))
            scenes.Add(new LoginScene(m.Groups["code"].Value, m.Groups["name"].Value, !m.Groups["off"].Success));
        return scenes;
    }

    /// <summary>Turns on exactly the given scenes (the others are commented out); returns whether the file changed.
    /// At least one scene stays on: with none picked, the file is left as it is.</summary>
    public static bool Write(string wowFolder, IReadOnlyCollection<string> on)
    {
        string file = PathIn(wowFolder);
        if (!File.Exists(file) || on.Count == 0) return false;
        byte[] raw = File.ReadAllBytes(file);
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        string text = Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string[] lines = text.Split(newline);
        bool changed = false;
        foreach ((int index, Match m) in SceneLines(lines))
        {
            bool want = on.Contains(m.Groups["code"].Value);
            if (want == !m.Groups["off"].Success) continue;
            string line = $"{m.Groups["indent"].Value}{(want ? "" : "--")}\"{m.Groups["code"].Value}\", -- {m.Groups["name"].Value}";
            lines[index] = line;
            changed = true;
        }
        if (!changed) return false;
        byte[] body = Encoding.UTF8.GetBytes(string.Join(newline, lines));
        File.WriteAllBytes(file, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
        return true;
    }

    /// <summary>The scene lines inside SceneList = { ... }.</summary>
    static IEnumerable<(int Index, Match Match)> SceneLines(string[] lines)
    {
        int start = Array.FindIndex(lines, l => l.Contains("\"SceneList\""));
        if (start < 0) yield break;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('}')) yield break;
            Match m = SceneLine().Match(lines[i]);
            if (m.Success) yield return (i, m);
        }
    }

    /// <summary>A scene's name for the window: "lich king" becomes "Lich King".</summary>
    public static string Title(string name) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
}
