using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// "Recent downloads": every file an update installs, with its name, whether it is new or changed, how much of it is
/// done, the time left and a bar, then Done. The last 60 files are kept between launches.
/// </summary>
public partial class MainWindow
{
    /// <summary>Digits drawn at one width, so changing numbers don't shift the text (OpenType tnum).</summary>
    static readonly FontFeatureCollection TabularDigits = new() { FontFeature.Parse("tnum") };

    enum RowState { Waiting, Downloading, Copying, Done, Copied, Failed, Stopped }

    sealed class DownloadEntry
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public bool New { get; set; }
        public RowState State { get; set; }
        public DateTime When { get; set; } = DateTime.Now;
    }

    sealed class DownloadRow(DownloadEntry entry)
    {
        public DownloadEntry Entry { get; } = entry;
        public Control? View;
        public TextBlock? Detail;
        public ProgressBar? Bar;
    }

    const int KeptRows = 60;
    readonly string _downloadsFile = Path.Combine(UserSettings.DefaultFolder, "download-history.json");
    readonly List<DownloadRow> _downloadRows = [];
    bool _downloadsLoaded;

    void LoadDownloadHistory()
    {
        if (_downloadsLoaded) return;
        _downloadsLoaded = true;
        try
        {
            if (!File.Exists(_downloadsFile)) return;
            List<DownloadEntry>? saved = JsonSerializer.Deserialize<List<DownloadEntry>>(File.ReadAllText(_downloadsFile));
            foreach (DownloadEntry e in saved ?? [])
            {
                // a file that was still going when the launcher closed did not finish
                if (e.State is RowState.Waiting or RowState.Downloading or RowState.Copying) e.State = RowState.Stopped;
                _downloadRows.Add(new DownloadRow(e));
            }
        }
        catch (Exception e) { Logger.Warning("Couldn't read the download history", e); }
        RebuildDownloadList();
    }

    void SaveDownloadHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_downloadsFile)!);
            File.WriteAllText(_downloadsFile, JsonSerializer.Serialize(_downloadRows.Take(KeptRows).Select(r => r.Entry).ToList()));
        }
        catch (Exception e) { Logger.Warning("Couldn't save the download history", e); }
    }

    /// <summary>Adds the files of a new update at the top, all waiting, and opens the list.</summary>
    Dictionary<PlannedFile, DownloadRow> StartDownloadList(IReadOnlyList<PlannedFile> outdated)
    {
        LoadDownloadHistory();
        Dictionary<PlannedFile, DownloadRow> rows = [];
        List<DownloadRow> batch = [];
        foreach (PlannedFile planned in outdated)
        {
            DownloadRow row = new(new DownloadEntry
            {
                Path = planned.File.Path,
                Size = planned.File.Size,
                New = !File.Exists(planned.Target),
                State = RowState.Waiting,
            });
            rows[planned] = row;
            batch.Add(row);
        }
        _downloadRows.InsertRange(0, batch);
        if (_downloadRows.Count > KeptRows) _downloadRows.RemoveRange(KeptRows, _downloadRows.Count - KeptRows);
        RebuildDownloadList();
        if (batch.Count > 0) DownloadsPanel.IsVisible = true;
        return rows;
    }

    void SetRow(DownloadRow row, RowState state, long done = 0, long total = 0, string left = "")
    {
        row.Entry.State = state;
        if (state is RowState.Done or RowState.Copied) row.Entry.When = DateTime.Now;
        if (row.Bar is { } bar)
        {
            bar.IsVisible = state is RowState.Downloading or RowState.Copying;
            bar.Value = total > 0 ? (double)done / total : 0;
        }
        if (row.Detail is { } detail) detail.Text = DetailText(row.Entry, done, total, left);
    }

    static string DetailText(DownloadEntry e, long done, long total, string left)
    {
        string kind = Translation.T(e.New ? "New" : "Changed");
        string size = Words.ShortSize(e.Size);
        return e.State switch
        {
            RowState.Waiting => $"{kind}  ·  {size}  ·  {Translation.T("Waiting")}",
            RowState.Downloading => $"{kind}  ·  ↓ {MainWindow.Steady(Words.ShortSize(done), 9)} / {Words.ShortSize(total > 0 ? total : e.Size)}"
                                    + (left.Length > 0 ? $"  ·  {MainWindow.Steady(left, 12)}" : ""),
            RowState.Copying => $"{kind}  ·  {size}  ·  {Translation.T("Copying from your disk")}",
            RowState.Done => $"{kind}  ·  {size}  ·  {Translation.T("Done")}  ·  {e.When:dd-MM-yyyy HH:mm}",
            RowState.Copied => $"{kind}  ·  {size}  ·  {Translation.T("Done (copied from your disk)")}  ·  {e.When:dd-MM-yyyy HH:mm}",
            RowState.Failed => $"{kind}  ·  {size}  ·  {Translation.T("Failed: see the log")}",
            _ => $"{kind}  ·  {size}  ·  {Translation.T("Not finished: press Update to get it")}",
        };
    }

    void RebuildDownloadList()
    {
        DownloadsList.Children.Clear();
        foreach (DownloadRow row in _downloadRows)
        {
            row.View = BuildRow(row);
            DownloadsList.Children.Add(row.View);
        }
        DownloadsEmpty.IsVisible = _downloadRows.Count == 0;
        UpdateDownloadsButton();
    }

    void UpdateDownloadsButton()
    {
        int waiting = _downloadRows.Count(r => r.Entry.State is RowState.Waiting or RowState.Downloading or RowState.Copying);
        DownloadsButton.Content = waiting > 0
            ? string.Format(Translation.T("Downloads ({0} left)"), waiting)
            : Translation.T("Downloads");
        DownloadsButton.IsVisible = _downloadRows.Count > 0;
    }

    Control BuildRow(DownloadRow row)
    {
        DownloadEntry e = row.Entry;
        string ext = Path.GetExtension(e.Path).TrimStart('.').ToUpperInvariant();
        if (ext.Length == 0 || ext.Length > 4) ext = "FILE";
        Border badge = new()
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Top,
            Background = (IBrush?)Application.Current!.FindResource("T.section") ?? Brushes.SteelBlue,
            Child = new TextBlock
            {
                Text = ext, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        TextBlock name = new()
        {
            Text = Path.GetFileName(e.Path), FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (IBrush?)Application.Current!.FindResource("T.text"),
        };
        ToolTip.SetTip(name, e.Path);
        row.Detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFeatures = TabularDigits };
        row.Detail.Classes.Add("dim");
        row.Bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 4, 0, 0),
            Foreground = (IBrush?)Application.Current!.FindResource("T.section") };
        StackPanel text = new() { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), Children = { name, row.Detail, row.Bar } };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 6, 0, 6) };
        grid.Children.Add(badge);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        SetRow(row, e.State);
        return grid;
    }

    void OnDownloads(object? sender, RoutedEventArgs e)
    {
        LoadDownloadHistory();
        DownloadsPanel.IsVisible = !DownloadsPanel.IsVisible;
    }

    void OnCloseDownloads(object? sender, RoutedEventArgs e) => DownloadsPanel.IsVisible = false;

    void OnClearDownloads(object? sender, RoutedEventArgs e)
    {
        _downloadRows.RemoveAll(r => r.Entry.State is not (RowState.Waiting or RowState.Downloading or RowState.Copying));
        RebuildDownloadList();
        SaveDownloadHistory();
    }
}
