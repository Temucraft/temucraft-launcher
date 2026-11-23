"""Builds src/Launcher.Core/wow-functions.txt.gz: the start address and name of every Wow.exe (3.3.5a 12340) function
that wowdecomp has, from its "// OFFSET: 0x..." tags. Diagnostics uses it to name the frames of a game crash.

    python tools/wow_functions.py <wowdecomp folder>
"""
import gzip, os, re, sys

root = sys.argv[1]
tag = re.compile(r'//\s*OFFSET:\s*(?:\w+\s+)?0x([0-9a-fA-F]+)')
name = re.compile(r'([~\w]+(?:::[~\w]+)*(?:::operator\s*\S+?)?)\s*\(')
found = {}
for folder, _, files in os.walk(root):
    if any(p in folder for p in ('.git', 'build', 'vendor')):
        continue
    for f in files:
        if not f.endswith(('.cpp', '.c')):
            continue
        lines = open(os.path.join(folder, f), errors='ignore').read().split('\n')
        for i, line in enumerate(lines):
            m = tag.search(line)
            if not m:
                continue
            for j in range(i + 1, min(i + 8, len(lines))):
                s = lines[j].strip()
                if not s or s.startswith(('//', '#', 'template')):
                    continue
                n = name.search(s)
                if n:
                    fn = n.group(1)
                    if '::' not in fn:
                        fn = os.path.splitext(f)[0] + ' ' + fn
                    found.setdefault(int(m.group(1), 16), fn)
                break
out = os.path.join(os.path.dirname(__file__), '..', 'src', 'Launcher.Core', 'wow-functions.txt.gz')
with gzip.open(out, 'wt', encoding='utf-8', newline='\n') as g:
    for addr in sorted(found):
        g.write(f'{addr:08X} {found[addr]}\n')
print(len(found), 'functions ->', os.path.normpath(out))
