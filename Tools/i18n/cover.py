# -*- coding: utf-8 -*-
"""How much of what CAN be translated actually is."""
import io, re, sys

tab = io.open('ClawTweaksCenter/Core/Localization.Tables.cs', encoding='utf-8-sig').read()
keys = set()
for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\]', tab):
    k = m.group(1)
    k = re.sub(r'\\u([0-9A-Fa-f]{4})', lambda x: chr(int(x.group(1), 16)), k)
    k = k.replace('\\"', '"')
    k = k.replace('\\\\', '\\')
    keys.add(k)

hit = []
for line in io.open(sys.argv[1], encoding='utf-8'):
    if '\t' in line:
        hit.append(line.rstrip('\n').split('\t', 1)[1])

missing = [v for v in hit if v not in keys]
sys.stdout.write("table keys: %d\n" % len(keys))
sys.stdout.write("builder-reachable: %d   translated: %d   still English: %d\n"
                 % (len(hit), len(hit) - len(missing), len(missing)))
for m in missing:
    sys.stdout.write("   " + m[:95].encode('ascii', 'replace').decode() + "\n")
