using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// Steam achievements from the signed-in ACCOUNT rather than from this disk - step 1 of
    /// Doku\ACHIEVEMENTS_Plan.md. <see cref="SteamAchievements"/> asks here first and falls back to
    /// its blobs; nothing in the UI talks to this class except to ask for a game's detail early.
    ///
    /// ── WHAT COMES FROM WHERE (measured 2026-10-02) ─────────────────────────────────────────────
    ///   unlocked / total, every game    IPlayerService/GetAchievementsProgress   POST, token, 20 per call
    ///   which are unlocked              IPlayerService/GetTopAchievementsForGames max 1000 = all of them
    ///   every achievement of the game   IPlayerService/GetGameAchievements        no token
    ///   WHEN one was unlocked           nowhere on this route - the local blobs add it where they can
    ///
    /// The join between the last two is internal_key = statid * 256 + bit (1543 = 6/7 for
    /// "Brave new world", measured), and between the schema and the local blob internal_name, which
    /// is the blob's own "name". Display names are never used to join: they are localised.
    ///
    /// ── WHEN ────────────────────────────────────────────────────────────────────────────────────
    /// The progress table is fetched in the background on a library refresh at most every
    /// <see cref="ProgressEvery"/>, and kept on disk so a start without network still has the
    /// numbers. The detail list is fetched per game when the library cursor rests on it, and kept
    /// for <see cref="DetailEvery"/>. Every finished fetch raises <see cref="Changed"/>.
    /// </summary>
    public static class SteamAccountAchievements
    {
        private const string Api = "https://api.steampowered.com/";
        private const int ProgressBatch = 20;

        private static readonly TimeSpan ProgressEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DetailEvery = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();

        private static Dictionary<string, AchievementSummary> _progress;
        private static DateTime _progressFetchedUtc = DateTime.MinValue;
        private static bool _progressRunning;

        private sealed class Detail
        {
            public DateTime FetchedUtc;
            public string Language;
            public List<AchievementEntry> Entries;
        }
        private static readonly Dictionary<string, Detail> Details = new Dictionary<string, Detail>(StringComparer.Ordinal);
        private static readonly HashSet<string> DetailRunning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A fetch finished. The argument is the appid for a detail list, null for the
        /// progress table. Raised on a worker thread.</summary>
        public static event Action<string> Changed;

        private static string ProgressPath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "steam-progress.json");

        // ── reading ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The account's unlocked / total, or null when signed out or not known for this
        /// game. Never blocks on the network.</summary>
        public static AchievementSummary SummaryFor(string appId)
        {
            if (appId == null || !SteamAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (Details.TryGetValue(appId, out var d) && d.Entries != null && d.Entries.Count > 0)
                    return new AchievementSummary { Total = d.Entries.Count, Unlocked = d.Entries.Count(e => e.Unlocked) };
                EnsureProgressLoaded();
                return _progress.TryGetValue(appId, out var s) ? s : null;
            }
        }

        /// <summary>
        /// Every achievement of the game with the account's unlocked state, in schema order, or null
        /// when it has not been fetched (yet). Fresh copies - the caller may stamp them.
        /// </summary>
        public static List<AchievementEntry> EntriesFor(string appId)
        {
            if (appId == null || !SteamAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (!Details.TryGetValue(appId, out var d) || d.Entries == null) return null;
                return d.Entries.Select(Copy).ToList();
            }
        }

        private static AchievementEntry Copy(AchievementEntry e) => new AchievementEntry
        {
            Id = e.Id, Name = e.Name, Description = e.Description, IconUrl = e.IconUrl,
            Unlocked = e.Unlocked, UnlockedAt = e.UnlockedAt, Hidden = e.Hidden,
            GlobalPercent = e.GlobalPercent, Progress = e.Progress, ProgressMax = e.ProgressMax,
        };

        /// <summary>Forgets everything fetched - on sign-out, so a second account never sees the
        /// first one's numbers.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _progress = new Dictionary<string, AchievementSummary>(StringComparer.Ordinal);
                _progressFetchedUtc = DateTime.MinValue;
                Details.Clear();
                try { if (File.Exists(ProgressPath)) File.Delete(ProgressPath); } catch { }
            }
            Changed?.Invoke(null);
        }

        // ── fetching ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Starts a progress fetch in the background unless one ran recently. Cheap to call
        /// on every library refresh.</summary>
        public static void RefreshProgressInBackground(bool force = false)
        {
            if (!SteamAccount.IsSignedIn) return;
            lock (Gate)
            {
                if (_progressRunning) return;
                if (!force && DateTime.UtcNow - _progressFetchedUtc < ProgressEvery) return;
                _progressRunning = true;
            }
            _ = Task.Run(async () =>
            {
                try { await FetchProgressAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] account progress failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) _progressRunning = false; }
            });
        }

        /// <summary>Starts a detail fetch for one game unless a fresh one is cached or running.</summary>
        public static void RequestDetail(string appId)
        {
            if (appId == null || !SteamAccount.IsSignedIn) return;
            string lang = SteamAchievements.SteamLanguage();
            lock (Gate)
            {
                if (DetailRunning.Contains(appId)) return;
                if (Details.TryGetValue(appId, out var d) && d.Language == lang && DateTime.UtcNow - d.FetchedUtc < DetailEvery) return;
                DetailRunning.Add(appId);
            }
            _ = Task.Run(async () =>
            {
                try { await FetchDetailAsync(appId, lang, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] account detail for " + appId + " failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) DetailRunning.Remove(appId); }
            });
        }

        private static async Task FetchProgressAsync(CancellationToken ct)
        {
            string token = await SteamAccount.GetAccessTokenAsync(ct).ConfigureAwait(false);
            ulong steamId = SteamAccount.SteamId;
            if (token == null || steamId == 0) return;

            // The ACCOUNT's games, not the installed ones: a game played on another machine has
            // progress too, and the library shows uninstalled games.
            using var owned = await GetJsonAsync("IPlayerService/GetOwnedGames/v1/?include_played_free_games=1&steamid=" + steamId, token, ct).ConfigureAwait(false);
            var appIds = new List<uint>();
            if (owned != null && owned.RootElement.TryGetProperty("response", out var r)
                && r.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
                foreach (var g in games.EnumerateArray())
                    if (g.TryGetProperty("appid", out var a) && a.TryGetUInt32(out uint id) && id != 0) appIds.Add(id);
            if (appIds.Count == 0) return;

            var table = new Dictionary<string, AchievementSummary>(StringComparer.Ordinal);
            for (int i = 0; i < appIds.Count; i += ProgressBatch)
            {
                string input = JsonSerializer.Serialize(new
                {
                    steamid = steamId.ToString(),
                    language = "english",
                    appids = appIds.Skip(i).Take(ProgressBatch).ToArray(),
                });
                // POST: a GET answers 405 with and without input_json (measured).
                using var doc = await PostJsonAsync("IPlayerService/GetAchievementsProgress/v1/", "input_json=" + Uri.EscapeDataString(input), token, ct).ConfigureAwait(false);
                if (doc == null) return; // a hole in the middle is not a table; keep the old one
                if (!doc.RootElement.TryGetProperty("response", out var pr)
                    || !pr.TryGetProperty("achievement_progress", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("appid", out var ap) || !ap.TryGetUInt32(out uint app)) continue;
                    int total = row.TryGetProperty("total", out var t) && t.TryGetInt32(out int tv) ? tv : 0;
                    int unlocked = row.TryGetProperty("unlocked", out var u) && u.TryGetInt32(out int uv) ? uv : 0;
                    // A game with no achievements answers total 0: left out, same as the local
                    // reader - "0%" on a game that has none is a claim about the player.
                    if (total > 0) table[app.ToString()] = new AchievementSummary { Total = total, Unlocked = unlocked };
                }
            }

            lock (Gate)
            {
                _progress = table;
                _progressFetchedUtc = DateTime.UtcNow;
                SaveProgress(table);
            }
            Core.InstallLog.Write("[Achievements] account progress: " + table.Count + " of " + appIds.Count + " games have achievements");
            Changed?.Invoke(null);
        }

        private static async Task FetchDetailAsync(string appId, string lang, CancellationToken ct)
        {
            string token = await SteamAccount.GetAccessTokenAsync(ct).ConfigureAwait(false);
            ulong steamId = SteamAccount.SteamId;
            if (token == null || steamId == 0) return;

            // The whole schema, in the UI's language, with rarity. Needs no token, but it costs
            // nothing to send it.
            using var schema = await GetJsonAsync("IPlayerService/GetGameAchievements/v1/?appid=" + appId + "&language=" + lang, token, ct).ConfigureAwait(false);
            if (schema == null || !schema.RootElement.TryGetProperty("response", out var sr)
                || !sr.TryGetProperty("achievements", out var all) || all.ValueKind != JsonValueKind.Array) return;

            // Which of them the account has: all of them when asked for 1000 (25 of 25, measured).
            string input = JsonSerializer.Serialize(new
            {
                steamid = steamId.ToString(),
                language = lang,
                max_achievements = 1000,
                appids = new[] { uint.Parse(appId) },
            });
            using var top = await GetJsonAsync("IPlayerService/GetTopAchievementsForGames/v1/?input_json=" + Uri.EscapeDataString(input), token, ct).ConfigureAwait(false);
            if (top == null) return; // without it every entry would read as locked
            var unlockedKeys = new HashSet<int>();
            if (top.RootElement.TryGetProperty("response", out var tr) && tr.TryGetProperty("games", out var tg)
                && tg.ValueKind == JsonValueKind.Array && tg.GetArrayLength() > 0
                && tg[0].TryGetProperty("achievements", out var ta) && ta.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in ta.EnumerateArray())
                {
                    int statId = a.TryGetProperty("statid", out var s) && s.TryGetInt32(out int sv) ? sv : -1;
                    int bit = a.TryGetProperty("bit", out var b) && b.TryGetInt32(out int bv) ? bv : -1;
                    if (statId >= 0 && bit >= 0) unlockedKeys.Add(statId * 256 + bit);
                }
            }

            const string iconBase = "https://shared.steamstatic.com/community_assets/images/apps/";
            var entries = new List<AchievementEntry>();
            foreach (var a in all.EnumerateArray())
            {
                int key = a.TryGetProperty("internal_key", out var k) && k.TryGetInt32(out int kv) ? kv : -1;
                bool unlocked = unlockedKeys.Contains(key);
                string icon = Str(a, unlocked ? "icon" : "icon_gray");
                if (string.IsNullOrEmpty(icon)) icon = Str(a, "icon");
                double? pct = null;
                // A string in this answer ("58.0"), not a number.
                if (a.TryGetProperty("player_percent_unlocked", out var p)
                    && double.TryParse(p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pv))
                    pct = pv;
                entries.Add(new AchievementEntry
                {
                    Id = Str(a, "internal_name"),
                    Name = Str(a, "localized_name") ?? Str(a, "internal_name"),
                    Description = Str(a, "localized_desc"),
                    IconUrl = string.IsNullOrEmpty(icon) ? null : iconBase + appId + "/" + icon,
                    Unlocked = unlocked,
                    Hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                    GlobalPercent = pct,
                });
            }
            if (entries.Count == 0) return;

            lock (Gate)
            {
                Details[appId] = new Detail { FetchedUtc = DateTime.UtcNow, Language = lang, Entries = entries };
                // The detail is the newer word on this game; the table row follows it.
                EnsureProgressLoaded();
                _progress[appId] = new AchievementSummary { Total = entries.Count, Unlocked = entries.Count(e => e.Unlocked) };
            }
            Changed?.Invoke(appId);
        }

        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ── http ─────────────────────────────────────────────────────────────────────────────────
        // The token travels as access_token; it is never logged, and neither is a URL carrying it.

        private static async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string token, CancellationToken ct)
        {
            string url = Api + pathAndQuery + (pathAndQuery.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token);
            using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
            return await ReadAsync(res, pathAndQuery, ct).ConfigureAwait(false);
        }

        private static async Task<JsonDocument> PostJsonAsync(string path, string form, string token, CancellationToken ct)
        {
            using var content = new StringContent(form + "&access_token=" + Uri.EscapeDataString(token),
                System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var res = await Http.PostAsync(Api + path, content, ct).ConfigureAwait(false);
            return await ReadAsync(res, path, ct).ConfigureAwait(false);
        }

        private static async Task<JsonDocument> ReadAsync(HttpResponseMessage res, string what, CancellationToken ct)
        {
            if (!res.IsSuccessStatusCode)
            {
                int q = what.IndexOf('?');
                Core.InstallLog.Write("[Achievements] " + (q < 0 ? what : what.Substring(0, q)) + " answered " + (int)res.StatusCode);
                return null;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        // ── the table on disk ────────────────────────────────────────────────────────────────────
        // Not a secret: counts per appid. Plain JSON beside the encrypted account file.

        private static void EnsureProgressLoaded()
        {
            if (_progress != null) return;
            _progress = new Dictionary<string, AchievementSummary>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(ProgressPath)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(ProgressPath));
                if (raw == null) return;
                foreach (var kv in raw)
                    if (kv.Value != null && kv.Value.Length == 2 && kv.Value[1] > 0)
                        _progress[kv.Key] = new AchievementSummary { Unlocked = kv.Value[0], Total = kv.Value[1] };
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] account progress cache unreadable: " + ex.GetType().Name);
            }
        }

        private static void SaveProgress(Dictionary<string, AchievementSummary> table)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath));
                var raw = table.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Unlocked, kv.Value.Total });
                string tmp = ProgressPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(raw));
                File.Move(tmp, ProgressPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] could not save account progress: " + ex.Message);
            }
        }
    }
}
