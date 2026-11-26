#!/usr/bin/env python3
"""
Character summary for the launcher: spec, professions, gold and unread mail of the logged-in player's characters, for
the character select screen (the card tooltips and the mail badge of TemucraftCharSelect.lua in patch-^).

The launcher asks for SERVER_URL/char-summary before PLAY and when the player switches between Live and PTR, with the
same login as the game files and the play ticket, and writes the answer into the game folder as
Interface\\TemucraftCharSummary.lua. This script is a CGI program: it sits in the folder Apache serves for the launcher
(next to play-ticket), so Apache runs the same checks on it as on every file there (the IP list and the username and
password, server/apache-temucraft.conf.example) before it runs. Which game account it answers about:

  - With a character ticket (the X-Temucraft-Ticket header): the game account the ticket was issued to. The launcher
    gets one by logging in with the player's GAME account name and password once (POST action=login, checked against
    acore_auth.account's SRP6 salt and verifier, like the login server does); only the ticket is kept on the PC. The
    tickets are in a small SQLite file (TICKET_DB), only as their SHA-256, with the account, the time, the IP they were
    issued to and a fingerprint of the account's salt and verifier: a changed password ends them. TICKET_DAYS (0 or -1:
    never) ends them after that many days; TICKET_SAME_IP=1 makes them work only from the IP they were issued to.
    Failed logins are limited per IP and per account (TICKET_LOGIN_RATE). POST action=logout ends a ticket.
  - Without a ticket, the username Apache checked (REMOTE_USER), as before:
    - char-summary-accounts.txt (ACCOUNT_MAP): "launcher login = GAMEACCOUNT, GAMEACCOUNT2" lines, for logins whose
      game account has another name (or several game accounts). A login listed there gets only those accounts.
    - otherwise, with SAME_NAME=1 (the default), the game account with the same name as the launcher login.
    - otherwise nothing (an empty answer). With one shared launcher login for everybody, set SAME_NAME=0.

    GET  char-summary                                       the summary (X-Temucraft-Ticket: <ticket>, or by REMOTE_USER)
         403 {"error": "ticket", "reason": "unknown|expired|ip|password"}   the ticket no longer works: log in again
    POST char-summary  action=login&account=..&password=..  200 {"ticket": "...", "account": "RYAN", "expires": 0}
         403 {"error": "login"}  wrong account or password;  429 {"error": "rate", "retry_after": 600}  too many tries
    POST char-summary  action=logout  (with the ticket header)  200 {"ok": true}

No account names, ids, guids or IPs are sent back: per realm (realm id and its realmlist name) the characters by name,
with what the card needs. One query per table per realm, all by the account ids.

    {"generated": 1764100000, "realms": [{"id": 1, "name": "Temucraft", "characters": {
        "Thrall": {"spec": "Restoration", "professions": ["Mining", "Herbalism"], "money": 162575561,
                   "mail": ["Jaina", "Auction House"]}}}]}

Settings: char-summary.env (see char-summary.env.example) in /etc/temucraft/, or where the CHAR_SUMMARY_ENV environment
variable points (SetEnv in Apache). Needs PyMySQL (apt install python3-pymysql) and the talent map talent-trees.json
(made by talent-trees.py from the server's Talent.dbc and TalentTab.dbc) next to the settings. The tickets need
Python's sqlite3 module (part of python3) and a folder www-data may write (TICKET_DB, /var/lib/temucraft/).

    python3 char-summary.py --self-test            checks the summary, SRP6 and ticket logic without a database
    REMOTE_USER=ryan python3 char-summary.py       what the launcher would get for "ryan" (run as www-data)
    python3 char-summary.py --tickets              how many tickets each account has (run as www-data)
    python3 char-summary.py --revoke RYAN          ends every ticket of that account (run as www-data)
"""
import hashlib
import hmac
import json
import os
import re
import secrets
import sqlite3
import sys
import time
import urllib.parse

DEFAULT_ENV = "/etc/temucraft/char-summary.env"

PRIMARY = {164: "Blacksmithing", 165: "Leatherworking", 171: "Alchemy", 182: "Herbalism", 186: "Mining",
           197: "Tailoring", 202: "Engineering", 333: "Enchanting", 393: "Skinning", 755: "Jewelcrafting",
           773: "Inscription"}
SECONDARY = {129: "First Aid", 185: "Cooking", 356: "Fishing"}
PROFESSIONS = {**PRIMARY, **SECONDARY}

# mail.messageType (AzerothCore MailMessageType)
MAIL_NORMAL, MAIL_AUCTION, MAIL_CREATURE, MAIL_GAMEOBJECT, MAIL_CALENDAR = 0, 2, 3, 4, 5
MAIL_CHECK_READ = 0x01            # mail.checked: MAIL_CHECK_MASK_READ
MAX_SENDERS = 8                   # names per character (newest first); the tooltip stays short

