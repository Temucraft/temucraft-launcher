#!/usr/bin/env python3
"""
Builds manifest.json for the launcher from the folder Apache serves.

Folder layout (FILES_DIR is the folder SERVER_URL points at):

    FILES_DIR/
        manifest.json                 written by this script
        launcher/<platform>/TemucraftLauncher[.exe]   the launcher per platform (windows, linux, macos-intel, macos-arm),
                                      for its self-check and self update (optional)
        patches/<file>                -> <WoW>/Data/<file>               always installed
        client/<path>                 -> <WoW>/<path>                    always installed (patched Wow.exe, dlls...)
        locales/<locale>/<file...>    -> <WoW>/Data/<locale>/<file...>   for the locales a player picks
        addons/<Addon>/<file...>      -> <WoW>/Interface/AddOns/<Addon>/...  when addons are on
        options/<Group>/<Option>/<path inside the WoW folder>            cosmetic patches only (darker nights...), installed
                                                                         when the player picks that option
                                      (e.g. options/Login screen/Classic/Data/patch-L.MPQ); options/<Group>/group.json
                                      may hold {"description": "...", "optional": true, "order": ["Classic", ...], "recommended": "HD",
                                      "links": {"GitHub": "https://..."}};
                                      an option in "order" with no folder installs nothing ("On" / "Off" fixes)
        client-version.txt            the client version shown in the launcher (e.g. "Temucraft 1.0.0")
        launcher-policy.json          ("_comment" notes allowed) the server's say over launcher settings: {"installAddons": {"value": true,
                                      "enforce": true}, "tweaks": {"farclip": {"value": 1277, "enforce": true}}, ...};
                                      see the launcher's README; options/<Group>/group.json may say "enforce": true too
        news.json, changelog.json     [{"date": "...", "title": "...", "text": "...", "link": "https://..."}]
        breakingnews.html             written by this script: the newest news and changes for the game's character
                                      screen (mod-breaking-news-override, BreakingNews.HtmlPath)
        addons.json                   [{"name": "...", "description": "...", "repo": "owner/repo", "branch": "main",
                                        "folders": ["Addon"], "required": true}]

Hashes are cached in .manifest-cache.json (by size and modified time), so re-running after adding one patch
doesn't re-read a 16 GB file. Files that were in the previous manifest but are gone now are listed as obsolete, so
launchers delete them (for example a renamed patch); files you keep elsewhere are never touched.

    python3 make-manifest.py /var/www/wow-files --realmlist logon.example.com
"""

import argparse
import hashlib
import json
import os
import sys

LOCALES = {"enUS", "enGB", "enCN", "enTW", "deDE", "frFR", "esES", "esMX", "ruRU", "koKR", "zhCN", "zhTW", "ptBR", "itIT"}
CACHE_NAME = ".manifest-cache.json"


# (runtime id, folder under launcher/, file name): what 3 - Build launcher puts there
LAUNCHERS = [
    ("win-x64", "windows", "TemucraftLauncher.exe"),
    ("linux-x64", "linux", "TemucraftLauncher"),
    ("osx-x64", "macos-intel", "TemucraftLauncher"),
    ("osx-arm64", "macos-arm", "TemucraftLauncher"),
]


def load_jsonc(path):
    """Reads JSON that may hold // line and /* block */ comments (launcher-policy.json, group.json); comments are only
    for the server's owner and never reach the launcher."""
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    out, i, in_string = [], 0, False
    while i < len(text):
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\" and i + 1 < len(text):
                out.append(text[i + 1]); i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True; out.append(c)
        elif text.startswith("//", i):
            while i < len(text) and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = len(text) if end < 0 else end + 2
            continue
        else:
            out.append(c)
        i += 1
    return drop_notes(json.loads("".join(out)))


def drop_notes(value):
    """Removes "_comment" (and any other "_" key): notes for whoever edits the file, never sent to the launcher."""
    if isinstance(value, dict):
        return {k: drop_notes(v) for k, v in value.items() if not k.startswith("_")}
    if isinstance(value, list):
        return [drop_notes(v) for v in value]
    return value


def sha256(path, cache):
    stat = os.stat(path)
    key = os.path.abspath(path)
    entry = cache.get(key)
    if entry and entry["size"] == stat.st_size and entry["mtime"] == stat.st_mtime_ns:
        return entry["sha256"]
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    cache[key] = {"size": stat.st_size, "mtime": stat.st_mtime_ns, "sha256": digest.hexdigest()}
    print(f"  hashed {os.path.relpath(path)}", file=sys.stderr)
    return cache[key]["sha256"]


