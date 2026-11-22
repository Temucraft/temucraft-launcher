using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Launcher.App;

/// <summary>
/// A drawing of the 3.3.5 login screen's buttons and boxes (where they sit at 1920 by 1080), laid over a login
/// background preview so players see how the scene looks with the login window on top. Drawn, not a picture.
/// </summary>
public static class LoginButtonsOverlay
{
    static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x00));
    static readonly IBrush ButtonFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0xB0, 0x10, 0x10), 0), new GradientStop(Color.FromRgb(0x60, 0x05, 0x05), 1) },
    };
    static readonly IBrush ButtonEdge = new SolidColorBrush(Color.FromRgb(0x9A, 0x86, 0x55));
    static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(0xC0, 0x22, 0x22, 0x26));
    static readonly IBrush BoxEdge = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x98));

    /// <summary>The overlay, scaled to fill whatever it is placed over (use the same Stretch as the picture).</summary>
    public static Control Build(Stretch stretch = Stretch.Fill)
    {
        Canvas c = new() { Width = 1920, Height = 1080, IsHitTestVisible = false };

        Label(c, "Username", 960, 531, 24, true);
        Box(c, 830, 548, 266, 36, "Enter your username");
        Label(c, "Password", 958, 630, 24, true);
        Box(c, 830, 647, 266, 36, null);
        Button(c, "Login", 826, 790, 264, 34, 24);
        Check(c, "Remember Account Name", 838, 842);
        Check(c, "Fast Login", 906, 866);

        Button(c, "Manage Account", 30, 794, 178, 28, 18);
        Button(c, "Community Site", 30, 847, 178, 28, 18);
        Check(c, "Show Launcher", 30, 902, true);
        Border esrb = new() { Width = 176, Height = 52, Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(2),
            Child = new TextBlock { Text = "ESRB Notice:\nGame Experience May\nChange During Online Play", FontSize = 12, Foreground = Brushes.Black,
                                    TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Place(c, esrb, 30, 962);

        Button(c, "Options", 1718, 679, 178, 27, 18);
        Button(c, "Cinematics", 1718, 729, 178, 27, 18);
        Button(c, "Credits", 1718, 780, 178, 27, 18);
        Button(c, "Terms of Use", 1718, 830, 178, 27, 18);
        Place(c, new TextBlock { Text = "Server used (Select):", FontSize = 17, Foreground = Brushes.LightGray, Width = 170, TextAlignment = TextAlignment.Right }, 1728, 888);
        Place(c, new TextBlock { Text = "your.server", FontSize = 21, Foreground = Brushes.White, Width = 170, TextAlignment = TextAlignment.Right }, 1728, 914);
        Button(c, "Quit", 1718, 996, 178, 28, 18);

        Border logo = new() { Width = 130, Height = 76, Background = new SolidColorBrush(Color.FromRgb(0x05, 0x20, 0x50)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0xD0)), BorderThickness = new Thickness(2),
            Child = new TextBlock { Text = "BLIZZARD", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x9F, 0xF0)),
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Place(c, logo, 895, 960);
        Place(c, new TextBlock { Text = "Temucraft 1.4.0  (3.3.5a build Release)", FontSize = 17, Foreground = Gold }, 4, 1048);
        Place(c, new TextBlock { Text = "Copyright 2004-2010  Blizzard Entertainment. All Rights Reserved.", FontSize = 17, Foreground = Gold }, 682, 1048);

        return new Viewbox { Stretch = stretch, Child = c, IsHitTestVisible = false };
    }

    static void Place(Canvas c, Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        c.Children.Add(control);
    }

    static void Label(Canvas c, string text, double centreX, double centreY, double size, bool shadow)
    {
        TextBlock t = new() { Text = text, FontSize = size, Foreground = Gold, Width = 300, TextAlignment = TextAlignment.Center };
        if (shadow) t.Effect = new DropShadowEffect { OffsetX = 1, OffsetY = 1, BlurRadius = 2, Color = Colors.Black };
        Place(c, t, centreX - 150, centreY - size * 0.65);
    }

    static void Box(Canvas c, double x, double y, double w, double h, string? hint)
    {
        Border b = new() { Width = w, Height = h, Background = BoxFill, BorderBrush = BoxEdge, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(3) };
        if (hint is not null)
            b.Child = new TextBlock { Text = hint, FontSize = 17, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment = VerticalAlignment.Center };
        Place(c, b, x, y);
    }

    static void Button(Canvas c, string text, double x, double y, double w, double h, double size)
    {
        Border b = new() { Width = w, Height = h, Background = ButtonFill, BorderBrush = ButtonEdge, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
            Child = new TextBlock { Text = text, FontSize = size, Foreground = Gold, HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center } };
        Place(c, b, x, y);
    }

    static void Check(Canvas c, string text, double x, double y, bool ticked = false)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 18, Height = 18, Background = BoxFill, BorderBrush = BoxEdge, BorderThickness = new Thickness(2),
            Child = ticked ? new TextBlock { Text = "✓", FontSize = 16, Foreground = Gold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) } : null });
        row.Children.Add(new TextBlock { Text = text, FontSize = 18, Foreground = Gold, VerticalAlignment = VerticalAlignment.Center });
        Place(c, row, x, y - 2);
    }
}
