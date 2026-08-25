# -*- coding: utf-8 -*-
"""Checks the four tables against each other.

The question this answers is the one asked from the device: when a German wording was corrected,
did the other three get the same treatment? Meaning cannot be checked mechanically, but three
things can, and each of them has caught a real inconsistency:

  1. asymmetry   - a key one language has and another does not (a width drop, intended or not)
  2. vocabulary  - a decided term used in one language but not carried through
  3. leftovers   - developer words ("build", "release") still sitting in a translation
"""
import io, re, sys, collections

TABLES = 'ClawTweaksCenter/Core/Localization.Tables.cs'
LANGS = ['German', 'French', 'Korean', 'Spanish']

src = io.open(TABLES, encoding='utf-8-sig').read()

def decode(x):
    x = re.sub(r'\\u([0-9A-Fa-f]{4})', lambda m: chr(int(m.group(1), 16)), x)
    return x.replace('\\"', '"').replace('\\\\', '\\')

tables = {}
for i, lang in enumerate(LANGS):
    start = src.index(lang + ' = new')
    end = src.index(LANGS[i + 1] + ' = new') if i + 1 < len(LANGS) else len(src)
    block = src[start:end]
    d = {}
    for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\] = "((?:[^"\\]|\\.)*)"', block):
        d[decode(m.group(1))] = decode(m.group(2))
    tables[lang] = d

sys.stdout.write("entries: %s\n\n" % {l: len(tables[l]) for l in LANGS})

# --- 1) asymmetry ----------------------------------------------------------
all_keys = set()
for l in LANGS:
    all_keys |= set(tables[l])
sys.stdout.write("=== keys missing in some language\n")
gaps = 0
for k in sorted(all_keys):
    missing = [l for l in LANGS if k not in tables[l]]
    if missing:
        gaps += 1
        sys.stdout.write("   %-46s missing: %s\n" % (k[:46], ', '.join(missing)))
if not gaps:
    sys.stdout.write("   (none)\n")

# --- 2) developer words left in a translation ------------------------------
sys.stdout.write("\n=== developer words still inside a TRANSLATION\n")
BAD = ['build', 'Build', 'release', 'Release', 'nightly', 'Nightly']
found = 0
for lang in ['German', 'French', 'Spanish']:      # Korean would not contain them anyway
    for k, v in sorted(tables[lang].items()):
        for w in BAD:
            if re.search(r'\b' + w + r'\b', v):
                # a proper name in the source is allowed to survive
                if re.search(r'\b' + w + r'\b', k, re.IGNORECASE):
                    continue
                found += 1
                sys.stdout.write("   %-8s %-34s -> %s\n" % (lang + ":", k[:34], v[:60]))
                break
if not found:
    sys.stdout.write("   (none)\n")

# --- 3) vocabulary decisions, carried through or not -----------------------
# Each entry: the English key family, and the word each language settled on.
VOCAB = [
    ("restore/backup", ["Restore Backup", "Restore complete", "Restore failed",
                        "Bring back a previous backup. A safety copy of the current state is taken automatically first."]),
    ("library",        ["Library", "Library Settings", "Library settings", "Back to library",
                        "Leave the library", "Game Library"]),
    ("versions",       ["Main versions", "Test versions", "Experimental versions (nightly)",
                        "version", "versions", "Install this version", "Install this version?"]),
    ("settings",       ["Settings", "Center settings", "Library settings"]),
]
sys.stdout.write("\n=== vocabulary families, side by side\n")
for name, keys in VOCAB:
    sys.stdout.write("   --- %s\n" % name)
    for k in keys:
        row = []
        for l in LANGS:
            row.append(tables[l].get(k, "-"))
        sys.stdout.write("      %-42s | %s\n" % (k[:42], ' | '.join(r[:26] for r in row)))
