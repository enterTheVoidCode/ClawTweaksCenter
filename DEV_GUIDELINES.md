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

### Getting a build onto a machine — ask how Center is installed there first

`dotnet publish` leaves a portable `CTW_Center_<ver>_Setup.exe`. **Whether that file is the right
way in depends entirely on how Center already lives on the target**, and getting it wrong does not
fail — it succeeds twice.

| Already installed as | Install a new build with |
|---|---|
| classic (per-user, `%LOCALAPPDATA%\Programs\ClawTweaks Center`) | `CTW_Center_<ver>_Setup.exe --resume-install` |
| **Velopack** (`%LOCALAPPDATA%\ClawTweaksCenter`, ARP entry owned by `Update.exe`) | `Build-Velopack.ps1`, then `ClawTweaksCenter-win-Setup.exe --silent` |

🔴 **THE CLASSIC ROUTE ON A VELOPACK MACHINE GIVES YOU TWO CENTERS.** It installs into
`Programs\ClawTweaks Center` and writes its own HKCU uninstall entry beside the one `Update.exe`
owns. Nothing errors; from then on which Center the helper starts is decided by whichever uninstall
key wins, and that is the exact state the 2026-09-07 migration existed to clear up.

**How to tell them apart in one command** — the uninstall string names the owner:

```powershell
Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    if ($p.DisplayName -like '*ClawTweaks*') { '{0} | {1} | {2}' -f $p.DisplayName, $p.DisplayVersion, $p.UninstallString }
}
```

`Update.exe --uninstall` means Velopack. A quoted `CTW_Center.exe --uninstall` means classic.

⚠️ **`--resume-install` is not a convenience switch.** A double-clicked exe has landed on a screen
pointing at the releases page since 2026-09-08; that argument is the gate that turns the same file
back into an installer. See "Center does not install itself on a double-click any more" below.

⚠️ **The Velopack setup does not start Center, and it wants the running one gone.** Close it
(`CloseMainWindow`, then force if it is still there), install, then start
`%LOCALAPPDATA%\ClawTweaksCenter\CTW_Center.exe` — the stub, which is what the shortcuts and AnyFSE
point at. Starting it from an ELEVATED shell hands Center an elevated token by inheritance and
breaks its single-instance pipe; see [[center-started-elevated-by-helper]].

⚠️ **`Build-Velopack.ps1` builds a delta against whatever is already in the feed folder**, so the
folder is reused on purpose and a cleaned one silently produces full-only releases. It also refuses a
version already in the feed unless `-Replace` is given — which is why the version bump is not
optional here either.

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

## Your own pictures: covers and the window background (2026-09-09)

The user names **one folder** and everything under it becomes a grid of tiles — a cover for a game,
or the background of the whole window. `Library/UserImageLibrary.cs` finds the files,
`CenterMenuWindow.UserArt.cs` is the two screens.

**A folder, not a file dialog, and that is the whole design.** Center is driven with a gamepad,
usually as the full screen experience: the Windows file dialog is a mouse surface. Misc's "browse
for an exe" gets away with it because a portable tool in no list has no other route; choosing art is
something people do again and again, and a mouse-only step in that loop is a step that does not get
taken. The folder is named **once** — from Library settings, or the first time somebody reaches for
their own cover — and after that it is the same D-pad grid the SteamGridDB picker already uses.
Downloads, Desktop and Pictures are offered as suggestions; **Browse… is last on that screen on
purpose**, as the answer for the folder nothing can guess.

**Sub-folders are included**, six levels deep, newest file first, capped at 400. Newest first is not
cosmetic: the reason to open this right after saving a cover in a browser is the file you just
saved.

### Four things that will break if they are changed without knowing why

1. **The picker is two more `GameMenuOverlay` states, not a new top-level overlay.** The library
   routes rendering, D-pad movement, the action bar and B through `GameMenuOverlayOpen` at eight
   separate call sites. A ninth kind of overlay has to be added to every one of them, and the one
   that gets missed is a screen the pad walks straight past.
