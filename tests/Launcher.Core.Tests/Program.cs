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
    ("Settings save and load with no window position (NaN crash)", SettingsSaveDefaults),
    ("Config.wtf keeps accented names (UTF-8, no BOM)", ConfigWtfKeepsUtf8),
    ("An empty file downloads", DownloadEmptyFile),
    ("Manifest refuses urls that leave the files folder", ManifestRejectsRootUrls),
    ("An addon in a sub folder downloads only its own files", AddonFromSubFolder),
    ("An addon falls back to the zip when GitHub's API fails", AddonZipFallback),
    ("README Markdown becomes plain text", MarkdownPlainText),
    ("The log writes details, rotates and tells the window", LoggerWritesAndRotates),
    ("CERT_SHA256 is read and checked", CertPinParsing),
    ("A pinned self-signed certificate is trusted, others are refused", CertPinOverTls),
    ("The same file twice in an update is downloaded once and copied", SameFileDownloadedOnce),
    ("A locale file another installed locale already has is copied, not downloaded", LocaleFileCopiedFromOtherLocale),
    ("A copy source that changed is not used", ChangedSourceIsDownloaded),
    ("The server's policy enforces settings and option groups", PolicyEnforces),
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

static Task SettingsSaveDefaults()
{
    string file = Path.Combine(TempDir(), "settings.json");
    UserSettings settings = new();
    settings.Save(file);   // threw ArgumentException before: the default window position was NaN
    UserSettings back = UserSettings.Load(file);
    Assert(back.WindowX is null && back.WindowY is null && back.WindowWidth == 980, "defaults lost");
    Assert(!File.Exists(file + ".tmp"), "temporary file left behind");
    settings.RememberWindow = true; settings.WindowX = 120; settings.WindowY = 80; settings.Save(file);
    back = UserSettings.Load(file);
    Assert(back.RememberWindow && back.WindowX == 120 && back.WindowY == 80, "position lost");
    File.WriteAllText(file, """{"WindowX":"NaN","Username":"ryan"}""");
    Assert(UserSettings.Load(file).Username == "ryan", "an old file with NaN didn't load");
    File.WriteAllText(file, "{ broken");
    Assert(UserSettings.Load(file).Username == "", "a broken file didn't fall back to the defaults");
    return Task.CompletedTask;
}

static Task ConfigWtfKeepsUtf8()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "WTF"));
    string file = Path.Combine(wow, "WTF", "Config.wtf");
    File.WriteAllText(file, "SET realmName \"Temucraft Açores\"\nSET accountName \"JOÃO\"\n", new System.Text.UTF8Encoding(false));
    ConfigWtf config = ConfigWtf.Load(wow);
    config.Set("maxFPS", "144");
    config.Save();
    byte[] bytes = File.ReadAllBytes(file);
    Assert(bytes[0] != 0xEF, "a byte order mark was written");
    string text = File.ReadAllText(file);
    Assert(text.Contains("Temucraft Açores") && text.Contains("JOÃO") && text.Contains("SET maxFPS \"144\""), text);
    return Task.CompletedTask;
}

static async Task DownloadEmptyFile()
{
    FakeServer server = new(new() { ["files/Data/empty.txt"] = [] });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    string target = Path.Combine(TempDir(), "Data", "empty.txt");
    await client.DownloadAsync(File1("Data/empty.txt", []), target);
    Assert(File.Exists(target) && new FileInfo(target).Length == 0, "empty file not installed");
}

static Task ManifestRejectsRootUrls()
{
    foreach (string bad in new[] { "/patches/a.MPQ", "//evil.example/a", "patches\\a.MPQ", "https://evil.example/a", "c:/a" })
    {
        ManifestFile file = File1("Data/a.MPQ", [1]);
        file.Url = bad;
        Throws<InvalidDataException>(() => file.Validate(), "accepted url " + bad);
    }
    ManifestFile good = File1("Data/a.MPQ", [1]);
    good.Url = "patches/My Patch #2.MPQ";
    good.Validate();
    return Task.CompletedTask;
}

