using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Launcher.Core;

/// <summary>The WoW 3.3.5a folder: checks, realmlist, cache, removing obsolete files and starting the game.</summary>
public static class GameFolder
{
    public static string ExePath(string folder) => Path.Combine(folder, "Wow.exe");

    public static bool LooksLikeClient(string folder) =>
        Directory.Exists(folder) && File.Exists(ExePath(folder)) && Directory.Exists(Path.Combine(folder, "Data"));

    /// <summary>The locale folders the client has (Data\enUS, Data\deDE...).</summary>
    public static IReadOnlyList<string> InstalledLocales(string folder)
    {
        string data = Path.Combine(folder, "Data");
        if (!Directory.Exists(data))
            return [];
        return Directory.GetDirectories(data)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: 4 } && char.IsLower(name[0]) && char.IsUpper(name[2]))
            .Select(name => name!)
            .Order()
            .ToList();
    }

    /// <summary>The client's languages: the name in English and in the language itself.</summary>
    public static readonly IReadOnlyDictionary<string, (string English, string Native)> LocaleNames =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["enUS"] = ("English (United States)", "English (United States)"),
            ["enGB"] = ("English (United Kingdom)", "English (United Kingdom)"),
            ["enCN"] = ("English (China)", "English (China)"),
            ["enTW"] = ("English (Taiwan)", "English (Taiwan)"),
            ["deDE"] = ("German", "Deutsch"),
            ["frFR"] = ("French", "Français"),
            ["esES"] = ("Spanish (Spain)", "Español (España)"),
            ["esMX"] = ("Spanish (Latin America)", "Español (Latinoamérica)"),
            ["ruRU"] = ("Russian", "Русский"),
            ["koKR"] = ("Korean", "한국어"),
            ["zhCN"] = ("Chinese (Simplified)", "简体中文"),
            ["zhTW"] = ("Chinese (Traditional)", "繁體中文"),
            ["ptBR"] = ("Portuguese (Brazil)", "Português (Brasil)"),
            ["itIT"] = ("Italian", "Italiano"),
        };

    /// <summary>"German · Deutsch": the name in the launcher's language, then in its own, once when they're the same.</summary>
    public static string LocaleName(string locale, Func<string, string>? translate = null)
    {
        if (!LocaleNames.TryGetValue(locale, out (string English, string Native) n)) return locale;
        string shown = translate?.Invoke(n.English) ?? n.English;
        return shown == n.Native ? shown : $"{shown} · {n.Native}";
    }

    /// <summary>The game's own language: Config.wtf's SET locale, else its only (or first) language folder; null
    /// without a game.</summary>
    public static string? ClientLocale(string folder)
    {
        if (!LooksLikeClient(folder)) return null;
        string? configured = null;
        try { configured = ConfigWtf.Load(folder).Get("locale"); } catch (IOException) { }
        IReadOnlyList<string> installed = InstalledLocales(folder);
        if (configured is { Length: 4 } && installed.Contains(configured, StringComparer.OrdinalIgnoreCase)) return configured;
        return installed.FirstOrDefault();
    }

    public static void WriteRealmlist(string folder, string realmlist)
    {
        if (string.IsNullOrWhiteSpace(realmlist))
            return;
        foreach (string locale in InstalledLocales(folder))
            File.WriteAllText(Path.Combine(folder, "Data", locale, "realmlist.wtf"), $"set realmlist {realmlist}\r\n",
                Encoding.ASCII);
    }

    public static IReadOnlyList<string> RemoveObsolete(string folder, IEnumerable<string> obsolete)
    {
        List<string> removed = [];
        foreach (string relative in obsolete)
        {
            string full = PathRules.Resolve(folder, relative);
            if (File.Exists(full))
            {
                File.Delete(full);
                removed.Add(relative);
            }
        }
        return removed;
    }

    /// <summary>The client's Cache folder holds old item and creature data that hides server changes.</summary>
    public static void ClearCache(string folder)
    {
        string cache = Path.Combine(folder, "Cache");
        if (Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
    }

    public static Process? Start(string folder) =>
        Process.Start(new ProcessStartInfo(ExePath(folder)) { WorkingDirectory = folder, UseShellExecute = true });
}

