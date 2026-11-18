using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.Core;

// A small test runner (no test framework needed): each test throws on failure.
int failed = 0;
List<(string Name, Func<Task> Body)> tests =
[
    ("Manifest round-trips and lists locales", ManifestRoundTrip),
    ("Manifest refuses paths outside the folder", ManifestRejectsUnsafePaths),
    ("Manifest refuses absolute urls and bad hashes", ManifestRejectsBadUrlsAndHashes),
    ("launcher.env needs SERVER_URL and https", EnvParsing),
    ("Planner picks patches, chosen locales, addons and client", PlannerChoices),
    ("Outdated finds missing, wrong-size and changed files", OutdatedFiles),
    ("Hash cache reuses and refreshes hashes", HashCacheReuse),
    ("Download installs a file only when its checksum matches", DownloadVerifies),
    ("Download resumes a partial file with a range request", DownloadResumes),
    ("Wrong password and blocked IP give clear errors", LoginErrors),
    ("Realmlist goes to every locale; obsolete files are removed", GameFolderChanges),
    ("File names with spaces and # download from the right address", DownloadEscapesNames),
    ("Parses make-manifest.py's output (LAUNCHER_SAMPLE_MANIFEST)", SampleManifest),
    ("Option groups install the picked option and remove the others", OptionGroups),
    ("Config.wtf values are read, changed and removed", ConfigWtfEdits),
    ("Large Address Aware flips one header bit and keeps a backup", LargeAddressAwareBit),
    ("addons.json refuses bad repositories and paths", CatalogRules),
    ("Untracked patches skip stock and server files", UntrackedPatches),
    ("Backups restore WTF; macros export and import", BackupsAndMacros),
];

static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
static string HexOf(char c) => new(c, 64);

static Task OptionGroups()
{
    Manifest m = Manifest.Parse("""
    {"optionGroups":[{"name":"Login screen","options":["Classic","Northrend"]},{"name":"HD","options":["On"],"optional":true}],
     "files":[{"path":"Data/patch-L.MPQ","url":"o/a","size":1,"sha256":"SHA_A","category":"option","group":"Login screen","option":"Classic"},
              {"path":"Data/patch-L.MPQ","url":"o/b","size":1,"sha256":"SHA_B","category":"option","group":"Login screen","option":"Northrend"},
              {"path":"Data/patch-H.MPQ","url":"o/c","size":1,"sha256":"SHA_C","category":"option","group":"HD","option":"On"}]}
    """.Replace("SHA_A", HexOf('a')).Replace("SHA_B", HexOf('b')).Replace("SHA_C", HexOf('c')));
    string wow = TempDir();
    var none = UpdatePlanner.Wanted(m, new InstallChoice(wow, [], false));
    Check(none.Count == 1 && none[0].File.Url == "o/a", "default picks the first option, optional groups none");
    Check(UpdatePlanner.Unchosen(m, new InstallChoice(wow, [], false)).SequenceEqual(["Data/patch-H.MPQ"]), "same path of the picked option is kept");
    var picked = UpdatePlanner.Wanted(m, new InstallChoice(wow, [], false, Options: new Dictionary<string, string> { ["Login screen"] = "Northrend", ["HD"] = "On" }));
    Check(picked.Select(p => p.File.Url).Order().SequenceEqual(["o/b", "o/c"]), "picked options");
    return Task.CompletedTask;
}

static Task ConfigWtfEdits()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "WTF"));
    File.WriteAllText(Path.Combine(wow, "WTF", "Config.wtf"), "SET gxResolution \"1920x1080\"\nSET farclip \"777\"\n");
    ConfigWtf c = ConfigWtf.Load(wow);
    Check(c.Get("FARCLIP") == "777", "reads case-insensitively");
    c.Set("farclip", "1277"); c.Set("maxFPS", "144"); c.Set("gxResolution", null); c.Save();
    string text = File.ReadAllText(Path.Combine(wow, "WTF", "Config.wtf"));
    Check(text.Contains("SET farclip \"1277\"") && text.Contains("SET maxFPS \"144\"") && !text.Contains("gxResolution"), text);
    return Task.CompletedTask;
}

