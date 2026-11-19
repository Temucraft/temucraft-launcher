using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Launcher.Core;

/// <summary>
/// Information about the player's PC that a server may ask the launcher to send with its requests, so the server's
/// owner knows who is connecting. Nothing is sent unless the server lists it (COLLECT in launcher.env), and the
/// login window shows the list before anything is sent. Each value travels in its own header,
/// <see cref="HeaderPrefix"/> + its name (e.g. X-Temucraft-osUser), URL-encoded, so the server can log each one in its
/// own column (see server/apache-launcher-clients.conf.example).
/// </summary>
public static class ClientInfo
{
    public const string HeaderPrefix = "X-Temucraft-";

    /// <summary>Every item a server can ask for, with how the login window describes it.</summary>
    public static readonly IReadOnlyList<(string Key, string Description)> Items =
    [
        ("launcherVersion", "The launcher's version"),
        ("os", "Your operating system, its version and build"),
        ("installId", "A random ID made for this launcher install"),
        ("computerName", "Your computer's name"),
        ("osUser", "Your operating system username"),
        ("mac", "Your network adapters' MAC addresses"),
        ("localIp", "Your local IP addresses (IPv4 and IPv6)"),
        ("timezone", "Your time zone"),
        ("language", "Your system language"),
        ("hardware", "Your processor and memory"),
    ];

    /// <summary>
    /// Sent to every server unless its launcher.env says COLLECT=none: the launcher's version (also in the User-Agent),
    /// the operating system with version and build, and the random install ID. Every server also sees the public IP
    /// address and answers with a code (let in or not); the login window says so.
    /// </summary>
    public static readonly IReadOnlyList<string> Defaults = ["launcherVersion", "os", "installId"];

    /// <summary>The items a server may add on top of <see cref="Defaults"/>.</summary>
    public static IEnumerable<(string Key, string Description)> Optional => Items.Where(i => !Defaults.Contains(i.Key));

    public static string Describe(string key) =>
        Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Description ?? key;

    /// <summary>
    /// What a COLLECT list means, in the order of <see cref="Items"/>: <see cref="Defaults"/> plus the items listed;
    /// "all" is every item, "none" is nothing at all (not even the defaults). No list at all: the defaults.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? keys)
    {
        HashSet<string> wanted = new((keys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (wanted.Contains("none")) return [];
        if (wanted.Contains("all")) return Items.Select(i => i.Key).ToList();
        wanted.UnionWith(Defaults);
        return Items.Where(i => wanted.Contains(i.Key)).Select(i => i.Key).ToList();
    }

    /// <summary>The values of the listed items, read from this PC. An item that can't be read is left out.</summary>
    public static Dictionary<string, string> Read(IEnumerable<string> keys, string installId)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Normalize(keys))
        {
            try
            {
                string? value = key switch
                {
                    "launcherVersion" => LauncherInfo.Version,
                    "os" => OsName(),
                    "installId" => installId,
                    "computerName" => Environment.MachineName,
                    "osUser" => Environment.UserName,
                    // per adapter, by its name ("Ethernet", "Wi-Fi", eth0, en0...): "Ethernet: 02:50:... | Wi-Fi: ..."
                    // each MAC once, under the first (real) adapter that has it: Windows also lists it under its filter drivers
                    "mac" => Group(Adapters().Select(n => (n.Name, FormatMac(n.GetPhysicalAddress().GetAddressBytes())))
                        .Where(a => a.Item2 is not null).DistinctBy(a => a.Item2).Select(a => (a.Name, new[] { a.Item2! }))),
                    "localIp" => Group(Adapters().Select(n => (n.Name, n.GetIPProperties().UnicastAddresses
                        .Where(a => IsUsefulLocal(a.Address) && !IsTemporary(a))
                        .OrderBy(a => a.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                        .Select(a => a.Address.ToString()).Distinct().ToArray()))),
                    "timezone" => TimeZoneInfo.Local.Id,
                    "language" => CultureInfo.CurrentUICulture.Name,
                    "hardware" => $"{Environment.ProcessorCount} threads, {Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30))} GB memory",
                    _ => null,
                };
                if (!string.IsNullOrEmpty(value)) values[key] = value;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                Logger.Warning($"Couldn't read {key} to send to the server", e);
            }
        }
        return values;
    }

    static IEnumerable<NetworkInterface> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                                                              && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                                                              && !IsFilterDriver(n.Name));

