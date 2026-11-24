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
    ("A wrong password and a blocked address give the same message", LoginErrors),
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
    ("The policy reads every kind of setting and tweak", PolicyReadsEverything),
    ("Only what the server lists is read and sent, one header each", ClientInfoSent),
    ("Remember me works through the Linux keyring (secret-tool)", LinuxKeyring),
    ("Only real MACs and useful local addresses are reported", ClientInfoFilters),
    ("News dates read as 18th of November 2026 (18/11/2026), numbered per day", NewsDates),
    ("Website links and the header switches are read from launcher.env", NavAndHeader),
    ("Realm status: address from NAV or the policy, counts read, player map link", RealmStatusBlock),
    ("DXVK settings go to dxvk.conf, the rest to Config.wtf", DxvkTweaks),
    ("Text files are read and the launcher's words translated", Translating),
    ("Themes mix a mode and a theme; the server can enforce them", ThemeMixing),
    ("Settings copy to text and import back", SettingsCopy),
    ("Speeds, sizes, counts, update hours and the download cap", SpeedsAndSizes),
    ("Settings, key bindings and macros are shared between accounts", SharingSettings),
    ("Shared settings write the game's cache.md5, so the game keeps them", SharingKeepsCacheMd5),
    ("Changelog tags are found and coloured", NewsTags),
    ("The server sets how News and Changelog show; the last ones are kept", FeedRules),
    ("Needed addons are turned back on in every AddOns.txt", RequiredAddonsEnabled),
    ("Diagnostics mark files against the server's copy", DiagnosticsMarks),
    ("Diagnostics read the crash helper's report", DiagnosticsCrashHelper),
    ("A new launcher is required unless the server says recommended", LauncherUpdateRule),
    ("Login screen scenes are read and turned on or off", LoginScenes),
    ("Self cast, focus cast and auto loot keys go to bindings-cache.wtf", ModifiedClicks),
    ("Key bindings are the game's defaults with the file's changes over them", DefaultKeyBindings),
    ("Macro files read and write back the same; new ids and limits", MacroFileRoundTrip),
    ("Per character settings go to the character's config-cache.wtf", PerCharacterOptions),
    ("The old launcher a self update leaves behind is removed at the next start", SelfUpdateLeftovers),
    ("testing.env copies test files in and takes them out again", TestingFilesInAndOut),
    ("Character screen scenes: Classic / HD defaults and the player's picks in TemucraftScenes.lua", CharacterScenesFile),
    ("HD character models: patch-K and patch-_ built per race from the pieces", HdModelsBuild),
];

static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

static Task HdModelsBuild()
{
    string wow = Path.Combine(Path.GetTempPath(), "tc-hd-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(wow, "Data", "TemucraftHD"));
        File.WriteAllText(Path.Combine(wow, "Wow.exe"), "x");
        // a 21-column table: the race in columns 0..2 (every table's race column is one of those), a string in 4, 5, 6 and 20
        static byte[] Table(string text, params int[] races)
        {
            using MemoryStream m = new();
            byte[] strings = [0, .. System.Text.Encoding.ASCII.GetBytes(text), 0];
            m.Write("WDBC"u8); m.Write(BitConverter.GetBytes(races.Length)); m.Write(BitConverter.GetBytes(21)); m.Write(BitConverter.GetBytes(84));
            m.Write(BitConverter.GetBytes(strings.Length));
            foreach (int race in races)
                for (int c = 0; c < 21; c++) m.Write(BitConverter.GetBytes(c <= 2 ? race : c is 4 or 5 or 6 or 20 ? 1 : 0));
            m.Write(strings);
            return m.ToArray();
        }
        using (MpqWriter common = new(HdModels.PiecePath(wow, "common"), 3))
        {
            foreach (string name in new[] { "CharSections", "CharHairGeosets", "CharacterFacialHairStyles", "CreatureDisplayInfoExtra", "EmotesTextSound", "HelmetGeosetVisData" })
            {
                common.AddPlain($"Temucraft\\Classic\\{name}.dbc", Table("classic", 1, 2, 50));
                common.AddPlain($"Temucraft\\HD\\{name}.dbc", Table("hd", 1, 2, 50));
            }
            common.AddPlain("Textures\\GuildEmblems\\e.blp", [1, 2, 3]);
        }
        using (MpqWriter human = new(HdModels.PiecePath(wow, "human"), 3)) human.AddPlain("Character\\Human\\a.blp", [4, 5]);
        using (MpqWriter orc = new(HdModels.PiecePath(wow, "orc"), 3)) orc.AddPlain("Character\\Orc\\b.blp", [6]);

        Check(HdModels.Sync(wow, ["human"]) is not null, "built");
        using (MpqArchive k = MpqArchive.Open(Path.Combine(wow, "Data", "patch-K.MPQ")))
        {
            Check(k.Names.Contains("Character\\Human\\a.blp") && k.Names.Contains("Textures\\GuildEmblems\\e.blp"), "human and common in patch-K");
            Check(!k.Names.Contains("Character\\Orc\\b.blp") && !k.Names.Any(n => n.StartsWith("Temucraft")), "no Classic race, no source tables");
            Check(k.ReadPlain("Character\\Human\\a.blp").SequenceEqual(new byte[] { 4, 5 }), "file data copied unchanged");
        }
        using (MpqArchive data = MpqArchive.Open(Path.Combine(wow, "Data", "patch-_.MPQ")))
        {
            byte[] sections = data.ReadPlain("DBFilesClient\\CharSections.dbc");
            string text = System.Text.Encoding.ASCII.GetString(sections);
            Check(BitConverter.ToInt32(sections, 4) == 3, "one row per race");
            Check(text.Contains("hd") && text.Contains("classic"), "HD rows for the HD race, Classic for the rest: " + text);
            Check(data.ReadPlain("DBFilesClient\\HelmetGeosetVisData.dbc").Length > 20, "whole table taken");
        }
        Check(HdModels.Sync(wow, ["human"]) is null, "unchanged: not built again");
        Check(HdModels.Sync(wow, []) is not null && !File.Exists(Path.Combine(wow, "Data", "patch-K.MPQ"))
              && !File.Exists(Path.Combine(wow, "Data", "patch-_.MPQ")), "all Classic: both removed");
    }
    finally { try { Directory.Delete(wow, true); } catch (IOException) { } }
    return Task.CompletedTask;
}

static Task CharacterScenesFile()
{
    Check(CharacterScenes.DefaultScene("HUMAN", "old") == "Huma2" && CharacterScenes.DefaultScene("HUMAN", "new") == "KulTiran" && CharacterScenes.DefaultScene("KULTIRAN", "new") == "Human", "human defaults (swapped folders)");
    Check(CharacterScenes.DefaultScene("GNOME", "old") == "Dwar2" && CharacterScenes.DefaultScene("TROLL", "old") == "Or2", "shared originals");
    Check(CharacterScenes.DefaultScene("ORC", "faction") == "Horde" && CharacterScenes.DefaultScene("DRAENEI", "faction") == "Alliance", "faction scenes");
    string text = CharacterScenes.Text("new", new Dictionary<string, string> { ["draenei"] = "Human", ["GNOME"] = "Or2", ["BAD\\"] = "x", ["ORC"] = "a\"b" });
    Check(text.Contains("TEMUCRAFT_CHARACTER_SCENES = \"new\";") && text.Contains("[\"DRAENEI\"] = \"Human\",") && text.Contains("[\"GNOME\"] = \"Or2\","), "picks written: " + text);
    Check(!text.Contains("BAD") && !text.Contains("ORC"), "unsafe names left out");
    Check(CharacterScenes.Text("faction", new Dictionary<string, string>()).Contains("\"faction\""), "faction mode written");
    Check(CharacterScenes.ModeOf("HD") == "new" && CharacterScenes.ModeOf("Faction") == "faction" && CharacterScenes.ModeOf("Classic") == "old", "option to mode");
    string wow = Path.Combine(Path.GetTempPath(), "tc-scenes-" + Guid.NewGuid().ToString("N"));
    try
    {
        Check(CharacterScenes.Write(wow, "old", new Dictionary<string, string>()), "first write");
        Check(!CharacterScenes.Write(wow, "old", new Dictionary<string, string>()), "unchanged: not written again");
        Check(File.ReadAllText(Path.Combine(wow, "Interface", "TemucraftScenes.lua")).Contains("\"old\""), "Classic = old");
    }
    finally { try { Directory.Delete(wow, true); } catch (IOException) { } }
    return Task.CompletedTask;
}

