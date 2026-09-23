# Steam (and Xbox) data: what needs a login, what does not

Research round of **2026-09-23**. **Nothing here is built.** The question was: how do we get fresher
Steam data - above all the friend activity feed, which only refreshes when Steam's own library page
loads it - and is an account login worth introducing.

Outcome in one line: **friends and achievements are solvable with a login, the friend activity feed
is not solvable without the running Steam client, and one useful piece needs no login at all.**

Related: `STEAM_Friends.md` (what is built), `STEAM_Achievements.md` (what is built).

---

## 1. Free, no login, no API key - and not used yet

Both measured anonymously on 2026-09-23.

```
IPlayerService/GetGameAchievements/v1?appid=<id>&language=<lang>          200
ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2?gameid=<id>     200
```

`GetGameAchievements` returns, for **any** app, without a key and without Steam running:
`internal_name`, `localized_name`, `localized_desc`, `icon`, `icon_gray`, `hidden` **and
`player_percent_unlocked`** (the world-wide rarity).

That closes two limits that `STEAM_Achievements.md` states outright:

| documented limit today | with this endpoint |
|---|---|
| rarity is missing on most games ("null is not zero") | always present |
| a game with no schema on disk gets a percentage and no list | gets the full list |

**What it cannot answer is which of them the user has.** That is account data, and there are exactly
three sources for it: the local blobs, the running client, or a token. There is no fourth.

---

## 2. What a login would buy

Every endpoint below answered `401` - it exists and only wants a token.

| Endpoint | What it gives |
|---|---|
| `IPlayerService/GetAchievementsProgress` ¹ | account-wide progress for every game, independent of the device it was unlocked on |
| `ISteamUserStats/GetPlayerAchievements` | exact unlock times from the account rather than from this disk |
| `IPlayerService/GetOwnedGames` | the whole library including never-installed games, with playtime |
| `IFriendsListService/GetFriendsList` + `ISteamUser/GetPlayerSummaries` | friends and presence **without Steam running** |
| `IPlayerService/GetFriendsGameplayInfo` | which friends own or play this game - for the game tile |
| `IPlayerService/GetTopAchievementsForGames` | rarest / latest achievements per game |

¹ answers `405` on GET, so it wants POST or `input_json`. **Not measured** - that needs a token.

### The login itself: QR, and SteamKit2 only for the handshake

The Playnite plugin `Mike-Aniki/Steam_Friends_Fullscreen` does exactly this, and the shape is the
part worth copying:

```
SteamKit2.Authentication.BeginAuthSessionViaQRAsync()   // QR on screen, phone app scans it
  -> PollAuthSessionStatusAsync()  -> AccessToken + RefreshToken (JWT)
  -> from here on plain HTTPS against api.steampowered.com?access_token=...
```

