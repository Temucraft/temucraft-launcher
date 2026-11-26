using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>A character ticket from the server's char-summary: which game account the summary is for. Never the
/// password: the player types it once, the server checks it and answers with this.</summary>
public sealed record CharacterTicket(string Server, string Account, string Ticket);

/// <summary>Encrypts the saved ticket for this Windows user (DPAPI); tests use a stand-in.</summary>
public interface ITicketProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI (CryptProtectData, current user): only this Windows user on this PC can read it back.</summary>
public sealed class DpapiProtector : ITicketProtector
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Temucraft character ticket");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] data) => Run(data, protect: true);
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    static byte[] Run(byte[] data, bool protect)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("DPAPI is Windows only.");
        Native.DATA_BLOB input = default, entropy = default, output = default;
        try
        {
            input = Native.Blob(data);
            entropy = Native.Blob(Entropy);
            bool ok = protect
                ? Native.CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, Native.CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : Native.CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, Native.CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new IOException($"DPAPI failed ({Marshal.GetLastWin32Error()}).");
            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (entropy.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropy.pbData);
            if (output.pbData != IntPtr.Zero) Native.LocalFree(output.pbData);
        }
    }

    static class Native
    {
        public const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        public static DATA_BLOB Blob(byte[] data)
        {
            DATA_BLOB blob = new() { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}

/// <summary>
/// %AppData%\TemucraftLauncher\char-ticket.dat: the saved character ticket (server, account name, ticket), encrypted
/// for this Windows user. Belongs to one server: a ticket of another server address is never sent. Without a
/// protector (not Windows) nothing is saved.
/// </summary>
public sealed class CharacterTicketStore(string file, ITicketProtector? protector)
{
    static readonly byte[] Magic = "TCT1"u8.ToArray();

    public static string DefaultFile => Path.Combine(UserSettings.DefaultFolder, "char-ticket.dat");

    /// <summary>The store for this PC: DPAPI on Windows, nothing elsewhere.</summary>
    public static CharacterTicketStore ForThisPc() => new(DefaultFile, DpapiProtector.IsSupported ? new DpapiProtector() : null);

    public string File { get; } = file;
    public bool IsSupported => protector is not null;

    public CharacterTicket? Load(Uri server)
    {
        if (protector is null || !System.IO.File.Exists(File)) return null;
        try
        {
            byte[] data = System.IO.File.ReadAllBytes(File);
            if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            CharacterTicket? saved = JsonSerializer.Deserialize<CharacterTicket>(protector.Unprotect(data[Magic.Length..]));
            if (saved is null || saved.Ticket.Length == 0 || saved.Account.Length == 0) return null;
            return string.Equals(saved.Server, server.ToString(), StringComparison.Ordinal) ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            Logger.Warning("Couldn't read the saved character ticket; logging in again", error);
            return null;
        }
    }

    /// <summary>Saves it (through a temporary file); false when this PC can't keep it.</summary>
    public bool Save(CharacterTicket ticket)
    {
        if (protector is null) return false;
        try
        {
            byte[] data = [.. Magic, .. protector.Protect(JsonSerializer.SerializeToUtf8Bytes(ticket))];
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllBytes(File + ".tmp", data);
            System.IO.File.Move(File + ".tmp", File, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or System.Security.Cryptography.CryptographicException)
        {
            Logger.Warning("Couldn't save the character ticket", error);
            return false;
        }
    }

    public void Clear()
    {
        try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Logger.Warning("Couldn't delete the saved character ticket", error); }
    }
}

public enum TicketLoginStatus { LoggedIn, WrongLogin, TooManyTries, NotOnServer, Failed }

public sealed record TicketLoginResult(TicketLoginStatus Status, CharacterTicket? Ticket = null, int RetryAfterSeconds = 0, string? Error = null);

/// <summary>An answer from the server: the HTTP status and the body.</summary>
public readonly record struct ServerAnswer(int Status, string Body);

/// <summary>
/// The launcher session's choices about the character login: "Not now" (until the launcher starts again), a ticket
/// kept only for this session ("Remember me" off), and whether the server can do it at all.
/// </summary>
public sealed class CharacterLoginSession
{
    public bool NotNow { get; private set; }
    public bool ServerCantLogIn { get; private set; }
    public CharacterTicket? SessionTicket { get; set; }

    public void SayNotNow() => NotNow = true;
    public void ServerHasNoLogin() => ServerCantLogIn = true;

    /// <summary>Logged in again ("Log in" on the Settings page): the dialog may come again.</summary>
    public void Reset() { NotNow = false; ServerCantLogIn = false; }

    public bool ShouldAsk(CharacterTicket? saved) => saved is null && SessionTicket is null && !NotNow && !ServerCantLogIn;
}

/// <summary>
/// The player's game login for the character summary (server/char-summary.py): POST action=login with the game
/// account and password gives a ticket; the summary is asked for with the X-Temucraft-Ticket header; a ticket the
/// server no longer takes (expired, a new IP, the password changed) gives 403 {"error": "ticket"}.
/// </summary>
public static class CharacterLogin
{
    public const string Header = "X-Temucraft-Ticket";

    /// <summary>The game's last account name (WTF\Config.wtf accountName), for the dialog; null when none.</summary>
    public static string? AccountFromConfig(string wowFolder)
    {
        try
        {
            return wowFolder.Length > 0 && ConfigWtf.Load(wowFolder).Get("accountName") is { Length: > 0 } name ? name.Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    static JsonElement? Json(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The server's answer to a ticket: its reason (unknown, expired, ip, password) when it no longer works, else null.</summary>
    public static string? RejectedTicket(ServerAnswer answer) =>
        answer.Status == 403 && Json(answer.Body) is { } e && Text(e, "error") == "ticket" ? Text(e, "reason") ?? "unknown" : null;

    public static TicketLoginResult ReadLoginAnswer(Uri server, ServerAnswer answer)
    {
        JsonElement? json = Json(answer.Body);
        if (answer.Status == 200)
            return json is { } ok && Text(ok, "ticket") is { Length: >= 20 } ticket && Text(ok, "account") is { Length: > 0 } account
                ? new(TicketLoginStatus.LoggedIn, new CharacterTicket(server.ToString(), account, ticket))
                : new(TicketLoginStatus.NotOnServer);   // a char-summary without logins answers with the summary
        if (json is { } e)
        {
            string? error = Text(e, "error");
            if (answer.Status == 403 && error == "login") return new(TicketLoginStatus.WrongLogin);
            if (answer.Status == 429 || error == "rate")
            {
                int wait = e.TryGetProperty("retry_after", out JsonElement r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int s) ? Math.Max(1, s) : 600;
                return new(TicketLoginStatus.TooManyTries, RetryAfterSeconds: wait);
            }
        }
        // no char-summary (404), or one from before logins (405 "GET only", 400)
        if (answer.Status is 404 or 405 or 400) return new(TicketLoginStatus.NotOnServer);
        return new(TicketLoginStatus.Failed, Error: answer.Status is 401 or 403 ? ServerClient.RefusedMessage : $"the server answered {answer.Status}");
    }

    /// <summary>Logs in; never throws (a failure is <see cref="TicketLoginStatus.Failed"/>). The password only goes into the request.</summary>
    public static async Task<TicketLoginResult> LoginAsync(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ServerAnswer>> post,
        Uri server, string account, string password, CancellationToken token = default)
    {
        try
        {
            ServerAnswer answer = await post(new Dictionary<string, string> { ["action"] = "login", ["account"] = account.Trim(), ["password"] = password }, token);
            return ReadLoginAnswer(server, answer);
        }
        catch (Exception error)
        {
            return new(TicketLoginStatus.Failed, Error: error is OperationCanceledException ? "no answer in time" : error.Message);
        }
    }

    /// <summary>Tells the server to end the ticket; never throws (it is forgotten on this PC either way).</summary>
    public static async Task LogoutAsync(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ServerAnswer>> post, CancellationToken token = default)
    {
        try { await post(new Dictionary<string, string> { ["action"] = "logout" }, token); }
        catch (Exception error) { Logger.Info($"Character summary: couldn't tell the server about the log out ({error.Message})", toWindow: false); }
    }

    /// <summary>
    /// The summary with the player's ticket: the saved one (or this session's), else the summary without one (the
    /// launcher login's, as before) and, when that is empty (the server doesn't know whose it is) and the player hasn't
    /// said "Not now", the login dialog (<paramref name="ask"/>: the new ticket, or null) and the summary again with its ticket. A ticket
    /// the server no longer takes is dropped and the dialog comes once more; never twice in one refresh.
    /// <paramref name="get"/>: GET char-summary with that ticket (null: without), or throws.
    /// </summary>
    public static async Task<(CharacterSummary.Outcome Outcome, int Characters, string? Error, string? Dropped)> RefreshAsync(
        Func<string?, CancellationToken, Task<ServerAnswer>> get, Func<Task<CharacterTicket?>> ask,
        CharacterTicketStore store, CharacterLoginSession session, Uri server,
        string wowFolder, int realmId, string? realmName, CancellationToken token = default)
    {
        string? dropped = null;
        try
        {
            CharacterTicket? saved = store.Load(server) ?? session.SessionTicket;
            ServerAnswer answer = await get(saved?.Ticket, token);
            if (saved is not null && RejectedTicket(answer) is { } reason)
            {
                dropped = reason;
                Forget(store, session);
                saved = null;
                answer = await get(null, token);
            }
            if (answer.Status == 404) return (CharacterSummary.Outcome.NotOnServer, 0, null, dropped);
            // only when the summary without a ticket is empty: a server where the launcher login is the game account already answers
            if (saved is null && answer.Status == 200 && NoAccount(answer.Body) && session.ShouldAsk(null) && await ask() is { } fresh)
            {
                ServerAnswer withTicket = await get(fresh.Ticket, token);
                if (RejectedTicket(withTicket) is { } again) { dropped = again; Forget(store, session); }
                else answer = withTicket;
            }
            if (answer.Status != 200) return (CharacterSummary.Outcome.Failed, 0, $"the server answered {answer.Status}", dropped);
            (CharacterSummary.Outcome outcome, int count, string? error) = await CharacterSummary.RefreshAsync(
                _ => Task.FromResult<string?>(answer.Body), wowFolder, realmId, realmName, token);
            return (outcome, count, error, dropped);
        }
        catch (Exception error)
        {
            return (CharacterSummary.Outcome.Failed, 0, error.Message, dropped);
        }
    }

    /// <summary>The summary names no realm at all: the server didn't know whose it was (realms come with every account).</summary>
    static bool NoAccount(string body)
    {
        try { return CharacterSummary.Parse(body).Count == 0; }
        catch (Exception error) when (error is JsonException or FormatException) { return false; }
    }

    static void Forget(CharacterTicketStore store, CharacterLoginSession session)
    {
        store.Clear();
        session.SessionTicket = null;
    }
}