static CatalogAddon JournalAddon() => new()
{
    Name = "Temucraft Journal", Repo = "Temucraft/mod-temucraft-journal", Branch = "master",
    Path = "client/AddOns", Folders = ["TemucraftJournal"],
};

static async Task AddonFromSubFolder()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Interface", "AddOns", ".Old.new"));   // a cut-off install
    FakeGitHub github = new()
    {
        Responses =
        {
            ["https://api.github.com/repos/Temucraft/mod-temucraft-journal/commits/master"] = "abc1234def"u8.ToArray(),
            ["https://api.github.com/repos/Temucraft/mod-temucraft-journal/git/trees/abc1234def?recursive=1"] = """
                {"truncated": false, "tree": [
                  {"path": "src/Journal.cpp", "type": "blob"},
                  {"path": "client/AddOns", "type": "tree"},
                  {"path": "client/AddOns/TemucraftJournal/TemucraftJournal.toc", "type": "blob"},
                  {"path": "client/AddOns/TemucraftJournal/Journal Frame.lua", "type": "blob"},
                  {"path": "client/AddOns/Other/Other.toc", "type": "blob"}
                ]}
                """u8.ToArray(),
            ["https://raw.githubusercontent.com/Temucraft/mod-temucraft-journal/abc1234def/client/AddOns/TemucraftJournal/TemucraftJournal.toc"] = "## Title: Temucraft Journal"u8.ToArray(),
            ["https://raw.githubusercontent.com/Temucraft/mod-temucraft-journal/abc1234def/client/AddOns/TemucraftJournal/Journal%20Frame.lua"] = "-- lua"u8.ToArray(),
        },
    };
    using HttpClient http = new(github);
    IReadOnlyList<string> installed = await AddonFolder.InstallAsync(http, wow, JournalAddon());
    string addon = Path.Combine(wow, "Interface", "AddOns", "TemucraftJournal");
    Assert(installed.SequenceEqual(["TemucraftJournal"]), "installed: " + string.Join(",", installed));
    Assert(File.ReadAllText(Path.Combine(addon, "Journal Frame.lua")) == "-- lua", "lua not installed");
    Assert(!Directory.Exists(Path.Combine(wow, "Interface", "AddOns", "Other")), "a folder not in the list was installed");
    Assert(!github.Requested.Any(u => u.Contains("codeload") || u.Contains("Journal.cpp")), "downloaded more than the addon");
    Assert(!Directory.Exists(Path.Combine(wow, "Interface", "AddOns", ".Old.new")), "the leftover staging folder stayed");
    Assert(AddonFolder.Installed(wow, null).All(a => !a.Folder.StartsWith('.')), "a hidden folder is listed as an addon");
}

