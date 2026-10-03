using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// RetroAchievements for the ROMs in the library. The RetroAchievements counterpart of
    /// <see cref="EpicAccountAchievements"/>, read through <see cref="SteamAchievements"/> so the
    /// screens need not know where a game's list comes from. Measured 2026-10-03, see
    /// Doku\ACHIEVEMENTS_Plan.md §5e.
    ///
    /// ── THE JOIN: PLAYNITEACHIEVEMENTS DID IT ──────────────────────────────────────────────────
    /// RetroAchievements knows a game by the hash of its ROM. Center hashes nothing (user,
    /// 2026-10-03): PlayniteAchievements already has, and keeps the result in its
    /// achievement_cache.db - table Games, ProviderKey 'RetroAchievements', PlayniteGameId -&gt;
    /// ProviderGameId. 111 games on the user's machine. A Playnite ROM's id in Center IS that
    /// PlayniteGameId (<see cref="PlayniteSource"/>), so the join is one lookup. The database is
    /// opened read-only and without pooling, so Playnite never finds it held.
    ///
    /// Several Playnite entries can share one RetroAchievements game (5 measured - the discs of one
    /// game, a hack and its original). Each keeps its own row in the library; they show the same
    /// list.
    ///
    /// ── THREE CALLS ────────────────────────────────────────────────────────────────────────────
    ///   API_GetUserCompletionProgress       every game the user has played, unlocked / total, ONE
    ///                                       call. Played-and-mapped: 16 of 111 here.
    ///   API_GetGameInfoAndUserProgress      one game's full list with the user's dates and the
    ///                                       player counts rarity is worked out from. On demand.
    ///   API_GetAchievementsEarnedBetween    every dated unlock of the account, for the history.
    /// A game the user never played answers nothing in the first; its total then comes from
    /// PlayniteAchievements' own table, so the library line can say "0 %" rather than nothing.
    ///
    /// RetroAchievements is English only - titles and descriptions come as the set's author wrote
    /// them, whatever the UI language.
    /// </summary>
    public static class RetroAchievementsAchievements
    {
        private const string MediaBase = "https://media.retroachievements.org";

        private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DetailEvery = TimeSpan.FromMinutes(5);

        private static readonly object Gate = new object();

        private sealed class Mapped
        {
            public int RaGameId;
            /// <summary>The set's size as PlayniteAchievements last saw it; 0 when unknown.</summary>
            public int Total;
        }
        /// <summary>PlayniteGameId -&gt; RetroAchievements game. Null until read.</summary>
        private static Dictionary<string, Mapped> _map;

        private sealed class Progress
        {
            public int Unlocked { get; set; }
            public int Total { get; set; }
        }
        private static Dictionary<int, Progress> _progress;          // by RA game id, on disk
        private static DateTime _refreshedUtc = DateTime.MinValue;
        private static bool _refreshRunning;

        private sealed class Detail
        {
            public DateTime FetchedUtc;
            public List<AchievementEntry> Entries;
        }
        private static readonly Dictionary<int, Detail> Details = new Dictionary<int, Detail>();
        private static readonly HashSet<int> DetailRunning = new HashSet<int>();

        /// <summary>A fetch finished: the PlayniteGameId of one game, null for the whole table.
        /// Worker thread.</summary>
        public static event Action<string> Changed;

        private static string ProgressPath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "retroachievements-progress.json");

        /// <summary>How many library ROMs PlayniteAchievements has matched. For the log and the
        /// accounts screen; reads the database on first use.</summary>
        public static int MappedCount
        {
            get { lock (Gate) { EnsureMapLoaded(); return _map.Count; } }
        }

        // ── reading ──────────────────────────────────────────────────────────────────────────────

        public static AchievementSummary SummaryFor(string playniteId)
        {
            if (string.IsNullOrEmpty(playniteId) || !RetroAchievementsAccount.IsSignedIn) return null;
            lock (Gate)
            {
                EnsureMapLoaded();
                if (!_map.TryGetValue(playniteId, out var m)) return null;
                if (Details.TryGetValue(m.RaGameId, out var d) && d.Entries != null && d.Entries.Count > 0)
                    return new AchievementSummary { Total = d.Entries.Count, Unlocked = d.Entries.Count(e => e.Unlocked) };
                EnsureProgressLoaded();
                if (_progress.TryGetValue(m.RaGameId, out var p) && p.Total > 0)
                    return new AchievementSummary { Total = p.Total, Unlocked = Math.Min(p.Unlocked, p.Total) };
                // Never played: nothing in the account's table, the set's size from the mapping.
                return m.Total > 0 ? new AchievementSummary { Total = m.Total, Unlocked = 0 } : null;
            }
        }

        /// <summary>The full list in the set's display order, or null when not fetched (yet). Copies.</summary>
        public static List<AchievementEntry> EntriesFor(string playniteId)
        {
            if (string.IsNullOrEmpty(playniteId) || !RetroAchievementsAccount.IsSignedIn) return null;
            lock (Gate)
            {
                EnsureMapLoaded();
                if (!_map.TryGetValue(playniteId, out var m)) return null;
                if (!Details.TryGetValue(m.RaGameId, out var d) || d.Entries == null) return null;
                return d.Entries.Select(Copy).ToList();
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _progress = new Dictionary<int, Progress>();
                _refreshedUtc = DateTime.MinValue;
                Details.Clear();
                try { if (File.Exists(ProgressPath)) File.Delete(ProgressPath); } catch { }
            }
            Changed?.Invoke(null);
        }

        // ── fetching ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Re-reads PlayniteAchievements' mapping and the account's completion table, at
        /// most every few minutes. One request.</summary>
        public static void RefreshInBackground(bool force = false)
        {
            if (!RetroAchievementsAccount.IsSignedIn) return;
            lock (Gate)
            {
                if (_refreshRunning) return;
                if (!force && DateTime.UtcNow - _refreshedUtc < RefreshEvery) return;
                _refreshRunning = true;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var map = ReadMap();
                    var table = new Dictionary<int, Progress>();
                    int offset = 0, total = int.MaxValue;
                    while (offset < total)
                    {
                        using var doc = await RetroAchievementsAccount.GetAsync("API_GetUserCompletionProgress.php",
                            new Dictionary<string, string> { ["c"] = "500", ["o"] = offset.ToString(CultureInfo.InvariantCulture) },
                            CancellationToken.None).ConfigureAwait(false);
                        if (doc == null) break;
                        var root = doc.RootElement;
                        total = Int(root, "Total");
                        if (!root.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array) break;
                        int page = 0;
                        foreach (var r in results.EnumerateArray())
                        {
                            page++;
                            int id = Int(r, "GameID");
                            if (id <= 0) continue;
                            table[id] = new Progress
                            {
                                Total = Int(r, "MaxPossible"),
                                // Softcore counts too: NumAwarded already includes the hardcore ones.
                                Unlocked = Math.Max(Int(r, "NumAwarded"), Int(r, "NumAwardedHardcore")),
                            };
                        }
                        if (page == 0) break;
                        offset += page;
                    }

                    int mappedPlayed = 0;
                    lock (Gate)
                    {
                        _map = map;
                        if (total != int.MaxValue)
                        {
                            _progress = table;
                            SaveProgress(table);
                        }
                        else EnsureProgressLoaded();
                        var ids = new HashSet<int>(map.Values.Select(v => v.RaGameId));
                        mappedPlayed = _progress.Keys.Count(ids.Contains);
                        _refreshedUtc = DateTime.UtcNow;
                    }
                    Core.InstallLog.Write("[Achievements] RetroAchievements: " + map.Count + " ROMs mapped by PlayniteAchievements, "
                        + table.Count + " games played, " + mappedPlayed + " of them in the library");
                }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] RetroAchievements refresh failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) _refreshRunning = false; }
                Changed?.Invoke(null);
            });
        }

        public static void RequestDetail(string playniteId)
        {
            if (string.IsNullOrEmpty(playniteId) || !RetroAchievementsAccount.IsSignedIn) return;
            int raId;
            lock (Gate)
            {
                EnsureMapLoaded();
                if (!_map.TryGetValue(playniteId, out var m)) return;
                raId = m.RaGameId;
                if (DetailRunning.Contains(raId)) return;
                if (Details.TryGetValue(raId, out var d) && DateTime.UtcNow - d.FetchedUtc < DetailEvery) return;
                DetailRunning.Add(raId);
            }
            _ = Task.Run(async () =>
            {
                try { await FetchDetailAsync(raId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] RetroAchievements list for game " + raId + " failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) DetailRunning.Remove(raId); }
                // Every library entry on this RA game hears about it.
                List<string> keys;
                lock (Gate) keys = _map.Where(kv => kv.Value.RaGameId == raId).Select(kv => kv.Key).ToList();
                foreach (string k in keys) Changed?.Invoke(k);
            });
        }

        private static async Task FetchDetailAsync(int raId, CancellationToken ct)
        {
            using var doc = await RetroAchievementsAccount.GetAsync("API_GetGameInfoAndUserProgress.php",
                new Dictionary<string, string> { ["g"] = raId.ToString(CultureInfo.InvariantCulture), ["a"] = "1" }, ct).ConfigureAwait(false);
            if (doc == null) return;
            var root = doc.RootElement;
            int players = Int(root, "NumDistinctPlayers");
            var entries = new List<(int Order, int Id, AchievementEntry Entry)>();
            if (root.TryGetProperty("Achievements", out var list) && list.ValueKind == JsonValueKind.Object)
                foreach (var prop in list.EnumerateObject())
                {
                    var a = prop.Value;
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    int id = Int(a, "ID");
                    // Hardcore and softcore are one achievement here; the earlier date wins.
                    DateTime? at = Earliest(ParseUtc(Str(a, "DateEarnedHardcore")), ParseUtc(Str(a, "DateEarned")));
                    string badge = Str(a, "BadgeName");
                    var e = new AchievementEntry
                    {
                        Id = id.ToString(CultureInfo.InvariantCulture),
                        Name = Str(a, "Title"),
                        Description = Str(a, "Description"),
                        // The locked face is the same badge greyed - RetroAchievements serves it
                        // as "<badge>_lock.png".
                        IconUrl = string.IsNullOrEmpty(badge) ? null : MediaBase + "/Badge/" + badge + (at.HasValue ? ".png" : "_lock.png"),
                        Unlocked = at.HasValue,
                        UnlockedAt = at?.ToLocalTime(),
                        GlobalPercent = players > 0 ? Math.Min(100.0, 100.0 * Int(a, "NumAwarded") / players) : (double?)null,
                    };
                    entries.Add((Int(a, "DisplayOrder"), id, e));
                }
            var ordered = entries.OrderBy(x => x.Order).ThenBy(x => x.Id).Select(x => x.Entry).ToList();

            lock (Gate)
            {
                Details[raId] = new Detail { FetchedUtc = DateTime.UtcNow, Entries = ordered };
                EnsureProgressLoaded();
                if (ordered.Count > 0)
                {
                    _progress[raId] = new Progress { Total = ordered.Count, Unlocked = ordered.Count(e => e.Unlocked) };
                    SaveProgress(_progress);
                }
            }
        }

        /// <summary>One dated unlock for the history.</summary>
        public sealed class RecentUnlock
        {
            public AchievementEntry Entry;
            /// <summary>The library's ROM for it, when PlayniteAchievements mapped one; else null.</summary>
            public string PlayniteId;
            public string GameTitle;
            public string ConsoleName;
        }

        /// <summary>
        /// The account's unlocks, newest first, at most <paramref name="max"/>. Every one of them is
        /// dated - RetroAchievements records the moment on its server. All of the account's, also
        /// for games that are not (or no longer) in the library: the history is about the player.
        /// </summary>
        public static async Task<List<RecentUnlock>> RecentUnlocksAsync(int max, CancellationToken ct)
        {
            var result = new List<RecentUnlock>();
            if (!RetroAchievementsAccount.IsSignedIn) return result;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var doc = await RetroAchievementsAccount.GetAsync("API_GetAchievementsEarnedBetween.php",
                new Dictionary<string, string> { ["f"] = "0", ["t"] = now.ToString(CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            Dictionary<int, string> byRaId;
            lock (Gate)
            {
                EnsureMapLoaded();
                byRaId = new Dictionary<int, string>();
                foreach (var kv in _map)
                    if (!byRaId.ContainsKey(kv.Value.RaGameId)) byRaId[kv.Value.RaGameId] = kv.Key;
            }

            // The same achievement can come twice when it was earned softcore first and hardcore
            // later; the earlier one stands.
            var seen = new HashSet<int>();
            foreach (var x in doc.RootElement.EnumerateArray().OrderBy(x => Str(x, "Date"), StringComparer.Ordinal))
            {
                var at = ParseUtc(Str(x, "Date"));
                int id = Int(x, "AchievementID");
                if (!at.HasValue || !seen.Add(id)) continue;
                int gameId = Int(x, "GameID");
                string badge = Str(x, "BadgeURL");
                result.Add(new RecentUnlock
                {
                    Entry = new AchievementEntry
                    {
                        Id = id.ToString(CultureInfo.InvariantCulture),
                        Name = Str(x, "Title"),
                        Description = Str(x, "Description"),
                        IconUrl = string.IsNullOrEmpty(badge) ? null : MediaBase + badge,
                        Unlocked = true,
                        UnlockedAt = at.Value.ToLocalTime(),
                    },
                    PlayniteId = byRaId.TryGetValue(gameId, out var pid) ? pid : null,
                    GameTitle = Str(x, "GameTitle"),
                    ConsoleName = Str(x, "ConsoleName"),
                });
            }
            return result.OrderByDescending(r => r.Entry.UnlockedAt.Value).Take(max).ToList();
        }

        // ── PlayniteAchievements' mapping ────────────────────────────────────────────────────────

        private static void EnsureMapLoaded()
        {
            if (_map == null) _map = ReadMap();
        }

        private static Dictionary<string, Mapped> ReadMap()
        {
            var map = new Dictionary<string, Mapped>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(RetroAchievementsAccount.PlayniteAchievementsDir, "achievement_cache.db");
            if (!File.Exists(path)) return map;
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
                using var db = new SqliteConnection(cs);
                db.Open();
                using var cmd = db.CreateCommand();
                // The lowest row per Playnite game when there are several; the set size from the
                // current user's progress row, which PlayniteAchievements writes for played and
                // unplayed games alike.
                cmd.CommandText = @"SELECT g.PlayniteGameId, g.ProviderGameId, COALESCE(MAX(p.TotalAchievements), 0)
                    FROM Games g LEFT JOIN UserGameProgress p ON p.GameId = g.Id
                    WHERE g.ProviderKey = 'RetroAchievements' AND g.PlayniteGameId IS NOT NULL AND g.ProviderGameId > 0
                    GROUP BY g.PlayniteGameId ORDER BY MIN(g.Id)";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string pid = r.GetString(0);
                    if (string.IsNullOrEmpty(pid) || map.ContainsKey(pid)) continue;
                    map[pid] = new Mapped { RaGameId = (int)r.GetInt64(1), Total = (int)r.GetInt64(2) };
                }
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] PlayniteAchievements cache unreadable: " + ex.GetType().Name + ": " + ex.Message);
            }
            return map;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>RetroAchievements writes "2026-05-06 18:04:07" in UTC (cross-checked against the
        /// completion table's "+00:00" date, 2026-10-03).</summary>
        private static DateTime? ParseUtc(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) && dt.Year > 2000
                ? dt : (DateTime?)null;
        }

        private static DateTime? Earliest(DateTime? a, DateTime? b) =>
            !a.HasValue ? b : !b.HasValue ? a : (a.Value < b.Value ? a : b);

        private static AchievementEntry Copy(AchievementEntry e) => new AchievementEntry
        {
            Id = e.Id, Name = e.Name, Description = e.Description, IconUrl = e.IconUrl,
            Unlocked = e.Unlocked, UnlockedAt = e.UnlockedAt, Hidden = e.Hidden,
            GlobalPercent = e.GlobalPercent, Progress = e.Progress, ProgressMax = e.ProgressMax,
        };

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            // Some RetroAchievements answers carry numbers as strings.
            return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : 0;
        }

        // ── the table on disk: counts per RetroAchievements game. Not a secret. ──────────────────

        private static void EnsureProgressLoaded()
        {
            if (_progress != null) return;
            _progress = new Dictionary<int, Progress>();
            try
            {
                if (!File.Exists(ProgressPath)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<int, Progress>>(File.ReadAllText(ProgressPath));
                if (raw != null)
                    foreach (var kv in raw)
                        if (kv.Value != null) _progress[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] RetroAchievements progress cache unreadable: " + ex.GetType().Name);
            }
        }

        private static void SaveProgress(Dictionary<int, Progress> table)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath));
                string tmp = ProgressPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(table));
                File.Move(tmp, ProgressPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] could not save RetroAchievements progress: " + ex.Message);
            }
        }
    }
}
