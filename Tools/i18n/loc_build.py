# -*- coding: utf-8 -*-
"""Generates Core/Localization.Tables.cs from strings.tsv.

WHY THERE IS A GENERATOR AT ALL. The tables started as four hand-written C# dictionaries, which
is a perfectly good shape for four languages and a bad one for thirteen: adding a language meant
a new 700-line block in the same file, every consistency check had to parse C# back into data,
and a wording corrected in one language had no mechanical way of reaching the others. The data
now lives in ONE tab-separated file - one row per English string, one column per language - and
this script is the only thing that writes the C#. Adding a language is a column.

    python Tools/i18n/loc_build.py            regenerate both outputs
    python Tools/i18n/loc_build.py --check    fail if either output is not what its TSV says

TWO OUTPUTS, TWO SOURCES. strings.tsv -> Core/Localization.Tables.cs is Center. inno.tsv ->
ClawTweaksInstaller/Languages/CustomMessages.iss is the SETUP, which lives in the other repo
(ClawTweaks_GoTweaksFork, found as a sibling of this one). The setup's strings used to sit inline
in the .iss - a second source of truth, in a second format, that no lint could reach. They are
data now, so placeholder parity and whitespace are checked for the setup too.

If the sibling repo is not there, the Inno half is skipped with a note and the Center half still
runs: a checkout of Center alone has to keep building.

THE TSV IS THE SOURCE. Editing Localization.Tables.cs by hand is a mistake the next run silently
undoes, which is why the generated file says so at the top.

An empty cell is not a gap to be filled later - it is a decision. A string with no translation
renders in English, by the design of Loc.T, so leaving a cell empty is how a translation that
cannot fit its control (see the width rule in check_width.py) is deliberately not shipped.
"""
import io, os, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the ClawTweaksCenter repo root
TSV = os.path.join(HERE, 'strings.tsv')
OUT = os.path.join(ROOT, 'ClawTweaksCenter', 'Core', 'Localization.Tables.cs')

# The setup half. The helper repo is a SIBLING of this one, not a submodule and not a fixed path:
# resolve it from here so the script works on any machine that has both checkouts next to each
# other, and say so plainly when it does not.
INNO_TSV = os.path.join(HERE, 'inno.tsv')
INNO_REPO = os.path.join(os.path.dirname(ROOT), 'ClawTweaks_GoTweaksFork')
INNO_OUT = os.path.join(INNO_REPO, 'ClawTweaksInstaller', 'Languages', 'CustomMessages.iss')

# TSV column -> the name the language carries in the setup's [Languages] section. Inno language
# names are identifiers, so the BCP-47 tags with a hyphen cannot be used as they stand.
INNO_NAME = collections.OrderedDict([
    ('en', 'en'), ('de', 'de'), ('fr', 'fr'), ('ko', 'ko'), ('es', 'es'), ('ru', 'ru'),
    ('el', 'el'), ('zh-Hans', 'zhHans'), ('zh-Hant', 'zhHant'), ('it', 'it'),
    ('pt-BR', 'ptBR'), ('ja', 'ja'), ('pl', 'pl'),
])

# WHAT THE SETUP DECLARES. ISCC rejects a CustomMessages entry whose language prefix is not in
# [Languages], so this list and that section have to agree. All thirteen are in as of 2026-09-15:
# ten from Inno's own .isl files, and el / zh-Hans / zh-Hant from the three vendored into
# ClawTweaksInstaller/Languages/ (see the README there).
#
# Keep it as a list rather than "everything in the TSV": a translated column that the setup cannot
# declare yet is a real situation - it was the situation for half a day - and the run prints which
# columns are being held back so the omission cannot go quiet.
INNO_SHIPPED = ['en', 'de', 'fr', 'ko', 'es', 'ru', 'it', 'pt-BR', 'ja', 'pl',
                'el', 'zh-Hans', 'zh-Hant']

# The helper's OSD cards. Same source (strings.tsv), third output, and the ONLY keys that reach
# the helper - see the module docstring for why the list is explicit.
#
# Card lines are separate keys because a card is "title\nbody" and the composed string could never
# match one; OsdLoc.Card splits on the newline and looks each line up on its own.
OSD_OUT = os.path.join(INNO_REPO, 'XboxGamingBarHelper', 'Localization', 'OsdLoc.Tables.cs')