static async Task AddonZipFallback()
{
    string wow = TempDir();
    using MemoryStream zipBytes = new();
    using (System.IO.Compression.ZipArchive zip = new(zipBytes, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        void Add(string name, string text)
        {
            using StreamWriter w = new(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("mod-temucraft-journal-master/README.md", "readme");
        Add("mod-temucraft-journal-master/client/AddOns/TemucraftJournal/TemucraftJournal.toc", "## Title: J");
        Add("mod-temucraft-journal-master/client/AddOns/TemucraftJournal/Frames/Main.xml", "<Ui/>");
    }
    FakeGitHub github = new()
    {
        Responses = { ["https://codeload.github.com/Temucraft/mod-temucraft-journal/zip/refs/heads/master"] = zipBytes.ToArray() },
    };   // the API calls get 403 (rate limit)
    using HttpClient http = new(github);
    await AddonFolder.InstallAsync(http, wow, JournalAddon());
    Assert(File.ReadAllText(Path.Combine(wow, "Interface", "AddOns", "TemucraftJournal", "Frames", "Main.xml")) == "<Ui/>", "not installed from the zip");
}

static Task MarkdownPlainText()
{
    string text = Markdown.ToPlainText("""
        # Temucraft Journal
        [![build](https://x/badge.svg)](https://x)
        A **dungeon** journal. See [the wiki](https://example.org/wiki) and `/journal`.
        - Bosses
        - <b>Loot</b>
        ```
        code stays
        ```
        """);
    Assert(text.StartsWith("TEMUCRAFT JOURNAL"), text);
    Assert(text.Contains("A dungeon journal. See the wiki (https://example.org/wiki) and /journal."), text);
    Assert(text.Contains("• Bosses") && text.Contains("• Loot") && text.Contains("    code stays"), text);
    Assert(!text.Contains("badge") && !text.Contains("**") && !text.Contains("```"), text);
    return Task.CompletedTask;
}

static Task LoggerWritesAndRotates()
{
    string file = Path.Combine(TempDir(), "launcher.log");
    List<LogEntry> shown = [];
    void Show(LogEntry e) => shown.Add(e);
    Logger.Written += Show;
    long oldMax = Logger.MaxBytes;
    try
    {
        Logger.Start(file);
        Logger.Info("hello");
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { Logger.Error("Update", e); }
        string text = File.ReadAllText(file);
        Assert(text.Contains("[INFO ] hello") && text.Contains("[ERROR] Update: boom"), text);
        Assert(text.Contains("InvalidOperationException") && text.Contains("LoggerWritesAndRotates"), "no stack trace in the file");
        Assert(shown.Any(e => e.Level == LogLevel.Error && e.Message == "Update: boom" && !e.Message.Contains("   at ")), "window line wrong");
        Logger.MaxBytes = 200;
        for (int i = 0; i < 20; i++) Logger.Info(new string('x', 50));
        Assert(File.Exists(Path.ChangeExtension(file, ".1.log")), "no rotation");
        Assert(!File.Exists(Path.ChangeExtension(file, ".4.log")), "kept too many old logs");
        Assert(new FileInfo(file).Length < 400, "the log keeps growing");
    }
    finally
    {
        Logger.Written -= Show;
        Logger.MaxBytes = oldMax;
    }
    return Task.CompletedTask;
}

static Task CertPinParsing()
{
    string pin = string.Join(':', Enumerable.Range(0, 32).Select(i => (i * 7 % 256).ToString("x2")));
    ServerSettings s = ServerSettings.Parse(["SERVER_URL=https://203.0.113.5:8443/", "CERT_SHA256=" + pin]);
    Assert(s.CertificateSha256 == pin.Replace(":", "").ToUpperInvariant(), "fingerprint not normalised: " + s.CertificateSha256);
    Assert(ServerSettings.Parse(["SERVER_URL=https://203.0.113.5/"]).CertificateSha256 is null, "a pin out of nowhere");
    Throws<InvalidDataException>(() => ServerSettings.Parse(["SERVER_URL=https://x/", "CERT_SHA256=12:34"]), "a short fingerprint accepted");
    Throws<InvalidDataException>(() => ServerSettings.Parse(["SERVER_URL=https://x/", "CERT_SHA256=" + new string('z', 64)]), "non-hex accepted");
    return Task.CompletedTask;
}

static async Task SameFileDownloadedOnce()
{
    byte[] font = RandomNumberGenerator.GetBytes(200_000);
    FakeServer server = new(new() { ["files/Fonts/ARIALN.ttf"] = font, ["files/Fonts/FRIZQT__.ttf"] = font, ["files/Fonts/skurri.ttf"] = font });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    string wow = TempDir();
    List<PlannedFile> plan = new[] { "Fonts/ARIALN.ttf", "Fonts/FRIZQT__.ttf", "Fonts/skurri.ttf" }
        .Select(p => new PlannedFile(File1(p, font), Path.Combine(wow, p.Replace('/', Path.DirectorySeparatorChar)))).ToList();
    HashCache cache = HashCache.Load(Path.Combine(TempDir(), "cache.json"));
    UpdateInstaller.Result result = await UpdateInstaller.InstallAsync(client, plan, cache, wow);
    Assert(server.Requests.Count == 1, "downloads: " + string.Join(",", server.Requests));
    Assert(result.Downloaded == 1 && result.Copied == 2, $"{result}");
    Assert(plan.All(p => File.ReadAllBytes(p.Target).SequenceEqual(font)), "a copy is wrong");
    Assert(plan.All(p => !File.Exists(p.Target + ".part")), "a .part left behind");
}

static async Task LocaleFileCopiedFromOtherLocale()
{
    byte[] data = RandomNumberGenerator.GetBytes(150_000);
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Data", "enUS"));
    File.WriteAllBytes(Path.Combine(wow, "Data", "enUS", "lichking-locale-enUS.MPQ"), data);   // from the base client
    FakeServer server = new(new() { ["files/Data/deDE/lichking-locale-deDE.MPQ"] = data });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    ManifestFile file = File1("Data/deDE/lichking-locale-deDE.MPQ", data, FileCategory.Locale, "deDE");
    string target = Path.Combine(wow, "Data", "deDE", "lichking-locale-deDE.MPQ");
    UpdateInstaller.Result result = await UpdateInstaller.InstallAsync(client, [new PlannedFile(file, target)],
        HashCache.Load(Path.Combine(TempDir(), "cache.json")), wow);
    Assert(server.Requests.Count == 0 && result.Copied == 1, "downloaded instead of copied: " + string.Join(",", server.Requests));
    Assert(File.ReadAllBytes(target).SequenceEqual(data), "copy wrong");

    // a different file of the same name is not taken
    byte[] other = RandomNumberGenerator.GetBytes(150_000);
    FakeServer server2 = new(new() { ["files/Data/frFR/lichking-locale-frFR.MPQ"] = other });
    using ServerClient client2 = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server2);
    ManifestFile fr = File1("Data/frFR/lichking-locale-frFR.MPQ", other, FileCategory.Locale, "frFR");
    string frTarget = Path.Combine(wow, "Data", "frFR", "lichking-locale-frFR.MPQ");
    await UpdateInstaller.InstallAsync(client2, [new PlannedFile(fr, frTarget)], HashCache.Load(Path.Combine(TempDir(), "c2.json")), wow);
    Assert(server2.Requests.Count == 1 && File.ReadAllBytes(frTarget).SequenceEqual(other), "a different file was copied");
}

static async Task ChangedSourceIsDownloaded()
{
    byte[] data = RandomNumberGenerator.GetBytes(100_000);
    string wow = TempDir(), cacheFile = Path.Combine(TempDir(), "cache.json");
    string source = Path.Combine(wow, "a.bin");
    File.WriteAllBytes(source, data);
    HashCache cache = HashCache.Load(cacheFile);
    cache.Remember(source, Sha(data));
    byte[] changed = data.ToArray(); changed[0] ^= 1;
    File.WriteAllBytes(source, changed);
    File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));
    FakeServer server = new(new() { ["files/b.bin"] = data });
    using ServerClient client = new(ServerSettings.Parse(["SERVER_URL=https://f.example/"]), "u", "p", server);
    string target = Path.Combine(wow, "b.bin");
    UpdateInstaller.Result result = await UpdateInstaller.InstallAsync(client, [new PlannedFile(File1("b.bin", data), target)], cache, wow);
    Assert(result.Downloaded == 1 && result.Copied == 0, $"{result}");
    Assert(File.ReadAllBytes(target).SequenceEqual(data), "wrong content installed");
}