2. **Opening it from Library settings PARKS `_settingsOpen`.** The settings screen wins those same
   routing checks (`MoveSelection` tests it first), so leaving it set gives the picker a screen it
   cannot steer. `UserArtBack` puts it back.
3. **Picked pictures are COPIED into the art cache** (`custom_*`, `background_*`), never referenced
   where they were found. What somebody picked out of Downloads is a file they will delete, and a
   cover that vanishes months later reads as Center losing it.
4. **A cover pick goes into `ArtOverrideStore`** — the same index the SteamGridDB picker writes. A
   second store would be a second opinion about which picture a tile should draw. `Reset cover` in
   the game menu clears it and then calls `ResolveLocalArt` **and** `StartArtFetch`, because clearing
   alone leaves a coloured plate until the next full rescan.

⚠️ **The old background file IS deleted when a new one is chosen, and a cover override's file is
NOT.** Exactly one setting points at the background; several games can share one picked picture.

### The background: cropped, and dimmed by a fixed amount

Both decided by the user on 2026-09-09.

- **`UniformToFill` — crop, never stretch.** Stretching fits every picture exactly and distorts
  every one that is not 16:10, and a distorted face does not read as "my picture does not fit", it
  reads as Center rendering it wrong.
- **A fixed scrim at 0.65, not a slider.** Every label and chip in Center is drawn for a dark flat
  background; a bright screenshot behind the footer makes the button hints unreadable. One value
  that always works beats a setting that lets someone make the app illegible and not know why.

The `Image` and the scrim are the **first two children** of the shell grid with `Grid.RowSpan="4"` —
WPF paints in document order, so that puts them behind everything without a single `ZIndex`. Both
stay `Collapsed` until there is a picture: a scrim over nothing would dim the window for no reason.
`ApplyBackgroundImage` runs in the constructor, before the first render, because painting it later
is a visible flash of the flat colour. It decodes at a fixed 1920 — `ActualWidth` is still 0 that
early, and a background decoded to nothing never appears.

## The footer carries two things that are not buttons (2026-09-09)

Battery on the left, clock on the right, chips in the middle — `CenterMenuWindow.FooterStatus.cs`.

**The battery comes from the HELPER, on request.** It already reads it, already resolves the runtime
Windows-first (the source MSI's own OSD uses, and the reason it works on a Claw 8 EX where the
battery exposes no rate sensor), and already publishes it as the QuickMetrics bundle the widget
draws. A second reader in Center would be a second answer to the same question.

⚠️ **A request, not the push next door.** `PushQuickMetrics` rides a 1 Hz timer that only runs while
the WIDGET's Quick Metrics row is switched on — a footer riding that stream would go blank because
of a setting in another program. The helper answers the Extra key **`GetPowerStatus`** with the same
JSON, built by the same method. It replies on `Function.QuickMetrics` rather than a new Function
value: same payload, and `Function` is ordinal, so not adding a member is one fewer thing to keep in
step across the two repositories.

**Every ten seconds, and that is a ceiling** (user, 2026-09-09). A charge percentage moves a few
times an hour, and the round trip costs a sensor read on the very battery being measured. The clock
rides the same timer — it shows h:mm, so being up to ten seconds late across a minute is invisible,
and a second timer to be exactly on time is not worth having. One request in flight at a time.

⚠️ **A missed answer leaves the last reading up.** The helper restarts on every ClawTweaks update;
blanking on that would make a working footer flicker. It clears only when the pipe is really down.

### 🔴 The pipe client starts DISCONNECTED — every user of it connects for itself

**Measured 2026-09-09, and it is the reason the battery was blank.** `_helperPipe` is one shared
`HelperPipeClient`, and it is **not** connected when Center starts. Every other caller opens it when
it needs it: the power actions, the tray column, onboarding, leave, maintenance — each calls
`ConnectAsync` first. The footer only tested `IsConnected`, so it drew a battery exactly when some
other screen happened to have the pipe open (right after an install, for instance) and nothing at
all the rest of the time.

