# How to take the Velopack updater back out

This folder is the whole feature. It was built to be removable because re-introducing a self-update
path is a **reversal of a decision this project made deliberately** — Center's old self-updater was
deleted for being the dropper shape, and "signed packages and a fixed layout" changes the build shape
of that path, not the fact that a background process fetches code and starts it.

So the exit is written down, not assumed.

## Turning it off (no build, no deploy)

Everything is off already unless somebody set these. Both are `HKCU\Software\ClawTweaks\Center`:

| value | type | meaning | default |
|---|---|---|---|
| `VelopackUpdates` | DWORD | the master switch | **absent = off** |
| `VelopackSilentUpdates` | DWORD | may an update apply itself | **absent = off** |
| `VelopackFeedOverride` | String | read releases from a local folder instead of GitHub | absent |
| `VelopackManifestOverride` | String | read the manifest from a local path or URL | absent |

`Enabled` is read **fresh on every call**, never cached — an emergency switch that needs a restart is
half a switch.

There is a third off switch that does not live on the machine at all: `silentUpdatesEnabled: false`
in `update-manifest.json`. That is the one that matters in the case it exists for, because the
machines that would need stopping are the ones nobody is sitting at.

## Rolling back the INSTALLATION - a different thing, and the harder one

Removing the code is three steps. Undoing an installation Velopack already made is not, because it
lives on other people's machines.

**The switch is a build flag:** `Build-Installer.ps1 -CenterVelopack` produces the Velopack
installer; **without it the classic one comes back**. That choice cannot be a runtime setting -
Velopack only replaces a folder it created, so who installed Center decides whether Center can
update itself at all.

A classic installer built after a Velopack one does **not** leave two Centers behind: it runs
`RemoveVelopackCenterIfPresent()` first, which calls Velopack's own uninstaller. That matters more
than tidiness - both installers write the SAME HKCU uninstall key (`...\Uninstall\ClawTweaksCenter`),
and that key is what the HELPER reads to find Center. Two owners for one key means whoever
uninstalls last takes it away from the other.

| direction | what the installer does |
|---|---|
| classic -> Velopack | install via Velopack, then delete the classic FILES and shortcut, leaving the registry key Velopack just wrote |
| Velopack -> classic | run Velopack's uninstaller (which takes its key with it), then let Center self-install as before |

WARNING: the migration must never remove the old Center with `CTW_Center.exe --uninstall`. That is
the guided leave screen: it resets the charge limit, hands the fan curve back to the firmware,
restores the controller mode and re-enables MSI Center M. A user who asked for an update would get an
offboarding, mid-install, with nothing saying why. Migrations delete files.

## Removing the code

Three steps, and nothing else in the tree knows this exists:

1. Delete `ClawTweaksCenter\Update\`.
2. Remove the `Velopack` `PackageReference` from `ClawTweaksCenter.csproj`.
3. Remove the single line at the top of `App.OnStartup`:
   ```csharp
   Update.VelopackUpdates.Bootstrap();
   ```

Then `dotnet build`. If it compiles, the removal is complete — that is the point of keeping the
settings accessors inside `VelopackUpdates` instead of in `CenterSettings`, where they would have
outlived the folder.

⚠️ **The four registry values survive the removal.** They are inert once the code is gone, and
deleting them is not worth a migration step; Center's uninstall drops the whole
`Software\ClawTweaks\Center` key anyway.

## What was deliberately NOT wired

- **No install, no shortcuts, no ARP entry.** `SelfInstaller` still owns installation. A second
  uninstall owner is how the guided leave screen gets lost — Windows Settings → Uninstall has to
  land there, not in an immediate deletion.
- **No automatic check on startup.** `Bootstrap()` only runs Velopack's own entry-point hook, which
  is a no-op on a Center that Velopack did not install. Nothing calls `CheckAsync` yet; wiring that
  to a screen is the next decision, not a leftover.
- **No elevation.** See the note in the plan: Center never asks for administrator rights, and that
  is an on-device-verified property, not an accident.

## The thing that still blocks real use

`UpdateManager` throws `NotInstalledException` unless the running Center IS a Velopack installation
(`%LOCALAPPDATA%\{packId}\{current, packages, Update.exe}`). A Center that `SelfInstaller` put in
place is, to it, simply not installed. So on today's builds `CheckAsync` returns null and the feature
is inert **even when switched on**.

That is not a bug here — it is the W1-versus-W2 decision in `PLAN_Velopack_Updates.md` §3, which has
not been taken. This folder is written so that either answer is a change of *configuration* rather
than of architecture: both paths end up at the same `UpdateManager`.
