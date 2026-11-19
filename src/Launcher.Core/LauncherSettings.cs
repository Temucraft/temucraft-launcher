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

    /// <summary>The server's name for the login window (SERVER_NAME), e.g. "Temucraft"; empty: "this server".</summary>
    public string Name { get; init; } = "";

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
            Name = values.TryGetValue("SERVER_NAME", out string? name) ? name : "" };
    }
}

/// <summary>The player's own choices, kept in %AppData%\TemucraftLauncher\settings.json. No password here.</summary>
public sealed class UserSettings
{
    public string WowFolder { get; set; } = "";
    public string Username { get; set; } = "";
    public bool RememberPassword { get; set; }
    public List<string> Locales { get; set; } = [];
    public bool InstallAddons { get; set; } = true;
    public bool ClearCacheOnPlay { get; set; } = true;
    /// <summary>Picked option per option group (login screen...); "" = none.</summary>
    public Dictionary<string, string> Options { get; set; } = [];
    public bool ReopenAfterGame { get; set; } = true;
    public bool RememberWindow { get; set; }
    /// <summary>The remembered window position; null until "Remember window position" has saved one.</summary>
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 640;
    /// <summary>Background picture of the launcher ("" = the default).</summary>
    public string Background { get; set; } = "";
    public bool BackupBeforeUpdate { get; set; } = true;
    public int BackupsToKeep { get; set; } = 10;
    /// <summary>Installed catalog addons and the commit they were installed from (to offer updates).</summary>
    public Dictionary<string, string> AddonCommits { get; set; } = [];

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
