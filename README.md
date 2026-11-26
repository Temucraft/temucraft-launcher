# Temucraft Launcher

A launcher for a WoW 3.3.5a server: players who have the client get the server's patches, addons
and locale files, checked by SHA-256, and start the game from it.

The files sit behind Apache with the same two checks as your website: an IP whitelist, then a username and
password. The server address lives in `launcher.env` next to the exe, which is git-ignored, so it never ends up in
the repository or in the exe.

## What it does

- Logs in with each player's own username and password for the game files (HTTP basic auth, https only unless
  `ALLOW_HTTP=1`); the server also only accepts the addresses it knows. A wrong password and an address that isn't
  allowed get the same message, so a stranger can't tell which it was. The login is asked first, in its own window, like a website's login
  prompt; the server checks it against the login set there. With "Remember me" it logs in by itself next time and
  only asks again if the server refuses it. Nothing about the login is stored in `launcher.env`.
- A new player picks an empty folder: PLAY turns into INSTALL and downloads the whole game there (the server's
  `base/`), then the server's files. "Install game files…" does the same for a folder picked there.
- Reads `manifest.json`: every file with its size and SHA-256.
- Compares your files with it and downloads what's missing or different. Downloads resume after a dropped
  connection, are checked against their SHA-256 and only then put in place, so a broken download never replaces a
  working file. Big files are hashed once and remembered (by size and time), not at every launch.
- Deletes patches the server stopped handing out (renamed ones, for example) and writes `realmlist.wtf` for every
  locale the client has.
- Locales: tick the languages you want; their files go to `Data\<locale>\`.
- Addons: the server's addons go to `Interface\AddOns\` (can be turned off).
- Self-check: compares itself with the launcher on the server and warns when it differs (outdated or changed).
- Play: clears the client's `Cache` folder (optional), writes the picked game version's realm into `WTF\Config.wtf`
  (so the game skips the realm list), skips the game's first-start agreement screens (optional) and starts `Wow.exe`.
- Live and PTR: "Game version" above PLAY picks Live or the PTR (the test realm), each with its own install, addons
  and settings (see Live and PTR below).
- The password can be saved in the operating system's password store (Windows Credential Manager, also under Wine;
  the macOS Keychain; on Linux the desktop keyring through `secret-tool`); nothing secret is written to a file.

The window has six pages:

- **News**: the server's news and changelog (`news.json`, `changelog.json` next to the manifest).
- **Tweaks**: client settings written to `WTF\Config.wtf` (auto-loot, FPS caps, windowed mode, render and clutter
  distance, camera distance, background sound...), recommended values and defaults in one click, and Large Address
  Aware (Wow.exe may use 4 GB; the original is kept as `Wow.exe.bak`).
- **Addons**: the server's addon list (`addons.json`: GitHub repositories) with a preview of each one's README,
  install, update (by the branch's newest commit) and "needed" addons that are installed and kept up to date with the
  game; your installed addons (from their .toc) with remove (moved to the backup folder, never deleted). An addon that
  lives in a sub folder of a bigger repository sets `"path"` (e.g. `"path": "client/AddOns"`): only its own files are
  downloaded, through GitHub's file list, instead of the whole repository (the zip is the fallback when GitHub's API
  can't be used).
- **Options**: the server's switchable fixes and cosmetic patches (`options/<Group>/<Option>/...`), one pick per group;
  everything the game needs goes in `patches/` and is always installed; untracked patches in Data that neither the
  client nor the server hand out, with remove.
- **Settings**: account, game folder (and installing the game into an empty one), the PTR's folder with Copy Account
  Data and Copy Key Bindings, languages, game settings (the server's addons, clearing the Cache, skipping the game's
  first-start agreement screens, whether a new PTR starts with your Live game settings), backups (WTF and AddOns zipped before each update and on request, restore),
  macros and key bindings export/import, verify game files.
- **Launcher**: the launcher's own settings (reopen after WoW closes, remember the window, background picture), its
  version and the log.

**The header** shows the server's address and the logged-in username (`SHOW_IP=0` / `SHOW_USERNAME=0` in
`launcher.env` hide them) and links to the server's websites: `NAV=Register|https://...; Database|https://...` (only
http and https links). A third part is your own text shown when hovering the link (`Register|https://...|Ask Ryan
for a login`), and `NAV_SHOW_URL=0` hides the address under it.

**Knowing who connects.** The server's owner can have the launcher send information about the player's PC: its
version, the operating system with version and build, a random ID for the install, the computer's name, the operating
system username, the MAC addresses, the local IP addresses, time zone, language and hardware. The launcher's version,
the operating system (version and build) and the install ID are sent to every server unless `COLLECT=none`; the rest
only when `COLLECT` in `launcher.env` lists it (`all` for everything). Every server also sees the public IP address. The login window shows what any server might collect, what this one collects
and the owner's own words (`COLLECT_NOTE`) before anything is sent, each item tagged "(Default)" or "(Enabled in
<`SERVER_NAME`>)", and logging in is agreeing to it; if the list
grows, the launcher asks again. The Launcher page shows the exact values sent from this PC. Each item goes in its
own header (`X-Temucraft-<item>`, URL-encoded). On the server, `server/apache-launcher-clients.conf.example` logs every
login (manifest.json) as a CSV line with the time, the public IP (IPv4 or IPv6), the login, whether it was let in and
one column per item; `server/clients-csv.py` writes a readable copy with a header row, starting with the code and its
description (200 OK, 401 Unauthorized...), then operating system, unique ID, public IP, local IP, username and MAC
(`--last`: the newest line per install).