static Task LargeAddressAwareBit()
{
    string dir = TempDir(), exe = Path.Combine(dir, "Wow.exe");
    byte[] image = new byte[512];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
    BitConverter.GetBytes(0x00004550u).CopyTo(image, 0x80);
    BitConverter.GetBytes((ushort)0x0102).CopyTo(image, 0x80 + 22);
    File.WriteAllBytes(exe, image);
    Check(!LargeAddressAware.IsEnabled(exe), "starts off");
    LargeAddressAware.Set(exe, true);
    Check(LargeAddressAware.IsEnabled(exe) && File.Exists(exe + ".bak"), "on with backup");
    Check(BitConverter.ToUInt16(File.ReadAllBytes(exe), 0x80 + 22) == 0x0122, "only the one bit changes");
    LargeAddressAware.Set(exe, false);
    Check(!LargeAddressAware.IsEnabled(exe), "off again");
    return Task.CompletedTask;
}

static Task CatalogRules()
{
    var list = AddonFolder.ParseCatalog("""[{"name":"A","repo":"Temucraft/addon-a","branch":"main","folders":["A"],"required":true}]""");
    Check(list[0].ZipUrl == "https://codeload.github.com/Temucraft/addon-a/zip/refs/heads/main", list[0].ZipUrl);
    foreach (string bad in new[] { """[{"name":"B","repo":"https://evil/x"}]""", """[{"name":"C","repo":"a/b","folders":["../x"]}]""" })
    {
        bool threw = false;
        try { AddonFolder.ParseCatalog(bad); } catch (InvalidDataException) { threw = true; }
        Check(threw, "refused " + bad);
    }
    return Task.CompletedTask;
}

static Task UntrackedPatches()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Data", "enUS"));
    foreach (string f in new[] { "common.MPQ", "patch-3.MPQ", "patch-A.MPQ", "patch-Z.MPQ", "enUS/patch-enUS-3.MPQ", "enUS/patch-enUS-X.MPQ" })
        File.WriteAllText(Path.Combine(wow, "Data", f), "");
    Manifest m = Manifest.Parse("""{"files":[{"path":"Data/patch-A.MPQ","url":"p/a","size":0,"sha256":"SHA"}]}""".Replace("SHA", HexOf('a')));
    var found = Untracked.Find(wow, m);
    Check(found.Order().SequenceEqual(["Data/enUS/patch-enUS-X.MPQ", "Data/patch-Z.MPQ"]), string.Join(",", found));
    return Task.CompletedTask;
}

static Task BackupsAndMacros()
{
    string wow = TempDir(), char1 = Path.Combine(wow, "WTF", "Account", "ME", "Realm", "Hero");
    Directory.CreateDirectory(char1);
    File.WriteAllText(Path.Combine(char1, "macros-cache.txt"), "MACRO 1");
    File.WriteAllText(Path.Combine(wow, "WTF", "Config.wtf"), "SET a \"1\"");
    string zip = Path.Combine(TempDir(), "m.zip");
    Check(Backups.ExportMacros(wow, zip) == 1, "one macro file exported");
    File.WriteAllText(Path.Combine(char1, "macros-cache.txt"), "changed");
    Check(Backups.ImportMacros(wow, zip) == 1 && File.ReadAllText(Path.Combine(char1, "macros-cache.txt")) == "MACRO 1", "imported back");
    string backup = Backups.Create(wow, "test", includeAddons: false, keep: 50);
    File.WriteAllText(Path.Combine(wow, "WTF", "Config.wtf"), "broken");
    Backups.Restore(wow, backup);
    Check(File.ReadAllText(Path.Combine(wow, "WTF", "Config.wtf")) == "SET a \"1\"", "restored");
    return Task.CompletedTask;
}

foreach ((string name, Func<Task> body) in tests)
{
    try
    {
        await body();
        Console.WriteLine($"  pass  {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}: {e.Message}");
    }
}
Console.WriteLine(failed == 0 ? $"{tests.Count} passed" : $"{failed} of {tests.Count} FAILED");
return failed == 0 ? 0 : 1;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new Exception(message);
}

static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