def walk(folder):
    for root, dirs, files in os.walk(folder):
        dirs[:] = sorted(d for d in dirs if not d.startswith("."))
        for name in sorted(files):
            # .part/.upload/.tmp are unfinished downloads or uploads (upload.py writes <file>.upload, then renames it)
            if not name.startswith(".") and not name.endswith((".part", ".upload", ".tmp")):
                yield os.path.join(root, name)


def url_of(files_dir, path):
    return os.path.relpath(path, files_dir).replace(os.sep, "/")


def entry(files_dir, path, target, category, cache, locale=None, group=None, option=None):
    item = {
        "path": target,
        "url": url_of(files_dir, path),
        "size": os.path.getsize(path),
        "sha256": sha256(path, cache),
        "category": category,
    }
    if locale:
        item["locale"] = locale
    if group:
        item["group"] = group
        item["option"] = option
    return item


def build(files_dir, realmlist, cache):
    files = []

    patches = os.path.join(files_dir, "patches")
    if os.path.isdir(patches):
        for path in walk(patches):
            files.append(entry(files_dir, path, "Data/" + os.path.relpath(path, patches).replace(os.sep, "/"),
                               "patch", cache))

    client = os.path.join(files_dir, "client")
    if os.path.isdir(client):
        for path in walk(client):
            files.append(entry(files_dir, path, os.path.relpath(path, client).replace(os.sep, "/"), "patch", cache))

    locales = os.path.join(files_dir, "locales")
    if os.path.isdir(locales):
        for locale in sorted(os.listdir(locales)):
            folder = os.path.join(locales, locale)
            if not os.path.isdir(folder):
                continue
            if locale not in LOCALES:
                print(f"  skipping locales/{locale}: not a client locale ({', '.join(sorted(LOCALES))})",
                      file=sys.stderr)
                continue
            for path in walk(folder):
                files.append(entry(files_dir, path, f"Data/{locale}/" +
                                   os.path.relpath(path, folder).replace(os.sep, "/"), "locale", cache, locale))

    addons = os.path.join(files_dir, "addons")
    if os.path.isdir(addons):
        for path in walk(addons):
            files.append(entry(files_dir, path, "Interface/AddOns/" +
                               os.path.relpath(path, addons).replace(os.sep, "/"), "addon", cache))

    base = os.path.join(files_dir, "base")
    if os.path.isdir(base):
        for path in walk(base):
            files.append(entry(files_dir, path, os.path.relpath(path, base).replace(os.sep, "/"), "base", cache))

    groups = []
    options = os.path.join(files_dir, "options")
    if os.path.isdir(options):
        for group in sorted(os.listdir(options)):
            gdir = os.path.join(options, group)
            if not os.path.isdir(gdir) or group.startswith("."):
                continue
            info = {}
            if os.path.isfile(os.path.join(gdir, "group.json")):
                info = load_jsonc(os.path.join(gdir, "group.json"))
            # a folder counts as a choice only when it holds files: an empty one is left over from a removed choice
            names = sorted(o for o in os.listdir(gdir) if os.path.isdir(os.path.join(gdir, o)) and not o.startswith(".")
                           and any(walk(os.path.join(gdir, o))))
            # an option listed in "order" may have no folder: it installs nothing (e.g. "Off"; uploads skip empty folders)
            order = list(info.get("order", [])) + [o for o in names if o not in info.get("order", [])]
            for option in order:
                odir = os.path.join(gdir, option)
                if not os.path.isdir(odir):
                    continue
                for path in walk(odir):
                    files.append(entry(files_dir, path, os.path.relpath(path, odir).replace(os.sep, "/"), "option", cache,
                                       group=group, option=option))
            # preview.png (the whole group) or preview-<Option>.png/.jpg next to group.json: shown on the card, never installed
            previews = {}
            for name in sorted(os.listdir(gdir)):
                stem, ext = os.path.splitext(name)
                if os.path.isfile(os.path.join(gdir, name)) and ext.lower() in (".png", ".jpg", ".jpeg") and stem.lower().startswith("preview"):
                    previews[stem[8:] if stem.lower().startswith("preview-") else ""] = url_of(files_dir, os.path.join(gdir, name))
            # gallery-<Option>-<NN>-<Label>.png/.jpg next to group.json: more pictures of a choice, shown in the launcher's
            # gallery window (in NN order) with "galleryTitle" from group.json on its button; never installed
            gallery = {}
            for name in sorted(os.listdir(gdir)):
                stem, ext = os.path.splitext(name)
                if not (os.path.isfile(os.path.join(gdir, name)) and ext.lower() in (".png", ".jpg", ".jpeg")
                        and stem.lower().startswith("gallery-")):
                    continue
                parts = stem[8:].split("-", 2)
                if len(parts) == 3:
                    gallery.setdefault(parts[0], []).append({"name": parts[2], "url": url_of(files_dir, os.path.join(gdir, name))})
            # "links": {"GitHub": "https://...", "Website": "https://..."} (or "link": "https://..."): shown on the card
            links = info.get("links") or ({"Page": info["link"]} if info.get("link") else {})
            links = [{"name": str(n), "url": str(u)} for n, u in links.items() if str(u).startswith(("https://", "http://"))]
            groups.append({"name": group, "description": info.get("description", ""), "options": order,
                           "optional": bool(info.get("optional", False)), "enforce": bool(info.get("enforce", False)),
                           "links": links,
                           # "requires": {"Character models": "HD"}: the group can only be turned on with that pick
                           "requires": {str(k): str(v) for k, v in (info.get("requires") or {}).items()},
                           "previews": previews,
                           # "category": "World": the heading the card goes under on the Mods page
                           "category": str(info.get("category", "") or ""),
                           "gallery": gallery, "galleryTitle": str(info.get("galleryTitle", "") or ""),
                           # "recommended": "HD": marked in the launcher and picked for new installs
                           "recommended": str(info.get("recommended", "") or "")})

    # previews/login/<code>.png or .jpg (the scene's code in loginui.lua, e.g. "lk"): shown on the login screen list
    login_previews = {}
    pdir = os.path.join(files_dir, "previews", "login")
    if os.path.isdir(pdir):
        for name in sorted(os.listdir(pdir)):
            stem, ext = os.path.splitext(name)
            if os.path.isfile(os.path.join(pdir, name)) and ext.lower() in (".png", ".jpg", ".jpeg"):
                login_previews[stem.lower()] = url_of(files_dir, os.path.join(pdir, name))
    # previews/music/<code>.mp3: a short clip of a scene's music, played by the launcher's music choices
    music_previews = {}
    mdir = os.path.join(files_dir, "previews", "music")
    if os.path.isdir(mdir):
        for name in sorted(os.listdir(mdir)):
            stem, ext = os.path.splitext(name)
            if os.path.isfile(os.path.join(mdir, name)) and ext.lower() in (".mp3", ".ogg"):
                music_previews[stem.lower()] = url_of(files_dir, os.path.join(mdir, name))

    # An option file that lands on the same path as a normal file (a stale option left on the server after a patch got
    # its letter, for example) would make the launcher swap the two on every check. The normal file wins.
    fixed = {f["path"].lower() for f in files if f.get("category") != "option"}
    clashes = [f for f in files if f.get("category") == "option" and f["path"].lower() in fixed]
    for f in clashes:
        print(f"  skipping option file {f.get('group')}/{f.get('option')}/{f['path']}: a normal file already installs "
              f"{f['path']} (remove the old option file from the server)", file=sys.stderr)
    files = [f for f in files if f not in clashes]

    manifest = {"version": 1, "realmlist": realmlist, "clientVersion": "", "optionGroups": groups,
                "loginScreenPreviews": login_previews,
                "musicPreviews": music_previews,
                "files": files, "obsolete": [], "policy": {}}
    # launcher-policy.json: {"installAddons": {"value": true, "enforce": true}, ...}; enforced settings can't be changed
    policy_file = os.path.join(files_dir, "launcher-policy.json")
    if os.path.isfile(policy_file):
        policy = load_jsonc(policy_file)
        if not isinstance(policy, dict):
            sys.exit("launcher-policy.json must be an object: {\"setting\": {\"value\": ..., \"enforce\": true}, ...}")
        manifest["policy"] = policy   # passed through as it is: the launcher knows the names (unknown ones are skipped)
    version_file = os.path.join(files_dir, "client-version.txt")
    if os.path.isfile(version_file):
        with open(version_file) as f:
            manifest["clientVersion"] = f.read().strip()

    # the launcher for each platform (launcher/<folder>/), by .NET runtime id; "launcher" stays the Windows one for
    # launchers before 1.4, which only read that. An old launcher/TemucraftLauncher.exe still counts as Windows.
    launchers = {}
    for rid, folder, name in LAUNCHERS:
        path = os.path.join(files_dir, "launcher", folder, name)
        if os.path.isfile(path):
            launchers[rid] = entry(files_dir, path, name, "base", cache)
    old_exe = os.path.join(files_dir, "launcher", "TemucraftLauncher.exe")
    if "win-x64" not in launchers and os.path.isfile(old_exe):
        launchers["win-x64"] = entry(files_dir, old_exe, "TemucraftLauncher.exe", "base", cache)
    if launchers:
        manifest["launchers"] = launchers
    if "win-x64" in launchers:
        manifest["launcher"] = launchers["win-x64"]
    return manifest