static Task TestingFilesInAndOut()
{
    string root = Path.Combine(Path.GetTempPath(), "temucraft-testing-" + Guid.NewGuid().ToString("N"));
    string launcher = Path.Combine(root, "launcher"), wow = Path.Combine(root, "wow");
    Directory.CreateDirectory(launcher); Directory.CreateDirectory(wow);
    try
    {
        string mpq = Path.Combine(root, "patch-enUS-Z.MPQ");
        File.WriteAllText(mpq, "test");
        File.WriteAllLines(Path.Combine(launcher, TestingFiles.FileName), [
            "# comment",
            $"{mpq} = Data\\{{locale}}\\patch-{{locale}}-Z.MPQ",
            "missing.mpq = Data/x.MPQ",
            $"{mpq} = ../outside.MPQ" ]);
        List<string> log = TestingFiles.Apply(launcher, wow, ["enUS", "deDE"]);
        Check(File.Exists(Path.Combine(wow, "Data", "enUS", "patch-enUS-Z.MPQ")), "enUS copy");
        Check(File.Exists(Path.Combine(wow, "Data", "deDE", "patch-deDE-Z.MPQ")), "deDE copy");
        Check(log.Count(l => l.Contains("does not exist")) == 1, "missing source reported");
        Check(log.Count(l => l.Contains("not a path inside")) == 1, "outside path refused");
        Check(!TestingFiles.Apply(launcher, wow, ["enUS", "deDE"]).Any(l => l.Contains("copied")), "unchanged files are not copied again");
        File.Delete(Path.Combine(launcher, TestingFiles.FileName));
        TestingFiles.Apply(launcher, wow, ["enUS", "deDE"]);
        Check(!File.Exists(Path.Combine(wow, "Data", "enUS", "patch-enUS-Z.MPQ")), "removed when testing.env is gone");
        Check(!File.Exists(Path.Combine(wow, "Data", "deDE", "patch-deDE-Z.MPQ")), "removed (deDE)");
    }
    finally { Directory.Delete(root, recursive: true); }
    return Task.CompletedTask;
}
static string HexOf(char c) => new(c, 64);

static async Task RealmStatusBlock()
{
    IReadOnlyList<NavLink> nav = ServerSettings.ParseNav("Register|https://reg.example.com:4000/signup; Database|https://db.example.com/aowow/index.php?home");
    Check(RealmStatus.Website(nav)?.ToString() == "https://db.example.com/aowow/", "website from the Database link: " + RealmStatus.Website(nav));
    Check(RealmStatus.Website(ServerSettings.ParseNav("Register|https://reg.example.com:4000/"))?.ToString() == "https://reg.example.com:4000/", "first link without a site name");
    Check(RealmStatus.Website([]) is null, "no links, no website");

    LauncherPolicy none = LauncherPolicy.From(null);
    Check(RealmStatus.StatusUrl(none, nav)?.ToString() == "https://db.example.com/aowow/?realm-status=data", "default address: " + RealmStatus.StatusUrl(none, nav));
    LauncherPolicy set = LauncherPolicy.From(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
        """{"realmStatusUrl":{"value":"https://site.example.com/?realm-status=data"},"playerMapUrl":"https://site.example.com/map","reportBugUrl":{"value":"https://bugs.example.com/"}}"""));
    Check(set.Unknown.Count == 0 && set.Setting("reportBugUrl") is not null, "link settings are known: " + string.Join(",", set.Unknown));
    Check(RealmStatus.StatusUrl(set, nav)?.ToString() == "https://site.example.com/?realm-status=data", "policy address");
    LauncherPolicy off = LauncherPolicy.From(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("""{"realmStatusUrl":{"value":false}}"""));
    Check(RealmStatus.StatusUrl(off, nav) is null, "false turns the block off");

    string json = """
    {"generated":1,"cacheSeconds":30,"realms":[{"id":1,"name":"Temucraft","online":true,"players":12,"alliance":5,"horde":7,"peak":57,"peakTime":1790000000,"startedAt":1,"uptime":93720,"classes":{"1":3},"races":{},"levels":[0,1,2,0,0,0,0,0,9]},
     {"id":2,"name":"PTR","online":false,"players":0,"alliance":0,"horde":0,"peak":3,"peakTime":0,"startedAt":0,"uptime":0,"classes":{},"races":{},"levels":[]}],
     "names":{"classes":{"1":"Warrior"},"races":{}},"mapEnabled":true,"mapUrl":"https://db.example.com/aowow/?player-map"}
    """;
    FakeServer server = new(new Dictionary<string, byte[]> { ["aowow/"] = System.Text.Encoding.UTF8.GetBytes(json) });
    RealmStatus status = await RealmStatus.FetchAsync(new Uri("https://db.example.com/aowow/?realm-status=data"), null, server);
    Check(status.Realms.Count == 2 && status.Realms[0] is { Name: "Temucraft", Online: true, Players: 12, Peak: 57, Alliance: 5, Horde: 7, Uptime: 93720 }
          && !status.Realms[1].Online, string.Join(" | ", status.Realms));
    Check(RealmStatus.Duration(93720) == "1d 2h" && RealmStatus.Duration(3720) == "1h 2m" && RealmStatus.Duration(0) == "-", "durations");
    Uri statusUrl = new("https://db.example.com/aowow/?realm-status=data");
    Check(RealmStatus.PlayerMapUrl(none, nav, status, statusUrl)?.ToString() == "https://db.example.com/aowow/?player-map", "map link from the website");
    Check(RealmStatus.PlayerMapUrl(set, nav, status, statusUrl)?.ToString() == "https://site.example.com/map", "map link from the policy");
    Check(RealmStatus.PlayerMapUrl(none, nav, RealmStatus.Parse("""{"realms":[],"mapEnabled":false}"""), statusUrl) is null, "map turned off on the website");
    Check(RealmStatus.PlayerMapUrl(none, nav, RealmStatus.Parse("""{"realms":[]}"""), statusUrl)?.ToString() == "https://db.example.com/aowow/?player-map", "map link built");
}

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
    // a wrong password and an address that isn't allowed must look the same
    foreach ((HttpStatusCode code, string expected) in new[] { (HttpStatusCode.Unauthorized, ServerClient.RefusedMessage),
                                                              (HttpStatusCode.Forbidden, ServerClient.RefusedMessage) })
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

static Task PolicyReadsEverything()
{
    Manifest m = Manifest.Parse("""
        {"files": [], "policy": {
          "backupsToKeep": {"value": 3, "enforce": false},
          "largeAddressAware": {"value": "on", "enforce": true},
          "requiredLocales": {"value": ["enUS", "deDE"]},
          "clearCache": false,
          "somethingNew": {"value": 1, "enforce": true},
          "tweaks": {
            "farclip": {"value": 1277, "enforce": true},
            "maxFPS": {"value": "144", "enforce": false},
            "gxWindow": {"value": true, "enforce": true},
            "cameraDistanceMaxFactor": {"value": 2.5, "enforce": true},
            "SET weird": {"enforce": true}
          }}}
        """);
    LauncherPolicy p = m.Rules;
    Assert(p.Setting("backupsToKeep") is { Int: 3, Enforce: false }, "backupsToKeep");
    Assert(p.EnforcedBool("largeAddressAware") == true, "a string \"on\" isn't true");
    Assert(p.RequiredLocales.SequenceEqual(["enUS", "deDE"]), "required locales: " + string.Join(",", p.RequiredLocales));
    Assert(LauncherPolicy.From(null).DefaultLocale == "enUS" && LauncherPolicy.From(null).RequiredLocales.Count == 0, "defaults");
    Assert(Manifest.Parse("""{"files": [], "policy": {"defaultLocale": {"value": "deDE"}}}""").Rules.DefaultLocale == "deDE", "defaultLocale");

    string game = TempDir();
    Directory.CreateDirectory(Path.Combine(game, "Data", "enUS")); Directory.CreateDirectory(Path.Combine(game, "Data", "frFR"));
    Directory.CreateDirectory(Path.Combine(game, "WTF"));
    File.WriteAllText(Path.Combine(game, "Wow.exe"), "MZ");
    Assert(GameFolder.ClientLocale(game) == "enUS", "first language folder");
    File.WriteAllText(Path.Combine(game, "WTF", "Config.wtf"), "SET locale \"frFR\"\n");
    Assert(GameFolder.ClientLocale(game) == "frFR", "Config.wtf's locale");
    Assert(GameFolder.ClientLocale(TempDir()) is null, "a language for an empty folder");
    Assert(GameFolder.LocaleName("deDE") == "German · Deutsch" && GameFolder.LocaleName("enUS") == "English (United States)", "locale names");
    Assert(GameFolder.LocaleName("deDE", t => t == "German" ? "Alemão" : t) == "Alemão · Deutsch" && GameFolder.LocaleName("xxYY") == "xxYY", "translated / unknown");
    Assert(p.Setting("clearCache") is { Enforce: false, Bool: false }, "a bare value isn't a recommendation");
    Assert(p.Unknown.Contains("somethingNew") && p.Unknown.Contains("tweaks.SET weird"), "unknown names: " + string.Join(",", p.Unknown));
    Assert(p.Tweaks["FARCLIP"].CVarText == "1277" && p.Tweaks["gxWindow"].CVarText == "1" && p.Tweaks["cameraDistanceMaxFactor"].CVarText == "2.5",
        "tweak values not written the Config.wtf way");

    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "WTF"));
    File.WriteAllText(Path.Combine(wow, "WTF", "Config.wtf"), "SET farclip \"777\"\nSET maxFPS \"60\"\n");
    ConfigWtf config = ConfigWtf.Load(wow);
    Assert(p.ApplyEnforcedTweaks(config) == 3, "enforced tweaks not all written");
    Assert(config.Get("farclip") == "1277" && config.Get("gxWindow") == "1" && config.Get("cameraDistanceMaxFactor") == "2.5", "enforced values");
    Assert(config.Get("maxFPS") == "60", "a recommended (not enforced) tweak overwrote the player's value");
    Assert(p.ApplyEnforcedTweaks(config) == 0, "writing again changed something");
    return Task.CompletedTask;
}

