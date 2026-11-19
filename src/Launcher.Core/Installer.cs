namespace Launcher.Core;

/// <summary>
/// Installs the outdated files of a plan. A file whose content is already on disk is copied instead of downloaded: a
/// file the hash cache knows has the same hash (another path in the same update, a font under a second name...), or
/// the same-named file of another installed locale (lichking-locale-enUS.MPQ for lichking-locale-deDE.MPQ). Every
/// copy is checked against the manifest's hash like a download, so a wrong copy is never installed.
/// </summary>
public static class UpdateInstaller
{
    public sealed record Result(int Downloaded, int Copied, long BytesDownloaded, long BytesCopied);

    /// <param name="onFile">Called before each file: the file, the bytes already done, and whether it is a copy.</param>
    /// <param name="progress">Download progress of the current file.</param>
    public static async Task<Result> InstallAsync(ServerClient client, IReadOnlyList<PlannedFile> outdated, HashCache cache,
        string? wowFolder = null, Action<PlannedFile, long, bool>? onFile = null,
        Func<PlannedFile, long, IProgress<(long Done, long Total)>?>? progress = null, CancellationToken token = default)
    {
        int downloaded = 0, copied = 0;
        long bytesDownloaded = 0, bytesCopied = 0, before = 0;
        foreach (PlannedFile planned in outdated)
        {
            token.ThrowIfCancellationRequested();
            if (await TryCopyAsync(planned, cache, wowFolder, token) is { } source)
            {
                onFile?.Invoke(planned, before, true);
                Logger.Info($"Copied {planned.File.Path} ({copied + downloaded + 1}/{outdated.Count}) from {source} (the same file, no download needed)");
                copied++;
                bytesCopied += planned.File.Size;
            }
            else
            {
                onFile?.Invoke(planned, before, false);
                await client.DownloadAsync(planned.File, planned.Target, progress?.Invoke(planned, before), token);
                cache.Remember(planned.Target, planned.File.Sha256);
                Logger.Info($"Installed {planned.File.Path} ({copied + downloaded + 1}/{outdated.Count}, {Words.Size(planned.File.Size)})");
                downloaded++;
                bytesDownloaded += planned.File.Size;
            }
            before += planned.File.Size;
        }
        return new Result(downloaded, copied, bytesDownloaded, bytesCopied);
    }

    /// <summary>Copies the file from a local file with the same content; returns where it came from, or null.</summary>
    public static async Task<string?> TryCopyAsync(PlannedFile planned, HashCache cache, string? wowFolder, CancellationToken token)
    {
        ManifestFile file = planned.File;
        if (file.Size == 0) return null;
        foreach (string source in await CandidatesAsync(planned, cache, wowFolder, token))
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(planned.Target), StringComparison.OrdinalIgnoreCase))
                continue;
            string part = planned.Target + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(planned.Target)!);
                File.Copy(source, part, overwrite: true);
                if (await Hashing.Sha256Async(part, token: token) != file.Sha256)
                {
                    File.Delete(part);
                    continue;   // the source changed after it was checked: try the next one, or download
                }
                File.Move(part, planned.Target, overwrite: true);
                cache.Remember(planned.Target, file.Sha256);
                return source;
            }
            catch (IOException e)
            {
                Logger.Warning($"Couldn't copy {source} to {planned.Target}; downloading it instead", e);
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            }
        }
        return null;
    }

    static async Task<List<string>> CandidatesAsync(PlannedFile planned, HashCache cache, string? wowFolder, CancellationToken token)
    {
        ManifestFile file = planned.File;
        List<string> found = cache.FilesWith(file.Sha256, file.Size).ToList();
        if (found.Count > 0 || wowFolder is null || file.Category != FileCategory.Locale || file.Locale is null)
            return found;
        // the same file name in another installed locale, e.g. Data/enUS/lichking-locale-enUS.MPQ for Data/deDE/lichking-locale-deDE.MPQ
        foreach (string other in GameFolder.InstalledLocales(wowFolder))
        {
            if (other.Equals(file.Locale, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = file.Path.Replace(file.Locale, other, StringComparison.OrdinalIgnoreCase);
            string candidate;
            try { candidate = PathRules.Resolve(wowFolder, relative); }
            catch (InvalidDataException) { continue; }
            if (File.Exists(candidate) && new FileInfo(candidate).Length == file.Size
                && await cache.GetAsync(candidate, token: token) == file.Sha256)
                found.Add(candidate);
        }
        return found;
    }
}
