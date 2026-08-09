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
| **ClawTweaks widget** (Game Bar UI) | separate, **private** |
| **ClawTweaks background helper** (the service that drives TDP, fan, LED, controller) | separate, **private** |
| App package releases, install manifest | the main `ClawTweaks` repo |

**The widget and the background helper are private and stay private.** Do not add their code here,
do not port pieces of it here, do not paraphrase it here, and do not add a project reference or
submodule pointing at them. A pull request that brings helper or widget internals into this
repository will not be merged.

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

## Commits

- **Commit messages and code comments in English.** UI strings are exempt.
- **No AI attribution.** No `Co-Authored-By` for an assistant, no tool signature line.
- **Stage explicit paths.** `git add <path>`, never `git add -A` or `git add .`.
- **Never `--force`.**
- Explain *why* in the message, not just *what* — the diff already says what changed. If a change is
  driven by a measurement (a log, a crash, a build size), put the number in the message.

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
