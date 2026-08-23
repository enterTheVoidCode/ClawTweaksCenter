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
    public static class SteamGridDb
    {
        private const string ApiBase = "https://www.steamgriddb.com/api/v2/";

        public static bool HasKey => !string.IsNullOrWhiteSpace(Core.CenterSettings.SteamGridDbApiKey);

        private static string CacheDir => Path.Combine(
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
            int? id = await SearchGameIdAsync(key, title, ct).ConfigureAwait(false);
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

        private static async Task<int?> SearchGameIdAsync(string key, string title, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + "search/autocomplete/" + Uri.EscapeDataString(title));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            // FIRST RESULT ONLY, and only when the name matches once punctuation is out of the way.
            // The endpoint is an autocomplete: searching "Doom" returns every Doom ever made, and
            // taking the top hit regardless would put a stranger's cover on the tile - worse than the
            // coloured plate it replaces, because it looks correct.
            string want = PlayniteSource.NormalizeTitle(title);
            foreach (var entry in data.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int id)) continue;
                if (!entry.TryGetProperty("name", out var nameProp)) continue;
                if (PlayniteSource.NormalizeTitle(nameProp.GetString()) == want) return id;
            }
            return null;
        }

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