/// <summary>
/// Saves the password in the operating system's own password store, never in a file: Windows Credential Manager
/// (also under Wine), the macOS Keychain, or on Linux the desktop's secret store (GNOME Keyring, KWallet...) through
/// secret-tool. Where none is available, <see cref="IsSupported"/> is false and nothing is saved.
/// </summary>
public static class CredentialStore
{
    const string Target = "TemucraftLauncher";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() && File.Exists(MacTool)) || (OperatingSystem.IsLinux() && LinuxTool is not null);

    /// <summary>Where the password goes on this system, for the window's wording.</summary>
    public static string StoreName =>
        OperatingSystem.IsWindows() ? "Windows Credential Manager" : OperatingSystem.IsMacOS() ? "the macOS Keychain" : "your system's keyring";

    const string MacTool = "/usr/bin/security";

    /// <summary>secret-tool on the PATH (Linux); tests may point LAUNCHER_SECRET_TOOL at a stand-in.</summary>
    static string? LinuxTool => Environment.GetEnvironmentVariable("LAUNCHER_SECRET_TOOL") is { Length: > 0 } fake ? fake
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "secret-tool")).FirstOrDefault(File.Exists);

    public static void Save(string username, string password)
    {
        if (OperatingSystem.IsWindows()) { WindowsSave(username, password); return; }
        if (OperatingSystem.IsMacOS() && File.Exists(MacTool))
        {
            // through security's own prompt (-i), so the password never shows in the list of running programs
            if (Run(MacTool, ["-i"], $"add-generic-password -U -a {Quote(username)} -s {Target} -w {Quote(password)}\n").Code != 0)
                throw new IOException("Couldn't save the password in the macOS Keychain.");
            return;
        }
        if (LinuxTool is { } tool && Run(tool, ["store", "--label=Temucraft Launcher", "service", Target, "user", username], password).Code != 0)
            throw new IOException("Couldn't save the password in your system's keyring.");
    }

    public static string? Load()
    {
        if (OperatingSystem.IsWindows()) return WindowsLoad();
        (int code, string output) = OperatingSystem.IsMacOS() && File.Exists(MacTool) ? Run(MacTool, ["find-generic-password", "-s", Target, "-w"], null)
            : LinuxTool is { } tool ? Run(tool, ["lookup", "service", Target], null) : (1, "");
        string password = output.TrimEnd('\r', '\n');
        return code == 0 && password.Length > 0 ? password : null;
    }

    public static void Delete()
    {
        if (OperatingSystem.IsWindows()) { WindowsDelete(); return; }
        if (OperatingSystem.IsMacOS() && File.Exists(MacTool)) Run(MacTool, ["delete-generic-password", "-s", Target], null);
        else if (LinuxTool is { } tool) Run(tool, ["clear", "service", Target], null);
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static (int Code, string Output) Run(string file, string[] args, string? input)
    {
        try
        {
            ProcessStartInfo start = new(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in args) start.ArgumentList.Add(a);
            using Process process = Process.Start(start)!;
            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) { process.Kill(); return (1, ""); }
            return (process.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Logger.Warning($"Couldn't run {file}", e);
            return (1, "");
        }
    }

    static void WindowsSave(string username, string password)
    {
        byte[] secret = Encoding.Unicode.GetBytes(password);
        IntPtr blob = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            Native.CREDENTIAL credential = new()
            {
                Type = Native.CRED_TYPE_GENERIC,
                TargetName = Target,
                UserName = username,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)secret.Length,
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!Native.CredWrite(ref credential, 0))
                throw new IOException("Couldn't save the password in Windows Credential Manager.");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    static string? WindowsLoad()
    {
        if (!Native.CredRead(Target, Native.CRED_TYPE_GENERIC, 0, out IntPtr pointer))
            return null;
        try
        {
            Native.CREDENTIAL credential = Marshal.PtrToStructure<Native.CREDENTIAL>(pointer);
            return credential.CredentialBlobSize == 0 ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            Native.CredFree(pointer);
        }
    }

    static void WindowsDelete() => Native.CredDelete(Target, Native.CRED_TYPE_GENERIC, 0);

    static class Native
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}

/// <summary>Compares the running launcher with the one the server hands out.</summary>
public static class SelfCheck
{
    /// <summary>The launcher's own exe, or null when it isn't running as TemucraftLauncher(.exe) itself: started with
    /// "dotnet TemucraftLauncher.dll" (or from a test or tool), ProcessPath is the dotnet host, and a self update would
    /// replace dotnet with the launcher.</summary>
    public static string? CurrentExe =>
        Environment.ProcessPath is { } path
        && Path.GetFileNameWithoutExtension(path).Equals("TemucraftLauncher", StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>The newest changelog entry about the launcher ("Launcher 1.3.0"), to show what a new launcher brings.</summary>
    public static NewsItem? WhatsNew(IEnumerable<NewsItem> changelog) =>
        changelog.FirstOrDefault(i => i.Title.StartsWith("Launcher", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the server's launcher next to this one (checked against the manifest's SHA-256), then swaps them:
    /// the running exe is renamed to .old (Windows allows renaming a running program, not replacing it) and the new
    /// one takes its name. The caller starts it and closes. The .old file is removed at the next start.
    /// </summary>
    public static async Task<string> UpdateAsync(ServerClient client, Manifest manifest, IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        if (manifest.Launcher is null || CurrentExe is null) throw new InvalidOperationException("The server has no launcher to update to.");
        string exe = CurrentExe, fresh = exe + ".new", old = exe + ".old";
        await client.DownloadAsync(manifest.Launcher, fresh, progress, token);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; }   // put the running one back if the new one can't take its place
        return exe;
    }

    /// <summary>What a self update can leave next to the launcher: the previous copy (.old), a new copy that never took
    /// the exe's place (.new) and a half-downloaded one (.new.part).</summary>
    public static IReadOnlyList<string> Leftovers(string exe) => [exe + ".old", exe + ".new", exe + ".new.part"];

    /// <summary>Removes the launcher an update left behind and any unfinished new one; returns how many are still
    /// there (locked, for example while the previous launcher is still closing). Never throws.</summary>
    public static int CleanUp(string? exe = null)
    {
        exe ??= CurrentExe;
        if (exe is null) return 0;
        int left = 0;
        foreach (string leftover in Leftovers(exe))
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left++; }
        return left;
    }

    /// <summary>At start: removes the leftovers of the last self update, trying again quietly for a while when the
    /// previous launcher still holds its file. Never throws, so it can't stop the launcher from starting.</summary>
    public static async Task CleanUpAtStartAsync(string? exe = null, int tries = 10, int delayMs = 2000)
    {
        try
        {
            for (int i = 0; i < tries; i++)
            {
                if (CleanUp(exe) == 0) return;
                await Task.Delay(delayMs);
            }
            Logger.Info("An old launcher file is still in use; it is removed at a later start.");
        }
        catch (Exception e) { Logger.Warning("Couldn't remove the old launcher file", e); }
    }

    public static async Task<bool> MatchesAsync(Manifest manifest, CancellationToken token = default)
    {
        if (manifest.Launcher is null || CurrentExe is null || !File.Exists(CurrentExe))
            return true;
        return await Hashing.Sha256Async(CurrentExe, token: token) == manifest.Launcher.Sha256;
    }
}
