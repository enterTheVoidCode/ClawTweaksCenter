# Library - to-do list

Open work on the Center library. Each item says what was found, where, and what is decided. Nothing
here is built unless it says so.

---

## 1. Own apps (My Apps): admin tools fail to start, silently

**Found 2026-09-21, not built (user: "wir machen deine Empfehlung, aber noch nicht jetzt").**

`MiscSource.Launch` starts an entry with a path through `Process.Start` with
`UseShellExecute = false`, i.e. a bare `CreateProcess`. An exe whose manifest asks for admin rights
(and any exe Windows' installer heuristics flag - "setup", "install", "update" in the name) makes
`CreateProcess` fail with **Win32 error 740, "the requested operation requires elevation"** - and
with no UAC prompt. The empty `catch { }` swallows it; path entries have no `LaunchUri` to fall back
to, so the launch fails: the screen says it failed and the log has no line at all.

**Planned:**
- On error 740, retry the same start with `UseShellExecute = true`. Windows then shows its own UAC
  prompt for the target. Center itself stays unelevated - the prompt belongs to the program, not to
  us, so "Center never asks for admin" holds.
- Log every failed start with its Win32 code and the path. "Individual launch problems" from users
  are not diagnosable today.
- Not planned: anything specific to cracked games (loader detection, bypasses). The UAC retry does
  the same as a double-click in Explorer on something the user picked; it does not favour them.

**To check alongside:** whether `GameRunTracker` can wait on a process that was elevated through
UAC (`WaitForExitAsync` from an unelevated Center).

## 2. Own apps: launcher stubs end the "game" at once (suspected)

**Derived from the code, NOT seen on a device.**

`GameRunTracker.Track` waits on the exact process we started and reads its exit as the end of the
game. Many tools and games start through a small launcher that starts the real program and exits
straight away. Center would then take the game as ended and come back while it is still running.

If a user report sounds like "Center pops back up right after the start", it is this case. A fix
would watch for a child process or the window, not the stub.

## 3. Own apps: .lnk files and start parameters

**Recommended 2026-09-21, not built.**

What exists: `MiscEntry.Args` is stored and passed on launch. It is filled only from Desktop and
Startup shortcuts (`AppInventory`, target + arguments via `WScript.Shell`). The file picker takes
`*.exe` only and never sets parameters; nothing in the library can edit them. The working directory
is always forced to the exe's folder.

**Recommendation (a mix of two routes):**
- **The file picker also takes `*.lnk`, and the shortcut itself is stored as the launch target**,
  started through the shell. Everything in it then applies 1:1 - parameters, working directory,
  "Run as administrator", compatibility mode - and what the user tested with a double-click is
  exactly what Center runs. Parameters are edited in the shortcut, not in Center.
- **Entries with an exe path get an editable start-parameter field** in the library.
- Resolving the shortcut into exe + args instead would lose "Run as administrator" (not exposed by
  `WScript.Shell`) and needs a new working-directory field.

**Where the field goes:** the game menu has no vertical room left. The Rename screen for own apps
already exists; a second text box "Start parameters" under the name is the natural place. Both
apply to own apps only.

## 4. EA app: installed games are not found (EA SPORTS FC 27)

**Found 2026-09-21 on the first EA-app game tested, not built.**

`EaSource` reads `HKLM\SOFTWARE\Origin Games\<id>` and needs a folder for each id. It looks for one
in the key (`InstallDir` / `Install Dir`) and then in the uninstall entries, matched by the key name
or by an `offerIds=` in the uninstall command. For FC 27 all three miss:

| where | what is there |
|---|---|
| `Origin Games\16425884` | `DisplayName`, `Locale` - **no folder** |
| `Uninstall\{B736B172-...}` | the folder, but the key is a **GUID**, and the command (`Cleanup.exe uninstall_game`) names no id |
| `SOFTWARE\EA Sports\EA SPORTS FC 27` | `Install Dir` + `Product GUID = {B736B172-...}` - the publisher key, which we never read |
| `<folder>\__Installer\installerdata.xml` | `<contentID>16425884</contentID>` - the link back to the id |

So the entry is dropped as "no folder", exactly like the uninstalled leftovers next to it (FC 24,
25, 26, WRC, F1 24, Battlefield 6 all sit in `Origin Games` without a folder on this machine - those
are correct to drop).

**Fix:** for every uninstall entry whose `InstallLocation` holds `__Installer\installerdata.xml`,
read its `<contentID>` values and map each id to that folder. This is the file the EA app itself
writes into every game it installs, it names the id directly, and it still requires the folder to
exist - the leftovers stay out.

**Unverified:** whether `origin2://game/launch?offerIds=16425884` starts the game. The id here is
a numeric content id, not an `Origin.OFR...` offer id. Test the launch before calling it done.
