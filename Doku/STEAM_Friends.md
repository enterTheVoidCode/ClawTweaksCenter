# Steam friends in Center

Built 2026-09-11 (Center 0.2.42, Big Picture removed and splash fixed in 0.2.43). The library shows
how many Steam friends are online, lists them with their status and the game they are in, and opens
a Steam chat with one of them.

**Confirmed on the device (0.2.42):** count "2 of 6", a friend in a game shown with the game's name,
A opened Steam's chat with him.

Background and the routes that were weighed: `Doku/STEAM_Achievements.md` §7.

---

## 1. What is on screen

| Where | What |
|---|---|
| Library, right end of the tab strip | `RT  2 of 6 online` |
| RT | the list: avatar, name, status or game; in-game first, then online, away/busy, offline |
| A on a friend | Steam's chat window with that friend (`steam://friends/message/<id64>`) |
| B | back to the shelf |

**Offered on:** Recent, Favorites, All, every store tab and Not Installed.
**Not on ROMs** - LT/RT keep moving between systems there - **and not on Misc**, which is the user's
own apps, not a store. On both, the corner is empty and the triggers are unchanged.

**The chip only exists while the list can be read.** With Steam closed or signed out the corner is
empty; a chip whose button does nothing is worse than no chip.

**Big Picture is NOT here** (user, after 0.2.42). It sat on LT and as Y in the list for one build.
LT is free on the store shelves.

**The old info button in that corner is gone** (user). X still opens the info on every shelf, and
the footer says so. `CenterSettings.LibraryInfoSeen` and the pulse went with it.

---

## 1b. Activity (0.2.44)

**Colours (user):** green online and in a game, yellow away/busy, grey offline. A friend in a game is
green even when Steam reports them away.

**Under each friend, one line of recent activity** - the newest of:
- their newest entry in **Steam's own activity feed** (achievement, first time playing a game,
  wishlist), e.g. `Achievement in Onimusha   ·   3 h ago`
- the **last game Center saw them in**, while they are not in one now: `Played X   ·   yesterday`
- offline, when newer: `Last online 20 min ago` (what Center saw)

**One screen, two columns (0.2.45, user):** friends on the left, Steam's feed on the right - one
heading per day, avatar, name in the status colour, what happened, and for achievements icon and
name, newest 60 entries. D-pad left/right switches the column, up/down moves in it, only the active
column shows a cursor. A chats with the person on the selected row (in the feed: whoever the entry is
about), B closes. In 0.2.44 the feed was a second screen behind X, which the user found bolted on.

⚠️ The screen adds two ColumnDefinitions to `LibraryRoot`, which every other library screen uses
without columns. `CloseFriends` and `LeaveLibrary` clear them (`ClearFriendsColumns`); a new way out
of this screen has to do the same or the next screen renders squeezed into the left column.

**Loading (0.2.45):** right after a boot Steam answers "signed in, 0 friends" for a while, which showed
as "0 of 0 online". An empty answer now counts as not loaded: the corner shows a spinner and "Steam
friends" without the RT cap, re-reads every 5 s, and gives up after 12 empty answers (~1 min) so a
signed-out Steam does not spin forever.

### Steam's feed is on disk

`<Steam>\userdata\<accountId>\config\librarycache\0.json` - found by searching the disk for two
achievement names from the user's Steam activity page. A JSON array of `[name, {version, data}]`:

| part | content |
|---|---|
| `usernews` | base64 **protobuf** messages, one per event (266 here, back to July, not in time order) |
| `achievementmap` | a JSON **string**: `[[appid, [[apiName, {strName, strDescription, strImage, bHidden}]]]]`, in the client's language |
| `gameactivity` | empty here |

Message fields: 1 type, 2 unix time, 3 SteamID (fixed64), 5 game id (fixed64), 8 achievement API
names (repeated). **The types are Steam's own EUserNewsType**, read from its UI code under
`steamui\`: `AchievementUnlocked=2`, `AddedGameToWishlist=9`, `PlayedGameFirstTime=30`. Type 3 also
occurs (with field 13 = an appid) and is not shown - its meaning was not found.

**Checked against the user's screenshot of Steam's activity page:** friend A / Onimusha /
"Qualität über Quantität" today, friend B / Persona 3 Reload / "Besonderer Gast" plus a
hidden one yesterday, friend A / Onimusha / two hidden ones on 8 September - all present, same
names, same hidden flags.

⚠️ **It is a cache.** Steam writes it when its library home page loads the feed. How often that
happens with the page closed is NOT measured. The user's own events are in it too and are dropped.
Hidden achievements stay hidden ("?"), as on Steam's page.

### What Center remembers itself

`%LOCALAPPDATA%\ClawTweaks\Center\steam-friends-seen.json`: per friend, when Center last saw them
online and the last game it saw them in. Updated on every refresh, written at most every two minutes
unless the game changed. It only knows the hours Center was polling.

### Rich presence - tried, nothing

`GetFriendRichPresenceKeyCount` (slot 46) returned **0** for a friend who was in a game, also after
`RequestFriendRichPresence` (slot 48) and five seconds of waiting. Either the slots are wrong or that
game sets none - not separable from here. Not built; the line shows the game name instead.

---

## 2. Where the data comes from

`ISteamFriends` from **`steamclient64.dll` in Steam's own folder**. Center ships no Steam binary -
no `steam_api64.dll`, no `steam_appid.txt`.

| Call | vtable slot |
|---|---|
| `CreateInterface("SteamClient020")` | export |
| `CreateSteamPipe` / `BReleaseSteamPipe` / `ConnectToGlobalUser` / `ReleaseUser` | 0 / 1 / 2 / 4 |
| `GetISteamFriends(user, pipe, "SteamFriends017")` | 8 |
| `GetFriendCount` / `GetFriendByIndex` / `GetFriendPersonaState` / `GetFriendPersonaName` / `GetFriendGamePlayed` / `GetPlayerNickname` | 3 / 4 / 6 / 7 / 8 / 11 |

`GetFriendByIndex` returns a `CSteamID`, a class - MSVC returns it through a hidden pointer right
after `this`. The delegate says so (`out ulong` as the second argument).

Names of games come from `appinfo.vdf` (`SteamOwned.NamesFor`, any type, cached per appid for the
session). Avatars come from the `friends` block of `localconfig.vdf` - the hash there is the CDN
name: `https://avatars.steamstatic.com/<hash>_medium.jpg`.

