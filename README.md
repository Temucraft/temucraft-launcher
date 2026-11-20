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
- Play: clears the client's `Cache` folder (optional) and starts `Wow.exe`.
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
- **Settings**: account, game folder (and installing the game into an empty one), languages, game settings (the
  server's addons, clearing the Cache), backups (WTF and AddOns zipped before each update and on request, restore),
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
| `largeAddressAware` | true / false | Wow.exe's 4 GB switch; enforced, it's set again after every update |
| `launcherUpdate` | "required" / "recommended" | A new launcher: installed before anything else (the default), or only a banner |
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

**The computer stays awake** while the launcher downloads, installs or checks files, so a long download goes on
with the screen locked or off (Windows: SetThreadExecutionState; macOS: caffeinate; Linux: systemd-inhibit). It may
sleep again as soon as the job ends.

**Automatic updates** (off by default): when the launcher opens and every 30 minutes, optionally only between two
hours, and never while the game is running.

**Launcher updates.** When the server hands out a different launcher (`publish.sh` puts the new one in the files
folder), a banner recommends updating and shows the changelog's newest "Launcher ..." entry. "Update the launcher"
downloads it (checked against the manifest), swaps it with the running one and restarts; "Later" hides the banner
until the next start. It is never forced.

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

## Shared settings between accounts

On the Game files page, "Keep my settings, key bindings and macros the same on all my accounts on this PC" copies the
newest `config-cache.wtf` (auto loot and the other account settings), `bindings-cache.wtf` and `macros-cache.txt` of
each account in `WTF\Account` over the older ones, before the game starts and after it closes (a backup is made
first). It can also share addon settings (`SavedVariables`) and each character's own key bindings and macros. The
server can do the same between PCs: `mod-temucraft-account-wide` (`Temucraft.AccountWide.Settings.*`).

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

The bottom bar shows the client version (`client-version.txt`), whether an update is waiting (PLAY turns into
UPDATE) and the untracked-files warning.

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
3. Optional files next to the manifest: `client-version.txt`, `news.json`, `changelog.json`, `addons.json` and the
   `options/` folder for **cosmetic** patches and switchable fixes (an option group with an `On` folder and `"order": ["On", "Off"]` in its `group.json` is on by
   default and can be turned off; `Off` needs no folder) (see `server/examples/` and the top of `make-manifest.py`); patches the
   game or the server need belong in `patches/`, which every player gets.
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
```

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
sub folder (and the zip fallback), README previews and the log. With `LAUNCHER_SAMPLE_MANIFEST=<path to manifest.json>` it also reads a manifest made by
`make-manifest.py`.

## Layout

- `src/Launcher.Core`: manifest, settings, file checks, downloads, game folder, credential store (no UI)
- `src/Launcher.App`: the window (Avalonia, like Anarres)
- `tests/Launcher.Core.Tests`: the tests
- `server/`: `make-manifest.py` and the Apache example
