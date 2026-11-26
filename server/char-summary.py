#!/usr/bin/env python3
"""
Character summary for the launcher: spec, professions, gold and unread mail of the logged-in player's characters, for
the character select screen (the card tooltips and the mail badge of TemucraftCharSelect.lua in patch-^).

The launcher asks for SERVER_URL/char-summary before PLAY and when the player switches between Live and PTR, with the
same login as the game files and the play ticket, and writes the answer into the game folder as
Interface\\TemucraftCharSummary.lua. This script is a CGI program: it sits in the folder Apache serves for the launcher
(next to play-ticket), so Apache runs the same checks on it as on every file there (the IP list and the username and
password, server/apache-temucraft.conf.example) before it runs, and hands it the checked username (REMOTE_USER). It
only ever answers about the game account(s) of that username:

  - char-summary-accounts.txt (ACCOUNT_MAP): "launcher login = GAMEACCOUNT, GAMEACCOUNT2" lines, for logins whose
    game account has another name (or several game accounts). A login listed there gets only those accounts.
  - otherwise, with SAME_NAME=1 (the default), the game account with the same name as the launcher login.
  - otherwise nothing (an empty answer).

No account names, ids, guids or IPs are sent back: per realm (realm id and its realmlist name) the characters by name,
with what the card needs. One query per table per realm, all by the account ids.

    {"generated": 1764100000, "realms": [{"id": 1, "name": "Temucraft", "characters": {
        "Thrall": {"spec": "Restoration", "professions": ["Mining", "Herbalism"], "money": 162575561,
                   "mail": ["Jaina", "Auction House"]}}}]}

Settings: char-summary.env (see char-summary.env.example) in /etc/temucraft/, or where the CHAR_SUMMARY_ENV environment
variable points (SetEnv in Apache). Needs PyMySQL (apt install python3-pymysql) and the talent map talent-trees.json
(made by talent-trees.py from the server's Talent.dbc and TalentTab.dbc) next to the settings.

    python3 char-summary.py --self-test            checks the summary logic without a database
    REMOTE_USER=ryan python3 char-summary.py       what the launcher would get for "ryan" (run as www-data)
"""
import json
import os
import re
import sys
import time

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


def build(login, cfg):
    realms = realms_of(cfg)
    out = {"generated": int(time.time()), "realms": []}
    accounts = accounts_for(login, cfg)
    if not accounts or not realms:
        return out
    trees = load_trees(cfg["TALENT_TREES"])
    db = connect(cfg)
    try:
        auth = cfg["AUTH_DB"] if NAME.match(cfg["AUTH_DB"]) else "acore_auth"
        account_ids = [int(r[0]) for r in query(db, f"SELECT id FROM `{auth}`.account WHERE username IN ({marks(accounts)})", accounts)]
        if not account_ids:
            return out
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
    finally:
        db.close()
    return out


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
    print("char-summary self-test: ok")


def main():
    if "--self-test" in sys.argv:
        self_test()
        return
    login = os.environ.get("REMOTE_USER", "")
    if os.environ.get("GATEWAY_INTERFACE") and not login:
        answer("403 Forbidden", {"error": "not logged in"})   # Apache didn't check a login: never answer without one
        return
    if os.environ.get("REQUEST_METHOD", "GET") not in ("GET", "HEAD"):
        answer("405 Method Not Allowed", {"error": "GET only"})
        return
    try:
        answer("200 OK", build(login, settings()))
    except Exception as error:
        print(f"char-summary: {type(error).__name__}: {error}", file=sys.stderr)   # Apache's error log
        answer("500 Internal Server Error", {"error": "the summary could not be read"})


if __name__ == "__main__":
    main()
