# Translating Center into more languages — the plan

Written to be picked up cold, by somebody who was not in the conversation that started it.
Tick the boxes as you go; **the boxes are the only record of progress that survives.**

---

## 0. What this job is

Center ships in five languages (English, German, French, Korean, Spanish). It is being taken to
**thirteen**: adding Russian, Greek, Chinese simplified, Chinese traditional, Italian, Brazilian
Portuguese, Japanese, Polish. The OSD notification cards of the helper and the widget get the same
treatment, **and so does the Inno setup** (P3b) - it is the first screen a user in any of these
languages sees, and today it speaks the five and falls back to English for the rest.

### Where the code is — this spans TWO repositories

| | path | what lives there |
|---|---|---|
| **Center** | `C:\Users\AlexB\Documents\Development\ClawTweaksCenter` | everything in P1 and P2 |
| **Helper + widget** | `C:\Users\AlexB\Documents\Development\ClawTweaks_GoTweaksFork` | everything in P3 |
| **Inno setup** | same repo, `ClawTweaksInstaller\ClawTweaksInstaller.iss` | everything in P3b |

Read `DEV_GUIDELINES.md` and `CONTRIBUTING.md` in the Center repo before touching anything. The
translation design is documented there, in the section beginning *"Translation happens at the
builders"* — this plan implements that design, it does not replace it.

**Building Center:** `dotnet publish -c Release` (needs the .NET 10 SDK). Only the exe under
`publish/` is shippable — see the Build section of DEV_GUIDELINES for why a plain `dotnet build`
output must not be handed out. The helper repo is the one built with `.\Build-Package.ps1`; that
script does **not** apply to Center.

### The decisions already taken (2026-09-14, by the repo owner)

These are settled. Do not relitigate them, and do not quietly do something else.

1. **Canonical table + generator.** `strings.tsv` is the source of truth;
   `Core/Localization.Tables.cs` is generated and never hand-edited.
2. **Over-wide translations are SHORTENED until they fit — not dropped.** This *reverses* the
   earlier rule. It was right for four languages close to English in length; with Russian, Polish,
   Greek and Portuguese in the set, dropping would leave those screens visibly half-English, which
   reads as broken rather than as unfinished. Only what cannot be shortened honestly stays English,
   and that goes in `left-in-english.tsv`.
3. **Interpolated strings go through `Loc.F`, with the format as the key** — not "translate the
   fixed part and concatenate the value", which bakes English word order in.
4. **Both Chinese scripts**, zh-Hans and zh-Hant. MSI is a Taiwanese company.
5. **OSD scope: the notification cards only.** The native Quick Panel's ~60 labels are out of scope.
6. **The helper follows CENTER's language, not the OS.** Somebody who set Center to Italian on a
   German Windows meant it. Fallback: Center's choice → OS language → English.

### The rules the translations have to pass

- **Width.** At most `max(english + 5, english × 1.7)`, CJK counted as two per character. Center's
  chips, tabs and tiles are sized for the English word and do not grow.
- **Menu headings stay English.** Home tiles keep their English titles; only their one-line
  descriptions are translated. `Library` is the deliberate exception and is translated everywhere.
- **An empty cell is a decision**, not an oversight — `Loc.T` returns the English for an absent key.
  Record the reason in the commit, and in `left-in-english.tsv` when it is a width decision.

---

## 1. Files, and which of them you may edit

```
Tools/i18n/
  strings.tsv                  SOURCE. English key + one column per language.
  left-in-english.tsv          SOURCE. What stays English on purpose, and why.
  loc_build.py                 generates the C#.  --check fails if it is stale.
  loc_coverage.py              finds strings the tables do not cover.
  loc_lint.py                  placeholder parity, whitespace, duplicates, width report.
  glossary.tsv                 DOES NOT EXIST YET - task P2.1.
  README.md                    how the mechanism works.
  TRANSLATION-INTO-MORE-LANGUAGES-PLAN.md   this file.

ClawTweaksCenter/Core/
  Localization.cs              Loc.T / Loc.F, the enum, Detect, NameOf, TableFor. Hand-written.
  Localization.Tables.cs       GENERATED. Never edit. Never add a comment to the bottom of it -
                               the generator rewrites the whole file and it will not survive.
```

Eighteen one-off scripts from the German round were deleted in `3b93e71`. They are in the history.
**Do not resurrect `survey3.py`**: it walked a hand-maintained list of 26 filenames, so every screen
added after it was written was invisible to it, and its report *looked* complete. That is the single
reason the German round cost hours of manual curation on the device.

