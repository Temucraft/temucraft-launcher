# Temucraft Launcher

A launcher for a WoW 3.3.5a server: players who have the client get the server's patches, addons
and locale files, checked by SHA-256, and start the game from it.

The files sit behind Apache with the same two checks as your website: an IP whitelist, then a username and
password. The server address lives in `launcher.env` next to the exe, which is git-ignored, so it never ends up in
the repository or in the exe.

## What it does

- Logs in with the Apache username and password (HTTP basic auth, https only unless `ALLOW_HTTP=1`). A wrong
  password and an address that isn't whitelisted each get their own message.
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
- The password can be saved in Windows Credential Manager; nothing secret is written to a file.

The window has five pages:

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
- **Mods**: cosmetic patches only (`options/<Group>/<Option>/...`: darker nights, login screens, model packs...), one
  pick per group; everything the game needs goes in `patches/` and is always installed;
  untracked patches in Data that neither the client nor the server hand out, with remove.
- **Settings**: account, game folder, locales, reopen the launcher after WoW closes, remember the
  window, launcher background picture, backups (WTF and AddOns zipped before each update and on request, restore),
  macros and key bindings export/import, verify game files, the log.

## The log

Everything the launcher does goes to `%AppData%\TemucraftLauncher\launcher.log` (Settings > Open log): each line has
the time and a level (INFO, WARN, ERROR). Errors are written with their full details (the exception and where it
happened), so a problem a friend reports can be found afterwards; the log box under Settings shows the same lines
without the details. Crashes anywhere in the launcher are caught and logged too. The file is renamed to
`launcher.1.log` (up to `.3`) when it passes 1 MB, so it never grows without end. Passwords are never logged.

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

2. Copy `server/apache-temucraft.conf.example` into your Apache config, set your whitelisted addresses and the path,
   create the users with `htpasswd`, and reload Apache. Use https.
3. Optional files next to the manifest: `client-version.txt`, `news.json`, `changelog.json`, `addons.json` and the
   `options/` folder for **cosmetic** patches and switchable fixes (an option group with an `On` and an empty `Off`
   folder and `"order": ["On", "Off"]` is on by default and can be turned off) (see `server/examples/` and the top of `make-manifest.py`); patches the
   game or the server need belong in `patches/`, which every player gets.
4. Each time you add or change files, rebuild the manifest:

   ```
   python3 server/make-manifest.py /var/www/wow-files --realmlist logon.example.com
   ```

   Hashes are cached in `.manifest-cache.json`, so only new or changed files are read again. Files that were in the
   last manifest and are gone now are listed as obsolete, and launchers delete them.

## Uploading (server/upload.py)

`upload.py` puts your files on the VPS over SSH and rebuilds the manifest: copy `server/upload.env.example` to
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
