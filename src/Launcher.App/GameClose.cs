using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The game keeps its MPQ files open while it runs, so updating or switching mod choices then fails. Before that work
/// the launcher looks for the game and offers to close it.
/// </summary>
public partial class MainWindow
{
    /// <summary>The game processes started from this WoW folder (any Wow.exe when the path can't be read).</summary>
    static List<Process> GameProcesses(string folder)
    {
        List<Process> found = [];
        string exe = Path.GetFullPath(Path.Combine(folder, "Wow.exe"));
        foreach (Process p in Process.GetProcessesByName("Wow"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch (Exception) { }
            if (path is null || string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase)) found.Add(p);
            else p.Dispose();
        }
        return found;
    }

    /// <summary>A yes / Cancel question in a small window; true for yes.</summary>
    async Task<bool> ConfirmAsync(string title, string text, string yesText)
    {
        bool yes = false;
        Window ask = new()
        {
            Title = "Temucraft", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button ok = new() { Content = Translation.T(yesText) }, cancel = new() { Content = Translation.T("Cancel") };
        ok.Click += (_, _) => { yes = true; ask.Close(); };
        cancel.Click += (_, _) => ask.Close();
        ask.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = Translation.T(title), FontSize = 16, FontWeight = FontWeight.Bold },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        await ask.ShowDialog(this);
        return yes;
    }

    /// <summary>True when the game isn't running (any more). When it is, asks whether to close it first.</summary>
    async Task<bool> MakeSureGameClosedAsync(string folder)
    {
        if (!OperatingSystem.IsWindows() || folder.Length == 0) return true;
        List<Process> running = GameProcesses(folder);
        if (running.Count == 0) return true;
        bool close = false;
        Window ask = new()
        {
            Title = "Temucraft",
            Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button yes = new() { Content = Translation.T("Close the game"), Classes = { "accent" } };
        Button no = new() { Content = Translation.T("Cancel") };
        yes.Click += (_, _) => { close = true; ask.Close(); };
        no.Click += (_, _) => ask.Close();
        ask.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = Translation.T("World of Warcraft is running"), FontSize = 16, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = Translation.T("The game keeps its files open, so they can't be changed while it runs. Close it now? Anything not saved in the game (an open chat line, a character you were making) is lost."),
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } },
            },
        };
        await ask.ShowDialog(this);
        if (!close)
        {
            foreach (Process p in running) p.Dispose();
            Status(Translation.T("Close World of Warcraft first, then try again."));
            return false;
        }
        foreach (Process p in running)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.CloseMainWindow();
                    if (!await Task.Run(() => p.WaitForExit(5000))) { p.Kill(); await Task.Run(() => p.WaitForExit(5000)); }
                }
            }
            catch (Exception error) { Logger.Warning("Couldn't close the game", error); }
            finally { p.Dispose(); }
        }
        if (GameProcesses(folder) is { Count: > 0 } still)
        {
            foreach (Process p in still) p.Dispose();
            Status(Translation.T("The game is still running. Close it, then try again."));
            return false;
        }
        Logger.Info("Closed the game before changing its files");
        return true;
    }
}
