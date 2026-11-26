namespace Launcher.Core;

/// <summary>What a different launcher on the server means for this one.</summary>
public enum LauncherUpdateKind
{
    /// <summary>The server's launcher is this one (or the server has none).</summary>
    None,
    /// <summary>A new launcher is available (launcherUpdate "recommended" for the picked realm): everything keeps working.</summary>
    Optional,
    /// <summary>The server needs the new launcher to play (launcherUpdate "required", the default): PLAY waits for it.</summary>
    Required,
}

/// <summary>
/// The "There's an update" window for one run of the launcher. It always asks (Update now / Later), required or not:
/// "Later" closes it until the next start, or until the server hands out yet another launcher. Everything that doesn't
/// need the new launcher keeps working after "Later"; when the update is required, PLAY doesn't start the game and
/// opens the window again instead (the server's launcherUpdate rule is what says the old launcher can't play there).
/// </summary>
public sealed class LauncherUpdatePrompt
{
    string _laterSha = "";

    public LauncherUpdateKind Kind { get; private set; }

    /// <summary>The SHA-256 of the server's launcher being offered.</summary>
    public string Sha { get; private set; } = "";

    public static LauncherUpdateKind KindOf(bool differs, LauncherPolicy policy, int realmId) =>
        !differs ? LauncherUpdateKind.None : policy.LauncherUpdateRequired(realmId) ? LauncherUpdateKind.Required : LauncherUpdateKind.Optional;

    /// <summary>What the server hands out now; returns whether to open the window now (an update that wasn't put off).</summary>
    public bool Offer(LauncherUpdateKind kind, string sha)
    {
        Kind = kind;
        Sha = kind == LauncherUpdateKind.None ? "" : sha;
        return kind != LauncherUpdateKind.None && !PutOff;
    }

    /// <summary>"Later" was pressed for the launcher on offer (a newer one asks again).</summary>
    public bool PutOff => Kind != LauncherUpdateKind.None && _laterSha.Length > 0 && string.Equals(_laterSha, Sha, StringComparison.OrdinalIgnoreCase);

    public void Later() => _laterSha = Sha;

    /// <summary>The game can't be played until the launcher is updated.</summary>
    public bool BlocksPlay => Kind == LauncherUpdateKind.Required;

    /// <summary>The small "New launcher" button in the header, which opens the window again.</summary>
    public bool ShowsBadge => Kind != LauncherUpdateKind.None;

    /// <summary>The window's text, in English (the key for <see cref="Translation.T"/>).</summary>
    public static string Message(LauncherUpdateKind kind) => kind == LauncherUpdateKind.Required
        ? "A new launcher is needed to keep playing on this server. It takes a few seconds and keeps your settings."
        : "A new launcher is available. It takes a few seconds and keeps your settings.";

    /// <summary>What "Later" means, in English; null when nothing stops working.</summary>
    public static string? LaterNote(LauncherUpdateKind kind) => kind == LauncherUpdateKind.Required
        ? "Later: you can still change settings, mods and game files, but you can't play on this server until the launcher is updated."
        : null;
}

/// <summary>The progress line of the launcher download: "45%  ·  12.3 MB / 27.0 MB  ·  4.1 MB/s".</summary>
public sealed class SelfUpdateProgress
{
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    long _firstDone = -1;

    public long Done { get; private set; }
    public long Total { get; private set; }

    /// <summary>Bytes per second since the first report (a resumed download starts its count there); 0 while unknown.</summary>
    public double Speed { get; private set; }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);

    public int Percent => (int)(Fraction * 100);

    public void Report(long done, long total) => Report(done, total, _clock.Elapsed);

    /// <summary>The same, with the time since the download started (for tests).</summary>
    public void Report(long done, long total, TimeSpan elapsed)
    {
        if (_firstDone < 0) _firstDone = done;
        Done = done;
        Total = total;
        Speed = elapsed.TotalSeconds >= 0.5 ? Math.Max(0, done - _firstDone) / elapsed.TotalSeconds : 0;
    }

    public string Text()
    {
        string text = $"{Percent}%  ·  {Words.ShortSize(Done)} / {Words.ShortSize(Total)}";
        return Speed > 0 ? text + "  ·  " + SpeedLimit.Format(Speed, bits: false) : text;
    }
}
