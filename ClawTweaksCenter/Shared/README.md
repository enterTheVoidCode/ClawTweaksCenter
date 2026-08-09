# `Shared/` — mirrored contract files

**These five files are copies. They are not owned here.** They are mirrored from the private
ClawTweaks repository, where the helper (`XboxGamingBarHelper`) and the Game Bar widget compile the
same sources out of a project called `Shared`.

| File | Source path in the private repo | What Center uses it for |
|---|---|---|
| `Enums/Function.cs` | `Shared/Enums/Function.cs` | Names every property the helper exposes over the pipe |
| `Enums/Command.cs` | `Shared/Enums/Command.cs` | Get / Set / Notify verb on a pipe message |
| `Data/ClawHardwareId.cs` | `Shared/Data/ClawHardwareId.cs` | Resolves the Claw model from HKLM + the device tree |
| `IPC/HelperHandover.cs` | `Shared/IPC/HelperHandover.cs` | Asks a running helper to shut down in an orderly way |
| `Deployment/HelperFileDeployment.cs` | `Shared/Deployment/HelperFileDeployment.cs` | Copies the helper out of the installed package |

The namespaces are deliberately left as `Shared.*`. Nothing in Center's own code had to change to
accommodate the mirror, and the namespace keeps saying where the code came from.

## Why a copy and not a project reference

Center referenced `..\Shared\Shared.csproj` until it moved into this repository. Two problems with
carrying that over:

1. It made this project impossible to build on its own — the whole point of this repository.
2. `Shared.csproj` pins an **absolute** `HintPath` to
   `C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd`. Anyone with a
   different Windows SDK version installed cannot restore it. It also drags in NLog and a WinRT
   projection that Center has no use for.

Only what Center actually calls was mirrored. `DeviceInfo` and `PipeMessage` were **not** copied —
they appear in Center's comments but in none of its code, and `PipeMessage` in particular pulls in
`Windows.Foundation.Collections.ValueSet`, which is exactly the WinRT dependency this repository is
better off without.

## The rule that matters: `Function.cs` is append-only

`Function` is serialized **by ordinal** on the pipe. The helper writes `(int)Function.X`; Center
reads the number back. Reordering entries, or inserting one in the middle, silently repoints every
value after it — no compiler error, no exception, just the wrong property being read and written.

So:

- **Only ever append** to `Function`. Never reorder, never insert, never delete.
- When the private repo changes any mirrored file, mirror it here in the same shape. Two copies of a
  wire contract drift by default; the drift is silent, and it surfaces as Center talking to the
  helper about the wrong property.

`Tools/Check-SharedMirror.ps1` in the private repo compares the two sides and reports differences.
Run it before releasing either component.

Mirrored at ClawTweaks 0.2.0.25 / Center 0.1.9.15.
