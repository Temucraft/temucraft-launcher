using System.Diagnostics;
using System.Globalization;

namespace Launcher.Core;

/// <summary>
/// The download speed cap: the player's own limit, kept under the server's cap ("downloadLimit" in
/// launcher-policy.json, in KB/s; 0 or -1 = no cap). Downloads wait a little after each read to stay under it.
/// </summary>
public static class SpeedLimit
{
    static readonly object Gate = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double _allowanceBytes;
    static long _lastTicks;

    /// <summary>Bytes per second; 0 = no limit.</summary>
    public static long BytesPerSecond { get; private set; }

    /// <summary>The limit to use in KB/s: the player's (when on), never above the server's cap; 0 = none.</summary>
    public static long EffectiveKBps(bool playerOn, long playerKBps, long serverKBps)
    {
        long server = serverKBps > 0 ? serverKBps : 0;
        long player = playerOn && playerKBps > 0 ? playerKBps : 0;
        return server == 0 ? player : player == 0 ? server : Math.Min(player, server);
    }

    public static void Set(long kiloBytesPerSecond)
    {
        lock (Gate)
        {
            BytesPerSecond = Math.Max(0, kiloBytesPerSecond) * 1024;
            _allowanceBytes = 0;
            _lastTicks = Clock.ElapsedTicks;
        }
    }

    /// <summary>How long to wait after reading <paramref name="bytes"/> (a token bucket of one second).</summary>
    public static TimeSpan Delay(int bytes)
    {
        lock (Gate)
        {
            long limit = BytesPerSecond;
            if (limit <= 0) return TimeSpan.Zero;
            long now = Clock.ElapsedTicks;
            _allowanceBytes = Math.Min(limit, _allowanceBytes + (now - _lastTicks) / (double)Stopwatch.Frequency * limit);
            _lastTicks = now;
            _allowanceBytes -= bytes;
            return _allowanceBytes >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_allowanceBytes / limit);
        }
    }

    public static async Task WaitAsync(int bytes, CancellationToken token)
    {
        TimeSpan wait = Delay(bytes);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
    }

    /// <summary>The limit a speed test suggests: 80% of the measured speed (room for everything else), at least
    /// 100 KB/s, never above the server's cap.</summary>
    public static long Suggest(double measuredBytesPerSecond, long serverCapKBps)
    {
        long kb = Math.Max(100, (long)(measuredBytesPerSecond * 0.8 / 1024 / 100) * 100);   // rounded down to 100 KB/s
        return serverCapKBps > 0 ? Math.Min(kb, serverCapKBps) : kb;
    }

    /// <summary>A speed for the status line: "4.2 MB/s", or in bits "33.6 Mbit/s".</summary>
    public static string Format(double bytesPerSecond, bool bits)
    {
        double value = bits ? bytesPerSecond * 8 : bytesPerSecond;
        string[] units = bits ? ["bit/s", "kbit/s", "Mbit/s", "Gbit/s"] : ["B/s", "KB/s", "MB/s", "GB/s"];
        double step = bits ? 1000 : 1024;
        int i = 0;
        while (value >= step && i < units.Length - 1) { value /= step; i++; }
        return value.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    /// <summary>"3 min left", "40 s left", "2 h 5 min left"; empty when unknown.</summary>
    public static string TimeLeft(long bytesLeft, double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || bytesLeft <= 0) return "";
        TimeSpan left = TimeSpan.FromSeconds(Math.Min(bytesLeft / bytesPerSecond, TimeSpan.FromDays(30).TotalSeconds));
        if (left.TotalHours >= 1) return string.Format(Translation.T("{0} h {1} min left"), (int)left.TotalHours, left.Minutes);
        if (left.TotalMinutes >= 1) return string.Format(Translation.T("{0} min left"), (int)Math.Ceiling(left.TotalMinutes));
        return string.Format(Translation.T("{0} s left"), Math.Max(1, (int)left.TotalSeconds));
    }
}

