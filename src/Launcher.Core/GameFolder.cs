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
/// Saves the password in Windows Credential Manager (encrypted for the Windows user), never in a file. Other systems
/// don't save it.
/// </summary>
public static class CredentialStore
{
    const string Target = "TemucraftLauncher";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void Save(string username, string password)
    {
        if (!IsSupported)
            return;
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

    public static string? Load()
    {
        if (!IsSupported || !Native.CredRead(Target, Native.CRED_TYPE_GENERIC, 0, out IntPtr pointer))
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

    public static void Delete()
    {
        if (IsSupported)
            Native.CredDelete(Target, Native.CRED_TYPE_GENERIC, 0);
    }

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
    public static string? CurrentExe => Environment.ProcessPath;

    public static async Task<bool> MatchesAsync(Manifest manifest, CancellationToken token = default)
    {
        if (manifest.Launcher is null || CurrentExe is null || !File.Exists(CurrentExe))
            return true;
        return await Hashing.Sha256Async(CurrentExe, token: token) == manifest.Launcher.Sha256;
    }
}
