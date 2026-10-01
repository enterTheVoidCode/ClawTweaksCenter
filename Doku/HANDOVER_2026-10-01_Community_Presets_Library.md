# Handover 2026-10-01 — Community presets in the Library

> ## ⚠️ STAND 2026-10-01 ABENDS — THIS BLOCK OVERRIDES THE REST OF THE FILE
>
> Everything below §0 is the state from the morning port and is partly **outdated** (banner above the
> cover, "applying is not built", "nothing committed"). What is true now:
>
> | | |
> |---|---|
> | **Committed & pushed** | Center `a8c3024` + `5cdff52` (master), dev repo `d34ad25c` (release/v0.3.98.0) |
> | **Center version** | 0.4.38. Setup 0.4.1.16 still carries Center **0.4.36** - a new full build (package + Velopack + Inno `-CenterVelopack`) is needed before the release. A build at 0.4.1.17 was started and **aborted on request**; `Package.appxmanifest` was restored, `internal_build.txt` is still 16. |
> | **Schema** | v2 adds `fpsCapMode`. Plan for the next field: dev repo `Doku/PLAN_Community_Preset_Schema_Versions.md` (versions sign over a prefix of the field order - read it before touching the order). |
>
> **Launch screen.** A round **pill in the top right corner** (one line, drawn D-pad-right glyph, count
> for this kind of Claw + "N in all"). **D-pad right opens the overlay directly**; Up from Play focuses
> it, A opens it. Achievements stay one column under the cover. The controller panel lists every remap
> from `ControllerGamepadMapping`, not only M1/M2.
>
> **List.** Opens on presets of this Claw family (`SameDeviceFamily`: a2vm7/a2vm8 = a2vm); **X** toggles
> all devices. The card IS the row: **A = Use this preset**, **Y = Rate** (bound, no footer chip; the
> glyph hints show only on the focused card). Share is a green filled button. Matching also finds widget
> posts that name only the exe (`HasExe` probes the install folder; title ".exe" stripped). The index is
> force-refreshed (past the CDN cache) when the overlay opens and on Y in the library; `MaxAge` 15 min.
>
> **Apply.** Center → helper `CommunityApplyPreset` → `Program.CommunityApply.cs` finds/creates the
> profile **by exe path**, writes power fields only (TDP, boost mode, OS power mode, cap), turns the
> profile on, syncs the RTSS whitelist. v2 `fpsCapMode` switches the game to that limiter and clears the
> other. Exe comes from `CommunityPresets.ResolveExe` (store ExePath → existing profile → a post's exe in
> the install folder); none → "Start the game once, then try again." Reply `CommunityApplyResult`.
>
> **Share form.** `Native FPS | FPS Limiter` (Off/Intel/RTSS - "FPS Limiter" untranslated on purpose),
> `TDP | Power`. FPS/TDP open the keyboard with **quick values** on top (FPS 30/40/60/90/120; TDP
> 8/12/15/17/20/25/30 capped at the PL1 max: A2VM 30, EX 35, A1M 43). Capped ⇒ the native FPS IS
> `fpsLimit`. Power starts on battery. Detailed graphics is an expander row (chevron, outline) and seeds
> from the overall preset. After a successful share: **back to the game**, and the game is remembered in
> `%LOCALAPPDATA%\ClawTweaks\Center\community-pending.tsv` until the index has this machine's preset (or
> 14 days) - the list shows a note instead of Share meanwhile.
>
> **Navigation rules (user-set).** Up/Down move row to row, a side-by-side pair counts as one row;
> Left/Right cross a pair. A chip row INSIDE a pair needs **A first** (amber outline, Left/Right pick, A/B
> done); full-width chip rows pick directly. Scroll offset survives re-renders per screen.
>
> **Rating.** Stars `1★…5★` beside an optional comment (50 chars, keyboard with Space - only there).
>
> **Widget (dev repo).** The wizard asks "Was the frame rate capped?" (No/Intel/RTSS) instead of taking
> the cap from the profile; `PerfFromCommunity` honours `fpsCapMode`.
>
> **Janitor.** `Diagnostics/Probe-CommunityPost.ps1` knows v2 (`FieldCountByVersion`) and the embed label
> **"Limiter type"** - no alias for the old "FPS cap type" (user removed it; the only such posts were
> tests). All 9 real/older posts verified after the change.
>
> **Open.**
> - Delete the test posts in Discord (Brotato by billy ×2 - one carries the old label and is rejected
>   until deleted; Witcher 3 by billy if it was a test). Then `Publish-CommunityIndex.ps1 -DryRun`.
> - Full build + release planning (next session).
> - The pending-share memory is Center-only; widget posts do not write it.
> - Translations were done by the model, not reviewed by native speakers.
> - Dev tool: `ClawTweaksCenter\Deploy-CenterDev.ps1` swaps a fresh publish into the installed Velopack
>   copy (no feed, no setup) - for testing only.

