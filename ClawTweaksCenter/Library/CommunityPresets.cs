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

        /// <summary>Six hours. The file changes when somebody posts, which is not often, and a
        /// handheld should not spend a request per launch screen to find that out. Same figure the
        /// widget uses, for the same reason.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

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
                if (forceRefresh) { _all = null; _loading = null; }
                return _loading ?? (_loading = Task.Run(() => LoadAsync(ct), ct));
            }
        }

        private static async Task LoadAsync(CancellationToken ct)
        {
            string json = null;

            try
            {
                if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < MaxAge)
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
                        json = await http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
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

                if (sameKey || sameTitle) result.Add(p);
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
                    case "0": return "0 (Disabled)";
                    case "1": return "1 (Enabled)";
                    case "2": return "2 (Aggressive)";
                    case "3": return "3 (Efficient Enabled)";
                    case "4": return "4 (Efficient Aggressive)";
                    case "5": return "5 (Aggressive At Guaranteed)";
                    case "6": return "6 (Efficient Aggressive At Guaranteed)";
                }
            }
            if (key == "upscalerSource")
            {
                switch (value)
                {
                    case "optiscaler-opticlick": return "OptiScaler (via OptiClick)";
                    case "optiscaler-manual":    return "OptiScaler (manual)";
                    case "ingame":               return "in-game";
                }
            }
            return value;
        }

        // ─────────────────────────────── ratings ───────────────────────────────

        public readonly struct RatingOption
        {
            public readonly string Value;
            public readonly string Text;
            public RatingOption(string value, string text) { Value = value; Text = text; }
        }

        public readonly struct RatingCategory
        {
            public readonly string Key;
            public readonly string Label;
            public readonly RatingOption[] Options;
            public RatingCategory(string key, string label, RatingOption[] options)
            { Key = key; Label = label; Options = options; }
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
        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