static async Task ClientInfoSent()
{
    ServerSettings settings = ServerSettings.Parse(["SERVER_URL=https://f.example/", "COLLECT=osUser, mac,installId, nonsense",
                                                    @"COLLECT_NOTE=Line one\nLine two"]);
    Assert(settings.Collect.SequenceEqual(["launcherVersion", "os", "installId", "osUser", "mac"]), "collect list: " + string.Join(",", settings.Collect));
    Assert(settings.CollectNote == "Line one\nLine two", "note: " + settings.CollectNote);
    Assert(ServerSettings.Parse(["SERVER_URL=https://f.example/", "COLLECT=all"]).Collect.Count == ClientInfo.Items.Count, "all");
    Assert(ServerSettings.Parse(["SERVER_URL=https://f.example/"]).Collect.SequenceEqual(ClientInfo.Defaults), "no list must mean the defaults");
    Assert(ServerSettings.Parse(["SERVER_URL=https://f.example/", "COLLECT=none"]).Collect.Count == 0, "none must mean nothing at all");

    Dictionary<string, string> values = ClientInfo.Read(settings.Collect, "id-123");
    Assert(values.Keys.All(k => settings.Collect.Contains(k)), "read something not listed: " + string.Join(",", values.Keys));
    Assert(values["installId"] == "id-123" && values["osUser"] == Environment.UserName, "values");

    FakeServer server = new(new() { ["news.json"] = "[]"u8.ToArray() });
    using (ServerClient client = new(settings, "u", "p", server, values))
        await client.GetTextAsync("news.json");
    Assert(server.LastHeaders["X-Temucraft-installId"] == "id-123", "installId header missing");
    Assert(Uri.UnescapeDataString(server.LastHeaders["X-Temucraft-osUser"]) == Environment.UserName, "osUser header wrong");
    Assert(!server.LastHeaders.ContainsKey("X-Temucraft-computerName"), "sent an item that wasn't listed");

    FakeServer quiet = new(new() { ["news.json"] = "[]"u8.ToArray() });
    using (ServerClient client = new(settings, "u", "p", quiet, clientInfo: null))
        await client.GetTextAsync("news.json");
    Assert(!quiet.LastHeaders.Keys.Any(k => k.StartsWith(ClientInfo.HeaderPrefix)), "sent something without agreement");
}

static Task LinuxKeyring()
{
    if (!OperatingSystem.IsLinux())
    {
        Console.WriteLine("        (skipped: Linux only)");
        return Task.CompletedTask;
    }
    // a stand-in for secret-tool that keeps the secret in a file, called the same way the real one is
    string dir = TempDir(), store = Path.Combine(dir, "secret"), tool = Path.Combine(dir, "secret-tool");
    File.WriteAllText(tool, $"#!/bin/sh\ncase \"$1\" in\n  store) cat > '{store}' ;;\n  lookup) [ -f '{store}' ] && cat '{store}' || exit 1 ;;\n  clear) rm -f '{store}' ;;\nesac\n");
    File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    Environment.SetEnvironmentVariable("LAUNCHER_SECRET_TOOL", tool);
    try
    {
        Assert(CredentialStore.IsSupported, "not supported with secret-tool present");
        CredentialStore.Save("ryan", "pa ss\"wörd");
        Assert(CredentialStore.Load() == "pa ss\"wörd", "loaded: " + CredentialStore.Load());
        Assert(!File.ReadAllText(tool).Contains("wörd"), "the password went on the command line");
        CredentialStore.Delete();
        Assert(CredentialStore.Load() is null, "still there after Delete");
    }
    finally
    {
        Environment.SetEnvironmentVariable("LAUNCHER_SECRET_TOOL", null);
    }
    return Task.CompletedTask;
}

static Task ClientInfoFilters()
{
    Assert(ClientInfo.FormatMac([0x02, 0x50, 0x55, 0x6F, 0x87, 0x0E]) == "02:50:55:6F:87:0E", "MAC format");
    Assert(ClientInfo.FormatMac([0, 0, 0, 0, 0, 0, 0, 0xE0]) is null, "a tunnel's 8-byte address was kept");
    Assert(ClientInfo.FormatMac(new byte[6]) is null, "an empty MAC was kept");
    foreach (string filter in new[] { "Radmin VPN-WFP Native MAC Layer LightWeight Filter-0000", "Ethernet-Npcap Packet Driver (NPCAP)-0000", "Ethernet-QoS Packet Scheduler-0000", "Ethernet-WFP 802.3 MAC Layer LightWeight Filter-0000" })
        Assert(ClientInfo.IsFilterDriver(filter), "kept the filter driver " + filter);
    foreach (string real in new[] { "Ethernet", "Wi-Fi", "Radmin VPN", "eth0", "en0" })
        Assert(!ClientInfo.IsFilterDriver(real), "dropped " + real);
    Assert(ClientInfo.Group([("Ethernet", ["192.168.1.150", "2001:8a0::1"]), ("Teredo", []), ("Radmin VPN", ["26.157.96.187"])])
           == "Ethernet: 192.168.1.150, 2001:8a0::1 | Radmin VPN: 26.157.96.187", "adapter grouping");
    foreach (string keep in new[] { "192.168.1.150", "26.157.96.187", "2001:8a0:6396:d400:4853:f01a:c9d:3439", "fdfd::1a9d:60bb" })
        Assert(ClientInfo.IsUsefulLocal(IPAddress.Parse(keep)), "dropped " + keep);
    foreach (string drop in new[] { "fe80::8b63:3ebc:dc5b:d70c%17", "169.254.83.107", "2001:0:4625:9800:3c0b:950:ae3e:d1ee", "127.0.0.1", "::1" })
        Assert(!ClientInfo.IsUsefulLocal(IPAddress.Parse(drop)), "kept " + drop);
    return Task.CompletedTask;
}

static Task NewsDates()
{
    Assert(NewsItem.FormatDate("2026-11-18") == "18th of November 2026 (18/11/2026)", NewsItem.FormatDate("2026-11-18"));
    foreach ((string date, string start) in new[] { ("2026-11-01", "1st"), ("2026-11-02", "2nd"), ("2026-11-03", "3rd"), ("2026-11-11", "11th"),
                                                    ("2026-11-12", "12th"), ("2026-11-13", "13th"), ("2026-11-21", "21st"), ("2026-11-22", "22nd"), ("2026-12-31", "31st") })
        Assert(NewsItem.FormatDate(date).StartsWith(start + " of "), NewsItem.FormatDate(date));
    Assert(NewsItem.FormatDate("soon") == "soon", "a free-text date must stay as written");
    var items = NewsItem.Parse("""[{"date": "2026-11-19", "title": "c"}, {"date": "2026-11-19", "title": "b"}, {"date": "2026-11-19", "title": "a"}, {"date": "2026-11-18", "title": "z"}]""");
    var lines = NewsItem.DateLines(items);
    Assert(lines[0] == "19th of November 2026 (19/11/2026)" && lines[1] == "" && lines[2] == "", "the date once per day: " + lines[0] + " / " + lines[1]);
    Assert(lines[3] == "18th of November 2026 (18/11/2026)", "next day: " + lines[3]);
    var numbers = NewsItem.DayNumbers(items);
    Assert(numbers[0] == (1, 3) && numbers[2] == (3, 3) && numbers[3] == (0, 0), "numbers from the top; a day's only entry has none");
    return Task.CompletedTask;
}

static Task Translating()
{
    Dictionary<string, string> y = MiniYaml.Parse("# comment\n_name: \"Português\"\n\"Save\": \"Guardar\"\nPlain key: plain value # note\n\"Say \\\"hi\\\"\\n\": \"Diz \\\"olá\\\"\"\n\"Downloading {0}  {1} / {2} MB\": \"A transferir {0}  {1} / {2} MB\"\n\"Downloading {0}\": \"A transferir {0}\"\n\"{0}: {1}. Press it now\": \"{1} em {0}. Carrega agora\"");
    Assert(y["_name"] == "Português" && y["Save"] == "Guardar" && y["Plain key"] == "plain value", "parse: " + string.Join("|", y.Keys));
    Assert(y["Say \"hi\"\n"] == "Diz \"olá\"", "escapes");
    Translation.Language pt = Translation.FromYaml("ptPT", string.Join("\n", y.Select(kv => MiniYaml.Quote(kv.Key) + ": " + MiniYaml.Quote(kv.Value))));
    Assert(pt.Name == "Português" && !pt.Lines.ContainsKey("_name"), "name");
    Translation.Use(pt);
    try
    {
        Assert(Translation.T("Save") == "Guardar" && Translation.T("Unknown words") == "Unknown words", "exact");
        Assert(Translation.T("Downloading patch-A.MPQ") == "A transferir patch-A.MPQ", "template: " + Translation.T("Downloading patch-A.MPQ"));
        Assert(Translation.T("Downloading a.MPQ  1.0 / 2.0 MB") == "A transferir a.MPQ  1.0 / 2.0 MB", "longest template first");
        Assert(Translation.T("DXVK: on. Press it now") == "on em DXVK. Carrega agora", "reordered: " + Translation.T("DXVK: on. Press it now"));
    }
    finally { Translation.Use(null); }
    Assert(Translation.T("Save") == "Save", "back to English");
    System.Globalization.CultureInfo deAT = new("de-AT"), ptPT = new("pt-PT"), zhTW = new("zh-TW"), zhCN = new("zh-CN");
    string[] have = ["deDE", "frFR", "esES", "esMX", "zhCN", "zhTW"];
    Assert(Translation.Pick("", "auto", deAT, have) == "deDE", "auto from the computer (de-AT finds deDE)");
    Assert(Translation.Pick("", "", zhTW, have) == "zhTW" && Translation.Pick("", "", zhCN, have) == "zhCN", "Chinese");
    Assert(Translation.Pick("", "auto", new System.Globalization.CultureInfo("es-MX"), have) == "esMX", "es-MX");
    Assert(Translation.Pick("", "enUS", deAT, have) == "enUS", "launcher.env says English");
    Assert(Translation.Pick("frFR", "enUS", deAT, have) == "frFR", "the player's pick wins");
    Assert(Translation.Pick("", "de", ptPT, have) == "deDE", "short code");
    Assert(Translation.Pick("", "", ptPT, have) == "enUS", "a language the game doesn't have: English");
    return Task.CompletedTask;
}

