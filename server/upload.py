#!/usr/bin/env python3
"""
Uploader: puts your release files and server builds on the VPS over SSH (SFTP), then rebuilds the launcher's manifest.

    pip install paramiko
    cp upload.env.example upload.env      # fill in host, user, key and folders (upload.env stays out of git)

    python3 upload.py files  <local folder>     # the launcher's files: patches/, locales/, addons/, options/,
                                                # launcher/, news.json, changelog.json, addons.json, client-version.txt
    python3 upload.py server <local folder>     # a server build (worldserver, authserver, configs, data...)
    python3 upload.py manifest                  # only rebuild manifest.json on the VPS
    python3 upload.py ptr    <local folder>     # the PTR (test realm): "5) Release\\ptr" goes to the served folder's
                                                # ptr/ (only the files that differ from Live), then ptr/manifest.json
                                                # is rebuilt there; `files` never touches ptr/

Only new or changed files are sent (size and modified time are compared; a changed file is written to a temporary
name and renamed when complete, so players never download half a file). A file cut off by a dropped connection or
Ctrl+C carries on where it stopped the next time you run the same command (and is checked by SHA-256 against yours).
The full client in base/ goes last, so the files every player needs arrive first. Files are sent with the system's
own sftp (OpenSSH, built into Windows 10/11) when it's there: it is several times faster than paramiko over a long
connection. Files under 16 MB go over the already open connection instead, since each OpenSSH run logs in again. USE_OPENSSH=0 in upload.env uses paramiko for everything. --delete also removes files on the VPS that
are gone locally, except hidden files and, for `server`, *.conf, *.log and the logs/ and data/ folders. For `files`,
files under options/ that are gone locally are always removed (an option you dropped must leave the manifest);
--keep-old-options keeps them.
--dry-run shows what would happen. For `server`, PRE_SERVER_CMD / POST_SERVER_CMD run before and
after (e.g. stop and start the worldserver service).
"""

import argparse
import hashlib
import os
import posixpath
import shlex
import shutil
import subprocess
import threading
import stat
import sys
import time

KEEP_REMOTE = {"manifest.json", ".manifest-cache.json", "make-manifest.py", "play-ticket", "breakingnews.html"}

# `server --delete` never removes these: the live configs, logs, the client data the server reads, and anything hidden
SERVER_KEEP_DIRS = {"logs", "log", "data", "etc/backup"}
SERVER_KEEP_SUFFIXES = (".conf", ".log", ".pid")


def protected(rel, server):
    """True when --delete must leave a remote file alone."""
    parts = rel.split("/")
    if any(p.startswith(".") for p in parts) or posixpath.basename(rel) in KEEP_REMOTE or rel.endswith(".upload"):
        return True   # hidden files (.htaccess...) are never uploaded, so they would always look "gone locally"
    if server:
        lowered = rel.lower()
        if lowered.endswith(SERVER_KEEP_SUFFIXES):
            return True
        if any(lowered == d or lowered.startswith(d + "/") for d in SERVER_KEEP_DIRS):
            return True
    return False


