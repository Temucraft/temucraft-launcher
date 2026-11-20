using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>What the server offers, as server/make-manifest.py writes it to manifest.json.</summary>
public sealed class Manifest
{
    public int Version { get; set; } = 1;

    /// <summary>Written to realmlist.wtf in every installed locale. Empty leaves realmlist.wtf alone.</summary>
    public string Realmlist { get; set; } = "";

    /// <summary>The launcher the server hands out, for the launcher's self-check.</summary>
    public ManifestFile? Launcher { get; set; }

    /// <summary>The server's client version (from client-version.txt next to the manifest), shown at the bottom.</summary>
    public string ClientVersion { get; set; } = "";

    /// <summary>Choices the player makes for optional files: login screen, HD packs... (group name -> options).</summary>
    public List<OptionGroup> OptionGroups { get; set; } = [];
    /// <summary>Pictures of the login screen scenes, by scene code ("lk"), next to the manifest.</summary>
    public Dictionary<string, string> LoginScreenPreviews { get; set; } = [];

    public List<ManifestFile> Files { get; set; } = [];

    /// <summary>Paths (relative to the WoW folder) to delete, such as patches that were renamed.</summary>
    public List<string> Obsolete { get; set; } = [];

    /// <summary>The server's say over the launcher (launcher-policy.json next to the manifest); see <see cref="LauncherPolicy"/>.</summary>
    public Dictionary<string, JsonElement> Policy { get; set; } = [];

    [JsonIgnore]
    public LauncherPolicy Rules => _rules ??= LauncherPolicy.From(Policy);
    LauncherPolicy? _rules;

    /// <summary>The enforced value of an on/off setting, or null when the player decides.</summary>
    public bool? Enforced(string setting) => Rules.EnforcedBool(setting);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static Manifest Parse(string json)
    {
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(json, Options)
            ?? throw new InvalidDataException("The manifest is empty.");
        foreach (ManifestFile file in manifest.Files)
            file.Validate();
        manifest.Launcher?.Validate();
        foreach (string path in manifest.Obsolete)
            PathRules.CheckRelative(path);
        return manifest;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The locales the server has files for.</summary>
    public IReadOnlyList<string> Locales =>
        Files.Where(f => f.Category == FileCategory.Locale && f.Locale is not null)
             .Select(f => f.Locale!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
}

public enum FileCategory
{
    /// <summary>Always installed: the server's patches.</summary>
    Patch,
    /// <summary>Installed for the locales the player picks.</summary>
    Locale,
    /// <summary>Installed when the player turns addons on.</summary>
    Addon,
    /// <summary>Base files, installed into a folder the player picks, on request.</summary>
    Base,
    /// <summary>Part of one option of an option group (e.g. one login screen): installed only when that option is picked.</summary>
    Option,
}

/// <summary>A choice between sets of files, e.g. "Login screen": "Classic", "Northrend". The first option is the default.</summary>
public sealed class OptionGroup
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Options { get; set; } = [];
    /// <summary>True when "none" is allowed (no files of this group installed).</summary>
    public bool Optional { get; set; }
    /// <summary>Set by the server: everyone gets the first option (or none when optional), whatever they picked.</summary>
    public bool Enforce { get; set; }
    /// <summary>Links shown on the option's card (its GitHub page, website...); only http(s) ones are shown.</summary>
    public List<OptionLink> Links { get; set; } = [];
    /// <summary>Other groups this one needs, e.g. {"Character models": "HD"}: until they are picked, this group stays at
    /// its first option (or none) and its switch can't be used.</summary>
    public Dictionary<string, string> Requires { get; set; } = [];
    /// <summary>Preview images next to the manifest, by option ("" = one picture for the whole group).</summary>
    public Dictionary<string, string> Previews { get; set; } = [];
}

public sealed class OptionLink
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsWeb => Uri.TryCreate(Url, UriKind.Absolute, out Uri? u) && u.Scheme is "https" or "http";
}

public sealed class ManifestFile
{
    /// <summary>Where it goes, relative to the WoW folder, with forward slashes (e.g. Data/patch-A.MPQ).</summary>
    public string Path { get; set; } = "";

    /// <summary>Where to download it from, relative to the manifest's URL.</summary>
    public string Url { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Lowercase hex SHA-256.</summary>
    public string Sha256 { get; set; } = "";

    public FileCategory Category { get; set; }

    /// <summary>For locale files: enUS, deDE...</summary>
    public string? Locale { get; set; }

    /// <summary>For option files: the option group and the option they belong to.</summary>
    public string? Group { get; set; }
    public string? Option { get; set; }

    public void Validate()
    {
        PathRules.CheckRelative(Path);
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidDataException($"{Path}: no url.");
        if (Url.Contains("..", StringComparison.Ordinal) || Url.StartsWith('/') || Url.Contains('\\') || Url.Contains(':'))
            throw new InvalidDataException($"{Path}: the url must stay inside the server folder.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{Path}: bad sha256.");
        if (Size < 0)
            throw new InvalidDataException($"{Path}: bad size.");
        if (Category == FileCategory.Locale && string.IsNullOrWhiteSpace(Locale))
            throw new InvalidDataException($"{Path}: a locale file needs a locale.");
        if (Category == FileCategory.Option && (string.IsNullOrWhiteSpace(Group) || string.IsNullOrWhiteSpace(Option)))
            throw new InvalidDataException($"{Path}: an option file needs a group and an option.");
        Sha256 = Sha256.ToLowerInvariant();
    }
}

/// <summary>Keeps manifest paths inside the folder they are installed to.</summary>
public static class PathRules
{
    public static void CheckRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':')
            || path.Split('/', '\\').Any(part => part is ".." or "."))
            throw new InvalidDataException($"Unsafe path in the manifest: {path}");
    }

    /// <summary>The full path of a manifest path under root, refusing anything that would land outside it.</summary>
    public static string Resolve(string root, string relative)
    {
        CheckRelative(relative);
        string fullRoot = System.IO.Path.GetFullPath(root);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot,
            relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        string rootWithSeparator = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? fullRoot : fullRoot + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path in the manifest: {relative}");
        return full;
    }
}
