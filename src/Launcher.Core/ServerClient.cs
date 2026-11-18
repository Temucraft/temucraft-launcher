using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Launcher.Core;

public sealed class LoginFailedException(string message) : Exception(message);

/// <summary>
/// Talks to the Apache folder: the manifest and the files, with the username and password Apache asks for (HTTP
/// basic auth, over https). Apache's IP whitelist is checked before the password, so an address that isn't on it gets
/// 403 Forbidden.
/// </summary>
public sealed class ServerClient : IDisposable
{
    readonly HttpClient _http;
    readonly Uri _base;

    public ServerClient(ServerSettings settings, string username, string password, HttpMessageHandler? handler = null)
    {
        _base = settings.ServerUrl;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // downloads take as long as they take; reads time out below
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TemucraftLauncher/1.0");
    }

    public async Task<Manifest> GetManifestAsync(CancellationToken token = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, "manifest.json"), timeout.Token);
        ThrowForStatus(response);
        return Manifest.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <summary>A small text file next to the manifest (news.json, addons.json...); null when the server has none.</summary>
    public async Task<string?> GetTextAsync(string relative, CancellationToken token = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, relative), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowForStatus(response);
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    /// <summary>The file's address, with each part of its path escaped (names may hold spaces or '#').</summary>
    public Uri FileUri(ManifestFile file) =>
        new(_base, string.Join('/', file.Url.Split('/').Select(Uri.EscapeDataString)));

    static void ThrowForStatus(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new LoginFailedException("Wrong username or password.");
            case HttpStatusCode.Forbidden:
                throw new LoginFailedException("The server doesn't accept connections from your IP address.");
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

        if (have < file.Size)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, FileUri(file));
            if (have > 0)
                request.Headers.Range = new RangeHeaderValue(have, null);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            ThrowForStatus(response);
            // A server that ignores the range sends the whole file again
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                have = 0;

            await using Stream body = await response.Content.ReadAsStreamAsync(token);
            await using FileStream output = new(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20, useAsync: true);
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = await ReadWithTimeoutAsync(body, buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                have += read;
                progress?.Report((have, file.Size));
            }
        }

        string sha = await Hashing.Sha256Async(part, token: token);
        if (sha != file.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"{file.Path} didn't download correctly (checksum mismatch). Try again.");
        }
        File.Move(part, target, overwrite: true);
    }

    // A connection that stops sending for a minute is dropped, so the download can be resumed
    static async Task<int> ReadWithTimeoutAsync(Stream body, byte[] buffer, CancellationToken token)
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