**SteamKit2 is used for the handshake only; no CM session stays open.** That avoids the documented
problem that a SteamKit login disconnects the running desktop client
([SteamKit #540](https://github.com/SteamRE/SteamKit/issues/540)).

On a handheld the QR is the right gesture - nothing to type. Center is `net10.0-windows`, so
SteamKit2 3.x fits (it requires .NET 10).

⚠️ That repo carries **no licence**. Readable as a reference, not copyable. The flow itself is
SteamKit2's documented API.

### What a login does NOT buy

The friend activity feed. See the next section.

---

## 3. The friend activity feed: client transport only

Found in the shipped Steam UI (`steamui\chunk~*.js`):

```js
SendMsg("UserNews.GetUserNews#1", ..., { ePrivilege: 1 })
```

| | Fields |
|---|---|
| Request | `count`, `starttime`, `endtime`, `language`, `filterflags`, `filterappid` |
| Event | `eventtype`, `eventtime`, `steamid_actor`, `steamid_target`, `gameid`, `packageid`, `shortcutid`, `achievement_names[]`, `clan_eventid`, `clan_announcementid`, `publishedfileid`, `appids[]`, `event_post_time` |

**These are exactly the fields `SteamFriendActivity` already decodes** out of
`librarycache\0.json` (1, 2, 3, 5, 8). So that cache file is the stored answer of this very call,
and it is written when Steam's library home loads the feed - which is why it goes stale.

**It is not on the public Web API.** Measured:

```
IUserNewsService/GetUserNews/v1            404   (same as an invented service)
IUserGameActivityService/GetActivity/v1    404
IFriendsListService/GetFriendsList/v1      401   (exists, wants a token)
```

Also 404 as `v2`, `v0001`, `IUserNews/...` and `ICommunityService/GetUserNews`. `ePrivilege: 1`
means client transport, and a token does not help.

### The four routes, and what each costs

| Route | live | cost |
|---|---|---|
| **A. local client, `ISteamUnifiedMessages` -> `UserNews.GetUserNews#1`** | yes | same shape as the existing friends child process, same vtable exposure; undocumented |
| **B. SteamKit2 CM session + `SteamUnifiedMessages`** | yes, typed | a second login disconnects the desktop client. **Unmeasured:** whether `PlatformType = MobileApp` avoids that |
| **C. CEF debugging** (`.cef-enable-remote-debugging`, port 8080, `SharedJSContext`) | yes, everything | needs a Steam restart with a flag file, opens an unauthenticated JS-execution port on loopback, breaks with every UI rework. The Decky / Millennium route |
| **D. build the feed ourselves** | no | scrape `steamcommunity.com/profiles/<id>/stats/<app>` per friend per game. What `justin-delano/playnite-friendsachievementfeed-plugin` (MIT) does |

✅ **Route A is reachable.** The probe of 2026-09-23 got a non-null
`STEAMUNIFIEDMESSAGES_INTERFACE_VERSION001` from `GetISteamGenericInterface` (client slot 12) -
**including in the run with no app id at all**. So the feed route does not need the thing that
section 5 rules out.

---

## 4. Local achievements are only fresh for what was just played

Measured on the dev machine, 2026-09-23.

| | |
|---|---|
| stats blobs in `appcache\stats` | 430 |
| **older than 30 days** | **373** |
| games with a `LastPlayed` in `localconfig.vdf` | 158 |
| **of those with no blob at all** | **60** (38 %) |
| `achievement_progress.json` (the fallback) | 5 days old |
| worst single case: app 482400 | played 24.08., blob from 05.07. - 51 days apart |

**Steam writes in batches**: 430 files spread over only 39 days, 83 on one of them, 77 on another.
Same shape as `0.json` for the friends feed - it is one defect in two places.

### The obvious measurement does NOT work

Comparing the newest unlock time inside a blob with the file's mtime: **0 of 47 blobs** were written
within an hour of their newest unlock. That reads like "Steam never writes on unlock" and **that is
a fallacy** - the mtime only shows the LAST write, and a later batch overwrote the trace. Whether
Steam also writes at unlock time is **not answerable from this data**.

⚠️ Opening Steam Big Picture destroys the evidence: four games with September unlocks all carry the
same batch timestamp afterwards.

**What would answer it:** a watcher on `appcache\stats` during one play session with an easy
achievement, and no Big Picture in that session.

---

## 5. The SAM route - measured, and it is off the table

`gibbed/SteamAchievementManager` (Zlib) reads achievements **live from the running client** through
`steamclient64.dll`, no login and no API key. The same plumbing Center already uses for friends.
The question was whether that can be done without announcing a game.

**It cannot.** Measured 2026-09-23 with a throwaway probe:

```
SteamUtils.GetAppID : 1627720  -> matches the app id we asked for
GetNumAchievements  : 53
expected (Steam Web): 53  -> MATCH
RESULT: 6 of 53 unlocked, live from the running client.
        newest unlock: LOP_123 at 2026-09-17 20:13
```

The read works perfectly - names, localised titles, unlock times, and for this game it agrees
exactly with the blob. **And the user's phone showed "playing Lies of P" while it ran.**

### 🔴 This sharpens `STEAM_Friends.md` §3 - the reason there is too narrow

That section says `SteamAPI_Init` with an app id is what makes Steam report a game. **We never
called `SteamAPI_Init`.** Setting the `SteamAppId` environment variable before loading
`steamclient64.dll` and then `ConnectToGlobalUser` is already enough.

The rule "never add `SteamAppId`, `SteamGameId` or a `steam_appid.txt`" is therefore not just
confirmed, it now has a direct measurement behind it instead of a derivation.

### ⚠️ The vtable of ISteamUserStats is OFF BY ONE against the public header

`isteamuserstats.h` starts the interface with `RequestCurrentStats`. **The shipped vtable does not
have it**, so every slot after it moves down by one. Taken from SAM's `ISteamUserStats013` layout,
which is the reference for this interface:

| | public header | shipped |
|---|---|---|
| `GetAchievement` | 6 | **5** |
| `GetAchievementAndUnlockTime` | 9 | **8** |
| `GetAchievementDisplayAttribute` | 12 | **11** |
| `GetNumAchievements` | 14 | **13** |
| `GetAchievementName` | 15 | **14** |

**Getting this wrong does not fail cleanly.** `GetNumAchievements` at the wrong slot returned a
*pointer* read as a count - 629474722 on one run, 2777351618 on the next - and the next call
crashed the process. Two different values for the same call is the signature to look for; it cost
this probe two crashes and one wrong conclusion ("uninitialised memory because there is no app
context" - it was the wrong slot).

`ISteamClient018`'s own layout is unchanged and confirms Center's existing numbers:
`GetISteamFriends` 8, `GetISteamGenericInterface` 12, `GetISteamUserStats` 13.

### The refinement that was considered and dropped

The user's idea: only announce when it is already true.

| Variant | status anyway | costs a visible change? |
|---|---|---|
| **A** - while the game runs, read that same app id | "playing X" | no |
| **B** - right after the game exits | already back to "Online" | **yes**, a flicker |
| **C** - user is invisible or offline | nothing is visible | no, and for **any** app id |

**Dropped (user, 2026-09-23.)** The catch: the variant that is safe helps least where the problem
is. The 373 stale blobs and the 60 played games without one are games that are **not running**, so
there is nothing to hide behind. Variant A would refresh exactly the game that is syncing anyway -
its real value would be a live "achievement unlocked" card during play, which is a feature, not a
freshness fix.

**Unmeasured, and would have to be, before anyone picks this up again:** whether a second
connection with the same app id works at all while the game holds the context, whether it
duplicates playtime on the account, and whether invisible mode really hides the game.

---

## 6. 🔴 The profile XML is NOT a live instrument

For the probe above, the observer was
`https://steamcommunity.com/profiles/<id64>/?xml=1` - anonymous, public, the outside view.

**It reported `onlineState=online, inGameInfo=False` eleven times in a row while the phone showed
"playing Lies of P".** It is cached. The driver printed "no game was announced" from it, and that
verdict was wrong.

⚠️ **Two lessons, both paid for in this session:**

1. **An observer needs a control cell.** A measurement device that has only ever said "no" cannot be
   told apart from a broken one. Start a real game once and confirm the observer shows it, *before*
   believing a negative.
2. **Check the exit code before printing a verdict.** The first stage-2 run died of an access
   violation in its first second; the 30-second status poll therefore measured nothing, and the
   driver still printed a verdict. Same class as "a script that discards stderr cannot tell a failed
   write from a successful one".

Also a parsing trap on the same file: the first `<gameName>` in the document belongs to
`<mostPlayedGames>`, not to the current state. Only read it inside `<inGameInfo>`.

---

## 7. Prior art - what exists and what it actually reads

| Project | Licence | Source | Login? | reads `appcache\stats`? |
|---|---|---|---|---|
| SuccessStory (Playnite) | MIT | Web API key **or** HTML scraping of the community stats page | yes | no |
| playnite-friendsachievementfeed | MIT | `steamLoginSecure` cookie + the same page, per friend per game | yes | no |
| Steam_Friends_Fullscreen | none | QR -> token -> Web API | yes | no |
| **gibbed/SteamAchievementManager** | **Zlib** | `steamclient.dll`, live | no (Steam must run) | no - asks the client |
| SAM-Enhanced (Rust fork) | Zlib | as SAM, plus parsing the binary KV cache | no | yes (per its description) |
| ValveKeyValue (SteamDatabase) | MIT | parser library for `.vdf` / `.acf` / binary KV1 | - | format yes, meaning no |
| Playnite SteamLibrary | MIT | local `.vdf` files | no | no (library only) |

**Nobody in the Playnite ecosystem reads Steam's local stats blobs.** Center's reader is not behind
the field, it is the only one - which is why there was no ready-made building block to adopt.

⚠️ SAM sets `SteamAppId`, so it is not adoptable as-is - see section 5.

🟡 **ValveKeyValue** (MIT, actively maintained) parses exactly the formats Center reads by hand
today. No reason to swap now; it is the replacement if the hand-written reader ever trips over a
Steam change.

---

## 8. Xbox - possible, clearly more expensive

Chain, from Playnite's `XboxAccountClient.cs` (MIT) and `OpenXbox/xbox-webapi-python`:

```
login.live.com/oauth20_authorize.srf   (WebView, public client id, redirect oauth20_desktop.srf)
  -> user.auth.xboxlive.com/user/authenticate
  -> xsts.auth.xboxlive.com/xsts/authorize        -> XBL3.0 token
  -> peoplehub.xboxlive.com     friends + presenceDetail
     userpresence.xboxlive.com  status
     titlehub / userstats       titles, achievements
```

No QR - a browser login in a window, which is the worse gesture on a handheld - and the token is
short-lived, so the refresh has to be built. **This shares no code with the Steam path.** Its own
undertaking, not an add-on.

---

## 9. Open, and not decided

1. **Do we introduce a login at all?** The honest pitch is one sentence: *"sign in so your
   achievements are right even when you played on the PC"*. It is true and measurable.
2. **If a token exists, does the account become the source of truth and the disk the offline
   fallback?** `STEAM_Achievements.md` warns against swapping sources without measuring (5 of 84
   games disagreed, unexplained). That warning stands.
3. **The two free endpoints of section 1** - rarity and the schema for games with no local blob.
   They cost no login and no key, and they are the smallest useful step out of this whole round.
4. **The friend feed** stays on route A or B whatever is decided about a login.
