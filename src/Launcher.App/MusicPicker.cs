using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Music on the Screens page: the login screen music (one track, or each scene's own) and the character screen music per
/// race (the music of the scene shown, or a track the player picks). Every track has a ▶ that plays a short clip
/// (previews/music/&lt;code&gt;.mp3 on the server).
/// </summary>
public partial class MainWindow
{
    const string OwnMusic = "";

    /// <summary>Tracks with a clip on the server: (code, name), names from loginui.lua's scene list.</summary>
    List<(string Code, string Name)> MusicTracks()
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        try { foreach (LoginScene scene in LoginScreens.Read(Folder)) names[scene.Code] = LoginScreens.Title(scene.Name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return [.. (_manifest?.MusicPreviews.Keys ?? Enumerable.Empty<string>())
            .Select(c => (c, names.TryGetValue(c, out string? n) ? n : c.ToUpperInvariant()))
            .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A choice box of tracks with a ▶ next to it.</summary>
    Control MusicChoice(string ownText, string current, Action<string> picked)
    {
        List<(string Code, string Name)> tracks = MusicTracks();
        List<string> items = [ownText, .. tracks.Select(t => t.Name)];
        ComboBox box = new() { ItemsSource = items, MinWidth = 260, FontSize = 12 };
        int index = tracks.FindIndex(t => t.Code.Equals(current, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = index < 0 ? 0 : index + 1;
        Button play = new() { Content = "▶", FontSize = 12, Padding = new Thickness(10, 2), Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(play, "Play a short part of this music (click again to stop)");
        string Code() => box.SelectedIndex > 0 ? tracks[box.SelectedIndex - 1].Code : OwnMusic;
        void Update() => play.IsEnabled = Code().Length > 0;
        box.SelectionChanged += (_, _) => { Update(); AudioPreview.Stop(); picked(Code()); };
        play.Click += async (_, _) =>
        {
            if (AudioPreview.IsPlaying(Code())) { AudioPreview.Stop(); return; }
            if (_manifest?.MusicPreviews.TryGetValue(Code(), out string? url) != true) return;
            try
            {
                using ServerClient client = NewClient();
                byte[]? clip = await client.GetBytesAsync(url!);
                if (clip is null) { Status("Couldn't load that music clip."); return; }
                if (!AudioPreview.Play(Code(), clip)) Status("No audio player found to play the clip (on Linux: install ffplay or mpg123).");
            }
            catch (Exception error) { Logger.Warning("Couldn't play the music clip", error); }
        };
        Update();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { box, play } };
    }

    /// <summary>Under the login screen backgrounds: one music for the login screen.</summary>
    void BuildLoginMusic(Panel target)
    {
        target.Children.Add(new TextBlock { Classes = { "section" }, Text = "MUSIC", Margin = new Thickness(0, 14, 0, 4) });
        target.Children.Add(new TextBlock
        {
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "The login screen's music: each background's own, or one you pick for all of them.",
        });
        target.Children.Add(MusicChoice("Each background's own music", _settings.LoginMusic, code =>
        {
            _settings.LoginMusic = code;
            SaveSettings();
            ApplyLoginMusic(Folder);
        }));
    }

    /// <summary>Under the character screen scenes: music per race.</summary>
    void BuildCharacterMusic(Panel target)
    {
        target.Children.Add(new TextBlock { Classes = { "section" }, Text = "MUSIC", Margin = new Thickness(0, 14, 0, 4) });
        target.Children.Add(new TextBlock
        {
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Each race gets the music of the scene behind it. Pick other music for a race here (the Mods page's preset turns Custom).",
        });
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        int row = 0;
        foreach (CharacterRace race in CharacterScenes.Races)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock name = new()
            {
                Text = race.Label + (race.Faction is "Alliance" or "Horde" ? $" ({race.Faction})" : ""), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 12, 2),
            };
            name.Foreground = new SolidColorBrush(FactionColor(race.Faction));
            string key = race.Key;
            Control choice = MusicChoice("Music of the scene shown", _settings.MusicPicks.GetValueOrDefault(key, ""), code =>
            {
                if (code.Length == 0) _settings.MusicPicks.Remove(key); else _settings.MusicPicks[key] = code;
                SaveSettings();
                ApplyCharacterScenes(Folder);
            });
            choice.Margin = new Thickness(0, 2);
            Grid.SetRow(name, row); Grid.SetRow(choice, row); Grid.SetColumn(choice, 1);
            grid.Children.Add(name);
            grid.Children.Add(choice);
            row++;
        }
        target.Children.Add(grid);
    }

    /// <summary>Interface\loginui.lua's LoginMusic: the player's pick (kept after updates, like the backgrounds).</summary>
    void ApplyLoginMusic(string folder)
    {
        if (_manifest is null || !GameFolder.LooksLikeClient(folder)) return;
        try
        {
            if (!LoginScreens.WriteMusic(folder, _settings.LoginMusic)) return;
            if (_manifest.Files.FirstOrDefault(f => f.Path.Equals(LoginScreens.FilePath, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                HashCache cache = HashCache.Load(_cacheFile);
                cache.Remember(LoginScreens.PathIn(folder), file.Sha256);
                cache.Save();
            }
            Log(_settings.LoginMusic.Length > 0 ? $"Login screen music: {_settings.LoginMusic}" : "Login screen music: each background's own");
        }
        catch (Exception error) { Logger.Warning("Couldn't set the login screen music", error); }
    }
}

/// <summary>Plays a music clip: Windows' own player (MCI) on Windows, else a command line player if one is installed.</summary>
static class AudioPreview
{
    static string? _playing;
    static Process? _process;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr callback);

    public static bool IsPlaying(string code) => _playing == code && (_process is null ? OperatingSystem.IsWindows() : !_process.HasExited);

    public static bool Play(string code, byte[] clip)
    {
        Stop();
        string file = Path.Combine(Path.GetTempPath(), $"temucraft-music-{code}.mp3");
        File.WriteAllBytes(file, clip);
        if (OperatingSystem.IsWindows())
        {
            if (mciSendString($"open \"{file}\" type mpegvideo alias temucraftmusic", null, 0, IntPtr.Zero) != 0) return false;
            mciSendString("play temucraftmusic", null, 0, IntPtr.Zero);
            _playing = code;
            return true;
        }
        foreach ((string exe, string[] args) in new (string, string[])[]
                 {
                     ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet"]), ("mpg123", ["-q"]), ("afplay", []),
                     ("mpv", ["--no-video", "--really-quiet"]),
                 })
        {
            try
            {
                ProcessStartInfo start = new(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in args) start.ArgumentList.Add(a);
                start.ArgumentList.Add(file);
                _process = Process.Start(start);
                if (_process is not null) { _playing = code; return true; }
            }
            catch (System.ComponentModel.Win32Exception) { }   // not installed: try the next one
        }
        return false;
    }

    public static void Stop()
    {
        if (OperatingSystem.IsWindows())
        {
            mciSendString("stop temucraftmusic", null, 0, IntPtr.Zero);
            mciSendString("close temucraftmusic", null, 0, IntPtr.Zero);
        }
        try { if (_process is { HasExited: false }) _process.Kill(); } catch (InvalidOperationException) { }
        _process?.Dispose();
        _process = null;
        _playing = null;
    }
}
