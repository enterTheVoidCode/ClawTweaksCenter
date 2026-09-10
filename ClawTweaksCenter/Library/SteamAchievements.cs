using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClawTweaksCenter.Library
{
    /// <summary>One achievement, already resolved into the language the UI is running in.</summary>
    public sealed class AchievementEntry
    {
        /// <summary>Steam's own API name, e.g. "AllThePresidentsMen". Stable across languages.</summary>
        public string Id;
        public string Name;
        public string Description;
        /// <summary>Absolute CDN url, or null when the schema carries no icon for this one.</summary>
        public string IconUrl;
        public bool Unlocked;
        /// <summary>When it was unlocked. Null is possible EVEN WHEN Unlocked is true - see
        /// <see cref="SteamAchievements"/>: the bitfield and the timestamp map are two different
        /// records and only the bitfield is authoritative.</summary>
        public DateTime? UnlockedAt;
        public bool Hidden;
    }

    /// <summary>How far along one game is.</summary>
    public sealed class AchievementSummary
    {
        public int Unlocked;
        public int Total;

        /// <summary>
        /// A whole number for the library line.
        ///
        /// NEITHER END IS ROUNDED THE NORMAL WAY, and both exceptions are deliberate. 699 of 700
        /// rounds to 100 %, which tells somebody hunting the last one that they are finished; and
        /// 1 of 700 rounds to 0 %, which reads as "none" next to a game that has one. So 100 is
        /// reserved for actually finished, 0 for actually nothing, and everything between is floored
        /// into 1..99.
        /// </summary>
        public int Percent
        {
            get
            {
                if (Total <= 0) return 0;
                if (Unlocked >= Total) return 100;
                if (Unlocked <= 0) return 0;
                int p = (int)Math.Floor(100.0 * Unlocked / Total);
                return Math.Min(99, Math.Max(1, p));
            }
        }
    }

    /// <summary>
    /// Steam achievements, read from Steam's own cache on this machine. No account, no API key, no
    /// network for the numbers - only the icons come off the CDN.
    ///
    /// Steam keeps two binary KeyValues blobs per game next to each other:
    ///
    ///   &lt;Steam&gt;\appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin
    ///       &lt;appid&gt; -> stats -> &lt;statId&gt; -> bits -> &lt;bit&gt;
    ///           name     "AllThePresidentsMen"        the API name
    ///           display -> name -> german            localised title (20 languages)
    ///           display -> desc -> german            localised description
    ///           display -> icon      "&lt;sha1&gt;.jpg"   unlocked icon
    ///           display -> icon_gray "&lt;sha1&gt;.jpg"   locked icon
    ///           display -> hidden    1               spoiler
    ///
    ///   &lt;Steam&gt;\appcache\stats\UserGameStats_&lt;accountId&gt;_&lt;appid&gt;.bin
    ///       cache -> &lt;statId&gt;
    ///           data              0x0000613F         BITFIELD of unlocked bits in this block
    ///           AchievementTimes -> &lt;bit&gt;  1759495289   unix seconds
    ///
    /// A game's achievement count is the number of bits across every stat block that HAS a bits
    /// child; the unlocked count is the population count of the data words. Verified against Steam's
    /// own summary file on this machine: of the 84 games present in both, 79 matched exactly.
    ///
    /// WARNING: THE FIVE THAT DID NOT MATCH ARE NOT EXPLAINED. Four had a higher total locally (extra
    /// bits in the schema that Steam's own figure does not count - DLC or withdrawn achievements is
    /// the guess, and it is only a guess), one had a higher unlocked count locally because Steam's
    /// summary was simply older. Do not "fix" this by preferring the other file without measuring
    /// first: see the source-of-truth note on LoadProgressFile.
    ///
    /// WHY THE BLOBS AND NOT STEAM'S OWN SUMMARY. There are three candidate files and this one wins
    /// on both counts that matter:
    ///
    ///   appcache\stats\*.bin                     422 games here, written when a game SYNCS STATS
    ///   userdata\..\librarycache\&lt;appid&gt;.json    153 games, written when the Steam UI renders that
    ///                                            game's page - stale for anything not looked at
    ///   userdata\..\librarycache\achievement_progress.json
    ///                                            180 games, refreshed in batches
    ///
    /// So the blobs are the widest AND the freshest for the case that matters most: something that
    /// was just unlocked on this device. The summary file is kept as a fallback for games that have
    /// no schema on disk at all (96 of them here) - those get a percentage and no detail list, which
    /// is better than a blank.
    /// </summary>
    public static class SteamAchievements
    {
        /// <summary>Where Steam publishes achievement art. This exact form is the one STEAM ITSELF
        /// writes into librarycache\&lt;appid&gt;.json, so it is copied rather than guessed; verified
        /// against a hash taken from the schema, which is what proves the two files agree.</summary>
        private const string IconBase = "https://shared.steamstatic.com/community_assets/images/apps/";

        private static readonly object Gate = new object();

        /// <summary>appid -&gt; parsed model, built once per game per session.</summary>
        private static readonly Dictionary<string, List<AchievementEntry>> Cache =
            new Dictionary<string, List<AchievementEntry>>(StringComparer.Ordinal);

        /// <summary>appid -&gt; Steam's own unlocked/total, from achievement_progress.json. Fallback
        /// only.</summary>
        private static Dictionary<string, AchievementSummary> _progress;

        private static string _statsDir;
        private static string _accountId;
        private static bool _scanned;

        /// <summary>
        /// Re-reads where Steam is and which account is signed in, and throws the per-game cache away.
        ///
        /// Called from the library refresh, on a background thread. Nothing here touches the UI.
        /// </summary>
        public static void Refresh()
        {
            lock (Gate)
            {
                Cache.Clear();
                _progress = null;
                _scanned = false;
                _statsDir = null;
                _accountId = null;

                try
                {
                    string steam = SteamSource.SteamPath();
                    if (steam != null)
                    {
                        string dir = Path.Combine(steam, "appcache", "stats");
                        if (Directory.Exists(dir)) _statsDir = dir;
                    }

                    _accountId = SteamPlaytime.ActiveAccountId();
                }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("[Achievements] refresh failed: " + ex.Message);
                }
                finally { _scanned = true; }
            }
        }

        /// <summary>
        /// Unlocked / total for a game, or null when this is not a Steam game or Steam knows nothing
        /// about it. Cheap after the first call per game.
        /// </summary>
        public static AchievementSummary SummaryFor(GameEntry g)
        {
            string appId = AppIdOf(g);
            if (appId == null) return null;

            var list = ModelFor(appId);
            if (list != null && list.Count > 0)
                return new AchievementSummary { Total = list.Count, Unlocked = list.Count(e => e.Unlocked) };

            // No schema on disk. Steam's own summary still knows some of these - a percentage with no
            // detail behind it is worth more than nothing, and the detail screen says so itself.
            lock (Gate)
            {
                if (_progress == null) _progress = LoadProgressFile();
                return _progress.TryGetValue(appId, out var s) ? s : null;
            }
        }

        /// <summary>
        /// Everything the user has actually unlocked, NEWEST FIRST.
        ///
        /// ⚠️ THIS, NOT SummaryFor().Unlocked, IS WHAT DECIDES WHETHER THE DETAIL ROW IS LIVE. The
        /// summary falls back to Steam's own progress file for games with no schema on disk, so it
        /// can report a healthy "8 unlocked" for a game this class has no list for - and the row
        /// would open an empty screen.
        ///
        /// Newest first rather than oldest first: the thing somebody opens this for is what they just
        /// did, and a list that opens on a 2019 tutorial achievement makes them scroll to find it.
        ///
        /// Anything unlocked WITHOUT a timestamp sorts to the end - it is genuinely unlocked, it just
        /// cannot claim a place in the order.
        /// </summary>
        public static List<AchievementEntry> UnlockedFor(GameEntry g)
        {
            var list = ModelFor(AppIdOf(g));
            if (list == null) return new List<AchievementEntry>();

            return list.Where(e => e.Unlocked)
                       .OrderByDescending(e => e.UnlockedAt ?? DateTime.MinValue)
                       .ToList();
        }

        /// <summary>The most recent few, for the panel on the game menu.</summary>
        public static List<AchievementEntry> RecentFor(GameEntry g, int count)
        {
            var all = UnlockedFor(g);
            if (all.Count > count) all.RemoveRange(count, all.Count - count);
            return all;
        }

        /// <summary>Steam appid, or null for anything that is not a Steam game.</summary>
        private static string AppIdOf(GameEntry g)
            => g != null && g.Store == GameStore.Steam && !string.IsNullOrEmpty(g.Id) ? g.Id : null;

        #region Model
        private static List<AchievementEntry> ModelFor(string appId)
        {
            if (appId == null) return null;

            lock (Gate)
            {
                if (!_scanned) RefreshLocked();
                if (Cache.TryGetValue(appId, out var cached)) return cached;

                List<AchievementEntry> built = null;
                try { built = Build(appId); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] " + appId + " failed: " + ex.Message); }

                // A null result is cached too. A game with no achievements is the common case, and
                // re-parsing nothing on every cursor move costs the same as parsing something.
                Cache[appId] = built;
                return built;
            }
        }

        /// <summary>The body of <see cref="Refresh"/> for callers that already hold the lock. Calling
        /// Refresh itself from inside the lock would work (it is the same thread) but it clears the
        /// cache, and a lookup that wipes the cache it is reading is a trap for whoever edits this
        /// next.</summary>
        private static void RefreshLocked()
        {
            try
            {
                string steam = SteamSource.SteamPath();
                if (steam != null)
                {
                    string dir = Path.Combine(steam, "appcache", "stats");
                    if (Directory.Exists(dir)) _statsDir = dir;
                }
                _accountId = SteamPlaytime.ActiveAccountId();
            }
            catch (Exception ex) { Core.InstallLog.Write("[Achievements] late refresh failed: " + ex.Message); }
            finally { _scanned = true; }
        }

        private static List<AchievementEntry> Build(string appId)
        {
            if (_statsDir == null) return null;

            string schemaPath = Path.Combine(_statsDir, "UserGameStatsSchema_" + appId + ".bin");
            if (!File.Exists(schemaPath)) return null;

            var schema = ReadKvFile(schemaPath);
            if (schema == null) return null;

            // The root is keyed by appid, but it is taken as "the first child that has stats" rather
            // than by name: one file is one game, and a name lookup would be a second thing that can
            // disagree with the filename we already trusted to find it.
            Dictionary<string, object> root = schema.Values.OfType<Dictionary<string, object>>()
                                                    .FirstOrDefault(n => n.ContainsKey("stats"));
            if (root == null) return null;
            if (!(root["stats"] is Dictionary<string, object> stats)) return null;

            // The user's side is optional: a game whose achievements were never touched has no blob,
            // and that is "nothing unlocked", not an error.
            Dictionary<string, object> userBlocks = null;
            if (_accountId != null)
            {
                string userPath = Path.Combine(_statsDir, "UserGameStats_" + _accountId + "_" + appId + ".bin");
                if (File.Exists(userPath))
                {
                    var user = ReadKvFile(userPath);
                    if (user != null && user.TryGetValue("cache", out object c))
                        userBlocks = c as Dictionary<string, object>;
                }
            }

            string lang = SteamLanguage();
            var result = new List<AchievementEntry>();

            foreach (var stat in stats)
            {
                // A stat block counts as achievements when it HAS bits, not when its "type" field
                // says ACHIEVEMENTS. The type is what the schema calls it; the bits are what we can
                // actually count, and a block with no bits contributes nothing either way.
                if (!(stat.Value is Dictionary<string, object> block)) continue;
                if (!block.TryGetValue("bits", out object rawBits)) continue;
                if (!(rawBits is Dictionary<string, object> bits)) continue;

                int unlockedMask = 0;
                Dictionary<string, object> times = null;
                if (userBlocks != null && userBlocks.TryGetValue(stat.Key, out object rawUser) &&
                    rawUser is Dictionary<string, object> userBlock)
                {
                    unlockedMask = AsInt(userBlock, "data");
                    userBlock.TryGetValue("AchievementTimes", out object rawTimes);
                    times = rawTimes as Dictionary<string, object>;
                }

                foreach (var bit in bits)
                {
                    if (!(bit.Value is Dictionary<string, object> b)) continue;
                    if (!int.TryParse(bit.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bitIndex)) continue;
                    if (bitIndex < 0 || bitIndex > 31) continue;

                    var display = b.TryGetValue("display", out object d) ? d as Dictionary<string, object> : null;

                    // THE BITFIELD DECIDES, not the presence of a timestamp. They agreed on every
                    // game measured here, and when they ever disagree the bitfield is the record
                    // Steam itself reads back.
                    bool unlocked = (unlockedMask & (1 << bitIndex)) != 0;

                    DateTime? at = null;
                    if (unlocked && times != null && times.TryGetValue(bit.Key, out object rawTs) &&
                        rawTs is int ts && ts > 0)
                    {
                        try { at = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().DateTime; }
                        catch (ArgumentOutOfRangeException) { }
                    }

                    string icon = AsString(display, unlocked ? "icon" : "icon_gray");
                    if (string.IsNullOrEmpty(icon)) icon = AsString(display, "icon");

                    result.Add(new AchievementEntry
                    {
                        Id = AsString(b, "name"),
                        Name = Pick(display, "name", lang) ?? AsString(b, "name"),
                        Description = Pick(display, "desc", lang),
                        IconUrl = string.IsNullOrEmpty(icon) ? null : IconBase + appId + "/" + icon,
                        Unlocked = unlocked,
                        UnlockedAt = at,
                        Hidden = display != null && AsInt(display, "hidden") != 0,
                    });
                }
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// The localised string, falling back through English to nothing.
        ///
        /// Nothing is a legitimate answer and is NOT replaced with the API name: a description that
        /// reads "AllThePresidentsMen" is worse than a title with no line under it.
        /// </summary>
        private static string Pick(Dictionary<string, object> display, string key, string lang)
        {
            if (display == null) return null;
            if (!display.TryGetValue(key, out object raw)) return null;
            if (!(raw is Dictionary<string, object> byLang)) return raw as string;

            if (lang != null && byLang.TryGetValue(lang, out object v) && v is string s && s.Length > 0) return s;
            if (byLang.TryGetValue("english", out object e) && e is string es && es.Length > 0) return es;
            return null;
        }

        /// <summary>
        /// Center's language as STEAM spells it. Korean is "koreana" in every Steam schema, which is
        /// the one entry here that cannot be derived from a culture name.
        /// </summary>
        private static string SteamLanguage()
        {
            switch (Core.Loc.Current)
            {
                case Core.UiLanguage.German: return "german";
                case Core.UiLanguage.French: return "french";
                case Core.UiLanguage.Korean: return "koreana";
                case Core.UiLanguage.Spanish: return "spanish";
                default: return "english";
            }
        }
        #endregion

        #region Steam's own summary, fallback only
        /// <summary>
        /// achievement_progress.json - Steam's pre-computed unlocked/total.
        ///
        /// WARNING: READ ONLY FOR GAMES WITH NO SCHEMA. It is authoritative in the sense that it is
        /// what a profile page would show, but it is also stale in patches (entries here ranged over
        /// six months) and it covers fewer games. Two sources answering "how far along is this game"
        /// with different numbers on different screens is the failure this project has paid for more
        /// than once, so there is exactly one primary and this is not it.
        /// </summary>
        private static Dictionary<string, AchievementSummary> LoadProgressFile()
        {
            var map = new Dictionary<string, AchievementSummary>(StringComparer.Ordinal);
            try
            {
                string steam = SteamSource.SteamPath();
                if (steam == null || _accountId == null) return map;

                string path = Path.Combine(steam, "userdata", _accountId, "config", "librarycache",
                                           "achievement_progress.json");
                if (!File.Exists(path)) return map;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = System.Text.Json.JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("mapCache", out var cache)) return map;

                // mapCache is an ARRAY OF [appid, {...}] PAIRS, not an object keyed by appid.
                foreach (var pair in cache.EnumerateArray())
                {
                    if (pair.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    if (pair.GetArrayLength() < 2) continue;

                    var idEl = pair[0];
                    var val = pair[1];
                    string id = idEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? idEl.GetInt64().ToString(CultureInfo.InvariantCulture)
                        : idEl.GetString();
                    if (string.IsNullOrEmpty(id)) continue;

                    int total = val.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    int unlocked = val.TryGetProperty("unlocked", out var u) ? u.GetInt32() : 0;
                    if (total > 0) map[id] = new AchievementSummary { Total = total, Unlocked = unlocked };
                }
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] progress file failed: " + ex.Message);
            }
            return map;
        }
        #endregion

        #region Binary KeyValues
        // Steam's own type tags. Written out rather than taken from a shared enum because this reader
        // is the only thing in Center that speaks the binary form.
        private const byte TypeNode = 0;
        private const byte TypeString = 1;
        private const byte TypeInt32 = 2;
        private const byte TypeFloat32 = 3;
        private const byte TypePointer = 4;
        private const byte TypeWideString = 5;
        private const byte TypeColor = 6;
        private const byte TypeUInt64 = 7;
        private const byte TypeEnd = 8;

        /// <summary>
        /// A reader for Steam's BINARY KeyValues, which is a different format from the text one
        /// ValveKeyValue is used for elsewhere in Center.
        ///
        /// WARNING: WRITTEN BY HAND RATHER THAN HANDED TO ValveKeyValue, deliberately. That library
        /// does have a KeyValues1Binary mode, but its framing expectations (trailing app ids,
        /// terminators) come from appinfo.vdf, and these blobs are a different shape. The format
        /// itself is fifty lines: a type byte, a null-terminated key, a payload sized by the type,
        /// and 0x08 to close a node. Verified byte for byte against both file kinds on this machine.
        ///
        /// Shared read: Steam holds these open while it runs, and figures that only appear with Steam
        /// closed are figures nobody sees - the same reason SteamPlaytime opens localconfig that way.
        /// </summary>
        private static Dictionary<string, object> ReadKvFile(string path)
        {
            byte[] bytes;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] cannot read " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }

            try
            {
                int i = 0;
                return ReadNode(bytes, ref i, 0);
            }
            catch (Exception ex)
            {
                // A blob we cannot parse is one game without achievements on screen, never a crash.
                Core.InstallLog.Write("[Achievements] malformed " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        private static Dictionary<string, object> ReadNode(byte[] b, ref int i, int depth)
        {
            // Depth is a stop, not a limit anybody should reach: the deepest real path is
            // appid -> stats -> id -> bits -> bit -> display -> name -> language = 8.
            if (depth > 32) throw new InvalidDataException("nesting too deep");

            var node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (i < b.Length)
            {
                byte type = b[i++];
                if (type == TypeEnd) return node;

                string key = ReadCString(b, ref i);
                switch (type)
                {
                    case TypeNode:
                        node[key] = ReadNode(b, ref i, depth + 1);
                        break;
                    case TypeString:
                    case TypeWideString:
                        node[key] = ReadCString(b, ref i);
                        break;
                    case TypeInt32:
                    case TypePointer:
                    case TypeColor:
                        node[key] = ReadInt32(b, ref i);
                        break;
                    case TypeFloat32:
                        Need(b, i, 4);
                        node[key] = BitConverter.ToSingle(b, i);
                        i += 4;
                        break;
                    case TypeUInt64:
                        Need(b, i, 8);
                        node[key] = BitConverter.ToInt64(b, i);
                        i += 8;
                        break;
                    default:
                        throw new InvalidDataException("unknown type " + type + " at " + (i - 1));
                }
            }
            return node;
        }

        private static void Need(byte[] b, int i, int n)
        {
            if (i + n > b.Length) throw new InvalidDataException("truncated at " + i);
        }

        private static int ReadInt32(byte[] b, ref int i)
        {
            Need(b, i, 4);
            int v = BitConverter.ToInt32(b, i);
            i += 4;
            return v;
        }

        /// <summary>UTF-8, null-terminated. Steam writes achievement text in UTF-8 in these blobs -
        /// reading them as the system code page turns every umlaut into a question mark.</summary>
        private static string ReadCString(byte[] b, ref int i)
        {
            int start = i;
            while (i < b.Length && b[i] != 0) i++;
            if (i >= b.Length) throw new InvalidDataException("unterminated string at " + start);
            string s = Encoding.UTF8.GetString(b, start, i - start);
            i++;
            return s;
        }

        private static int AsInt(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out object v) && v is int n ? n : 0;

        private static string AsString(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out object v) ? v as string : null;
        #endregion
    }
}
