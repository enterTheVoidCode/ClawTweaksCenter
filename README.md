# ClawTweaks Center

Installer and control panel for [ClawTweaks](https://github.com/enterTheVoidCode/ClawTweaks) on the
MSI Claw. Center installs and updates the ClawTweaks app package, guides you through the
prerequisites it cannot install for you, and gives you a controller-navigable menu for maintenance
tasks once everything is in place.

Center is a WPF desktop app, shipped as **one self-contained exe**: no .NET installation required,
nothing to unpack, runs from wherever you put it.

## Contributing

Read [DEV_GUIDELINES.md](DEV_GUIDELINES.md) before changing anything — it is written for human
developers and AI coding agents alike. [CONTRIBUTING.md](CONTRIBUTING.md) covers submitting a change.

One of those rules is a hard boundary rather than a preference: **the ClawTweaks background helper
lives in a separate, private repository, and its code must never be brought into this one** — not
copied, ported, paraphrased, or referenced. Center talks to it over a named pipe, and everything
needed for that is already in [`ClawTweaksCenter/Shared/`](ClawTweaksCenter/Shared/README.md).

The Game Bar **widget** is public too, but it lives in the main
[`ClawTweaks`](https://github.com/enterTheVoidCode/ClawTweaks) repository — contributions to the
widget belong there, not here.

## Building

```
dotnet publish -c Release
```

That is the whole build. Everything that makes the result portable — self-contained, single-file,
bundled native WPF libraries — is set in the project file, so a plain publish cannot accidentally
produce something that only runs on the machine it was built on.

Output: `ClawTweaksCenter/bin/Release/net10.0-windows/win-x64/publish/CTW_Center_<version>_Setup.exe`,
also copied to `ClawTweaksCenter/PortableExe/` so shippable builds have one obvious home.

**Only the exe under `publish/` may be handed to anyone.** A plain `dotnet build` leaves a
same-named ~300 KB apphost one directory up that needs its sibling DLLs. It runs fine from that
folder, which is what makes it dangerous — Center would install itself by copying that single file,
and the installed copy then dies before `Main`. `SelfInstaller.IsSelfContainedSingleFile` refuses to
install such a copy, but do not ship one either.

Requirements: .NET 10 SDK, Windows, x64.

## Layout

| Path | What lives there |
|---|---|
| `ClawTweaksCenter/Phases/` | The install flow, one class per step (detect, tools, install, controller, finalize) |
| `ClawTweaksCenter/Core/` | Everything with no UI: device detection, downloads, package install, helper control, pipe client |
| `ClawTweaksCenter/Core/Sources/` | Where installable builds come from (GitHub releases, Google Drive) |
| `ClawTweaksCenter/Navigation/` | Gamepad navigation — Center is meant to be usable with the Claw's own controller |
| `ClawTweaksCenter/Ui/` | Shared window chrome, action bar, small helpers |
| `ClawTweaksCenter/Shared/` | **Mirrored** contract files shared with the ClawTweaks helper — see [its README](ClawTweaksCenter/Shared/README.md) before touching them |

## Two things worth knowing before you change something

**Center never asks for administrator rights.** Not rarely — never. It installs itself per-user
(`%LOCALAPPDATA%\Programs\ClawTweaks Center`), and the three things that genuinely need elevation are
handed off rather than performed: drivers go to their vendors' own installers, the certificate goes
to the Windows import wizard, and the scheduled task is registered by the signed ClawTweaks helper
itself. Please do not reintroduce a "just in case" self-relaunch as administrator.

**Center does not download and run executables.** It reports that an update exists and links to the
release page; you download and install it. Fetching an exe and starting it is the shape of a dropper,
and checking its bytes afterwards does not change that. Installing the ClawTweaks **app package** is
different and stays — that is `Add-AppxPackage`, not starting a binary.

## Which builds Center offers

The list comes from
[`manifest/setup-manifest.json`](https://github.com/enterTheVoidCode/ClawTweaks/blob/master/manifest/setup-manifest.json)
in the main ClawTweaks repository, not from this code. `minimumClawTweaksVersion` there is the floor
for offered app builds; anything below it is listed greyed out. The check **fails open** on purpose —
offline, missing field, or an unparsable version all mean installable, because nobody should be
locked out for having no connection.