**The server decides what it wants to.** `launcher-policy.json` next to the manifest can set any launcher setting.
Each entry is `{"value": ..., "enforce": true|false}` (a bare value means "recommended"). An enforced setting is set by
the server and greyed out; one that isn't enforced stays the player's choice, with the server's value as the
recommendation (and as the value outright where the launcher has no box for it).

| Name | Value | What it does |
|---|---|---|
| `installAddons`, `clearCache`, `reopenAfterGame`, `rememberWindow`, `backupBeforeUpdate` | true / false | the launcher's switches |
| `skipGameAgreements` | true / false | skip the game's first-start agreement screens (on by default) |
| `ptrCopyLiveSettings` | true / false | a new PTR install starts with the player's Live `WTF\Config.wtf` settings (resolution, graphics, sound, interface; on by default); false: fresh settings, like a new install. Copy Account Data copies them whatever this says (it's an explicit button). |
| `largeAddressAware` | true / false | Wow.exe's 4 GB switch; enforced, it's set again after every update |
| `launcherUpdate` | "required" / "recommended", or per realm | A new launcher: required to play (the default) or only recommended. Either way a window in the middle ("There's an update") asks Update now / Later; required, PLAY waits for the new launcher. Per realm id: `{"required": [1, 2], "recommended": [3], "default": "required"}`; a realm in neither list follows `default` (`required` when left out). The realm is the picked game version's: 1 for Live, 3 for the PTR. |
| `backupsToKeep` | a number | how many backups the launcher keeps |
| `defaultLocale` | e.g. `"enUS"` | the server's default language (its base client's): always installed, ticked and greyed out; `enUS` when not set. The game's own language is always kept too. |
| `requiredLocales` | e.g. `["deDE"]` | extra languages everyone gets (players can still add more) |
| `news`, `changelog` | `{"show": true, "entries": 20, "more": 20}` | whether the panel shows (the other one then takes the whole page), how many entries at first, and how many "Load more" adds; `-1` makes it "Load all". The launcher keeps the last ones it got, so they show at once on the next start. |
| `tweaks` | `{"<CVar>": {...}}` | any `Config.wtf` value by its name, the Tweaks page's or any other (e.g. `farclip`, `maxFPS`); enforced ones are written to `Config.wtf` after every update and on Save, others show as the recommendation ("Use recommended"). DXVK's settings (`d3d9.maxFrameRate`, `d3d9.samplerAnisotropy`, `d3d9.presentInterval`, `dxvk.hud`) go to `dxvk.conf`; Awesome WotLK's (`nameplateDistance`, `cameraFov`) to `Config.wtf`. The Tweaks page only shows those while that option is on. |
| `downloadLimit` | KB/s, e.g. `2000` | the most each player may download at; `0` or `-1` (the default) is no cap. Players can still set a lower limit of their own. |
| `theme` | e.g. `"temucraft"`, `"wotlk"` | the launcher's default theme (see Themes below) |
| `themeMode` | `"dark"` or `"light"` | the default mode |
| `reportBugUrl` | a web address | where the bug icon next to Play sends players (the launcher's GitHub issues when not set) |
| `realmStatusUrl` | a web address, or `false` | the realm status over the news (online or offline, players, peak, uptime): the website's `?realm-status=data`. Not set: the NAV link named like Database / AoWoW / Website (else the first) with `?realm-status=data`; `false` hides it |
| `playerMapUrl` | a web address, or `false` | the "Player map" link next to the realm status; not set: the map address the website sends (`?player-map`) |
| `liveRealmName` / `ptrRealmName` | a realm's name | the realm PLAY logs Live / the PTR into (see Live and PTR below), when `realm-name.txt` doesn't say |

Names the launcher doesn't know are skipped and noted in its log, so a newer policy never breaks an older launcher.
`launcher-policy.json` may hold `"_comment"` notes (on the file, on a setting or in `tweaks`); `make-manifest.py` removes them and the launcher skips them. Plain JSON, so editors don't complain. See
`server/examples/launcher-policy.json`. An option group's `group.json` can say `"enforce": true` too: everyone
then gets its first option.

## Installing, downloads and launcher updates

**Installing.** A player without the game presses INSTALL and gets a window to pick the folder and what to install:
the game, the server's needed files, each language, each option and the addons, each with its size, plus the total
against the free space on that drive.

**Downloads** are at full speed unless the player sets a limit (Launcher page) or the server caps them
(`downloadLimit`). "Test my speed" measures the speed to the server for a few seconds and suggests a limit (80% of it).
The status line shows each file as "(12/188)", the speed and the time left; sizes read as "16.6 GB (17029.3 MB)".
Two bars show the progress: the file being downloaded (only while an update has more than one file) and, under it,
the whole update, each with its percentage. On Windows the launcher's taskbar button shows the whole update's
progress too.

**The computer stays awake** while the launcher downloads, installs or checks files, so a long download goes on
with the screen locked or off (Windows: SetThreadExecutionState; macOS: caffeinate; Linux: systemd-inhibit). It may
sleep again as soon as the job ends.

**Automatic updates** (off by default): when the launcher opens and every 30 minutes, optionally only between two
hours, and never while the game is running.

**Launcher updates.** When the server hands out a different launcher (`publish.sh` puts the new one in the files
folder), a window in the middle ("There's an update") shows the changelog's newest "Launcher ..." entry and asks:
**Update now** or **Later**. It is never installed without asking. `launcherUpdate` for the realm of the picked game
version says what Later means. Recommended: "A new launcher is available", and everything keeps working. Required (the
default): "A new launcher is needed to keep playing on this server". Settings, mods, game files and news still work,
but PLAY doesn't start the game; it opens the window again. After Later, a **New launcher** button at the top opens the
window again. The window asks again at the next start, or when the server hands out yet another launcher, and not at
the 5-minute checks in between. Update now downloads it in the window with a bar, the percent, MB done / total and the
speed. **Cancel** stops the download and keeps what came so far, so the next Update now carries on from there. A failed
download shows in the window with Retry / Later. When it's done, the window says "Restarting the launcher…"; the new
launcher is checked against the manifest, swapped with the running one and started. Since 1.5.3 the launcher reads
`manifest.json` again right before each download try (Update now and Retry), so a release uploaded after the last check
is downloaded with its own SHA-256 and size; a part of another version is never resumed. A checksum mismatch is tried
once more by itself (manifest read again, from zero) before the error shows; the log has the expected and the received
SHA-256 and when the manifest was read.

## Play tickets: the game only through the launcher

With play tickets, the game's login port (3724) only opens for computers that came through the launcher. When a player
presses PLAY, the launcher asks for `<SERVER_URL>play-ticket` with their login; Apache logs it, and
`server/play-ticket.py` opens the port to that IP for 12 hours (renewed every hour while the game runs). Everyone else
is dropped by the firewall, so starting Wow.exe by hand can't reach the login. A server without play tickets just
answers 404 and nothing changes.

Setup on the server (Linux, root):

1. Add `server/apache-play-ticket.conf.example` to the `<VirtualHost>` that serves the files, then
   `a2enmod headers && systemctl reload apache2`.
2. `apt install ipset iptables`
3. `cp server/play-ticket.env.example server/play-ticket.env`; put your own IP in `TICKET_ALWAYS` while testing.
4. `cp server/temucraft-play-ticket.service /etc/systemd/system/` (fix the path inside), then
   `systemctl daemon-reload && systemctl enable --now temucraft-play-ticket`.

`python3 server/play-ticket.py status` shows who may log in; `allow <ip>` lets someone in by hand; `teardown` (after
stopping the service) opens the port to everyone again. Use an IPv4 address in `SERVER_URL` when the realmlist is IPv4,
so the launcher and the game come from the same address.

## Character summary: spec, professions, gold and mail on character select

(The game login with a character ticket: launcher 1.5.3.)

The character select screen (`TemucraftCharSelect.lua` in patch-^) shows each character's spec, professions and gold
in its card tooltip, and an envelope badge for unread mail. The game's own character list doesn't carry those, so the
launcher fetches them: before PLAY, and when the player switches between Live and PTR, it asks for
`<SERVER_URL>char-summary` (with the player's character ticket, at most 8 seconds a request) and writes the picked version's realm into
that version's game folder as `Interface\TemucraftCharSummary.lua` (patch-^ ships an empty copy):

```
TEMUCRAFT_CHAR_SUMMARY = {
	["Temucraft"] = {
		["Thrall"] = { spec = "Restoration", professions = { "Mining", "Herbalism" }, money = 162575561, mail = { "Jaina", "Auction House" } },
	},
};
```

The realm key is the name the game shows (the realm the launcher writes into Config.wtf, and the server's realmlist
name too if that differs). It is quiet: only the log file says what happened. When the server has no `char-summary`
(404), the login is refused, the answer is broken or the realm is missing from it, the old file stays as it was.

**Whose characters: the player's game login (a character ticket).** The launcher login is one shared login for
everybody on Temucraft, so it can't say whose characters to show. So at PLAY (and at the Live/PTR switch), while the
launcher has no ticket and the summary without one comes back empty, a small window asks once: "Show your characters'
spec, gold, professions and mail on the character screen", with the game account name (filled in from the game's
`WTF\Config.wtf` `accountName` when there is one), the password, "Remember me" (on), **Log in** and **Not now**. The
launcher sends them once (POST to `char-summary`, over the same https and launcher login) and keeps only the ticket the
server answers with: in `%AppData%\TemucraftLauncher\char-ticket.dat`, encrypted with Windows DPAPI for that Windows
user, and only for that server address (with Remember me off: in memory until the launcher closes). The password is
never stored or logged. The summary is then asked for with the header `X-Temucraft-Ticket`. "Not now" lasts until the
launcher starts again (no window at every PLAY). When the server no longer takes the ticket (it expired, the player is on
a new IP, the password changed, or it was ended on the server), the launcher drops it and asks once more; a timeout or
any other failure keeps the old summary file as before. The Settings page shows "Character summary: logged in as
RYAN" with **Log out** (the server ends the ticket, and the character screen's lines are emptied in both versions'
folders), or **Log in**. Wrong passwords, too many tries ("Try again in N minute(s)") and an older server without logins
("The server can't do this yet") show in the window.