static Task SharingSettings()
{
    string wow = Directory.CreateTempSubdirectory().FullName;
    string a = Path.Combine(wow, "WTF", "Account", "MAIN"), b = Path.Combine(wow, "WTF", "Account", "ALT");
    string ca = Path.Combine(a, "Realm", "Hero"), cb = Path.Combine(b, "Realm", "Alt");
    foreach (string d in new[] { ca, cb, Path.Combine(a, "SavedVariables"), Path.Combine(b, "SavedVariables"), Path.Combine(wow, "WTF", "Account", "SavedVariables") })
        Directory.CreateDirectory(d);
    void Write(string path, string text, int minutesAgo) { File.WriteAllText(path, text); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo)); }
    Write(Path.Combine(a, "config-cache.wtf"), "SET autoLootDefault \"1\"", 1);   // newest
    Write(Path.Combine(b, "config-cache.wtf"), "SET autoLootDefault \"0\"", 60);
    Write(Path.Combine(b, "macros-cache.txt"), "alt macros", 5);                  // only on ALT: goes to MAIN
    Write(Path.Combine(a, "SavedVariables", "Bagnon.lua"), "x = 1", 1);
    Write(Path.Combine(ca, "bindings-cache.wtf"), "hero binds", 1);
    Write(Path.Combine(cb, "bindings-cache.wtf"), "alt binds", 30);
    int backups = 0;
    var first = SharedSettings.Sync(wow, addonSettings: false, characters: false, () => backups++);
    Assert(File.ReadAllText(Path.Combine(b, "config-cache.wtf")).Contains("\"1\""), "the newest config wins");
    Assert(File.ReadAllText(Path.Combine(a, "macros-cache.txt")) == "alt macros", "a file only one account has reaches the others");
    Assert(!File.Exists(Path.Combine(b, "SavedVariables", "Bagnon.lua")) && File.ReadAllText(Path.Combine(cb, "bindings-cache.wtf")) == "alt binds", "addons and characters only when asked");
    Assert(first.Copied == 2 && backups == 1 && first.Accounts.SequenceEqual(["ALT", "MAIN"]), $"copied {first.Copied}, backups {backups}, accounts {string.Join(",", first.Accounts)}");
    var second = SharedSettings.Sync(wow, addonSettings: true, characters: true, () => backups++);
    Assert(File.ReadAllText(Path.Combine(b, "SavedVariables", "Bagnon.lua")) == "x = 1" && File.ReadAllText(Path.Combine(cb, "bindings-cache.wtf")) == "hero binds", "addons and characters");
    Assert(second.Copied == 2 && backups == 2, "only the new ones: " + string.Join(", ", second.Files));
    Assert(SharedSettings.Sync(wow, true, true, () => backups++).Copied == 0 && backups == 2, "nothing left to copy, no backup");
    return Task.CompletedTask;
}

static Task SharingKeepsCacheMd5()
{
    string wow = Directory.CreateTempSubdirectory().FullName;
    string a = Path.Combine(wow, "WTF", "Account", "MAIN"), b = Path.Combine(wow, "WTF", "Account", "ALT");
    string ca = Path.Combine(a, "Realm", "Hero"), cb = Path.Combine(b, "Realm", "Alt");
    foreach (string d in new[] { ca, cb }) Directory.CreateDirectory(d);
    byte[] Entry(byte[] content, uint time) => [.. MD5.HashData(content), .. BitConverter.GetBytes(time)];
    byte[] mainConfig = "SET autoLootDefault \"1\""u8.ToArray(), altConfig = "SET autoLootDefault \"0\""u8.ToArray();
    byte[] binds = "bind A STRAFELEFT"u8.ToArray();
    File.WriteAllBytes(Path.Combine(a, "config-cache.wtf"), mainConfig);
    File.WriteAllBytes(Path.Combine(b, "config-cache.wtf"), altConfig);
    File.SetLastWriteTimeUtc(Path.Combine(b, "config-cache.wtf"), DateTime.UtcNow.AddHours(-2));
    // both accounts already have the same bindings, but ALT's cache.md5 still has the old file's MD5 (an older launcher copied it)
    File.WriteAllBytes(Path.Combine(a, "bindings-cache.wtf"), binds);
    File.WriteAllBytes(Path.Combine(b, "bindings-cache.wtf"), binds);
    File.SetLastWriteTimeUtc(Path.Combine(b, "bindings-cache.wtf"), DateTime.UtcNow.AddHours(-1));
    // the game's cache.md5: config, bindings, macros (MD5 + save time)
    File.WriteAllBytes(Path.Combine(a, "cache.md5"), [.. Entry(mainConfig, 2000), .. Entry(binds, 1999), .. new byte[20]]);
    File.WriteAllBytes(Path.Combine(b, "cache.md5"), [.. Entry(altConfig, 1000), .. Entry("old"u8.ToArray(), 900), .. new byte[20]]);
    // a character file: the character's cache.md5 is made when missing (5 entries)
    File.WriteAllText(Path.Combine(ca, "bindings-cache.wtf"), "hero binds");
    File.WriteAllText(Path.Combine(cb, "bindings-cache.wtf"), "alt binds");
    File.SetLastWriteTimeUtc(Path.Combine(cb, "bindings-cache.wtf"), DateTime.UtcNow.AddHours(-2));

    SharedSettings.Sync(wow, addonSettings: false, characters: true);
    byte[] alt = File.ReadAllBytes(Path.Combine(b, "cache.md5"));
    Assert(alt.Length == 60 && alt.AsSpan(0, 16).SequenceEqual(MD5.HashData(mainConfig)), "the copied config's MD5 is in ALT's cache.md5");
    Assert(BitConverter.ToUInt32(alt, 16) == 2000, "with MAIN's save time: " + BitConverter.ToUInt32(alt, 16));
    Assert(alt.AsSpan(20, 16).SequenceEqual(MD5.HashData(binds)) && BitConverter.ToUInt32(alt, 36) >= 1999, "an equal file with a stale MD5 is put right");
    byte[] chr = File.ReadAllBytes(Path.Combine(cb, "cache.md5"));
    Assert(chr.Length == 100 && chr.AsSpan(20, 16).SequenceEqual(MD5.HashData("hero binds"u8.ToArray())), "a character's cache.md5 is made with the bindings entry");
    Assert(SharedSettings.Sync(wow, false, true).Copied == 0, "nothing left to do");
    return Task.CompletedTask;
}