OSD_KEYS = [
    # the controller mount card, and the two it can end on
    'Connecting controller',
    'Setting up virtual gamepad\u2026',
    'Controller connected',
    'Ready to play',
    'Controller not connected',
    # the restore-before-shutdown card
    'Restoring controller',
    'Switching back to the hardware gamepad\u2026',
    'Controller restored',
    # the firmware mouse card: the gamepad is silent in that mode, so the tile is the way back
    'Switch back with the Mode tile',
    'Hardware gamepad is back',
    'Controller not restored',
    'It stays in DInput mode',
    # the hardware-mouse card and the mode confirmations
    'HW Mouse ON \u2014 controller paused. Click the HW Mouse tile or press the keyboard hotkey to return.',
    'Virtual mouse restored',
    'Controller mode',
    'Mouse mode',
    # the two limits, as formats - the value moves, the sentence does not
    'Charge Limit: set it up in Settings first',
    'Charge Limit: On {0}%',
    'Charge Limit: Off',
    'FPS Limit: {0} FPS',
    'FPS Limit: Off',
    # the native Quick Panel's one translated notice: it cannot be used without the virtual pad
    'This panel works only with the virtual controller',
    'Use the Game Bar instead.',
    # the fallback card when the button is pressed without the virtual controller
    'Game Bar opened instead',
    'Quick Settings need the virtual controller',
    # the native Quick Panel's labels, values and hints (user, 2026-09-16). Keys in [brackets] are
    # controller buttons and sticks - the bracket is part of the key and stays in every language.
    'Brightness', 'Volume', 'muted',
    'Profile', 'Game profile', 'Global profile',
    'Power limit', 'FPS Limit', 'stepped', 'free',
    'CPU boost', 'On', 'Off',
    'OS power mode', 'Best power efficiency', 'Balanced', 'Best performance', 'Efficiency', 'Performance',
    'Overlay', 'Basic', 'Horizontal', 'Horizontal Custom', 'Vertical Custom', 'Vertical',
    'Background', 'Position',
    'Left top', 'Left middle', 'Left bottom', 'Center top', 'Center bottom',
    'Right top', 'Right middle', 'Right bottom',
    'Fly-in',
    'Set in the Game Bar', 'delete it in the Game Bar',
    # what A does on the controller profile row, where A creates the profile.
    '[A] create a game profile',
    # the tools page: its tiles and the two-press confirmation of the ones that end something
    'Mode', 'LED', 'End task', 'Sleep', 'Hibernate', 'Restart', 'Shut down', 'Controller', 'Mouse',
    '[A] confirm', '[B] cancel',
    'Vibration', 'Gyro', 'Gyro sens X', 'Gyro sens Y', 'Gyro smoothing', 'Gyro anti-deadzone',
    '[Right stick], hold [LT]',
    '[Right stick] ^ v source · < > step size',
    '[Start] opens Game Bar',
]

# TSV column -> the C# field name for that language's dictionary. The order here is the order the
# blocks appear in the generated file.
LANGS = collections.OrderedDict([
    ('de', 'German'),
    ('fr', 'French'),
    ('ko', 'Korean'),
    ('es', 'Spanish'),
    ('ru', 'Russian'),
    ('el', 'Greek'),
    ('zh-Hans', 'ChineseSimplified'),
    ('zh-Hant', 'ChineseTraditional'),
    ('it', 'Italian'),
    ('pt-BR', 'Portuguese'),
    ('ja', 'Japanese'),
    ('pl', 'Polish'),
])

