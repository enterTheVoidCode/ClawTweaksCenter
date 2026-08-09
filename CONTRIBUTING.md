# Contributing to ClawTweaks Center

Contributions are welcome. Please read this first — one section of it is a hard boundary rather than
a preference.

## The boundary: this repo is Center, and only Center

ClawTweaks is split across repositories:

- **ClawTweaks Center** (this repo, public) — the installer and control panel.
- **The ClawTweaks widget and background helper** — separate and **private**. That is the part that
  actually drives TDP, fan curves, LED, and the controller.

**Do not bring helper or widget code into this repository**, in any form: not copied, not ported, not
paraphrased, not as a submodule or project reference. Pull requests that do will be closed. This is
not about credit — that code is not published, and publishing it through a side door is not something
a maintainer can undo.

Center communicates with the helper over a named pipe. Everything needed for that already exists in
`ClawTweaksCenter/Shared/`. That is the contract, and it is the only overlap between the two sides.

**Never commit secrets** — API keys, tokens, signing certificates (`*.pfx`), credentials — or anything
taken from a user's machine, such as logs, diagnostics bundles or device identifiers. A secret that
lands in the history has to be rotated; removing the commit is not enough.

## Before you open a pull request

1. **It has to build clean.** `dotnet publish -c Release` (needs the .NET 10 SDK, Windows x64). No new
   warnings.
2. **It has to be usable with a controller.** Center runs on a handheld. Every control you add must
   be reachable with the D-pad and confirmable with A. A feature that only works with a mouse is not
   finished.
3. **Try it against a real device if you can.** Center's whole job is touching a real installation.
   If you cannot test on an MSI Claw, say so in the PR — that is useful information, not a problem.

## Things that will be asked about in review

**`ClawTweaksCenter/Shared/Function.cs` is serialized by ordinal.** Appending is fine; inserting or
reordering is not. It silently repoints every value after the change, with no compiler error and no
exception — Center just starts reading the wrong property. If you need a new pipe property, open an
issue: that change starts on the helper side.

**Center never asks for administrator rights.** It installs per-user and hands off the three things
that genuinely need elevation (drivers to their vendors' installers, the certificate to the Windows
import wizard, the scheduled task to the signed helper). Please do not add a self-relaunch as
administrator.

**Center does not download and execute binaries.** It links to the release page and lets the user
install. This is a deliberate response to real antivirus findings, not caution for its own sake.
Installing the app package via `Add-AppxPackage` is a different thing and is fine.

## Style

- Commit messages and code comments in **English**; UI strings stay as they are.
- Explain **why** in the commit message. The diff already shows what changed. If a measurement drove
  the change — a log line, a crash, a size — put the number in.
- Stage explicit paths (`git add <path>`), not `git add -A`.
- Match the surrounding code. This codebase comments the non-obvious reasoning rather than restating
  the line below; please keep that habit.

## Reporting a bug

Include your device model, the Center version (shown in the app), and what you expected versus what
happened. Center writes an install log — attaching it helps a lot. **Please skim it first and remove
anything you would rather not publish**; it can contain paths and device identifiers.
