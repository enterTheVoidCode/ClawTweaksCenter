using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClawTweaksCenter.Library
{
    public enum FriendActivityKind
    {
        Achievement,
        FirstPlayed,
        // Wishlist (type 9) is deliberately not decoded. The event carries only the appid, and a
        // wishlisted game is almost never in appinfo.vdf (measured 2026-09-15: 1 of 11 resolved),
        // so the row would read "Added 3681610 to the wishlist". Naming it needs the Store API;
        // the user chose to drop the events instead.
    }

    public sealed class FriendAchievement
    {
        public string Name;
        public string Description;
        public string IconUrl;
        public bool Hidden;
    }

    public sealed class FriendActivity
    {
        /// <summary>The friend's key - see <see cref="SteamFriend.SteamId"/> (an XUID for Xbox).</summary>
        public ulong SteamId;
        public GameStore Store = GameStore.Steam;
        public DateTime When;
        public FriendActivityKind Kind;
        public int AppId;
        public string GameName;
        public List<FriendAchievement> Achievements = new List<FriendAchievement>();
    }

    /// <summary>
    /// Steam's own "friend activity" feed, read from the cache the Steam client keeps on disk.
    ///
    /// -- Where it is (found 2026-09-11) ------------------------------------------------------------
    ///   &lt;Steam&gt;\userdata\&lt;accountId&gt;\config\librarycache\0.json
    /// A JSON array of [name, {version, data}] pairs. Two of them matter:
    ///   usernews        base64 protobuf messages, one per event
    ///   achievementmap  per appid, the achievement names, descriptions and icons those events use,
    ///                   already in the Steam client's language
    /// Checked against the user's own Steam activity page: every entry on it was in the file, with
    /// the same achievement names and the same hidden ones.
    ///
    /// -- The message ---------------------------------------------------------------------------------
    /// field 1 varint    type (Steam's EUserNewsType, from its own UI code: 2 AchievementUnlocked,
    ///                   30 PlayedGameFirstTime; 9 AddedGameToWishlist is dropped, see the enum)
    /// field 2 varint    unix time
    /// field 3 fixed64   the friend's SteamID
    /// field 5 fixed64   game id (low 24 bits = appid)
    /// field 8 string    achievement API names, repeated
    ///
    /// ⚠️ IT IS A CACHE. Steam writes it when its library home page loads the feed; how often that
    /// happens without the page being open is not measured. The file carries the user's OWN events
    /// too - they are dropped here. Entries in the file are not in time order.
    ///
    /// -- Live since 2026-10-02 ----------------------------------------------------------------------
    /// The file is the FALLBACK now. While Steam runs, the friends reader asks the client for the
    /// same feed (UserNews.GetUserNews#1, see SteamFriends.ReadNews) and <see cref="FromLive"/>
    /// decodes the answer: the same message, field for field, with the achievement names and icons
    /// in the answer itself instead of in achievementmap.
    /// </summary>
    public static class SteamFriendActivity
    {
        private const ulong IndividualBase = 76561197960265728UL;

        private static readonly object Lock = new object();
        private static string _cachedPath;
        private static DateTime _cachedWrite;
        private static List<FriendActivity> _cached = new List<FriendActivity>();

        /// <summary>Newest first. Never throws; empty when there is no cache.</summary>
        public static List<FriendActivity> Read()
        {
            try
            {
                string steam = SteamSource.SteamPath();
                string account = SteamPlaytime.ActiveAccountId();
                if (steam == null || account == null) return new List<FriendActivity>();

                string path = Path.Combine(steam, "userdata", account, "config", "librarycache", "0.json");
                if (!File.Exists(path)) return new List<FriendActivity>();

                DateTime written = File.GetLastWriteTimeUtc(path);
                lock (Lock)
                {
                    if (path == _cachedPath && written == _cachedWrite) return _cached;
                }

                ulong self = uint.TryParse(account, out uint acc) ? IndividualBase + acc : 0;
                var parsed = Parse(path, self);

                lock (Lock)
                {
                    _cachedPath = path;
                    _cachedWrite = written;
                    _cached = parsed;
                }
                return parsed;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[SteamFriends] activity feed unreadable: " + ex.Message);
                return new List<FriendActivity>();
            }
        }

        private static List<FriendActivity> Parse(string path, ulong self)
        {
            string text;
            // Shared read: Steam may be writing it.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                text = reader.ReadToEnd();

            var news = new List<string>();
            var achievements = new Dictionary<int, Dictionary<string, FriendAchievement>>();

            using (var doc = JsonDocument.Parse(text))
            {
                foreach (var pair in doc.RootElement.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                    string name = pair[0].GetString();
                    if (!pair[1].TryGetProperty("data", out var data)) continue;

                    if (name == "usernews" && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in data.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String) news.Add(e.GetString());
                    }
                    else if (name == "achievementmap" && data.ValueKind == JsonValueKind.String)
                    {
                        ReadAchievementMap(data.GetString(), achievements);
                    }
                }
            }

            var result = new List<FriendActivity>();
            foreach (string b64 in news)
            {
                var item = Decode(b64, achievements);
                if (item == null || item.SteamId == 0 || item.SteamId == self) continue;
                result.Add(item);
            }
            result.Sort((a, b) => b.When.CompareTo(a.When));
            return result;
        }

        /// <summary>
        /// achievementmap's data is itself a JSON string: [[appid, [[apiName, {strName, strDescription,
        /// strImage, bHidden, ...}], ...]], ...].
        /// </summary>
        private static void ReadAchievementMap(string json, Dictionary<int, Dictionary<string, FriendAchievement>> into)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var app in doc.RootElement.EnumerateArray())
                {
                    if (app.ValueKind != JsonValueKind.Array || app.GetArrayLength() < 2) continue;
                    if (!app[0].TryGetInt32(out int appId)) continue;

                    var map = new Dictionary<string, FriendAchievement>(StringComparer.Ordinal);
                    foreach (var entry in app[1].EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
                        string api = entry[0].GetString();
                        var o = entry[1];
                        if (api == null || o.ValueKind != JsonValueKind.Object) continue;

                        map[api] = new FriendAchievement
                        {
                            Name = o.TryGetProperty("strName", out var n) ? n.GetString() : api,
                            Description = o.TryGetProperty("strDescription", out var d) ? d.GetString() : null,
                            IconUrl = o.TryGetProperty("strImage", out var i) ? i.GetString() : null,
                            Hidden = o.TryGetProperty("bHidden", out var h) && h.ValueKind == JsonValueKind.True,
                        };
                    }
                    into[appId] = map;
                }
            }
        }

        private static FriendActivity Decode(string b64, Dictionary<int, Dictionary<string, FriendAchievement>> achievements)
        {
            byte[] b;
            try { b = Convert.FromBase64String(b64); }
            catch { return null; }

            ulong type = 0, time = 0, actor = 0, gameId = 0;
            var apiNames = new List<string>();

            int i = 0;
            try
            {
                while (i < b.Length)
                {
                    ulong key = ReadVarint(b, ref i);
                    int field = (int)(key >> 3);
                    int wire = (int)(key & 7);
                    switch (wire)
                    {
                        case 0:
                            ulong v = ReadVarint(b, ref i);
                            if (field == 1) type = v;
                            else if (field == 2) time = v;
                            break;
                        case 1:
                            ulong f = BitConverter.ToUInt64(b, i);
                            i += 8;
                            if (field == 3) actor = f;
                            else if (field == 5) gameId = f;
                            break;
                        case 2:
                            int len = (int)ReadVarint(b, ref i);
                            if (len < 0 || i + len > b.Length) return null;
                            if (field == 8) apiNames.Add(Encoding.UTF8.GetString(b, i, len));
                            i += len;
                            break;
                        case 5:
                            i += 4;
                            break;
                        default:
                            return null;
                    }
                }
            }
            catch
            {
                return null;
            }

            return Build(type, time, actor, gameId, apiNames, achievements);
        }

        /// <summary>One feed entry from the message's fields - shared by the file and the live answer.</summary>
        private static FriendActivity Build(ulong type, ulong time, ulong actor, ulong gameId, List<string> apiNames,
                                            Dictionary<int, Dictionary<string, FriendAchievement>> achievements)
        {
            FriendActivityKind kind;
            switch (type)
            {
                case 2: kind = FriendActivityKind.Achievement; break;
                case 30: kind = FriendActivityKind.FirstPlayed; break;
                default: return null;
            }
            if (time == 0) return null;

            int appId = (gameId >> 24) == 0 ? (int)(gameId & 0xFFFFFF) : 0;
            if (appId == 0) return null;

            var item = new FriendActivity
            {
                SteamId = actor,
                When = DateTimeOffset.FromUnixTimeSeconds((long)time).LocalDateTime,
                Kind = kind,
                AppId = appId,
            };

            if (kind == FriendActivityKind.Achievement)
            {
                achievements.TryGetValue(appId, out var map);
                foreach (string api in apiNames)
                {
                    if (map != null && map.TryGetValue(api, out var a)) item.Achievements.Add(a);
                    else item.Achievements.Add(new FriendAchievement { Name = api });
                }
                if (item.Achievements.Count == 0) return null;
            }
            return item;
        }

        /// <summary>
        /// The client's own answer to UserNews.GetUserNews, decoded. Newest first, the user's own
        /// events dropped - the same contract as <see cref="Read"/>.
        /// </summary>
        public static List<FriendActivity> FromLive(byte[] response, ulong self)
        {
            SteamKit2.WebUI.Internal.CUserNews_GetUserNews_Response resp;
            using (var ms = new MemoryStream(response))
                resp = ProtoBuf.Serializer.Deserialize<SteamKit2.WebUI.Internal.CUserNews_GetUserNews_Response>(ms);

            var achievements = new Dictionary<int, Dictionary<string, FriendAchievement>>();
            foreach (var app in resp.achievement_display_data ?? new List<SteamKit2.WebUI.Internal.CUserNewsAchievementDisplayData>())
            {
                // MERGED, NOT REPLACED: the answer lists one app several times, each entry with only
                // the achievements of some of its events (measured 2026-10-02: 28 entries for 16
                // apps). Replacing kept the last entry only, and the others showed as a raw API name
                // with a "?" for an icon.
                if (!achievements.TryGetValue((int)app.appid, out var map))
                {
                    map = new Dictionary<string, FriendAchievement>(StringComparer.Ordinal);
                    achievements[(int)app.appid] = map;
                }
                foreach (var a in app.achievements ?? new List<SteamKit2.WebUI.Internal.CUserNewsAchievementDisplayData_CAchievement>())
                {
                    if (string.IsNullOrEmpty(a.name)) continue;
                    map[a.name] = new FriendAchievement
                    {
                        Name = string.IsNullOrEmpty(a.display_name) ? a.name : a.display_name,
                        Description = a.display_description,
                        IconUrl = IconUrl((int)app.appid, a.icon),
                        Hidden = a.hidden,
                    };
                }
            }

            var result = new List<FriendActivity>();
            foreach (var e in resp.news ?? new List<SteamKit2.WebUI.Internal.CUserNews_Event>())
            {
                var item = Build(e.eventtype, e.eventtime, e.steamid_actor, e.gameid,
                                 e.achievement_names ?? new List<string>(), achievements);
                if (item == null || item.SteamId == 0 || item.SteamId == self) continue;
                result.Add(item);
            }
            result.Sort((a, b) => b.When.CompareTo(a.When));
            return result;
        }

        /// <summary>
        /// The file carries a full URL; the live answer may carry only the file name. Both end up as
        /// the URL achievementmap uses, which is the form Steam itself writes.
        /// </summary>
        private static string IconUrl(int appId, string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
            return "https://shared.steamstatic.com/community_assets/images/apps/" + appId + "/" + icon;
        }

        private static ulong ReadVarint(byte[] b, ref int i)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte x = b[i++];
                result |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) return result;
                shift += 7;
                if (shift > 63) throw new FormatException("varint too long");
            }
        }
    }

    /// <summary>What Center itself last saw of a friend - kept only while Center was polling.</summary>
    public sealed class FriendSeen
    {
        public DateTime? LastOnlineUtc { get; set; }
        public int LastGameAppId { get; set; }
        public string LastGameName { get; set; }
        public DateTime? LastGameUtc { get; set; }
    }

    /// <summary>
    /// The part of "last activity" Steam's feed does not carry: when a friend was last online and
    /// what they last played. Center notes it on every friends refresh and keeps it on disk.
    ///
    /// Honest about its limit: it only knows the hours Center was polling. A friend who played all
    /// night while the device was asleep shows whatever was seen before.
    /// </summary>
    public static class SteamFriendSeen
    {
        /// <summary>Timestamps move on every refresh; the file is written at most this often unless
        /// something other than a timestamp changed.</summary>
        private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(2);

        private static readonly object Lock = new object();
        private static Dictionary<string, FriendSeen> _seen;
        private static DateTime _lastSaveUtc = DateTime.MinValue;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "steam-friends-seen.json");

        public static FriendSeen Get(ulong steamId)
        {
            lock (Lock)
            {
                EnsureLoaded();
                return _seen.TryGetValue(steamId.ToString(), out var s) ? s : null;
            }
        }

        public static void Note(IEnumerable<SteamFriend> friends)
        {
            lock (Lock)
            {
                EnsureLoaded();
                DateTime now = DateTime.UtcNow;
                bool changed = false;

                foreach (var f in friends)
                {
                    string key = f.SteamId.ToString();
                    if (!_seen.TryGetValue(key, out var s))
                    {
                        s = new FriendSeen();
                        _seen[key] = s;
                    }

                    if (f.IsOnline) s.LastOnlineUtc = now;

                    if (f.InGame && (f.AppId > 0 || !string.IsNullOrEmpty(f.GameName)))
                    {
                        if (s.LastGameAppId != f.AppId) changed = true;
                        s.LastGameAppId = f.AppId;
                        s.LastGameName = f.GameName;
                        s.LastGameUtc = now;
                    }
                }

                if (changed || now - _lastSaveUtc >= SaveEvery) Save(now);
            }
        }

        private static void EnsureLoaded()
        {
            if (_seen != null) return;
            try
            {
                _seen = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, FriendSeen>>(File.ReadAllText(FilePath))
                    : null;
            }
            catch { _seen = null; }
            if (_seen == null) _seen = new Dictionary<string, FriendSeen>();
        }

        private static void Save(DateTime now)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_seen));
                File.Move(tmp, FilePath, overwrite: true);
                _lastSaveUtc = now;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[SteamFriends] could not save last-seen: " + ex.Message);
            }
        }
    }
}
