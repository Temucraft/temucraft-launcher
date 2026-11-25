using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>
/// The server connection, read from launcher.env next to the launcher. That file is never in git (see .gitignore and
/// launcher.env.example), so the server address stays out of the repository and out of the exe.
/// </summary>
public sealed class ServerSettings
{
    /// <summary>The folder Apache serves, ending in /; manifest.json is read from there.</summary>
    public Uri ServerUrl { get; init; } = new("https://example.invalid/");

    /// <summary>Only for testing on a LAN: plain http sends the password unencrypted.</summary>
    public bool AllowHttp { get; init; }

    /// <summary>
    /// SHA-256 fingerprint of the server's certificate (64 hex digits, colons allowed). When set, the launcher trusts
    /// that one certificate and nothing else, so a self-signed certificate works and a different one is refused.
    /// </summary>
    public string? CertificateSha256 { get; init; }

    /// <summary>What the launcher sends about the player's PC: <see cref="ClientInfo.Defaults"/> plus COLLECT's items
    /// (see <see cref="ClientInfo.Normalize"/>); empty only with COLLECT=none.</summary>
    public IReadOnlyList<string> Collect { get; init; } = [];

    /// <summary>Links to the server's websites in the launcher's header (NAV=Name|https://...|tooltip; Other|https://...).</summary>
    public IReadOnlyList<NavLink> Nav { get; init; } = [];

    /// <summary>Show the link's address when hovering it (NAV_SHOW_URL); on unless set to 0.</summary>
    public bool NavShowUrl { get; init; } = true;

    /// <summary>Show the server's address (SHOW_IP) and the logged-in username (SHOW_USERNAME) in the header; both on
    /// unless set to 0.</summary>
    public bool ShowIp { get; init; } = true;
    public bool ShowUsername { get; init; } = true;

    /// <summary>The server's name for the login window (SERVER_NAME), e.g. "Temucraft"; empty: "this server".</summary>
    public string Name { get; init; } = "";

    /// <summary>The launcher's language (LANGUAGE): "auto" (the computer's language, the default), or one of the
    /// game's languages: enUS, deDE, frFR, esES, esMX, ruRU, koKR, zhCN, zhTW. The player can still pick another one.</summary>
    public string Language { get; init; } = "auto";

    /// <summary>The server owner's own words about why (COLLECT_NOTE), shown in the login window.</summary>
    public string CollectNote { get; init; } = "";

    public static ServerSettings Load(string envFile)
    {
        if (!File.Exists(envFile))
            throw new FileNotFoundException(
                $"{System.IO.Path.GetFileName(envFile)} is missing. Copy launcher.env.example next to the launcher " +
                "as launcher.env and put your server's address in it.", envFile);
        return Parse(File.ReadAllLines(envFile));
    }

    /// <summary>"Register|https://a/|Text shown on hover; AoWoW|https://b/": only http(s) links, others are skipped.
    /// The hover text is optional; "\n" in it starts a new line.</summary>
    public static IReadOnlyList<NavLink> ParseNav(string text)
    {
        List<NavLink> links = [];
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] bits = part.Split('|', 3, StringSplitOptions.TrimEntries);
            if (bits.Length < 2 || bits[0].Length == 0) continue;
            if (Uri.TryCreate(bits[1], UriKind.Absolute, out Uri? url) && url.Scheme is "https" or "http")
                links.Add(new NavLink(bits[0], url, bits.Length > 2 ? bits[2].Replace("\\n", "\n") : ""));
        }
        return links;
    }

    public static ServerSettings Parse(IEnumerable<string> lines)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
        }

        if (!values.TryGetValue("SERVER_URL", out string? url) || string.IsNullOrWhiteSpace(url))
            throw new InvalidDataException("launcher.env has no SERVER_URL.");
        if (!url.EndsWith('/'))
            url += "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new InvalidDataException($"SERVER_URL isn't a web address: {url}");

        bool allowHttp = values.TryGetValue("ALLOW_HTTP", out string? allow) && allow is "1" or "true";
        if (uri.Scheme == "http" && !allowHttp)
            throw new InvalidDataException(
                "SERVER_URL uses http, which sends your password unencrypted. Use https (or ALLOW_HTTP=1 on a LAN).");
        string? pin = null;
        if (values.TryGetValue("CERT_SHA256", out string? rawPin) && rawPin.Length > 0)
        {
            pin = new string(rawPin.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
            if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
                throw new InvalidDataException("CERT_SHA256 must be the certificate's SHA-256 fingerprint (64 hex digits).");
        }
        IReadOnlyList<string> collect = ClientInfo.Normalize(values.TryGetValue("COLLECT", out string? list) ? list.Split(',') : null);
        string note = values.TryGetValue("COLLECT_NOTE", out string? n) ? n.Replace("\\n", "\n") : "";
        return new ServerSettings { ServerUrl = uri, AllowHttp = allowHttp, CertificateSha256 = pin, Collect = collect, CollectNote = note,
            Name = values.TryGetValue("SERVER_NAME", out string? name) ? name : "",
            Nav = values.TryGetValue("NAV", out string? nav) ? ParseNav(nav) : [],
            Language = values.TryGetValue("LANGUAGE", out string? language) && language.Length > 0 ? language : "auto",
            NavShowUrl = !values.TryGetValue("NAV_SHOW_URL", out string? showUrl) || showUrl is not ("0" or "false" or "no"),
            ShowIp = !values.TryGetValue("SHOW_IP", out string? ip) || ip is not ("0" or "false" or "no"),
            ShowUsername = !values.TryGetValue("SHOW_USERNAME", out string? user) || user is not ("0" or "false" or "no") };
    }
}