static Task SpeedsAndSizes()
{
    Assert(Words.Files(1) == "1 file" && Words.Files(188) == "188 files" && Words.Files(0) == "0 files", "plurals");
    Assert(Words.Size(17029L * 1048576 + 314573) == "16.6 GB", "GB: " + Words.Size(17029L * 1048576 + 314573));
    Assert(Words.Size(5 * 1048576) == "5.0 MB" && Words.Size(2000) == "2 KB", "MB/KB: " + Words.Size(2000));
    Assert(Words.ShortSize(3L * 1073741824) == "3.0 GB", "short");
    Assert(SpeedLimit.Format(5.6 * 1048576, false) == "5.6 MB/s" && SpeedLimit.Format(1_000_000, true) == "8.0 Mbit/s", "speeds: " + SpeedLimit.Format(1_000_000, true));
    Assert(SpeedLimit.TimeLeft(600L * 1048576, 1048576) == "10 min left" && SpeedLimit.TimeLeft(30, 1) == "30 s left" && SpeedLimit.TimeLeft(5, 0) == "", "time left");
    Assert(SpeedLimit.EffectiveKBps(false, 500, 0) == 0, "unlimited by default");
    Assert(SpeedLimit.EffectiveKBps(true, 500, 0) == 500 && SpeedLimit.EffectiveKBps(true, 5000, 1000) == 1000 && SpeedLimit.EffectiveKBps(false, 5000, 1000) == 1000, "player and server caps");
    Assert(SpeedLimit.EffectiveKBps(true, 500, -1) == 500, "-1 is no cap");
    Assert(SpeedLimit.Suggest(10 * 1048576, 0) == 8100 && SpeedLimit.Suggest(10 * 1048576, 2000) == 2000 && SpeedLimit.Suggest(1000, 0) == 100, "suggestion: " + SpeedLimit.Suggest(10 * 1048576, 0));
    SpeedLimit.Set(1000);
    Assert(SpeedLimit.Delay(1024 * 1024) > TimeSpan.Zero, "waits once over the limit");
    SpeedLimit.Set(0);
    Assert(SpeedLimit.Delay(100 * 1024 * 1024) == TimeSpan.Zero, "no limit, no wait");
    Assert(UpdateHours.Inside(3, 2, 5) && !UpdateHours.Inside(5, 2, 5) && UpdateHours.Inside(23, 22, 4) && UpdateHours.Inside(1, 22, 4) && !UpdateHours.Inside(12, 22, 4) && UpdateHours.Inside(9, 0, 0), "hours");
    Manifest m = new()
    {
        Files =
        [
            new() { Path = "Data/common.MPQ", Size = 100, Category = FileCategory.Base },
            new() { Path = "Data/patch-A.MPQ", Size = 10, Category = FileCategory.Patch },
            new() { Path = "Data/deDE/a.MPQ", Size = 7, Category = FileCategory.Locale, Locale = "deDE" },
            new() { Path = "Data/patch-9.MPQ", Size = 3, Category = FileCategory.Option, Group = "Night sky", Option = "On" },
            new() { Path = "Interface/AddOns/X/x.lua", Size = 1, Category = FileCategory.Addon },
        ],
        OptionGroups = [new() { Name = "Night sky", Options = ["On", "Off"] }],
    };
    Assert(InstallSizes.Of(m, FileCategory.Base) == 100 && InstallSizes.Locale(m, "dede") == 7 && InstallSizes.Option(m, "Night sky", "On") == 3, "parts");
    Assert(InstallSizes.Total(m, new InstallChoice("/w", ["deDE"], true, "/w", new Dictionary<string, string> { ["Night sky"] = "On" })) == 121, "total with everything");
    Assert(InstallSizes.Total(m, new InstallChoice("/w", [], false, null, new Dictionary<string, string> { ["Night sky"] = "Off" })) == 10, "just the needed files");
    Assert(InstallSizes.FreeSpace(Path.GetTempPath()) > 0, "free space");
    return Task.CompletedTask;
}

static Task SettingsCopy()
{
    string text = SettingsText.Write([new("farclip", "1277"), new("dxvk.hud", "fps")],
                                     [new("theme", "wotlk"), new("options.Night sky", "On"), new("locales", "enUS,deDE"), new("evil", "a\nb")]);
    Assert(text.StartsWith(SettingsText.Header), "header");
    var parsed = SettingsText.Parse(text);
    Assert(parsed is not null, "parsed");
    var (tweaks, launcher) = parsed!.Value;
    Assert(tweaks["farclip"] == "1277" && tweaks["dxvk.hud"] == "fps" && tweaks.Count == 2, "tweaks");
    Assert(launcher["theme"] == "wotlk" && launcher["options.Night sky"] == "On" && launcher["locales"] == "enUS,deDE", "launcher: " + string.Join(",", launcher.Keys));
    Assert(launcher["evil"] == "ab", "no new lines inside a value");
    Assert(SettingsText.Parse("hello world") is null && SettingsText.Parse("") is null, "not settings");
    Assert(SettingsText.Parse("[tweaks]\r\nmaxFPS = 60\r\n[future]\r\nx = 1") is { } p && p.Tweaks["maxFPS"] == "60" && p.Launcher.Count == 0, "windows lines, unknown section");
    return Task.CompletedTask;
}

static Task ThemeMixing()
{
    Themes.Theme dark = Themes.FromYaml("dark", "accent: \"#F2C14E\"\nbackground: \"#0E0B14\"\ntext: \"#E8E2D0\"");
    Themes.Theme light = Themes.FromYaml("light", "background: \"#ECEEF1\"\ntext: \"#23262B\"");
    Themes.Theme wotlk = Themes.FromYaml("wotlk", "name: Wrath of the Lich King\norder: 3\naccent: \"#3BA7E3\"\nlight.accent: \"#24678C\"\ndark.background: \"#0E0F11\"\nbogus: \"#123456\"\ntext.x: red");
    Assert(wotlk.Name == "Wrath of the Lich King" && wotlk.Order == 3, "name and order");
    Dictionary<string, string> d = Themes.Resolve(dark, wotlk, "dark", dark);
    Assert(d["accent"] == "#3BA7E3" && d["background"] == "#0E0F11" && d["text"] == "#E8E2D0" && !d.ContainsKey("bogus"), "dark: " + string.Join(",", d));
    Dictionary<string, string> l = Themes.Resolve(light, wotlk, "light", dark);
    Assert(l["accent"] == "#24678C" && l["background"] == "#ECEEF1" && l["text"] == "#23262B", "light: " + string.Join(",", l));
    string[] have = ["temucraft", "wotlk"];
    Assert(Themes.Pick("", "", new(), have) == ("dark", "temucraft"), "nothing chosen");
    Assert(Themes.Pick("", "", new("wotlk", false, "light", false), have) == ("light", "wotlk"), "the server's default");
    Assert(Themes.Pick("dark", "temucraft", new("wotlk", false, "light", false), have) == ("dark", "temucraft"), "the player's pick");
    Assert(Themes.Pick("dark", "temucraft", new("wotlk", true, "light", false), have) == ("dark", "wotlk"), "the server enforces its theme");
    Assert(Themes.Pick("", "nope", new("missing", false), have) == ("dark", "temucraft"), "unknown themes are skipped");

    // gradients and style lines (the classic, pandaria and draenor themes use them)
    Themes.Theme classic = Themes.FromYaml("classic", "name: Classic\nsectionBar: \"#4A6E94 > #1A2C46\"\ndark.play: \"#C8321A > #8A1608 > #5A0A02\"\n"
        + "panel: \"#111 > #222\"\ntexture: stone\nlight.texture: parchment\ntitleFont: \"Cinzel, Georgia, serif\"\nradius: \"2\"\nborderWidth: \"9\"\n"
        + "panelTexture: ../../evil.png\ntextureStrength: \"0.4\"");
    Assert(Themes.Stops("#4A6E94 > #1A2C46").Length == 2 && Themes.Stops("#4A6E94").Length == 1, "stops");
    Assert(Themes.Stops("#4A6E94 >").Length == 0 && Themes.Stops("red > #000000").Length == 0 && !Themes.IsBrush("> #000000"), "bad gradients");
    Assert(Themes.FirstColour("#C8321A > #8A1608") == "#C8321A", "first colour");
    Dictionary<string, string> c = Themes.Resolve(dark, classic, "dark", dark);
    Assert(c["sectionBar"] == "#4A6E94 > #1A2C46" && c["play"] == "#C8321A > #8A1608 > #5A0A02" && !c.ContainsKey("panel"), "gradients: " + string.Join(",", c));
    Dictionary<string, string> sd = Themes.ResolveStyle(dark, classic, "dark"), sl = Themes.ResolveStyle(light, classic, "light");
    Assert(sd["texture"] == "stone" && sl["texture"] == "parchment" && sd["titleFont"] == "Cinzel, Georgia, serif" && sd["radius"] == "2", "style: " + string.Join(",", sd));
    Assert(sd["borderWidth"] == "1" && sd["panelTexture"] == "none" && sd["textureStrength"] == "0.4", "style limits: " + string.Join(",", sd));
    Assert(Themes.ResolveStyle(dark, wotlk, "dark")["texture"] == "none" && Themes.ResolveStyle(null, null, "dark").Count == Themes.StyleKeys.Count, "style defaults");
    Assert(Themes.IsImageFile("marble.png") && !Themes.IsImageFile("../marble.png") && !Themes.IsImageFile("C:marble.png") && !Themes.IsImageFile("marble.exe"), "theme pictures");

    // the built-in themes: every line is a known colour or style line with a value that fits
    string? themesDir = null;
    for (DirectoryInfo? up = new(AppContext.BaseDirectory); up is not null && themesDir is null; up = up.Parent)
        if (Directory.Exists(Path.Combine(up.FullName, "src", "Launcher.App", "Themes"))) themesDir = Path.Combine(up.FullName, "src", "Launcher.App", "Themes");
    Assert(themesDir is not null, "found the themes folder");
    foreach (string file in Directory.GetFiles(themesDir!, "*.yml"))
    {
        Themes.Theme t = Themes.FromYaml(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
        foreach ((string key, string value) in t.Lines)
        {
            string bare = key.StartsWith("dark.") ? key[5..] : key.StartsWith("light.") ? key[6..] : key;
            bool ok = Themes.Keys.Contains(bare) ? Themes.IsBrush(value)
                : Themes.StyleKeys.ContainsKey(bare) && Themes.ResolveStyle(null, Themes.FromYaml("x", $"{bare}: \"{value}\""), "dark")[bare] == value;
            Assert(ok, $"{Path.GetFileName(file)}: {key}: {value}");
        }
    }
    foreach (string mode in Themes.Modes)
    {
        Themes.Theme m = Themes.FromYaml(mode, File.ReadAllText(Path.Combine(themesDir!, mode + ".yml")));
        Assert(Themes.Keys.All(m.Lines.ContainsKey), $"{mode}.yml sets every colour");
    }
    return Task.CompletedTask;
}

static Task DxvkTweaks()
{
    string dir = Directory.CreateTempSubdirectory().FullName;
    File.WriteAllLines(Path.Combine(dir, "dxvk.conf"), ["# mine", "d3d9.maxFrameRate = 60", "dxvk.hud = fps"]);
    DxvkConf conf = DxvkConf.Load(dir);
    Assert(conf.Get("d3d9.maxFrameRate") == "60" && conf.Get("dxvk.hud") == "fps", "read");
    conf.Set("d3d9.samplerAnisotropy", "16");
    conf.Set("dxvk.hud", "");
    conf.Save();
    string[] lines = File.ReadAllLines(Path.Combine(dir, "dxvk.conf"));
    Assert(lines.SequenceEqual(["# mine", "d3d9.maxFrameRate = 60", "d3d9.samplerAnisotropy = 16"]), string.Join("|", lines));
    Assert(Tweaks.FileOf("d3d9.samplerAnisotropy") == "dxvk" && Tweaks.FileOf("farclip") == "config" && Tweaks.FileOf("whatever") == "config", "files");
    LauncherPolicy policy = LauncherPolicy.From(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
        """{"tweaks": {"d3d9.samplerAnisotropy": {"value": 8, "enforce": true}, "farclip": {"value": 1277, "enforce": true}}}""")!);
    ConfigWtf config = ConfigWtf.Load(dir);
    Assert(policy.ApplyEnforcedTweaks(config) == 1 && config.Get("d3d9.samplerAnisotropy") is null, "config gets only its own");
    Assert(policy.ApplyEnforcedTweaks(conf, "dxvk") == 1 && conf.Get("d3d9.samplerAnisotropy") == "8", "dxvk gets its own");
    // no dxvk.conf and nothing to write: no empty file appears
    string empty = Directory.CreateTempSubdirectory().FullName;
    DxvkConf.Load(empty).Save();
    Assert(!File.Exists(Path.Combine(empty, "dxvk.conf")), "no empty file");
    return Task.CompletedTask;
}