---

## P0 — Foundation ✅ done

- [x] TSV + generator; round trip proven identical for all four shipped languages
- [x] `loc_coverage.py` walks all 113 `.cs` files, classifying by shape and position
- [x] Thirteen languages wired: `UiLanguage`, `Detect`, `DetectChinese`, `NameOf`, `TableFor`,
      the settings picker
- [x] The eight new tables are empty, which renders correct English by design
- [x] `left-in-english.tsv` + generator footer — **this was a regression**: the first regeneration
      deleted the "left in English on purpose" block that DEV_GUIDELINES points readers at. It is
      data now, so it cannot be deleted by a regeneration again.

Commits `5b9c475`, `e8ae4b2`, `4846fc3`, `3b93e71`.

---

## P1 — Close the English side

None of this is translation. It is settling the key set **once**, because from here on a string
found late costs twelve translations instead of one.

Baseline measured 2026-09-15 (`python Tools/i18n/loc_coverage.py`):
`wrapped 4 · builder 63 · loose 494 · interpolated 111 · stale 48 · 718 keys`.

⚠️ **The correct fix is usually NOT to add `Loc.T` at the call site.** DEV_GUIDELINES is explicit:
*"Do not sprinkle `Loc.T` through new code. If a new string does not reach the screen through one of
those builders, that is the thing to fix."* So for each candidate, in this order of preference:
**(a)** it already reaches a builder → just add the TSV row; **(b)** it does not → route it through
the right builder; **(c)** only where neither is possible, `Loc.T` at the call site, and say why in
the commit.

- [x] **P1.1 — the 4 `wrapped`** (done 2026-09-15; three of the four went stale with the experimental band, the fourth has its row). They already call `Loc.T` and have no row, so they render English
      in *all four* shipped languages today:
      - `CenterMenuWindow.CenterSettings.cs:130` — `Experimental`
      - `CenterMenuWindow.CenterSettings.cs:139` — `Center starts the helper`
      - `CenterMenuWindow.CenterSettings.cs:147` — `Only in the full screen experience. …`
      - `CenterMenuWindow.Library.cs:2895` — `Tab visibility & order`
- [x] **P1.2 — the 63 `builder`** (done 2026-09-15: 31 rows added, 33 recorded as proper-noun / heading / not-ui / log / dead in `triage.tsv`; the coverage tool now subtracts what is triaged). Tray menu, game menu (Favorite / Rename / Remove), Sleep /
      Hibernate / Launch / Edit, platform tabs, parts of Maintenance. Proper nouns and symbols
      (`Steam`, `Epic`, `Xbox`, `ROMs`, `A-Z`, `Z-A`) stay English — record that they were seen and
      rejected, or the next run of the survey reports them again as if nobody had looked.
- [x] **P1.3 — the 494 `loose`** (done 2026-09-15, `loose 0`). Three passes, all recorded in
      `triage.tsv`: by shape (identifiers, paths, registry names, the log-only files), by hand
      (endonyms, the deliberately English half of the language picker, WMI queries, Playnite/Steam
      file names, parser errors, install log lines), and then the 118 real UI strings — 120 rows in
      de/fr/ko/es plus call-site changes where the text is built from parts: `Loc.F` in
      `ClawProfileDetails` (units, boost/action fallbacks), `LeaveRunner` (the joined summary was
      never a key — translated part by part), `ControllerHealth`, `OnboardingRunner`, `ToolDetect`
      (the two usbip UNSUPPORTED formats), `BuildDownloader` (translated where thrown, sentence by
      sentence - the install log file gets the translated line for those, the only place that is
      so); `Loc.T` on the prerequisite card (`Why`/`WhatToGet`/`Warning`, BROKEN detail), the
      certificate steps, the hand-off Ⓨ button, the device banner name, the build origin
      (`Test build`/`Release`/`Nightly`), release notes. Three Maintenance/setup-check sentences
      that were interpolated were rewritten to `Loc.F` on the way (they are P1.4 items, but they
      were on the same line).
      Two tool fixes came out of it: `loc_coverage.py` matches triage rows on **stripped** text
      (the scanner keeps a literal's leading space, `--gaps` and the person copying it do not,
      so `' is not exported'` never matched), and it rejects SVG path data (`M45.9,41H…`) outright
      instead of relying on a 150-char truncated triage row that could never match either.
      Left English on purpose: `Deploying helper files failed: <exception>` (install log),
      ToolDetect's healthy-state details (install log only), the `— DEBUG` device names.
