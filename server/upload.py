#!/usr/bin/env python3
"""
Uploader: puts your release files and server builds on the VPS over SSH (SFTP), then rebuilds the launcher's manifest.

    pip install paramiko
    cp upload.env.example upload.env      # fill in host, user, key and folders (upload.env stays out of git)

    python3 upload.py files  <local folder>     # the launcher's files: patches/, locales/, addons/, options/,
                                                # launcher/, news.json, changelog.json, addons.json, client-version.txt
    python3 upload.py server <local folder>     # a server build (worldserver, authserver, configs, data...)
    python3 upload.py manifest                  # only rebuild manifest.json on the VPS

Only new or changed files are sent (size and modified time are compared; a changed file is written to a temporary
name and renamed when complete, so players never download half a file). --delete also removes files on the VPS that
are gone locally. --dry-run shows what would happen. For `server`, PRE_SERVER_CMD / POST_SERVER_CMD run before and
after (e.g. stop and start the worldserver service).
"""

import argparse
import os
import posixpath
import stat
import sys

KEEP_REMOTE = {"manifest.json", ".manifest-cache.json", "make-manifest.py"}


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


def sync(client, local, remote, delete, dry, skip=()):
    sftp = client.open_sftp()
    have = remote_files(sftp, remote)
    made, sent, total = set(), 0, 0
    local_set = set()
    for root, dirs, files in os.walk(local):
        dirs[:] = [d for d in dirs if not d.startswith(".")]
        for name in files:
            if name.startswith(".") or name.endswith((".part", ".tmp")):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, local).replace(os.sep, "/")
            if rel in skip:
                continue
            local_set.add(rel)
            st = os.stat(path)
            if have.get(rel) == (st.st_size, int(st.st_mtime)):
                continue
            target = posixpath.join(remote, rel)
            print(f"  upload {rel} ({st.st_size / 1048576:.1f} MB)")
            total += st.st_size
            sent += 1
            if dry:
                continue
            makedirs(sftp, posixpath.dirname(target), made)
            sftp.put(path, target + ".upload")
            sftp.utime(target + ".upload", (int(st.st_atime), int(st.st_mtime)))
            sftp.posix_rename(target + ".upload", target)
    removed = 0
    if delete:
        for rel in sorted(set(have) - local_set):
            if posixpath.basename(rel) in KEEP_REMOTE or rel.endswith(".upload"):
                continue
            print(f"  delete {rel}")
            removed += 1
            if not dry:
                sftp.remove(posixpath.join(remote, rel))
    sftp.close()
    print(f"{sent} file(s) uploaded ({total / 1048576:.1f} MB), {removed} deleted" + (" [dry run]" if dry else ""))


def manifest(client, env, dry):
    files_dir = env.get("REMOTE_FILES_DIR")
    if not files_dir:
        sys.exit("upload.env has no REMOTE_FILES_DIR.")
    tool = env.get("REMOTE_MANIFEST_TOOL", posixpath.join(files_dir, "..", "make-manifest.py"))
    command = f"python3 '{tool}' '{files_dir}'"
    if env.get("REALMLIST"):
        command += f" --realmlist '{env['REALMLIST']}'"
    run(client, command, dry)


def main():
    parser = argparse.ArgumentParser(description="Upload release files or a server build to the VPS.")
    parser.add_argument("what", choices=["files", "server", "manifest"])
    parser.add_argument("folder", nargs="?")
    parser.add_argument("--env", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "upload.env"))
    parser.add_argument("--delete", action="store_true", help="remove files on the VPS that are gone locally")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    env = load_env(args.env)
    client = connect(env)
    try:
        if args.what == "manifest":
            manifest(client, env, args.dry_run)
            return
        if not args.folder or not os.path.isdir(args.folder):
            sys.exit("Give the local folder to upload.")
        if args.what == "files":
            sync(client, args.folder, env["REMOTE_FILES_DIR"], args.delete, args.dry_run, skip={"manifest.json"})
            manifest(client, env, args.dry_run)
        else:
            if not env.get("REMOTE_SERVER_DIR"):
                sys.exit("upload.env has no REMOTE_SERVER_DIR.")
            if env.get("PRE_SERVER_CMD"):
                run(client, env["PRE_SERVER_CMD"], args.dry_run)
            sync(client, args.folder, env["REMOTE_SERVER_DIR"], args.delete, args.dry_run)
            if env.get("POST_SERVER_CMD"):
                run(client, env["POST_SERVER_CMD"], args.dry_run)
    finally:
        client.close()


if __name__ == "__main__":
    main()
