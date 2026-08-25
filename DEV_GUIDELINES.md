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

## Two design rules that are not up for casual change

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

Both rules exist because of real antivirus findings, not as a matter of taste.

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