**The helper was answering the whole time.** Probed over the free Quick Settings pipe while the
footer showed nothing:

```
{"batteryLevel":87,"timeRemaining":19502,"isCharging":false, …}
```

So: `RequestPowerStatusAsync` now connects when needed. **One attempt per 30 s while disconnected**,
not one per tick — a connect costs up to 4 s of liveness verification, and the client re-establishes
itself after a drop on its own (`_keepConnected`), so the only case that needs retrying here is a
machine with no helper at all.

⚠️ **`IsConnected == false` is not "no helper".** It is the default state of this object. Anything
new that reads from the helper has to connect, or it will work only by coincidence — and the
coincidence is another screen having been open, which is exactly the kind of bug that reads as
"sometimes it shows, sometimes it doesn't".

### 🟡 The helper's metrics JSON is not valid JSON on a German system — NOT fixed

Same probe, same line: `"batteryDrain":9,4` — a decimal comma, from `{value:F1}` formatted with the
current culture. It affects the three `F1` fields (`batteryDrain`, `cpuWattage`, `gpuWattage`); every
`F0` field is safe, which is why the battery percentage works and why nobody noticed.

**Both sides would have to change together, and that is why it was left alone:** the widget parses
with `Regex (-?\d+\.?\d*)` plus `double.TryParse` on the **current** culture. Today it reads
`9,4` as `9` — a lost decimal. If the helper started writing `9.4` while the widget still parsed
with the German culture, it would read **94**. Fixing the producer alone makes it worse.

### Immersive mode hides the CHIPS, not the bar

`ApplyFooterVisibility` collapsed the whole `FooterBar` until today, which would take the clock and
the battery with it — and those two are exactly what somebody still wants from across the room when
the hints are down. Now `ActionBar` collapses and `ApplyFooterChrome` drops the background and the
hairline, so what is left reads as text over the shelf rather than as a band.

⚠️ **`ApplyFooterChrome` is ONE writer that reads BOTH facts every time** — is there a background
picture, are the chips hidden. The two arrive from opposite directions (settings vs. immersive
mode), and a chrome each of them half-owns is how a footer ends up transparent with a hairline under
it depending on which happened last.

### The background reaches the footer as a blurred copy

A second `Image`, same source, same geometry (`RowSpan` over everything, `UniformToFill`) so the two
are pixel-aligned — a copy sized to the footer strip alone would show a different part of the
picture and break at the seam. An `OpacityMask` with both gradient stops on the same offset confines
it to the strip; `RefreshFooterBlurMask` recomputes that offset from the footer's real height, which
is why it is wired to the window's `SizeChanged` **and** the bar's own. `BitmapCache`, because a
blur over a 1920px image is not something to recompute per frame.

### Two more switches, and one chip that went away

- **Recent reflections** (`CenterSettings.RecentReflections`, on by default) — the mirrored covers
  under the reel. Off gives the covers the height the mirror was using: `MeasureReelMetrics` divides
  by `1 + fraction` only when they are on.
- **B has no chip on the shelf** any more (Recent, the stores, ROMs) — it still opens the exit
  prompt, bound the way the ROM-system triggers are, without a footer chip. Every SUB-screen keeps
  its chip, because there B means something specific to that screen.
- **German "Rescan" is now "Neu laden"**, not "Neu suchen": the button re-reads what is already
  there.

## Steam achievements (2026-09-09)

**The whole thing is written up in `Doku/STEAM_Achievements.md`** - where Steam keeps it, what was
measured, what was left out on purpose, and the Steamworks-SDK route for friend presence. Start
there. What follows is only the part that gets broken by accident.