NAME = re.compile(r"^[A-Za-z0-9_]{1,64}$")              # database names (they go into the SQL, so only these)
ACCOUNT = re.compile(r"^[A-Za-z0-9_.@-]{1,32}$")        # game account names


def settings():
    values = {
        "DB_HOST": "127.0.0.1",
        "DB_PORT": "3306",
        "DB_SOCKET": "",
        "DB_USER": "",
        "DB_PASSWORD": "",
        "AUTH_DB": "acore_auth",
        # realm id : characters database : world database (creature and object names for mail senders), comma separated
        "REALMS": "1:acore_characters:acore_world,3:acore_ptr_characters:acore_world",
        "SAME_NAME": "1",
        "ACCOUNT_MAP": "",
        "TALENT_TREES": "",
        # character tickets: the SQLite file, days until a ticket ends (0 or -1: never), whether it works only from the
        # IP it was issued to, and failed logins allowed per IP and per account: "tries/seconds"
        "TICKET_DB": "/var/lib/temucraft/char-tickets.sqlite",
        "TICKET_DAYS": "90",
        "TICKET_SAME_IP": "1",
        "TICKET_LOGIN_RATE": "5/600",
    }
    path = os.environ.get("CHAR_SUMMARY_ENV", DEFAULT_ENV)
    if os.path.isfile(path):
        for raw in open(path, encoding="utf-8"):
            line = raw.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip('"')
    folder = os.path.dirname(os.path.abspath(path))
    values["ACCOUNT_MAP"] = values["ACCOUNT_MAP"] or os.path.join(folder, "char-summary-accounts.txt")
    values["TALENT_TREES"] = values["TALENT_TREES"] or os.path.join(folder, "talent-trees.json")
    return values


def realms_of(cfg):
    out = []
    for part in cfg["REALMS"].split(","):
        bits = [b.strip() for b in part.split(":")]
        if len(bits) < 2 or not bits[0].isdigit():
            continue
        chars = bits[1]
        world = bits[2] if len(bits) > 2 and bits[2] else "acore_world"
        if NAME.match(chars) and NAME.match(world):
            out.append((int(bits[0]), chars, world))
    return out


def accounts_for(login, cfg):
    """The game account names this launcher login may see (upper case, like account.username)."""
    login = (login or "").strip()
    if not login:
        return []
    path = cfg["ACCOUNT_MAP"]
    if os.path.isfile(path):
        for raw in open(path, encoding="utf-8"):
            line = raw.split("#", 1)[0].strip()
            if "=" not in line:
                continue
            who, names = line.split("=", 1)
            if who.strip().lower() == login.lower():
                return [n.strip().upper() for n in names.split(",") if ACCOUNT.match(n.strip())]
    if cfg["SAME_NAME"].strip() not in ("0", "false", "no", "") and ACCOUNT.match(login):
        return [login.upper()]
    return []


