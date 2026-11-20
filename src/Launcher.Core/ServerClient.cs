using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Launcher.Core;

public sealed class LoginFailedException(string message) : Exception(message);

/// <summary>A new launcher was installed: start it and close this one.</summary>
public sealed class LauncherRestartException(string exe) : Exception("A new launcher was installed.")
{
    public string Exe { get; } = exe;
}

/// <summary>
/// Talks to the Apache folder: the manifest and the files, with the username and password Apache asks for (HTTP
/// basic auth, over https). The server also only lets in known addresses; a refused login and a refused address get the
/// same message, so nobody can tell which it was.
/// </summary>
public sealed class ServerClient : IDisposable
{
    public const string RefusedMessage =
        "The server didn't accept the login. Check your username and password; if they're right, ask the server's owner to let you in.";

    readonly HttpClient _http;
    readonly Uri _base;
    readonly bool _pinned;

    static HttpMessageHandler NewHandler(string? pin) => new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => CertificatePin.Accept(certificate, errors, pin),
        },
    };

    /// <param name="clientInfo">The player's PC information the server collects (sent as one header); null sends none.</param>
    public ServerClient(ServerSettings settings, string username, string password, HttpMessageHandler? handler = null,
        IReadOnlyDictionary<string, string>? clientInfo = null)
    {
        _base = settings.ServerUrl;
        _pinned = settings.CertificateSha256 is not null;
        _http = new HttpClient(handler ?? NewHandler(settings.CertificateSha256));
        _http.Timeout = Timeout.InfiniteTimeSpan; // downloads take as long as they take; reads time out below
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherInfo.UserAgent);
        if (clientInfo is { Count: > 0 })
            foreach ((string name, string value) in ClientInfo.Headers(clientInfo))
                _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    public async Task<Manifest> GetManifestAsync(CancellationToken token = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "manifest.json")),
            HttpCompletionOption.ResponseContentRead, timeout.Token);
        ThrowForStatus(response);
        Logger.Debug($"manifest.json: {(int)response.StatusCode}");
        return Manifest.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <summary>A small text file next to the manifest (news.json, addons.json...); null when the server has none.</summary>
    public async Task<string?> GetTextAsync(string relative, CancellationToken token = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relative)),
            HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowForStatus(response);
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    /// <summary>A small file next to the manifest (a preview image...); null when the server has none.</summary>
    public async Task<byte[]?> GetBytesAsync(string relative, CancellationToken token = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Uri uri = new(_base, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));
        using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowForStatus(response);
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>The file's address, with each part of its path escaped (names may hold spaces or '#').</summary>
    public Uri FileUri(ManifestFile file) =>
        new(_base, string.Join('/', file.Url.Split('/').Select(Uri.EscapeDataString)));

    /// <summary>Sends a request; a refused certificate becomes a message the player can act on.</summary>
    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken token)
    {
        try
        {
            return await _http.SendAsync(request, completion, token);
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException)
        {
            Logger.Warning($"The server's certificate was refused ({request.RequestUri?.Authority})", e);
            throw new HttpRequestException(_pinned
                ? "The server's certificate doesn't match CERT_SHA256 in launcher.env. Get the current launcher.env from the server's owner."
                : "The server's certificate isn't trusted. If it's self-signed, put its SHA-256 fingerprint in launcher.env as CERT_SHA256.", e);
        }
    }

    static void ThrowForStatus(HttpResponseMessage response)
    {
        // One message for both on purpose: like the server, the launcher doesn't tell a stranger whether the password
        // was wrong or the address isn't allowed
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Logger.Info($"Login refused ({(int)response.StatusCode})", toWindow: false);
            throw new LoginFailedException(RefusedMessage);
        }
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Downloads to target + ".part", resuming a part left by an earlier try, checks the SHA-256 and only then moves
    /// it into place. A file that doesn't match is deleted, never installed.
    /// </summary>
    public async Task DownloadAsync(ManifestFile file, string target, IProgress<(long Done, long Total)>? progress = null,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string part = target + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size)
        {
            File.Delete(part);
            have = 0;
        }

        if (file.Size == 0 && have == 0)
            File.Create(part).Dispose();   // an empty file: nothing to download, but the checks below need the .part
        else if (have < file.Size)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, FileUri(file));
            if (have > 0)
                request.Headers.Range = new RangeHeaderValue(have, null);
            using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            ThrowForStatus(response);
            // A server that ignores the range sends the whole file again
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                have = 0;

            await using Stream body = await response.Content.ReadAsStreamAsync(token);
            await using FileStream output = new(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20, useAsync: true);
            byte[] buffer = new byte[1 << 20];
            int read;
            // with a speed limit, small reads so the waits stay short and the progress smooth
            while ((read = await ReadWithTimeoutAsync(body, buffer.AsMemory(0, SpeedLimit.BytesPerSecond > 0 ? 64 * 1024 : buffer.Length), token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                have += read;
                progress?.Report((have, file.Size));
                await SpeedLimit.WaitAsync(read, token);
            }
        }

        string sha = await Hashing.Sha256Async(part, token: token);
        if (sha != file.Sha256)
        {
            File.Delete(part);
            Logger.Warning($"{file.Path}: checksum mismatch (got {sha}, expected {file.Sha256}); the download was deleted");
            throw new InvalidDataException($"{file.Path} didn't download correctly (checksum mismatch). Try again.");
        }
        File.Move(part, target, overwrite: true);
    }

    /// <summary>Measures the download speed: reads the start of <paramref name="file"/> for at most
    /// <paramref name="time"/> or <paramref name="maxBytes"/> (no speed limit) and returns bytes per second.
    /// The first half second is left out (the connection still speeding up).</summary>
    public async Task<double> SpeedTestAsync(ManifestFile file, TimeSpan time, long maxBytes, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, FileUri(file));
        request.Headers.Range = new RangeHeaderValue(0, Math.Min(file.Size, maxBytes) - 1);
        using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        ThrowForStatus(response);
        await using Stream body = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[256 * 1024];
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long total = 0, warm = 0;
        double warmSeconds = 0;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(time);
        try
        {
            int read;
            while (total < maxBytes && (read = await body.ReadAsync(buffer, stop.Token)) > 0)
            {
                total += read;
                if (warmSeconds == 0 && clock.Elapsed.TotalSeconds >= 0.5) { warm = total; warmSeconds = clock.Elapsed.TotalSeconds; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }   // time is up
        double seconds = clock.Elapsed.TotalSeconds - warmSeconds;
        return seconds > 0.2 ? (total - warm) / seconds : total / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
    }

    // A connection that stops sending for a minute is dropped, so the download can be resumed
    static async Task<int> ReadWithTimeoutAsync(Stream body, Memory<byte> buffer, CancellationToken token)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            return await body.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The download stalled. Press Update again to resume it.");
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Accepts a server certificate: the pinned one when a fingerprint is set, otherwise only a normally trusted one.</summary>
public static class CertificatePin
{
    public static bool Accept(X509Certificate? certificate, SslPolicyErrors errors, string? pinnedSha256)
    {
        if (pinnedSha256 is null)
            return errors == SslPolicyErrors.None;
        // pinned: exactly this certificate, whether or not Windows trusts it (self-signed, an IP address as its name...)
        return certificate is not null && Fingerprint(certificate) == pinnedSha256;
    }

    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
