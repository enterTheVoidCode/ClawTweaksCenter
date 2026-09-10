# Steam achievements in Center

Built 2026-09-09. Center shows how far along a Steam game is and what was unlocked in it, read from
Steam's own cache on the machine. **No account, no API key, and no network for any of the numbers** —
only the icons come off the CDN.

Every figure below was measured on the development machine (one Steam account, 422 games with an
achievement schema on disk). Where something is a guess, it says so.

---

## 1. What is on screen

| Where | What |
|---|---|
| Library, in the line under the title | `Steam · 47 h · 62 GB · Last played 3 Sep 2026 · **19% achievements**` |
| Game menu (Start button), above the rows | `Achievements 11 / 57 · 19 %` plus the **last three unlocked**, with icon and date |
| Game menu → *All achievements…* | Every unlocked achievement, **newest first**, icon, description, date and time |

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

## Files

| File | Role |
|---|---|
| `Library/SteamAchievements.cs` | the reader, the binary KV parser, the model and the cache |
| `Library/SteamPlaytime.cs` | now exposes `ActiveAccountId()` — one owner for "which account" |
| `Library/GameLibrary.cs` | drops the achievement cache on each refresh round |
| `CenterMenuWindow.Achievements.cs` | the panel and the full list |
| `CenterMenuWindow.GameMenu.cs` | the overlay state, the row, navigation and the footer |
| `CenterMenuWindow.Library.cs` | the percentage in the selected-title line |
| `Core/Localization.Tables.cs` | seven strings × four languages |
