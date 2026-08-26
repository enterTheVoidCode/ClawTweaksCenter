# ClawTweaks Center — development guidelines

**Read this before changing anything.** It is written for whoever does the work — a human developer
or an AI coding agent — and it applies to both equally. If you are an agent operating on this
repository, treat this file as your instructions for it.

[CONTRIBUTING.md](CONTRIBUTING.md) covers the practical side of submitting a change. This file covers
what the code expects of you.

## ⛔ What belongs in this repository — and what never does

This repository is **public**. Everything committed here is world-readable, permanently: deleting a
commit does not remove it from forks, clones, caches or GitHub's event stream.

ClawTweaks is split across repositories on purpose:

| Component | Where it lives |
|---|---|
| **ClawTweaks Center** — installer and control panel (this repo) | public, here |
| **ClawTweaks widget** (Game Bar UI) | public, in the main [`ClawTweaks`](https://github.com/enterTheVoidCode/ClawTweaks) repo |
| **ClawTweaks background helper** (the service that drives TDP, fan, LED, controller) | separate, **private** |
| App package releases, install manifest | the main `ClawTweaks` repo |

**The background helper is private and stays private.** Do not add its code here, do not port pieces
of it here, do not paraphrase it here, and do not add a project reference or submodule pointing at
it. A pull request that brings helper internals into this repository will not be merged.

The widget is not restricted — it is public and open to contributions, it just lives in the main
repository rather than this one. If your change belongs there, send it there.

Center talks to the helper over a named pipe. Everything Center needs for that is already in
`ClawTweaksCenter/Shared/` — that is the **contract**, not the implementation, and it is the only
thing shared between the two sides.

**Never commit:** signing certificates (`*.pfx`), API keys, tokens, credentials, or anything from a
user's machine (logs, diagnostics dumps, device identifiers). Not even temporarily — a secret that
reaches the history has to be rotated, not deleted.

## `ClawTweaksCenter/Shared/` is mirrored, not owned

Those five files are copies of sources that live in the private helper repository. Read
[`ClawTweaksCenter/Shared/README.md`](ClawTweaksCenter/Shared/README.md) before editing any of them.

The short version: **`Function.cs` is serialized by ordinal.** The helper writes `(int)Function.X`
and Center reads the number back. Inserting or reordering an entry silently repoints every value
after it — nothing fails to compile, nothing throws, Center simply reads and writes the wrong
property. **Only ever append.**

If you need a new pipe property, that change starts on the helper side. Open an issue rather than
guessing an ordinal.

## Build

```
dotnet publish -c Release
```

Requires the .NET 10 SDK on Windows x64. That is the entire build — everything that makes the output
portable is set in the project file, so a plain publish cannot produce something that only runs on
the machine it was built on.

**Only the exe under `publish/` is shippable.** A plain `dotnet build` leaves a same-named ~300 KB
apphost one directory up that needs its sibling DLLs next to it. It runs fine from that folder, which
is exactly what makes it dangerous: Center installs itself by copying a single file, and a copy of
the apphost dies before `Main`. `SelfInstaller.IsSelfContainedSingleFile` refuses to install one, but
do not hand one out either.

## Three design rules that are not up for casual change

**Center never asks for administrator rights.** Not rarely — never. It installs per-user, and the
three things that genuinely need elevation are handed off instead of performed: drivers go to their
vendors' own installers, the certificate goes to the Windows import wizard, and the scheduled task is
registered by the signed ClawTweaks helper itself. Do not add a `Verb = "runas"`, a self-relaunch, or
an elevated child process "just in case".

**Center does not download and execute binaries.** It reports that an update exists and links to the
release page; the user downloads and installs it. Fetching an executable and starting it is the shape
of a dropper, and verifying its bytes afterwards does not change that shape. Installing the
ClawTweaks **app package** is a different thing and stays — that is `Add-AppxPackage`, not starting a
binary.

The first two exist because of real antivirus findings, not as a matter of taste.

**Grey it out, do not hide it** (user decision, 2026-08-26). When a control cannot be used on this
machine, it stays on screen, dimmed, saying why — it does not disappear. The cover-art row in the
game menu had always worked this way: it stays put and reads "Set a SteamGridDB key in Settings
first" instead of vanishing. Everything else now follows it.

The argument that used to win the other way is written in the old comments and is worth knowing,
because it is not stupid: a tab that can only ever be empty is a dead end, and "ROMs 0" invites a
hunt for a bug that is really "you have no Playnite". That holds for a bare zero. It stops holding
once the thing is dimmed AND its empty state names the reason — at which point hiding is strictly
worse, because an absent control cannot tell the user anything, and a user who never learns the
category exists cannot go and enable it.

Two consequences that are easy to get wrong:

- **A dimmed control must still be reachable.** Hiding it from D-pad navigation gets you a control
  that can be seen and not focused, which on a handheld is the trap this project has already paid for
  more than once. The library's shoulder cycle therefore visits the dimmed tabs too.
- **The reason has to be somewhere the user can actually reach.** A tooltip is not a reason on a
  device with no mouse. Put it in the row's own subtitle, or in the empty state behind it.



## Translations

Center ships English, German, French, Korean and Spanish. On a fresh installation it follows the
Windows **display** language (`CultureInfo.CurrentUICulture`, not `CurrentCulture` — that one follows
the region and would put a German keyboard on an English Windows into German). The user can pin a
language on **Home → Center Settings**; the choice is stored in `HKCU\Software\ClawTweaks\Center`
under `Language`, **by name, not by ordinal**, so inserting a language later cannot silently turn
somebody's German into French.

**The tables are keyed by the English string** (`Core/Localization.Tables.cs`). A string that is not
in a table renders as itself, so:

- adding a label to the interface needs no work here at all, and
- **leaving a string untranslated is a decision, not a bug.**

**Translation happens at the builders, not at the call sites.** One lookup in each of these covers
every screen that goes through them, so **new text needs no work here at all** as long as it goes
through one of them:

`UiHelpers.Title` · `Caption` · `Body` · `StatusRow` · `ActionCallout` · `ToolRow` · `ModeBanner`
· `ActionBarBuilder.BuildChip` · `BuildHomeTile` · `BuildTab` · `BuildSettingRow` ·
`BuildCenterSettingRow` · `BuildLibraryMessage` · `ExitPromptRow` · `InfoLead` / `InfoHeading` /
`InfoLine` · the maintenance, misc and game-menu row builders · the onboarding step card.

Do not sprinkle `Loc.T` through new code. If a new string does not reach the screen through one of
those, that is the thing to fix.

**Two shapes CANNOT be translated by this mechanism, and neither is an oversight:**

- **Interpolated strings.** `$"Last checked {time}"` is built at runtime, so it can never match a
  key. Where such a line matters, split it: translate the fixed part and concatenate the value, the
  way `UpdateSelectedTitle` does with "Last played".
- **Date and number formats.** `"d MMM yyyy"` reaches a builder like any other string; translating
  one would corrupt the output. They are simply absent from the tables, and formatting is left to
  `CultureInfo.CurrentCulture`, which already reads correctly for the user.

### The two rules a new translation has to pass

1. **It has to FIT.** Center's chips, tabs and tiles are sized for the English word and do not grow.
   The budget is at most **1.7× the English, or five characters more, whichever is larger**, counting
   CJK characters as two because they render about twice as wide. A translation over budget is left
   out and the English stays — the alternative is a clipped label, which is worse than an English one.
   The entries that failed this check are listed at the **bottom of `Localization.Tables.cs`**; that
   list is the answer to "why is this one word still English", so keep it up to date rather than
   tidying it away.
2. **Menu headings stay English.** The Home tiles keep their English titles and only their one-line
   descriptions are translated. "Library" is the deliberate exception and is translated everywhere it
   appears.

Adding a language means: a member on `UiLanguage`, a case in `Loc.Detect`, a name in `Loc.NameOf`
(**in that language** — somebody who has landed in a script they cannot read has to find their way
out), an entry in `Loc.Order`, a table, and a case in `TableFor`.

## Commits

- **Commit messages and code comments in English.** UI strings are exempt.
- **Stage explicit paths.** `git add <path>`, never `git add -A` or `git add .`.
- **Never `--force`** on a shared branch.
- Explain *why* in the message, not just *what* — the diff already says what changed. If a change is
  driven by a measurement (a log, a crash, a build size), put the number in the message.

## Trying something big

The rules above describe a change that is ready to merge. They are not a hurdle you have to clear
before you are allowed to experiment.

Exploratory work — a framework migration, a rewrite of a screen, a spike to find out whether an
approach is even viable — is welcome and does not have to arrive finished. Open a draft PR or an
issue early and say what you are trying to establish. A branch that does not build yet, has warnings,
or replaces something wholesale is a perfectly good conversation starter; nobody will hold it to the
merge bar while it is still a question.

Two things stay true even in a spike, because they are not style preferences: **no helper code in
this repository**, and **no secrets in a commit**. Everything else is negotiable if you can say why.

If you are weighing a larger change, the parts most likely to constrain you are the single-file
self-contained publish (see the Build section) and the D-pad requirement below — Center runs on a
handheld and is used with a controller far more often than with a mouse. Neither rules an approach
out, but a proposal that has not accounted for them will get asked about both.

## The library does not need ClawTweaks

`LibraryAvailable` is gone (2026-08-26). It was `_installedVersionChecked && _installedVersion !=
null`, and it hid the library tab, both Home tiles and the entire tab strip until a PowerShell
version check had answered.

The premise was that the library is a ClawTweaks feature. It is not: it scans Steam, Epic, Xbox, the
four other launchers, Playnite and your own apps, and launches them — none of which involves
ClawTweaks. The only two parts that do are the profile badge and the play history, and both read
files that are simply absent without it, which they already handled.

It was **deleted rather than pinned to `true`**, because a property that is always true is an
invitation to put the condition back. The same change let `HomeCenterSettingsIndex`, `HomeFaqIndex`
and `HomeLeaveIndex` go back to being constants: every tile is now always drawn, so the grid is
always eight cells, which is what keeps Home's row navigation as plain division.

The startup jump no longer waits on the version check either. That check still runs — it drives the
header chip, the update banner, Browse's tags and the uninstall screen's gating — it just no longer
decides whether there is a library to open.

## The FAQ, and the two rules its entries have to keep

`CenterMenuWindow.Faq.cs`. Eight questions, collapsed until pressed, one statement per line. The
questions are the index — that is the whole reason they start closed, and why adding a ninth is
cheap while turning any one answer into a paragraph is not.

**Only what the code actually does.** Every answer is checkable against this repo or the helper: the
virtual controller really does roll itself back when no pad mounts, the scheduled task really carries
no version number so updates cost no prompt, Center really never elevates. A FAQ that drifts from the
software is worse than no FAQ, because it is believed and it is not read alongside the code that
would contradict it.

**Say where to go, not how it works.** These answer "what do I do". The reasoning lives here and in
the private repo's `CLAUDE.md`, not on a 7-inch screen.

The entry list is a plain array of `(question, answer lines)` and the answers are ordinary strings, so
they go through `Loc.T` like everything else — a new entry needs a translation round, not a code
change.

### Home's row navigation is arithmetic now, and that was the point

The grid is three columns with no gaps, so a row is `index / 3` and moving a row is ±3. It used to be
a hand-written ladder of index ranges that had to be edited every time a tile was added — and the
last time one was, Down stopped halfway down the grid because the ladder had not been. Arithmetic
cannot fall out of step with the tile list; a ladder of literals can, and did.

That only holds while the grid stays gap-free, which is why `HomeFaqIndex` and `HomeLeaveIndex` are
properties keyed off `LibraryAvailable` rather than constants: without ClawTweaks the two library
tiles are absent, and fixed numbers would leave dead cursor positions where they used to be.

## Uninstalling: the order is the feature

`CenterMenuWindow.Leave.cs` + `Core/LeaveRunner.cs`. Reached from the Home tile and from Windows
Settings → Apps, because `--uninstall` now opens this screen instead of deleting Center on the spot.

**Three of the things ClawTweaks changes are hardware state**: the battery charge limit, the fan
curve in the EC, and which controller the device presents. Removing an app does not undo any of
them. Someone who deletes ClawTweaks first is left with a charge limit they can no longer see, on a
device whose fan follows a curve nothing owns any more — and no software on the machine that could
put either back. That, and nothing about presentation, is why leaving is a list rather than a
button:

```
0 Restore the device     needs the helper   ← only ClawTweaks can undo the hardware state
1 Turn MSI Center M on   needs the helper   ← after step 2 there is no pipe left to ask
2 Uninstall ClawTweaks                      ← the helper watches for its own package
                                              disappearing and uses that to remove its
                                              scheduled task and deployed copy, then exits
3 Uninstall Center                          ← last: it ends this process
```

Inside step 0 the same logic runs in miniature: **the full reset goes first, the three hardware
writes after it.** The reset wipes the helper's settings store, so writing "charge limit off" before
it would persist a value the reset then erases, and the next helper start would re-apply whatever had
been stored before. Wipe the settings, then put the hardware back.

### Two rules that must survive any rework

**Step 3 is never gated.** Steps 0–2 need ClawTweaks installed and the helper answering; step 3 needs
nothing. Someone who already removed ClawTweaks is warned, told that reinstalling it is how the
device gets restored, pointed at Update & Release — and then allowed to uninstall Center anyway. A
wizard that cannot be finished is worse than one that finishes badly, and this one is reached from
Windows Settings, where refusing to proceed means an app that cannot be uninstalled at all.

**`--uninstall` must always end in an uninstall being possible.** The branch in `App.OnStartup` wraps
the window in a try/catch and falls back to the old direct removal, and `--uninstall-silent` still
does exactly what `--uninstall` used to do. Windows started that process to remove something; a
screen that failed to draw must not be the reason nothing happened.

### The Center M step checks, and says when the answer is half

`LeaveRunner.ReenableCenterMAsync` turns Center M back on and then asks
`CenterM.IsGameBarWidgetInstalled()` whether MSI's Game Bar widget actually came back. It routinely
has not: disabling Center M removes that package with `-AllUsers`, and that takes the staged copy
Windows would re-register from with it (measured on the dev machine 2026-08-26, with a control cell —
the detail is in the private repo's `CLAUDE.md`). The step then reports a warning naming the one fix
that works, reinstalling MSI Center M, rather than a clean success.

⚠️ **That check hardcodes `9426MICRO-STARINTERNATION.MSIQuickSettings`, and so does the helper.** Two
repos, no shared compiler. Rename it on one side and nothing breaks — Center simply says "the widget
did not come back" for ever, about a widget that is right there.

## 🟡 Open: the Steam download readout has never been watched live

The Not Installed tab shows a percentage for a Steam download in progress. **That number is derived,
not measured**, and it is the only claim in the library work that is.

What IS measured: a Steam manifest that is not `FullyInstalled` is kept and marked not-installed, and
a *pending* download carries real figures — Helldivers 2 sat at `StateFlags=6` with
`BytesToDownload=84,396,352` and `BytesDownloaded=0` while being perfectly playable, which is what
proved the update case and the install case have to be told apart.

What is NOT measured: whether `BytesDownloaded` actually grows while Steam is fetching a game, which
`StateFlags` value stands during it, and whether `steamapps\downloading\<appid>` appears and
disappears at the two ends. Every one of those is an assumption from the field names.

**How to close it** — the same before/after that settled the Battle.net question, and it takes one
real download:

1. Start any Steam install. While it runs, record for that appid: the whole `appmanifest_<id>.acf`,
   and whether `steamapps\downloading\<id>` exists.
2. Let it finish. Record both again.
3. The diff is the answer. If `BytesDownloaded` moved, the percentage is honest; if it did not, the
   tab is showing 0% for the whole download and the figure has to come from the folder size instead.

Until that is done, treat a bug report about the percentage as likely real. The rest of the tab — the
list, the covers, the install hand-over — is verified: 839 owned-but-not-installed games with 831
covers, and zero of them leaking into any other tab.

Battle.net has the same question already answered, and answered differently: there is no percentage
there at all, only "finished or not", because that is all `.patch.result` says. See
`Library/OtherStores.cs`.

## Layout

| Path | What lives there |
|---|---|
| `ClawTweaksCenter/Phases/` | The install flow, one class per step |
| `ClawTweaksCenter/Core/` | No-UI logic: detection, downloads, package install, helper control, pipe client |
| `ClawTweaksCenter/Core/Sources/` | Where installable builds come from |
| `ClawTweaksCenter/Navigation/` | Gamepad navigation — Center must stay usable with the Claw's own controller |
| `ClawTweaksCenter/Ui/` | Window chrome, action bar, shared helpers |
| `ClawTweaksCenter/Shared/` | Mirrored contract files — see the warning above |

Center is used on a handheld. **Anything you add has to be reachable with the D-pad and the A
button**, not just with a mouse.