- [x] **P1.4 — the `interpolated` → `Loc.F`** (done 2026-09-15, `interpolated 0`). Of the 105,
      **30 reach a screen** and are now `Loc.F` with the format as the key (33 rows): the Maintenance
      results (reset/backup/restore, the two restore warnings), the Insider-channel note, the
      Center-update card, `Installing {0}`, the install-transition line (`DescribeTransition` builds
      an English line for the file and a `Loc.F` one for the panel; `LogShown` carries both), the
      download progress lines, `Outdated version — install {0} or newer`, the auto-jump step
      texts, and the self-installer's status lines (they reach `InstallCenterWindow` through
      `Loc.T` on the finished string, which passes a translated line through unchanged). The other
      **75 are log lines** and were recorded as such in `triage.tsv`: HelperControl/HelperPipeClient/
      FseHelperStart/HelperHandover diagnostics, the mirrored `Shared/` files (never edited here),
      UiStallTrace/WindowMode, the dead `Phases/` wizard, registry paths and cmd lines.
      The rule from the decision holds: the **format** is the key; a language may move the value.
- [x] **P1.5 — the stale keys** (done 2026-09-15). 47 rows deleted: the experimental band, the
      wishlist line, the widget-interval hint, the old Home tiles and leave-screen texts, and four
      keys whose English had been edited in the code after the row was written (the current text
      already had a row of its own). One live key was edited to match its call site (the legacy
      uninstaller notice, now with "(press Ⓨ to refresh)") and that call site wrapped in `Loc.T`.
      `loc_coverage.py` now scans `Localization.cs` itself, so `System language` is no longer a
      false stale. **Stale is 0.**
- [x] **P1.6 — `loc_lint.py`** (done 2026-09-15): placeholder parity as ERROR, stray whitespace and
      duplicate keys and shifted rows as ERROR, identical-to-English and double spaces as WARN, the
      width budget as a report (`--width-only`, worst first; `left-in-english.tsv` exempts). First
      run on the shipped four: **0 errors**, 34 warnings, 45 over width - the P2.4 worklist. `%1`/`%n`
      for the setup come with `inno.tsv` (P3b), behind a flag, not as a relaxation.
- [x] **P1.7 — DEV_GUIDELINES brought in line** (done 2026-09-15): `Loc.F` for interpolated strings,
      shorten-don't-drop, the left-in-English list generated from `left-in-english.tsv`, the tables
      generated from `strings.tsv`, `Loc.Order` named as dead, and the language list corrected to
      thirteen.

**Gate out of P1:** coverage reports `wrapped 0` and `builder 0`; `loc_build.py --check` passes;
`dotnet publish -c Release` succeeds.
✅ **Passed 2026-09-15:** `wrapped 0 · builder 0 · loose 0 · interpolated 0 · stale 0`, `--check`
passes, `dotnet build -c Release` clean, `loc_lint.py --strict` 0 errors (42 over width = the P2.4 list).

---

## P2 — Translate

Expect 1000–1100 keys after P1, times twelve columns: roughly 13,000 cells. This is the bulk of the
job and it is several working sessions, not one.

- [x] **P2.1 — `glossary.tsv` first** (done 2026-09-15: 51 terms × 12 languages, with a `note` column that says what stays English and why — TDP, FPS, DInput/XInput, Game Bar, Center, helper), ~40 recurring terms per language: TDP, Fan Curve, Profile,
      Handheld, Overlay, Helper, Center, Controller, DInput / XInput, Charge Limit, Sleep, Hibernate,
      Library, Onboarding, Backup, Restore. Fixed **before** any screen is translated, or the same
      word arrives three ways on three screens. Terms gamers use untranslated (TDP, FPS, DInput)
      stay English on purpose — translating them makes the screen harder to read, not easier.
- [x] **P2.2 — fill the four existing columns' gaps** (done 2026-09-15; the cells still empty are identical-to-English on purpose: units, `Release`/`Nightly`, Windows folder names — the lint would flag a copy) (de 717, fr 715, ko 718, es 717 of 718 today,
      plus everything P1 adds).
