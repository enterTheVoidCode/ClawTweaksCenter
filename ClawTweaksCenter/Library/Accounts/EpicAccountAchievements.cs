using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// Epic achievements from the signed-in account. The Epic counterpart of
    /// <see cref="XboxAccountAchievements"/>, read through <see cref="SteamAchievements"/> so the
    /// screens need not know which store a game is from.
    ///
    /// ── THE JOIN ───────────────────────────────────────────────────────────────────────────────
    /// Center knows an Epic game by its AppName (<see cref="EpicSource"/>). The same manifest carries
    /// the CatalogNamespace, and that namespace IS the sandbox id the achievement schema is asked
    /// by - so no library call is needed (both installed games' namespaces were in the account's
    /// library, measured 2026-10-03).
    ///
    /// ── TWO QUERIES, launcher.store.epicgames.com/graphql ──────────────────────────────────────
    ///   productAchievementsRecordBySandbox(namespace, locale)   the schema: names, texts, both
    ///       icons, hidden flag, rarity, and the productId. Works WITHOUT a token. Cached per
    ///       namespace and language for the session.
    ///   playerProfile(accountId).productAchievements(productId)  the player's unlocks WITH dates.
    ///       Answers ServiceError for a game the account never started (4 of 7, measured) - read
    ///       as "nothing unlocked", which is what it means.
    ///
    /// Epic has everything the other two each lack: unlock dates (Steam does not) and rarity on every
    /// achievement (Xbox does not) - 732 of 732, measured.
    /// </summary>
    public static class EpicAccountAchievements
    {
        private const string GraphQl = "https://launcher.store.epicgames.com/graphql";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher";

        private const string SchemaQuery = @"query Achievement($SandboxId: String!, $Locale: String!) {
  Achievement { productAchievementsRecordBySandbox(sandboxId: $SandboxId, locale: $Locale) {
    productId
    achievements { achievement { name hidden unlockedDisplayName unlockedDescription lockedDisplayName lockedDescription unlockedIconLink lockedIconLink rarity { percent } } }
  } } }";

        private const string PlayerQuery = @"query playerProfileAchievementsByProductId($EpicAccountId: String!, $ProductId: String!) {
  PlayerProfile { playerProfile(epicAccountId: $EpicAccountId) { productAchievements(productId: $ProductId) {
    __typename
    ... on PlayerProductAchievementsResponseSuccess { data { playerAchievements { playerAchievement { achievementName unlocked unlockDate } } } }
  } } } }";

        private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DetailEvery = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();

        private sealed class Progress
        {
            public int Unlocked { get; set; }
            public int Total { get; set; }
        }
        private static Dictionary<string, Progress> _progress;          // by AppName, on disk
        private static DateTime _refreshedUtc = DateTime.MinValue;
        private static bool _refreshRunning;

        /// <summary>One achievement with both of Epic's faces: the locked one (a hidden achievement's
        /// is empty) and the unlocked one. Which shows is decided per player.</summary>
        private sealed class SchemaItem
        {
            public AchievementEntry Locked;
            public string UnlockedName, UnlockedDescription, UnlockedIcon;
        }

        private sealed class Schema
        {
            public string ProductId;
            public List<SchemaItem> Items;
        }
        private static readonly Dictionary<string, Schema> Schemas = new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase);

        private sealed class Detail
        {
            public DateTime FetchedUtc;
            public string Language;
            public List<AchievementEntry> Entries;
        }
        private static readonly Dictionary<string, Detail> Details = new Dictionary<string, Detail>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DetailRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A fetch finished: the AppName for one game, null for the whole table. Worker thread.</summary>
        public static event Action<string> Changed;

        private static string ProgressPath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "epic-progress.json");

        // ── reading ──────────────────────────────────────────────────────────────────────────────

        public static AchievementSummary SummaryFor(string appName)
        {
            if (string.IsNullOrEmpty(appName) || !EpicAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (Details.TryGetValue(appName, out var d) && d.Entries != null && d.Entries.Count > 0)
                    return new AchievementSummary { Total = d.Entries.Count, Unlocked = d.Entries.Count(e => e.Unlocked) };
                EnsureProgressLoaded();
                return _progress.TryGetValue(appName, out var p) && p.Total > 0
                    ? new AchievementSummary { Total = p.Total, Unlocked = Math.Min(p.Unlocked, p.Total) }
                    : null;
            }
        }

        /// <summary>The full list in the schema's order, or null when not fetched (yet). Copies.</summary>
        public static List<AchievementEntry> EntriesFor(string appName)
        {
            if (string.IsNullOrEmpty(appName) || !EpicAccount.IsSignedIn) return null;
            lock (Gate)
            {
                if (!Details.TryGetValue(appName, out var d) || d.Entries == null) return null;
                return d.Entries.Select(Copy).ToList();
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _progress = new Dictionary<string, Progress>(StringComparer.OrdinalIgnoreCase);
                _refreshedUtc = DateTime.MinValue;
                Details.Clear();
                try { if (File.Exists(ProgressPath)) File.Delete(ProgressPath); } catch { }
            }
            Changed?.Invoke(null);
        }

        // ── fetching ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Every installed Epic game's list, one after another, at most every few
        /// minutes. Epic has no one-call summary like Steam's or Xbox's; two small queries per game
        /// is the price, and Center only lists installed games.</summary>
        public static void RefreshInBackground(bool force = false)
        {
            if (!EpicAccount.IsSignedIn) return;
            lock (Gate)
            {
                if (_refreshRunning) return;
                if (!force && DateTime.UtcNow - _refreshedUtc < RefreshEvery) return;
                _refreshRunning = true;
            }
            _ = Task.Run(async () =>
            {
                int games = 0, withList = 0;
                try
                {
                    string lang = EpicLocale();
                    foreach (var kv in InstalledNamespaces())
                    {
                        games++;
                        lock (Gate) if (!DetailRunning.Add(kv.Key)) continue;
                        try { if (await FetchDetailAsync(kv.Key, kv.Value, lang, CancellationToken.None).ConfigureAwait(false)) withList++; }
                        catch (Exception ex) { Core.InstallLog.Write("[Achievements] Epic list for " + kv.Key + " failed: " + ex.GetType().Name + ": " + ex.Message); }
                        finally { lock (Gate) DetailRunning.Remove(kv.Key); }
                    }
                    lock (Gate) _refreshedUtc = DateTime.UtcNow;
                    Core.InstallLog.Write("[Achievements] Epic: " + games + " installed, " + withList + " with achievements");
                }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] Epic refresh failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) _refreshRunning = false; }
                Changed?.Invoke(null);
            });
        }

        public static void RequestDetail(string appName)
        {
            if (string.IsNullOrEmpty(appName) || !EpicAccount.IsSignedIn) return;
            string lang = EpicLocale();
            lock (Gate)
            {
                if (DetailRunning.Contains(appName)) return;
                if (Details.TryGetValue(appName, out var d) && d.Language == lang && DateTime.UtcNow - d.FetchedUtc < DetailEvery) return;
                DetailRunning.Add(appName);
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    InstalledNamespaces().TryGetValue(appName, out string ns);
                    if (ns != null) await FetchDetailAsync(appName, ns, lang, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { Core.InstallLog.Write("[Achievements] Epic list for " + appName + " failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) DetailRunning.Remove(appName); }
            });
        }

        /// <summary>Schema plus the player's unlocks for one game. True when it has achievements.</summary>
        private static async Task<bool> FetchDetailAsync(string appName, string ns, string lang, CancellationToken ct)
        {
            var schema = await SchemaAsync(ns, lang, ct).ConfigureAwait(false);
            if (schema == null || schema.Items.Count == 0) return false;

            string token = await EpicAccount.GetAccessTokenAsync(ct).ConfigureAwait(false);
            string account = EpicAccount.AccountId;
            if (token == null || string.IsNullOrEmpty(account)) return false;

            using var doc = await QueryAsync(PlayerQuery, new { EpicAccountId = account, ProductId = schema.ProductId }, token, lang, ct).ConfigureAwait(false);
            if (doc == null) return false;

            var unlocks = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (TryPath(doc.RootElement, out var pa, "data", "PlayerProfile", "playerProfile", "productAchievements"))
            {
                // ServiceError: never started. Anything else unexpected is logged once per fetch.
                string type = Str(pa, "__typename");
                if (TryPath(pa, out var list, "data", "playerAchievements") && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in list.EnumerateArray())
                    {
                        if (!x.TryGetProperty("playerAchievement", out var a) || a.ValueKind != JsonValueKind.Object) continue;
                        string name = Str(a, "achievementName");
                        if (name == null || !(a.TryGetProperty("unlocked", out var u) && u.ValueKind == JsonValueKind.True)) continue;
                        DateTime? at = null;
                        string date = Str(a, "unlockDate");
                        if (date != null && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) && dt.Year > 2000)
                            at = dt.ToLocalTime();
                        unlocks[name] = at;
                    }
                }
                else if (type != "ServiceError") Core.InstallLog.Write("[Achievements] Epic player answer for " + appName + ": " + (type ?? "no type"));
            }

            // A locked entry shows Epic's locked face, an unlocked one the unlocked face - as in the
            // launcher.
            var entries = new List<AchievementEntry>(schema.Items.Count);
            foreach (var item in schema.Items)
            {
                var e = Copy(item.Locked);
                if (unlocks.TryGetValue(e.Id, out var at))
                {
                    e.Unlocked = true;
                    e.UnlockedAt = at;
                    e.Name = item.UnlockedName ?? e.Name;
                    e.Description = item.UnlockedDescription ?? e.Description;
                    e.IconUrl = item.UnlockedIcon ?? e.IconUrl;
                }
                entries.Add(e);
            }

            lock (Gate)
            {
                Details[appName] = new Detail { FetchedUtc = DateTime.UtcNow, Language = lang, Entries = entries };
                EnsureProgressLoaded();
                _progress[appName] = new Progress { Total = entries.Count, Unlocked = entries.Count(e => e.Unlocked) };
                SaveProgress(_progress);
            }
            Changed?.Invoke(appName);
            return true;
        }

        private static async Task<Schema> SchemaAsync(string ns, string lang, CancellationToken ct)
        {
            string key = ns + "|" + lang;
            lock (Gate) if (Schemas.TryGetValue(key, out var cached)) return cached;

            // No token: the schema is public (measured).
            using var doc = await QueryAsync(SchemaQuery, new { SandboxId = ns, Locale = lang }, null, lang, ct).ConfigureAwait(false);
            if (doc == null) return null;
            var schema = new Schema { Items = new List<SchemaItem>() };
            if (TryPath(doc.RootElement, out var rec, "data", "Achievement", "productAchievementsRecordBySandbox") && rec.ValueKind == JsonValueKind.Object)
            {
                schema.ProductId = Str(rec, "productId");
                if (rec.TryGetProperty("achievements", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var x in list.EnumerateArray())
                    {
                        if (!x.TryGetProperty("achievement", out var a) || a.ValueKind != JsonValueKind.Object) continue;
                        string id = Str(a, "name");
                        if (id == null) continue;
                        double? pct = null;
                        if (a.TryGetProperty("rarity", out var r) && r.ValueKind == JsonValueKind.Object
                            && r.TryGetProperty("percent", out var p) && p.TryGetDouble(out double pv))
                            pct = pv;
                        // A hidden achievement's locked texts are EMPTY STRINGS, not absent (18 of 18
                        // measured) - so empty counts as missing, and the unlocked text stands in.
                        // The list screen draws hidden-and-locked ones as the hidden tile anyway.
                        string uName = Text(a, "unlockedDisplayName"), uDesc = Text(a, "unlockedDescription"), uIcon = Text(a, "unlockedIconLink");
                        schema.Items.Add(new SchemaItem
                        {
                            Locked = new AchievementEntry
                            {
                                Id = id,
                                Name = Text(a, "lockedDisplayName") ?? uName,
                                Description = Text(a, "lockedDescription") ?? uDesc,
                                IconUrl = Text(a, "lockedIconLink") ?? uIcon,
                                Hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                                GlobalPercent = pct,
                            },
                            UnlockedName = uName,
                            UnlockedDescription = uDesc,
                            UnlockedIcon = uIcon,
                        });
                    }
            }
            if (string.IsNullOrEmpty(schema.ProductId)) schema.Items.Clear();
            lock (Gate) Schemas[key] = schema;
            return schema;
        }

        private static async Task<JsonDocument> QueryAsync(string query, object variables, string token, string lang, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GraphQl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", lang);
            if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Core.InstallLog.Write("[Achievements] Epic graphql answered " + (int)res.StatusCode);
                return null;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        /// <summary>AppName -> CatalogNamespace for every installed Epic game, from the launcher's
        /// manifests - the same files <see cref="EpicSource"/> lists the games from.</summary>
        private static Dictionary<string, string> InstalledNamespaces()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = EpicSource.ManifestDir;
                if (!Directory.Exists(dir)) return map;
                foreach (string file in Directory.GetFiles(dir, "*.item"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        string app = Str(doc.RootElement, "AppName"), ns = Str(doc.RootElement, "CatalogNamespace");
                        if (!string.IsNullOrEmpty(app) && !string.IsNullOrEmpty(ns)) map[app] = ns;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static AchievementEntry Copy(AchievementEntry e) => new AchievementEntry
        {
            Id = e.Id, Name = e.Name, Description = e.Description, IconUrl = e.IconUrl,
            Unlocked = e.Unlocked, UnlockedAt = e.UnlockedAt, Hidden = e.Hidden,
            GlobalPercent = e.GlobalPercent, Progress = e.Progress, ProgressMax = e.ProgressMax,
        };

        private static bool TryPath(JsonElement root, out JsonElement found, params string[] path)
        {
            found = root;
            foreach (string p in path)
                if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(p, out found)) return false;
            return true;
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Text(JsonElement o, string name)
        {
            string s = Str(o, name);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        /// <summary>
        /// The UI language as an Epic locale.
        ///
        /// MOSTLY THE BARE LANGUAGE. Epic answers "de-DE", "fr-FR", "ja-JP" in ENGLISH and only "de",
        /// "fr", "ja" translated - but Spanish and Portuguese the other way round ("es" and "pt" are
        /// English, "es-ES" and "pt-BR" translated). Measured one by one, 2026-10-03. Greek had no
        /// translation in the game tried; "el" is asked anyway and falls back to English by itself.
        /// </summary>
        private static string EpicLocale()
        {
            switch (Core.Loc.Current)
            {
                case Core.UiLanguage.German: return "de";
                case Core.UiLanguage.French: return "fr";
                case Core.UiLanguage.Korean: return "ko";
                case Core.UiLanguage.Spanish: return "es-ES";
                case Core.UiLanguage.Russian: return "ru";
                case Core.UiLanguage.Greek: return "el";
                case Core.UiLanguage.ChineseSimplified: return "zh-Hans";
                case Core.UiLanguage.ChineseTraditional: return "zh-Hant";
                case Core.UiLanguage.Italian: return "it";
                case Core.UiLanguage.Portuguese: return "pt-BR";
                case Core.UiLanguage.Japanese: return "ja";
                case Core.UiLanguage.Polish: return "pl";
                default: return "en";
            }
        }

        // ── the table on disk: counts per AppName. Not a secret. ─────────────────────────────────

        private static void EnsureProgressLoaded()
        {
            if (_progress != null) return;
            _progress = new Dictionary<string, Progress>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ProgressPath)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, Progress>>(File.ReadAllText(ProgressPath));
                if (raw != null)
                    foreach (var kv in raw)
                        if (kv.Value != null) _progress[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Achievements] Epic progress cache unreadable: " + ex.GetType().Name);
            }
        }

        private static void SaveProgress(Dictionary<string, Progress> table)
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
                Core.InstallLog.Write("[Achievements] could not save Epic progress: " + ex.Message);
            }
        }
    }
}
