using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Vertical cover art from SteamGridDB, for the games nothing local has a picture of.
    ///
    /// THE ONLY PART OF THE LIBRARY THAT USES THE NETWORK, and it does nothing at all until the user
    /// pastes their own API key. There is no key in this repository and none in the exe: a shipped
    /// key is a credential in a public binary, the quota is charged per key, and the first person to
    /// extract and abuse it takes the feature away from everyone. See CenterSettings.SteamGridDbApiKey.
    ///
    /// Every result is cached on disk, including the misses. Without caching the failures, a library
    /// with twenty unmatched games would re-ask for all twenty on every single start - which is how a
    /// key gets rate-limited by its own owner.
    /// </summary>
    /// <summary>One art option offered by the manual picker (CenterMenuWindow.GameMenu.cs).</summary>
    public sealed class ArtCandidate
    {
        public string Url { get; set; }
        /// <summary>A smaller preview, when SteamGridDB provided one. Code that renders the picker
        /// grid falls back to <see cref="Url"/> itself when this is null - downloading the full
        /// 600x900 image just to show a thumbnail would multiply the request count by however many
        /// options are offered.</summary>
        public string Thumb { get; set; }
    }

    public static class SteamGridDb
    {
        private const string ApiBase = "https://www.steamgriddb.com/api/v2/";

        public static bool HasKey => !string.IsNullOrWhiteSpace(Core.CenterSettings.SteamGridDbApiKey);

        // Internal so ArtOverrideStore can resolve its own filenames against the same folder,
        // without duplicating the LocalApplicationData\ClawTweaks\Center\artcache path in two places.
        internal static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "artcache");

        private static string IndexPath => Path.Combine(CacheDir, "index.json");

        // title key -> cached file name, or empty string for "asked, nothing found".
        private static Dictionary<string, string> _index;
        private static readonly object IndexLock = new object();

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
            return client;
        }

        private static Dictionary<string, string> Index
        {
            get
            {
                lock (IndexLock)
                {
                    if (_index != null) return _index;
                    _index = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        if (File.Exists(IndexPath))
                        {
                            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath));
                            if (loaded != null) _index = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                        }
                    }
                    catch { }
                    return _index;
                }
            }
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string tmp = IndexPath + ".tmp";
                lock (IndexLock) File.WriteAllText(tmp, JsonSerializer.Serialize(_index));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch { }
        }

        /// <summary>
        /// Fills in covers for everything still without one, and reports progress so the grid can
        /// redraw as they arrive rather than after the last request.
        ///
        /// Concurrency is deliberately ONE. This is somebody's personal API quota, not a CDN - a fan
        /// of parallel requests would finish a few seconds sooner and is exactly the traffic pattern
        /// that gets a key throttled.
        /// </summary>
        public static async Task FetchMissingAsync(IReadOnlyList<GameEntry> games, CancellationToken ct, Action onProgress)
        {
            if (!HasKey || games == null) return;

            string key = Core.CenterSettings.SteamGridDbApiKey.Trim();
            bool changed = false;
            int found = 0;

            foreach (var game in games)
            {
                ct.ThrowIfCancellationRequested();
                if (game?.ArtPath != null) continue;

                string titleKey = PlayniteSource.NormalizeTitle(game.Title);
                if (titleKey.Length == 0) continue;

                string cachedName;
                lock (IndexLock) Index.TryGetValue(titleKey, out cachedName);

                if (cachedName != null)
                {
                    // Empty means "asked before, nothing there" - do not ask again.
                    if (cachedName.Length == 0) continue;
                    string cachedPath = Path.Combine(CacheDir, cachedName);
                    if (File.Exists(cachedPath)) { game.ArtPath = cachedPath; found++; continue; }
                }

                string file = null;
                try { file = await DownloadCoverAsync(key, game.Title, titleKey, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }

                lock (IndexLock) Index[titleKey] = file ?? string.Empty;
                changed = true;

                if (file != null)
                {
                    game.ArtPath = Path.Combine(CacheDir, file);
                    found++;
                    if (found % 3 == 0) onProgress?.Invoke();
                }
            }

            if (changed) SaveIndex();
            if (found > 0) onProgress?.Invoke();
        }

        private static async Task<string> DownloadCoverAsync(string key, string title, string titleKey, CancellationToken ct)
        {
            int? id = await SearchGameIdAsync(key, title, ct, strict: true).ConfigureAwait(false);
            if (id == null) return null;

            string url = await FirstVerticalGridAsync(key, id.Value, ct).ConfigureAwait(false);
            if (url == null) return null;

            byte[] bytes;
            using (var response = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            if (bytes.Length == 0) return null;

            // Named after the title key, not after the remote file: two games can be served the same
            // image name, and the key is what we look it up by anyway.
            string ext = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string name = titleKey + ext;
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(Path.Combine(CacheDir, name), bytes);
            return name;
        }

        /// <summary>
        /// strict=true is the silent auto-fill's rule: first result only, and only when the name
        /// matches once punctuation is out of the way - an unattended background fetch must never put
        /// a stranger's cover on a tile. strict=false is the manual picker's rule: nobody is
        /// unattended there, a person is looking at the result and can retype the query, so the plain
        /// top autocomplete hit is offered even when the names do not match exactly.
        /// </summary>
        private static async Task<int?> SearchGameIdAsync(string key, string title, CancellationToken ct, bool strict)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "search/autocomplete/" + Uri.EscapeDataString(title));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Logged only on the manual picker's path (strict=false) - the silent background sweep
            // runs this for every uncovered game on the machine and must stay quiet on a miss, but a
            // person watching this one search deserves to know WHERE it failed rather than just that
            // it did. This is the search that had "no results" with no visible reason.
            if (!strict) LogArtSearch("autocomplete '" + title + "' -> " + (int)response.StatusCode + " " + Truncate(body, 500));

            if (!response.IsSuccessStatusCode) return null;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex) { if (!strict) LogArtSearch("autocomplete JSON parse failed: " + ex.Message); return null; }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    if (!strict) LogArtSearch("autocomplete response has no 'data' array");
                    return null;
                }

                // The endpoint is an autocomplete: searching "Doom" returns every Doom ever made. In
                // strict mode only an exact match (punctuation aside) is accepted - see the doc
                // comment above for why. In non-strict mode the first entry with a usable id wins.
                string want = PlayniteSource.NormalizeTitle(title);
                int seen = 0;
                foreach (var entry in data.EnumerateArray())
                {
                    seen++;
                    if (!entry.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int id)) continue;
                    if (!strict) { LogArtSearch("autocomplete matched id=" + id + " (of " + seen + "+ candidates)"); return id; }
                    if (!entry.TryGetProperty("name", out var nameProp)) continue;
                    if (PlayniteSource.NormalizeTitle(nameProp.GetString()) == want) return id;
                }
                if (!strict) LogArtSearch("autocomplete returned " + seen + " candidate(s), none usable");
                return null;
            }
        }

        private static void LogArtSearch(string message) => Core.InstallLog.Write("[ArtPicker] " + message);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        private static async Task<string> FirstVerticalGridAsync(string key, int gameId, CancellationToken ct)
        {
            // 600x900 only: that is the shape every tile in this library is drawn at, and asking the
            // API to filter costs nothing compared to downloading a banner and discovering it is wide.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "grids/game/" + gameId + "?dimensions=600x900&types=static&limit=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            foreach (var grid in data.EnumerateArray())
                if (grid.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    return url.GetString();
            return null;
        }

        /// <summary>
        /// The manual art picker's search: takes whatever text the user typed (pre-filled with the
        /// game's title, editable), finds the best-matching game on SteamGridDB, and returns every
        /// portrait cover on file for it.
        ///
        /// Deliberately NOT the strict normalized-name match FetchMissingAsync uses for its silent
        /// background fill. That strictness exists so an unattended fetch never puts a stranger's
        /// cover on a tile; here a person is looking at the result and can retype the query if the
        /// first hit is wrong, which is exactly the escape hatch a strict match would take away.
        /// </summary>
        public static async Task<IReadOnlyList<ArtCandidate>> SearchArtAsync(string query, CancellationToken ct)
        {
            var empty = Array.Empty<ArtCandidate>();
            if (!HasKey)
            {
                LogArtSearch("search skipped - no API key set");
                return empty;
            }
            if (string.IsNullOrWhiteSpace(query)) return empty;
            string key = Core.CenterSettings.SteamGridDbApiKey.Trim();
            LogArtSearch("search '" + query + "'");

            int? id;
            try { id = await SearchGameIdAsync(key, query, ct, strict: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            // Both catches used to be silent - an exception here (a DNS failure, a timed-out
            // connection, TLS) looked EXACTLY like "found nothing", and that ambiguity was the reason
            // "the search finds nothing" could not be diagnosed from a bug report alone.
            catch (Exception ex) { LogArtSearch("autocomplete threw: " + ex); return empty; }
            if (id == null) return empty;

            try { return await AllVerticalGridsAsync(key, id.Value, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogArtSearch("grids threw: " + ex); return empty; }
        }

        /// <summary>
        /// Downloads one picked candidate into the shared art cache and returns its path.
        ///
        /// Named with a fresh id rather than the title key SteamGridDb.Index uses: this is a pick for
        /// ONE tile (see GameEntry.FavoriteKey / ArtOverrideStore), and two games that happen to share
        /// a title must not end up sharing this file the way the silent auto-fill's cache deliberately
        /// does.
        /// </summary>
        public static async Task<string> DownloadForOverrideAsync(ArtCandidate candidate, CancellationToken ct)
        {
            if (candidate?.Url == null) return null;

            byte[] bytes;
            using (var response = await Http.GetAsync(candidate.Url, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            if (bytes.Length == 0) return null;

            string ext = candidate.Url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string name = "override_" + Guid.NewGuid().ToString("N") + ext;
            Directory.CreateDirectory(CacheDir);
            string path = Path.Combine(CacheDir, name);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }

        /// <summary>Every portrait grid on file for one game, in whatever order the API returns them
        /// (not re-sorted here).</summary>
        private static async Task<List<ArtCandidate>> AllVerticalGridsAsync(string key, int gameId, CancellationToken ct)
        {
            var results = new List<ArtCandidate>();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "grids/game/" + gameId + "?dimensions=600x900&types=static&limit=24");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LogArtSearch("grids/game/" + gameId + " -> " + (int)response.StatusCode + " " + Truncate(body, 500));
            if (!response.IsSuccessStatusCode) return results;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex) { LogArtSearch("grids JSON parse failed: " + ex.Message); return results; }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    LogArtSearch("grids response has no 'data' array");
                    return results;
                }

                foreach (var grid in data.EnumerateArray())
                {
                    if (!grid.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String) continue;
                    string thumb = grid.TryGetProperty("thumb", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.String
                        ? thumbProp.GetString() : null;
                    results.Add(new ArtCandidate { Url = urlProp.GetString(), Thumb = thumb });
                }
                LogArtSearch("grids/game/" + gameId + " -> " + results.Count + " portrait candidate(s)");
                return results;
            }
        }

        /// <summary>Checks a key by asking for one search result. Used by the key entry screen so the
        /// user finds out immediately, rather than by noticing that no covers ever appear.</summary>
        public static async Task<bool> VerifyKeyAsync(string key, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "search/autocomplete/portal");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }
    }
}