/// <summary>A link in the header: its name, address and the server's own hover text (may be empty).</summary>
public sealed record NavLink(string Name, Uri Url, string Tip)
{
    /// <summary>What hovering the link shows: the hover text, then the address when <paramref name="showUrl"/>;
    /// null when there is nothing to show.</summary>
    public string? Tooltip(bool showUrl)
    {
        string text = string.Join("\n", new[] { Tip, showUrl ? Url.ToString() : "" }.Where(t => t.Length > 0));
        return text.Length > 0 ? text : null;
    }
}

/// <summary>The player's own choices, kept in %AppData%\TemucraftLauncher\settings.json. No password here.</summary>
public sealed class UserSettings
{
    public string WowFolder { get; set; } = "";
    /// <summary>The game version PLAY starts: "live" or "ptr" (the test realm, see <see cref="GameVersions"/>).</summary>
    public string GameVersion { get; set; } = GameVersions.Live;
    /// <summary>The PTR's own install; "" = the Live folder + " PTR".</summary>
    public string PtrFolder { get; set; } = "";
    public string Username { get; set; } = "";
    public bool RememberPassword { get; set; }
    public List<string> Locales { get; set; } = [];
    public bool InstallAddons { get; set; } = true;
    public bool ClearCacheOnPlay { get; set; } = true;
    /// <summary>Skip the game's first-start agreement screens (server setting "skipGameAgreements").</summary>
    public bool SkipGameAgreements { get; set; } = true;
    /// <summary>A new PTR install starts with Live's game settings (server setting "ptrCopyLiveSettings").</summary>
    public bool PtrCopyLiveSettings { get; set; } = true;
    /// <summary>Picked option per option group (login screen...); "" = none.</summary>
    public Dictionary<string, string> Options { get; set; } = [];
    /// <summary>The login screen scenes the player picked (codes from loginui.lua); null: the server's own list.</summary>
    public List<string>? LoginScenes { get; set; }
    /// <summary>The player's character screen scene per race (race file string upper case -> scene folder).</summary>
    public Dictionary<string, string> ScenePicks { get; set; } = [];
    /// <summary>Character models: HD, but these races (HdModels.Parts) stay Classic.</summary>
    public List<string> HdClassicParts { get; set; } = [];
    /// <summary>Character screen music per race (race file string upper case -> scene code whose music plays); none =
    /// the music of the scene shown.</summary>
    public Dictionary<string, string> MusicPicks { get; set; } = [];
    /// <summary>Login screen music: a scene code whose music plays on the login screen; "" = each scene's own.</summary>
    public string LoginMusic { get; set; } = "";
    public bool ReopenAfterGame { get; set; } = true;
    public bool RememberWindow { get; set; }
    /// <summary>The launcher turned the game's debug settings on (Debug mode), so it turns them off again later.</summary>
    public bool DebugSettingsApplied { get; set; }
    /// <summary>The same for the PTR's install (each game version has its own Config.wtf).</summary>
    public bool PtrDebugSettingsApplied { get; set; }
    /// <summary>The remembered window position; null until "Remember window position" has saved one.</summary>
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 640;
    /// <summary>Background picture of the launcher ("" = the default).</summary>
    public string Background { get; set; } = "";
    /// <summary>The launcher's language ("" = launcher.env's LANGUAGE, "auto" = the computer's).</summary>
    public string Language { get; set; } = "";
    /// <summary>The player's theme and mode ("" = the server's default).</summary>
    public string Theme { get; set; } = "";
    public string ThemeMode { get; set; } = "";
    /// <summary>The server's last say over the look, so the next start looks right straight away.</summary>
    public Themes.ServerLook ServerLook { get; set; } = new();
    public bool BackupBeforeUpdate { get; set; } = true;
    /// <summary>Update starts at once instead of showing Update info first.</summary>
    public bool SkipUpdateInfo { get; set; }
    /// <summary>The launcher version whose What's new was seen.</summary>
    public string LastSeenLauncherVersion { get; set; } = "";
    /// <summary>The first-start preset question was answered (or skipped).</summary>
    public bool ConfigPresetAsked { get; set; }
    /// <summary>Downloads: the player's speed limit (KB/s, used when <see cref="LimitDownloads"/>) and speeds shown in bits.</summary>
    public bool LimitDownloads { get; set; }
    /// <summary>Keep account settings, key bindings and macros the same on every account on this PC (and addon
    /// settings, and between characters, when those are on too). See <see cref="SharedSettings"/>.</summary>
    public bool ShareAccounts { get; set; }
    public bool ShareAddonSettings { get; set; }
    public bool ShareCharacters { get; set; }
    public long DownloadLimitKBps { get; set; } = 4000;
    public bool SpeedInBits { get; set; }
    /// <summary>Update by itself when the launcher opens (and later, while it is open), optionally only between two hours.</summary>
    public bool AutoUpdate { get; set; }
    public bool AutoUpdateHoursOnly { get; set; }
    public int AutoUpdateFrom { get; set; } = 2;
    public int AutoUpdateTo { get; set; } = 5;
    /// <summary>When the game starts: "keep" (stay open), "minimize", "hide" (until the game closes, with a tray icon), "exit".</summary>
    public string OnGameLaunch { get; set; } = "hide";
    /// <summary>The window's X: "exit" or "tray" (keep running in the system tray).</summary>
    public string OnClose { get; set; } = "exit";
    /// <summary>The page at start: "news" or "last" (the last one used, <see cref="LastPage"/>).</summary>
    public string StartPage { get; set; } = "news";
    public int LastPage { get; set; }
    /// <summary>The Tweaks page's preset: "server", "recommended", "default" or "custom" ("" = not chosen yet).</summary>
    public string TweakPreset { get; set; } = "";
    /// <summary>True once the player picked a preset or changed a tweak themselves (then nothing is filled in for them).</summary>
    public bool TweakPresetChosen { get; set; }
    public int BackupsToKeep { get; set; } = 10;
    /// <summary>Installed catalog addons and the commit they were installed from (to offer updates).</summary>
    public Dictionary<string, string> AddonCommits { get; set; } = [];
    /// <summary>The same for the PTR's own addons.</summary>
    public Dictionary<string, string> PtrAddonCommits { get; set; } = [];
    /// <summary>Default addons already installed once (not installed again if the player removes them).</summary>
    public List<string> DefaultAddonsDone { get; set; } = [];
    /// <summary>Set by the install window: the next update also installs the recommended addons (once).</summary>
    public bool InstallRecommendedNext { get; set; }

    /// <summary>A random ID for this launcher install (made once; sent only when a server collects "installId").</summary>
    public string InstallId { get; set; } = "";

    /// <summary>The items the player agreed to send, and to which server; a longer list asks again.</summary>
    public List<string> CollectAgreed { get; set; } = [];
    public string CollectAgreedServer { get; set; } = "";
    /// <summary>The server's note when the player agreed (a changed note asks again) and when they agreed.</summary>
    public string CollectAgreedNote { get; set; } = "";
    public DateTime? CollectAgreedAt { get; set; }

    // NaN/Infinity are allowed so an odd value can never stop the settings from saving
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TemucraftLauncher");

    public static UserSettings Load(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file), JsonOptions) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Warning("Couldn't read the settings; using the defaults", e);
            return new UserSettings();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        // written to a temporary file first, so a crash mid-write never leaves half a settings file
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, JsonOptions));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