HEADER = u'''\ufeffusing System.Collections.Generic;

namespace ClawTweaksCenter.Core
{
    public static partial class Loc
    {
        // GENERATED FROM Tools/i18n/strings.tsv BY Tools/i18n/loc_build.py - DO NOT EDIT BY HAND.
        // An edit here survives exactly until the next run of that script. Change the TSV instead,
        // regenerate, and commit both.
        //
        // The tables behind T(). Keyed by the English string; anything absent renders in English,
        // which is what makes leaving a string out a decision rather than a bug.
        //
        // WIDTH-CHECKED. Every entry passed a rendered-width check against its English original: at
        // most 1.7x the English, or five characters more, whichever is larger, with CJK characters
        // counted double because they render about twice as wide. Center's chips, tabs and tiles are
        // sized for the English word and do not grow. A translation that failed the check was
        // SHORTENED until it fit rather than dropped - a half-English Russian screen reads as broken
        // where a half-English German one merely reads as unfinished. The handful that could not be
        // shortened honestly are left empty in the TSV and stay English; Tools/i18n/check_width.py
        // reports them.
        //
        // Menu headings are English on purpose: the Home tiles keep their English titles and only
        // their one-line descriptions are translated. "Library" is the exception, and it is
        // translated everywhere it appears.
        //
        // NOT IN HERE, and not by oversight: date format strings ("d MMM yyyy") and brand names.
        //
        // A HANDFUL OF KEYS DO CARRY {0}, and they are the exception that proves the rule above: an
        // INTERPOLATED string is built at runtime and can never match a key, so those call sites were
        // rewritten to pass the format itself through Loc.F and fill it in afterwards. Translating
        // the format rather than the finished sentence is what lets a language put the game name
        // somewhere other than where English puts it. Everything still built by interpolation stays
        // English, and stays out of here.
'''

FOOTER = u'''
    }
}
'''

LEFT = os.path.join(HERE, 'left-in-english.tsv')


def render_left_in_english():
    """The trailing block DEV_GUIDELINES points at as the answer to "why is this one word still
    English".

    It is DATA, in left-in-english.tsv, and not a hand-written block at the bottom of the C#:
    this generator rewrites that file wholesale, so anything written there by hand survives
    exactly until the next run. It did not survive the first one."""
    try:
        text = io.open(LEFT, encoding='utf-8').read()
    except IOError:
        return u''

    rows = [l.split(u'\t') for l in text.splitlines()[1:] if l.strip()]
    if not rows:
        return u''

    out = [u'\n/*\n'
           u' * LEFT IN ENGLISH ON PURPOSE - the honest translation is wider than the control it\n'
           u' * has to fit in, and could not be shortened without saying something else. This list\n'
           u' * is the answer to "why is this one word still English", so it is kept rather than\n'
           u' * tidied away. GENERATED FROM Tools/i18n/left-in-english.tsv.\n'
           u' *\n']
    for r in rows:
        while len(r) < 6:
            r.append(u'')
        lang, en, honest, width, budget, note = r[0], r[1], r[2], r[3], r[4], r[5]
        out.append(u' *   %-8s "%s" -> "%s" (%s wide, budget %s)\n'
                   % (lang + u':', en, honest, width, budget))
        if note:
            out.append(u' *             %s\n' % note)
    out.append(u' */\n')
    return u''.join(out)


def tsv_unescape(s):
    out, i, n = [], 0, len(s)
    while i < n:
        c = s[i]
        if c == '\\' and i + 1 < n:
            d = s[i + 1]
            out.append({'t': '\t', 'n': '\n', 'r': '\r', '\\': '\\'}.get(d, d))
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def tsv_escape(s):
    return (s.replace('\\', '\\\\').replace('\t', '\\t')
             .replace('\n', '\\n').replace('\r', '\\r'))


def read_tsv(path=TSV):
    """-> (ordered list of English keys, {lang: {english: translation}})"""
    with io.open(path, encoding='utf-8') as f:
        rows = [line.rstrip('\n').rstrip('\r').split('\t') for line in f]
    rows = [r for r in rows if r and r[0].strip()]
    head = rows[0]
    if head[0] != 'english':
        raise SystemExit('strings.tsv: first column must be "english", found %r' % head[0])
    for col in head[1:]:
        if col not in LANGS:
            raise SystemExit('strings.tsv: unknown language column %r' % col)

    keys, tables, seen = [], {c: collections.OrderedDict() for c in head[1:]}, set()
    for n, row in enumerate(rows[1:], start=2):
        row += [''] * (len(head) - len(row))
        key = tsv_unescape(row[0])
        if key in seen:
            raise SystemExit('strings.tsv line %d: duplicate English key %r' % (n, key[:60]))
        seen.add(key)
        keys.append(key)
        for col, cell in zip(head[1:], row[1:]):
            cell = tsv_unescape(cell).strip()
            if cell:
                tables[col][key] = cell
    return keys, tables


