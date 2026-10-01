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

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// The READ half of community presets, ported from the Game Bar widget
    /// (XboxGamingBar/Features/GamePresets/GamingWidget.CommunityIndex.cs) into the Library.
    ///
    /// ⚠ THIS DOES NOT TALK TO DISCORD, and it must never learn how. Reading the forum needs a bot
    /// token, and a bot token shipped in a binary is a bot token everyone has — doubly so here, where
    /// Center is a PUBLIC repository. The janitor holds the token, verifies every post's signature
    /// code on a machine that is allowed to hold the key, and publishes the survivors as one flat
    /// file. This side only fetches that file; the signature check has already happened.
    ///
    /// Fetch, cache and staleness are the SAME SHAPE as <see cref="GamePresets"/> — same cache folder
    /// root, same "parse before you replace" rule, same "a stale cache beats nothing" handling, same
    /// System.Text.Json. One pattern, learned once.
    ///
    /// WRITING (sharing a preset, rating one) is NOT here. The signing key lives in the helper, so
    /// those go over the Center→helper pipe; see CenterMenuWindow.Community.cs.
    ///
    /// Contract: the dev repo's Doku/SPEC_Community_Preset_Post.md. The schema is a flat list of
    /// string fields — see the widget's ParseCommunityIndex for the authoritative field set.
    /// </summary>
    public static class CommunityPresets
    {
        /// <summary>
        /// One shared preset, as a bag of string fields exactly as the janitor published them. A thin
        /// wrapper rather than a typed record on purpose: the schema only ever appends fields
        /// (SPEC §7), and a dictionary carries a field this build has never heard of without a code
        /// change — which is what lets an older Center keep working against a newer file.
        /// </summary>
        public sealed class Preset
        {
            private readonly Dictionary<string, string> _fields;

            internal Preset(Dictionary<string, string> fields) => _fields = fields;

            public string Get(string key) => _fields.TryGetValue(key, out string v) ? v : "";

            /// <summary>Did THIS machine post it. authorId is device-derived and stamped by the
            /// helper; see the authorId note in the dev repo's CommunityPrefill.</summary>
            public bool IsOwn(string myAuthorId)
                => !string.IsNullOrWhiteSpace(myAuthorId)
                   && Get("authorId").Equals(myAuthorId, StringComparison.Ordinal);
        }

        private const string IndexUrl =
            "https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaks/master/manifest/community-presets.json";

        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "community-presets.json");

        /// <summary>Fifteen minutes. Was six hours like the widget's, but the widget has a refresh
        /// button and the Library had none - a rating posted a minute ago stayed invisible here for
        /// the rest of the afternoon (user, 2026-10-01). Opening the overlay and Y in the library now
        /// also force a network read; this only bounds how stale the launch-screen banner can be.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

        private static List<Preset> _all;
        private static Task _loading;
        private static readonly object Gate = new object();

        public static bool Loaded => _all != null;

        /// <summary>How the last load ended, for the banner's three-state line ("loading" vs "none
        /// yet" vs a count) — the same distinction the widget's CommunityListStatus draws.</summary>
        public static string Status { get; private set; } = "Not loaded yet";

        /// <summary>
        /// Loads the index once per session, from cache when fresh and the network otherwise. Safe to
        /// call from anywhere; concurrent callers await the same load.
        /// </summary>
        public static Task EnsureLoadedAsync(CancellationToken ct, bool forceRefresh = false)
        {
            lock (Gate)
            {
                if (_all != null && !forceRefresh) return Task.CompletedTask;
                // A forced refresh does NOT clear what is loaded: the screen keeps showing the last
                // list until the new one has parsed, instead of flashing "Loading…" over it.
                if (forceRefresh)
                {
                    if (_loading != null && !_loading.IsCompleted && _loadingIsForced) return _loading;
                    _loadingIsForced = true;
                    return _loading = Task.Run(() => LoadAsync(ct, skipCache: true), ct);
                }
                if (_loading != null) return _loading;
                _loadingIsForced = false;
                return _loading = Task.Run(() => LoadAsync(ct, skipCache: false), ct);
            }
        }

        private static bool _loadingIsForced;

        private static async Task LoadAsync(CancellationToken ct, bool skipCache)
        {
            string json = null;

            try
            {
                if (!skipCache && File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < MaxAge)
                    json = File.ReadAllText(CachePath);
            }
            catch { }

            bool fromNetwork = false;
            if (json == null)
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
                        // A forced read asks past raw.githubusercontent's CDN cache (minutes old
                        // otherwise), so a rating the widget already shows is not missing here.
                        string url = skipCache ? IndexUrl + "?t=" + DateTime.UtcNow.Ticks : IndexUrl;
                        json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                    }
                    fromNetwork = true;

                    // Replace the cache only on a successful parse below — writing the response first
                    // would let one bad publish, or a captive portal's login page, overwrite a working
                    // cache and leave the feature broken offline. So the write is deferred until Parse
                    // has returned something usable.
                }
                catch (Exception ex)
                {
                    // A 404 is the EXPECTED answer until the index is first published, so this is not
                    // an error, and it must never clear a cache that already works.
                    Core.InstallLog.Write("Community index fetch failed: " + ex.Message);
                    try { if (File.Exists(CachePath)) json = File.ReadAllText(CachePath); }
                    catch { }
                }
            }

            List<Preset> parsed = null;
            if (json != null)
            {
                try { parsed = Parse(json); }
                catch (Exception ex) { Core.InstallLog.Write("Community index parse failed: " + ex.Message); }
            }

            if (parsed == null)
            {
                // Keep whatever we had. "Could not read it" is not "there is none".
                if (_all == null) Status = "No shared presets published yet";
                return;
            }

            _all = parsed;
            Status = parsed.Count == 1 ? "1 shared preset" : $"{parsed.Count} shared presets";

            if (fromNetwork)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                    string tmp = CachePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, CachePath, overwrite: true);
                }
                catch (Exception ex) { Core.InstallLog.Write("Community index cache write failed: " + ex.Message); }
            }

            Core.InstallLog.Write($"Community index loaded: {parsed.Count} preset(s)");
        }

        /// <summary>
        /// An EMPTY list is a valid answer and returns an empty list; only a broken document returns
        /// null, so the caller can tell "nothing published" from "could not read" and keep its cache
        /// on the second.
        /// </summary>
        private static List<Preset> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("presets", out var presets) ||
                presets.ValueKind != JsonValueKind.Array) return null;

            var list = new List<Preset>();
            foreach (var entry in presets.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in entry.EnumerateObject())
                {
                    string s = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                    if (!string.IsNullOrEmpty(s) && s != "-") row[prop.Name] = s;
                }
                if (row.ContainsKey("presetId")) list.Add(new Preset(row));
            }
            return list;
        }

        // ─────────────────────────────── matching ───────────────────────────────

        /// <summary>
        /// The presets that belong to one library game, best first.
        ///
        /// TWO WAYS TO MATCH, and the order of preference does not matter because both feed one sort.
        /// The store key is exact; the title is the fallback, because the same game reaches different
        /// people through different stores — a Steam copy and an Epic copy produce different keys for
        /// one game, and refusing to show those to each other would empty the feature out for no
        /// reason a player could understand. The widget matches the same two ways.
        /// </summary>
        /// <summary>Every published preset, for the browse screen on Home - a copy, so a background
        /// refresh cannot change the list under the cursor.</summary>
        public static List<Preset> All() => _all == null ? new List<Preset>() : new List<Preset>(_all);

        /// <summary>
        /// The browse keypad's bucket for a title: its first letter A-Z, '#' for anything else. A
        /// leading "the"/"a"/"an" is dropped so The Witcher 3 sits under W - the widget's browse list
        /// does the same (GamingWidget.CommunityBrowse.cs, BrowseBucketOf).
        /// </summary>
        public static char BrowseBucket(string title)
        {
            string t = (title ?? "").Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
            foreach (string article in new[] { "the ", "a ", "an " })
                if (t.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { t = t.Substring(article.Length).TrimStart(); break; }
            if (t.Length == 0) return '#';
            char c = char.ToUpperInvariant(t[0]);
            return c >= 'A' && c <= 'Z' ? c : '#';
        }

        public static List<Preset> ForGame(GameEntry game)
        {
            var result = new List<Preset>();
            if (_all == null || game == null) return result;

            string store = StoreCode(game.Store);
            string id    = game.Id ?? "";
            string title = NormalizeTitle(game.Title);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(id)) return result;

            foreach (var p in _all)
            {
                bool sameKey = !string.IsNullOrEmpty(id)
                    && p.Get("gameId").Equals(id, StringComparison.OrdinalIgnoreCase)
                    && p.Get("gameStore").Equals(store, StringComparison.OrdinalIgnoreCase);

                bool sameTitle = !string.IsNullOrEmpty(title)
                    && NormalizeTitle(p.Get("gameTitle")).Equals(title, StringComparison.Ordinal);

                // A widget post for a game the helper only knew by its process carries
                // gameStore=local and the EXE NAME as gameId (Brotato: "brotato.exe", titled
                // "Brotato.exe"). A Steam library entry has neither - its id is the AppID - so the
                // exe has to be looked for where the game is installed.
                bool sameExe = !sameKey && !sameTitle && HasExe(game, p.Get("gameId"));

                if (sameKey || sameTitle || sameExe) result.Add(p);
            }

            SortBestFirst(result);
            return result;
        }

        /// <summary>
        /// Best first, and "best" is not the bare average. Ported verbatim in intent from the widget's
        /// CommunityPresetsForThisGame.
        ///
        /// ⚠ THE DEVICE COMES BEFORE THE RATING. A preset is a set of watts and frame rates, and those
        /// do not carry across models — a five-star profile measured on hardware with a different power
        /// envelope is a good report about a machine this is not. Same-device entries are a block of
        /// their own, sorted among themselves, not merely a tiebreak.
        ///
        /// ⚠ A LONE FIVE-STAR MUST NOT OUTRANK A WELL-LIKED PRESET. The score is a Bayesian average
        /// pulled towards the middle by how little is known; the DISPLAYED number stays the real
        /// average, because a shown figure nobody can reproduce by adding up the ratings is worse than
        /// none.
        /// </summary>
        private static void SortBestFirst(List<Preset> list)
        {
            string myDevice = DeviceCode();
            int SameDevice(Preset x)
                => !string.IsNullOrEmpty(myDevice)
                   && x.Get("device").Equals(myDevice, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            list.Sort((a, b) =>
            {
                int byDevice = SameDevice(b).CompareTo(SameDevice(a));
                if (byDevice != 0) return byDevice;

                int byScore = RatingScore(b).CompareTo(RatingScore(a));
                if (byScore != 0) return byScore;

                // Same standing: the newer one, likelier to match a current build of the game.
                return string.CompareOrdinal(b.Get("createdAt"), a.Get("createdAt"));
            });
        }

        /// <summary>Three votes' worth of doubt — small enough that four or five real ratings decide
        /// the order, large enough that a lone five-star cannot.</summary>
        private const double RatingPriorWeight = 3.0;

        /// <summary>The middle of the scale. An unrated preset sorts as "unknown", not as "bad".</summary>
        private const double RatingPriorMean = 3.0;

        /// <summary>What the list sorts on. NOT what it shows.</summary>
        private static double RatingScore(Preset p)
        {
            if (!double.TryParse(p.Get("ratingAvg"), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double avg)) return RatingPriorMean;
            if (!int.TryParse(p.Get("ratingCount"), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out int n) || n <= 0) return RatingPriorMean;
            return (RatingPriorWeight * RatingPriorMean + avg * n) / (RatingPriorWeight + n);
        }

        // ─────────────────────────────── shared, not published yet ───────────────────────────────

        /// <summary>
        /// Games this machine shared a preset for that the janitor has not published yet (user,
        /// 2026-10-01). Between the post and the next janitor round the index does not know the
        /// preset, so "you already shared one" could not be said and the Share button came back -
        /// inviting a second post for the same game. One line per game:
        /// <c>sharedAtUtc \t store \t id \t title</c>, beside the index cache.
        ///
        /// It ends on its own: once the index carries a preset by this machine for the game the line
        /// is dropped, and after <see cref="PendingMaxAge"/> it is ignored (a rejected or deleted post
        /// must not block sharing for ever). Center only - a post from the widget is not seen here.
        /// </summary>
        private static string PendingPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "community-pending.tsv");

        private static readonly TimeSpan PendingMaxAge = TimeSpan.FromDays(14);

        public static void MarkShared(GameEntry game)
        {
            if (game == null) return;
            try
            {
                var lines = ReadPending().Where(l => !SameGame(l, game)).Select(l => l.Raw).ToList();
                lines.Add(string.Join("\t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    StoreCode(game.Store), Clean(game.Id), Clean(game.Title)));
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
                string tmp = PendingPath + ".tmp";
                File.WriteAllLines(tmp, lines);
                File.Move(tmp, PendingPath, overwrite: true);
            }
            catch (Exception ex) { Core.InstallLog.Write("Community pending write failed: " + ex.Message); }
        }

        /// <summary>True while this machine shared a preset for the game that the published index
        /// does not carry yet.</summary>
        public static bool IsPending(GameEntry game, string myAuthorId)
        {
            if (game == null) return false;
            var hit = ReadPending().FirstOrDefault(l => SameGame(l, game));
            if (hit == null || DateTime.UtcNow - hit.SharedAt > PendingMaxAge) return false;
            // Published now: the index has this machine's preset for the game - the memory is done.
            if (!string.IsNullOrEmpty(myAuthorId) && ForGame(game).Any(p => p.IsOwn(myAuthorId)))
            {
                Forget(game);
                return false;
            }
            return true;
        }

        private static void Forget(GameEntry game)
        {
            try
            {
                var keep = ReadPending().Where(l => !SameGame(l, game)).Select(l => l.Raw).ToList();
                File.WriteAllLines(PendingPath, keep);
            }
            catch { }
        }

        private sealed class PendingLine
        {
            public string Raw; public DateTime SharedAt; public string Store, Id, Title;
        }

        private static List<PendingLine> ReadPending()
        {
            var list = new List<PendingLine>();
            try
            {
                if (!File.Exists(PendingPath)) return list;
                foreach (string raw in File.ReadAllLines(PendingPath))
                {
                    var c = raw.Split('\t');
                    if (c.Length < 4) continue;
                    if (!DateTime.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                    list.Add(new PendingLine { Raw = raw, SharedAt = at, Store = c[1], Id = c[2], Title = c[3] });
                }
            }
            catch { }
            return list;
        }

        private static bool SameGame(PendingLine l, GameEntry g)
            => (!string.IsNullOrEmpty(g.Id) && l.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase)
                && l.Store.Equals(StoreCode(g.Store), StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrEmpty(g.Title) && NormalizeTitle(l.Title) == NormalizeTitle(g.Title));

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        // ─────────────────────────────── identity ───────────────────────────────

        /// <summary>
        /// This machine's device code, for the same-device sort above and for prefilling a new post.
        ///
        /// ⚠ CENTER CANNOT TELL CLAW 7 FROM CLAW 8 (both are DeviceDetect.Model.A2VM). The accurate
        /// variant (a2vm7 / a2vm8) lives only in the helper's ClawIdentityResult, so a shared post's
        /// device is taken from the helper, not from here — see CommunityIdentity in
        /// CenterMenuWindow.Community.cs. This value is enough for the read-side preference, where
        /// "a2vm" groups both correctly.
        /// </summary>
        public static string DeviceCode()
        {
            switch (Core.DeviceDetect.Detect().Model)
            {
                case Core.DeviceDetect.Model.A2VM: return "a2vm";
                case Core.DeviceDetect.Model.Ex:   return "ex";
                case Core.DeviceDetect.Model.A1M:  return "a1m";
                default: return "";
            }
        }

        /// <summary>
        /// Whether a preset was measured on this machine's KIND of Claw. Compared by family, not by
        /// exact code: Center cannot tell Claw 7 from Claw 8 (both "a2vm"), and a post says "a2vm7" or
        /// "a2vm8" - the same chip and the same power envelope. An unidentified machine matches
        /// nothing, and the list then shows everything rather than nothing.
        /// </summary>
        public static bool SameDeviceFamily(Preset p, string myDevice)
        {
            string mine = DeviceFamily(myDevice);
            if (mine.Length == 0) return true;
            return DeviceFamily(p.Get("device")) == mine;
        }

        private static string DeviceFamily(string code)
        {
            code = (code ?? "").Trim().ToLowerInvariant();
            return code.StartsWith("a2vm") ? "a2vm" : code;
        }

        /// <summary>
        /// The executable a profile for this game has to be keyed on, for adopting a preset: the
        /// store's own ExePath, else the exe of an existing ClawTweaks profile in the install folder,
        /// else an exe a shared post named that really exists in the install folder. Null when none
        /// is known - then the game has to have been started once.
        /// </summary>
        public static string ResolveExe(GameEntry game, IEnumerable<Preset> hints)
        {
            if (game == null) return null;
            if (!string.IsNullOrEmpty(game.ExePath) && File.Exists(game.ExePath)) return game.ExePath;

            string fromProfile = ClawProfiles.PerformanceExeFor(game);
            if (!string.IsNullOrEmpty(fromProfile)) return fromProfile;

            if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir)) return null;
            foreach (var p in hints ?? Enumerable.Empty<Preset>())
            {
                string exe = p.Get("gameId");
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                try
                {
                    string direct = Path.Combine(game.InstallDir, exe);
                    if (File.Exists(direct)) return direct;
                    foreach (var sub in Directory.EnumerateDirectories(game.InstallDir))
                    {
                        string nested = Path.Combine(sub, exe);
                        if (File.Exists(nested)) return nested;
                    }
                }
                catch { }
            }
            return null;
        }

        public static string StoreCode(GameStore store)
        {
            switch (store)
            {
                case GameStore.Steam:     return "steam";
                case GameStore.Epic:      return "epic";
                case GameStore.Xbox:      return "xbox";
                case GameStore.Ubisoft:   return "ubisoft";
                case GameStore.EA:        return "ea";
                case GameStore.BattleNet: return "battlenet";
                case GameStore.Gog:       return "gog";
                default:                  return "local";   // Playnite ROMs and anything else
            }
        }

        // ─────────────────────────────── display ───────────────────────────────

        public static string DeviceName(string key)
        {
            switch (key)
            {
                case "a1m":   return "Claw A1M";
                case "a2vm7": return "Claw 7 AI+";
                case "a2vm8": return "Claw 8 AI+";
                case "a2vm":  return "Claw 7/8 AI+";
                case "ex":    return "Claw 8 EX";
                default:      return string.IsNullOrEmpty(key) ? "" : key;
            }
        }

        /// <summary>The reader's side of the Display/canonical split (SPEC, HANDOVER §5): the stored
        /// value stays the small identifier, the reader turns it into something a person reads. Only
        /// the fields that are not already self-explanatory need an entry.</summary>
        public static string Display(string key, string value)
        {
            if (key == "cpuBoostMode")
            {
                switch (value)
                {
                    case "0": return "0 (" + Core.Loc.T("Disabled") + ")";
                    case "1": return "1 (" + Core.Loc.T("Enabled") + ")";
                    case "2": return "2 (" + Core.Loc.T("Aggressive") + ")";
                    case "3": return "3 (" + Core.Loc.T("Efficient Enabled") + ")";
                    case "4": return "4 (" + Core.Loc.T("Efficient Aggressive") + ")";
                    case "5": return "5 (" + Core.Loc.T("Aggressive At Guaranteed") + ")";
                    case "6": return "6 (" + Core.Loc.T("Efficient Aggressive At Guaranteed") + ")";
                }
            }
            if (key == "fpsCapMode")
                return value == "intel" ? "Intel" : value == "rtss" ? "RTSS" : value;
            if (key == "upscalerSource")
            {
                switch (value)
                {
                    case "optiscaler-opticlick": return Core.Loc.T("OptiScaler (via OptiClick)");
                    case "optiscaler-manual":    return Core.Loc.T("OptiScaler (manual)");
                    case "ingame":               return Core.Loc.T("in-game");
                }
            }
            return value;
        }

        // ─────────────────────────────── ratings ───────────────────────────────

        public readonly struct RatingOption
        {
            public readonly string Value;
            private readonly string _text;
            /// <summary>Translated when READ - the canonical Value is what gets posted.</summary>
            public string Text => Core.Loc.T(_text);
            public RatingOption(string value, string text) { Value = value; _text = text; }
        }

        public readonly struct RatingCategory
        {
            public readonly string Key;
            private readonly string _label;
            public string Label => Core.Loc.T(_label);
            public readonly RatingOption[] Options;
            public RatingCategory(string key, string label, RatingOption[] options)
            { Key = key; _label = label; Options = options; }
        }

        /// <summary>The categories a rating can answer, in the order they are asked. Each option is
        /// (canonical value, what the user reads) — the same split the post makes, which is why
        /// translating this would change no stored value. Ported from the widget's RatingCategories.</summary>
        public static readonly RatingCategory[] RatingCategories =
        {
            new RatingCategory("fbPerf", "Frame rate", new[]
            {
                new RatingOption("matches", "Matched the stated fps"),
                new RatingOption("slightlyLower", "Slightly lower"),
                new RatingOption("muchLower", "Much lower"),
                new RatingOption("better", "Better than stated"),
            }),
            new RatingCategory("fbStability", "Stability", new[]
            {
                new RatingOption("clean", "Ran clean"),
                new RatingOption("someStutter", "Occasional stutter"),
                new RatingOption("frequentStutter", "Frequent stutter"),
                new RatingOption("crashes", "Crashed"),
            }),
            // NOT a battery question: whether the stated wattage was enough to reach the stated frame
            // rate, which is what qualifies the two numbers in the preset's title.
            new RatingCategory("fbTdp", "TDP", new[]
            {
                new RatingOption("asStated", "The stated TDP was enough"),
                new RatingOption("hadToRaise", "I had to raise it"),
                new RatingOption("couldLower", "I could lower it"),
            }),
            new RatingCategory("fbVisuals", "Image", new[]
            {
                new RatingOption("good", "Looked good"),
                new RatingOption("tooSoft", "Too soft"),
                new RatingOption("artifacts", "Shimmering or artefacts"),
            }),
            new RatingCategory("fbAdoption", "Adopting it", new[]
            {
                new RatingOption("asIs", "Worked as-is"),
                new RatingOption("smallChanges", "Needed small changes"),
                new RatingOption("didNotFit", "Did not fit"),
            }),
        };

        /// <summary>"★★★★☆  4.1  (7)" or null when nobody has rated it. Null rather than "0.0": no
        /// ratings and a bad rating are different answers, and a printed zero reads as the second.</summary>
        public static string RatingLine(Preset p)
        {
            string avg = p.Get("ratingAvg");
            string count = p.Get("ratingCount");
            if (string.IsNullOrEmpty(avg) || string.IsNullOrEmpty(count)) return null;
            if (!double.TryParse(avg, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return null;

            int filled = Math.Max(0, Math.Min(5, (int)Math.Round(a, MidpointRounding.AwayFromZero)));
            return new string('★', filled) + new string('☆', 5 - filled)
                 + "  " + a.ToString("0.0", CultureInfo.InvariantCulture) + "  (" + count + ")";
        }

        /// <summary>
        /// Turns "fbPerf=matches:5,fbTdp=hadToRaise:2" into readable lines, most-agreed first. Only
        /// what people actually answered — a category nobody touched stays silent rather than showing
        /// a zero, because a printed "0 said it crashed" is a statement about crashes nobody made.
        /// </summary>
        public static List<string> RatingBreakdownLines(string breakdown)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(breakdown)) return lines;

            var byCategory = new Dictionary<string, List<(string Text, int N)>>();
            foreach (string part in breakdown.Split(','))
            {
                int colon = part.LastIndexOf(':');
                int eq = part.IndexOf('=');
                if (colon <= 0 || eq <= 0 || eq > colon) continue;
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1, colon - eq - 1);
                if (!int.TryParse(part.Substring(colon + 1), out int n)) continue;

                string text = RatingOptionText(key, value);
                if (text == null) continue;
                if (!byCategory.TryGetValue(key, out var l)) byCategory[key] = l = new List<(string, int)>();
                l.Add((text, n));
            }

            foreach (var cat in RatingCategories)
            {
                if (!byCategory.TryGetValue(cat.Key, out var l)) continue;
                l.Sort((a, b) => b.N.CompareTo(a.N));
                lines.Add(cat.Label + ": " + string.Join(", ", l.Select(x => $"{x.Text} ({x.N})")));
            }
            return lines;
        }

        private static string RatingOptionText(string key, string value)
        {
            foreach (var cat in RatingCategories)
            {
                if (cat.Key != key) continue;
                foreach (var opt in cat.Options)
                    if (opt.Value == value) return opt.Text;
            }
            return null;   // an option a newer janitor knows and this build does not
        }

        /// <summary>Letters and digits only, lower case — the same rule GamePresets.Normalize and
        /// PlayniteSource use, so a title matches the same way everywhere and against a file written
        /// by somebody else.</summary>
        /// <summary>
        /// Whether a preset's gameId names an executable of this library game. Only ids that ARE an
        /// exe name are tried, and only by file name - the known ExePath first, then the install
        /// folder and its direct subfolders (Unreal games keep theirs one level down at least, the
        /// rest at the root). Answers are cached per game and exe, because ForGame runs on every
        /// launch-screen render and a folder probe is disk I/O.
        /// </summary>
        private static bool HasExe(GameEntry game, string exe)
        {
            if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            if (!string.IsNullOrEmpty(game.ExePath) &&
                Path.GetFileName(game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase)) return true;

            string dir = game.InstallDir;
            if (string.IsNullOrEmpty(dir)) return false;

            string key = dir + "|" + exe.ToLowerInvariant();
            lock (ExeProbeCache)
                if (ExeProbeCache.TryGetValue(key, out bool known)) return known;

            bool found = false;
            try
            {
                if (Directory.Exists(dir))
                {
                    found = File.Exists(Path.Combine(dir, exe));
                    if (!found)
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                            if (File.Exists(Path.Combine(sub, exe))) { found = true; break; }
                }
            }
            catch { }

            lock (ExeProbeCache) ExeProbeCache[key] = found;
            return found;
        }

        private static readonly Dictionary<string, bool> ExeProbeCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            // A title that is really a file name ("Brotato.exe") is the game's name plus an extension.
            if (title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 4);
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
