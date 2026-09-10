# Steam achievements in Center

Built 2026-09-09. Center shows how far along a Steam game is and what was unlocked in it, read from
Steam's own cache on the machine. **No account, no API key, and no network for any of the numbers** —
only the icons come off the CDN.

Every figure below was measured on the development machine (one Steam account, 422 games with an
achievement schema on disk). Where something is a guess, it says so.

---

## 1. What is on screen

**Moved on 2026-09-10** on the user's call — see §10 for what was there before and why it changed.

| Where | What |
|---|---|
| Library, in the line under the title | `Steam · 47 h · 62 GB · Last played 3 Sep 2026 · **19%**` |
| Launch screen (the one A opens), under the cover | `Achievements 11 / 57 · 19 %`, the **last two unlocked**, and a row into the list |
| Game menu (Start button) | the row *All achievements…* — **and nothing above the rows** |
| Either row | Every achievement: **unlocked newest first, then the ones still to go** |

The percentage is left out entirely for a game that has no achievements. `0%` on a game that never
had any is a claim about the player, and it is the wrong one — the same rule the rest of that line
already follows for playtime and dates.

---

## 2. Where Steam keeps it

Three candidate files, all under the Steam install. They are **not** interchangeable:

| File | Games here | Written when |
|---|---|---|
| `appcache\stats\UserGameStatsSchema_<appid>.bin` | **422** | the schema is fetched |
| `appcache\stats\UserGameStats_<accountId>_<appid>.bin` | **422** | a game **syncs its stats** |
| `userdata\<acc>\config\librarycache\<appid>.json` | 153 | the Steam UI **renders that game's page** |
| `userdata\<acc>\config\librarycache\achievement_progress.json` | 180 | refreshed in batches |

**Center reads the two `.bin` blobs.** They are the widest *and* the freshest for the case that
matters most — something unlocked on this device minutes ago. The librarycache JSON only refreshes
when somebody opens that game's page in Steam, and on this machine its write times spanned fourteen
months.

`achievement_progress.json` is kept as a **fallback for games with no schema on disk** (96 of them
here). Those get a percentage and no detail list, which is better than a blank.

### The blob format

Both are **binary KeyValues** — a different format from the text VDF that `ValveKeyValue` reads
elsewhere in Center. A type byte, a null-terminated UTF-8 key, a payload sized by the type, and
`0x08` closes a node.

```
UserGameStatsSchema_1091500.bin
  1091500 -> stats -> "1" -> bits -> "0"
      name      "TheFool"
      display -> name -> german     "Der Narr"
      display -> desc -> german     "Werde zum Söldner."
      display -> icon      "<sha1>.jpg"
      display -> icon_gray "<sha1>.jpg"
      display -> hidden    1

UserGameStats_1383763160_1091500.bin
  cache -> "1"
      data              0x0000613F        BITFIELD of unlocked bits in this block
      AchievementTimes -> "0"  1734901826  unix seconds
```

A game's **total** is the number of bits across every stat block that has a `bits` child. The
**unlocked** count is the population count of the `data` words. `AchievementTimes` supplies the date.

Twenty languages sit in every schema, so the German titles above cost nothing extra — Center picks
the one the UI is running in and falls back through English.

### Why the reader is hand-written

`ValveKeyValue` has a `KeyValues1Binary` mode, but its framing expectations (trailing app ids,
terminators) come from `appinfo.vdf`, and these blobs are a different shape. The format is fifty
lines and was verified byte for byte against both file kinds. See `Library/SteamAchievements.cs`.

---

## 3. What was verified

Of the 84 games present in **both** the blobs and Steam's own summary file, **79 matched exactly**
on unlocked and total.

⚠️ **The five that did not match are NOT explained.** Four had a higher total locally (extra bits in
the schema that Steam's own figure does not count — DLC or withdrawn achievements is the guess, and
it is only a guess). One had a higher *unlocked* count locally, because Steam's summary was simply
older. Do not "fix" this by switching the primary source without measuring first.

