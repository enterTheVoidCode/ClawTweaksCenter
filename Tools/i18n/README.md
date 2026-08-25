# Translation tooling

`ClawTweaksCenter/Core/Localization.Tables.cs` is the artefact. It is ordinary C# and can be edited
by hand — **nothing here has to be run to change a translation.** These scripts exist because
finding out *what still needs translating* is the hard half, and because two of the answers cost a
build each to discover.

Run them from the **repository root**, with Python 3:

```
python Tools/i18n/loc_gen2.py                      # regenerate Localization.Tables.cs
python Tools/i18n/consistency.py                   # compare the four tables against each other
python Tools/i18n/survey4.py hit.tsv miss.tsv      # what is on screen and can it be translated
python Tools/i18n/cover.py hit.tsv                 # of the reachable strings, how many are done
```

## What each one is for

**`loc_gen2.py`** rebuilds the tables from `loc_*.py`, applying the width rule (at most 1.7× the
English or five characters more, CJK counted double) and writing the entries that fail into the
comment block at the bottom of the generated file. That list is the record of what is deliberately
left in English — keep it, do not tidy it away.

The `loc_*.py` files are rounds, in the order the work happened. There is nothing special about the
split; a new batch is a new file plus two lines in `loc_gen2.py`.

**`consistency.py`** is the one worth running before every release. It answers the question that
kept producing defects: *when a German wording was corrected, did the other three get the same
treatment?* It checks three things, and each has caught a real one:

- a key one language has and another does not,
- developer words (`build`, `release`, `nightly`) still sitting inside a translation,
- vocabulary families side by side, so "Bibliothek" against "Bibliothek Einstellungen" is visible.

**`survey3.py` / `survey4.py`** find the user-facing string literals and split them into *reaches a
translated builder* and *needs a code change first*.

## Two traps these were written around

**A regex cannot extract C# literals.** It desynchronises on the first quote inside a `//` comment
and then reports the CODE between literals as if it were text — the first attempt found 164
literals in a 145 KB file and none of the ones actually on screen. `survey3.py` walks the file as a
small state machine instead, and joins adjacent literals, because a sentence split over three source
lines is one runtime string and the tables are keyed by the runtime string.

**`survey4.py`'s builder list is a heuristic, not an authority.** It decides "reachable" by looking
for a builder name in the preceding few hundred characters. That misses a literal defined far from
its call — a table of card titles, for instance — and it once missed `BuildMaintCard(` because the
list said `MaintCard(`, which hid three cards' descriptions for a whole build. Treat a "needs a code
change first" verdict as a question, not an answer.