static Task PolicyEnforces()
{
    Manifest m = Manifest.Parse("""
        {"files": [], "policy": {"installAddons": {"value": true, "enforce": true}, "clearCache": {"value": false, "enforce": false}},
         "optionGroups": [{"name": "DXVK", "options": ["On", "Off"], "enforce": true},
                          {"name": "Night sky", "options": ["On", "Off"]}]}
        """);
    Assert(m.Enforced("installAddons") == true && m.Enforced("INSTALLADDONS") == true, "enforced setting not read");
    Assert(m.Enforced("clearCache") is null && m.Enforced("rememberWindow") is null, "a setting the server doesn't enforce was enforced");
    Dictionary<string, string> picks = new() { ["DXVK"] = "Off", ["Night sky"] = "Off" };
    Assert(UpdatePlanner.ChosenOption(m, picks, "DXVK") == "On", "the player turned off an enforced option");
    Assert(UpdatePlanner.ChosenOption(m, picks, "Night sky") == "Off", "the player's own pick was ignored");
    return Task.CompletedTask;
}

/// <summary>A tiny https server on 127.0.0.1 with a made-up self-signed certificate, answering every request with "ok".</summary>
static async Task CertPinOverTls()
{
    using System.Security.Cryptography.ECDsa key = System.Security.Cryptography.ECDsa.Create();
    System.Security.Cryptography.X509Certificates.CertificateRequest req = new("CN=launcher-test", key, HashAlgorithmName.SHA256);
    using System.Security.Cryptography.X509Certificates.X509Certificate2 made = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
    using System.Security.Cryptography.X509Certificates.X509Certificate2 cert =
        System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(made.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
    System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using CancellationTokenSource stop = new();
    Task server = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
            _ = Task.Run(async () =>
            {
                using (tcp)
                using (System.Net.Security.SslStream ssl = new(tcp.GetStream()))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(cert);
                        byte[] buffer = new byte[4096];
                        await ssl.ReadAsync(buffer);
                        await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                    }
                    catch (Exception) { }   // the refused handshakes end here
                }
            });
        }
    });
    try
    {
        string url = $"SERVER_URL=https://127.0.0.1:{port}/";
        string right = CertificatePin.Fingerprint(cert);
        using (ServerClient pinned = new(ServerSettings.Parse([url, "CERT_SHA256=" + right]), "u", "p"))
            Assert(await pinned.GetTextAsync("news.json") == "ok", "the pinned certificate wasn't trusted");

        string wrong = new string('A', 64);
        using (ServerClient other = new(ServerSettings.Parse([url, "CERT_SHA256=" + wrong]), "u", "p"))
        {
            string? message = null;
            try { await other.GetTextAsync("news.json"); } catch (HttpRequestException e) { message = e.Message; }
            Assert(message?.Contains("doesn't match CERT_SHA256") == true, "a different certificate wasn't refused: " + message);
        }
        using (ServerClient unpinned = new(ServerSettings.Parse([url]), "u", "p"))
        {
            string? message = null;
            try { await unpinned.GetTextAsync("news.json"); } catch (HttpRequestException e) { message = e.Message; }
            Assert(message?.Contains("isn't trusted") == true, "a self-signed certificate was trusted without a pin: " + message);
        }
    }
    finally
    {
        stop.Cancel();
        listener.Stop();
        await server;
    }
}

/// <summary>Answers like Apache: files under the folder, Range requests with 206.</summary>
sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = files;
    public HttpStatusCode? ForceStatus { get; init; }
    public string? LastAuth { get; private set; }
    public long? LastRangeFrom { get; private set; }
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        LastAuth = request.Headers.Authorization?.ToString();
        if (ForceStatus is { } status)
            return Task.FromResult(new HttpResponseMessage(status));
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        lock (Requests) Requests.Add(path);
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

/// <summary>Answers like GitHub for the URLs it knows; anything else gets 403 (like the API's hourly limit).</summary>
sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, byte[]> Responses { get; } = [];
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string url = request.RequestUri!.AbsoluteUri;
        Requested.Add(url);
        return Task.FromResult(Responses.TryGetValue(url, out byte[]? body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