- [x] **P2.3 — the eight new columns** (all eight done 2026-09-15), one language at a time, in batches of ~120 keys:
      - [x] `it` Italian — done 2026-09-15, 850 of 858 (the 8 empties are the deliberate ones above); lint clean, 0 over width
      - [x] `pt-BR` Portuguese — done 2026-09-15, 838 of 858 (empties = the deliberate ones plus 20 cells identical to English: Online, Offline, Chat, Anti-cheat, Info, Layout, Macro, Menu, Mouse, Preset, Status, Windows Update, Desktop, Downloads, Release, Nightly, the four `{0} MHz`-style formats); lint clean, 0 over width
      - [x] `pl` Polish — done 2026-09-15, 844 of 858 (empties = the deliberate ones plus Online, Offline, Anti-cheat, Info, Menu, Preset, Start, System, Windows Update, Nightly); lint clean, 0 over width
      - [x] `ru` Russian — done 2026-09-15, 854 of 858 (empties = Windows Update, Nightly, `{0} fps (Intel)`, `{0} fps (RTSS)`); lint clean, 0 over width
      - [x] `el` Greek — done 2026-09-15, 845 of 858 (empties = Windows Update, Release, Nightly, the two `{0} fps` formats, plus Anti-cheat, Gyro, Info, Macro, Nearest Neighbour, `{0} MHz`, `{0} Hz`, Desktop); lint clean, 0 over width — 11 cells kept over budget with a note in `left-in-english.tsv`, `Busy` at +4 the widest
      - [x] `ja` Japanese — done 2026-09-15, 852 of 858 (empties = Windows Update, Nightly, `{0} MHz`, `{0} Hz`, the two `{0} fps` formats); lint clean, 0 over width — only `Uninstall` is kept over budget, with a note
      - [x] `zh-Hans` Chinese simplified — done 2026-09-15, 850 of 858 (empties = Windows Update, Nightly, `{0} MHz`, `{0} Hz`, the two `{0} fps` formats, and the two `OptiScaler wiki` lines); lint clean, **0 over width on the first pass** — Chinese is short even at two columns per character
      - [x] `zh-Hant` Chinese traditional — done 2026-09-15, 850 of 858, same empties as Hans; lint clean, 0 over width. Derived from Hans and then corrected — **not** a character conversion: Taiwan vocabulary throughout (控制器 not 手柄, 設定檔 not 配置文件, 資料夾 not 文件夹, 解除安裝 not 卸载, 裝置 not 設備, 憑證 not 證書, 排程工作 not 計劃任務, 小工具 not 小組件, 滑鼠 not 鼠標). A scan against a list of simplified-only characters found none left in the column.
- [x] **P2.4 — width pass per language.** ✅ all twelve columns 2026-09-15 (0 over budget); repeats once per new column.
      **One addition to the rule:** a cell that is +1/+2 over and has no shorter honest word (`Busy` → `Beschäftigt`, `Today` → `Aujourd'hui`) may stay **in the cell** with a `kept in the cell:` note in `left-in-english.tsv` — the lint exempts by (lang, english), so the record still says why. Emptying the cell was the worse answer for those four. `loc_lint.py` lists every over-budget cell; each is
      shortened until it fits. What cannot be shortened honestly goes in `left-in-english.tsv`
      with its width and budget, and the cell is left empty.

**Gate out of P2: ✅ CLOSED 2026-09-15 by the repo owner.** Lint clean, `--check` passes, builds,
and **several** languages opened in Center on the device. Not all thirteen, and the owner said so:
the remainder goes to test users. That is a decision, not an oversight — record it as one. What a
test user is actually checking is the thing nobody here can: whether the words are right. Width
and font coverage are what the owner could see, and those were fine in what was opened.

Device-test builds so far: setup `0.3.1.190` (Center 0.2.71: en/de/fr/ko/es/it), setup `0.3.1.191`
(Center 0.2.72: plus pt-BR/pl/ru), setup `0.3.1.192` (Center 0.2.73: **all thirteen** — plus
el/ja/zh-Hans/zh-Hant), SHA256 `8E9B4449BE89AE5EC591654F634160B953055DF2B20531D9F56514CACE59C2F2`,
109,3 MB, `[VELOPACK]`, FSE package included, nothing published.
**Nothing has been opened on the device yet — the gate is still open.** Greek and both Chinese
scripts have never been seen in Center's font at all; Korean and Japanese only in passing.

---

## P3 — The helper and the widget OSD

**Different repository:** `C:\Users\AlexB\Documents\Development\ClawTweaks_GoTweaksFork`. Its own
rules apply there — build with `.\Build-Package.ps1` and nothing else, `Shared/Enums/Function.cs` is
append-only. The helper is .NET Framework 4.8, not .NET 10. Scope is the notification cards only.

