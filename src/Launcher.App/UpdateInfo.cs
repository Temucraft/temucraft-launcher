using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// "Update info": before an update, every change it makes. Files to download (new or changed, with their size),
/// files that come back from the kept folder, addons, files the server removed, and untracked files in the game
/// folder that can be moved away. Shown when Update is pressed unless "Don't ask again" was ticked.
/// </summary>
public partial class MainWindow
{
    sealed record PendingFile(string Path, long Size, bool New, bool Kept);
    sealed record PendingUpdate(string Folder, List<PendingFile> Files, List<string> Addons, List<string> Removed, long DownloadSize)
    {
        public int Count => Files.Count + Addons.Count + Removed.Count;
    }

    PendingUpdate? _pendingUpdate;

    /// <summary>Called by the check (any thread): what an update would do now.</summary>
    static PendingUpdate DescribeUpdate(string folder, Manifest manifest, IReadOnlyList<PlannedFile> outdated, IEnumerable<CatalogAddon> addons)
    {
        List<PendingFile> files = outdated
            .Select(p => new PendingFile(p.File.Path, p.File.Size, !File.Exists(p.Target), KeptFiles.Has(folder, p.File.Sha256)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> removed = manifest.Obsolete
            .Where(path => File.Exists(Path.Combine(folder, path)) || Directory.Exists(Path.Combine(folder, path))).ToList();
        long size = files.Where(f => !f.Kept).Sum(f => f.Size);
        return new PendingUpdate(folder, files, addons.Select(a => a.Name).ToList(), removed, size);
    }

    void SetPendingUpdate(PendingUpdate? update)
    {
        _pendingUpdate = update is { Count: > 0 } ? update : null;
        UpdateInfoButton.IsVisible = _pendingUpdate is not null;
    }

    void OnUpdateInfo(object? sender, RoutedEventArgs e) => ShowUpdateInfo(fromUpdate: false);

    void ShowUpdateInfo(bool fromUpdate)
    {
        if (_pendingUpdate is not { } update) return;
        UpdateInfoFolder.Text = update.Folder;
        UpdateInfoSummary.Text = string.Format(Translation.T("{0} to download, {1}."), Words.Files(update.Files.Count(f => !f.Kept)), Words.Size(update.DownloadSize));
        UpdateInfoSkip.IsChecked = _settings.SkipUpdateInfo;
        UpdateInfoContinue.Content = Translation.T(fromUpdate ? "Continue" : "Update now");
        UpdateInfoList.Children.Clear();
        foreach (PendingFile f in update.Files)
            UpdateInfoList.Children.Add(InfoRow(f.Kept ? "Restore" : "Download", f.Kept ? "#7FB2E5" : "#8FD16A", f.Path,
                f.Kept ? Translation.T("kept on your PC, no download") : $"{Translation.T(f.New ? "new" : "changed")}  ·  {Words.ShortSize(f.Size)}"));
        foreach (string addon in update.Addons)
            UpdateInfoList.Children.Add(InfoRow("Addon", "#8FD16A", addon, Translation.T("installed or updated"), isPath: false));
        foreach (string path in update.Removed)
            UpdateInfoList.Children.Add(InfoRow("Remove", "#E57373", path, Translation.T("no longer on the server")));
        AddUntrackedRows(update.Folder);
        UpdateInfoOverlay.IsVisible = true;
    }

    void AddUntrackedRows(string folder)
    {
        if (_manifest is null || !GameFolder.LooksLikeClient(folder)) return;
        foreach (string file in Untracked.Find(folder, _manifest))
        {
            Control row = InfoRow("Untracked", "#E5B24C", file, "");
            Button remove = new() { Content = "🗑", FontSize = 12, Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, Translation.T("Move it to the backup folder (it is not part of the server's files)"));
            remove.Click += (_, _) =>
            {
                try
                {
                    Untracked.Remove(folder, file);
                    Log($"Moved untracked {file} to the backup folder");
                    UpdateInfoList.Children.Remove(row);
                    RefreshUntracked();
                    RefreshUntrackedButton();
                }
                catch (Exception error) { Status(error.Message); Logger.Error($"Moving {file}", error); }
            };
            ((StackPanel)((Grid)row).Children[1]).Children.Add(remove);
            UpdateInfoList.Children.Add(row);
        }
    }

    static Control InfoRow(string label, string color, string path, string detail, bool isPath = true)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = Translation.T(label), Width = 78, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center,
        });
        StackPanel right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TextBlock name = new()
        {
            Text = isPath ? "/" + path.TrimStart('/') : path, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (IBrush?)Application.Current!.FindResource("T.text"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 470,
        };
        ToolTip.SetTip(name, path);
        right.Children.Add(name);
        if (detail.Length > 0)
            right.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (IBrush?)Application.Current!.FindResource("T.dim"),
            });
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    async void OnUpdateInfoContinue(object? sender, RoutedEventArgs e)
    {
        _settings.SkipUpdateInfo = UpdateInfoSkip.IsChecked == true;
        SaveSettings();
        UpdateInfoOverlay.IsVisible = false;
        await CheckAsync(download: true);
    }

    void OnUpdateInfoClose(object? sender, RoutedEventArgs e)
    {
        _settings.SkipUpdateInfo = UpdateInfoSkip.IsChecked == true;
        SaveSettings();
        UpdateInfoOverlay.IsVisible = false;
    }
}