def cs_escape(s):
    """C# string literal body, non-ASCII as \\uXXXX so the file survives any encoding it meets."""
    out = []
    for ch in s:
        if ch == '\\':
            out.append('\\\\')
        elif ch == '"':
            out.append('\\"')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\r':
            out.append('\\r')
        elif ch == '\t':
            out.append('\\t')
        elif ord(ch) < 0x20 or ord(ch) > 0x7E:
            out.append('\\u%04X' % ord(ch))
        else:
            out.append(ch)
    return ''.join(out)


def render(keys, tables):
    parts = [HEADER]
    for col, field in LANGS.items():
        table = tables.get(col) or {}
        parts.append(u'\n        private static readonly Dictionary<string, string> %s '
                     u'= new Dictionary<string, string>\n        {\n' % field)
        for k in keys:
            if k in table:
                parts.append(u'            ["%s"] = "%s",\n' % (cs_escape(k), cs_escape(table[k])))
        parts.append(u'        };\n')
    parts.append(FOOTER)
    parts.append(render_left_in_english())
    return ''.join(parts)


# ---------------------------------------------------------------------------------------------
# The setup half: inno.tsv -> ClawTweaksInstaller/Languages/CustomMessages.iss
# ---------------------------------------------------------------------------------------------

INNO_HEADER = u"""; GENERATED FROM ClawTweaksCenter/Tools/i18n/inno.tsv BY Tools/i18n/loc_build.py
; DO NOT EDIT BY HAND - an edit here survives exactly until the next run of that script.
; Change the TSV in the Center repo, regenerate, and commit both files.
;
; This file is pulled into ClawTweaksInstaller.iss with #include. It opens its own sections, so
; the include has to sit where a section may start.
;
; WHY THE TEXT MOVED OUT OF THE .iss. It used to be a second source of truth next to strings.tsv,
; in a different format, in a different repository. Nothing checked it: a %1 dropped from one
; language, a stray trailing space, a key renamed on one side - all of it compiled. It is data
; now, and loc_lint.py --inno reads it.
;
; %1 / %2 are FmtMessage slots and %n is a line break - Inno's grammar, not C#'s braces. They are
; resolved in M() / MF() at the bottom of the .iss, because CustomMessage() hands the text back
; raw.
;
; Log() lines are NOT here and must not be: the setup log is read in English by whoever debugs
; it, and a translated log is a log nobody can grep.
;
; Product names (ClawTweaks, Center, CTW Library, FSE, Game Bar, MSI Center M, winget) stay as
; they are in every language.
;
; The two FSE wizard pages carry the user's own wording (2026-09-10) - the translations keep its
; length and register. Do not expand them into prose in any language.
;
; WelcomeLabel2 is said, not hidden: the certificate import happens without a wizard page, so the
; one place a user can read that it happens at all is the welcome text. Not a question - the
; install needs it - but an answer.
"""


def read_inno_tsv(path=INNO_TSV):
    """-> (ordered [(section, key)], {lang: {key: value}})"""
    with io.open(path, encoding='utf-8') as f:
        rows = [line.rstrip('\n').rstrip('\r').split('\t') for line in f]
    rows = [r for r in rows if r and r[0].strip()]
    head = rows[0]
    if head[:2] != ['section', 'key']:
        raise SystemExit('inno.tsv: first two columns must be "section" and "key"')
    for col in head[2:]:
        if col not in INNO_NAME:
            raise SystemExit('inno.tsv: unknown language column %r' % col)

    order, tables, seen = [], {c: {} for c in head[2:]}, set()
    for n, row in enumerate(rows[1:], start=2):
        row += [''] * (len(head) - len(row))
        section, key = row[0], row[1]
        if key in seen:
            raise SystemExit('inno.tsv line %d: duplicate key %r' % (n, key))
        seen.add(key)
        order.append((section, key))
        for col, cell in zip(head[2:], row[2:]):
            if cell.strip():
                tables[col][key] = cell
    return order, tables