The shipped class was then driven against the real cache, not just compiled:

```
Cyberpunk 2077     1091500    11/57    19%
  2025-10-03 14:41  Flucht aus Dogtown - Rette Präsidentin Myers.
  2025-04-12 20:46  Die Welt - Schließe die Hauptgeschichte ab.
Hollow Knight SS   1030300     6/52    11%
Half-Life 2        220         0/69     0%
appid 7            (Steam client)  NO DATA, correctly rejected
```

---

## 4. Icons come from the CDN

**Steam does not cache achievement icons on disk.** Checked: three known hashes from the schema
appear nowhere under the Steam folder, and the 5266 jpgs in `appcache\librarycache` are all cover
art and headers.

The schema carries the bare hash. The URL is built as

```
https://shared.steamstatic.com/community_assets/images/apps/<appid>/<sha1>.jpg
```

which is the exact form **Steam itself writes** into `librarycache\<appid>.json` — copied, not
guessed, and then verified against a hash taken from the schema, which is what proves the two files
agree.

Loading goes through `GameArt.LoadRemoteAsync`, **never `LoadAsync`**: an http source handed to
`BitmapImage.UriSource` downloads asynchronously and then makes `Freeze` throw, which is how the art
picker once ended up with a grid of grey tiles.

**Offline costs a picture and never a line of text.** Every row draws a card behind the icon whether
or not the image arrives, so the layout does not shift when one lands and a missing icon does not
read as a broken row.

---

## 5. Decisions worth knowing before changing this

**Percent is not rounded the usual way at either end.** 699 of 700 would round to 100 % and tell
somebody hunting their last achievement that they are finished; 1 of 700 would round to 0 % and read
as "none". So 100 is reserved for actually finished, 0 for actually nothing, and everything between
is floored into 1..99.

**Newest first, although the screen is chronological.** What somebody opens this for is what they
just did. An unlocked achievement with no timestamp sorts to the end — it is genuinely unlocked, it
just cannot claim a place in the order — and it draws **no date at all** rather than a dash.

**The bitfield decides what is unlocked, not the presence of a timestamp.** They agreed on every game
measured here; when they ever disagree, the bitfield is the record Steam itself reads back.

**The parse is lazy and cached per game.** The library refresh only drops the cache — a full pass
would mean opening two binary blobs for every one of several hundred games on a refresh that has to
stay responsive.

**One source, not two.** The library percentage and the menu's `11 / 57` come from the same call.
Two sources answering "how far along is this game" with different numbers on different screens is a
failure this project has paid for more than once.

**The row icon is `U+EB95`, an award certificate — chosen by looking at it.** Segoe Fluent Icons has
no trophy or medal anywhere in E7xx/E8xx/E9xx/EAxx/EBxx (all five blocks were rendered and
inspected). A codepoint that merely *exists* still draws whatever glyph happens to live there. The
first attempt landed on `U+E735`, which is the filled favourite star the row above already uses.

---

## 6. Measured and deliberately left out

**Global rarity** (`flAchieved`, "3.4 % of players have this") is real and sits in
`librarycache\<appid>.json`. It is **not** used, because that file only covers 153 games and only
lists a handful of highlights plus twelve unachieved entries — so rarity would appear on some rows
and not others, in the same list. A detail that comes and goes is the shape people read as broken.
It can be added later as a strictly additive enrichment; it is a different fact from the
unlocked count, so it cannot contradict anything.

**Locked achievements** are parsed (the model has them, with grey icons and the `hidden` flag) but
the screen shows only unlocked ones, which is what was asked for. Showing the rest is a filter on an
existing list, not new plumbing.

---

## 7. Friends and presence — measured, and it is not on disk

Asked for at the same time; **not built**, because the data is not there.