Confirmed on the device on 2026-09-10: the numbers and the lists are right, and the only thing
left open is how it looks. Center reads Steam's own binary caches under `<Steam>\appcache\stats`: a **schema** blob per game
(localised names, descriptions, icon hashes) and a **per-user** blob whose `data` word is a bitfield
of unlocked achievements, with an `AchievementTimes` map beside it for the dates. No account, no API
key, no network for any number - only the icons come off the CDN.

**There is ONE primary source and it is those blobs.** `achievement_progress.json` next door is
Steam's own pre-computed summary and is used ONLY for games with no schema on disk. It is staler and
covers fewer games, and having the library line and the game menu answer "how far along is this?"
from two different files is the failure this project has paid for more than once.

WARNING: **`SteamPlaytime.ActiveAccountId()` is now shared.** The achievements reader needs the same
account id that sits in the middle of `UserGameStats_<accountId>_<appid>.bin`. Two copies of "which
Steam account" is how a machine ends up showing one user's hours next to another user's
achievements - if that resolution ever changes, it changes in one place.

WARNING: **The parse is lazy, cached per game, and the library refresh only DROPS the cache.** It
does not read every game: that would mean opening two binary blobs for each of several hundred
entries on a pass that has to stay responsive. Anything that wants an eager pass needs to move it
off the refresh thread first.

**Icons: `GameArt.LoadRemoteAsync`, never `LoadAsync`.** An http source handed to
`BitmapImage.UriSource` downloads asynchronously and then makes `Freeze` throw - the same trap that
once left the art picker showing a grid of grey tiles. Every row draws a card behind the icon, so
offline costs a picture and never a line of text.

## Steam friends (2026-09-11)

**Written up in `Doku/STEAM_Friends.md`.** The three things that break by accident:

WARNING: **No app id, ever.** The friend list is read from `steamclient64.dll` in Steam's folder with
no `SteamAppId` and no `steam_appid.txt`. With an app id Steam shows the user "playing" something to
every friend for as long as Center runs. Without one the status stays "Online" (measured on the
device, desktop client and phone app).

WARNING: **The read is a child process (`--steam-friends`), never in Center itself.** Loaded into
Center the DLL would stay locked for days and a moved vtable slot would crash Center. The argument is
handled FIRST in `Program.Main`; anything added above it runs in every friends refresh.

WARNING: **Center has its own `Main` because of this.** A `SplashScreen` build item makes WPF's
generated entry point show the splash before our code sees the arguments - every refresh flashed it
over the library. The splash is a plain Resource shown by `Program.Main`; do not turn it back.

**The activity feed is Steam's own cache** - `userdata\<id>\config\librarycache\0.json`, protobuf
entries in `usernews`. It is refreshed by Steam, not by us, and the refresh cadence is unmeasured:
never present its newest entry as "now". Type numbers are Steam's `EUserNewsType` from `steamui\`.

**RT opens the friends list on every shelf except ROMs and Misc.** ROMs keeps its systems on the
triggers. The corner chip and the RT binding read the same `LibraryTabOffersFriends`; keep them one
test.

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

## ⛔ Center does not install itself on a double-click any more (2026-09-08)

There are exactly **two** ways Center is allowed to arrive on a machine:

| | |
|---|---|
| **The ClawTweaks setup** (Inno) | first install and every upgrade |
| **Velopack** | Center updating itself in place |

A bare `CTW_Center.exe` downloaded from this repo's releases is a **build, not an installer**, and the
release notes have to say so. Running one now lands on a screen that says the same thing and offers
one action: open the ClawTweaks release page.

### The gate is `--resume-install`, and it is not a new mechanism

`App.OnStartup` → gate #0 → `!SelfInstaller.IsRunningFromInstallDir()`:

```
--resume-install present  ->  Install / Update / AlreadyInstalled   (the old behaviour)
otherwise                 ->  NotForInstall                          (the new screen)
```

