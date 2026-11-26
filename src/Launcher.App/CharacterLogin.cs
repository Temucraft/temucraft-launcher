using Avalonia.Input;
using Avalonia.Interactivity;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The character summary's game login (<see cref="CharacterLogin"/>, server/char-summary.py). The launcher login is
/// shared by everybody, so it can't say whose characters to show: at PLAY (and at the Live/PTR switch), while there is no
/// ticket, a small window asks for the player's game account and password once. The server checks them and sends a
/// ticket; only the ticket is kept (DPAPI, this Windows user), never the password. "Not now" lasts until the launcher
/// starts again. A ticket the server no longer takes (expired, a new IP, a changed password) is dropped and the window
/// comes once more. Settings page: "logged in as ACCOUNT — Log out" or "Log in".
/// </summary>
public partial class MainWindow
{
    readonly CharacterTicketStore _charTickets = CharacterTicketStore.ForThisPc();
    readonly CharacterLoginSession _charSession = new();
    TaskCompletionSource<CharacterTicket?>? _charLoginWait;
    bool _charLoginHooked;

    /// <summary>The saved ticket for this server, or this session's (Remember me off).</summary>
    CharacterTicket? CurrentCharTicket => _server is null ? null : _charTickets.Load(_server.ServerUrl) ?? _charSession.SessionTicket;

    /// <summary>The Settings page's line: who the character summary is for.</summary>
    void ShowCharacterLogin()
    {
        CharacterTicket? ticket = CurrentCharTicket;
        CharLoginStateText.Text = ticket is null
            ? Translation.T("Character summary: not logged in")
            : string.Format(Translation.T("Character summary: logged in as {0}"), ticket.Account);
        CharLoginSettingsButton.Content = Translation.T(ticket is null ? "Log in" : "Log out");
        CharLoginSettingsButton.IsEnabled = _server is not null;
    }

    async void OnCharLoginSettingsButton(object? sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        if (CurrentCharTicket is { } ticket)
        {
            // the server ends the ticket too (best effort); this PC forgets it either way
            if (HaveLogin)
            {
                using ServerClient client = NewClient();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
                await CharacterLogin.LogoutAsync((form, token) => client.RequestAsync(CharacterSummary.ServerPath, form,
                    new Dictionary<string, string> { [CharacterLogin.Header] = ticket.Ticket }, token), timeout.Token);
            }
            _charTickets.Clear();
            _charSession.SessionTicket = null;
            _charSession.SayNotNow();   // don't ask again right away at the next PLAY: Log in is here
            ClearCharacterSummaryFiles();
            Log(string.Format(Translation.T("Character summary: logged out ({0})"), ticket.Account));
            ShowCharacterLogin();
            return;
        }
        _charSession.Reset();
        if (await AskCharacterLoginAsync() is not null)
            await RefreshCharacterSummaryAsync(Folder, ask: false);
    }