static Task NavAndHeader()
{
    ServerSettings s = ServerSettings.Parse(["SERVER_URL=https://f.example/",
        "NAV=Register|https://f.example:4000/; AoWoW|https://db.example/ ; Bad|javascript:alert(1); NoName|; |https://x/",
        "SHOW_IP=0"]);
    Assert(s.Nav.Select(n => n.Name).SequenceEqual(["Register", "AoWoW"]), "links: " + string.Join(",", s.Nav.Select(n => n.Name)));
    Assert(s.Nav[0].Url.Port == 4000, "port");
    Assert(s.Nav[0].Tip == "" && s.Nav[0].Tooltip(true) == "https://f.example:4000/" && s.Nav[0].Tooltip(false) is null, "plain tip");
    ServerSettings t = ServerSettings.Parse(["SERVER_URL=https://f.example/", "NAV_SHOW_URL=0",
        "NAV=Register|https://f.example:4000/|Use the login you were given.\\nAsk Ryan if it fails"]);
    Assert(!t.NavShowUrl && t.Nav[0].Tip == "Use the login you were given.\nAsk Ryan if it fails", "tip: " + t.Nav[0].Tip);
    Assert(t.Nav[0].Tooltip(true) == t.Nav[0].Tip + "\nhttps://f.example:4000/", "tip with url");
    Assert(!s.ShowIp && s.ShowUsername, "switches");
    ServerSettings d = ServerSettings.Parse(["SERVER_URL=https://f.example/"]);
    Assert(d.Nav.Count == 0 && d.ShowIp && d.ShowUsername && d.NavShowUrl, "defaults");
    return Task.CompletedTask;
}

static async Task SelfUpdateLeftovers()
{
    string dir = TempDir(), exe = Path.Combine(dir, "TemucraftLauncher.exe");
    File.WriteAllText(exe, "running");
    foreach (string f in SelfCheck.Leftovers(exe)) File.WriteAllText(f, "left");
    Check(SelfCheck.CleanUp(exe) == 0, "every leftover removed");
    Check(SelfCheck.Leftovers(exe).All(f => !File.Exists(f)), "no leftover is still there");
    Check(File.ReadAllText(exe) == "running", "the running launcher is kept");
    Check(SelfCheck.CleanUp(exe) == 0 && SelfCheck.CleanUp(Path.Combine(dir, "missing", "x.exe")) == 0, "nothing to remove is fine");
    File.WriteAllText(exe + ".old", "left");
    await SelfCheck.CleanUpAtStartAsync(exe, tries: 2, delayMs: 10);
    Check(!File.Exists(exe + ".old"), "removed at start");
}

static Task NewsTags()
{
    var parts = NewsItem.Segments("• [new] Login window\n• [Not live yet] Wardrobe [not a tag] [Fixed]");
    Assert(parts.Count(p => p.Color is not null) == 3, string.Join("|", parts.Select(p => p.Text)));
    Assert(parts[1] == ("[New]", "#8FC24A"), "tag written in its own spelling and colour");
    Assert(string.Concat(parts.Select(p => p.Text)).Contains("[not a tag]"), "an unknown bracket was changed");
    var values = NewsItem.Segments("Costs {-350 mana} instead of 335, heals {+661} more");
    Assert(values.Any(p => p.Text == "350 mana" && p.Color == NewsItem.Worse) && values.Any(p => p.Text == "661" && p.Color == NewsItem.Better), "better/worse");
    var lines = NewsItem.Lines("• [New] Racial heals\n  - Human: Light's Mending\n  • Dwarf: Stoneform Mend\n• [New] Other");
    Assert(lines.Count == 2 && lines[0].Details.SequenceEqual(["Human: Light's Mending", "Dwarf: Stoneform Mend"]) && lines[1].Details.Count == 0, "details");
    return Task.CompletedTask;
}

static Task RequiredAddonsEnabled()
{
    string wow = TempDir();
    string account = Path.Combine(wow, "WTF", "Account", "RYAN"), character = Path.Combine(account, "Realm 1", "Arthas");
    Directory.CreateDirectory(character);
    File.WriteAllText(Path.Combine(account, "AddOns.txt"), "Questie: disabled\nDetails: disabled\nTemucraft: enabled\n");
    File.WriteAllText(Path.Combine(character, "AddOns.txt"), "Temucraft: disabled\n");
    int changed = AddonsTxt.EnableRequired(wow, ["Temucraft", "Questie"]);
    Check(changed == 2, $"changed {changed}");
    string top = File.ReadAllText(Path.Combine(account, "AddOns.txt"));
    Check(top.Contains("Questie: enabled") && top.Contains("Details: disabled"), top);
    Check(File.ReadAllText(Path.Combine(character, "AddOns.txt")).Contains("Temucraft: enabled"), "character file");
    Check(AddonsTxt.EnableRequired(wow, ["Temucraft"]) == 0, "second run changes nothing");
    return Task.CompletedTask;
}

static async Task DiagnosticsMarks()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Data"));
    File.WriteAllBytes(Path.Combine(wow, "Data", "patch-T.MPQ"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(wow, "Data", "patch-X.MPQ"), [9]);
    File.WriteAllBytes(Path.Combine(wow, "extra.dll"), [4]);
    string sha = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
    Manifest manifest = new() { Files = [
        new ManifestFile { Path = "Data/patch-T.MPQ", Size = 3, Sha256 = sha, Category = FileCategory.Patch },
        new ManifestFile { Path = "Data/patch-X.MPQ", Size = 1, Sha256 = new string('0', 64), Category = FileCategory.Patch },
        new ManifestFile { Path = "Data/patch-Y.MPQ", Size = 1, Sha256 = new string('0', 64), Category = FileCategory.Patch } ] };
    string text = await Diagnostics.BuildAsync(wow, manifest, ["Mods: (none)"]);
    Check(text.Contains("Data/patch-T.MPQ") && text.Contains("same as the server"), text);
    Check(text.Contains("DIFFERENT from the server"), text);
    Check(text.Contains("extra.dll") && text.Contains("not from the server"), text);
    Check(text.Contains("Data/patch-Y.MPQ: missing") && text.Contains("Wow.exe: not found"), text);
}