static string TempDir()
{
    string dir = Path.Combine(Path.GetTempPath(), "launcher-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static ManifestFile File1(string path, byte[] data, FileCategory category = FileCategory.Patch, string? locale = null) =>
    new() { Path = path, Url = "files/" + path, Size = data.Length, Sha256 = Sha(data), Category = category, Locale = locale };

static Task ManifestRoundTrip()
{
    Manifest manifest = new()
    {
        Realmlist = "logon.example",
        Files =
        [
            File1("Data/patch-A.MPQ", [1, 2, 3]),
            File1("Data/deDE/patch-deDE-4.MPQ", [4], FileCategory.Locale, "deDE"),
            File1("Data/enUS/patch-enUS-4.MPQ", [5], FileCategory.Locale, "enUS"),
        ],
        Obsolete = ["Data/patch-R.MPQ"],
    };
    Manifest back = Manifest.Parse(manifest.ToJson());
    Assert(back.Files.Count == 3 && back.Realmlist == "logon.example", "files or realmlist lost");
    Assert(back.Files[1].Category == FileCategory.Locale, "category lost");
    Assert(back.Locales.SequenceEqual(["deDE", "enUS"]), "locales: " + string.Join(",", back.Locales));
    Assert(back.ToJson().Contains("\"locale\""), "json isn't camelCase");
    return Task.CompletedTask;
}

static Task ManifestRejectsUnsafePaths()
{
    foreach (string bad in new[] { "../Wow.exe", "Data/../../x", "/etc/passwd", "C:/Windows/x", "Data\\..\\..\\x" })
    {
        Manifest manifest = new() { Files = [File1(bad, [1])] };
        Throws<InvalidDataException>(() => Manifest.Parse(manifest.ToJson()), "accepted " + bad);
    }
    Manifest obsolete = new() { Obsolete = ["../../boot.ini"] };
    Throws<InvalidDataException>(() => Manifest.Parse(obsolete.ToJson()), "accepted an unsafe obsolete path");
    Throws<InvalidDataException>(() => PathRules.Resolve("/tmp/wow", "../x"), "Resolve left the folder");
    Assert(PathRules.Resolve("/tmp/wow", "Data/patch-A.MPQ").EndsWith(Path.Combine("Data", "patch-A.MPQ")), "Resolve");
    return Task.CompletedTask;
}

static Task ManifestRejectsBadUrlsAndHashes()
{
    ManifestFile absolute = File1("Data/a.MPQ", [1]);
    absolute.Url = "https://evil.example/a.MPQ";
    Throws<InvalidDataException>(() => Manifest.Parse(new Manifest { Files = [absolute] }.ToJson()), "absolute url");
    ManifestFile up = File1("Data/a.MPQ", [1]);
    up.Url = "../secret";
    Throws<InvalidDataException>(() => Manifest.Parse(new Manifest { Files = [up] }.ToJson()), "url with ..");
    ManifestFile hash = File1("Data/a.MPQ", [1]);
    hash.Sha256 = "abc";
    Throws<InvalidDataException>(() => Manifest.Parse(new Manifest { Files = [hash] }.ToJson()), "short hash");
    ManifestFile noLocale = File1("Data/enUS/x.MPQ", [1], FileCategory.Locale);
    Throws<InvalidDataException>(() => Manifest.Parse(new Manifest { Files = [noLocale] }.ToJson()), "locale file without locale");
    return Task.CompletedTask;
}

static Task EnvParsing()
{
    ServerSettings settings = ServerSettings.Parse(["# comment", "SERVER_URL = \"https://files.example/wow\""]);
    Assert(settings.ServerUrl.ToString() == "https://files.example/wow/", "url: " + settings.ServerUrl);
    Throws<InvalidDataException>(() => ServerSettings.Parse(["# nothing"]), "missing SERVER_URL accepted");
    Throws<InvalidDataException>(() => ServerSettings.Parse(["SERVER_URL=http://files.example/"]), "http accepted");
    Assert(ServerSettings.Parse(["SERVER_URL=http://192.168.1.5/", "ALLOW_HTTP=1"]).AllowHttp, "ALLOW_HTTP ignored");
    Throws<InvalidDataException>(() => ServerSettings.Parse(["SERVER_URL=ftp://x/"]), "ftp accepted");
    return Task.CompletedTask;
}

static Task PlannerChoices()
{
    Manifest manifest = new()
    {
        Files =
        [
            File1("Data/patch-A.MPQ", [1]),
            File1("Data/enUS/patch-enUS-4.MPQ", [2], FileCategory.Locale, "enUS"),
            File1("Data/deDE/patch-deDE-4.MPQ", [3], FileCategory.Locale, "deDE"),
            File1("Interface/AddOns/Hide/Hide.toc", [4], FileCategory.Addon),
            File1("Data/common.MPQ", [5], FileCategory.Base),
        ],
    };
    string wow = TempDir(), other = TempDir();
    var basic = UpdatePlanner.Wanted(manifest, new InstallChoice(wow, ["enUS"], Addons: false));
    Assert(basic.Select(p => p.File.Path).SequenceEqual(["Data/patch-A.MPQ", "Data/enUS/patch-enUS-4.MPQ"]),
        "basic: " + string.Join(",", basic.Select(p => p.File.Path)));
    var all = UpdatePlanner.Wanted(manifest, new InstallChoice(wow, ["ENUS", "deDE"], Addons: true, BaseFolder: other));
    Assert(all.Count == 5, "all: " + all.Count);
    Assert(all.Single(p => p.File.Category == FileCategory.Base).Target.StartsWith(other), "base files go to their own folder");
    return Task.CompletedTask;
}

static async Task OutdatedFiles()
{
    string wow = TempDir();
    byte[] good = [1, 2, 3], other = [9, 9, 9];
    Manifest manifest = new() { Files = [File1("Data/a.MPQ", good), File1("Data/b.MPQ", good), File1("Data/c.MPQ", good)] };
    Directory.CreateDirectory(Path.Combine(wow, "Data"));
    await File.WriteAllBytesAsync(Path.Combine(wow, "Data", "a.MPQ"), good);
    await File.WriteAllBytesAsync(Path.Combine(wow, "Data", "b.MPQ"), other);      // same size, different content
    var planned = UpdatePlanner.Wanted(manifest, new InstallChoice(wow, [], false)); // c.MPQ is missing
    var outdated = await UpdatePlanner.OutdatedAsync(planned, HashCache.Load(Path.Combine(wow, "cache.json")));
    Assert(outdated.Select(p => p.File.Path).SequenceEqual(["Data/b.MPQ", "Data/c.MPQ"]),
        "outdated: " + string.Join(",", outdated.Select(p => p.File.Path)));
}

static async Task HashCacheReuse()
{
    string dir = TempDir(), file = Path.Combine(dir, "x.bin"), cacheFile = Path.Combine(dir, "cache.json");
    await File.WriteAllBytesAsync(file, [1, 2, 3]);
    HashCache cache = HashCache.Load(cacheFile);
    string first = await cache.GetAsync(file);
    Assert(first == Sha([1, 2, 3]), "hash");
    cache.Save();

    // A stale cache entry is used as long as size and time match: proves the cache is read
    string text = (await File.ReadAllTextAsync(cacheFile)).Replace(first, new string('a', 64));
    await File.WriteAllTextAsync(cacheFile, text);
    Assert(await HashCache.Load(cacheFile).GetAsync(file) == new string('a', 64), "cache not used");

    // Changing the file refreshes it
    await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
    Assert(await HashCache.Load(cacheFile).GetAsync(file) == Sha([1, 2, 3, 4]), "changed file not hashed again");
}

static async Task DownloadVerifies()
{
    byte[] data = RandomNumberGenerator.GetBytes(300_000);
    FakeServer server = new(new() { ["files/Data/a.MPQ"] = data });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    string dir = TempDir(), target = Path.Combine(dir, "Data", "a.MPQ");

    await client.DownloadAsync(File1("Data/a.MPQ", data), target);
    Assert(File.Exists(target) && (await File.ReadAllBytesAsync(target)).SequenceEqual(data), "not installed");
    Assert(!File.Exists(target + ".part"), "part left behind");
    Assert(server.LastAuth == "Basic " + Convert.ToBase64String("u:p"u8.ToArray()), "no basic auth sent");

    // The server sends something else than the manifest says: nothing gets installed
    ManifestFile lying = File1("Data/b.MPQ", data);
    server.Files["files/Data/b.MPQ"] = RandomNumberGenerator.GetBytes(data.Length);
    string bad = Path.Combine(dir, "Data", "b.MPQ");
    bool threw = false;
    try { await client.DownloadAsync(lying, bad); } catch (InvalidDataException) { threw = true; }
    Assert(threw && !File.Exists(bad) && !File.Exists(bad + ".part"), "corrupt file kept");
}

static async Task DownloadResumes()
{
    byte[] data = RandomNumberGenerator.GetBytes(500_000);
    FakeServer server = new(new() { ["files/Data/a.MPQ"] = data });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    string dir = TempDir(), target = Path.Combine(dir, "Data", "a.MPQ");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    await File.WriteAllBytesAsync(target + ".part", data[..200_000]);

    await client.DownloadAsync(File1("Data/a.MPQ", data), target);
    Assert(server.LastRangeFrom == 200_000, "no range request: " + server.LastRangeFrom);
    Assert((await File.ReadAllBytesAsync(target)).SequenceEqual(data), "resumed file differs");
}

static async Task LoginErrors()
{
    foreach ((HttpStatusCode code, string expected) in new[] { (HttpStatusCode.Unauthorized, "password"),
                                                              (HttpStatusCode.Forbidden, "IP") })
    {
        FakeServer server = new(new()) { ForceStatus = code };
        using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
        try
        {
            await client.GetManifestAsync();
            throw new Exception("no error for " + code);
        }
        catch (LoginFailedException e)
        {
            Assert(e.Message.Contains(expected), "message: " + e.Message);
        }
    }
}

static Task GameFolderChanges()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Data", "enUS"));
    Directory.CreateDirectory(Path.Combine(wow, "Data", "deDE"));
    Directory.CreateDirectory(Path.Combine(wow, "Data", "Interface")); // not a locale
    File.WriteAllText(Path.Combine(wow, "Data", "patch-R.MPQ"), "old");
    Assert(GameFolder.InstalledLocales(wow).SequenceEqual(["deDE", "enUS"]), "locales");
    GameFolder.WriteRealmlist(wow, "logon.example");
    Assert(File.ReadAllText(Path.Combine(wow, "Data", "deDE", "realmlist.wtf")).StartsWith("set realmlist logon.example"),
        "realmlist");
    var removed = GameFolder.RemoveObsolete(wow, ["Data/patch-R.MPQ", "Data/never-there.MPQ"]);
    Assert(removed.SequenceEqual(["Data/patch-R.MPQ"]) && !File.Exists(Path.Combine(wow, "Data", "patch-R.MPQ")), "obsolete");
    return Task.CompletedTask;
}

static async Task DownloadEscapesNames()
{
    byte[] data = [7, 7, 7];
    ManifestFile file = File1("Data/enUS/My Patch #2.MPQ", data);
    FakeServer server = new(new() { ["files/Data/enUS/My%20Patch%20%232.MPQ"] = data });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/wow files/"]), "u", "p", server);
    Assert(client.FileUri(file).AbsolutePath.EndsWith("/files/Data/enUS/My%20Patch%20%232.MPQ"), client.FileUri(file).ToString());
    string target = Path.Combine(TempDir(), "x.MPQ");
    await client.DownloadAsync(file, target);
    Assert(File.Exists(target), "not downloaded");
}

static async Task SampleManifest()
{
    string? path = Environment.GetEnvironmentVariable("LAUNCHER_SAMPLE_MANIFEST");
    if (path is null)
    {
        Console.WriteLine("        (skipped: set LAUNCHER_SAMPLE_MANIFEST to a manifest.json from server/make-manifest.py)");
        return;
    }
    Manifest manifest = Manifest.Parse(await File.ReadAllTextAsync(path));
    Assert(manifest.Files.Count > 0, "no files");
    Assert(manifest.Launcher is not null, "no launcher entry");
    Assert(manifest.Files.Any(f => f.Category == FileCategory.Locale && f.Locale is not null), "no locale files");
}

/// <summary>Answers like Apache: files under the folder, Range requests with 206.</summary>
sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = files;
    public HttpStatusCode? ForceStatus { get; init; }
    public string? LastAuth { get; private set; }
    public long? LastRangeFrom { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        LastAuth = request.Headers.Authorization?.ToString();
        if (ForceStatus is { } status)
            return Task.FromResult(new HttpResponseMessage(status));
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (path.StartsWith("wow%20files/"))
            path = path["wow%20files/".Length..];
        if (!Files.TryGetValue(path, out byte[]? data))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        long from = request.Headers.Range?.Ranges.First().From ?? 0;
        LastRangeFrom = request.Headers.Range is null ? null : from;
        HttpResponseMessage response = new(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data[(int)from..]),
        };
        if (from > 0)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
        return Task.FromResult(response);
    }
}
