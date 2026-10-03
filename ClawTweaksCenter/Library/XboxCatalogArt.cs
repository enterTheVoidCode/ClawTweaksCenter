using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Cover and backdrop for installed Xbox games from Microsoft's own store catalogue - no account,
    /// no key. Measured 2026-10-03:
    ///
    ///   displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&amp;value=&lt;pfn&gt;
    ///
    /// answers with the product and its images, among them "Poster" (2:3, 1440x2160) and
    /// "SuperHeroArt" (16:9, 3840x2160, no logo on it). The CDN scales on request: ?w=600 is the
    /// cover at ~90 KB, ?w=1920 the backdrop at ~320 KB.
    ///
    /// Why this exists: Xbox caches no cover art on disk at all (GameArt.ResolveLocalArt), so an
    /// Xbox game Playnite does not know - one installed today, say - stayed a coloured plate unless
    /// the user had pasted a SteamGridDB key. The official picture is also the right one, which a
    /// title search on SteamGridDB only usually is.
    ///
    /// Only fills what is still empty: a hand-picked cover or Playnite's stays. Cached on disk with
    /// the misses, like SteamGridDb, so a package the catalogue does not know (a sideloaded app, a
    /// delisted game) is asked about once.
    /// </summary>
    public static class XboxCatalogArt
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();

        private sealed class Cached
        {
            public string Cover { get; set; }
            public string Hero { get; set; }
            public DateTime AskedUtc { get; set; }
        }

        /// <summary>A miss is asked again after this long - a game can get its store page late.</summary>
        private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

        private static Dictionary<string, Cached> _index;
        private static string IndexPath => Path.Combine(SteamGridDb.CacheDir, "xboxindex.json");

        /// <summary>Fills <see cref="GameEntry.ArtPath"/> and <see cref="GameEntry.HeroPath"/> for the
        /// Xbox games without them. One request per game, ever (misses weekly), one at a time.</summary>
        public static async Task FetchMissingAsync(IReadOnlyList<GameEntry> games, CancellationToken ct, Action onProgress)
        {
            if (games == null) return;
            int asked = 0, covers = 0, heroes = 0;
            bool changed = false;
            foreach (var g in games)
            {
                ct.ThrowIfCancellationRequested();
                if (g == null || g.Store != GameStore.Xbox || (g.ArtPath != null && g.HeroPath != null)) continue;
                string pfn = Accounts.XboxAccountAchievements.PfnOf(g.Id);
                if (string.IsNullOrEmpty(pfn)) continue;

                Cached c;
                lock (Gate) Index.TryGetValue(pfn, out c);
                bool stale = c == null
                    || (c.Cover == null && DateTime.UtcNow - c.AskedUtc > MissRetry)
                    || (c.Cover != null && !File.Exists(Path.Combine(SteamGridDb.CacheDir, c.Cover)));
                if (stale)
                {
                    asked++;
                    try { c = await FetchAsync(pfn, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Core.InstallLog.Write("[Art] Xbox catalogue failed: " + ex.GetType().Name);
                        continue;   // not cached: nothing was learned
                    }
                    lock (Gate) Index[pfn] = c;
                    changed = true;
                }

                bool got = false;
                if (g.ArtPath == null && c.Cover != null) { g.ArtPath = Path.Combine(SteamGridDb.CacheDir, c.Cover); covers++; got = true; }
                if (g.HeroPath == null && c.Hero != null && File.Exists(Path.Combine(SteamGridDb.CacheDir, c.Hero)))
                { g.HeroPath = Path.Combine(SteamGridDb.CacheDir, c.Hero); heroes++; }
                if (got && stale) onProgress?.Invoke();
            }
            if (changed) SaveIndex();
            if (asked > 0 || covers > 0)
                Core.InstallLog.Write("[Art] Xbox catalogue: " + asked + " asked, " + covers + " covers and " + heroes + " backdrops applied");
        }

        private static async Task<Cached> FetchAsync(string pfn, CancellationToken ct)
        {
            var result = new Cached { AskedUtc = DateTime.UtcNow };
            string url = "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value="
                + Uri.EscapeDataString(pfn) + "&market=" + StoreCatalog.Country() + "&languages=" + StoreCatalog.GamePassLanguage() + ",neutral";
            using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                // 404: not a store product. Cached as a miss.
                if ((int)res.StatusCode == 404) return result;
                throw new HttpRequestException("catalogue answered " + (int)res.StatusCode);
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array || products.GetArrayLength() == 0)
                return result;
            var p = products[0];
            if (!p.TryGetProperty("LocalizedProperties", out var lps) || lps.ValueKind != JsonValueKind.Array || lps.GetArrayLength() == 0) return result;
            if (!lps[0].TryGetProperty("Images", out var images) || images.ValueKind != JsonValueKind.Array) return result;

            string Find(string purpose) => images.EnumerateArray()
                .Where(i => i.TryGetProperty("ImagePurpose", out var ip) && ip.GetString() == purpose)
                .Select(i => i.TryGetProperty("Uri", out var u) ? u.GetString() : null)
                .FirstOrDefault(u => !string.IsNullOrEmpty(u));

            string safe = string.Concat(pfn.Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' ? ch : '_'));
            // The poster is a cover's shape; box art (square) only when there is no poster.
            string cover = Find("Poster") ?? Find("BoxArt");
            if (cover != null) result.Cover = await DownloadAsync(cover, 600, "xbox_" + safe + ".jpg", ct).ConfigureAwait(false);
            // The backdrop without a logo printed on it first: the launch screen sets the title itself.
            string hero = Find("SuperHeroArt") ?? Find("TitledHeroArt");
            if (hero != null) result.Hero = await DownloadAsync(hero, 1920, "xboxhero_" + safe + ".jpg", ct).ConfigureAwait(false);
            return result;
        }

        private static async Task<string> DownloadAsync(string uri, int width, string name, CancellationToken ct)
        {
            if (uri.StartsWith("//", StringComparison.Ordinal)) uri = "https:" + uri;
            uri += (uri.Contains('?') ? "&" : "?") + "w=" + width;
            using var res = await Http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            Directory.CreateDirectory(SteamGridDb.CacheDir);
            File.WriteAllBytes(Path.Combine(SteamGridDb.CacheDir, name), bytes);
            return name;
        }

        private static Dictionary<string, Cached> Index
        {
            get
            {
                if (_index != null) return _index;
                _index = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(IndexPath))
                    {
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, Cached>>(File.ReadAllText(IndexPath));
                        if (loaded != null) _index = new Dictionary<string, Cached>(loaded, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { }
                return _index;
            }
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(SteamGridDb.CacheDir);
                string tmp = IndexPath + ".tmp";
                lock (Gate) File.WriteAllText(tmp, JsonSerializer.Serialize(_index));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch { }
        }
    }
}
