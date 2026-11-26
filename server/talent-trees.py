#!/usr/bin/env python3
"""
Makes talent-trees.json for char-summary.py: which talent tree (and rank) every talent spell belongs to, and the trees'
names. char-summary.py adds up a character's talent points per tree (character_talent holds the spell of each talent's
current rank) and calls the tree with the most points its spec.

    python3 talent-trees.py <folder with the server's Talent.dbc and TalentTab.dbc> [talent-trees.json]

Use the server's own DBCs (data/dbc on the server, _dev/server-dbc/dbc on the PC): they hold Temucraft's custom talents.
Run it again when talents are added or moved to another tree, then copy the new talent-trees.json next to the script.
Pet talent trees (Ferocity, Tenacity, Cunning) are left out: those points are in pet_spell, not character_talent.
"""
import json
import os
import struct
import sys


def read_dbc(path):
    data = open(path, "rb").read()
    magic, records, fields, size, _ = struct.unpack_from("<4s4I", data)
    if magic != b"WDBC":
        sys.exit(f"{path} is not a DBC file")
    strings = data[20 + records * size:]
    rows = [struct.unpack_from(f"<{fields}i", data, 20 + i * size) for i in range(records)]
    return rows, lambda offset: strings[offset:strings.index(b"\0", offset)].decode("utf-8", "replace")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    folder = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "talent-trees.json")
    # TalentTab.dbc (3.3.5a): ID, Name_lang[16], Name flags, SpellIconID, RaceMask, ClassMask, PetTalentMask, OrderIndex, BackgroundFile
    tabs_rows, text = read_dbc(os.path.join(folder, "TalentTab.dbc"))
    tabs = {}
    for row in tabs_rows:
        if row[20] == 0:
            continue                                    # a pet's tree
        names = [text(row[1 + i]) for i in range(16)]
        tabs[str(row[0])] = {"name": names[0], "class": row[20], "order": row[22]}
    # Talent.dbc (3.3.5a): ID, TabID, TierID, ColumnIndex, SpellRank[9], PrereqTalent[3], PrereqRank[3], Flags, RequiredSpellID, CategoryMask[2]
    talent_rows, _ = read_dbc(os.path.join(folder, "Talent.dbc"))
    spells = {}
    talents = 0
    for row in talent_rows:
        if str(row[1]) not in tabs:
            continue
        talents += 1
        for rank in range(9):
            spell = row[4 + rank]
            if spell > 0:
                spells[str(spell)] = [row[1], rank + 1]
    with open(out + ".tmp", "w", encoding="utf-8") as f:
        json.dump({"_comment": "made by talent-trees.py from Talent.dbc and TalentTab.dbc; spells: spell id -> [tree id, rank]",
                   "tabs": tabs, "spells": dict(sorted(spells.items(), key=lambda kv: int(kv[0])))}, f, separators=(",", ":"))
        f.write("\n")
    os.replace(out + ".tmp", out)
    print(f"{out}: {len(tabs)} trees, {talents} talents, {len(spells)} talent spells")


if __name__ == "__main__":
    main()