static async Task DiagnosticsCrashHelper()
{
    string wow = TempDir();
    string errors = Path.Combine(wow, "Errors");
    Directory.CreateDirectory(errors);
    File.WriteAllText(Path.Combine(errors, "2026-11-24 21.05.13 Crash.txt"),
        "ERROR #132 (0x85100084) Fatal Exception\r\nException:\t0xC0000005 (ACCESS_VIOLATION) at 0023:0058A57A\r\n\r\nWoWBuild: 12340\r\n");
    string report = Path.Combine(errors, "Temucraft-2026-11-24-210513.txt");
    File.WriteAllText(report, string.Join("\r\n",
        "Temucraft crash report", "", "Hooks: server packets on, client packets on", "",
        "What happened: exception 0xC0000005 (ACCESS_VIOLATION)",
        "Crashed at: 0x0058A57A Wow.exe+0x18A57A CGGossipInfo::SetGossip+0x2A",
        "It tried to read from 0x00000010 (a null pointer)", "",
        "Stack of the crashing thread (StackWalk64):",
        "  #0   0x0058A57A Wow.exe+0x18A57A CGGossipInfo::SetGossip+0x2A",
        "  #1   0x00632018 Wow.exe+0x232018 NetClient::ProcessMessage+0x38", "",
        "Last server packets received (newest first, 1 shown, 9 since the game started):",
        "  -0.012 s  SMSG_GOSSIP_MESSAGE (0x17D)                   312 bytes", "",
        "Last client packets sent (newest first, 0 shown, 0 since the game started):", "  (none)", "",
        "Last Lua errors (newest first, 0 since the game started):", "  (none)", ""));
    File.WriteAllBytes(Path.ChangeExtension(report, ".dmp"), new byte[2048]);
    string text = await Diagnostics.BuildAsync(wow, null, []);
    Check(text.Contains("Newest Temucraft crash report: Temucraft-2026-11-24-210513.txt"), text);
    Check(text.Contains("Crashed at: 0x0058A57A Wow.exe+0x18A57A CGGossipInfo::SetGossip+0x2A"), text);
    Check(text.Contains("SMSG_GOSSIP_MESSAGE (0x17D)") && text.Contains("NetClient::ProcessMessage+0x38"), text);
    Check(!text.Contains("Last client packets:") && text.Contains("Minidump: ") && text.Contains(".dmp"), text);
    // the game's own report is still read, and ours is not listed as one of the game's
    Check(text.Contains("Last crashes (1, newest first):") && text.Contains("ERROR #132"), text);
    Check(text.IndexOf("Newest Temucraft crash report", StringComparison.Ordinal) < text.IndexOf("Last crashes", StringComparison.Ordinal), text);
    Check(Diagnostics.NewestCrashDump(wow, TimeSpan.FromDays(7)) == Path.ChangeExtension(report, ".dmp"), "dump");
    File.SetLastWriteTime(report, DateTime.Now.AddDays(-30));
    Check(Diagnostics.NewestCrashDump(wow, TimeSpan.FromDays(7)) is null, "old dump");
}

static Task LoginScenes()
{
    string wow = TempDir();
    Directory.CreateDirectory(Path.Combine(wow, "Interface"));
    string file = Path.Combine(wow, "Interface", "loginui.lua");
    File.WriteAllText(file, "vx = {\r\n\t[\"SceneList\"] = {\r\n\t\t\t\"cl\", -- classic\r\n\t\t\t--\"ns\", -- chinese lich king (no Sindragosa)\r\n\t\t\t\"lk\", -- lich king\r\n\t\t\t--\"Interface\\\\GLUES\\\\loading\",\r\n\t\t\t},\r\n\t[\"LoginMusic\"] ={ --\"cl\", -- classic\r\n}\r\n");
    var scenes = LoginScreens.Read(wow);
    Check(scenes.Count == 3 && scenes[0].On && !scenes[1].On && scenes[2].Name == "lich king", string.Join(",", scenes));
    Check(LoginScreens.Write(wow, ["ns"]), "changed");
    scenes = LoginScreens.Read(wow);
    Check(!scenes[0].On && scenes[1].On && !scenes[2].On, string.Join(",", scenes));
    string text = File.ReadAllText(file);
    Check(text.Contains("\r\n\t\t\t\"ns\", -- chinese lich king (no Sindragosa)\r\n") && text.Contains("LoginMusic"), text);
    Check(!LoginScreens.Write(wow, ["ns"]) && !LoginScreens.Write(wow, []), "no change");
    Check(LoginScreens.Title("lich king") == "Lich King", "title");
    return Task.CompletedTask;
}

static Task LauncherUpdateRule()
{
    Check(LauncherPolicy.From(null).LauncherUpdateFirst, "default");
    LauncherPolicy recommended = LauncherPolicy.From(new Dictionary<string, System.Text.Json.JsonElement>
        { ["launcherUpdate"] = System.Text.Json.JsonDocument.Parse("{\"value\": \"recommended\"}").RootElement });
    Check(!recommended.LauncherUpdateFirst, "recommended");
    LauncherPolicy off = LauncherPolicy.From(new Dictionary<string, System.Text.Json.JsonElement>
        { ["launcherUpdate"] = System.Text.Json.JsonDocument.Parse("false").RootElement });
    Check(!off.LauncherUpdateFirst && off.Unknown.Count == 0, "false");
    LauncherPolicy notes = LauncherPolicy.From(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
        "{\"_comment\": [\"a\"], \"clearCache\": {\"_comment\": \"b\", \"value\": true}, \"tweaks\": {\"_comment\": \"c\", \"farclip\": {\"_comment\": \"d\", \"value\": 1277}}}"));
    Check(notes.Unknown.Count == 0 && notes.Tweaks.Count == 1 && notes.Tweaks["farclip"].CVarText == "1277" && notes.Setting("clearCache")?.Bool == true, "_comment notes");
    return Task.CompletedTask;
}