/// <summary>The download speed over the last few seconds, for the status line.</summary>
public sealed class SpeedMeter
{
    readonly Queue<(long Ticks, long Bytes)> _samples = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Adds the total bytes done so far; returns bytes per second over the last 5 seconds.</summary>
    public double Add(long totalBytes)
    {
        long now = _clock.ElapsedTicks;
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Ticks > 5 * Stopwatch.Frequency) _samples.Dequeue();
        (long firstTicks, long firstBytes) = _samples.Peek();
        double seconds = (now - firstTicks) / (double)Stopwatch.Frequency;
        return seconds < 0.25 ? 0 : (totalBytes - firstBytes) / seconds;
    }
}

/// <summary>Automatic updates only between two hours (e.g. 2 to 5 at night). The same hour twice means all day.</summary>
public static class UpdateHours
{
    public static bool Inside(int hour, int from, int to)
    {
        from = ((from % 24) + 24) % 24;
        to = ((to % 24) + 24) % 24;
        if (from == to) return true;
        return from < to ? hour >= from && hour < to : hour >= from || hour < to;
    }
}

/// <summary>Counts and sizes in plain words: "1 file", "188 files", "16.6 GB (17029.3 MB)".</summary>
public static class Words
{
    /// <summary>"1 file" / "2 files" (translated; the file has "1 file" and "{0} files").</summary>
    public static string Files(long count) =>
        count == 1 ? Translation.T("1 file") : string.Format(Translation.T("{0} files"), count);

    public static string Addons(long count) =>
        count == 1 ? Translation.T("1 addon") : string.Format(Translation.T("{0} addons"), count);

    static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"512 KB", "312.4 MB", or "16.6 GB (17029.3 MB)" from 1 GB up.</summary>
    public static string Size(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (bytes < 1048576) return $"{Math.Ceiling(bytes / 1024.0):0} KB";
        if (mb < 1024) return $"{Number(mb)} MB";
        return $"{Number(mb / 1024)} GB ({Number(mb)} MB)";
    }

    /// <summary>"312.4 MB" or "16.6 GB": for progress, where the MB in brackets would be too long.</summary>
    public static string ShortSize(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (bytes < 1048576) return $"{Math.Ceiling(bytes / 1024.0):0} KB";
        return mb < 1024 ? $"{Number(mb)} MB" : $"{Number(mb / 1024)} GB";
    }
}

/// <summary>How big each part of an install is, from the manifest: the game, the server's required files, each
/// language, each option, the addons, and the total for a choice.</summary>
public static class InstallSizes
{
    public static long Of(Manifest manifest, FileCategory category) => manifest.Files.Where(f => f.Category == category).Sum(f => f.Size);

    public static long Locale(Manifest manifest, string locale) =>
        manifest.Files.Where(f => f.Category == FileCategory.Locale && string.Equals(f.Locale, locale, StringComparison.OrdinalIgnoreCase)).Sum(f => f.Size);

    public static long Option(Manifest manifest, string group, string option) =>
        manifest.Files.Where(f => f.Category == FileCategory.Option && f.Group == group && string.Equals(f.Option, option, StringComparison.OrdinalIgnoreCase)).Sum(f => f.Size);

    /// <summary>Everything the choice installs (the game too when it has a base folder); a file used twice counts once.</summary>
    public static long Total(Manifest manifest, InstallChoice choice) =>
        UpdatePlanner.Wanted(manifest, choice).DistinctBy(p => p.Target, StringComparer.OrdinalIgnoreCase).Sum(p => p.File.Size);

    /// <summary>Free space on the drive of a folder (the folder may not exist yet); null when it can't be told.</summary>
    public static long? FreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            // the deepest existing folder on the way (on Linux the "drive" is a mount point)
            string probe = Path.GetFullPath(folder);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { } up) probe = up;
            DriveInfo best = DriveInfo.GetDrives().Where(d => d.IsReady && probe.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                                       .MaxBy(d => d.RootDirectory.FullName.Length) ?? new DriveInfo(root);
            return best.AvailableFreeSpace;
        }
        catch (Exception) { return null; }
    }
}
