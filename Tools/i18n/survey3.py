# -*- coding: utf-8 -*-
"""C#-aware literal extractor.

A plain regex desynchronises on the first quote inside a // comment and then reports the CODE
between literals as if it were text - that is why the first attempt found 164 literals in a 145 KB
file and none of the ones actually on screen. So this walks the file as a tiny state machine
instead, and joins adjacent literals (a + b) because a sentence split over three source lines is
one runtime string, and the table is keyed by the runtime string.
"""
import io, os, sys, glob, collections

def literals(src):
    """Yield (line, value, joined_with_previous) for every "..." and $"..." literal."""
    out = []
    i, n, line = 0, len(src), 1
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1; i += 1; continue
        if c == '/' and i + 1 < n and src[i+1] == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and src[i+1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i+1] == '/'):
                if src[i] == '\n':
                    line += 1
                i += 1
            i += 2
            continue
        if c == "'":                                  # char literal
            i += 1
            if i < n and src[i] == '\\':
                i += 1
            i += 2
            continue
        if c == '@' and i + 1 < n and src[i+1] == '"':   # verbatim string
            i += 2
            start_line = line
            buf = []
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i+1] == '"':
                        buf.append('"'); i += 2; continue
                    i += 1; break
                if src[i] == '\n':
                    line += 1
                buf.append(src[i]); i += 1
            out.append((start_line, ''.join(buf), i))
            continue
        if c == '"':
            i += 1
            start_line = line
            buf = []
            while i < n and src[i] != '"':
                if src[i] == '\\' and i + 1 < n:
                    esc = src[i+1]
                    if esc == 'u' and i + 5 < n:
                        try:
                            buf.append(chr(int(src[i+2:i+6], 16))); i += 6; continue
                        except ValueError:
                            pass
                    buf.append({'n': '\n', 't': '\t', 'r': '\r'}.get(esc, esc)); i += 2; continue
                if src[i] == '\n':
                    line += 1
                buf.append(src[i]); i += 1
            i += 1
            out.append((start_line, ''.join(buf), i))
            continue
        i += 1
    return out

def join_adjacent(src, lits):
    """Merge a,b when only whitespace and a '+' sit between them."""
    merged = []
    for ln, val, end in lits:
        if merged:
            gap = src[merged[-1][2]:end - len(val) - 2]
            # crude but sufficient: the gap is the raw source between the two literals
            gap = gap.strip()
            if gap == '+':
                p = merged[-1]
                merged[-1] = (p[0], p[1] + val, end)
                continue
        merged.append((ln, val, end))
    return merged

def looks_ui(v):
    if len(v) < 4 or ' ' not in v:
        return False
    if '{' in v or v.startswith('http') or v.startswith('pack:') or v.startswith('-'):
        return False
    for bad in ('.exe', '.dll', '.json', 'Software\\', 'Get-', 'Add-', 'Remove-', 'Select-',
                'ForEach-', '$_', 'HKEY', 'CN=', '|', 'Segoe'):
        if bad in v:
            return False
    if not (v[0].isalpha() or v[0] in u'•→'):
        return False
    return True

UI_FILES = set("""CenterMenuWindow.xaml.cs CenterMenuWindow.Library.cs CenterMenuWindow.GameMenu.cs
CenterMenuWindow.Maintenance.cs CenterMenuWindow.Misc.cs CenterMenuWindow.Tray.cs
CenterMenuWindow.CenterSettings.cs MainWindow.xaml.cs InstallCenterWindow.xaml.cs DetectPhase.cs
ToolsPhase.cs ControllerPhase.cs InstallPhase.cs FinalizePhase.cs PhaseBase.cs PlaceholderPhase.cs
OnboardingRunner.cs MaintenanceRunner.cs PrerequisiteGuide.cs CertInstaller.cs ToolDetect.cs
HelperControl.cs SetupVersionCheck.cs SelfInstaller.cs PackageInstaller.cs BuildDownloader.cs
GameLibrary.cs ReleaseNotes.cs""".split())

rows = collections.OrderedDict()
for f in sorted(glob.glob('ClawTweaksCenter/**/*.cs', recursive=True)):
    base = os.path.basename(f)
    if base not in UI_FILES or os.sep + 'obj' + os.sep in f or os.sep + 'bin' + os.sep in f:
        continue
    src = io.open(f, encoding='utf-8-sig', errors='replace').read()
    for ln, val, _ in join_adjacent(src, literals(src)):
        if looks_ui(val):
            rows.setdefault(val, []).append('%s:%d' % (base, ln))

out = io.open(sys.argv[1], 'w', encoding='utf-8', newline='')
for v, locs in rows.items():
    out.write(u'%s\t%s\n' % (locs[0], v.replace('\n', '\\n')))
out.close()
sys.stdout.write("UI literals: %d\n" % len(rows))
for f, c in collections.Counter(l[0].split(':')[0] for l in rows.values()).most_common(40):
    sys.stdout.write("  %-38s %d\n" % (f, c))
