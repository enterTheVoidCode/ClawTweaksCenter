using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// The signed-in Xbox account's friends and their activity, in the shapes the Steam friends
    /// screen already draws (<see cref="SteamFriend"/>, <see cref="FriendActivity"/>) - so the screen
    /// shows one list and one feed for both networks (user, 2026-10-03). Measured the same day, see
    /// Doku\ACHIEVEMENTS_Plan.md "Xbox friends".
    ///
    ///   peoplehub.xboxlive.com .../people/social/decoration/presencedetail   the list with presence,
    ///       picture and last seen (contract 3)
    ///   avty.xboxlive.com .../activity/People/People/Feed                     the friends' unlocks,
    ///       dated, one call for everybody (contract 13)
    ///
    /// Asked at most every <see cref="ReadEvery"/>; between those the last answer is handed back,
    /// so the friends screen's own 10-second poll costs Xbox nothing.
    /// </summary>
    public static class XboxFriends
    {
        private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(60);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);

        private static List<SteamFriend> _friends = new List<SteamFriend>();
        private static List<FriendActivity> _activity = new List<FriendActivity>();
        private static DateTime _readUtc = DateTime.MinValue;

        public static void Clear()
        {
            _friends = new List<SteamFriend>();
            _activity = new List<FriendActivity>();
            _readUtc = DateTime.MinValue;
        }

        /// <summary>The friends and the feed, newest first. Empty when signed out. Never throws.</summary>
        public static async Task<(List<SteamFriend> friends, List<FriendActivity> activity)> ReadAsync(CancellationToken ct)
        {
            if (!XboxAccount.IsSignedIn) return (new List<SteamFriend>(), new List<FriendActivity>());
            await Lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow - _readUtc < ReadEvery) return (_friends, _activity);
                _readUtc = DateTime.UtcNow;

                string auth = await XboxAccount.GetAuthHeaderAsync(ct).ConfigureAwait(false);
                string me = XboxAccount.Xuid;
                if (auth == null) return (_friends, _activity);

                var friends = await ReadFriendsAsync(auth, ct).ConfigureAwait(false);
                if (friends != null) _friends = friends;
                var activity = await ReadFeedAsync(auth, me, ct).ConfigureAwait(false);
                if (activity != null) _activity = activity;

                // Each friend's newest feed entry, as the Steam reader does - the row's activity
                // line and the sort both read it.
                foreach (var f in _friends)
                    f.LatestActivity = _activity.FirstOrDefault(a => a.SteamId == f.SteamId);
                return (_friends, _activity);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[XboxFriends] read failed: " + ex.GetType().Name + ": " + ex.Message);
                return (_friends, _activity);
            }
            finally { Lock.Release(); }
        }

        private static async Task<List<SteamFriend>> ReadFriendsAsync(string auth, CancellationToken ct)
        {
            using var doc = await GetAsync("https://peoplehub.xboxlive.com/users/me/people/social/decoration/presencedetail", "3", auth, ct).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Array) return null;

            var list = new List<SteamFriend>();
            foreach (var p in people.EnumerateArray())
            {
                if (!ulong.TryParse(Str(p, "xuid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong xuid)) continue;
                // A one-way follow is not a friend: only people who follow back are listed.
                if (!(p.TryGetProperty("isFollowingCaller", out var fc) && fc.ValueKind == JsonValueKind.True)) continue;

                var f = new SteamFriend
                {
                    SteamId = xuid,
                    Store = GameStore.Xbox,
                    Name = Str(p, "modernGamertag") ?? Str(p, "gamertag") ?? Str(p, "displayName") ?? "Xbox",
                    AvatarUrl = Picture(Str(p, "displayPicRaw")),
                };

                string state = Str(p, "presenceState");
                f.State = state == "Online" ? SteamPersonaState.Online
                        : state == "Away" ? SteamPersonaState.Away
                        : SteamPersonaState.Offline;

                // In a game: the active detail that is a game. Its PresenceText is the game's name
                // ("Halo Infinite"), RichPresenceText what the game itself says, when it says anything.
                if (f.State != SteamPersonaState.Offline && p.TryGetProperty("presenceDetails", out var pd) && pd.ValueKind == JsonValueKind.Array)
                    foreach (var d in pd.EnumerateArray())
                    {
                        if (!(d.TryGetProperty("IsGame", out var g) && g.ValueKind == JsonValueKind.True)) continue;
                        if (Str(d, "State") is string ds && !string.Equals(ds, "Active", StringComparison.OrdinalIgnoreCase)) continue;
                        f.InGame = true;
                        f.GameName = Str(d, "PresenceText");
                        break;
                    }

                // Xbox says when it last saw them - the "Last online" line the Steam rows get from
                // what Center itself saw.
                if (p.TryGetProperty("lastSeenDateTimeUtc", out var ls) && ls.ValueKind == JsonValueKind.String && ls.TryGetDateTime(out var seen))
                    f.Seen = new FriendSeen { LastOnlineUtc = seen.ToUniversalTime() };

                list.Add(f);
            }
            return list;
        }

        private static async Task<List<FriendActivity>> ReadFeedAsync(string auth, string me, CancellationToken ct)
        {
            using var doc = await GetAsync("https://avty.xboxlive.com/users/me/activity/People/People/Feed?excludeTypes=Played&numItems=50", "13", auth, ct).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("activityItems", out var items) || items.ValueKind != JsonValueKind.Array) return null;

            var feed = new List<FriendActivity>();
            foreach (var i in items.EnumerateArray())
            {
                if (Str(i, "activityItemType") != "Achievement") continue;
                string user = Str(i, "userXuid");
                if (user == null || user == me || !ulong.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out ulong xuid)) continue;
                if (!(i.TryGetProperty("date", out var dv) && dv.ValueKind == JsonValueKind.String && dv.TryGetDateTime(out var when))) continue;

                var ach = new FriendAchievement
                {
                    Name = Str(i, "achievementName"),
                    Description = Str(i, "achievementDescription"),
                    IconUrl = Str(i, "achievementIcon"),
                    Hidden = i.TryGetProperty("isSecret", out var sc) && sc.ValueKind == JsonValueKind.True,
                };
                string game = Str(i, "contentTitle");
                string titleId = i.TryGetProperty("titleId", out var tid) ? tid.ToString() : null;
                DateTime local = when.ToLocalTime();

                // Unlocks in the same game by the same friend within an hour are ONE entry with
                // several icons - the way Steam's feed reports a session's unlocks.
                var last = feed.LastOrDefault();
                if (last != null && last.SteamId == xuid && last.GameName == game
                    && Math.Abs((last.When - local).TotalMinutes) <= 60)
                {
                    last.Achievements.Add(ach);
                    continue;
                }
                feed.Add(new FriendActivity
                {
                    SteamId = xuid,
                    Store = GameStore.Xbox,
                    When = local,
                    Kind = FriendActivityKind.Achievement,
                    GameName = game ?? titleId,
                    Achievements = new List<FriendAchievement> { ach },
                });
            }
            return feed.OrderByDescending(a => a.When).ToList();
        }

        /// <summary>The gamerpic at a size worth decoding - the raw link serves the full image.</summary>
        private static string Picture(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw + (raw.Contains("?") ? "&" : "?") + "w=128&h=128";
        }

        private static async Task<JsonDocument> GetAsync(string url, string contract, string auth, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            req.Headers.Add("x-xbl-contract-version", contract);
            req.Headers.Add("Accept-Language", Core.Loc.Current == Core.UiLanguage.German ? "de-DE" : "en-US");
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Core.InstallLog.Write("[XboxFriends] " + new Uri(url).Host + " answered " + (int)res.StatusCode);
                return null;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
