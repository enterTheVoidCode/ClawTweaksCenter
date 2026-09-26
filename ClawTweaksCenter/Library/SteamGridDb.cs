using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Successful images are cached on disk. Confirmed misses last for the current app session so
    /// unmatched games do not repeatedly spend quota, but new artwork is discovered on a later start.
    /// Failed requests remain retryable, including legacy empty entries that may record an outage.
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

        private static readonly HttpClient Http = CreateClient();
        private static readonly SteamGridDbCache AutomaticArt = new SteamGridDbCache(Http, CacheDir);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
            return client;
        }

        /// <summary>Fills missing covers sequentially and reports progress as they arrive.</summary>
        public static Task FetchMissingAsync(IReadOnlyList<GameEntry> games, CancellationToken ct, Action onProgress)
            => AutomaticArt.FetchMissingAsync(games, Core.CenterSettings.SteamGridDbApiKey, ct, onProgress);

        /// <summary>Fetches one wide backdrop on demand. Steam's local hero still wins at the caller.</summary>
        public static Task<string> EnsureHeroAsync(GameEntry game, CancellationToken ct)
            => AutomaticArt.EnsureHeroAsync(game, Core.CenterSettings.SteamGridDbApiKey, ct);

        /// <summary>
        /// strict=true is the silent auto-fill's rule: first result only, and only when the name
        /// matches once punctuation is out of the way - an unattended background fetch must never put
        /// a stranger's cover on a tile. strict=false is the manual picker's rule: nobody is
        /// unattended there, a person is looking at the result and can retype the query, so the plain
        /// top autocomplete hit is offered even when the names do not match exactly.
        /// </summary>
        internal static async Task<int?> SearchGameIdAsync(HttpClient http, string key, string title, CancellationToken ct, bool strict)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "search/autocomplete/" + Uri.EscapeDataString(title));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Logged only on the manual picker's path (strict=false) - the silent background sweep
            // runs this for every uncovered game on the machine and must stay quiet on a miss, but a
            // person watching this one search deserves to know WHERE it failed rather than just that
            // it did. This is the search that had "no results" with no visible reason.
            if (!strict) LogArtSearch("autocomplete '" + title + "' -> " + (int)response.StatusCode + " " + Truncate(body, 500));

            if (strict) response.EnsureSuccessStatusCode();
            else if (!response.IsSuccessStatusCode) return null;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex)
            {
                if (strict) throw;
                LogArtSearch("autocomplete JSON parse failed: " + ex.Message);
                return null;
            }
            using (doc)
            {
                JsonElement data;
                if (strict) data = SuccessfulData(doc.RootElement);
                else if (!doc.RootElement.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Array)
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
                    if (strict && (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("id", out var validId) || validId.ValueKind != JsonValueKind.Number ||
                        !validId.TryGetInt32(out int number) || number <= 0 ||
                        !entry.TryGetProperty("name", out var validName) || validName.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(validName.GetString())))
                        throw new InvalidDataException("SteamGridDB returned an invalid game result.");
                    if (!entry.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int id)) continue;
                    if (!strict) { LogArtSearch("autocomplete matched id=" + id + " (of " + seen + "+ candidates)"); return id; }
                    if (!entry.TryGetProperty("name", out var nameProp)) continue;
                    if (PlayniteSource.NormalizeTitle(nameProp.GetString()) == want) return id;
                }
                if (!strict) LogArtSearch("autocomplete returned " + seen + " candidate(s), none usable");
                return null;
            }
        }

        // Automatic lookups may cache absence only after a successful, well-formed API response.
        // The manual picker retains its existing forgiving parsing and empty-page behavior.
        internal static JsonElement SuccessfulData(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("SteamGridDB did not return a successful data array.");
            return data;
        }

        private static void LogArtSearch(string message) => Core.InstallLog.Write("[ArtPicker] " + message);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

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
        /// <summary>
        /// One page of covers, plus what is needed to ask for the next one.
        ///
        /// The GAME ID is carried out deliberately: paging must not repeat the autocomplete step. It
        /// costs a round trip, and worse, it is not guaranteed to pick the same game twice - a second
        /// call could quietly start paging through a different title's covers.
        /// </summary>
        public sealed class ArtPage
        {
            public int GameId { get; set; }
            public IReadOnlyList<ArtCandidate> Items { get; set; } = Array.Empty<ArtCandidate>();
            public bool HasMore { get; set; }
        }

        /// <summary>How many covers one request asks for. Six full rows of five.</summary>
        public const int ArtPageSize = 30;

        public static async Task<ArtPage> SearchArtAsync(string query, CancellationToken ct)
        {
            var empty = new ArtPage();
            if (!HasKey)
            {
                LogArtSearch("search skipped - no API key set");
                return empty;
            }
            if (string.IsNullOrWhiteSpace(query)) return empty;
            string key = Core.CenterSettings.SteamGridDbApiKey.Trim();
            LogArtSearch("search '" + query + "'");

            int? id;
            try { id = await SearchGameIdAsync(Http, key, query, ct, strict: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            // Both catches used to be silent - an exception here (a DNS failure, a timed-out
            // connection, TLS) looked EXACTLY like "found nothing", and that ambiguity was the reason
            // "the search finds nothing" could not be diagnosed from a bug report alone.
            catch (Exception ex) { LogArtSearch("autocomplete threw: " + ex); return empty; }
            if (id == null) return empty;

            try { return await VerticalGridsAsync(key, id.Value, 0, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogArtSearch("grids threw: " + ex); return empty; }
        }

        /// <summary>The next page for a game already found by <see cref="SearchArtAsync"/>.</summary>
        public static async Task<ArtPage> MoreArtAsync(int gameId, int page, CancellationToken ct)
        {
            if (!HasKey || page <= 0) return new ArtPage { GameId = gameId };
            string key = Core.CenterSettings.SteamGridDbApiKey.Trim();
            try { return await VerticalGridsAsync(key, gameId, page, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogArtSearch("grids page " + page + " threw: " + ex); return new ArtPage { GameId = gameId }; }
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

        /// <summary>One page of portrait grids for a game, in whatever order the API returns them
        /// (not re-sorted here).</summary>
        private static async Task<ArtPage> VerticalGridsAsync(string key, int gameId, int page, CancellationToken ct)
        {
            var results = new List<ArtCandidate>();
            var outcome = new ArtPage { GameId = gameId, Items = results };

            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "grids/game/" + gameId + "?dimensions=600x900&types=static"
                        + "&limit=" + ArtPageSize + "&page=" + page);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LogArtSearch("grids/game/" + gameId + " page " + page + " -> " + (int)response.StatusCode + " " + Truncate(body, 500));
            if (!response.IsSuccessStatusCode) return outcome;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex) { LogArtSearch("grids JSON parse failed: " + ex.Message); return outcome; }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    LogArtSearch("grids response has no 'data' array");
                    return outcome;
                }

                foreach (var grid in data.EnumerateArray())
                {
                    if (!grid.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String) continue;
                    string thumb = grid.TryGetProperty("thumb", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.String
                        ? thumbProp.GetString() : null;
                    results.Add(new ArtCandidate { Url = urlProp.GetString(), Thumb = thumb });
                }
                // 'total' is the count across ALL pages, so it - not the size of this page - is what
                // says whether another one exists. Deriving it from "did this page come back full"
                // would ask for an empty page every time the total is an exact multiple of the size.
                int total = doc.RootElement.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32() : (page * ArtPageSize) + results.Count;
                outcome.HasMore = results.Count > 0 && (page + 1) * ArtPageSize < total;

                LogArtSearch("grids/game/" + gameId + " page " + page + " -> " + results.Count
                             + " portrait candidate(s), total " + total + ", more=" + outcome.HasMore);
                return outcome;
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