The Inno setup has **always** run the bundled Center exe as
`<setup> --resume-install [--onboarding]`, and `InstallCenterWindow` has always read that argument as
"the user already acted, install without asking again". All that changed is that it is now the *only*
way in.

⚠️ **Velopack never passes through this gate at all.** It installs into its own root
(`%LOCALAPPDATA%\ClawTweaksCenter\{current, Update.exe}`), and `IsRunningFromInstallDir` already
returns true for that layout — the Velopack arm in `SelfInstaller` predates this change and is what
makes the whole thing a no-op for the updater. `Update.exe` never launches Center *to install it*;
it replaces the folder and starts the stub.

⚠️ **It is a don't-do-this-by-accident gate, not a security boundary.** Anyone who types the switch
gets the old behaviour — which is exactly what a developer testing a portable build out of
`PortableExe\` needs: `CTW_Center.exe --resume-install`.

### Three things that go wrong if this is touched carelessly

1. **Removing the `--resume-install` arm breaks the classic (non-Velopack) installer.**
   `Build-Installer.ps1` without `-CenterVelopack` bundles `CTW_Center_<ver>_Setup.exe` and relies on
   it self-installing *and* starting Center. That build is the rollback path for "Velopack turned out
   to be a mistake" and has to keep working.
2. **`NotForInstall` must never reach `StartInstall`.** The autoStart branch checks for it explicitly,
   on top of the action bar not offering the chip — an install that happens on a screen which says it
   will not install is worse than no gate at all.
3. **The URL is printed on the screen, not only behind the chip.** `PrerequisiteGuide.OpenPage`
   swallows a failed browser launch by design; without the visible URL a machine with no usable
   default browser would show a chip that does nothing and no way to find out where to go.

## 🔴 The usbip version gate is TWO-SIDED, and it used to point the wrong way (2026-09-08)

`ToolDetect` had `MaxSupportedUsbipVersion = "0.9.7.7"` and a one-sided test:

```csharp
return found > max ? raw.Trim() : null;   // 0.9.8.0 > 0.9.7.7  =>  "UNSUPPORTED"
```

The ClawTweaks setup installs **0.9.8.0**. So Center reported the version its own installer had just
put there as unsupported, and the card told the user to *uninstall it and install 0.9.7.7 from the
link on this page*. That link is not a stale string: **0.9.7.7 bugchecks the machine** with
DPC_WATCHDOG_VIOLATION (0x133) whenever the virtual pad is mounted — two minidumps with
byte-identical stacks, upstream usbip-win2 issue #172, fixed in 0.9.8.0. Center was instructing
people to downgrade into a crash, on every machine, right after installation.

**Now:** `SupportedUsbipVersion = "0.9.8.0"`, and anything that is not exactly that version counts as
not installed. `UsbipIsTooOld` splits the message, because the two sides are not the same problem —
too old **crashes the device**, too new is merely unverified. One sentence for both would be either
scaremongering or an understatement.

### Three things to know before touching this

1. **This status is a GATE, not a display.** `Installed = false` makes `ShowMissingPrerequisites`
   abort the ClawTweaks build install that was in progress. That is deliberate: a user on 0.9.7.7
   should not be able to install a build until usbip is updated.
2. **The helper checks again at the moment it would mount the pad**, and that is the check which
   actually prevents the bugcheck. Center's copy is the one that stops a *download*; the helper's is
   the one that stops a *crash*. The setup can be skipped (usbip already "installed", a hand
   rollback, a rollback build), so neither alone is enough.
3. **It fails OPEN on an unreadable version**, like every other version gate here. Locking a working
   machine out over a version string we could not parse is the more expensive mistake.

⚠️ **This screen is now a DIAGNOSIS screen, not an install guide.** The five onboarding steps carry
no tools step, `ToolsPhase` belongs to the deliberately-disabled `MainWindow`, and the ClawTweaks
Inno setup installs the tools. The prerequisites card therefore only appears when something is
genuinely missing or wrong — which is exactly why its text has to be right: it is read only in the
failure case.

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

## ✅ Erledigt: im Schnellmenue ist nur die UEBERSCHRIFT zentriert

**Falsch in `a6e38dc` "Centre the quick menu's labels" (2026-09-05), am selben Tag
zurueckgenommen.** Gefragt war die grosse **Ueberschrift**; zentriert wurden stattdessen die
Beschriftungen in allen drei Spalten - in den beiden Seitenlisten und der Mitte also genau das
Gegenteil.

**Der Stand jetzt:**

| | Ausrichtung |
|---|---|
| "Library quick menu" (26 pt) | **zentriert** |
| `SidebarHeading` ueber den beiden Seitenspalten | zentriert (war es schon vorher) |
| **jede Zeile** - Tray-Apps, Windows-Tools, die Aktionen in der Mitte | **links** |

**Warum die Zeilen links bleiben:** eine Liste, die jemand von oben nach unten liest, scannt an
einer geraden linken Kante. Der `centerText`-Parameter an `BuildRowVisual` ist wieder weg - wer ihn
erneut braucht, hat vermutlich dieselbe Verwechslung vor sich.

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

## 🔴 Two halves of one list have to be LAID OUT the same way, not merely padded the same (2026-09-10)

Reported on the library quick menu: the power rows (Sleep / Hibernate / Shut down / Restart) did not
line up with **Center start screen** and **Close Center** above them. Their icons were clipped to
slivers and every label started an icon column too far left.

It reads as a padding fault and it is not one. Both the power CARD and the free-standing rows carry
`MinWidth = ExitPromptCentreWidth`. As soon as the two sidebars are up, the middle column is
**narrower than that** - and then a stretched child pins its left edge to the column and overflows
to the right, while a **centred** one overflows by half on each side. The card was centred. It
therefore sat half the overflow further left than the rows above it, and clipped its own contents.

**Fixed by stretching the card like the rows above it.** In the same pass the horizontal padding was
reduced to one number: the card pads nothing sideways, every row pads itself with
`ExitPromptRowPadding`.

⚠️ **A first attempt changed only the padding arithmetic** - the card kept 6 and the rows
inside subtracted it. Arithmetically identical, and it did not settle the report, because the offset
was never in the padding. Two numbers that have to be kept in step to produce one alignment are a
worse way of saying "these are the same" than using the same number.

⚠️ **The glyphs were checked before the geometry was touched.** `\uE708`, `\uE74E`,
`\uE7E8` and `\uE777` render correctly in the shipped Segoe Fluent Icons (moon, disk, power,
restart arrow) - so "the icon is broken" was ruled out and only the layout was left. Rendering a
codepoint block to a PNG and looking at it is the two-minute check; a cmap query only says the
codepoint exists, not which icon sits there.

## The footer battery says what the machine is doing (2026-09-10)

| state | line |
|---|---|
| charging | `87% · charging · 1:15 h` |
| discharging | `87% · discharging · 2:30 h` |
| plugged in, full | `100% · AC power · fully charged` |
| plugged in, below full | `80% · AC power · not charging` |

The last row is not what was asked for and is deliberate: **ClawTweaks itself sets charge limits**,
so "fully charged" at 80% would be a sentence this very product made false. The threshold is 99%.

⚠️ **The AC line is the one fact on that screen that does not come from the helper.** Its
metrics bundle carries the charge, the runtime and "is it charging" - and no AC line at all. Without
it, "plugged in and full" and "on battery with no runtime estimate" are the same absence of
information, and the first is the normal state of a handheld in its dock. `Core.PowerLine.OnMains()`
asks Windows directly. That is not a second answer to a question the helper already answers.

⚠️ **It lives in `Core/PowerLine.cs` because there were TWO copies.** The profile detail
page already had this P/Invoke and the footer was about to grow an identical one. Unknown (255) and
a failed call both read as "not on AC" - unplugged is this product's primary state.