def load_trees(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    spells = {int(k): (int(v[0]), int(v[1])) for k, v in data["spells"].items()}
    tabs = {int(k): v for k, v in data["tabs"].items()}
    return spells, tabs


# ------------------------------------------------------------------------------------------------ the summary itself
def spec_of(talents, active_group, trees):
    """talents: (spell, specMask) rows of one character; the tree with the most points in the active group, or None."""
    spells, tabs = trees
    mask = 1 << (active_group or 0)
    points = {}
    for spell, spec_mask in talents:
        if (spec_mask or 0) & mask and spell in spells:
            tab, rank = spells[spell]
            points[tab] = points.get(tab, 0) + rank
    if not points:
        return None
    best = max(points.values())
    winners = sorted((t for t, p in points.items() if p == best), key=lambda t: tabs.get(t, {}).get("order", 0))
    return tabs.get(winners[0], {}).get("name") or None


def professions_of(skills):
    """skills: (skill, value) rows; primary professions first (highest skill first), then the secondary ones."""
    primary = sorted((s for s in skills if s[0] in PRIMARY), key=lambda s: (-(s[1] or 0), PRIMARY[s[0]]))
    secondary = sorted((s for s in skills if s[0] in SECONDARY), key=lambda s: list(SECONDARY).index(s[0]))
    return [PROFESSIONS[s[0]] for s in primary + secondary]


def sender_name(message_type, sender, names):
    """names: {"char": {guid: name}, "creature": {entry: name}, "object": {entry: name}}."""
    if message_type == MAIL_AUCTION:
        return "Auction House"
    if message_type == MAIL_NORMAL:
        return names["char"].get(sender) or "Mailbox"
    if message_type == MAIL_CREATURE:
        return names["creature"].get(sender) or "Mailbox"
    if message_type == MAIL_GAMEOBJECT:
        return names["object"].get(sender) or "Mailbox"
    if message_type == MAIL_CALENDAR:
        return "Calendar"
    return "Mailbox"


def summarize(characters, skills, talents, mails, names, trees):
    """characters: (guid, name, money, activeTalentGroup); skills: (guid, skill, value); talents: (guid, spell, specMask);
    mails: (receiver, messageType, sender), newest first. Returns {name: {spec, professions, money, mail}}."""
    by_guid = {}
    for guid, name, money, group in characters:
        by_guid[guid] = {"name": name, "money": int(money or 0), "group": int(group or 0), "skills": [], "talents": [], "mail": []}
    for guid, skill, value in skills:
        if guid in by_guid:
            by_guid[guid]["skills"].append((int(skill), int(value or 0)))
    for guid, spell, spec_mask in talents:
        if guid in by_guid:
            by_guid[guid]["talents"].append((int(spell), int(spec_mask or 0)))
    for receiver, message_type, sender in mails:
        c = by_guid.get(receiver)
        if c is None:
            continue
        who = sender_name(int(message_type or 0), int(sender or 0), names)
        if who not in c["mail"] and len(c["mail"]) < MAX_SENDERS:
            c["mail"].append(who)
    out = {}
    for c in by_guid.values():
        entry = {"money": c["money"], "professions": professions_of(c["skills"]), "mail": c["mail"]}
        spec = spec_of(c["talents"], c["group"], trees)
        if spec:
            entry["spec"] = spec
        out[c["name"]] = entry
    return out


# ------------------------------------------------------------------------------------------------ SRP6 (the game login)
# AzerothCore (src/common/Cryptography/Authentication/SRP6.cpp): v = g ^ SHA1(salt || SHA1(USER ":" PASS)) mod N, with
# the hash read as a little-endian number and salt and verifier stored as 32 little-endian bytes in acore_auth.account.
SRP_N = int("894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7", 16)
SRP_G = 7


def upper_latin(text):
    """AzerothCore's Utf8ToUpperOnlyLatin: only a-z become capitals; any other letter stays as it is."""
    return "".join(chr(ord(c) - 32) if "a" <= c <= "z" else c for c in text)


def srp6_verifier(username, password, salt):
    """The verifier the login server stores for this name, password and salt (32 bytes, little-endian)."""
    inner = hashlib.sha1(f"{upper_latin(username)}:{upper_latin(password)}".encode("utf-8")).digest()
    x = int.from_bytes(hashlib.sha1(bytes(salt) + inner).digest(), "little")
    return pow(SRP_G, x, SRP_N).to_bytes(32, "little")


def srp6_check(username, password, salt, verifier):
    """True when the password is the account's (constant-time compare)."""
    return hmac.compare_digest(srp6_verifier(username, password, salt), bytes(verifier))


def fingerprint(salt, verifier):
    """Changes when the password does (a new salt and verifier): a ticket remembers it."""
    return hashlib.sha256(b"temucraft-ticket\0" + bytes(salt) + b"\0" + bytes(verifier)).hexdigest()


# ------------------------------------------------------------------------------------------------ character tickets
class TicketError(Exception):
    """The ticket doesn't work (any more); reason: unknown, expired, ip or password."""
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def login_rate(cfg):
    try:
        tries, seconds = (int(v) for v in cfg.get("TICKET_LOGIN_RATE", "5/600").split("/", 1))
        if tries > 0 and seconds > 0:
            return tries, seconds
    except ValueError:
        pass
    return 5, 600


def ticket_days(cfg):
    try:
        return int(str(cfg.get("TICKET_DAYS", "90")).strip())
    except ValueError:
        return 90


def on(value):
    return str(value).strip().lower() not in ("0", "false", "no", "off", "")


def ticket_hash(ticket):
    return hashlib.sha256(ticket.encode("ascii", "replace")).hexdigest()


TICKETS_PER_ACCOUNT = 20   # PCs (and old tickets) per account; the oldest go first


class Tickets:
    """The ticket file: tickets(hash, account id and name, issued, ip, fingerprint, used) and failed logins."""

    def __init__(self, path):
        self.db = sqlite3.connect(path, timeout=10, isolation_level=None)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("CREATE TABLE IF NOT EXISTS tickets (hash TEXT PRIMARY KEY, account_id INTEGER NOT NULL, "
                        "account TEXT NOT NULL, issued INTEGER NOT NULL, ip TEXT NOT NULL, fingerprint TEXT NOT NULL, "
                        "used INTEGER NOT NULL)")
        self.db.execute("CREATE INDEX IF NOT EXISTS tickets_account ON tickets (account_id)")
        self.db.execute("CREATE TABLE IF NOT EXISTS failures (key TEXT NOT NULL, at INTEGER NOT NULL)")
        self.db.execute("CREATE INDEX IF NOT EXISTS failures_key ON failures (key, at)")

    def close(self):
        self.db.close()

    # -- failed logins
    def blocked_for(self, keys, now, tries, seconds):
        """Seconds until a login may be tried again for any of the keys (0: now)."""
        wait = 0
        for key in keys:
            rows = self.db.execute("SELECT at FROM failures WHERE key = ? AND at > ? ORDER BY at",
                                   (key, now - seconds)).fetchall()
            if len(rows) >= tries:
                wait = max(wait, rows[len(rows) - tries][0] + seconds - now)
        return max(wait, 0)

    def failed(self, keys, now, seconds):
        self.db.execute("DELETE FROM failures WHERE at <= ?", (now - seconds,))
        self.db.executemany("INSERT INTO failures (key, at) VALUES (?, ?)", [(k, now) for k in keys])

    def forget_failures(self, key):
        self.db.execute("DELETE FROM failures WHERE key = ?", (key,))

    # -- tickets
    def issue(self, account_id, account, ip, print_, now, days):
        ticket = secrets.token_urlsafe(32)          # 256 random bits
        self.db.execute("INSERT INTO tickets (hash, account_id, account, issued, ip, fingerprint, used) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        (ticket_hash(ticket), account_id, account, now, ip, print_, now))
        if days > 0:
            self.db.execute("DELETE FROM tickets WHERE issued <= ?", (now - days * 86400,))
        self.db.execute("DELETE FROM tickets WHERE account_id = ? AND hash NOT IN (SELECT hash FROM tickets WHERE "
                        "account_id = ? ORDER BY issued DESC, rowid DESC LIMIT ?)", (account_id, account_id, TICKETS_PER_ACCOUNT))
        return ticket

    def resolve(self, ticket, ip, current_print, now, days, same_ip):
        """(account id, account name) of a working ticket; else TicketError (and a dead ticket is deleted).
        current_print(account id): the account's fingerprint now, or None when the account is gone."""
        key = ticket_hash(ticket or "")
        row = self.db.execute("SELECT account_id, account, issued, ip, fingerprint FROM tickets WHERE hash = ?", (key,)).fetchone()
        if row is None:
            raise TicketError("unknown")
        account_id, account, issued, issued_ip, print_ = row
        reason = None
        if days > 0 and now >= issued + days * 86400:
            reason = "expired"
        elif same_ip and issued_ip != ip:
            reason = "ip"
        else:
            now_print = current_print(account_id)
            if now_print is None or not hmac.compare_digest(now_print, print_):
                reason = "password"
        if reason:
            self.db.execute("DELETE FROM tickets WHERE hash = ?", (key,))
            raise TicketError(reason)
        self.db.execute("UPDATE tickets SET used = ? WHERE hash = ?", (now, key))
        return account_id, account

    def revoke(self, ticket):
        self.db.execute("DELETE FROM tickets WHERE hash = ?", (ticket_hash(ticket or ""),))

    def revoke_account(self, account):
        return self.db.execute("DELETE FROM tickets WHERE account = ?", (upper_latin(account),)).rowcount

    def counts(self):
        return self.db.execute("SELECT account, COUNT(*), MAX(used) FROM tickets GROUP BY account ORDER BY account").fetchall()


DUMMY_SALT = bytes(32)
DUMMY_VERIFIER = bytes(32)


def login(tickets, cfg, lookup, account, password, ip, now):
    """Checks a game login and issues a ticket. lookup(ACCOUNT): (id, salt, verifier) or None.
    Returns (status, body) for the answer. The password is never stored, logged or sent anywhere."""
    tries, seconds = login_rate(cfg)
    name = upper_latin((account or "").strip())
    ip_key, account_key = "ip:" + (ip or "?"), "account:" + name
    wait = tickets.blocked_for([ip_key, account_key], now, tries, seconds)
    if wait > 0:
        return "429 Too Many Requests", {"error": "rate", "retry_after": int(wait)}
    found = lookup(name) if ACCOUNT.match(name) and password and len(password) <= 128 else None
    salt, verifier = (found[1], found[2]) if found else (DUMMY_SALT, DUMMY_VERIFIER)
    good = srp6_check(name, password or "", salt, verifier)   # worked out for unknown names too: the same time
    if not found or not good:
        tickets.failed([ip_key, account_key], now, seconds)
        return "403 Forbidden", {"error": "login"}
    tickets.forget_failures(account_key)
    days = ticket_days(cfg)
    ticket = tickets.issue(int(found[0]), name, ip or "?", fingerprint(salt, verifier), now, days)
    return "200 OK", {"ticket": ticket, "account": name, "expires": now + days * 86400 if days > 0 else 0}


# ------------------------------------------------------------------------------------------------ the database
def connect(cfg):
    import pymysql   # apt install python3-pymysql
    args = dict(user=cfg["DB_USER"], password=cfg["DB_PASSWORD"], charset="utf8mb4", connect_timeout=5,
                read_timeout=10, autocommit=True)
    if cfg["DB_SOCKET"]:
        args["unix_socket"] = cfg["DB_SOCKET"]
    else:
        args["host"], args["port"] = cfg["DB_HOST"], int(cfg["DB_PORT"])
    return pymysql.connect(**args)


def query(db, sql, params=()):
    with db.cursor() as cur:
        cur.execute(sql, params)
        return cur.fetchall()


def marks(values):
    return ",".join(["%s"] * len(values))


def realm_summary(db, chars_db, world_db, account_ids, trees, now):
    ids = marks(account_ids)
    c = f"`{chars_db}`"
    characters = query(db, f"SELECT guid, name, money, activeTalentGroup FROM {c}.characters WHERE account IN ({ids})", account_ids)
    if not characters:
        return {}
    skills = query(db, f"SELECT s.guid, s.skill, s.value FROM {c}.character_skills s JOIN {c}.characters ch ON ch.guid = s.guid "
                       f"WHERE ch.account IN ({ids}) AND s.skill IN ({marks(PROFESSIONS)})", (*account_ids, *PROFESSIONS))
    talents = query(db, f"SELECT t.guid, t.spell, t.specMask FROM {c}.character_talent t JOIN {c}.characters ch ON ch.guid = t.guid "
                        f"WHERE ch.account IN ({ids})", account_ids)
    mails = query(db, f"SELECT m.receiver, m.messageType, m.sender FROM {c}.mail m JOIN {c}.characters ch ON ch.guid = m.receiver "
                      f"WHERE ch.account IN ({ids}) AND (m.checked & {MAIL_CHECK_READ}) = 0 AND m.deliver_time <= %s "
                      f"AND m.expire_time > %s ORDER BY m.deliver_time DESC, m.id DESC", (*account_ids, now, now))
    names = {"char": {}, "creature": {}, "object": {}}
    wanted = {kind: sorted({int(s) for _, t, s in mails if int(t or 0) == code and s})
              for kind, code in (("char", MAIL_NORMAL), ("creature", MAIL_CREATURE), ("object", MAIL_GAMEOBJECT))}
    if wanted["char"]:
        names["char"] = dict(query(db, f"SELECT guid, name FROM {c}.characters WHERE guid IN ({marks(wanted['char'])})", wanted["char"]))
    if wanted["creature"]:
        names["creature"] = dict(query(db, f"SELECT entry, name FROM `{world_db}`.creature_template WHERE entry IN ({marks(wanted['creature'])})", wanted["creature"]))
    if wanted["object"]:
        names["object"] = dict(query(db, f"SELECT entry, name FROM `{world_db}`.gameobject_template WHERE entry IN ({marks(wanted['object'])})", wanted["object"]))
    return summarize(characters, skills, talents, mails, names, trees)


def account_ids_of(db, cfg, accounts):
    auth = auth_db(cfg)
    return [int(r[0]) for r in query(db, f"SELECT id FROM `{auth}`.account WHERE username IN ({marks(accounts)})", accounts)]


def auth_db(cfg):
    return cfg["AUTH_DB"] if NAME.match(cfg["AUTH_DB"]) else "acore_auth"


def summary_of(db, cfg, account_ids, realms):
    out = {"generated": int(time.time()), "realms": []}
    if not account_ids or not realms:
        return out
    trees = load_trees(cfg["TALENT_TREES"])
    auth = auth_db(cfg)
    realm_ids = [r[0] for r in realms]
    realm_names = dict(query(db, f"SELECT id, name FROM `{auth}`.realmlist WHERE id IN ({marks(realm_ids)})", realm_ids))
    now = int(time.time())
    for realm_id, chars_db, world_db in realms:
        if realm_id not in realm_names:
            continue
        try:
            characters = realm_summary(db, chars_db, world_db, account_ids, trees, now)
        except Exception as error:     # one realm's database down or missing: the others still answer
            print(f"char-summary: realm {realm_id} ({chars_db}): {error}", file=sys.stderr)
            continue
        out["realms"].append({"id": realm_id, "name": realm_names[realm_id], "characters": characters})
    return out


def build(login, cfg):
    """The summary for the launcher login Apache checked (no ticket): SAME_NAME / ACCOUNT_MAP."""
    realms = realms_of(cfg)
    accounts = accounts_for(login, cfg)
    if not accounts or not realms:
        return {"generated": int(time.time()), "realms": []}
    db = connect(cfg)
    try:
        return summary_of(db, cfg, account_ids_of(db, cfg, accounts), realms)
    finally:
        db.close()


def account_row(db, cfg, name):
    """(id, salt, verifier) of a game account, or None."""
    rows = query(db, f"SELECT id, salt, verifier FROM `{auth_db(cfg)}`.account WHERE username = %s", (name,))
    return (int(rows[0][0]), bytes(rows[0][1]), bytes(rows[0][2])) if rows else None


def current_print(db, cfg):
    def of(account_id):
        rows = query(db, f"SELECT salt, verifier FROM `{auth_db(cfg)}`.account WHERE id = %s", (account_id,))
        return fingerprint(bytes(rows[0][0]), bytes(rows[0][1])) if rows else None
    return of


def build_for_ticket(ticket, ip, cfg):
    """(status, body): the summary of the ticket's account, or 403 when the ticket doesn't work."""
    tickets = Tickets(cfg["TICKET_DB"])
    db = connect(cfg)
    try:
        try:
            account_id, _ = tickets.resolve(ticket, ip, current_print(db, cfg), int(time.time()), ticket_days(cfg),
                                            on(cfg["TICKET_SAME_IP"]))
        except TicketError as error:
            return "403 Forbidden", {"error": "ticket", "reason": error.reason}
        return "200 OK", summary_of(db, cfg, [account_id], realms_of(cfg))
    finally:
        db.close()
        tickets.close()


def read_form():
    try:
        length = int(os.environ.get("CONTENT_LENGTH") or 0)
    except ValueError:
        length = 0
    if length <= 0 or length > 4096:
        return {}
    body = sys.stdin.buffer.read(length).decode("utf-8", "replace")
    return {k: v[0] for k, v in urllib.parse.parse_qs(body, keep_blank_values=True).items()}


def post(cfg):
    """(status, body) for POST: action=login (account, password) or action=logout (the ticket header)."""
    form = read_form()
    action = form.get("action", "")
    ip = os.environ.get("REMOTE_ADDR", "")
    if action == "logout":
        ticket = os.environ.get("HTTP_X_TEMUCRAFT_TICKET", "").strip()
        if ticket:
            tickets = Tickets(cfg["TICKET_DB"])
            try:
                tickets.revoke(ticket)
            finally:
                tickets.close()
        return "200 OK", {"ok": True}
    if action != "login":
        return "400 Bad Request", {"error": "action=login or action=logout"}
    tickets = Tickets(cfg["TICKET_DB"])
    db = None
    try:
        def lookup(name):
            nonlocal db
            db = db or connect(cfg)
            return account_row(db, cfg, name)
        status, body = login(tickets, cfg, lookup, form.get("account", ""), form.get("password", ""), ip, int(time.time()))
        if status.startswith("200"):
            print(f"char-summary: ticket issued to {body['account']} from {ip}", file=sys.stderr)
        elif status.startswith("429"):
            print(f"char-summary: too many failed logins ({ip})", file=sys.stderr)
        return status, body
    finally:
        if db is not None:
            db.close()
        tickets.close()


def answer(status, body):
    data = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    sys.stdout.write(f"Status: {status}\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n"
                     f"Content-Length: {len(data)}\r\n\r\n")
    sys.stdout.flush()
    sys.stdout.buffer.write(data)
    sys.stdout.flush()


def self_test():
    trees = ({100: (262, 1), 101: (262, 2), 200: (261, 3), 300: (263, 5)},
             {261: {"name": "Elemental", "order": 0}, 262: {"name": "Restoration", "order": 2}, 263: {"name": "Enhancement", "order": 1}})
    names = {"char": {7: "Jaina"}, "creature": {900: "Innkeeper Allison"}, "object": {}}
    out = summarize(
        characters=[(1, "Thrall", 162575561, 0), (2, "Rexxar", 0, 1), (3, "Babý", None, 0)],
        skills=[(1, 182, 300), (1, 186, 450), (1, 185, 200), (1, 129, 1), (1, 98, 300), (2, 356, 10)],
        talents=[(1, 101, 1), (1, 200, 1), (1, 300, 2), (2, 300, 2), (2, 200, 1), (2, 999, 2)],
        mails=[(1, 0, 7), (1, 2, 0), (1, 0, 7), (1, 3, 900), (1, 0, 55), (2, 4, 1), (4, 0, 7)],
        names=names, trees=trees)
    assert out["Thrall"] == {"money": 162575561, "professions": ["Mining", "Herbalism", "First Aid", "Cooking"],
                             "mail": ["Jaina", "Auction House", "Innkeeper Allison", "Mailbox"], "spec": "Elemental"}, out["Thrall"]
    assert out["Rexxar"] == {"money": 0, "professions": ["Fishing"], "mail": ["Mailbox"], "spec": "Enhancement"}, out["Rexxar"]
    assert out["Babý"] == {"money": 0, "professions": [], "mail": []}, out["Babý"]
    # a tie goes to the tree that comes first in the talent window
    assert spec_of([(100, 1), (200, 1), (101, 1)], 0, trees) == "Elemental"
    assert spec_of([(300, 1), (200, 1)], 0, ({300: (263, 3), 200: (261, 3)}, trees[1])) == "Elemental"
    assert spec_of([], 0, trees) is None and spec_of([(100, 2)], 0, trees) is None   # no points in the active group
    cfg = {"ACCOUNT_MAP": "/nonexistent", "SAME_NAME": "1"}
    assert accounts_for("ryan", cfg) == ["RYAN"] and accounts_for("", cfg) == [] and accounts_for("a b", cfg) == []
    assert accounts_for("ryan", {**cfg, "SAME_NAME": "0"}) == []
    assert realms_of({"REALMS": "1:acore_characters:acore_world, 3:acore_ptr_characters, 4:bad;name, x:y"}) == \
        [(1, "acore_characters", "acore_world"), (3, "acore_ptr_characters", "acore_world")]
    ticket_self_test()
    print("char-summary self-test: ok")


def ticket_self_test():
    import tempfile
    # SRP6: verifiers worked out by AzerothCore's own SRP6::CalculateVerifier (SRP6.cpp + BigNumber.cpp built with OpenSSL)
    salt1 = bytes(range(1, 33))
    assert srp6_verifier("TEST", "TEST", salt1).hex() == "d7c6ea0e6a621560d57b6a1fe847adc19dc7b2fc7c0c3b9f9cccf60646e9af02"
    salt2 = bytes.fromhex("ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100")
    v2 = bytes.fromhex("f9cc8d91d0ce5d1427314e6010aac5f312a83a38e0ddcd7cd753137dab2fe325")
    assert srp6_verifier("RYAN", "HUNTER2!", salt2) == v2
    assert srp6_check("ryan", "hunter2!", salt2, v2), "the game ignores the case of a-z"
    assert not srp6_check("RYAN", "HUNTER2", salt2, v2) and not srp6_check("RYAN", "HUNTER2!", salt1, v2)
    assert upper_latin("abcXYZ 12 éß") == "ABCXYZ 12 éß", "only a-z, like Utf8ToUpperOnlyLatin"

    accounts = {"RYAN": [5, salt2, v2]}
    lookup = lambda name: tuple(accounts[name]) if name in accounts else None
    prints = lambda account_id: next((fingerprint(a[1], a[2]) for a in accounts.values() if a[0] == account_id), None)
    with tempfile.TemporaryDirectory() as folder:
        t = Tickets(os.path.join(folder, "t.sqlite"))
        cfg = {"TICKET_DAYS": "0", "TICKET_SAME_IP": "1", "TICKET_LOGIN_RATE": "5/600"}
        now = 1_800_000_000
        status, body = login(t, cfg, lookup, "ryan", "hunter2!", "1.2.3.4", now)
        assert status.startswith("200") and body["account"] == "RYAN" and body["expires"] == 0 and len(body["ticket"]) >= 43, body
        ticket = body["ticket"]
        stored = t.db.execute("SELECT hash, account_id, ip FROM tickets").fetchall()
        assert stored == [(ticket_hash(ticket), 5, "1.2.3.4")] and ticket not in str(stored), "only the hash is kept"
        # never expires with 0 or -1; N days does
        for days in (0, -1):
            assert t.resolve(ticket, "1.2.3.4", prints, now + 3650 * 86400, days, True) == (5, "RYAN")
        assert t.resolve(ticket, "1.2.3.4", prints, now + 89 * 86400, 90, True) == (5, "RYAN")
        try:
            t.resolve(ticket, "1.2.3.4", prints, now + 90 * 86400, 90, True)
            raise AssertionError("expired after 90 days")
        except TicketError as e:
            assert e.reason == "expired"
        try:
            t.resolve(ticket, "1.2.3.4", prints, now, 0, True)
            raise AssertionError("an expired ticket is deleted")
        except TicketError as e:
            assert e.reason == "unknown"
        # a new IP: only with TICKET_SAME_IP=0
        ticket = login(t, cfg, lookup, "RYAN", "HUNTER2!", "1.2.3.4", now)[1]["ticket"]
        assert t.resolve(ticket, "9.9.9.9", prints, now, 0, False) == (5, "RYAN")
        try:
            t.resolve(ticket, "9.9.9.9", prints, now, 0, True)
            raise AssertionError("another IP")
        except TicketError as e:
            assert e.reason == "ip"
        # a password change (new salt and verifier) ends the ticket
        ticket = login(t, cfg, lookup, "RYAN", "HUNTER2!", "1.2.3.4", now)[1]["ticket"]
        accounts["RYAN"][1], accounts["RYAN"][2] = salt1, srp6_verifier("RYAN", "NEWPASS", salt1)
        try:
            t.resolve(ticket, "1.2.3.4", prints, now, 0, True)
            raise AssertionError("password changed")
        except TicketError as e:
            assert e.reason == "password"
        assert login(t, cfg, lookup, "RYAN", "newpass", "1.2.3.4", now)[0].startswith("200")
        # logout
        ticket = login(t, cfg, lookup, "RYAN", "newpass", "1.2.3.4", now)[1]["ticket"]
        t.revoke(ticket)
        try:
            t.resolve(ticket, "1.2.3.4", prints, now, 0, True)
            raise AssertionError("logged out")
        except TicketError as e:
            assert e.reason == "unknown"
        # failed logins: 5 per 10 minutes per account (from any IP) and per IP (any account)
        for i in range(5):
            assert login(t, cfg, lookup, "RYAN", "wrong", f"10.0.0.{i}", now + i)[0].startswith("403")
        status, body = login(t, cfg, lookup, "RYAN", "newpass", "10.0.0.9", now + 10)
        assert status.startswith("429") and 0 < body["retry_after"] <= 600, (status, body)
        assert login(t, cfg, lookup, "RYAN", "newpass", "10.0.0.9", now + 601)[0].startswith("200"), "the window passed"
        for i in range(5):
            assert login(t, cfg, lookup, f"NOBODY{i}", "x", "6.6.6.6", now)[0].startswith("403")
        assert login(t, cfg, lookup, "RYAN", "newpass", "6.6.6.6", now + 1)[0].startswith("429"), "per IP too"
        assert login(t, cfg, lookup, "RYAN", "newpass", "7.7.7.7", now + 1)[0].startswith("200"), "other IPs still log in"
        assert login(t, {**cfg, "TICKET_DAYS": "30"}, lookup, "RYAN", "newpass", "7.7.7.7", now)[1]["expires"] == now + 30 * 86400
        assert login(t, cfg, lookup, "a b", "x", "8.8.8.8", now)[0].startswith("403") and login(t, cfg, lookup, "RYAN", "", "8.8.8.8", now)[0].startswith("403")
        # at most TICKETS_PER_ACCOUNT per account
        for i in range(TICKETS_PER_ACCOUNT + 5):
            login(t, cfg, lookup, "RYAN", "newpass", "7.7.7.7", now + 1000 + i)
        assert t.db.execute("SELECT COUNT(*) FROM tickets WHERE account_id = 5").fetchone()[0] == TICKETS_PER_ACCOUNT
        assert t.revoke_account("ryan") == TICKETS_PER_ACCOUNT
        t.close()
    assert ticket_days({"TICKET_DAYS": "x"}) == 90 and login_rate({"TICKET_LOGIN_RATE": "bad"}) == (5, 600)
    assert on("1") and not on("0") and not on("")


def main():
    if "--self-test" in sys.argv:
        self_test()
        return
    if "--tickets" in sys.argv or "--revoke" in sys.argv:
        tickets = Tickets(settings()["TICKET_DB"])
        try:
            if "--revoke" in sys.argv:
                who = sys.argv[sys.argv.index("--revoke") + 1] if len(sys.argv) > sys.argv.index("--revoke") + 1 else ""
                print(f"{tickets.revoke_account(who)} ticket(s) of {upper_latin(who)} ended")
            else:
                for account, count, used in tickets.counts():
                    print(f"{account}: {count} ticket(s), last used {time.strftime('%Y-%m-%d %H:%M', time.localtime(used))}")
        finally:
            tickets.close()
        return
    login_name = os.environ.get("REMOTE_USER", "")
    if os.environ.get("GATEWAY_INTERFACE") and not login_name:
        answer("403 Forbidden", {"error": "not logged in"})   # Apache didn't check a login: never answer without one
        return
    method = os.environ.get("REQUEST_METHOD", "GET")
    if method not in ("GET", "HEAD", "POST"):
        answer("405 Method Not Allowed", {"error": "GET or POST only"})
        return
    try:
        cfg = settings()
        if method == "POST":
            answer(*post(cfg))
            return
        ticket = os.environ.get("HTTP_X_TEMUCRAFT_TICKET", "").strip()
        if ticket:
            answer(*build_for_ticket(ticket, os.environ.get("REMOTE_ADDR", ""), cfg))
        else:
            answer("200 OK", build(login_name, cfg))
    except Exception as error:
        # never the request body (the password): only what went wrong
        print(f"char-summary: {type(error).__name__}: {error}", file=sys.stderr)   # Apache's error log
        answer("500 Internal Server Error", {"error": "the summary could not be read"})


if __name__ == "__main__":
    main()
