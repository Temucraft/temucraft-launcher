using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Launcher.Core;

namespace Launcher.App;

/// <summary>More pictures of an option group's choices (gallery-&lt;Option&gt;-&lt;NN&gt;-&lt;Label&gt; on the server): a button
/// per choice, the picture big, previous / next (Left / Right arrows too).</summary>
public sealed class OptionGalleryWindow : Window
{
    readonly OptionGroup _group;
    readonly Func<string, Task<Bitmap?>> _picture;
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _label = new() { FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _choices = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    string _option;
    int _index;

    public OptionGalleryWindow(OptionGroup group, string? chosen, Func<string, Task<Bitmap?>> picture)
    {
        _group = group; _picture = picture;
        List<string> options = [.. group.Options.Where(group.Gallery.ContainsKey), .. group.Gallery.Keys.Where(k => !group.Options.Contains(k))];
        _option = chosen is not null && group.Gallery.ContainsKey(chosen) ? chosen : options[0];
        Title = group.GalleryTitle.Length > 0 ? group.GalleryTitle : group.Name;
        Width = 1100; Height = 720; CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10));

        foreach (string option in options)
        {
            Button b = new() { Content = option, Tag = option };
            b.Click += (_, _) => { _option = option; _index = 0; _ = ShowAsync(); };
            _choices.Children.Add(b);
        }
        Button previous = new() { Content = "Previous" }, next = new() { Content = "Next" }, done = new() { Content = "Done" };
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        done.Click += (_, _) => Close();

        DockPanel top = new() { Margin = new Thickness(12, 10) };
        DockPanel.SetDock(_choices, Dock.Left);
        top.Children.Add(_choices);
        _label.Margin = new Thickness(16, 0, 0, 0);
        top.Children.Add(_label);
        StackPanel bottom = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                                    Margin = new Thickness(0, 8, 0, 12), Children = { previous, next, done } };
        DockPanel layout = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(new Border { Child = _image, Margin = new Thickness(12, 0), Background = Brushes.Black });
        Content = layout;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) { Step(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
        _ = ShowAsync();
    }

    void Step(int by)
    {
        int count = _group.Gallery[_option].Count;
        _index = ((_index + by) % count + count) % count;
        _ = ShowAsync();
    }

    async Task ShowAsync()
    {
        foreach (Button b in _choices.Children.OfType<Button>())
            b.FontWeight = (string?)b.Tag == _option ? FontWeight.Bold : FontWeight.Normal;
        List<OptionLink> pictures = _group.Gallery[_option];
        OptionLink shown = pictures[_index];
        _label.Text = $"{_option}: {shown.Name}   ({_index + 1} / {pictures.Count})";
        Bitmap? bitmap = await _picture(shown.Url);
        if (pictures.Count > _index && pictures[_index] == shown) _image.Source = bitmap;
    }
}