def breaking_news(files_dir, posts=2, lines=8):
    """breakingnews.html for mod-breaking-news-override (the news box on the game's character screen): the newest
    news and changelog posts in WoW's SimpleHTML. Point BreakingNews.HtmlPath at this file."""
    import html
    import re
    def esc(text):
        text = re.sub(r"\[(\w[\w ]*)\]", r"\1:", text)   # changelog tags: "[New] x" -> "New: x"
        # SimpleHTML wants plain text; the module needs [ ] ' and \ escaped (it sends the page as a Lua string)
        text = html.escape(text, quote=False)
        for ch in "\\[]'":
            text = text.replace(ch, "\\" + ch)
        return text
    parts = []
    for name, heading in (("news.json", "News"), ("changelog.json", "Changes")):
        path = os.path.join(files_dir, name)
        if not os.path.isfile(path):
            continue
        try:
            items = load_jsonc(path)
        except ValueError:
            continue
        items = [i for i in items if isinstance(i, dict)][:posts]
        if not items:
            continue
        parts.append(f"<h1>{heading}</h1>")
        for item in items:
            parts.append(f"<h2>{esc(item.get('title', ''))}</h2>")
            if item.get("date"):
                parts.append(f"<p>{esc(item['date'])}</p>")
            text = [l.strip() for l in str(item.get("text", "")).splitlines() if l.strip()]
            for line in text[:lines]:
                parts.append(f"<p>{esc(line)}</p>")
            if len(text) > lines:
                parts.append("<p>...</p>")
            parts.append("<br/>")
    if not parts:
        return
    with open(os.path.join(files_dir, "breakingnews.html"), "w", encoding="utf-8") as f:
        f.write("<html><body>\n" + "\n".join(parts) + "\n</body></html>\n")


