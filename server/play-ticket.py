#!/usr/bin/env python3
"""
Play tickets: the game's login port only opens for computers that came through the launcher.

When a player presses PLAY, the launcher (logged in to the files server) asks for SERVER_URL/play-ticket. Apache
writes that request to its own log (server/apache-play-ticket.conf.example). This script follows that log and, for
every request Apache let in (200), opens the login port to that IP for a few hours, using an ipset with a timeout.
Everyone else is dropped at the firewall, so starting Wow.exe by hand can't even reach the login. Players already in
the game stay in when their ticket runs out (only new logins need one); the launcher renews it while the game runs.

    sudo python3 play-ticket.py setup      make the ipsets and the firewall rules (safe to run again)
    sudo python3 play-ticket.py run        setup, then follow the log (what the systemd service runs)
    sudo python3 play-ticket.py status     who may log in right now
    sudo python3 play-ticket.py allow IP   let an address in by hand (hours from the settings)
    sudo python3 play-ticket.py teardown   remove the rules: the login port is open to everyone again

Settings: play-ticket.env next to this script (see play-ticket.env.example), or the same names as environment
variables. Needs ipset and iptables (apt install ipset iptables).
"""
import ipaddress
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
SET4, SET6 = "temucraft-play", "temucraft-play6"
CHAIN = "TEMUCRAFT-PLAY"


def settings():
    values = {
        "TICKET_LOG": "/var/log/apache2/temucraft-play-tickets.log",
        "TICKET_PORTS": "3724",         # the authserver; add 8085 (worldserver) to close that too
        "TICKET_HOURS": "12",
        "TICKET_ALWAYS": "",            # addresses that never need a ticket (your own PC, the website...), comma separated
        "TICKET_FILE": "/var/www/wow-files/play-ticket",
    }
    path = os.path.join(HERE, "play-ticket.env")
    if os.path.isfile(path):
        for raw in open(path, encoding="utf-8"):
            line = raw.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip('"')
    for key in list(values):
        values[key] = os.environ.get(key, values[key])
    return values


def sh(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True)


def seconds(cfg):
    return int(float(cfg["TICKET_HOURS"]) * 3600)


def ports(cfg):
    return [p.strip() for p in cfg["TICKET_PORTS"].split(",") if p.strip().isdigit()]


def setup(cfg):
    # the ipsets: an address stays in for TICKET_HOURS after its last ticket
    sh("ipset", "create", SET4, "hash:ip", "family", "inet", "timeout", str(seconds(cfg)), "-exist")
    sh("ipset", "create", SET6, "hash:ip", "family", "inet6", "timeout", str(seconds(cfg)), "-exist")
    for tool, ipset in (("iptables", SET4), ("ip6tables", SET6)):
        if sh(tool, "-L", CHAIN, check=False).returncode != 0:
            sh(tool, "-N", CHAIN)
        sh(tool, "-F", CHAIN)
        # this machine, then addresses with a ticket, then everyone else is dropped
        sh(tool, "-A", CHAIN, "-i", "lo", "-j", "RETURN")
        sh(tool, "-A", CHAIN, "-m", "set", "--match-set", ipset, "src", "-j", "RETURN")
        sh(tool, "-A", CHAIN, "-j", "DROP")
        for port in ports(cfg):
            rule = ["-p", "tcp", "--dport", port, "-j", CHAIN]
            if sh(tool, "-C", "INPUT", *rule, check=False).returncode != 0:
                sh(tool, "-I", "INPUT", *rule)
    for address in [a.strip() for a in cfg["TICKET_ALWAYS"].split(",") if a.strip()]:
        allow(cfg, address, timeout=0)   # 0: never runs out
    # the file the launcher asks for (Apache logs the request; the content doesn't matter)
    try:
        if not os.path.exists(cfg["TICKET_FILE"]):
            with open(cfg["TICKET_FILE"], "w") as f:
                f.write("ok\n")
    except OSError as error:
        print(f"Couldn't make {cfg['TICKET_FILE']}: {error}", file=sys.stderr)
    print(f"Login port(s) {', '.join(ports(cfg))} now only open for play tickets ({cfg['TICKET_HOURS']} h each).")


def teardown(cfg):
    for tool in ("iptables", "ip6tables"):
        for port in ports(cfg):
            sh(tool, "-D", "INPUT", "-p", "tcp", "--dport", port, "-j", CHAIN, check=False)
        sh(tool, "-F", CHAIN, check=False)
        sh(tool, "-X", CHAIN, check=False)
    sh("ipset", "destroy", SET4, check=False)
    sh("ipset", "destroy", SET6, check=False)
    print("Play tickets off: the login port is open to everyone again.")


def allow(cfg, address, timeout=None):
    try:
        ip = ipaddress.ip_address(address.split("%")[0])
    except ValueError:
        return False
    if ip.version == 6 and ip.ipv4_mapped:
        ip = ip.ipv4_mapped                         # ::ffff:1.2.3.4 is an IPv4 visitor
    name = SET4 if ip.version == 4 else SET6
    sh("ipset", "add", name, str(ip), "timeout", str(seconds(cfg) if timeout is None else timeout), "-exist")
    return True


def status(_cfg):
    for name in (SET4, SET6):
        result = sh("ipset", "list", name, check=False)
        print(result.stdout if result.returncode == 0 else f"{name}: not set up")


def follow(cfg):
    """Follows the ticket log (and its rotation): '<ip> <status> <user>' per request."""
    path = cfg["TICKET_LOG"]
    handle, inode = None, None
    while True:
        try:
            current = os.stat(path).st_ino
        except FileNotFoundError:
            time.sleep(2)
            continue
        if handle is None or current != inode:
            if handle:
                handle.close()
            handle = open(path, encoding="utf-8", errors="replace")
            if inode is None:
                handle.seek(0, os.SEEK_END)       # first start: only new tickets
            inode = current
        line = handle.readline()
        if not line:
            time.sleep(0.5)
            continue
        parts = line.split()
        if len(parts) >= 2 and parts[1] == "200" and allow(cfg, parts[0]):
            user = parts[2] if len(parts) > 2 else "-"
            print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} ticket: {parts[0]} ({user}) may log in for {cfg['TICKET_HOURS']} h", flush=True)


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("setup", "run", "status", "allow", "teardown"):
        print(__doc__)
        sys.exit(1)
    cfg = settings()
    command = sys.argv[1]
    if command == "setup":
        setup(cfg)
    elif command == "run":
        setup(cfg)
        follow(cfg)
    elif command == "status":
        status(cfg)
    elif command == "allow":
        if len(sys.argv) < 3 or not allow(cfg, sys.argv[2]):
            sys.exit("Give an IPv4 or IPv6 address.")
        print(f"{sys.argv[2]} may log in for {cfg['TICKET_HOURS']} h.")
    else:
        teardown(cfg)


if __name__ == "__main__":
    main()
