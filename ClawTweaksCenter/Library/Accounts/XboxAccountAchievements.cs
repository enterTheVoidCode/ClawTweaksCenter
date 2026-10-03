using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// Xbox achievements from the signed-in Microsoft account. The Xbox counterpart of
    /// <see cref="SteamAccountAchievements"/>, read through <see cref="SteamAchievements"/> so the
    /// screens need not know which store a game is from.
    ///
    /// ── THE JOIN ───────────────────────────────────────────────────────────────────────────────
    /// Center knows an Xbox game by its AUMID, "Microsoft.ForteBaseGame_8wekyb3d8bbwe!Forzahorizon6".
    /// The part before '!' is the package family name, and titlehub carries exactly that as `pfn`
    /// for every title (43 of 43, measured 2026-10-03). The pfn gives the titleId the achievement
    /// service wants.
    ///
    /// ── WHAT COMES FROM WHERE ───────────────────────────────────────────────────────────────────
    ///   which titles, unlocked count   titlehub titlehistory (contract 2) - every PLAYED title
    ///   the full list, unlock times    achievements.xboxlive.com ?titleId= (contract 2)
    ///
    /// ⚠️ titlehub's totalAchievements IS NOT TRUSTED. Forza Horizon 6 answered "1 of 0" (measured).
    /// A summary needs a total, so a title whose titlehub total is 0 gets its summary only once its
    /// list has been fetched - the cursor resting on it is enough for that.
    ///
    /// Xbox is the mirror of Steam here: unlock TIMES yes, rarity no (absent on all 57 of Forza's).
    /// A game that was installed but never started is not in titlehub, and has nothing to show.
    /// </summary>
    public static class XboxAccountAchievements
    {
        private static readonly TimeSpan TitlesEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DetailEvery = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();

        private sealed class Title
        {
            public string TitleId { get; set; }
            public int Unlocked { get; set; }
            public int Total { get; set; }
        }
        private static Dictionary<string, Title> _titles;        // by pfn
        private static DateTime _titlesFetchedUtc = DateTime.MinValue;
        private static bool _titlesRunning;

        private sealed class Detail
        {
            public DateTime FetchedUtc;
            public string Language;
            public List<AchievementEntry> Entries;
        }
        private static readonly Dictionary<string, Detail> Details = new Dictionary<string, Detail>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DetailRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A fetch finished: the pfn for a list, null for the title table. Worker thread.</summary>
        public static event Action<string> Changed;

        private static string TitlesPath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "xbox-titles.json");

        /// <summary>The package family name out of an AUMID, or null.</summary>
        public static string PfnOf(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            int bang = aumid.IndexOf('!');
            return bang > 0 ? aumid.Substring(0, bang) : null;
        }

        // ── reading ──────────────────────────────────────────────────────────────────────────────

        public static AchievementSummary SummaryFor(string pfn)
        {
            if (pfn == null || !XboxAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (Details.TryGetValue(pfn, out var d) && d.Entries != null && d.Entries.Count > 0)
                    return new AchievementSummary { Total = d.Entries.Count, Unlocked = d.Entries.Count(e => e.Unlocked) };
                EnsureTitlesLoaded();
                return _titles.TryGetValue(pfn, out var t) && t.Total > 0
                    ? new AchievementSummary { Total = t.Total, Unlocked = Math.Min(t.Unlocked, t.Total) }
                    : null;
            }
        }

        /// <summary>The full list in the service's order, or null when not fetched (yet). Copies.</summary>
        public static List<AchievementEntry> EntriesFor(string pfn)
        {
            if (pfn == null || !XboxAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (!Details.TryGetValue(pfn, out var d) || d.Entries == null) return null;
                return d.Entries.Select(e => new AchievementEntry
                {
                    Id = e.Id, Name = e.Name, Description = e.Description, IconUrl = e.IconUrl,
                    Unlocked = e.Unlocked, UnlockedAt = e.UnlockedAt, Hidden = e.Hidden,
                    GlobalPercent = e.GlobalPercent, Progress = e.Progress, ProgressMax = e.ProgressMax,
                }).ToList();
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _titles = new Dictionary<string, Title>(StringComparer.OrdinalIgnoreCase);
                _titlesFetchedUtc = DateTime.MinValue;
                Details.Clear();
                try { if (File.Exists(TitlesPath)) File.Delete(TitlesPath); } catch { }
            }
            Changed?.Invoke(null);
        }

        // ── fetching ─────────────────────────────────────────────────────────────────────────────

        public static void RefreshTitlesInBackground(bool force = false)
        {
            if (!XboxAccount.IsSignedIn) return;
            lock (Gate)
            {
                if (_titlesRunning) return;
                if (!force && DateTime.UtcNow - _titlesFetchedUtc < TitlesEvery) return;
                _titlesRunning = true;
            }
            _ = Task.Run(async () =>
            {
                try { await FetchTitlesAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] Xbox titles failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) _titlesRunning = false; }
            });
        }

        public static void RequestDetail(string pfn)
        {
            if (pfn == null || !XboxAccount.IsSignedIn) return;
            string lang = AcceptLanguage();
            lock (Gate)
            {
                if (DetailRunning.Contains(pfn)) return;
                if (Details.TryGetValue(pfn, out var d) && d.Language == lang && DateTime.UtcNow - d.FetchedUtc < DetailEvery) return;
                DetailRunning.Add(pfn);
            }
            _ = Task.Run(async () =>
            {
                try { await FetchDetailAsync(pfn, lang, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] Xbox list for " + pfn + " failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) DetailRunning.Remove(pfn); }
            });
        }

        private static async Task FetchTitlesAsync(CancellationToken ct)
        {
            string auth = await XboxAccount.GetAuthHeaderAsync(ct).ConfigureAwait(false);
            string xuid = XboxAccount.Xuid;
            if (auth == null || string.IsNullOrEmpty(xuid)) return;

            using var doc = await GetAsync("https://titlehub.xboxlive.com/users/xuid(" + xuid + ")/titles/titlehistory/decoration/achievement",
                "2", auth, ct).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) return;

            var table = new Dictionary<string, Title>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in titles.EnumerateArray())
            {
                string pfn = t.TryGetProperty("pfn", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string id = t.TryGetProperty("titleId", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(pfn) || string.IsNullOrEmpty(id)) continue;
                int unlocked = 0, total = 0;
                if (t.TryGetProperty("achievement", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    if (a.TryGetProperty("currentAchievements", out var c)) c.TryGetInt32(out unlocked);
                    if (a.TryGetProperty("totalAchievements", out var tt)) tt.TryGetInt32(out total);
                }
                table[pfn] = new Title { TitleId = id, Unlocked = unlocked, Total = total };
            }

            lock (Gate)
            {
                // titlehub's total of 0 does not overwrite a total an earlier LIST fetch counted -
                // that is the one this table cannot get right on its own (Forza: "1 of 0").
                EnsureTitlesLoaded();
                foreach (var kv in table)
                    if (kv.Value.Total <= 0 && _titles.TryGetValue(kv.Key, out var old) && old.Total > 0)
                        kv.Value.Total = old.Total;
                _titles = table;
                _titlesFetchedUtc = DateTime.UtcNow;
                SaveTitles(table);
            }
            Core.InstallLog.Write("[Achievements] Xbox titles: " + table.Count + " played, "
                + table.Values.Count(t => t.Total > 0) + " with a trusted total");
            Changed?.Invoke(null);
        }

        private static async Task FetchDetailAsync(string pfn, string lang, CancellationToken ct)
        {
            string titleId;
            lock (Gate)
            {
                EnsureTitlesLoaded();
                titleId = _titles.TryGetValue(pfn, out var t) ? t.TitleId : null;
            }
            if (titleId == null)
            {
                // Not in the table: never played, or the table predates the first start. One fresh
                // read of the table, then give up - an unplayed game has no achievements to show.
                await FetchTitlesAsync(ct).ConfigureAwait(false);
                lock (Gate) titleId = _titles.TryGetValue(pfn, out var t2) ? t2.TitleId : null;
                if (titleId == null) return;
            }

            string auth = await XboxAccount.GetAuthHeaderAsync(ct).ConfigureAwait(false);
            string xuid = XboxAccount.Xuid;
            if (auth == null || string.IsNullOrEmpty(xuid)) return;

            using var doc = await GetAsync("https://achievements.xboxlive.com/users/xuid(" + xuid + ")/achievements?titleId=" + titleId + "&maxItems=1000",
                "2", auth, ct, lang).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array) return;

            var entries = new List<AchievementEntry>();
            foreach (var a in list.EnumerateArray())
            {
                var e = Parse(a);
                if (e != null) entries.Add(e);
            }
            if (entries.Count == 0) return;

            lock (Gate)
            {
                Details[pfn] = new Detail { FetchedUtc = DateTime.UtcNow, Language = lang, Entries = entries };
                EnsureTitlesLoaded();
                if (_titles.TryGetValue(pfn, out var t))
                {
                    t.Total = entries.Count;
                    t.Unlocked = entries.Count(e => e.Unlocked);
                    SaveTitles(_titles);
                }
            }
            Changed?.Invoke(pfn);
        }

        /// <summary>One unlock from <see cref="RecentUnlocksAsync"/>: the achievement, and the game it
        /// belongs to as Xbox names it - with the package family name when the title table knows it,
        /// so the history can show Center's own title and art for it.</summary>
        public sealed class RecentUnlock
        {
            public AchievementEntry Entry;
            public string TitleName;
            public string Pfn;
        }

        /// <summary>
        /// The account's most recent unlocks across EVERY title, newest first, in one call - for the
        /// achievement history. The per-title list above would need one call per played title.
        /// Empty when signed out or when the service says no; logged either way.
        /// </summary>
        public static async Task<List<RecentUnlock>> RecentUnlocksAsync(int max, CancellationToken ct)
        {
            var result = new List<RecentUnlock>();
            string auth = await XboxAccount.GetAuthHeaderAsync(ct).ConfigureAwait(false);
            string xuid = XboxAccount.Xuid;
            if (auth == null || string.IsNullOrEmpty(xuid)) return result;

            using var doc = await GetAsync("https://achievements.xboxlive.com/users/xuid(" + xuid + ")/achievements?unlockedOnly=true&orderBy=UnlockTime&maxItems=" + max,
                "2", auth, ct).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                Core.InstallLog.Write("[Achievements] Xbox recent unlocks: no list");
                return result;
            }

            Dictionary<string, string> pfnByTitle;
            lock (Gate)
            {
                EnsureTitlesLoaded();
                pfnByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _titles) pfnByTitle[kv.Value.TitleId] = kv.Key;
            }

            foreach (var a in list.EnumerateArray())
            {
                var e = Parse(a);
                if (e == null || !e.Unlocked) continue;
                string titleId = null, titleName = null;
                if (a.TryGetProperty("titleAssociations", out var ta) && ta.ValueKind == JsonValueKind.Array && ta.GetArrayLength() > 0)
                {
                    titleName = Str(ta[0], "name");
                    if (ta[0].TryGetProperty("id", out var tid)) titleId = tid.ToString();
                }
                result.Add(new RecentUnlock
                {
                    Entry = e,
                    TitleName = titleName,
                    Pfn = titleId != null && pfnByTitle.TryGetValue(titleId, out var pfn) ? pfn : null,
                });
            }
            Core.InstallLog.Write("[Achievements] Xbox recent unlocks: " + result.Count + ", dated " + result.Count(r => r.Entry.UnlockedAt.HasValue));
            return result;
        }

        /// <summary>One entry of the achievement service's answer (contract 2), or null for a
        /// revoked one.</summary>
        private static AchievementEntry Parse(JsonElement a)
        {
            // Revoked ones are not part of the game any more.
            if (a.TryGetProperty("isRevoked", out var rv) && rv.ValueKind == JsonValueKind.True) return null;

            bool unlocked = Str(a, "progressState") == "Achieved";
            DateTime? at = null;
            float progress = 0, max = 0;
            if (a.TryGetProperty("progression", out var pr) && pr.ValueKind == JsonValueKind.Object)
            {
                // An unlock without a time is 0001-01-01, not null (measured).
                if (unlocked && pr.TryGetProperty("timeUnlocked", out var tu) && tu.TryGetDateTime(out var dt) && dt.Year > 2000)
                    at = dt.ToLocalTime();
                // A counted one ("100 of 100 km") carries one requirement with current/target.
                if (pr.TryGetProperty("requirements", out var req) && req.ValueKind == JsonValueKind.Array && req.GetArrayLength() == 1)
                {
                    float.TryParse(Str(req[0], "current"), NumberStyles.Float, CultureInfo.InvariantCulture, out progress);
                    float.TryParse(Str(req[0], "target"), NumberStyles.Float, CultureInfo.InvariantCulture, out max);
                    if (max <= 1) { progress = 0; max = 0; }
                }
            }

            string icon = null;
            if (a.TryGetProperty("mediaAssets", out var media) && media.ValueKind == JsonValueKind.Array)
                foreach (var m in media.EnumerateArray())
                    if (Str(m, "type") == "Icon") { icon = Str(m, "url"); break; }

            double? pct = null;
            if (a.TryGetProperty("rarity", out var rar) && rar.ValueKind == JsonValueKind.Object
                && rar.TryGetProperty("currentPercentage", out var cp) && cp.TryGetDouble(out double cpv))
                pct = cpv;

            bool secret = a.TryGetProperty("isSecret", out var sc) && sc.ValueKind == JsonValueKind.True;
            return new AchievementEntry
            {
                Id = Str(a, "id"),
                Name = Str(a, "name"),
                // Xbox has a separate text for the locked state; that one is what a locked entry
                // shows, as on the console.
                Description = unlocked ? Str(a, "description") : (Str(a, "lockedDescription") ?? Str(a, "description")),
                IconUrl = icon,
                Unlocked = unlocked,
                UnlockedAt = at,
                Hidden = secret,
                GlobalPercent = pct,
                Progress = progress,
                ProgressMax = max,
            };
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>The UI language as an Accept-Language tag - the achievement service localises by it.</summary>
        private static string AcceptLanguage()
        {
            switch (Core.Loc.Current)
            {
                case Core.UiLanguage.German: return "de-DE";
                case Core.UiLanguage.French: return "fr-FR";
                case Core.UiLanguage.Korean: return "ko-KR";
                case Core.UiLanguage.Spanish: return "es-ES";
                default: return "en-US";
            }
        }

        private static async Task<JsonDocument> GetAsync(string url, string contract, string auth, CancellationToken ct, string lang = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            req.Headers.Add("x-xbl-contract-version", contract);
            req.Headers.Add("Accept-Language", lang ?? AcceptLanguage());
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Core.InstallLog.Write("[Achievements] " + new Uri(url).Host + " answered " + (int)res.StatusCode);
                return null;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        // ── the table on disk: titleId and counts per pfn. Not a secret. ─────────────────────────

        private static void EnsureTitlesLoaded()
        {
            if (_titles != null) return;
            _titles = new Dictionary<string, Title>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(TitlesPath)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, Title>>(File.ReadAllText(TitlesPath));
                if (raw != null)
                    foreach (var kv in raw)
                        if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.TitleId)) _titles[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] Xbox title cache unreadable: " + ex.GetType().Name);
            }
        }

        private static void SaveTitles(Dictionary<string, Title> table)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TitlesPath));
                string tmp = TitlesPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(table));
                File.Move(tmp, TitlesPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] could not save Xbox titles: " + ex.Message);
            }
        }
    }
}