- [x] **P3.1 — how Center's language reaches the helper. DECIDED AND BUILT 2026-09-15.**
      **Registry at startup, pipe for changes.** The helper reads
      `HKCU\Software\ClawTweaks\Center` → `Language` when it starts - the value already exists
      and is stored BY NAME (`Greek`, `System`, …), which is why renaming a `UiLanguage` member
      is the one unsafe edit. That works with no Center running and before any pipe connection,
      which is exactly the case the cards at boot fall into. Center additionally pushes a change
      over the pipe so the next card is right immediately. Fallback: Center's choice → OS
      language → English.
      ✅ **Verified 2026-09-15:** the task runs `UserId=<the user>`, `LogonType=Interactive`,
      `RunLevel=Highest` — elevated, but in the user context, so `HKEY_CURRENT_USER` is the
      right hive. If it is ever changed to SYSTEM this reads a different hive, finds nothing,
      and every card quietly falls back to the OS language.
      **Simplified while building it: there is no pipe push.** The value is read fresh on every
      card instead of being cached, which is what the push was for — the next card after a
      change is already right, with no message to add on Center's side and no cache to
      invalidate. A card is a rare event and a registry read is nothing next to drawing a
      Direct2D window.
- [x] **P3.2 — done 2026-09-15.** `XboxGamingBarHelper/Localization/OsdLoc.cs` (hand-written:
      language resolution, `L`, `Card`, `F`) and `OsdLoc.Tables.cs` (generated).
      `loc_build.py` now has a **third** output, and it carries ONLY the keys the cards use —
      an explicit `OSD_KEYS` list, not all 858 rows of Center UI in a .NET 4.8 background
      process, and not a scan of the helper source that would break silently. A key in the
      list that the TSV does not have stops the generator.
      `Card()` is the piece that matters: a card is `"title\nbody"`, so it splits on the
      newline and looks each line up separately. That is what makes a body ending in a value
      translatable without making every value a key.
- [x] **P3.3 — done 2026-09-15**, 20 keys. The named ones:
      `Program.MSIClaw.cs` — charge-limit hint (~1954), hardware-mouse mode (~2754), controller
      connecting / connected / not connected (~4212, ~4228, ~4242);
      `Program.PowerActions.cs` — controller restoring / restored / not restored (~117, ~137, ~139).
      Cards split on the first newline: line 1 is the title, the rest is the body.
      Also routed, because they sit on the same surface and half a translated surface reads as
      broken: `Controller mode` / `Mouse mode`, and the charge-limit and FPS-limit
      confirmations — the last two as **formats** (`Charge Limit: On {0}%`), so a language may
      move the number.
      ⚠️ **Still English, and deliberately listed rather than left to be discovered:** the
      failure REASON on the mount card (a runtime diagnostic, and the thing a bug report
      quotes), `Overlay: {0}` and `Hotkey: {0}`, the brightness and volume cards, and the
      power-source card (`Charging` and its parts). Each is one TSV row plus one `OSD_KEYS`
      entry away.
- [x] **P3.4 — done 2026-09-15 with NO widget change at all**, which is a deviation from what this
      box assumed and the better answer. The widget does not draw its cards: it sends
      `title + "\n" + content` over the pipe as `ShowOSDNotification`, and the HELPER draws it
      (`Program.PipeHandlers.cs`). One `OsdLoc.Card(notifText)` there translates every card the
      widget raises.
      This dissolves the problem the box names. The concatenation was only a problem while the
      translation had to happen where the string was BUILT; doing it where the card is DRAWN
      makes line-wise lookup possible instead. It also avoids giving a UWP process a registry
      read it is not allowed to make.
      ✅ **Done 2026-09-26 (0.4.0.56):** those card lines now have their rows. The composer moved
      out of the widget entirely in the meantime (`Shared/Data/GameStartCard.cs`, the helper draws
      every game start regardless of route), so the 21 rows added cover the game-start and
      game-end cards, the interactive `[Start]`/`[Select]` card and what each answer leads to, and
      the two refusals the global profile now raises.
      ⚠️ **One line needed a code change, not a row:** the restore card said
      `"Restored after " + gameName`. A card is translated line by line against a table keyed by
      the English string, so a line with a game's name glued on can never match — it would have
      stayed English in all thirteen languages and nothing would have said so. It is a FORMAT now
      (`Restored after {0}`, `GameStartCardContent.BodyArg`), filled in after the lookup.