def load_env(path):
    values = {}
    if not os.path.isfile(path):
        sys.exit(f"{path} is missing: copy upload.env.example to upload.env and fill it in.")
    with open(path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip('"')
    for key in ("SSH_HOST", "SSH_USER"):
        if not values.get(key):
            sys.exit(f"upload.env has no {key}.")
    return values


def connect(env):
    try:
        import paramiko
    except ImportError:
        sys.exit("This needs paramiko: pip install paramiko")
    client = paramiko.SSHClient()
    client.load_system_host_keys()
    known = os.path.expanduser("~/.ssh/known_hosts")
    if os.path.isfile(known):
        client.load_host_keys(known)
    if env.get("TRUST_NEW_HOST") == "1":
        client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(env["SSH_HOST"], port=int(env.get("SSH_PORT", "22")), username=env["SSH_USER"],
                   key_filename=os.path.expanduser(env["SSH_KEY"]) if env.get("SSH_KEY") else None,
                   password=env.get("SSH_PASSWORD") or None)
    return client


def run(client, command, dry):
    print(f"$ {command}")
    if dry:
        return
    _, out, err = client.exec_command(command)
    code = out.channel.recv_exit_status()
    sys.stdout.write(out.read().decode(errors="replace"))
    sys.stderr.write(err.read().decode(errors="replace"))
    if code != 0:
        sys.exit(f"remote command failed ({code}): {command}")


def remote_files_fast(client, root):
    """Every file under root with its size and time, from one `find` on the server (one round trip instead of one per
    folder); None when the server's find can't do it, so the caller lists folder by folder instead."""
    command = f"find {shlex.quote(root)} -type f -printf '%P\\0%s\\0%T@\\0' 2>/dev/null; echo"
    _, out, _ = client.exec_command(command)
    data = out.read()
    if out.channel.recv_exit_status() != 0:
        return None
    parts = data.rstrip(b"\n").split(b"\0")
    if len(parts) % 3 != 1:   # three fields per file and the empty tail
        return None
    found = {}
    for i in range(0, len(parts) - 1, 3):
        try:
            found[parts[i].decode("utf-8", "surrogateescape")] = (int(parts[i + 1]), int(float(parts[i + 2])))
        except ValueError:
            return None
    return found


def remote_files(sftp, root):
    found = {}

    def walk(folder, rel):
        try:
            entries = sftp.listdir_attr(folder)
        except FileNotFoundError:
            return
        for e in entries:
            path, relpath = posixpath.join(folder, e.filename), posixpath.join(rel, e.filename) if rel else e.filename
            if stat.S_ISDIR(e.st_mode):
                walk(path, relpath)
            else:
                found[relpath] = (e.st_size, int(e.st_mtime))
    walk(root, "")
    return found


def makedirs(sftp, folder, made):
    if folder in made or folder in ("", "/"):
        return
    makedirs(sftp, posixpath.dirname(folder), made)
    try:
        sftp.stat(folder)
    except FileNotFoundError:
        sftp.mkdir(folder)
    made.add(folder)


CHUNK = 1 << 20
OPENSSH_MIN_BYTES = 16 << 20


def sha256_local(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest()


def sha256_remote(client, path):
    _, out, _ = client.exec_command(f"sha256sum {shlex.quote(path)}")
    code = out.channel.recv_exit_status()
    text = out.read().decode(errors="replace").split()
    return text[0] if code == 0 and text else None


def openssh_command(env):
    """The system's own sftp (OpenSSH, built into Windows 10/11), which is several times faster than paramiko over a
    long connection. None when it isn't installed or USE_OPENSSH=0."""
    sftp = shutil.which("sftp")
    if not sftp or env.get("USE_OPENSSH", "1") == "0":
        return None
    command = [sftp, "-b", "-", "-P", env.get("SSH_PORT", "22"), "-o", "BatchMode=yes"]
    if env.get("SSH_KEY"):
        command += ["-i", os.path.expanduser(env["SSH_KEY"])]
    if env.get("TRUST_NEW_HOST") == "1":
        command += ["-o", "StrictHostKeyChecking=accept-new"]
    return command + [f"{env['SSH_USER']}@{env['SSH_HOST']}"]


def sftp_quote(text):
    return '"' + text.replace("\\", "/").replace('"', '\\"') + '"'


def put_openssh(command, sftp, path, temp, size, start):
    """Sends path to temp with OpenSSH's sftp (reput carries on from the remote file's size); shows progress by
    asking the server how big temp is every few seconds."""
    verb = "reput" if start else "put"
    finished = threading.Event()

    def progress():
        began, first = time.monotonic(), start
        while not finished.wait(3):
            try:
                done = sftp.stat(temp).st_size
            except (IOError, OSError):
                continue
            speed = (done - first) / max(time.monotonic() - began, 0.001)
            left = (size - done) / speed if speed > 0 else 0
            print(f"\r    {done * 100 // max(size, 1):3d}%  {done / 1048576:9.1f} / {size / 1048576:.1f} MB  {speed / 1048576:5.2f} MB/s"
                  f"  {int(left // 3600)}h{int(left % 3600 // 60):02d}m left   ", end="", flush=True)

    watcher = threading.Thread(target=progress, daemon=True)
    watcher.start()
    try:
        result = subprocess.run(command, input=f"{verb} {sftp_quote(os.path.abspath(path))} {sftp_quote(temp)}\n",
                                text=True, capture_output=True)
    finally:
        finished.set()
        watcher.join()
    print(f"\r    {size / 1048576:9.1f} / {size / 1048576:.1f} MB sent" + " " * 30 if result.returncode == 0 else "")
    if result.returncode != 0:
        sys.exit(f"sftp failed ({result.returncode}): {(result.stderr or result.stdout).strip()}\n"
                 "Run the same command again to carry on from where it stopped.")
    if sftp.stat(temp).st_size != size:
        sys.exit(f"{temp} has the wrong size after the upload; run the same command again.")


def put(client, sftp, path, target, size, already, openssh=None):
    """Uploads path to target + ".upload", carrying on from `already` bytes left by a cut-off upload, then checks
    the result. A resumed file is compared by SHA-256 with the local one; on a mismatch it is sent again whole."""
    temp = target + ".upload"
    for attempt in (1, 2):
        start = already if 0 < already < size else 0
        if start:
            print(f"    resuming at {start / 1048576:.1f} MB")
        # each OpenSSH run logs in anew (a second or two), so small files go over the open paramiko connection
        if openssh and size >= OPENSSH_MIN_BYTES:
            if not start:
                try:
                    sftp.remove(temp)   # put would overwrite it anyway; this makes the progress start at 0
                except IOError:
                    pass
            put_openssh(openssh, sftp, path, temp, size, start)
            if not start:
                return
            local_sha, remote_sha = sha256_local(path), sha256_remote(client, temp)
            if remote_sha is None or local_sha == remote_sha:
                return
            print(f"    the resumed copy doesn't match (sha256), sending it again whole")
            already = 0
            continue
        began, last = time.monotonic(), 0.0
        with open(path, "rb") as source, sftp.open(temp, "ab" if start else "wb") as out:
            out.set_pipelined(True)
            source.seek(start)
            done = start
            for block in iter(lambda: source.read(CHUNK), b""):
                out.write(block)
                done += len(block)
                now = time.monotonic()
                if size >= 8 << 20 and (now - last >= 2 or done == size):   # progress only for big files
                    last = now
                    speed = (done - start) / max(now - began, 0.001)
                    left = (size - done) / speed if speed > 0 else 0
                    print(f"\r    {done * 100 // max(size, 1):3d}%  {done / 1048576:9.1f} / {size / 1048576:.1f} MB  {speed / 1048576:5.2f} MB/s"
                          f"  {int(left // 3600)}h{int(left % 3600 // 60):02d}m left   ", end="", flush=True)
        if size >= 8 << 20:
            print()
        if not start:
            return
        local_sha, remote_sha = sha256_local(path), sha256_remote(client, temp)
        if remote_sha is None or local_sha == remote_sha:
            return   # no sha256sum on the server: trust the resumed copy (the size already matches)
        print(f"    the resumed copy doesn't match (sha256), sending it again whole")
        already = 0


def sync(client, local, remote, delete, dry, skip=(), server=False, openssh=None, delete_under=(), skip_top=()):
    """skip_top: top folders of `local` and `remote` left out completely (not sent, not listed, never deleted)."""
    sftp = client.open_sftp()
    if openssh and not dry:
        print("Sending big files with the system's sftp (OpenSSH), small ones over the open connection.")
    print(f"Listing what's already on the server in {remote}...", flush=True)
    have = remote_files_fast(client, remote)
    if have is None:
        print("  (the server's find can't list it in one go; listing folder by folder)", flush=True)
        have = remote_files(sftp, remote)
    have = {rel: v for rel, v in have.items() if rel.split("/")[0] not in skip_top}
    print(f"  {len(have)} file(s) there. Comparing with {local}...", flush=True)
    checked = 0
    made, sent, total = set(), 0, 0
    local_set = set()
    pending = []
    for root, dirs, files in os.walk(local):
        # base/ (the full client, only for new players) goes last, so the files every player needs arrive first
        dirs[:] = sorted((d for d in dirs if not d.startswith(".") and not (root == local and d in skip_top)),
                         key=lambda d: (root == local and d == "base", d))
        for name in files:
            if name.startswith(".") or name.endswith((".part", ".tmp")):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, local).replace(os.sep, "/")
            if rel in skip:
                continue
            local_set.add(rel)
            checked += 1
            st = os.stat(path)
            if have.get(rel) == (st.st_size, int(st.st_mtime)):
                continue
            pending.append((rel, path, st))
    # overall progress (files and bytes) on every line, even for small files that go by fast
    grand = sum(st.st_size for _, _, st in pending) or 1
    for rel, path, st in pending:
        target = posixpath.join(remote, rel)
        sent += 1
        print(f"  [{sent}/{len(pending)} {total * 100 // grand:3d}%] upload {rel} ({st.st_size / 1048576:.1f} MB)")
        total += st.st_size
        if dry:
            continue
        makedirs(sftp, posixpath.dirname(target), made)
        put(client, sftp, path, target, st.st_size, (have.get(rel + ".upload") or (0, 0))[0], openssh)
        sftp.utime(target + ".upload", (int(st.st_atime), int(st.st_mtime)))
        sftp.posix_rename(target + ".upload", target)
    if pending:
        print(f"  [{sent}/{len(pending)} 100%] done")
    removed = 0
    if delete or delete_under:
        # delete_under: only these top folders (options/ for "files": an option or choice removed locally goes away on
        # the server too, or the manifest would still offer it)
        for rel in sorted(r for r in set(have) - local_set if delete or r.split("/")[0] in delete_under):
            if protected(rel, server):
                if server:
                    print(f"  keep   {rel}")
                continue
            print(f"  delete {rel}")
            removed += 1
            if not dry:
                sftp.remove(posixpath.join(remote, rel))
        # folders left empty (a removed choice like Character screens/Original) go too: an empty choice folder on the
        # server would still be offered by the manifest of an older make-manifest.py
        for top in delete_under:
            folder = posixpath.join(remote, top)
            if dry:
                print(f"  would remove empty folders under {top}/")
                continue
            _, out, _ = client.exec_command(f"find {shlex.quote(folder)} -mindepth 1 -type d -empty -print -delete")
            for line in out.read().decode(errors="replace").splitlines():
                print(f"  delete empty folder {line[len(remote) + 1:] if line.startswith(remote) else line}")
    sftp.close()
    print(f"{checked} file(s) checked: {sent} uploaded ({total / 1048576:.1f} MB), {checked - sent} already up to date,"
          f" {removed} deleted" + (" [dry run]" if dry else ""))


PTR_DIR = "ptr"   # the PTR's channel: a folder of the served folder, with its own manifest.json


def manifest(client, env, dry, channel=None):
    files_dir = env.get("REMOTE_FILES_DIR")
    if not files_dir:
        sys.exit("upload.env has no REMOTE_FILES_DIR.")
    tool = env.get("REMOTE_MANIFEST_TOOL", posixpath.join(files_dir, "..", "make-manifest.py"))
    if channel:
        files_dir = posixpath.join(files_dir, channel)
    # the server's manifest tool is kept the same version as this one (it knows the launcher's newest settings)
    local_tool = os.path.join(os.path.dirname(os.path.abspath(__file__)), "make-manifest.py")
    print(f"Updating {tool} on the server...", flush=True)
    if not dry:
        sftp = client.open_sftp()
        sftp.put(local_tool, tool)
        sftp.close()
    print(f"Rebuilding {posixpath.join(channel, 'manifest.json') if channel else 'manifest.json'} on the server (new or changed files are hashed)...", flush=True)
    command = f"python3 {shlex.quote(tool)} {shlex.quote(files_dir)}"
    if env.get("REALMLIST"):
        command += f" --realmlist {shlex.quote(env['REALMLIST'])}"
    run(client, command, dry)


def main():
    parser = argparse.ArgumentParser(description="Upload release files, developer tools (dev, never served) or a server build to the VPS.")
    parser.add_argument("what", choices=["files", "ptr", "server", "manifest", "dev"])
    parser.add_argument("folder", nargs="?")
    parser.add_argument("--env", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "upload.env"))
    parser.add_argument("--delete", action="store_true", help="remove files on the VPS that are gone locally")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--keep-old-options", action="store_true",
                        help="files: don't remove options/ files on the VPS that are gone locally (removed by default)")
    args = parser.parse_args()
    env = load_env(args.env)
    client = connect(env)
    try:
        if args.what == "manifest":
            manifest(client, env, args.dry_run)
            return
        if not args.folder or not os.path.isdir(args.folder):
            sys.exit("Give the local folder to upload.")
        if args.what == "dev":
            # developer tools (_dev: the login screen tool, screenshots...) kept on the VPS but never served: only
            # reachable over SSH, so the folder must be outside the web folder
            remote = env.get("REMOTE_DEV_DIR") or "/opt/temucraft-dev"
            web = (env.get("REMOTE_FILES_DIR") or "/var/www").rstrip("/")
            if remote.rstrip("/").startswith(web) or remote.startswith("/var/www"):
                sys.exit(f"REMOTE_DEV_DIR ({remote}) is inside the web folder; pick one players can't download from.")
            sync(client, args.folder, remote, args.delete, args.dry_run, openssh=openssh_command(env))
            return
        if args.what == "files":
            # ptr/ is the PTR's own channel (upload.py ptr): never sent or deleted from here
            sync(client, args.folder, env["REMOTE_FILES_DIR"], args.delete, args.dry_run, skip={"manifest.json"},
                 openssh=openssh_command(env), delete_under=() if args.keep_old_options else ("options",), skip_top={PTR_DIR})
            manifest(client, env, args.dry_run)
        elif args.what == "ptr":
            if os.path.basename(os.path.normpath(args.folder)).lower() != PTR_DIR:
                sys.exit(f'Give the PTR folder itself (e.g. "F:\\TemuCraft Server\\5) Release\\{PTR_DIR}").')
            if os.path.isdir(os.path.join(args.folder, PTR_DIR)) or os.path.isdir(os.path.join(args.folder, "base")):
                sys.exit("That looks like the whole release folder, not its ptr/ folder.")
            remote = posixpath.join(env["REMOTE_FILES_DIR"], PTR_DIR)
            # like files: options/ that are gone locally go on the server too (--delete removes the rest)
            sync(client, args.folder, remote, args.delete, args.dry_run, skip={"manifest.json"},
                 openssh=openssh_command(env), delete_under=() if args.keep_old_options else ("options",))
            manifest(client, env, args.dry_run, channel=PTR_DIR)
        else:
            if not env.get("REMOTE_SERVER_DIR"):
                sys.exit("upload.env has no REMOTE_SERVER_DIR.")
            if env.get("PRE_SERVER_CMD"):
                run(client, env["PRE_SERVER_CMD"], args.dry_run)
            sync(client, args.folder, env["REMOTE_SERVER_DIR"], args.delete, args.dry_run, server=True,
                 openssh=openssh_command(env))
            if env.get("POST_SERVER_CMD"):
                run(client, env["POST_SERVER_CMD"], args.dry_run)
    finally:
        client.close()


if __name__ == "__main__":
    main()