    /// <summary>After Log out: the character screen's extra lines go (both versions' files), so nobody else sees them.</summary>
    void ClearCharacterSummaryFiles()
    {
        foreach (string folder in new[] { LiveFolder, PtrFolder }.Where(f => f.Length > 0 && GameFolder.LooksLikeClient(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { CharacterSummary.Clear(folder); }
            catch (Exception error) { Logger.Warning("Couldn't empty Interface\\TemucraftCharSummary.lua", error); }
        }
    }

    /// <summary>The login window; the ticket, or null ("Not now", or closed). Saves the ticket when Remember me is on.</summary>
    Task<CharacterTicket?> AskCharacterLoginAsync()
    {
        if (_server is null) return Task.FromResult<CharacterTicket?>(null);
        if (_charLoginWait is { } open) return open.Task;   // already showing
        if (!_charLoginHooked)
        {
            _charLoginHooked = true;
            CharLoginPassword.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OnCharLogin(null, new RoutedEventArgs()); } };
            CharLoginAccount.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CharLoginPassword.Focus(); } };
            CharLoginOverlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; OnCharLoginNotNow(null, new RoutedEventArgs()); } };
        }
        _charLoginWait = new TaskCompletionSource<CharacterTicket?>();
        CharLoginAccount.Text = CharLoginAccount.Text is { Length: > 0 } typed ? typed : CharacterLogin.AccountFromConfig(Folder) ?? "";
        CharLoginPassword.Text = "";
        CharLoginRemember.IsChecked = true;
        CharLoginRemember.IsEnabled = _charTickets.IsSupported;
        CharLoginError.IsVisible = false;
        CharLoginButton.IsEnabled = CharLoginNotNowButton.IsEnabled = true;
        CharLoginOverlay.IsVisible = true;
        (CharLoginAccount.Text.Length > 0 ? CharLoginPassword : CharLoginAccount).Focus();
        return _charLoginWait.Task;
    }

    void CloseCharacterLogin(CharacterTicket? ticket)
    {
        CharLoginPassword.Text = "";   // the password never stays anywhere
        CharLoginOverlay.IsVisible = false;
        TaskCompletionSource<CharacterTicket?>? wait = _charLoginWait;
        _charLoginWait = null;
        ShowCharacterLogin();
        wait?.TrySetResult(ticket);
    }

    void OnCharLoginNotNow(object? sender, RoutedEventArgs e)
    {
        _charSession.SayNotNow();
        Logger.Info("Character summary: Not now (asked again when the launcher starts again)", toWindow: false);
        CloseCharacterLogin(null);
    }

    async void OnCharLogin(object? sender, RoutedEventArgs e)
    {
        if (_server is null || _charLoginWait is null || !CharLoginButton.IsEnabled) return;
        string account = CharLoginAccount.Text?.Trim() ?? "", password = CharLoginPassword.Text ?? "";
        if (account.Length == 0 || password.Length == 0)
        {
            ShowCharLoginError(Translation.T("Enter your game account name and password."));
            return;
        }
        if (!HaveLogin)
        {
            ShowCharLoginError(Translation.T("Log in to the launcher first (Settings page, ACCOUNT)."));
            return;
        }
        CharLoginButton.IsEnabled = CharLoginNotNowButton.IsEnabled = false;
        CharLoginError.IsVisible = false;
        TicketLoginResult result;
        using (ServerClient client = NewClient())
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)))
            result = await CharacterLogin.LoginAsync((form, token) => client.RequestAsync(CharacterSummary.ServerPath, form, token: token),
                _server.ServerUrl, account, password, timeout.Token);
        password = "";
        CharLoginButton.IsEnabled = CharLoginNotNowButton.IsEnabled = true;
        switch (result.Status)
        {
            case TicketLoginStatus.LoggedIn when result.Ticket is { } ticket:
                bool saved = CharLoginRemember.IsChecked == true && _charTickets.Save(ticket);
                _charSession.SessionTicket = saved ? null : ticket;
                Log(string.Format(Translation.T("Character summary: logged in as {0}"), ticket.Account)
                    + (saved ? "" : " " + Translation.T("(for this launcher session only)")));
                CloseCharacterLogin(ticket);
                break;
            case TicketLoginStatus.WrongLogin:
                CharLoginPassword.Text = "";
                ShowCharLoginError(Translation.T("Wrong account name or password."));
                break;
            case TicketLoginStatus.TooManyTries:
                ShowCharLoginError(string.Format(Translation.T("Too many tries. Try again in {0} minute(s)."), Math.Max(1, (result.RetryAfterSeconds + 59) / 60)));
                break;
            case TicketLoginStatus.NotOnServer:
                _charSession.ServerHasNoLogin();
                Logger.Info("Character summary: the server's char-summary has no game login (an older char-summary)", toWindow: false);
                ShowCharLoginError(Translation.T("The server can't do this yet."));
                CharLoginButton.IsEnabled = false;
                break;
            default:
                Logger.Info($"Character summary: login failed ({result.Error})", toWindow: false);
                ShowCharLoginError(string.Format(Translation.T("Couldn't log in ({0}). Try again, or press Not now."), result.Error ?? "?"));
                break;
        }
    }

    void ShowCharLoginError(string text)
    {
        CharLoginError.Text = text;
        CharLoginError.IsVisible = true;
    }
}