## 0. The morning port (outdated where the block above says so)


**Who continues here starts at §9 (what is left).** Above it is what was built and why it looks the way
it does. This is a port of the Game Bar widget's community-presets feature into Center's Library.

| | |
|---|---|
| **Source feature** | the widget: `XboxGamingBar/Features/GamePresets/GamingWidget.Community*.cs` + `.../Input/GamingWidget.MiniKeyboard.cs` (in the **helper/widget dev repo**, not this one) |
| **Contract** | the dev repo's `Doku/SPEC_Community_Preset_Post.md`, `Doku/HANDOVER_2026-08-29_Community_Presets.md` |
| **Built & compiling (Center)** | data model, launch-screen banner + slideshow, the overlay (browse / rate / share), the on-screen keyboard |
| **Built (helper, dev repo)** | the three Center↔helper round-trips (identity / submit / rate) |
| **NOT built, on purpose (user, 2026-10-01)** | APPLYING a shared preset to a game's own settings — a later, separate task |
| **Committed** | nothing yet (commit rule: only on the user's say-so) |

---

## 1. What happens, in one paragraph

On the launch screen (the confirm prompt, just before a game starts) there is a **banner above the
cover**. It shows how many community presets exist for this game and slides through the best few.
Pressing **up** from Play moves focus onto it; **A** opens a full overlay where you browse every shared
preset for the game, **rate** one, or **share your own**. Reading the list needs nothing but a public
file; rating and sharing need the helper, because the signing key lives there and Center is a public
repo.

---

## 2. The files

### This repo (Center)

| File | What |
|---|---|
| `ClawTweaksCenter/Library/CommunityPresets.cs` | data model: fetch + cache the published index, match for a game, Bayesian "best first" sort, display + rating helpers. Self-contained — no helper, no Discord. |
| `ClawTweaksCenter/CenterMenuWindow.Community.cs` | the overlay plumbing: open/close, the screen machine, the self-contained selectable-row list, the browse screen, input + footer. |
| `ClawTweaksCenter/CenterMenuWindow.Community.Forms.cs` | the launch-screen banner + slideshow, the rate form, the share form, and the on-screen keyboard. |
| `ClawTweaksCenter/Library/ClawProfileDetails.cs` | **added** `CommunityFactsFor(game)` — reads tdpW / cpuBoostMode / osPowerMode / fpsLimit / resolution from the game's saved profile to prefill a share. |
| `ClawTweaksCenter/CenterMenuWindow.GameMenu.cs` | **hooked**: a new `GameMenuOverlay.Community` enum value, plus its line in the render switch, `GameMenuBack`, `MoveGameMenuSelection`, and `RefreshGameMenuActionBar`. |
| `ClawTweaksCenter/CenterMenuWindow.Library.cs` | **hooked** the launch overlay: `LaunchFocusCommunity`, the banner inserted above the cover, `MoveLaunchSelection` / `ApplyLaunchFocusVisuals` / `ActivateLaunchSelection` extended, the A label, and slideshow cleanup in `ClearLaunchOverlay`. |
| `ClawTweaksCenter/Shared/Enums/Function.cs` | **appended** 3 result functions (ordinal, mirrored from the helper). |

### The dev repo (helper/widget) — NOT in this repo

| File | What |
|---|---|
| `Shared/Enums/Function.cs` | the same 3 appended values, in the same order (the mirror). |
| `XboxGamingBarHelper/Startup/Program.PipeHandlers.cs` | a `CommunityRequestIdentity` handler, and submit/rate now reply to the **requesting** pipe (Center) as well as the widget. |

---

## 3. The read side (self-contained)

`CommunityPresets` fetches one flat file —
`https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaks/master/manifest/community-presets.json`
— caches it under `%LocalAppData%\ClawTweaks\Center\community-presets.json`, 6-hour staleness, exactly
the shape of `GamePresets` (the Clawptimize catalog). The janitor bot has already verified every post's
signature before publishing, so this side trusts the file and never touches Discord.

- `ForGame(game)` matches by **store+id OR normalized title** (title is the fallback that makes a
  cross-store copy of a game show the same presets), then sorts **same-device first, then a Bayesian
  rating score, then newest**. The displayed rating stays the real average — only the sort is Bayesian
  (a lone five-star must not outrank a well-liked preset).
- Display helpers (`DeviceName`, `Display`, `RatingLine`, `RatingBreakdownLines`) and `RatingCategories`
  are ported verbatim from the widget.

The published schema is a flat list of string fields; the authoritative set is the widget's
`ParseCommunityIndex`. `Preset` is a bag of strings on purpose — the schema only appends, so an older
Center reads a newer file without a code change.

---

## 4. The write side (Center → helper → Discord)

Center cannot hold the signing key or derive the authorId (it is a public repo), so **rate** and
**share** go over the Center→helper pipe and come back on a `Function`, the same round-trip shape the
driver check uses (`HelperPipeClient.RequestWithResultAsync`). The helper validates everything again —
it has the final say, because what it signs has to be what it approved.

Three round-trips, all added to the helper's `Program.PipeHandlers.cs`:

| Request (Extra key) | Reply (Function) | Payload out → in |
|---|---|---|
| `CommunityRequestIdentity` | `CommunityIdentityResult` | `true` → `device=<code>;nickname=<nick>;authorId=<id>` |
| `CommunitySubmit` | `CommunitySubmitResult` | the `key=value\n` submission → `""` ok, else the refusal reason |
| `CommunityRate` | `CommunityRateResult` | the `key=value\n` rating → `""` ok, else the refusal reason |

The helper branches on `ReferenceEquals(sender, centerPipeServer)`: a Center request is answered with
`PushCenterResult(...)`; the **widget's own Extra-push replies are untouched**. The submit/rate
validation and signing (`CommunitySubmission` / `CommunityRatingSubmission` in the dev repo) are
unchanged — Center just builds the same wire payload the widget's `BuildCwSubmission` / `SubmitRatingAsync`
build.

### Why identity comes from the helper

`device` must carry the Claw 7/8 variant (`a2vm7`/`a2vm8`), which only `MSIClawModelCatalog.LastIdentity`
knows — Center's `DeviceDetect` only sees `a2vm`. The **nickname** is stored by the helper
(`community-nickname.txt`, beside the profile store) and is shared with the widget. The **authorId**
marks your own posts and lets the rating form refuse a self-rating early. One request gets all three.
If the helper is unreachable, the read side still works; YOURS badges and the nickname prefill are
simply absent.

### The share submission

`CenterMenuWindow.Community.Forms.cs` → `SubmitCommunityCreate` builds the `key=value\n` lines:
`device, gameStore, gameId, gameTitle, powerState, tdpW, fpsNative, author` (required-ish), the
profile-derived extras `cpuBoostMode / osPowerMode / fpsLimit`, and the chosen
`resolution / graphicsPreset / upscaler / frameGen` plus their dependents (`upscalerPreset`,
`frameGenFactor`, `upscalerSource`) and the six optional detail fields. Dependents are **dropped**, not
hidden — a stale value behind a changed answer is how "off / quality" gets into a database. `tdpW` is
required by the helper; it is prefilled from the game's saved profile and otherwise typed.

---

## 5. The UI model

The overlay is a **`GameMenuOverlay.Community`** state, exactly like the achievements list: it opens
over the standing launch prompt and B returns to it. That reuses the whole GameMenu overlay plumbing
(render switch, back, direction dispatch, action bar). Inside it is a small screen machine
(`CommunityScreen`: `List / Rate / Create / Keyboard / Message`).

It does **not** borrow the game menu's `_gameMenuRows`/cursor — it has its own `_communityRows`,
`_communityRowActions`, `_communityRowLabels`, `_communityChipRows`, `_communityIndex`, and
`_communityScroller`, so two differently-shaped screens cannot fight over one cursor. Chip rows
(Left/Right picks a value) are the Center-native equivalent of the widget's dropdowns.

The **keyboard** (`CommunityScreen.Keyboard`) is the widget's MiniKeyboard ported to WPF: a D-pad grid
of character keys, A types, X backspaces, Y shifts (one-shot), B cancels, ✓ commits. No `TextBox` — a
controller cannot drive one, which is the whole reason the widget built its own, and the user asked to
carry it over.

### The launch-screen banner

`BuildCommunityBanner` (in `.Forms.cs`) returns the banner inserted as the **first child of the launch
stack**, above the cover, on the confirm screen only. `LaunchFocusCommunity` sits above `LaunchFocusPlay`
in `MoveLaunchSelection`. The slideshow is a 4-second `DispatcherTimer` that updates two held `TextBlock`s
without redrawing the screen; it is stopped in `ClearLaunchOverlay` and when the overlay opens.

---

## 6. Decisions (with the why)

1. **Apply/takeover is out of scope** (user). The overlay shows, rates and shares; it never writes a
   game's profile. See §9.
2. **The banner shows even with zero presets** (user): sharing the first one is why it is there.
3. **Rating with no comment.** The comment field is optional in the spec and needs the keyboard; it is
   left out of the rate form for now (the keyboard is wired for the nickname/FPS/TDP on the share form,
   so adding the comment later is small). Not a blocker — a rating is stars + optional categories.
4. **The share form is one scrollable page, not the widget's 6-page wizard.** Center's launch screen is
   full-size; a paged wizard is a small-widget shape. Same fields, same validation.
5. **You-already-shared** turns the Share row into a non-selectable note, not a dead button.

---

## 7. How to test on the device

1. **Banner:** open a game's launch screen (confirm). A banner sits above the cover. With the index
   published it shows a count and slides through presets; with none it invites the first share.
2. **Reach it:** press **up** from Play → the banner outlines; the footer A reads "Community presets".
   **A** opens the overlay.
3. **Browse:** cards show `fps@W`, author, device, settings and stars. B returns to the launch screen.
4. **Rate** a preset that is not your own: stars + categories, Submit → "Thanks". Needs the helper.
5. **Share:** the top row → the form → set FPS and a nickname with the keyboard, pick the chips, Share
   → "Shared". Needs the helper, and posts a REAL entry to the forum.
6. **Keyboard:** D-pad over the keys, Y shifts once, X backspaces, ✓ commits, B cancels.

Helper log lines to look for: `Pipe: CommunitySubmit received`, `[Community] posting CTW-…`,
`[Community] posted, thread id …`, and for identity the reply built in the `CommunityRequestIdentity`
handler.

⚠️ A successful share posts a real forum entry. Until the schema is frozen (dev repo HANDOVER §10),
test posts should be cleaned up, and the four existing dev-key test posts must be deleted before the
janitor runs.

---

## 8. Build status

- **Center builds clean** (`dotnet build ClawTweaksCenter/ClawTweaksCenter.csproj`, Debug) with all of
  the above.
- **The helper changes are verified by inspection, not yet built here** — per the dev repo's CLAUDE.md
  the helper is only built through `Build-Package.ps1`, not hand-invoked msbuild. They are ~40 lines:
  the 3 `Function` values (mirrored, append-only) and the three pipe branches, each using only symbols
  already present (`PushCenterResult`, `MSIClawModelCatalog.LastIdentity`, `CommunityPreset.DeviceKey`,
  `CommunityCode.AuthorId`, `CommunitySubmission.LoadNickname`). Confirm them in the next package build.

⚠️ **`Function.cs` is ordinal and mirrored across both repos.** The three new values are appended in the
same order on both sides. Never reorder or delete.

---

## 9. What is left (for the next agent)

1. **Applying a shared preset to the game.** The explicit next task. The widget does it in
   `GamingWidget.CommunityIndex.cs` → `ApplyCommunityPreset_Click` / `PerfFromCommunity` — **power
   fields only** (TDP, boost, power mode, fps cap), never resolution/graphics/upscaler, because those
   describe what the poster did inside the game and are not Center's to set. In Center this means
   writing those fields into the game's performance profile (the same store `ClawProfileDetails` reads)
   and is a profile-writing change — coordinate before building. A card button "Use this preset" is the
   natural home.
2. **The rating comment.** Add a "Comment (optional)" row on the rate form that opens the keyboard
   (`OpenCommunityKeyboard(..., digitsOnly:false, ...)`, max 50) and send `comment=` in the payload.
3. **Localization.** Every user string goes through `Core.Loc.T/F`; the new keys need adding to
   `strings.tsv` (see other Center screens). Until then they fall back to English.
4. **"Update your preset".** The helper can PATCH its own forum post (dev repo HANDOVER §7d). Showing
   "update" instead of "share" when this machine already posted needs the helper to remember
   `threadId/msgId` — not built on either side.
5. **Banner on the install screen.** Today the banner is confirm-only (installed games). A not-installed
   game could still have presets worth seeing; deferred with the launch focus model.

---

## 10. Traps worth knowing

- **WPF `StackPanel` has no `Spacing`** (that is WinUI). Use margins. Cost one build here.
- **The overlay reuses the GameMenu input funnel**, which is checked *before* the launch branch in both
  the direction dispatch and the action bar — that ordering is what lets an overlay open over a standing
  launch prompt. Do not reorder it.
- **The helper replies to the requesting pipe.** If a Center round-trip ever "hangs", check the helper
  branched on `centerPipeServer` and used `PushCenterResult` — a reply sent to the widget pipe is the
  documented failure mode for every Center round-trip in this project.
- **Device sort vs. device post.** `CommunityPresets.DeviceCode()` (no variant) is fine for the
  read-side same-device preference; a *post* must use the helper's accurate `device` (with variant).