- [ ] **P3.4b — the widget's OWN chrome is still entirely English.** Not a gap in the rows: there
      is **no `Loc` in the Game Bar widget at all** (`grep Loc.T XboxGamingBar` finds nothing).
      Every label, checkbox and flyout in it is a literal in XAML or code-behind. The two newest
      ones are the FPS-limit and RTSS-overlay refusals plus the RTSS restart caveat next to
      "Up to 180 FPS" (0.4.0.56).
      ⚠️ This is the bigger piece of work of the two and it is a MECHANISM, not a translation
      round: a UWP widget cannot make the registry read `OsdLoc` uses, so it needs the language
      handed to it over the pipe, and its XAML literals need to become bindings or be set from
      code. Decide that before adding rows — rows for strings nothing can look up are the shape
      P3.4 already rejected once.
- [ ] **P3.5 — test on the device** with Center set to a language Windows does not have.

---

## P3b — The Inno setup

**Same repository as P3**, file `ClawTweaksInstaller\ClawTweaksInstaller.iss`. Measured 2026-09-15,
nothing below is assumed:

| what | today |
|---|---|
| `[Languages]` | the five: `en de fr es ko`, all from Inno's own `.isl` files |
| `[Messages]` | **one** overridden key, `WelcomeLabel2`, in the five languages |
| `[CustomMessages]` | **54 keys x 5 languages**, read through `M('Key')` / `MF('Key', [...])` - **never** `CustomMessage()` directly, that returns `%n` unresolved |
| language choice | `ShowLanguageDialog=no`, `UsePreviousLanguage=no` - Inno picks by the Windows UI language, silently, and falls back to the first line (English). See the note in the `.iss` on why `UsePreviousLanguage` has to stay `no` |
| the two FSE wizard pages | wording is the owner's, near-verbatim, in every language - **do not** expand it into prose in any language |
| `Log()` lines | English, and stay English - a translated setup log cannot be grepped |

So the job is **55 keys x 8 new columns = 440 cells**, plus eight `[Languages]` lines. Small
next to P2, but it has three traps of its own.

*(Measured again on 2026-09-15 by the extractor: 54 `[CustomMessages]` keys plus
`WelcomeLabel2` in `[Messages]` is **55 in total**, not 56. The earlier note counted
`WelcomeLabel2` twice.)*

### Where Inno's own translations come from