def render_inno(order, tables):
    """One block per key, languages in TSV column order, grouped by the section headings the .iss
    carried. A key with no value in a language simply has no line - Inno then falls back to the
    first language in [Languages], which is English. That is the same "an empty cell is a
    decision" rule the C# half runs on."""
    parts = [INNO_HEADER]
    langs = [c for c in INNO_NAME if c in tables and c in INNO_SHIPPED]

    # [Messages] first: it overrides one of Inno's own keys and cannot sit in [CustomMessages].
    msg = [(s, k) for s, k in order if s == 'Messages']
    if msg:
        parts.append(u'\n[Messages]\n')
        for _, key in msg:
            for lg in langs:
                if key in tables[lg]:
                    parts.append(u'%s.%s=%s\n' % (INNO_NAME[lg], key, tables[lg][key]))

    parts.append(u'\n[CustomMessages]\n')
    section = None
    for s, key in order:
        if s == 'Messages':
            continue
        if s != section:
            section = s
            rule = u'; --- %s ' % s
            parts.append(u'\n%s%s\n' % (rule, u'-' * max(0, 99 - len(rule))))
        for lg in langs:
            if key in tables[lg]:
                parts.append(u'%s.%s=%s\n' % (INNO_NAME[lg], key, tables[lg][key]))
        parts.append(u'\n')
    return ''.join(parts)


def build_inno(check):
    """-> (exit code, message). Skips cleanly when the sibling repo is not checked out."""
    if not os.path.isdir(INNO_REPO):
        return 0, 'Inno: sibling repo not found at %s - skipped.' % INNO_REPO
    if not os.path.isfile(INNO_TSV):
        return 0, 'Inno: %s not found - skipped.' % INNO_TSV

    order, tables = read_inno_tsv()
    text = render_inno(order, tables)
    counts = ', '.join('%s %d' % (c, len(tables.get(c) or {})) for c in INNO_NAME if c in tables)
    waiting = [c for c in INNO_NAME if c in tables and tables[c] and c not in INNO_SHIPPED]
    head = '%d setup keys; %s' % (len(order), counts)
    if waiting:
        head += ('\n  translated but not written - no .isl in [Languages] yet: %s'
                 % ', '.join(waiting))

    if check:
        try:
            current = io.open(INNO_OUT, encoding='utf-8-sig').read()
        except IOError:
            return 1, head + '\nCustomMessages.iss is missing - run loc_build.py.'
        if current.lstrip(u'\ufeff') == text.lstrip(u'\ufeff'):
            return 0, head + '\nCustomMessages.iss is up to date.'
        return 1, head + '\nCustomMessages.iss does NOT match inno.tsv - run loc_build.py.'

    folder = os.path.dirname(INNO_OUT)
    if not os.path.isdir(folder):
        os.makedirs(folder)
    # utf-8-SIG: an #include'd file without a BOM is read as ANSI by Inno, and this one carries
    # Korean, Greek and both Chinese scripts.
    io.open(INNO_OUT, 'w', encoding='utf-8-sig', newline='\r\n').write(text)
    return 0, head + '\nwrote %s' % INNO_OUT


# ---------------------------------------------------------------------------------------------
# The helper half: strings.tsv (OSD_KEYS only) -> XboxGamingBarHelper/Localization/OsdLoc.Tables.cs
# ---------------------------------------------------------------------------------------------

OSD_HEADER = u"""// GENERATED FROM ClawTweaksCenter/Tools/i18n/strings.tsv BY Tools/i18n/loc_build.py
// DO NOT EDIT BY HAND - an edit here survives exactly until the next run of that script.
//
// The notification cards AND the native Quick Panel's labels (user, 2026-09-16). The i18n plan's
// decision 5 had scoped this to the cards alone; the panel came in as one block once it had its
// controller page and was about to ship.
//
// Keyed by the English line, like every other table in this project, so a line with no row simply
// renders in English. OsdLoc.Card splits a card on its newline and looks up each line separately -
// a card is "title\\nbody" and the composed string could never match a key.
//
// WHICH KEYS: Tools/i18n/loc_build.py holds the list (OSD_KEYS). Adding a card line means adding
// it there and in strings.tsv, then regenerating - the generator stops if the list names a key the
// TSV does not have, so the two cannot drift apart quietly.
using System.Collections.Generic;

namespace XboxGamingBarHelper.Localization
{
    internal static class OsdLocTables
    {
"""

