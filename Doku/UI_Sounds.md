# Library sounds and background music

Built 2026-09-11 (Center 0.2.46), sound sets added 2026-09-12 (0.2.47). The library plays short
sounds for navigation, A, B and a game start, and optional music while it is on screen. Both are
switches in **Library settings -> Sound settings**, and the navigation and back sounds can be
switched between two recordings each.

**Not heard on the device yet.** Built and compiled; the probe that chose the engine played the
Kenney files on the dev machine.

---

## 1. What plays when

| Input in the library | Sound | File name |
|---|---|---|
| D-pad, left stick, LB/RB | Navigate | `navigate` |
| A that runs an action | Confirm | `confirm` |
| B that runs an action | Back | `back` |
| A on the launch prompt, game started | Launch (instead of Confirm) | `launch` |
| library on screen, music switched on | music, looped, in name order | `music*` |

**Only in the library** (settings, friends and the launch prompt included). Home, onboarding and the
installer screens stay silent.

**Navigate plays on every press, also against the edge of a list.** `MoveSelection` does not report
whether anything moved, and teaching forty screens to say so was not worth it for the first version.

**One place decides.** `CenterMenuWindow.Invoke` picks the effect from the button. An action that
wants its own sound plays it itself (`ConfirmLaunch` plays Launch), and `UiSounds.PlayCount` tells
Invoke to skip the generic one. A new screen gets its sounds without doing anything.

**Music plays while** the setting is on, the view is the library, the window is visible and not
minimised, and no game is running (`_launchPrompt == Running`). It fades over 0.8 s and resumes where
it stopped.

**When the WINDOW goes, the music is CUT, not faded** (user, 2026-09-12): hidden to the tray,
minimised, or a game on screen. The thing the music belonged to is gone, and a fade plays on over
whatever took its place. A move inside Center - to Home, into a menu - still fades.
`SetMusicPlaying(playing, immediate)` carries the difference; the track position survives both.

⚠️ **Losing FOCUS is not covered, and cannot be** with the gate as it stands: `IsActive` is the one
signal that would report it, and a Center plainly on screen routinely reports `IsActive == false`.
Alt-tabbing to something Center did not start leaves the music playing. ⚠️ Not `IsActive`: a Center plainly on screen often reports inactive (see
`CenterMenuWindow.Tray.cs`). `UpdateLibraryMusic` runs from `RefreshActionBar` and from the window's
visibility and state events.

## 2. Sound sets

Navigate and Back have two recordings each; Confirm and Launch have one (user, 2026-09-12 - there is
no alternative for those yet). The set is picked per sound in **Sound settings**, and each press of
the row plays the set it just picked - the names say which recording it is, not what it sounds like.

| Set | Navigate | Back | Where it came from |
|---|---|---|---|
| **Glass** (default) | `navigate.ogg` | `back.ogg` | Kenney `glass_001` / `glass_003` |
| **Natural** | `navigate_natural.ogg` | `back_natural.ogg` | Kenney, user 2026-09-12 |

**The default set has NO suffix**, and that is the whole trick: a loose `navigate.ogg` in the
override folder still replaces it, exactly the way it did before sets existed. Every other set is
`<sound>_<set>`. A set with no file of its own falls back to the default one, so a half-filled set is
one sound short rather than silent on that press.

Stored as the NAME in `NavigateSound` / `BackSound` (`HKCU\Software\ClawTweaks\Center`), not as an
ordinal - the same reasoning as the language, and an unknown name reads as the default set.

**The Natural set is played louder** (`UiSounds.FileGain`, user 2026-09-12): `navigate_natural` at
**1.8x**, `back_natural` at **1.3x**. Both are quieter than the Glass set as recordings, and the
alternative was re-encoding files we were handed. The clip provider clamps, so a gain cannot push a
loud sample past full scale - if a set ever sounds harsh rather than louder, that is clipping, and
the answer is a quieter effects volume, not a bigger number.

⚠️ **Adding a set is a line in `VariantsPerSound` plus the files.** Everything else - decoding,
the settings row, the preview - derives from it. Every set of every effect is decoded at start-up,
because the press that switches a set is the one moment a decode would be heard.

## 3. Files

Looked up by base name, first match wins:

1. `%LOCALAPPDATA%\ClawTweaks\Center\sounds\` - loose files, to try a sound without a build
2. `ClawTweaksCenter\Assets\sounds\` - embedded in the exe, what ships

⚠️ **A new file in `Assets\sounds\` is NOT picked up by an incremental build.** Measured 2026-09-11:
`confirm.mp3` was added, `dotnet build` reported success and the assembly kept its old timestamp and
its old four sounds. `--no-incremental` embedded it. A version bump renames the assembly and forces
the rebuild anyway, which is one more reason for the bump on every build.

`.ogg`, `.wav` and `.mp3` are read (MP3 through Media Foundation). Mono is widened to stereo,
everything is resampled to 48 kHz. **Both folders are read once per Center start** (first library
entry); a file added later counts from the next start. No file means silence, never an error.

Shipped today (Kenney "Interface Sounds", CC0, licence file next to them):

| File | Source | Chosen by |
|---|---|---|
| `navigate.ogg` | `glass_001.ogg` | user, 2026-09-11 |
| `back.ogg` | `glass_003.ogg` | user, 2026-09-11 |
| `navigate_natural.ogg` | the Natural set | user, 2026-09-12 |
| `back_natural.ogg` | the Natural set | user, 2026-09-12 |
| `music.mp3` | `atlasaudio-sci-fi-ambient-587603.mp3` (2:15, 44.1 kHz stereo, 4.3 MB) | user, 2026-09-11 |
| `launch.mp3` | `soundshelfstudio-ui-success-chime-513565.mp3` (1 s, 44 KB) | user, 2026-09-11 |
| `confirm.mp3` | `soundshelfstudio-ui-click-deep-512211.mp3` | user, 2026-09-11 |

**The music, the launch and the confirm sound are not Kenney's and not CC0.** Their names have the
shape of a Pixabay download (`<author>-<title>-<id>.mp3`). The user confirmed on 2026-09-12 that the
licences are in order for this repository, which is public - a file that ships inside the exe is also
handed out on its own here.

⚠️ **It is not a seamless loop.** It opens with silence (the first 0.1 s are digital zero) and is
looped as it is, so the wrap is audible as a short gap. Fine for ambience; a track made to loop would
remove it.

It adds 4.3 MB to the exe - MP3 does not compress further in the single-file bundle.

`drop_003.ogg` was the first navigation pick and was replaced by `glass_001` because it fits the B
sound better. A choice between navigation sounds for the user was considered and is **not** wanted
for now.

## 4. Engine

`Audio/UiSounds.cs`, NAudio 2.2.1 + NAudio.Vorbis 1.5.0 (MIT):

- **One WASAPI shared stream** (40 ms), one `MixingSampleProvider`. Every effect is decoded once into
  a float array; a press adds a small provider that the mixer drops when it ends.
- **Pinned to NAudio 2.x.** NAudio 3 moved `ISampleProvider` to `Span<float>` and deprecated
  `WasapiOut` - measured while building, it is a port, not an update.
- **The stream pauses itself** three seconds after the last audible sound (music faded out counts as
  silent) and resumes on the next one. A running stream keeps the endpoint awake on a battery device.
- **A lost device** (headphones out) stops the stream with an exception; it is disposed and rebuilt
  on the next sound.
- **Same effect within 35 ms plays once** - a diagonal stick push raises two directions in one tick.
- Volumes come from the settings (§4). An effect takes the volume when it starts; the music reads
  it on every buffer, so a change is heard within one 40 ms buffer.
- Every failure is logged once to the install log with `[Sound]` and becomes silence. The loaded
  set is logged as `[Sound] effects: navigate, back; music tracks: 0`.

## 5. Settings

**Library settings -> Sound settings** (user, 2026-09-11): one row in the library settings grid
(row 12, the key row is 13) opens its own screen, `CenterMenuWindow.SoundSettings.cs`. Same layout
as the tab editor - heading on the left, rows on the right - and like it, `_soundSettingsOpen`
implies `_settingsOpen`.

| Row | Registry value (`HKCU\Software\ClawTweaks\Center`) | Default | Input |
|---|---|---|---|
| Interface sounds | `InterfaceSounds` | on | A |
| Effects volume | `EffectsVolume` (0..100) | 48 | Left/Right, steps of 10 |
| Navigate | `NavigateSound` | `glass` | A, or Left/Right |
| Go back | `BackSound` | `glass` | A, or Left/Right |
| Background music | `BackgroundMusic` | off | A |
| Music volume | `MusicVolume` (0..100) | 28 | Left/Right, steps of 10 |

The two set rows sit under the effects volume, not under the switch: they are what the sounds ARE,
and the music keeps the same shape (switch, then volume) below them.

**Both defaults went down a fifth on 2026-09-12** (60 -> 48, 35 -> 28, user). They are deliberately
OFF the round-number grid, and the step snaps back onto it (`SteppedVolume`) - plain addition would
carry the offset for the life of the installation, and 58 / 68 / 78 is a value nobody can read out
loud. Sounds are **on** by default and the music is **off**; both sound sets default to Glass.

Two volumes, not one: the music runs under everything, the effects are a click per press, and the
balance between them is the thing people turn. The defaults were my call, not confirmed.

**The effects volume previews itself:** Left/Right in the library plays the navigation sound, and it
plays at the new volume. The music follows its slider live when music is on.

## 6. Open

- Whether a press at the edge of a list should stay silent, or get its own sound.
- Alternatives for Confirm and Start game - the table is there, the recordings are not.
- Latency on the Claw (40 ms buffer; the first sound after a pause restarts the stream).
