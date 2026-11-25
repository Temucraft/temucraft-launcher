using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Shift + left click on PLAY: the game starts in debug mode this time only, whatever the Mods page says. The first
/// time the player has to tick that debug mode is meant for the PTR and developers. The launcher puts debug mode's
/// file (TemucraftDebug.txt) and settings in for that game and takes them out again when it closes.
/// </summary>
public partial class MainWindow
{
    /// <summary>The first-time question; true to go on. Asked once: the tick is remembered.</summary>
    async Task<bool> AskDebugPlayAsync()
    {
        if (_settings.DebugPlayAcknowledged) return true;
        bool go = false;
        Window ask = new()
        {
            Title = "Temucraft", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        CheckBox understood = new()
        {
            Content = new TextBlock
            {
                Text = Translation.T("I understand: debug mode is meant for the PTR and for developers. Outside of that it isn't intended, and the game can behave inconsistently."),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Button ok = new() { Content = Translation.T("Play in debug mode"), IsEnabled = false };
        Button cancel = new() { Content = Translation.T("Cancel") };
        understood.IsCheckedChanged += (_, _) => ok.IsEnabled = understood.IsChecked == true;
        ok.Click += (_, _) => { go = true; ask.Close(); };
        cancel.Click += (_, _) => ask.Close();
        ask.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = Translation.T("Play in debug mode?"), FontSize = 16, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = Translation.T("Shift + left click on PLAY starts the game in debug mode, this time only: every interface error is logged to Logs\\TemucraftDebug.log, error popups and the taint log are on, and the ` key opens the game's console. Debug mode goes back off when the game closes."),
                    TextWrapping = TextWrapping.Wrap,
                },
                understood,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        await ask.ShowDialog(this);
        if (go)
        {
            _settings.DebugPlayAcknowledged = true;
            SaveSettings();
        }
        return go;
    }

    /// <summary>Puts debug mode's file next to Wow.exe for one game, and remembers to take it out again.</summary>
    void StartOneOffDebug(string folder)
    {
        File.WriteAllText(Path.Combine(folder, DebugMode.Marker),
            "Debug mode for this game only (Shift + PLAY in the Temucraft launcher).\r\n" +
            "The launcher removes this file when the game closes.\r\n");
        _settings.OneOffDebugFolder = folder;
        _settings.OneOffDebugPtr = IsPtr;
        SaveSettings();
        Log(Translation.T("Debug mode for this game only (Shift + PLAY)"));
    }

    /// <summary>Takes a one-off debug mode back out: its file, and the Config.wtf settings the launcher turned on for
    /// it. Does nothing when there is none, or while that game still runs (it is retried at the next PLAY).</summary>
    void EndOneOffDebug()
    {
        string folder = _settings.OneOffDebugFolder;
        if (folder.Length == 0) return;
        if (Directory.Exists(folder) && GameRunning(folder)) return;
        try
        {
            if (Directory.Exists(folder))
            {
                string marker = Path.Combine(folder, DebugMode.Marker);
                if (File.Exists(marker)) File.Delete(marker);
                // the PTR keeps its own "settings applied" flag (its own Config.wtf)
                bool ptr = _settings.OneOffDebugPtr;
                bool applied = ptr ? _settings.PtrDebugSettingsApplied : _settings.DebugSettingsApplied;
                DebugMode.Apply(folder, false, ref applied);
                if (ptr) _settings.PtrDebugSettingsApplied = applied; else _settings.DebugSettingsApplied = applied;
            }
            _settings.OneOffDebugFolder = "";
            SaveSettings();
            Log(Translation.T("Debug mode taken back out (it was on for one game only)"));
        }
        catch (Exception e) { Logger.Warning("Couldn't take the one-off debug mode back out", e); }
        if (folder == Folder) ShowPlayState();
    }
}
