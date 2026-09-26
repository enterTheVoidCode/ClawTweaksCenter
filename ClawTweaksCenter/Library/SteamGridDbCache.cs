using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ClawTweaksCenter.Library
{
    // The automatic artwork cache owns its transport and storage boundary; the manual picker
    // continues to use SteamGridDb's public API and never writes automatic lookup outcomes.
    internal sealed class SteamGridDbCache
    {
        private const string ApiBase = "https://www.steamgriddb.com/api/v2/";
        private readonly HttpClient _http;
        private readonly string _cacheDir;
        private readonly ArtIndex _covers;
        private readonly ArtIndex _heroes;

        internal SteamGridDbCache(HttpClient http, string cacheDir)
        {
            _http = http;
            _cacheDir = cacheDir;
            _covers = new ArtIndex(Path.Combine(cacheDir, "index.json"));
            _heroes = new ArtIndex(Path.Combine(cacheDir, "heroindex.json"));
        }

        internal async Task FetchMissingAsync(IReadOnlyList<GameEntry> games, string key,
            CancellationToken ct, Action onProgress)
        {
            if (string.IsNullOrWhiteSpace(key) || games == null) return;
            int found = 0;
            // One request at a time: automatic art uses the user's personal API quota.
            foreach (var game in games)
            {
                ct.ThrowIfCancellationRequested();
                if (game == null || game.ArtPath != null) continue;
                string path = await FetchAsync(game, key.Trim(), hero: false, ct).ConfigureAwait(false);
                if (path == null) continue;
                game.ArtPath = path;
                found++;
                if (found % 3 == 0) onProgress?.Invoke();
            }
            if (found > 0) onProgress?.Invoke();
        }

        internal async Task<string> EnsureHeroAsync(GameEntry game, string key, CancellationToken ct)
        {
            if (game == null || string.IsNullOrWhiteSpace(key)) return null;
            try { return await FetchAsync(game, key.Trim(), hero: true, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }

        private async Task<string> FetchAsync(GameEntry game, string key, bool hero, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string titleKey = PlayniteSource.NormalizeTitle(game.Title);
            if (titleKey.Length == 0) return null;
            var index = hero ? _heroes : _covers;
            string cachedName = index.Get(titleKey);
            if (cachedName != null)
            {
                if (cachedName.Length == 0) return null;
                string cachedPath = Path.Combine(_cacheDir, cachedName);
                if (File.Exists(cachedPath)) return cachedPath;
            }

            string file;
            try { file = await DownloadAsync(key, game.Title, titleKey, hero, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // Failure says nothing about whether artwork exists.
            ct.ThrowIfCancellationRequested();
            index.Set(titleKey, file);
            return file == null ? null : Path.Combine(_cacheDir, file);
        }

        private async Task<string> DownloadAsync(string key, string title, string titleKey, bool hero, CancellationToken ct)
        {
            int? id = await SteamGridDb.SearchGameIdAsync(_http, key, title, ct, strict: true).ConfigureAwait(false);
            if (id == null) return null;
            string endpoint = hero ? "heroes" : "grids";
            string dimensions = hero ? "1920x620" : "600x900";
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + endpoint + "/game/" + id.Value + "?dimensions=" + dimensions + "&types=static&limit=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string url = null;
            using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
            {
                var data = SteamGridDb.SuccessfulData(doc.RootElement);
                if (data.GetArrayLength() == 0) return null;
                foreach (var art in data.EnumerateArray())
                    if (art.ValueKind == JsonValueKind.Object && art.TryGetProperty("url", out var u) &&
                        u.ValueKind == JsonValueKind.String && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    { url = u.GetString(); break; }
            }
            if (url == null) throw new InvalidDataException("SteamGridDB returned artwork without a usable image URL.");
            byte[] bytes;
            using (var image = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                image.EnsureSuccessStatusCode();
                bytes = await image.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            if (bytes.Length == 0) throw new InvalidDataException("SteamGridDB returned an empty image.");
            // A successful HTTP response may still be an error page or truncated image. Do not
            // persist a positive filename that can never decode, either.
            using (var stream = new MemoryStream(bytes))
                _ = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            string ext = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string name = (hero ? "hero_" : "") + titleKey + ext;
            Directory.CreateDirectory(_cacheDir);
            string dest = Path.Combine(_cacheDir, name);
            string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Move(tmp, dest, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
            return name;
        }

        private sealed class ArtIndex(string path)
        {
            private readonly object _gate = new object();
            private Dictionary<string, string> _files;
            private readonly HashSet<string> _misses = new HashSet<string>(StringComparer.Ordinal);

            private Dictionary<string, string> Files
            {
                get
                {
                    if (_files != null) return _files;
                    _files = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        if (File.Exists(path))
                        {
                            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                            if (loaded != null)
                                foreach (var entry in loaded)
                                    // Old empty values cannot distinguish absence from a past outage.
                                    if (!string.IsNullOrEmpty(entry.Value)) _files[entry.Key] = entry.Value;
                        }
                    }
                    catch { }
                    return _files;
                }
            }

            internal string Get(string titleKey)
            {
                lock (_gate)
                    return _misses.Contains(titleKey) ? string.Empty
                        : Files.TryGetValue(titleKey, out string name) ? name : null;
            }

            internal void Set(string titleKey, string file)
            {
                lock (_gate)
                {
                    // Only confirmed absence reaches null here. Cache it for this process, with
                    // positives alone on disk: a restart can discover newly uploaded artwork.
                    if (file == null)
                    {
                        _misses.Add(titleKey);
                        if (!Files.Remove(titleKey)) return;
                    }
                    else
                    {
                        _misses.Remove(titleKey);
                        Files[titleKey] = file;
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        string tmp = path + ".tmp";
                        File.WriteAllText(tmp, JsonSerializer.Serialize(Files));
                        File.Move(tmp, path, overwrite: true);
                    }
                    catch { }
                }
            }
        }
    }
}
