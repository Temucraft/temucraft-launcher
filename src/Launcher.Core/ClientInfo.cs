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

    public static string Describe(string key) =>
        Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Description ?? key;

    /// <summary>The known items of a COLLECT list, in the order of <see cref="Items"/>; "all" means every item.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> keys)
    {
        HashSet<string> wanted = new(keys.Select(k => k.Trim()).Where(k => k.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (wanted.Contains("all")) return Items.Select(i => i.Key).ToList();
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
                    "os" => RuntimeInformation.OSDescription,
                    "installId" => installId,
                    "computerName" => Environment.MachineName,
                    "osUser" => Environment.UserName,
                    "mac" => string.Join(",", Adapters().Select(n => n.GetPhysicalAddress().ToString()).Where(m => m.Length > 0 && m != "000000000000").Distinct()),
                    "localIp" => string.Join(",", Adapters().SelectMany(n => n.GetIPProperties().UnicastAddresses)
                        .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !System.Net.IPAddress.IsLoopback(a.Address))
                        .Select(a => a.Address.ToString()).Distinct()),
                    "timezone" => TimeZoneInfo.Local.Id,
                    "language" => CultureInfo.CurrentUICulture.Name,
                    "hardware" => $"{Environment.ProcessorCount} threads, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB",
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
                                                              && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    /// <summary>The headers to send: one per item, the value URL-encoded (any name or language fits in a header, and
    /// commas and quotes can't break the server's CSV log).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string> values) =>
        values.Select(v => KeyValuePair.Create(HeaderPrefix + v.Key, Uri.EscapeDataString(v.Value)));
}
