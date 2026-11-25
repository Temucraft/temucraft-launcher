using System.Net.Security;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>One realm from the website's <c>?realm-status=data</c>: counts only, nothing about single players. Id is the
/// realm's id in the login server's realmlist (0 when the website doesn't send it).</summary>
public sealed record RealmInfo(string Name, bool Online, int Players, int Alliance, int Horde, int Peak, long PeakTime, long Uptime, int Id = 0);

/// <summary>
/// The realm status block on the main page. The website (AoWoW) answers <c>?realm-status=data</c> with the realms, players
/// online, the all-time peak and the uptime, cached there for 30 seconds. Where it is read from: the policy's
/// "realmStatusUrl" (false or "" turns the block off), else the website link in launcher.env's NAV (the one named like
/// Database / AoWoW / Website, else the first) with <c>?realm-status=data</c>. The "Player map" link: the policy's
/// "playerMapUrl", else the map address the website sends, else the website with <c>?player-map</c>.
/// </summary>
public sealed class RealmStatus
{
    public IReadOnlyList<RealmInfo> Realms { get; init; } = [];
    public bool MapEnabled { get; init; }
    public Uri? MapUrl { get; init; }

    static readonly string[] SiteWords = ["database", "aowow", "armory", "website", "site", "db", "web", "home"];

    public static RealmStatus Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        List<RealmInfo> realms = [];
        if (root.TryGetProperty("realms", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            foreach (JsonElement r in list.EnumerateArray())
                realms.Add(new RealmInfo(Text(r, "name"), r.TryGetProperty("online", out JsonElement on) && on.ValueKind == JsonValueKind.True,
                    (int)Number(r, "players"), (int)Number(r, "alliance"), (int)Number(r, "horde"), (int)Number(r, "peak"),
                    Number(r, "peakTime"), Number(r, "uptime"), (int)Number(r, "id")));
        return new RealmStatus
        {
            Realms = realms,
            MapEnabled = !root.TryGetProperty("mapEnabled", out JsonElement map) || map.ValueKind != JsonValueKind.False,
            MapUrl = Web(Text(root, "mapUrl")),
        };
    }

    static string Text(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Number(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
    static Uri? Web(string? text) => Uri.TryCreate(text, UriKind.Absolute, out Uri? u) && u.Scheme is "https" or "http" ? u : null;

    /// <summary>The website's base address (folder of the page, no query) from the NAV links; null without links.</summary>
    public static Uri? Website(IReadOnlyList<NavLink> nav)
    {
        NavLink? link = nav.FirstOrDefault(n => SiteWords.Any(w => n.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) ?? nav.FirstOrDefault();
        if (link is null) return null;
        Uri page = new(link.Url.GetLeftPart(UriPartial.Path));
        return page.AbsolutePath.EndsWith('/') ? page : new Uri(page, ".");
    }

    /// <summary>Where to read the status from; null means the block is off (policy false or "", or no website known).</summary>
    public static Uri? StatusUrl(LauncherPolicy policy, IReadOnlyList<NavLink> nav)
    {
        if (policy.Setting("realmStatusUrl") is { } rule)
            return rule.Value.ValueKind == JsonValueKind.String ? Web(rule.Value.GetString()) : null;
        return Website(nav) is { } site ? new Uri(site, "?realm-status=data") : null;
    }

    /// <summary>The "Player map" link; null hides it.</summary>
    public static Uri? PlayerMapUrl(LauncherPolicy policy, IReadOnlyList<NavLink> nav, RealmStatus? status, Uri? statusUrl)
    {
        if (policy.Setting("playerMapUrl") is { } rule)
            return rule.Value.ValueKind == JsonValueKind.String ? Web(rule.Value.GetString()) : null;
        if (status is { MapEnabled: false }) return null;
        if (status?.MapUrl is { } sent) return sent;
        Uri? site = statusUrl is null ? Website(nav) : new Uri(statusUrl.GetLeftPart(UriPartial.Path));
        return site is null ? null : new Uri(site, "?player-map");
    }

    /// <summary>"3d 4h", "5h 12m", "9m".</summary>
    public static string Duration(long seconds)
    {
        if (seconds <= 0) return "-";
        long d = seconds / 86400, h = seconds % 86400 / 3600, m = seconds % 3600 / 60;
        return d > 0 ? $"{d}d {h}h" : h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    /// <summary>Reads the status; a normal certificate, or the pinned one of launcher.env (same self-signed server).</summary>
    public static async Task<RealmStatus> FetchAsync(Uri url, string? pin, HttpMessageHandler? handler = null, CancellationToken token = default)
    {
        using HttpClient http = new(handler ?? new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    errors == SslPolicyErrors.None || (pin is not null && CertificatePin.Accept(cert, errors, pin)),
            },
        }, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherInfo.UserAgent);
        return Parse(await http.GetStringAsync(url, token));
    }
}