Inno 6.7 installs per-user here (`%LOCALAPPDATA%\Programs\Inno Setup 6\Languages\`), and ships
official files for **five** of the eight new languages. The other three are on jrsoftware.org as
*unofficial* translations and are **not** in the install:

| language | `.isl` | where |
|---|---|---|
| `it` | `Italian.isl` | shipped |
| `pt-BR` | `BrazilianPortuguese.isl` | shipped (`Portuguese.isl` is pt-PT - not that one) |
| `pl` | `Polish.isl` | shipped |
| `ru` | `Russian.isl` | shipped |
| `ja` | `Japanese.isl` | shipped |
| `el` | `Greek.isl` | **unofficial** - download, vendor into `ClawTweaksInstaller\Languages\`, `MessagesFile: "Languages\Greek.isl"` (no `compiler:` prefix) |
| `zh-Hans` | `ChineseSimplified.isl` | **unofficial** - same |
| `zh-Hant` | `ChineseTraditional.isl` | **unofficial** - same |

The three vendored files are **repo content** and go through the normal commit; a build on another
machine must not depend on somebody having downloaded them. Check each one's `LanguageID` line:
that is what Inno matches the Windows UI language against, and it is the whole mechanism by which
zh-Hans (`$0804`) and zh-Hant (`$0404`) end up on different files.

### The decision to put to the owner FIRST: where the 56 keys live

Today the translations are **inline in the `.iss`** - a second source of truth next to
`strings.tsv`, in a different file format, in a different repo. Two ways forward:

- **(a) Keep them in the `.iss`.** Eight more blocks of 55 lines. Simple, no tooling, but the
  setup's strings are then the one place P1.6's lint does not reach (placeholder parity for
  `MF('Key', [a, b])` arguments, width, stray whitespace), and a key renamed on one side is found
  at compile time only for the languages that exist.
- **(b) Move them to `Tools/i18n/inno.tsv`** in the Center repo, next to `strings.tsv`, and let
  `loc_build.py` grow a second output: `ClawTweaksInstaller\Languages\CustomMessages.iss`,
  pulled in with `#include`. One generator, one lint, one width rule. The cost is a cross-repo
  build step (the generated file is committed in the helper repo, like `Localization.Tables.cs`
  is in Center) and the same "never edit the generated file" discipline.

**DECIDED 2026-09-15: (b)**, by the repo owner. Done - see P3b.1 below.

### The traps

1. **`%n` is Inno's newline and `%1` is Inno's placeholder** - not `\n` and not `{0}`. A lint
   written for `strings.tsv` has to know the difference, or it flags every line.
2. **Width has no automatic check here.** Inno wizard pages wrap text but **buttons do not grow**:
   the "Open Windows Settings" button already measures its own width from its caption
   (`CalculateButtonWidth`) because French is one and a half times English. Every other fixed
   `Width` on a custom control is a clipping case waiting for Russian or Greek.
3. **A missing key in one language is a compile error** in ISCC, which is the check - but only
   for languages listed in `[Languages]`. Add the `[Languages]` line **before** filling the column,
   so the compiler reports what is missing instead of the language silently falling back to English.
4. **Proving a language without switching Windows:** `ClawTweaks_<ver>_Setup.exe /LANG=el`. Korean
   is still unseen in the wizard font; CJK and Greek need one look each on the device.

- [x] **P3b.1 — decided (b)** (2026-09-15) and carried out in the same pass: `Tools/i18n/inno.tsv`
      holds the 55 keys x 13 languages, `loc_build.py` grew a second output
      (`ClawTweaksInstaller/Languages/CustomMessages.iss`, pulled in with `#include`), and
      `loc_lint.py --inno` is the second placeholder grammar. **The round trip was proven
      before the inline blocks were deleted**: the generated file carried exactly the 275
      (language, key, value) triples the `.iss` carried, none added, lost or changed.
      Two lint rules differ from the C# half and the first run proved why: edge whitespace
      can be the string's whole job (`PayloadAnd` is `" and"`), and `WelcomeLabel2` indents
      its bullets with several spaces in every language. Commits `24ef404` (Center),
      `174b2b80` (helper).
- [x] **P3b.2 — done 2026-09-15.** All three vendored under `ClawTweaksInstaller\Languages\`
      and all thirteen `[Languages]` lines are in; ISCC compiles clean.
      **Correction to what this plan assumed:** they are taken from **jrsoftware/issrc at tag
      `is-6_7_3`**, not from the website and not from `main`. On `main` all three have been
      promoted to the official `Files/Languages/` for Inno 7, and a newer `.isl` can carry
      message keys a 6.7 compiler does not know - so the tag has to match the installed
      compiler. `Languages/README.md` records source, tag, size, sha256 and the maintainers
      the files name. `.gitattributes` marks `*.isl -text`, or git's eol normalisation
      rewrites them and those checksums stop matching a fresh clone - which it had already
      done to `ChineseTraditional.isl` before it was caught.
      ⚠️ **Known gap:** `ChineseTraditional.isl` is a 6.1.0+ translation and leaves eight of
      Inno's own newer messages in English (`StopExtraction`, the seven `Verification*`).
      All eight are failure paths and none of them is ClawTweaks text. Left as it is on
      purpose; a newer upstream file is the fix.
      ⚠️ The order the plan assumed is reversed in practice: the keys were filled FIRST, so
      nothing fails on missing keys. What enforces the pairing instead is the generator - it
      writes only languages that `[Languages]` declares (`INNO_SHIPPED` in `loc_build.py`)
      and prints which translated columns it is holding back, because ISCC rejects a
      `CustomMessages` entry whose prefix is not declared.
- [x] **P3b.3 — the 55 keys x 8** (done 2026-09-15, 440 cells, `loc_lint.py --inno` 0 errors
      0 warnings across all thirteen). Slot parity holds everywhere, `%n` line breaks match the
      English shape so the FSE pages keep their layout, and `PayloadAnd` keeps its leading
      space in every language. Commits `ab5b4c9` (Center), `ceaf2392` (helper).
- [x] **P3b.4 — CLOSED 2026-09-15 by the repo owner** after four runs; the rest goes to test users. One run per language with `/LANG=xx`, the six wizard pages and the uninstall
      dialog. Fixed-width controls that clip get `CalculateButtonWidth` treatment, not a shorter
      translation. **Four of thirteen run and judged on 2026-09-15**, from
      `ClawTweaks_0.3.1.192_Setup.exe` (SHA256 `9107081...`):

      | `/LANG=` | seen | verdict |
      |---|---|---|
      | `ru` | title, FSE page, both buttons | clean - and **reviewed by a Russian speaker**, the only one of the four with a native check |
      | `zhHans` | title, FSE page | clean (this run was carried through to a real install - that is how the dev device got 0.3.1.192 / Center 0.2.73) |
      | `zhHant` | title | clean; proves the vendored `ChineseTraditional.isl` loads |
      | `el` | title, FSE page, both buttons | clean |

      Still unseen: `de`, `fr`, `es`, `ko`, `it`, `ptBR`, `ja`, `pl` — **handed to test users**,
      not pending here. Korean was unseen before this work too.

      Two things the runs settled that no lint could:
      * **Inno grows its own buttons.** Greek "&Επόμενο" got 96 px where Russian got 86. So
        the wizard chrome is not a clipping risk; only OUR fixed-width controls are, and the
        one that already measures itself (`CalculateButtonWidth`) is the Windows-settings
        button on the second FSE page - **still unseen, it sits past the install step**.
      * **An upstream typo, not ours:** the Greek Cancel button reads `Ακυρο`, missing the
        tonos of `Άκυρο`. It comes from the vendored `Greek.isl`. Left alone for the same
        reason as the eight English sentences in zh-Hant: it is someone else's text with a
        named maintainer. A `[Messages]` override on our side is the fix if a tester minds.

      ⛔ **Do not try to check widths by reading the wizard's controls from a script.** It was
      tried and the numbers were wrong twice over: `WM_GETFONT` to an elevated window is
      blocked by UIPI, so the text gets measured in the system font instead of the wizard's,
      and `EnumChildWindows` also returns the hidden controls of every other page, whose
      sizes have not been laid out yet. It reported two "clipping" cases that were neither.
      Reading the control TEXT that way is fine and useful - it is how the table above was
      filled - but the only verdict on width is a pair of eyes on the screen.

---

## P4 — Close out

- [ ] Attach a coverage report to the final commit, so the next reader sees what was left English.
- [ ] **Decide `Loc.Order` / `Loc.Next`.** Dead since the settings screen grew its own
      `LanguageOrder()`. Kept and marked; deleting them is the owner's call.
- [ ] Update the memory note `clawtweaks-i18n-thirteen-languages` and this file's boxes.
- [ ] Update `memory/inno-setup-speaks-centers-five-languages.md` in the helper repo - its title
      is wrong once P3b lands.

---

## Keeping progress — read this before starting P1.3

A batch-of-60 pass over 494 candidates is the one part of this job that **cannot** be reconstructed
from the repository afterwards. A string that was read and correctly rejected looks exactly like a
string nobody has read yet, so an interrupted pass silently starts over.

So: **P1.3 writes its verdict for every candidate into `Tools/i18n/triage.tsv`** — one row per
candidate, columns `file`, `line`, `english`, `verdict`, `note`, where verdict is one of
`ui` (row added) · `not-ui` · `proper-noun` · `log` · `dead` · `interpolated`. Commit it with each
batch. `loc_coverage.py` should then subtract what is already triaged, so the number it reports
falls as the work proceeds. Without that file the count stays at 494 forever and nobody can tell
what has been looked at.

Everything else survives on its own: the tables are in the TSV, the code is in git, the decisions
are at the top of this file, and the boxes above say where the work stopped.

---

## Known traps

- Some `.cs` files are **Windows-1252**, the rest UTF-8. The tools handle both. A key arriving with
  `\ufffd` in it can never match at runtime, so treat one appearing in a report as a tool bug, not a
  string to add. (A `?` in a *console* line is usually just the terminal's code page — check the
  file, not the terminal.)
- `Localization.Tables.cs` is **LF**, not CRLF, and starts with a BOM. The generator matches that;
  do not "fix" it.
- `UiLanguage` is persisted **by name**, not by ordinal (`CenterSettings.Language` round-trips
  `value.ToString()` through `Enum.TryParse`), so inserting a member in the middle is safe.
  **Renaming one is not:** an unparseable name falls back to `System`, so a user's pinned language
  quietly becomes "follow the OS". An earlier comment in `Localization.cs` claimed the opposite —
  if you find that wording anywhere else, it is wrong.
- `Loc.F` never throws: a mistyped placeholder falls back to the English format. That means a broken
  translation shows as a slightly wrong screen and **not** as an error — the lint in P1.6 is the only
  thing that catches it.
