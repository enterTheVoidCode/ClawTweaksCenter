# -*- coding: utf-8 -*-
"""Which UI literals actually reach a translated builder? Everything else needs a code change
before a translation could show, so it is not a table problem."""
import io, os, sys, glob, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from survey3 import literals, join_adjacent, looks_ui, UI_FILES

# Anything that ENDS in one of these reaches a translated builder. Written as suffixes rather
# than exact names after "MaintCard(" missed BuildMaintCard( and hid three cards for a build.
BUILDERS = [
    'Title(', 'Caption(', 'Body(', 'StatusRow(', 'ActionCallout(', 'ToolRow(', 'ModeBanner(',
    'AddAction(', 'Chip(', 'Tile(', 'Tab(', 'SettingRow(', 'LibraryMessage(', 'PromptRow(',
    'InfoLead(', 'InfoHeading(', 'InfoLine(', 'Loc.T(', 'Card(', 'Row(', 'Label(', 'Step(',
    'SetStatus(', 'Note(', 'Content =', 'Text =',
]

hit = collections.OrderedDict()
miss = collections.OrderedDict()
for f in sorted(glob.glob('ClawTweaksCenter/**/*.cs', recursive=True)):
    base = os.path.basename(f)
    if base not in UI_FILES or os.sep + 'obj' + os.sep in f:
        continue
    src = io.open(f, encoding='utf-8-sig', errors='replace').read()
    for ln, val, end in join_adjacent(src, literals(src)):
        if not looks_ui(val):
            continue
        before = src[max(0, end - len(val) - 400):end - len(val)]
        target = hit if any(b in before for b in BUILDERS) else miss
        target.setdefault(val, []).append('%s:%d' % (base, ln))

for name, d in (('reaches a builder', hit), ('needs a code change first', miss)):
    sys.stdout.write("\n=== %s: %d\n" % (name, len(d)))
    for f, c in collections.Counter(l[0].split(':')[0] for l in d.values()).most_common(30):
        sys.stdout.write("  %-38s %d\n" % (f, c))

out = io.open(sys.argv[1], 'w', encoding='utf-8', newline='')
for v, locs in hit.items():
    out.write(u'%s\t%s\n' % (locs[0], v.replace('\n', '\\n')))
out.close()
out = io.open(sys.argv[2], 'w', encoding='utf-8', newline='')
for v, locs in miss.items():
    out.write(u'%s\t%s\n' % (locs[0], v.replace('\n', '\\n')))
out.close()