    /// <summary>Windows' filter drivers show up as extra adapters ("Ethernet-WFP Native MAC Layer LightWeight
    /// Filter-0000", "...-Npcap Packet Driver (NPCAP)-0000", "...-QoS Packet Scheduler-0000").</summary>
    public static bool IsFilterDriver(string name) =>
        name.EndsWith("-0000", StringComparison.Ordinal) || name.Contains("Filter-", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Packet Driver", StringComparison.OrdinalIgnoreCase) || name.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase);

    /// <summary>A short title for each item, for the Launcher page.</summary>
    public static string Title(string key) => key switch
    {
        "launcherVersion" => "Launcher version (Temucraft)", "os" => "Operating system", "installId" => "Install ID",
        "computerName" => "Computer name", "osUser" => "Operating system username", "mac" => "MAC addresses",
        "localIp" => "Local IP addresses", "timezone" => "Time zone", "language" => "System language",
        "hardware" => "Processor and memory", _ => key,
    };

    /// <summary>Adapters with something to report, as "Name: a, b | Other: c" (adapters with nothing are left out).</summary>
    public const string AdapterSeparator = " | ";

    public static string Group(IEnumerable<(string Name, string[] Values)> adapters) =>
        string.Join(AdapterSeparator, adapters.Where(a => a.Values.Length > 0).Select(a => $"{a.Name}: {string.Join(", ", a.Values)}"));

    /// <summary>A real 6-byte hardware address as 02:50:55:6F:87:0E; null for tunnels and empty ones.</summary>
    public static string? FormatMac(byte[] bytes) =>
        bytes.Length == 6 && bytes.Any(b => b != 0) ? string.Join(":", bytes.Select(b => b.ToString("X2"))) : null;

    /// <summary>
    /// An address worth reporting: not loopback, not link-local (fe80::, 169.254.x.x: made up by the PC itself), not
    /// Teredo (2001:0::, a Windows tunnel). The LAN IPv4, VPN addresses and stable IPv6 stay.
    /// </summary>
    public static bool IsUsefulLocal(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254) && !(b[0] == 0);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !address.IsIPv6Teredo
               && !address.IsIPv6Multicast;
    }

    /// <summary>IPv6 privacy addresses change every day or so; the stable one is enough.</summary>
    static bool IsTemporary(UnicastIPAddressInformation a)
    {
        if (a.Address.AddressFamily != AddressFamily.InterNetworkV6 || !OperatingSystem.IsWindows()) return false;
        try { return a.SuffixOrigin == SuffixOrigin.Random; }
        catch (PlatformNotSupportedException) { return false; }
    }

    /// <summary>"Windows 11 25H2 (build 26200.7171)" rather than "Microsoft Windows 10.0.26200" (Windows 11 still
    /// calls itself 10.0); other systems as they describe themselves.</summary>
    public static string OsName()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.OSDescription;
        Version v = Environment.OSVersion.Version;
        string name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : $"Windows {v.Major}.{v.Minor}";
        string release = "", revision = "";
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = key?.GetValue("DisplayVersion") as string ?? "";
            revision = key?.GetValue("UBR") is int ubr ? "." + ubr : "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return $"{name}{(release.Length > 0 ? " " + release : "")} (build {v.Build}{revision})";
    }

    /// <summary>The headers to send: one per item, the value URL-encoded (any name or language fits in a header, and
    /// commas and quotes can't break the server's CSV log).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string> values) =>
        values.Select(v => KeyValuePair.Create(HeaderPrefix + v.Key, Uri.EscapeDataString(v.Value)));
}
