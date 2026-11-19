#!/usr/bin/env python3
"""
Builds manifest.json for the launcher from the folder Apache serves.

Folder layout (FILES_DIR is the folder SERVER_URL points at):

    FILES_DIR/
        manifest.json                 written by this script
        launcher/TemucraftLauncher.exe   the launcher, for its self-check (optional)
        patches/<file>                -> <WoW>/Data/<file>               always installed
        client/<path>                 -> <WoW>/<path>                    always installed (patched Wow.exe, dlls...)
        locales/<locale>/<file...>    -> <WoW>/Data/<locale>/<file...>   for the locales a player picks
        addons/<Addon>/<file...>      -> <WoW>/Interface/AddOns/<Addon>/...  when addons are on
        options/<Group>/<Option>/<path inside the WoW folder>            cosmetic patches only (darker nights...), installed
                                                                         when the player picks that option
                                      (e.g. options/Login screen/Classic/Data/patch-L.MPQ); options/<Group>/group.json
                                      may hold {"description": "...", "optional": true, "order": ["Classic", ...]};
                                      an option in "order" with no folder installs nothing ("On" / "Off" fixes)
        client-version.txt            the client version shown in the launcher (e.g. "Temucraft 1.0.0")
        news.json, changelog.json     [{"date": "...", "title": "...", "text": "...", "link": "https://..."}]
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
                with open(os.path.join(gdir, "group.json")) as f:
                    info = json.load(f)
            names = sorted(o for o in os.listdir(gdir) if os.path.isdir(os.path.join(gdir, o)) and not o.startswith("."))
            # an option listed in "order" may have no folder: it installs nothing (e.g. "Off"; uploads skip empty folders)
            order = list(info.get("order", [])) + [o for o in names if o not in info.get("order", [])]
            for option in order:
                odir = os.path.join(gdir, option)
                if not os.path.isdir(odir):
                    continue
                for path in walk(odir):
                    files.append(entry(files_dir, path, os.path.relpath(path, odir).replace(os.sep, "/"), "option", cache,
                                       group=group, option=option))
            groups.append({"name": group, "description": info.get("description", ""), "options": order,
                           "optional": bool(info.get("optional", False))})

    manifest = {"version": 1, "realmlist": realmlist, "clientVersion": "", "optionGroups": groups,
                "files": files, "obsolete": []}
    version_file = os.path.join(files_dir, "client-version.txt")
    if os.path.isfile(version_file):
        with open(version_file) as f:
            manifest["clientVersion"] = f.read().strip()

    launcher = os.path.join(files_dir, "launcher", "TemucraftLauncher.exe")
    if os.path.isfile(launcher):
        manifest["launcher"] = entry(files_dir, launcher, "TemucraftLauncher.exe", "base", cache)
    return manifest


def main():
    parser = argparse.ArgumentParser(description="Write manifest.json for the Temucraft launcher.")
    parser.add_argument("files_dir", help="the folder Apache serves (where SERVER_URL points)")
    parser.add_argument("--realmlist", default=None,
                        help="logon server for realmlist.wtf (keeps the previous manifest's if left out)")
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
    realmlist = args.realmlist if args.realmlist is not None else (previous or {}).get("realmlist", "")

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

    counts = {}
    for item in manifest["files"]:
        counts[item["category"]] = counts.get(item["category"], 0) + 1
    print(f"Wrote {manifest_path}: " + ", ".join(f"{n} {c}" for c, n in sorted(counts.items())) +
          (f", {len(manifest['obsolete'])} obsolete" if manifest["obsolete"] else "") +
          (", launcher" if "launcher" in manifest else ""))


if __name__ == "__main__":
    main()