OSD_FOOTER = u"""
        internal static Dictionary<string, string> For(OsdLanguage language)
        {
            switch (language)
            {
%s                default: return null;
            }
        }
    }
}
"""


def render_osd(tables):
    """One dictionary per language, holding only OSD_KEYS. English is not a table: an absent
    translation returns the key, which IS the English."""
    missing = [k for k in OSD_KEYS if all(k not in (tables.get(c) or {}) for c in LANGS)]
    parts = [OSD_HEADER]
    cases = []
    for col, field in LANGS.items():
        table = tables.get(col) or {}
        rows = [(k, table[k]) for k in OSD_KEYS if k in table]
        parts.append(u'        private static readonly Dictionary<string, string> %s '
                     u'= new Dictionary<string, string>\n        {\n' % field)
        for k, v in rows:
            parts.append(u'            { "%s", "%s" },\n' % (cs_escape(k), cs_escape(v)))
        parts.append(u'        };\n\n')
        cases.append(u'                case OsdLanguage.%s: return %s;\n' % (field, field))
    parts.append(OSD_FOOTER % ''.join(cases))
    return ''.join(parts), missing


def build_osd(keys, tables, check):
    """-> (exit code, message). Skips cleanly when the sibling repo is not checked out."""
    if not os.path.isdir(INNO_REPO):
        return 0, 'OSD: sibling repo not found - skipped.'

    unknown = [k for k in OSD_KEYS if k not in keys]
    if unknown:
        return 1, ('OSD: %d key(s) in OSD_KEYS are not in strings.tsv - add the row or fix the '
                   'spelling:%s' % (len(unknown), ''.join('\n    ' + repr(k) for k in unknown)))

    text, missing = render_osd(tables)
    head = ('%d OSD keys' % len(OSD_KEYS))
    if missing:
        head += ' (%d with no translation in any language)' % len(missing)

    if check:
        try:
            current = io.open(OSD_OUT, encoding='utf-8-sig').read()
        except IOError:
            return 1, head + '\nOsdLoc.Tables.cs is missing - run loc_build.py.'
        if current.lstrip(u'\ufeff') == text.lstrip(u'\ufeff'):
            return 0, head + '\nOsdLoc.Tables.cs is up to date.'
        return 1, head + '\nOsdLoc.Tables.cs does NOT match strings.tsv - run loc_build.py.'

    folder = os.path.dirname(OSD_OUT)
    if not os.path.isdir(folder):
        os.makedirs(folder)
    io.open(OSD_OUT, 'w', encoding='utf-8', newline='\r\n').write(text)
    return 0, head + '\nwrote %s' % OSD_OUT


def main():
    keys, tables = read_tsv()
    text = render(keys, tables)

    counts = ', '.join('%s %d' % (c, len(tables.get(c) or {})) for c in LANGS)
    sys.stdout.write('%d English keys; %s\n' % (len(keys), counts))

    check = '--check' in sys.argv
    rc = 0

    if check:
        current = io.open(OUT, encoding='utf-8-sig').read()
        if current.lstrip('\ufeff') == text.lstrip('\ufeff'):
            sys.stdout.write('Localization.Tables.cs is up to date.\n')
        else:
            sys.stdout.write('Localization.Tables.cs does NOT match strings.tsv - run loc_build.py.\n')
            rc = 1
    else:
        io.open(OUT, 'w', encoding='utf-8', newline='\n').write(text)
        sys.stdout.write('wrote %s\n' % OUT)

    # The setup half. Its failure is reported next to the C# one rather than instead of it, so a
    # stale CustomMessages.iss cannot hide behind an up-to-date Localization.Tables.cs.
    inno_rc, inno_msg = build_inno(check)
    sys.stdout.write(inno_msg + '\n')

    osd_rc, osd_msg = build_osd(keys, tables, check)
    sys.stdout.write(osd_msg + '\n')

    return rc or inno_rc or osd_rc


if __name__ == '__main__':
    sys.exit(main())