static Task FeedRules()
{
    LauncherPolicy p = Manifest.Parse("""{"files": [], "policy": {"news": {"show": false}, "changelog": {"entries": 5, "more": -1}}}""").Rules;
    Assert(!p.News.Show && p.Changelog is { Show: true, Entries: 5, More: -1 }, $"{p.News} {p.Changelog}");
    Assert(LauncherPolicy.From(null).News == FeedRule.Default, "default");
    Assert(new FeedRule(true, 20, 20).MoreLabel(45) == "Load 20 more (45 left)", new FeedRule(true, 20, 20).MoreLabel(45)!);
    Assert(new FeedRule(true, 20, 20).MoreLabel(7) == "Load all (7)", "fewer left than a page");
    Assert(new FeedRule(true, 20, -1).MoreLabel(45) == "Load all (45)" && new FeedRule(true, 20, -1).NextCount(20, 65) == 65, "-1 loads all");
    Assert(new FeedRule(true, 20, 20).MoreLabel(0) is null && new FeedRule(true, 20, 20).NextCount(20, 65) == 40, "paging");

    string file = Path.Combine(TempDir(), "news-cache.json");
    new FeedCache { News = "[]", Changelog = """[{"date":"2026-11-19","title":"x"}]""", ChangelogRule = new FeedRule(true, 5, -1) }.Save(file);
    FeedCache back = FeedCache.Load(file);
    Assert(back.ChangelogRule == new FeedRule(true, 5, -1) && NewsItem.Parse(back.Changelog).Count == 1, "cache round trip");
    File.WriteAllText(file, "{ broken");
    Assert(FeedCache.Load(file).News == "", "a broken cache must just be empty");
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

static Task DefaultKeyBindings()
{
    string file = Path.Combine(Path.GetTempPath(), "kb" + Guid.NewGuid().ToString("N") + ".wtf");
    File.WriteAllText(file, "BINDINGMODE 0\nbind A STRAFELEFT\nbind Q NONE\n");
    BindingsFile b = BindingsFile.Load(file, "test");
    Dictionary<string, List<string>> keys = b.Keys();
    Check(keys["MOVEFORWARD"].SequenceEqual(["W", "UP"]), "defaults show without file lines");
    Check(keys["STRAFELEFT"].SequenceEqual(["A"]) && keys["TURNLEFT"].SequenceEqual(["LEFT"]), "a file line moves a key");
    Check(!keys.ContainsKey("NONE") && !keys.Values.Any(k => k.Contains("Q")), "NONE takes a key off");
    // taking a default key off a command writes a NONE line, or the game gives it back
    b.Set(new Dictionary<string, List<string>> { ["MOVEFORWARD"] = ["W"] });
    b.Save();
    string text = File.ReadAllText(file);
    Check(text.Contains("bind W MOVEFORWARD") && text.Contains("bind UP NONE"), text);
    Check(BindingsFile.Load(file, "test").Keys()["MOVEFORWARD"].SequenceEqual(["W"]), "read back");
    File.Delete(file);
    return Task.CompletedTask;
}

static Task ModifiedClicks()
{
    string wow = Path.Combine(Path.GetTempPath(), "mc" + Guid.NewGuid().ToString("N"));
    string account = Path.Combine(wow, "WTF", "Account", "MAIN");
    string character = Path.Combine(account, "Realm", "Hero");
    Directory.CreateDirectory(character);
    File.WriteAllText(Path.Combine(wow, "Wow.exe"), "");
    File.WriteAllText(Path.Combine(account, "bindings-cache.wtf"), "BINDINGMODE 0\nbind A STRAFELEFT\nmodifiedclick SELFCAST ALT\n");
    File.WriteAllText(Path.Combine(character, "bindings-cache.wtf"), "bind B STRAFERIGHT\nmodifiedclick SELFCAST ALT\n");
    File.WriteAllText(Path.Combine(account, "config-cache.wtf"), "SET autoLootDefault \"1\"\n");
    GameOptionStore store = GameOptionStore.Load(wow);
    Check(store.Get("modifiedclick SELFCAST") == "ALT", "reads the account's key");
    Check(store.Get("modifiedclick FOCUSCAST") is null, "not set: the game's default");
    Check(store.Set("modifiedclick SELFCAST", "CTRL") && store.Set("modifiedclick FOCUSCAST", "SHIFT"), "set");
    store.Set("targetOfTargetMode", "3");
    store.Save();
    string acc = File.ReadAllText(Path.Combine(account, "bindings-cache.wtf"));
    string chr = File.ReadAllText(Path.Combine(character, "bindings-cache.wtf"));
    Check(acc.Contains("modifiedclick SELFCAST CTRL") && acc.Contains("modifiedclick FOCUSCAST SHIFT") && acc.Contains("bind A STRAFELEFT"), acc);
    Check(chr.Contains("modifiedclick SELFCAST CTRL") && !chr.Contains("FOCUSCAST") && chr.Contains("bind B STRAFERIGHT"), chr);
    Check(GameOptionStore.Load(wow).Get("targetOfTargetMode") == "3", "CVars still go to Config.wtf");
    GameOption self = GameOptions.All.Single(o => o.ModifiedClick == "SELFCAST");
    Check(self.Key == "modifiedclick SELFCAST" && self.ChoiceLabel(1) == "ALT key", self.Key);
    Check(GameOptions.All.Where(o => o.Choices is not null).All(o => o.ChoiceLabels is null || o.ChoiceLabels.Count == o.Choices!.Count), "a label per choice");
    Check(!GameOptions.All.Any(o => o.CVar == "autoLootKey"), "auto loot key is a modified click, not a CVar");
    Directory.Delete(wow, true);
    return Task.CompletedTask;
}

static Task MacroFileRoundTrip()
{
    string wow = Path.Combine(Path.GetTempPath(), "mf" + Guid.NewGuid().ToString("N"));
    string account = Path.Combine(wow, "WTF", "Account", "MAIN");
    string character = Path.Combine(account, "Realm", "Hero");
    Directory.CreateDirectory(character);
    Directory.CreateDirectory(Path.Combine(account, "SavedVariables"));
    // as the game writes it: CRLF, a name cut to 16 letters, an empty macro
    string text = "MACRO 4 \"Focus\" INV_Misc_QuestionMark\r\n#showtooltip\r\n/cast [@target] Mangle (Bear)\r\nEND\r\n"
                + "MACRO 1 \"#showtooltip/cas\" Ability_Hunter_BeastCall\r\n.learn 2575\r\n.additem 7005\r\nEND\r\n"
                + "MACRO 2 \"Empty\" INV_Misc_QuestionMark\r\nEND\r\n";
    File.WriteAllText(Path.Combine(account, "macros-cache.txt"), text);
    File.WriteAllText(Path.Combine(character, "macros-cache.txt"), "MACRO 16777217 \"test\" INV_Misc_QuestionMark\n/petautocaston Growl\nEND\n");

    MacroFile file = MacroFile.ForAccount(account);
    Check(file.Macros.Count == 3 && !file.IsCharacter && file.Limit == 36, "three account macros: " + file.Macros.Count);
    Check(file.Macros[0] is { Id: 4, Name: "Focus", Icon: "INV_Misc_QuestionMark", Body: "#showtooltip\n/cast [@target] Mangle (Bear)" }, "first macro");
    Check(file.Macros[1].Name == "#showtooltip/cas" && file.Macros[2].Body == "", "name and empty body");
    Check(file.Text == text, "writes back exactly what it read:\n" + file.Text);

    Macro added = file.Add("A name that is much too long \"quoted\"", " Spell_Holy_Heal ", "/cast Heal\r\nEND\n" + new string('x', 300));
    Check(added.Id == 3, "first free account id: " + added.Id);
    file.Macros.RemoveAll(m => m.Id == 4);
    file.Save();
    MacroFile again = MacroFile.ForAccount(account);
    Macro back = again.Macros.Single(m => m.Id == 3);
    Check(back.Name == "A name that is m" && back.Icon == "Spell_Holy_Heal", $"cleaned name and icon: {back.Name}|{back.Icon}");
    Check(back.Body.Length <= Macro.BodyLimit && back.Body.StartsWith("/cast Heal\nEND \n"), "body cut to 255 and END kept inside: " + back.Body[..20]);
    Check(again.Macros.Count == 3 && !again.Macros.Any(m => m.Id == 4), "deleted one, added one");
    Check(File.ReadAllText(again.File).Contains("\r\nEND\r\n") && !File.ReadAllBytes(again.File).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "CRLF, no BOM");

    MacroFile own = MacroFile.ForCharacter(character);
    Check(own.IsCharacter && own.Limit == 18 && own.Macros.Single().Name == "test", "character macros");
    Check(own.Add("Two").Id == 16777218, "character ids start at 16777217");
    Check(MacroFile.ForCharacter(Path.Combine(account, "Realm", "Nobody")).Macros.Count == 0, "no file, no macros");

    List<WowCharacter> characters = WowCharacter.List(wow);
    Check(characters.Count == 1 && characters[0] is { Account: "MAIN", Realm: "Realm", Name: "Hero" } && characters[0].AccountFolder == account,
        "characters found (SavedVariables is not a realm): " + string.Join(",", characters));
    Directory.Delete(wow, true);
    return Task.CompletedTask;
}

static Task PerCharacterOptions()
{
    string wow = Path.Combine(Path.GetTempPath(), "pc" + Guid.NewGuid().ToString("N"));
    string account = Path.Combine(wow, "WTF", "Account", "MAIN");
    string hero = Path.Combine(account, "Realm", "Hero"), alt = Path.Combine(account, "Realm", "Alt");
    Directory.CreateDirectory(hero);
    Directory.CreateDirectory(alt);
    File.WriteAllText(Path.Combine(wow, "WTF", "Config.wtf"), "SET farclip \"777\"\n");
    File.WriteAllText(Path.Combine(account, "config-cache.wtf"), "SET autoLootDefault \"1\"\nSET nameplateShowEnemies \"0\"\n");
    File.WriteAllText(Path.Combine(hero, "config-cache.wtf"), "SET nameplateShowEnemies \"1\"\nSET trackedQuests \"v\"\n");
    File.WriteAllText(Path.Combine(alt, "config-cache.wtf"), "SET cameraSavedPitch \"20\"\n");
    WowCharacter heroChar = WowCharacter.List(wow).Single(c => c.Name == "Hero");
    WowCharacter altChar = WowCharacter.List(wow).Single(c => c.Name == "Alt");

    GameOptionStore accountStore = GameOptionStore.Load(wow, null);
    Check(accountStore.Character is null && accountStore.Get("nameplateShowEnemies") == "0", "the account by default");
    GameOptionStore store = GameOptionStore.Load(wow, heroChar);
    Check(store.Get("nameplateShowEnemies") == "1", "the character's own value wins");
    Check(store.Get("autoLootDefault") == "1" && store.Get("farclip") == "777", "the rest from the account and Config.wtf");
    Check(store.IsPerCharacter("blockTrades") && store.IsPerCharacter("trackedQuests") && !store.IsPerCharacter("autoLootDefault")
          && !store.IsPerCharacter("modifiedclick SELFCAST"), "which settings are per character");
    store.Set("nameplateShowEnemies", "0");
    store.Set("blockTrades", "1");
    store.Set("autoLootDefault", "0");
    store.Save();
    string heroText = File.ReadAllText(Path.Combine(hero, "config-cache.wtf"));
    Check(heroText.Contains("SET nameplateShowEnemies \"0\"") && heroText.Contains("SET blockTrades \"1\"") && heroText.Contains("trackedQuests")
          && !heroText.Contains("autoLootDefault"), heroText);
    string accText = File.ReadAllText(Path.Combine(account, "config-cache.wtf"));
    Check(accText.Contains("SET autoLootDefault \"0\"") && !accText.Contains("blockTrades"), "account settings stay in the account's file: " + accText);
    Check(GameOptionStore.Load(wow, altChar).Get("blockTrades") is null && GameOptionStore.Load(wow, altChar).Get("nameplateShowEnemies") == "0",
        "the other character keeps its own (none: the account's)");
    string altBefore = File.ReadAllText(Path.Combine(alt, "config-cache.wtf"));
    GameOptionStore altStore = GameOptionStore.Load(wow, altChar);
    altStore.Set("farclip", "900");
    altStore.Save();
    Check(File.ReadAllText(Path.Combine(alt, "config-cache.wtf")) == altBefore && GameOptionStore.Load(wow).Get("farclip") == "900",
        "an account setting from a character goes to Config.wtf, the character file is left alone");
    Directory.Delete(wow, true);
    return Task.CompletedTask;
}

/// <summary>Answers like Apache: files under the folder, Range requests with 206.</summary>
sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = files;
    public HttpStatusCode? ForceStatus { get; init; }
    public string? LastAuth { get; private set; }
    public long? LastRangeFrom { get; private set; }
    public List<string> Requests { get; } = [];
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        LastAuth = request.Headers.Authorization?.ToString();
        lock (LastHeaders)
        {
            LastHeaders.Clear();
            foreach (var h in request.Headers) LastHeaders[h.Key] = string.Join(",", h.Value);
        }
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
