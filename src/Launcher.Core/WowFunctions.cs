using System.IO.Compression;
using System.Reflection;

namespace Launcher.Core;

/// <summary>
/// Names for Wow.exe (3.3.5a, build 12340) code addresses, from the wowdecomp function list
/// (tools/wow_functions.py builds wow-functions.txt.gz). A crash report only has bare addresses; with this a pasted
/// crash says which function it was in.
/// </summary>
public static class WowFunctions
{
    // Past this distance from the last known start, the address is most likely in a function the list doesn't have.
    const uint MaxOffset = 0x2000;

    static readonly Lazy<(uint[] Starts, string[] Names)> table = new(Load);

    static (uint[], string[]) Load()
    {
        List<uint> starts = [];
        List<string> names = [];
        try
        {
            using Stream? raw = Assembly.GetExecutingAssembly().GetManifestResourceStream("Launcher.Core.wow-functions.txt.gz");
            if (raw is null) return ([], []);
            using StreamReader reader = new(new GZipStream(raw, CompressionMode.Decompress));
            while (reader.ReadLine() is { } line)
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                if (!uint.TryParse(line.AsSpan(0, space), System.Globalization.NumberStyles.HexNumber, null, out uint start)) continue;
                starts.Add(start);
                names.Add(line[(space + 1)..]);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { }
        return (starts.ToArray(), names.ToArray());
    }

    /// <summary>"CGReputationInfo::AddHeader+0x31" for an address, or null when the list doesn't know it.</summary>
    public static string? Name(uint address)
    {
        (uint[] starts, string[] names) = table.Value;
        int i = Array.BinarySearch(starts, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        uint offset = address - starts[i];
        if (offset > MaxOffset) return null;
        return offset == 0 ? names[i] : $"{names[i]}+0x{offset:X}";
    }

    /// <summary>Name(address) for a hex address such as "005CFEA1" or "0x005CFEA1".</summary>
    public static string? Name(string hex)
    {
        string h = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint address) ? Name(address) : null;
    }
}