`localconfig.vdf` holds a `friends` block: 79 friends with name, name history and avatar hash. That
is a **static address book**. There is no online status and no current game anywhere in it. The
avatar cache held exactly one file (the user's own). With Steam running, nothing in `config\`,
`userdata\` or `appcache\` was written that carried presence.

Presence lives in `steam.exe`'s memory and travels over Steam's own protocol. Three routes, none of
them free:

1. **Steamworks SDK** (`steam_api64.dll`, `ISteamFriends`) — local, no key, gives status *and*
   `GetFriendGamePlayed`. See below.
2. **Public profile XML** (`steamcommunity.com/profiles/<id64>/?xml=1`) — no key, and the roster is
   already on disk, so the friend ids are known. Costs 79 HTTP requests, works only for public
   profiles, and is scraping.
3. **Launching Steam with `-cef-enable-debugging`** and driving the friends UI over CEF DevTools —
   changes how the user starts Steam. Ruled out.

### The Steamworks SDK idea, in full

This is the only route that gives the real answer, and it is a genuine trade.

**What it would give.** `ISteamFriends` returns the friend list, persona state (online, away, busy,
in-game), rich presence, and `GetFriendGamePlayed` — which is literally "what are they playing right
now". It is a local IPC call into the running Steam client: no web API key, no rate limit, no
scraping, and it works for **private** profiles the way the Steam client itself does.

**What it would cost.**

- **A third-party native DLL in the shipped package.** `steam_api64.dll` is redistributable but it is
  another binary in a package that is already fighting AV heuristics — see the Defender history in
  the helper repo. Center's exe is unsigned; adding an unsigned native dependency is not neutral.
- **An app id, and a `steam_appid.txt` next to the exe.** The SDK refuses to initialise without one.
- **⚠️ Center would appear to every friend as a game the user is playing.** This is the part that
  decides it. `SteamAPI_Init` registers the process as running that app id, so the user's status
  would read "playing <whatever app id we used>" for as long as Center is open — which on a handheld
  is most of the time. Using app id 480 (Spacewar, the SDK sample) makes it worse, not better:
  everyone would see the user permanently playing *Spacewar*.
- **It only works while Steam is running.** Fine for presence, which is meaningless otherwise, but
  it means the feature is absent rather than stale when Steam is closed.
- **It is a hard dependency in one direction only.** Achievements would gain nothing from it — they
  are already complete, offline, and cover more games than the SDK would report without a network
  round trip.

**If it is ever built**, the shape that keeps the cost contained: the SDK lives **behind a switch
that is off by default**, it is initialised only while a friends screen is actually open, and it is
shut down when that screen closes — so the "playing Center" status is bounded to the seconds
somebody is looking at their friends list rather than the whole session. That is a design decision
and needs agreeing before anybody writes it.

**Route 2 is the cheaper experiment** and answers whether the feature is wanted at all: the roster is
already on disk, and one request per friend against a public profile needs nothing new in the
package. Its honest limitation is that it silently shows nothing for private profiles.

---

## 8. Other achievement sources

The longer-term idea is one global achievement summary that also covers RetroAchievements and Xbox.

**Nothing here was investigated.** Whether either can be read without an API key is an open
question, and guessing at it would be worse than saying so. What this implementation does do is
leave room: the model is *game → unlocked/total → list with timestamps*, which a second source can
be placed beside without touching the Steam path.

---

## 9. Confirmed on the device (2026-09-10)

**It works.** The user ran 0.2.34 on the Claw and can see the achievements; the verdict was
"functionally clean, the UI could be better".

So the whole chain is proven end to end - Steam's binary caches, the parse, the localised text, the
percentage in the library line, the panel on the game menu, the full list, and the icons arriving
off the CDN.

### 🟡 Open, and NOT a defect: the presentation

"UI could be better" is the only thing left, and it is deliberately not guessed at here. Nothing
about the layout was fed back specifically, so anybody picking this up should ask what bothered them
rather than redesign on a hunch. The obvious candidates, in the order they would be worth asking
about:

- the three-line block on the game menu competes with the rows below it for the same column width
- the full list is one long flat column; there is no grouping by date and no jump to a month
- 52 px icons at 900 px max width leave a lot of empty middle on a wide row
- locked achievements are parsed but not shown at all, so the list has no sense of what is left

None of these is broken. All of them are choices that were made without a device in front of me.

---

## 10. The 2026-09-10 round: where it sits, and two more figures

Four changes, all on the user's instruction after running 0.2.34 on the device. Three of them close
items §9 had already listed as worth asking about.

### 10.0 Status: built and installed, layout NOT seen yet

Shipped as **0.2.36** and installed on the development Claw the same day, over the existing Velopack
install. **Nobody has looked at the result.** §9's device confirmation covers 0.2.34, which is the
layout this round replaces — so the numbers, the parse and the icons stay proven, and everything
below about where things sit is compiled and reasoned rather than seen.

The four things worth checking first, in the order they would fail:

1. the launch screen with the block present — the cover drops to 34 % of the height for it, and the
   row at the bottom is the thing that goes off-screen if that is still not enough,
2. down and up between Play and the row, and the footer label changing with it,
3. B out of the list landing back on the launch screen rather than in the library,
4. a game with no rarity on disk — the common case — ending its rows after the description with no
   gap where a figure would have been.

### 10.1 Out of the game menu, onto the launch screen

The block used to sit **above the rows in the Start-button menu**. It is now under the cover on the
**launch screen** — the one that asks *Start X?* before A launches it.

**The objection is one the menu could not answer.** Every other thing on that menu is an *action on
the entry*: favourite it, give it a cover, rename it, remove it. A three-line status block over them
pushed the list halfway down the screen for a game nobody opened that menu to read about. The launch
screen is the opposite case — somebody is standing in front of it deciding whether to play, and "you
are three away from the end" is exactly the thing that decides it.

**Two, not three.** That screen already carries the key art, the cover, a headline and up to three
OptiScaler badges. The third line was the one that had to go, and the row underneath is what it
became.

**The row in the game menu stays.** It is an action, so it belongs there; only the status block left.

### 10.2 The launch screen now has exactly one focusable element

⚠️ **That screen had no focus at all, on purpose.** It asks one question with two answers, A and B,
and the standing rule was that anything the stick could land on turns that into a navigation problem.
The achievements row is a deliberate exception, and the reasoning it has to keep clearing:

- **there was no button left that anyone would find.** A is Play, B is Cancel, X is the OptiScaler
  wiki and Y is OptiClick. Start and Select are free, and both already mean something else one screen
  away — a way in on either is a way in nobody discovers.
- **it sits BELOW the whole question**, not between its two answers.
- **the focus starts on Play every single time.** Every path that opens or re-opens a launch prompt
  resets it (`_launchFocus`), so a two-press decision is still two presses for anyone who never
  touches the stick. A screen whose default answer depends on what was on it last time eventually
  launches a game somebody was only reading about.

Down moves onto the row, up moves back, and **the footer label follows the focus** — "Play" over a
highlighted achievements row would be the footer contradicting the screen.

⚠️ **The cover is sized around the block, not independently of it.** There is no ScrollViewer on
this screen, so a cover taking its usual 46 % of the height would push the row off a short window.
With the block present the cover drops to 34 % and caps at 320 px instead of 420. **An unreachable
row is worse than a smaller picture** — and it would be unreachable *silently*.

⚠️ **The list opens with the launch prompt LEFT STANDING.** Clearing it would drop the target, the
cover and the cold-start timer, and B would have to rebuild the screen from the library. Both
overlays are open at once instead, and B knows which way to go back (`_achievementsFromLaunch`).

That made one existing ordering load-bearing: **`RefreshGameMenuActionBar()` now runs BEFORE the
launch branch** in `RefreshLibraryActionBar`. `RenderLibrary` and `MoveLibrarySelection` have always
asked in that order; the action bar was the one funnel asking in the other, and it would have
labelled A "Play" over a screen with no Play on it.

### 10.3 Locked achievements — free, and they were being thrown away

§9 listed "locked achievements are parsed but not shown at all" as an open item. It was more literal
than it sounded: `Build()` has always read every achievement in the game, and picks Steam's second,
**grey** icon (`icon_gray`) for a locked one. `UnlockedFor()` was simply filtering them out.

`AllFor()` returns unlocked newest-first, then the rest. **No second file, no request, no new
parsing.**

- **Locked rows are the same row, dimmed** — grey text, icon at 55 % — rather than a different
  shape. That is what keeps a list of eighty scannable when a third of it is still to go.
- **The locked half keeps schema order**, which is the developer's own and on most games roughly the
  order they are meant to be earned. Sorting it by rarity was considered and not done: that answers a
  different question than this screen asks.
- ⚠️ **A spoiler stays a spoiler while it is locked.** Steam flags these itself and hides their
  text on its own pages. The **name is kept** — Steam keeps it too — and the description is replaced
  with one italic line. Showing it would turn a list somebody opened to see what is left into the one
  thing they were being kept from.

**Consequence for both rows: they are live on "there is a list", not on "something is unlocked".**
A game where nothing has been earned yet is exactly where the list is worth opening, and the old test
greyed the row precisely there.

### 10.4 Rarity — how many players have it — and progress

**Yes, and it is on disk.** `userdata\<id>\config\librarycache\<appid>.json` carries `flAchieved`
per achievement: the percentage of all players who have it. The same file carries `flCurrentProgress`
/ `flMaxProgress` for a counted achievement ("1 / 2 spool fragments"), which is shown on locked rows.

🔴 **IT IS A PARTIAL ANSWER, AND THAT IS THE ONLY THING WORTH KNOWING ABOUT IT.** Measured here:

| | |
|---|---|
| Games with an achievement schema | **422** |
| Games with this file at all | **153** |
| Of those, files carrying EVERY achievement | **3** |

The file is written when the **Steam UI renders that game's page**, not when a game syncs — the same
property that makes `achievement_progress.json` the fallback rather than the source (§2). So most
games have rarity for a handful of achievements and many have it for none.

**Every caller therefore draws nothing rather than a zero.** A row simply ends after its description.
There is no dash and no placeholder: "—" there would read as *nobody has this* rather than as *Steam
never told us*, and a genuinely rare achievement really does sit below 1 %.

**One decimal, trailing zero dropped.** Whole numbers turn every hard achievement in the game into
"0 %", which is the one figure somebody scanning for the rare ones is looking for — 0.4 and 0.04 are
a different afternoon. Common ones lose nothing: 90 stays 90.

#### ⛔ What Steam does NOT have, at all

**Absolute player counts.** Not in any file, not from any API. "How many people got it" exists only
as a percentage. Anybody asked for a headcount has to be told there isn't one.

#### The complete answer exists, over the network, and is deliberately not used

`https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid=<appid>`
— **public, no API key**. Spot-checked against Hollow Knight Silksong on 2026-09-10: **52 of 52**
names, and the four percentages the local file also carried matched to the decimal, so the two are
the same data at different coverage.

It is not called, on the user's decision (*"erstmal nur lokal"*). This class answers from disk and
the icons are the only thing that leaves the machine — that sentence is the first line of this
document, and a complete rarity column is not worth spending it. If it is ever wanted, the shape is
already obvious: fetch once per game when the full list is opened, cache to disk, keep the local file
as the offline answer.

---

## Files

| File | Role |
|---|---|
| `Library/SteamAchievements.cs` | the reader, the binary KV parser, the model and the cache |
| `Library/SteamPlaytime.cs` | now exposes `ActiveAccountId()` — one owner for "which account" |
| `Library/GameLibrary.cs` | drops the achievement cache on each refresh round |
| `CenterMenuWindow.Achievements.cs` | the launch-screen block, the row, and the full list |
| `CenterMenuWindow.GameMenu.cs` | the overlay state, the row, navigation and the footer |
| `CenterMenuWindow.Library.cs` | the percentage in the selected-title line, and the launch screen's one focus |
| `Core/Localization.Tables.cs` | nine strings × four languages |