def main():
    parser = argparse.ArgumentParser(description="Write manifest.json for the Temucraft launcher.")
    parser.add_argument("files_dir", help="the folder Apache serves (where SERVER_URL points)")
    parser.add_argument("--realmlist", default=None,
                        help="logon server for realmlist.wtf (keeps the previous manifest's if left out; "
                             "127.0.0.1 the first time)")
    args = parser.parse_args()

    files_dir = os.path.abspath(args.files_dir)
    manifest_path = os.path.join(files_dir, "manifest.json")
    cache_path = os.path.join(files_dir, CACHE_NAME)

    cache = {}
    if os.path.isfile(cache_path):
        with open(cache_path) as f:
            cache = json.load(f)

    previous = None
    if os.path.isfile(manifest_path):
        with open(manifest_path) as f:
            previous = json.load(f)
    # the launcher writes realmlist.wtf into every installed locale from this, so it is never left empty
    realmlist = args.realmlist if args.realmlist is not None else (previous or {}).get("realmlist") or "127.0.0.1"

    manifest = build(files_dir, realmlist, cache)

    # Patches, addons and locale files that were handed out before and are gone now: launchers delete them.
    if previous:
        current = {f["path"] for f in manifest["files"]}
        gone = {f["path"] for f in previous.get("files", [])
                if f.get("category") not in ("base", "option") and f["path"] not in current}
        manifest["obsolete"] = sorted(gone | (set(previous.get("obsolete", [])) - current))

    with open(manifest_path + ".tmp", "w") as f:
        json.dump(manifest, f, indent=2)
    os.replace(manifest_path + ".tmp", manifest_path)
    with open(cache_path, "w") as f:
        json.dump(cache, f)

    breaking_news(files_dir)

    counts = {}
    for item in manifest["files"]:
        counts[item["category"]] = counts.get(item["category"], 0) + 1
    print(f"Wrote {manifest_path}: " + ", ".join(f"{n} {c}" for c, n in sorted(counts.items())) +
          (f", {len(manifest['obsolete'])} obsolete" if manifest["obsolete"] else "") +
          (", launcher" if "launcher" in manifest else ""))


if __name__ == "__main__":
    main()