**On the server** (`server/char-summary.py`, a CGI program): it sits in the folder Apache serves, next to
`play-ticket`, so Apache runs the same IP list and launcher login on it as on every file there. Which game account it
answers about:

- **With a ticket** (`X-Temucraft-Ticket`): the account the ticket was issued to. `POST char-summary` with
  `action=login&account=..&password=..` checks the game password the way the login server does: AzerothCore's SRP6,
  `v = g^SHA1(salt | SHA1(ACCOUNT ":" PASSWORD)) mod N` (g = 7, the 3.3.5 N, little-endian, only a-z upper-cased)
  against `acore_auth.account.salt` and `verifier`, compared in constant time; the self-test checks it against
  verifiers made by AzerothCore's own `SRP6.cpp`. On success it answers `{"ticket", "account", "expires"}` (a random
  256-bit ticket; `expires` 0 = never). The ticket file (SQLite, `TICKET_DB`, default
  `/var/lib/temucraft/char-tickets.sqlite`) keeps only the ticket's SHA-256, the account id and name, the time, the IP
  it was issued to and a fingerprint of the account's salt and verifier, so a password change ends every ticket of
  that account. At most 20 tickets per account (the oldest go). A ticket that no longer works gets
  `403 {"error": "ticket", "reason": "unknown|expired|ip|password|banned"}` (and is deleted); `POST action=logout` with
  the header ends it. Failed logins: `TICKET_LOGIN_RATE` (5 in 10 minutes) per **account and IP together**, so somebody
  typing wrong passwords for an account from their own IP never locks the player out from the player's IP, and
  `TICKET_IP_RATE` (20 in 10 minutes) per IP over all accounts, against one IP trying many accounts; then
  `429 {"error": "rate", "retry_after": seconds}`. **Bans:** a banned IP (`acore_auth.ip_banned`), or the right password
  for a banned account (`account_banned` with `active = 1`, permanent or not over yet: the login server's own rule),
  gets `403 {"error": "banned"}` and no ticket (the account's tickets are ended too); a ticket in use stops working
  (`reason: banned`, deleted) as soon as its account or the IP using it is banned. Passwords never reach the logs
  (Apache's error log gets "ticket issued to RYAN from <ip>", "too many failed logins" and "banned account or IP").
- **Without a ticket** (older launchers, "Not now"): the launcher login Apache checked, as before: the account with the
  same name (`SAME_NAME=1`) or the ones listed for that login in `char-summary-accounts.txt`. With one shared launcher
  login (Temucraft) set `SAME_NAME=0`, so a no-ticket request gets an empty answer (and the launcher asks to log in).

Per realm (Live realm 1 in `acore_characters`, PTR realm 3 in `acore_ptr_characters`, set in `REALMS`) it sends the
characters by name: `money`, professions (`character_skills`: the primary ones by skill, then First Aid, Cooking,
Fishing), the spec (the talent tree with the most points in the active talent group, from `character_talent` and
`talent-trees.json`) and the senders of unread, delivered mail (a character's name, "Auction House", the creature or
object for NPC mail, else "Mailbox"; at most 8). No account names, ids, guids or IPs are sent in the summary. One query
per table per realm, all by account id.

Settings in `/etc/temucraft/char-summary.env` for the tickets (Temucraft's values; see `server/char-summary.env.example`):

| Key | Temucraft | Without the line | What it does |
| --- | --- | --- | --- |
| `TICKET_DAYS` | `0` | `90` | Days until a ticket ends and the launcher asks for the password again; `0` or `-1`: never (it still ends at a password change, Log out or `--revoke`). |
| `TICKET_SAME_IP` | `1` | `1` | `1`: a ticket works only from the IP it was issued to; from a new IP the launcher asks to log in again. `0`: from anywhere. |
| `TICKET_LOGIN_RATE` | `5/600` | `5/600` | Failed logins allowed per account and IP pair: tries/seconds. Never blocks the account from another IP. |
| `TICKET_IP_RATE` | `20/600` | `20/600` | Failed logins allowed per IP over all accounts (password spraying): tries/seconds. |
| `TICKET_DB` | (default) | `/var/lib/temucraft/char-tickets.sqlite` | The ticket file; its folder must be writable by Apache's user. |
| `SAME_NAME` | `0` | `1` | See above: `0` with one shared launcher login. |

Setup (Linux, root):

1. `apt install python3-pymysql && a2enmod cgid` (the tickets use Python's own `sqlite3` module; the `sqlite3` command
   is only handy for looking: `apt install sqlite3`).
2. A MySQL user that may only read what it needs:

   ```
   CREATE USER 'temucraft_summary'@'localhost' IDENTIFIED BY '<a password>';
   GRANT SELECT (id, username, salt, verifier) ON acore_auth.account TO 'temucraft_summary'@'localhost';
   GRANT SELECT (id, bandate, unbandate, active) ON acore_auth.account_banned TO 'temucraft_summary'@'localhost';
   GRANT SELECT (ip, bandate, unbandate) ON acore_auth.ip_banned TO 'temucraft_summary'@'localhost';
   GRANT SELECT (id, name) ON acore_auth.realmlist TO 'temucraft_summary'@'localhost';
   -- for each characters database (acore_characters, acore_ptr_characters):
   GRANT SELECT ON acore_characters.characters TO 'temucraft_summary'@'localhost';
   GRANT SELECT ON acore_characters.character_skills TO 'temucraft_summary'@'localhost';
   GRANT SELECT ON acore_characters.character_talent TO 'temucraft_summary'@'localhost';
   GRANT SELECT ON acore_characters.mail TO 'temucraft_summary'@'localhost';
   -- for the world database (NPC and object mail senders):
   GRANT SELECT (entry, name) ON acore_world.creature_template TO 'temucraft_summary'@'localhost';
   GRANT SELECT (entry, name) ON acore_world.gameobject_template TO 'temucraft_summary'@'localhost';
   ```

   (An existing user from before the tickets only needs the new columns:
   `GRANT SELECT (id, username, salt, verifier) ON acore_auth.account TO 'temucraft_summary'@'localhost';`, and from
   before the ban checks (2026-11-26) the two `account_banned` / `ip_banned` lines: without them every login and
   ticket answers 500, never a ticket for a banned account.)
3. `mkdir -p /etc/temucraft`; copy `server/char-summary.env.example` to `/etc/temucraft/char-summary.env` (the
   password, the realm list, the ticket keys above), `server/talent-trees.json` next to it, and
   `server/char-summary-accounts.txt.example` as `char-summary-accounts.txt` if any login needs it;
   `chown root:www-data /etc/temucraft/* && chmod 640 /etc/temucraft/*`.
4. The ticket folder, writable only by Apache's user: `install -d -o www-data -g www-data -m 700 /var/lib/temucraft`
   (the SQLite file and its `-wal`/`-shm` files are made there at the first login).
5. `install -m 755 server/char-summary.py /var/www/wow-files/char-summary` (no extension; `upload.py --delete` leaves
   it alone: `char-summary` is in `KEEP_REMOTE`). Login and logout are the same address (POST), so there is no new file
   to serve and no Apache change beyond step 6.
6. Add `server/apache-char-summary.conf.example` inside the files folder's `<Directory>` block (if it isn't there
   yet), then `systemctl reload apache2`. Apache passes the `X-Temucraft-Ticket` header to the script by itself.
7. Check: `python3 server/char-summary.py --self-test` (SRP6 against AzerothCore's verifiers, expiry 0/-1/N, a new IP,
   a password change, logout, the per account+IP and per IP login limits and the bans, without a database).
   `curl -u temucraft --data-urlencode action=login --data-urlencode account=<GAMEACCOUNT> --data-urlencode password=<its password> https://<server>/wow-files/char-summary`
   gives a ticket (`-k` for a self-signed certificate; type the password at curl's prompt for the launcher login);
   `curl -u temucraft -H "X-Temucraft-Ticket: <ticket>" https://<server>/wow-files/char-summary` gives that account's
   characters, and without `-u` Apache refuses it. `sudo -u www-data python3 /var/www/wow-files/char-summary --tickets`
   lists the tickets per account; `--revoke <ACCOUNT>` ends all of an account's tickets.
   `sudo -u www-data REMOTE_USER=<a launcher login> python3 /var/www/wow-files/char-summary` still prints the no-ticket
   answer (empty with `SAME_NAME=0`).

When talents are added or moved, run `python3 server/talent-trees.py <folder with the server's Talent.dbc and
TalentTab.dbc>` and copy the new `talent-trees.json` to `/etc/temucraft/`. The spec names are English (the server's
DBC names).

## Shared settings between accounts

On the Game files page, "Keep my settings, key bindings and macros the same on all my accounts on this PC" copies the
newest `config-cache.wtf` (auto loot and the other account settings), `bindings-cache.wtf` and `macros-cache.txt` of
each account in `WTF\Account` over the older ones, before the game starts and after it closes (a backup is made
first). It can also share addon settings (`SavedVariables`) and each character's own key bindings and macros. The
server can do the same between PCs: `mod-temucraft-account-wide` (`Temucraft.AccountWide.Settings.*`).

## Live and PTR (the test realm)

"Game version" above PLAY picks Live or the PTR (remembered); updates, Tweaks, Addons, Mods and PLAY then work on that
install. Each has its own install: the PTR's folder is on the Settings page (the Live folder's name + " PTR" unless you
pick another; never the Live folder or a folder inside it). INSTALL on the PTR makes it from the Live game: the MPQs
under `Data` (and the kept option files) are hard linked when both folders are on the same drive (no extra space; the
launcher only ever moves new files over them, never writes into one, so Live's copies are never changed). On another
drive, or a file system without hard links, they are copied instead (needing the space twice). Everything else is
copied, `Wow.exe` last (a cancelled install is made from Live again next time, not downloaded), and `WTF`,
`Interface\AddOns`, `Cache`, `Logs` and `Screenshots` are left out (no addons). Nothing is ever written into the Live
folder, except "Also install on Live" when the player asks for it.

A new PTR starts with the Live game's settings from `WTF\Config.wtf` (resolution, graphics, sound, interface, saved
account name) the first time it is played, while the PTR has never been set up: only settings the PTR doesn't have yet
are added, and the realm and debug mode lines stay the PTR's own. "A new PTR install starts with my Live game settings"
(Settings page, policy `ptrCopyLiveSettings`) turned off gives it fresh settings instead, like a new install.

Addons are per version: each install has its own `Interface\AddOns` and its own list of what the launcher installed
from the server's addon list (and at which commit), so an update on one never touches the other. The PTR's file list is
the Live manifest with the server's `ptr/manifest.json` over it (only the files that differ from Live, downloaded from
`ptr/`); its addon list is `addons.json` with `ptr/addons.json` over it (PTR-only addons get "Also install on Live").
The game never shows a realm list: before it starts, the launcher writes `SET realmName` and `SET realmList` into that
install's `WTF\Config.wtf`. Live's realm: `realm-name.txt` next to the manifest, else the policy's `liveRealmName`,
else the realm status's only non-PTR realm (id 1 when there are several). The PTR's: `ptr/realm-name.txt`, else
`ptrRealmName`, else realm id 3 in the realm status, else `[PTR] Falric`.

Copying from Live (only the PTR folder is written; whatever it replaces is kept in the PTR's `_Temucraft\Backups`):
"Copy my addons from Live" (Addons page, PTR only: `Interface\AddOns` and the addons' `SavedVariables`), "Copy Account
Data" (the game settings in `WTF\Config.wtf` but the realm and debug lines, whatever `ptrCopyLiveSettings` says;
`config-cache.wtf`, `macros-cache.txt`, `SavedVariables`, and each character's files) and "Copy Key Bindings"
(`bindings-cache.wtf`, per account and per character), both on the Settings page. Each asks first and waits for the
PTR game to be closed. Characters go from the Live
realm's folder to the PTR realm's (`WTF\Account\NAME\<realm>\<character>`); copied files get a fresh `cache.md5`
entry, so the game keeps them and sends them to the PTR realm.

**Realm names.** Live's realm comes from `realm-name.txt` next to the manifest, the PTR's from `ptr/realm-name.txt`;
without them, the policy's `liveRealmName` / `ptrRealmName`, then the realm status (see above). `make-manifest.py`
puts the file's text in the manifest as `realmName`.

**The PTR on the server** is the `ptr/` folder of the served folder: only the files that differ from Live (`ptr/client/`,
`ptr/patches/`, `ptr/locales/`... laid out like the Live folder), optional `ptr/addons.json`, `ptr/realm-name.txt` and
`ptr/client-version.txt`, and its own `ptr/manifest.json`. Keep it in `5) Release\ptr` and send it with
`upload.py ptr` (see Uploading); `upload.py files` never touches it. A `launcher-policy.json` in `ptr/` goes over Live's
for the PTR.

**Debug mode** is off by default (the server's `options/Debug mode/group.json` lists Off first); Crash reports, the
TemucraftCrash hook (version.dll), stays on by default. **DEBUG tab.** Under PLAY a small tab says DEBUG: green while Debug mode (Mods page) is on for the picked game version,
red while it is off. With Debug mode on, PLAY turns on the game's error popups and taint log in that version's
`Config.wtf`, starts the game with its console (the ` key) and Copy diagnostics adds the debug and taint logs; turned
off, the launcher takes those settings out again. Each game version remembers this for itself.

**Shift + left click on PLAY** plays in debug mode for this game only, while Debug mode is off on the Mods page. The
first time, a window asks the player to tick that debug mode is meant for the PTR and developers (remembered after
that). The launcher puts `TemucraftDebug.txt` and the debug settings in for that game and takes them out when it closes,
or at its next start or PLAY if the launcher was closed meanwhile. In testing mode (`testing.env`) Shift + PLAY also
skips the update, as before.

## Languages

The launcher's own text comes in the languages the 3.3.5a client has: English (enUS), German (deDE), French (frFR),
Spanish (esES and esMX), Russian (ruRU), Korean (koKR) and Chinese (zhCN and zhTW), in `src/Launcher.App/Lang/*.yml`.
By default it uses the computer's language (English when the game has no such language); `LANGUAGE=enUS` (or `deDE`,
`ruRU`...) in `launcher.env` picks one instead, and the player can still choose another on the Launcher page. Only the
launcher's words are translated: what the server writes (news, changelog, option names) shows as the server wrote it.

Each file is one line per text, the English text first: `"Save": "Speichern"`. `{0}`, `{1}` stand for parts that
change (`"Downloading {0}": "Lade {0} herunter"`). A `lang` folder next to the launcher can hold your own files: one
with the same name replaces the built-in one, and a missing line just stays English.

## Themes

Like the database website, the look has two parts: a **mode** (Dark, the default, or Light) and a **theme**: Temucraft,
or one per expansion (Vanilla, The Burning Crusade, Wrath of the Lich King ... Midnight). The player picks both on the
Launcher page; the server picks the defaults with `theme` and `themeMode` in `launcher-policy.json` and can enforce
them. The launcher remembers the server's choice, so the next start opens in it straight away.

The files are in `src/Launcher.App/Themes/`: `dark.yml` and `light.yml` set every colour; a theme file sets `name`,
`order`, colours for both modes (`accent: "#3BA7E3"`) and colours for one mode (`light.accent: "#24678C"`). A
`themes` folder next to the launcher adds more themes or replaces the built-in ones.

Three themes recall the old game websites: **Classic (2006)** (dark stone, blue steel heading bars, gold links, a red
PLAY button; Light mode is parchment with blue links), **Pandaria (2013)** (misty jade sky, warm wood panels, orange
gold links, a jade PLAY button) and **Draenor (2015)** (red sky with embers, very dark brown panels, orange links, an
iron red PLAY button). They use a few extra lines a theme may set:

| Line | Values | What it does |
|------|--------|--------------|
| any colour | `"#4A6E94 > #2A4466 > #1A2C46"` | a top to bottom gradient instead of one colour |
| `sectionBar` | colour | a bar behind section headings (see-through in `dark.yml`/`light.yml`: no bar) |
| `frame` | colour | a second, outer frame around panels |
| `playBorder` | colour | the rim of the PLAY and UPDATE buttons |
| `texture` | `none`, `stone`, `parchment`, `mist`, `ember`, `wood` or `marble.png` | drawn over the window background |
| `textureStrength` | `0` to `1` | how strong that texture is |
| `panelTexture`, `panelTextureStrength` | same | a texture inside the panels, in the panel colour |
| `titleFont` | `"Cinzel, Georgia, serif"` | fonts for titles, headings, tabs and PLAY |
| `radius`, `borderWidth` | `0` to `16`, `0` to `4` | corners and frame width of panels |

The textures are drawn by the launcher from noise (no picture files). A picture name (`marble.png`) is read from the
`themes` folder next to the launcher. Cinzel and Marcellus are built in (`Assets/Fonts`, SIL Open Font Licence, the
licence files sit next to them); other names are looked up on the PC. Every line also works with `dark.` or `light.`
in front.

## The log

Everything the launcher does goes to `%AppData%\TemucraftLauncher\launcher.log` (Settings > Open log): each line has
the time and a level (INFO, WARN, ERROR). Errors are written with their full details (the exception and where it
happened), so a problem a friend reports can be found afterwards; the log box under Settings shows the same lines
without the details. Crashes anywhere in the launcher are caught and logged too. The file is renamed to
`launcher.1.log` (up to `.3`) when it passes 1 MB, so it never grows without end. Passwords are never logged.

**Identical files are downloaded once.** Before downloading a file, the launcher looks for the same content already on
your disk: a file it has already checked with the same hash (another path in the same update, a font under a second
name), or the same-named file of another installed language (`lichking-locale-enUS.MPQ` for
`lichking-locale-deDE.MPQ`). It copies that instead and checks the copy against the manifest's hash like a download.

The bottom bar shows the game files' version ("Game version: ...", from `client-version.txt`; the PTR's
`ptr/client-version.txt` when it has one), whether an update is waiting (PLAY turns into UPDATE) and the
untracked-files warning. The launcher's own version is on the bug button next to PLAY ("Launcher v1.5.0").

## Server setup (VPS with Apache)

1. Make the folder Apache will serve, for example `/var/www/wow-files`:

   ```
   /var/www/wow-files/
       patches/patch-A.MPQ ...           -> Data\           (always installed)
       client/Wow.exe, client/*.dll ...  -> the WoW folder  (always installed: patched exe, loaders...)
       locales/enUS/..., locales/deDE/... -> Data\<locale>\  (for the locales a player ticks)
       addons/<AddonName>/...            -> Interface\AddOns\<AddonName>\
       launcher/TemucraftLauncher.exe    -> for the self-check
   ```

2. Copy `server/apache-temucraft.conf.example` into your Apache config (a self-signed certificate is fine: put its
   SHA-256 fingerprint in `launcher.env` as `CERT_SHA256`, and the launcher trusts exactly that certificate), set your whitelisted addresses and the path,
   create the users with `htpasswd`, and reload Apache. Use https.
3. Optional files next to the manifest: `client-version.txt`, `realm-name.txt`, `news.json`, `changelog.json`,
   `addons.json`, the `ptr/` folder for the PTR (see Live and PTR) and the
   `options/` folder for **cosmetic** patches and switchable fixes (an option group with an `On` folder and `"order": ["On", "Off"]` in its `group.json` is on by
   default and can be turned off; `Off` needs no folder) (see `server/examples/` and the top of `make-manifest.py`); patches the
   game or the server need belong in `patches/`, which every player gets.
   Pictures: `options/<Group>/preview-<Option>.png` (or .jpg; `preview-Off.png` for a switch's off side) are shown side by
   side on the group's card, and clicking one picks it; `previews/login/<code>.png` (the scene's code in loginui.lua, such as
   `lk`) is shown above that login screen's tick box. Neither is installed.
4. Each time you add or change files, rebuild the manifest:

   ```
   python3 server/make-manifest.py /var/www/wow-files --realmlist logon.example.com
   ```

   Hashes are cached in `.manifest-cache.json`, so only new or changed files are read again. Files that were in the
   last manifest and are gone now are listed as obsolete, and launchers delete them.

## Uploading (server/upload.py)

`upload.py` puts your files on the VPS over SSH, puts its own copy of `make-manifest.py` there, and rebuilds the manifest: copy `server/upload.env.example` to
`server/upload.env` (git-ignored), then

```
pip install paramiko
python3 server/upload.py files  "D:\Temucraft release"      # patches/, locales/, addons/, options/, news.json...
python3 server/upload.py server "D:\AzerothCore build"      # worldserver, authserver, configs (stops/starts it)
python3 server/upload.py manifest                            # only rebuild manifest.json
python3 server/upload.py ptr    "D:\Temucraft release\ptr"   # the PTR's files to ptr/, then ptr/manifest.json
```

`files` never sends, lists or deletes the `ptr/` folder; `ptr` only works on it.

Only new or changed files are sent, each under a temporary name until it is complete (`make-manifest.py` skips
unfinished `.upload` files); `--delete` removes what's gone locally, `--dry-run` shows the plan. `--delete` never
removes hidden files (`.htaccess`...), and for `server` it also keeps `*.conf`, `*.log`, `*.pid` and the `logs/` and
`data/` folders, so a build folder without your live configs or map data can't wipe them.

## Building the launcher

With the .NET 10 SDK, run `publish.bat` on Windows or `./publish.sh` on Linux (your VPS works: `./publish.sh /var/www/wow-files` also copies the exe to `launcher/` for the self-check). It writes one self-contained `publish\TemucraftLauncher.exe`
(no .NET install needed to run it). Then:

- copy `launcher.env.example` to `launcher.env`, put your server address in it, and hand friends the exe with that
  file next to it;
- copy the same exe to `launcher/` on the server and re-run `make-manifest.py`, so the self-check knows it.

## Tests

`dotnet run --project tests/Launcher.Core.Tests` checks the manifest rules (no paths outside the WoW folder, no
outside URLs), `launcher.env` parsing, update planning, the hash cache, verified and resumed downloads, and login
errors, against a fake server; saving the settings, Config.wtf keeping accented names, addons from a repository's
sub folder (and the zip fallback), README previews and the log; the PTR (its manifest over Live's, realm names, the
install from Live with hard links that never writes into Live, the copies from Live with backups) and the server
settings, `skipGameAgreements` and `ptrCopyLiveSettings` among them; the character summary file (Lua escaping, its format, and that
a failure keeps the old file). With `LAUNCHER_SAMPLE_MANIFEST=<path to manifest.json>` it also reads a manifest made by
`make-manifest.py`.

## Layout

- `src/Launcher.Core`: manifest, settings, file checks, downloads, game folder, credential store (no UI)
- `src/Launcher.App`: the window (Avalonia, like Anarres)
- `tests/Launcher.Core.Tests`: the tests
- `server/`: `make-manifest.py`, `upload.py`, play tickets, the character summary (`char-summary.py`, `talent-trees.py`) and the Apache examples

## Testing mode (server owner only)

Put a `testing.env` next to `TemucraftLauncher.exe` (copy `testing.env.example`; it is never in git). Each line is
`<file on this computer> = <path inside the WoW folder>`; `{locale}` becomes every game language picked. On every Play
the launcher copies the listed files into the game folder (only when they changed) and logs it. Remove a line, or the
whole file, and the next Play takes those files out again. Use names the server doesn't send (`patch-enUS-Z`, not
`patch-enUS-T`), or the update check will want to replace them.
