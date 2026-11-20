using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The realm status block over the news: per realm online or offline, players online, the all-time peak and the uptime,
/// from the website's <c>?realm-status=data</c> (see <see cref="RealmStatus"/> for where the address comes from), and
/// a "Player map" link to the website's live map. Read when the window opens, again every minute, and at once when the
/// server's policy brings another address. Nothing shows when there is no address or the website does not answer.
/// </summary>
public partial class MainWindow
{
    readonly DispatcherTimer _realmTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Uri? _realmUrl;
    DateTime _realmRead = DateTime.MinValue;
    Uri? _playerMapUrl;
    bool _realmBusy;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NewsPage.PropertyChanged += (_, change) => { if (change.Property == IsVisibleProperty) ShowRealmPanel(); };
        _realmTimer.Tick += async (_, _) => await RefreshRealmsAsync();
        _realmTimer.Start();
        _ = RefreshRealmsAsync();
    }

    IReadOnlyList<NavLink> NavLinksOrNone => _server?.Nav ?? [];

    async Task RefreshRealmsAsync()
    {
        Uri? url = RealmStatus.StatusUrl(_policy, NavLinksOrNone);
        bool changed = url != _realmUrl;
        if (_realmBusy || (!changed && DateTime.UtcNow - _realmRead < TimeSpan.FromSeconds(60))) return;
        _realmUrl = url;
        _realmRead = DateTime.UtcNow;
        if (url is null)
        {
            RealmList.Children.Clear();
            ShowRealmPanel();
            return;
        }

        _realmBusy = true;
        try
        {
            RealmStatus status = await RealmStatus.FetchAsync(url, _server?.CertificateSha256);
            _playerMapUrl = RealmStatus.PlayerMapUrl(_policy, NavLinksOrNone, status, url);
            ShowRealms(status);
        }
        catch (Exception error)
        {
            if (changed) Logger.Debug($"Realm status not read from {url}: {error.Message}");
            RealmList.Children.Clear();
            ShowRealmPanel();
        }
        finally
        {
            _realmBusy = false;
        }
    }

    void ShowRealms(RealmStatus status)
    {
        RealmList.Children.Clear();
        foreach (RealmInfo realm in status.Realms)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 22, 2), VerticalAlignment = VerticalAlignment.Center };
            Ellipse dot = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, this.GetResourceObservable(realm.Online ? "T.good" : "T.bad"));
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = realm.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            TextBlock state = new() { Text = Translation.T(realm.Online ? "Online" : "Offline"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            state.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(realm.Online ? "T.good" : "T.bad"));
            row.Children.Add(state);
            if (realm.Online)
                row.Children.Add(Dim(string.Format(Translation.T("{0} online"), realm.Players)));
            row.Children.Add(Dim(string.Format(Translation.T("Peak {0}"), realm.Peak)));
            if (realm.Online && realm.Uptime > 0)
                row.Children.Add(Dim(string.Format(Translation.T("Up {0}"), RealmStatus.Duration(realm.Uptime))));
            if (realm.Online && realm.Alliance + realm.Horde > 0)
                ToolTip.SetTip(row, string.Format(Translation.T("Alliance {0}, Horde {1}"), realm.Alliance, realm.Horde));
            RealmList.Children.Add(row);
        }
        PlayerMapButton.IsVisible = _playerMapUrl is not null;
        ShowRealmPanel();
    }

    TextBlock Dim(string text)
    {
        TextBlock block = new() { Text = "· " + text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("T.dim"));
        return block;
    }

    void ShowRealmPanel() => RealmPanel.IsVisible = NewsPage.IsVisible && RealmList.Children.Count > 0;

    void OnPlayerMap(object? sender, RoutedEventArgs e)
    {
        if (_playerMapUrl is not null) OpenUrl(_playerMapUrl.ToString());
    }
}