---

## 3. ⚠️ No app id - measured, and it is the whole reason this is shippable

`SteamAPI_Init` registers the process as a running game, so every friend would see the user
"playing" something for as long as Center is open. Connecting to the client **without** an app id
does not.

**Measured 2026-09-11:** a probe held the connection for three minutes. The user's status stayed
"Online" in the desktop client and in the Steam phone app.

**Never add `SteamAppId`, `SteamGameId` or a `steam_appid.txt`.** The reader clears both variables
before loading the DLL.

---

## 4. Why the read runs in a child process

`SteamFriends.ReadAsync` starts **a copy of Center** with `--steam-friends`. That copy prints one
JSON object and exits. **`Program.Main`** handles the argument before the splash screen, Velopack,
the instance gate and every window.

⚠️ **Why Center has its own `Main` now.** With the splash as a `SplashScreen` build item, WPF's
GENERATED entry point shows it before any of our code runs - so every refresh flashed the splash
over the library (seen on the device with 0.2.42). The splash is a plain `Resource` and
`Program.Main` shows it only for a real start (`<StartupObject>` in the csproj). Turning it back into
a `SplashScreen` item brings the flash back.

1. **A loaded DLL stays loaded.** Center runs for days; holding `steamclient64.dll` would lock the
   file when Steam wants to update it.
2. **The vtable is Valve's.** A moved method is an access violation - that should cost one refresh,
   not Center.
3. **Nothing of Steam's threads stays behind** between reads.

Measured: **0.95 s** per read on the Claw, exit code 0.

**Timeouts and load:** the child is killed after 8 s. The library polls every **30 s** on the shelf
and every **10 s** with the list open, only while the library is the view and the window is visible,
and not on ROMs or Misc. Failures are logged **once** until a read succeeds again (`[SteamFriends]`
in the install log).

---

## 5. TODO (user, 2026-09-11)

- **Hide the tab strip while the friends screen is open.** The library tabs stay visible above the two
  columns, and LB/RB are blocked there anyway (`LaunchOverlayOpen` includes `_friendsOpen`), so the
  strip shows navigation that does not apply. The model is the launch prompt: `RefreshTabStrip`
  collapses the strip while `LaunchPromptOwnsScreen` is true - adding `_friendsOpen` there is the
  obvious form. Check that the corner count comes back when the screen closes (`CloseFriends` already
  calls `RefreshTabStrip`).

## 6. State and history (for picking this up cold)

| Center | What changed | Seen on the device |
|---|---|---|
| 0.2.42 | friends corner, list, chat, Big Picture on LT/Y, info button removed | yes - count, game name, chat |
| 0.2.43 | Big Picture removed; own `Program.Main` so the reader does not flash the splash | yes (installed with 0.2.44) |
| 0.2.44 | Steam's activity feed from `0.json`, last-seen store, green/yellow colours, feed behind X | yes - activity visible, "0 of 0" while loading, feed felt bolted on |
| 0.2.45 | two columns (friends / activity), loading spinner, X toggle removed | yes - "looks very good" |

Setups built for these runs: `ClawTweaksInstaller\Output\ClawTweaks_0.3.1.157` .. `0.3.1.160_Setup.exe`
in the private repo, each with the Center version above (checked in the build log's `Center :` line).

The two probes that decided the design (no app id keeps the status "Online"; rich presence returns
nothing) were throwaway programs and are not in any repo - their results are §3 and §1b.

## 7. Not verified yet

- **Chat over a full-screen Center.** It opened on the device; whether it can land behind a
  borderless full-screen Center has not been checked separately.
- **The splash fix (0.2.43)** is built, not yet seen on the device.
- **A Steam update while a read runs.** The child holds the DLL for about a second.
- **Non-Steam games.** Shown as "In a non-Steam game" - no appid to name them by.

## Files

| File | Role |
|---|---|
| `Library/SteamFriends.cs` | the child-side reader, the process runner, names, avatars, sorting, chat |
| `Library/SteamFriendActivity.cs` | Steam's feed from `0.json` (protobuf + achievementmap), and what Center saw |
| `CenterMenuWindow.Friends.cs` | the corner, polling, the list, navigation, the footer |
| `CenterMenuWindow.Library.cs` | hooks: overlay routing, RT, poll start/stop, info button removed |
| `Program.cs` | `--steam-friends` before the splash and anything else |
| `Library/SteamOwned.cs` | `NamesFor` for any appid |
