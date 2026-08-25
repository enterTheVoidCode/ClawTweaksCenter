using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// What the ClawTweaks catalog knows about a game's upscaler route, for the launch screen.
    ///
    /// THE SAME FILE THE WIDGET READS — <c>manifest/game-presets.json</c> on the public repo. Center
    /// shows a small part of it: whether the game has an OptiClick or OptiScaler route, and whether
    /// OptiScaler's wiki has a page for it. The performance values in there are the widget's
    /// business and are deliberately not read here; Center does not apply settings.
    ///
    /// ⚠ THE ANTI-CHEAT RULE TRAVELS WITH THE ROUTE, ALWAYS. "OptiClick: yes" on its own is an
    /// invitation to inject into an online game. The catalog carries the caveat in the same block as
    /// the answer for exactly that reason, and the detail panel prints it whenever it prints the
    /// route. Do not build a view that shows one without the other.
    ///
    /// FETCHED LAZILY, and that is deliberate: the file is 3 MB, this is a handheld, and the answer
    /// is only ever needed once a launch screen is on the actual screen. Nothing loads it during a
    /// library scan.
    /// </summary>
    public static class GamePresets
    {
        public sealed class Info
        {
            public string Title;

            /// <summary>"OptiClick" or "OptiScaler" - the validated external route. Null for the 584
            /// catalogued games with neither.</summary>
            public string Tool;

            public bool Available;

            /// <summary>OptiScaler's per-game wiki PAGE NAME, e.g. "007-First-Light". Independent of
            /// the route above: a page can exist for a game with no validated route, which is why it
            /// is a third badge rather than a property of the first two.</summary>
            public string WikiPage;

            public string SupportPath;     // "Yes - OptiClick official (Intel)"
            public string Output;          // "XeSS"
            public string Preset;          // "Balanced"
            public string FgOutput;        // "XeFG x3 -> 116 FPS cap"
            public string Requirements;
            public string AntiCheat;
            public string Policy;

            public bool IsOptiClick => Available && string.Equals(Tool, "OptiClick", StringComparison.OrdinalIgnoreCase);
            public bool IsOptiScaler => Available && string.Equals(Tool, "OptiScaler", StringComparison.OrdinalIgnoreCase);
            public bool HasWiki => !string.IsNullOrWhiteSpace(WikiPage);

            /// <summary>Whether this entry says anything worth putting on screen. A catalogued game
            /// with no route and no wiki page is the majority case and gets no banner - an empty row
            /// of badges is worse than none.</summary>
            public bool HasAnything => IsOptiClick || IsOptiScaler || HasWiki;
        }

        private const string CatalogUrl =
            "https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaks/master/manifest/game-presets.json";

        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "game-presets.json");

        /// <summary>A curated catalog changes on the order of weeks; a daily check is already
        /// generous. Same figure the widget uses, for the same reason.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private static Dictionary<int, Info> _byAppId;
        private static Dictionary<string, Info> _byTitle;
        private static Task _loading;
        private static readonly object Gate = new object();

        public static bool Loaded => _byTitle != null;

        /// <summary>
        /// Loads the catalog once per session, from cache when it is fresh enough and from the
        /// network otherwise. Safe to call from anywhere - concurrent callers await the same load.
        /// </summary>
        public static Task EnsureLoadedAsync(CancellationToken ct)
        {
            lock (Gate)
            {
                if (_byTitle != null) return Task.CompletedTask;
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

            if (json == null)
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
                        json = await http.GetStringAsync(CatalogUrl, ct).ConfigureAwait(false);
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                        string tmp = CachePath + ".tmp";
                        File.WriteAllText(tmp, json);
                        File.Move(tmp, CachePath, overwrite: true);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("Game catalog fetch failed: " + ex.Message);

                    // A STALE CACHE BEATS NOTHING. The alternative is the launch screen claiming a
                    // game has no upscaler route because the device happens to be offline, and "no"
                    // and "could not ask" are not the same answer.
                    try { if (File.Exists(CachePath)) json = File.ReadAllText(CachePath); }
                    catch { }
                }
            }

            var byAppId = new Dictionary<int, Info>();
            var byTitle = new Dictionary<string, Info>(StringComparer.Ordinal);

            if (json != null)
            {
                try { Parse(json, byAppId, byTitle); }
                catch (Exception ex) { Core.InstallLog.Write("Game catalog parse failed: " + ex.Message); }
            }

            _byAppId = byAppId;
            _byTitle = byTitle;
        }

        private static void Parse(string json, Dictionary<int, Info> byAppId, Dictionary<string, Info> byTitle)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("presets", out var presets) ||
                presets.ValueKind != JsonValueKind.Array) return;

            foreach (var p in presets.EnumerateArray())
            {
                var info = new Info
                {
                    Title = Str(p, "title"),
                    WikiPage = Str(p, "optiScalerWikiPage"),
                };

                if (p.TryGetProperty("upscaler", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    info.Tool = Str(u, "tool");
                    info.Available = u.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
                    info.SupportPath = Str(u, "supportPath");
                    info.Output = Str(u, "output");
                    info.Preset = Str(u, "preset");
                    info.FgOutput = Str(u, "fgOutput");
                    info.Requirements = Str(u, "requirements");
                    info.AntiCheat = Str(u, "antiCheatRule");
                    info.Policy = Str(u, "policy");
                }

                if (!info.HasAnything) continue;   // 584 of 782 entries, and none of them says anything here

                if (p.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object &&
                    ids.TryGetProperty("steam", out var steam) && steam.TryGetInt32(out int appId))
                    byAppId[appId] = info;

                // The catalog's own key, normalised the same way as our titles. Measured against the
                // file: all 782 keys stay distinct through that normalisation, so it cannot silently
                // collapse two games into one.
                string key = Normalize(Str(p, "key") ?? info.Title);
                if (key.Length > 0) byTitle[key] = info;
            }
        }

        /// <summary>
        /// What the catalog says about one library entry, or null.
        ///
        /// BY STEAM APPID FIRST. It is an exact identity, while a title match is a guess that happens
        /// to be right - and 634 of the 782 catalogued games carry one. The title is the fallback
        /// that makes Epic, Xbox and the smaller stores work at all.
        /// </summary>
        public static Info For(GameEntry game)
        {
            if (game == null || _byTitle == null) return null;

            if (game.Store == GameStore.Steam && _byAppId != null &&
                int.TryParse(game.Id, out int appId) && _byAppId.TryGetValue(appId, out var byId))
                return byId;

            string key = Normalize(game.Title);
            return key.Length > 0 && _byTitle.TryGetValue(key, out var byName) ? byName : null;
        }

        /// <summary>Letters and digits only, lower case. Deliberately the same rule
        /// PlayniteSource.NormalizeTitle uses - two normalisers for the same job drift apart, and
        /// this one has to agree with a file written by somebody else.</summary>
        private static string Normalize(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string Str(JsonElement parent, string name)
            => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
               ? NullIfBlank(v.GetString()) : null;

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
