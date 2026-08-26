# -*- coding: utf-8 -*-
"""Does every string the FAQ screen renders have a translation?

Run from the repository root:  python Tools/i18n/check_faq.py

WHY IT EXISTS. The FAQ's content is a literal array in the C# file and its translations are a table
keyed by those literals. Nothing checks that the two agree, and one missing entry renders in English
in the middle of an otherwise translated screen - which is exactly what happened, and which nobody
notices without reading a foreign-language screen line by line.

⚠ THE TRAP THIS WAS WRITTEN AROUND. The generated table escapes every non-ASCII character as
\\uXXXX, so searching it for the literal text finds nothing and reports perfectly good entries as
missing. The first version of this check did that and produced three false positives out of four
findings. It now runs the keys through the generator's OWN esc() before looking for them.
"""
import io, re, sys


def esc(s):
    """The generator's escaping, COPIED rather than imported.

    ⚠ Importing loc_gen REGENERATES Localization.Tables.cs as a side effect, and it only carries
    round one - so a read-only check that imported it silently threw away twelve rounds of
    translations and then reported them all as missing. A check must not be able to change what it
    is checking. Nine lines duplicated is the cheap half of that trade; if esc() ever changes, this
    check reports false missing entries, which is the loud failure rather than the quiet one.
    """
    BACKSLASH = chr(92)
    out = []
    for c in s:
        if c == '"':
            out.append(BACKSLASH + '"')
        elif c == BACKSLASH:
            out.append(BACKSLASH + BACKSLASH)
        elif ord(c) < 128:
            out.append(c)
        else:
            out.append(BACKSLASH + ('u%04X' % ord(c)))
    return ''.join(out)

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

SRC = 'ClawTweaksCenter/CenterMenuWindow.Faq.cs'
TABLE = 'ClawTweaksCenter/Core/Localization.Tables.cs'

src = io.open(SRC, encoding='utf-8-sig').read()

# The entry array only. A regex is enough here and nowhere else in this toolchain: this block is a
# literal array whose strings carry no comments and no braces (see the README's note on why
# survey3.py needs a state machine for the general case).
block = src[src.index('FaqEntries ='):src.index('// ── Entry')]
literals = re.findall(r'"((?:[^"\\]|\\.)*)"', block)

def unescape(x):
    return x.replace('\\"', '"').replace('\\\\', '\\')

wanted = [unescape(x) for x in literals if x.strip()]

# The screen's own chrome, which is not in the array.
wanted += ["Answers to the questions that come up most.",
           "Press Ⓐ on a question to open it.",
           "Open", "Close", "Open all", "Close all", "Back"]

table = io.open(TABLE, encoding='utf-8-sig').read()
missing = [w for w in wanted if ('["' + esc(w) + '"]') not in table]

sys.stdout.write("FAQ strings rendered: %d\n" % len(wanted))
sys.stdout.write("without a translation: %d\n" % len(missing))
for m in missing:
    sys.stdout.write("   " + m + "\n")
sys.exit(1 if missing else 0)
