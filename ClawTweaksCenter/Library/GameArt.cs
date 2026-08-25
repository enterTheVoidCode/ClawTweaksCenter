using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Cover art, local only. No network, no API key.
    ///
    /// Steam already ships what we want: it caches the vertical 600x900 library capsule next to its
    /// own data. Measured on this machine - 37 of 44 installed games have one, without a single byte
    /// off the network.
    ///
    /// A game with no cover does NOT get a broken image or an empty box: the tile draws itself from
    /// the title (see <see cref="ColorForTitle"/>), so it looks like a deliberate tile rather than a
    /// load that failed.
    /// </summary>
    public static class GameArt
    {
        /// <summary>
        /// Steam's cover cache has TWO file names and TWO layouts, and all four combinations are live
        /// at once. Measured here: 807 files called <c>library_600x900.jpg</c> (flat and nested) plus
        /// 91 newer ones called <c>library_capsule.jpg</c> (always nested).
        ///
        /// The second name is what newer cache entries use, and missing it is not a rounding error:
        /// Hollow Knight Silksong, Cyberpunk 2077 and No Rest for the Wicked were blank tiles for
        /// exactly this reason while their covers sat on disk the whole time. A cover that is present
        /// but not found reads as "the art download is broken", never as "the path was wrong".
        ///
        /// Despite the name, both are 300x450 - measured, not assumed. Same 2:3 capsule either way,
        /// so the tile geometry does not care which one is found.
        /// </summary>
        public static string FindSteamCover(string steamPath, string appId)
            => FindSteamImage(steamPath, appId, CoverPrefixes);

        /// <summary>
        /// The wide 1920x620 key image Steam caches beside the capsule, for the launch backdrop.
        ///
        /// BETTER COVERED THAN THE COVER ITSELF - measured on this machine: all 44 installed games
        /// have a library_hero.jpg, against 43 with a capsule. It costs nothing, needs no key and no
        /// network, and it is the same picture Steam puts behind a game on its own library page,
        /// which is why it looks right behind a title rather than merely large.
        ///
        /// NOT the blurred sibling: Steam also ships library_hero_blur.jpg, but at 192x62 it exists
        /// to be stretched behind a page, not to be looked at. The launch screen dims and gradients
        /// the sharp one instead, and blurs only where it has to fall back to a cover.
        /// </summary>
        public static string FindSteamHero(string steamPath, string appId)
            => FindSteamImage(steamPath, appId, HeroPrefixes);

        private static string FindSteamImage(string steamPath, string appId, string[] prefixes)
        {
            if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(appId)) return null;
            try
            {
                string appDir = Path.Combine(steamPath, "appcache", "librarycache", appId);
                if (!Directory.Exists(appDir)) return null;

                string hit = FindIn(appDir, prefixes);
                if (hit != null) return hit;

                foreach (string sub in Directory.GetDirectories(appDir))
                {
                    hit = FindIn(sub, prefixes);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        /// <summary>In preference order. Data, not logic - a third name appearing in a future Steam
        /// build is one more line here.</summary>
        private static readonly string[] CoverPrefixes = { "library_600x900", "library_capsule" };

        /// <summary>⚠ "library_hero" would also match "library_hero_blur" under the localised-suffix
        /// rule below, and the blur is a 192x62 thumbnail - a backdrop that arrived as a smear would
        /// look like a decode fault. The exact name is therefore tried on its own first, and the
        /// suffix search excludes the blur explicitly.</summary>
        private static readonly string[] HeroPrefixes = { "library_hero" };

        /// <summary>
        /// One directory, checked for any capsule.
        ///
        /// Matched by PREFIX, not by exact name, because Steam caches a LOCALISED capsule when the
        /// store page has one: Helldivers 2 and Ken Follett's The Pillars of the Earth were blank
        /// tiles while their covers sat on disk as <c>library_capsule_german.jpg</c> and
        /// <c>library_600x900_german.jpg</c>. The plain name is preferred where both exist; the
        /// language suffix is whatever this user's Steam is set to, so it cannot be listed up front.
        /// </summary>
        private static string FindIn(string dir, string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                string exact = Path.Combine(dir, prefix + ".jpg");
                if (File.Exists(exact)) return exact;
            }
            foreach (string prefix in prefixes)
            {
                string[] hits;
                try { hits = Directory.GetFiles(dir, prefix + "_*.jpg"); }
                catch { continue; }

                var usable = new List<string>();
                foreach (string h in hits)
                {
                    // "library_hero_blur.jpg" is not a localised library_hero - and neither is
                    // "library_hero_blur_german.jpg", which is what this test originally missed by
                    // looking at the END of the name. The suffix sits between the prefix and the
                    // language, so what matters is what follows the prefix. Measured: one file in
                    // 719 on this machine takes that shape, and it was the one that got through.
                    string rest = Path.GetFileNameWithoutExtension(h);
                    rest = rest.Length > prefix.Length ? rest.Substring(prefix.Length) : string.Empty;
                    if (rest.StartsWith("_blur", StringComparison.OrdinalIgnoreCase)) continue;
                    usable.Add(h);
                }
                if (usable.Count > 0)
                {
                    usable.Sort(StringComparer.OrdinalIgnoreCase);
                    return usable[0];
                }
            }
            return null;
        }

        /// <summary>
        /// Fills in <see cref="GameEntry.ArtPath"/> from everything already on disk.
        ///
        /// Two sources, in this order:
        ///   1. Steam's own capsule cache, by AppID - exact, and it covers 42 of the 44 Steam games
        ///      on this machine.
        ///   2. Playnite's downloaded art, for the rest. Epic and Xbox cache NO cover art at all
        ///      (measured: 98 image files in a Forza package, not one of them 2:3), so without this
        ///      every Epic and Xbox tile is a coloured plate. Playnite has already fetched a cover
        ///      for nearly everything it knows, and reading it costs nothing.
        ///
        /// Still local, still no network, still no API key. A machine without Playnite simply keeps
        /// the plates - the fallback is a deliberate-looking tile, not a failure.
        /// </summary>
        public static void ResolveLocalArt(IEnumerable<GameEntry> games)
        {
            string steam = SteamSource.SteamPath();
            var playnite = PlayniteSource.LastArtIndex;

            foreach (var g in games)
            {
                if (g == null) continue;

                // The hero is resolved even for an entry that already has a cover: the two are
                // different pictures for different places, and a hand-picked cover (ArtOverrideStore)
                // must not cost the game its backdrop.
                if (g.Store == GameStore.Steam && g.HeroPath == null) g.HeroPath = FindSteamHero(steam, g.Id);

                if (g.ArtPath != null) continue;
                if (g.Store == GameStore.Steam) g.ArtPath = FindSteamCover(steam, g.Id);
                if (g.ArtPath == null && playnite != null && !playnite.IsEmpty) g.ArtPath = playnite.TryFindArt(g);
            }
        }

        // Decoded covers, keyed by file path AND target width: the same picture at two sizes is two
        // different bitmaps, and mixing them up is how a grid ends up decoding at the wrong size.
        private static readonly ConcurrentDictionary<string, Task<BitmapSource>> Cache =
            new ConcurrentDictionary<string, Task<BitmapSource>>();

        // Decoding several JPEGs at once is worth it; decoding two hundred at once is not. Four keeps
        // the pipeline busy without turning the first paint into a stall.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(4);

        /// <summary>
        /// Decodes a cover at the size it will actually be drawn, on a background thread, and freezes
        /// it so it can cross to the UI thread.
        ///
        /// DecodePixelWidth is the single most important line in this file. A 600x900 JPEG decoded in
        /// full costs about 2.1 MB of memory; two hundred of them is 430 MB. Decoded to a 150 px tile
        /// it is about 130 KB. Every other performance measure is irrelevant next to this one.
        /// </summary>
        public static Task<BitmapSource> LoadAsync(string path, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(path) || decodePixelWidth <= 0) return Task.FromResult<BitmapSource>(null);
            string key = decodePixelWidth.ToString() + "|" + path;
            return Cache.GetOrAdd(key, _ => Task.Run(() => Decode(path, decodePixelWidth)));
        }

        private static BitmapSource Decode(string path, int decodePixelWidth)
        {
            Gate.Wait();
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = decodePixelWidth;
                // OnLoad reads the whole file during EndInit, so the stream is closed by the time we
                // return and the bitmap owns no file handle. It is also what makes Freeze legal, and
                // a frozen bitmap is the one kind of WPF image a background thread may hand to the UI.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Fetches a remote image (the art picker's SteamGridDB previews) and decodes it.
        ///
        /// ⚠ THIS CANNOT GO THROUGH <see cref="LoadAsync"/>, and the reason is not obvious enough to
        /// leave unwritten - the art picker shipped broken on exactly this assumption. Decode above
        /// sets BitmapImage.UriSource, which is synchronous ONLY for a local file. Handed an http(s)
        /// URI, WPF starts an ASYNCHRONOUS download instead: EndInit returns immediately with
        /// IsDownloading true, and Freeze then throws because a still-downloading BitmapImage is not
        /// freezable. Decode's catch swallowed that and returned null, so every picker tile stayed a
        /// grey rectangle with nothing anywhere saying why. (The async download would not have
        /// completed either - Task.Run puts it on a thread-pool thread with no Dispatcher to pump the
        /// download callbacks.)
        ///
        /// Downloading the bytes ourselves and decoding from a MemoryStream keeps the whole thing
        /// synchronous inside our own control, which is what makes Freeze legal again.
        /// </summary>
        public static Task<BitmapSource> LoadRemoteAsync(string url, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(url) || decodePixelWidth <= 0) return Task.FromResult<BitmapSource>(null);
            string key = "remote|" + decodePixelWidth + "|" + url;
            return Cache.GetOrAdd(key, _ => DownloadAndDecodeAsync(url, decodePixelWidth, key));
        }

        private static async Task<BitmapSource> DownloadAndDecodeAsync(string url, int decodePixelWidth, string cacheKey)
        {
            try
            {
                byte[] bytes = await RemoteHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 0)
                {
                    var decoded = DecodeBytes(bytes, decodePixelWidth);
                    if (decoded != null) return decoded;
                    Core.InstallLog.Write("[ArtPicker] preview decode produced nothing for " + url);
                }
                else
                {
                    Core.InstallLog.Write("[ArtPicker] preview download was empty for " + url);
                }
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[ArtPicker] preview download failed for " + url + ": " + ex.Message);
            }

            // Do NOT leave a failed remote load in the cache. Cache.GetOrAdd stores the Task itself,
            // so a transient network blip would otherwise pin "this image is unavailable" for the rest
            // of the session and searching again would show the same grey tiles. Local files keep the
            // old behaviour - a missing file on disk stays missing.
            Cache.TryRemove(cacheKey, out _);
            return null;
        }

        private static BitmapSource DecodeBytes(byte[] bytes, int decodePixelWidth)
        {
            Gate.Wait();
            try
            {
                using var stream = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.DecodePixelWidth = decodePixelWidth;
                // OnLoad is what lets the MemoryStream be disposed on the way out of this method.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[ArtPicker] preview decode threw: " + ex.Message);
                return null;
            }
            finally { Gate.Release(); }
        }

        private static readonly HttpClient RemoteHttp = CreateRemoteHttp();

        private static HttpClient CreateRemoteHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
            return client;
        }

        /// <summary>
        /// A stable colour per title for the no-cover tile. Derived from the title so the same game
        /// always gets the same colour - a tile that changes colour between launches looks like a
        /// glitch, and the whole point of this fallback is to look intentional.
        /// </summary>
        public static Brush ColorForTitle(string title)
        {
            int hash = 17;
            foreach (char c in title ?? string.Empty) hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
            double hue = Math.Abs(hash % 360);
            var brush = new SolidColorBrush(FromHsv(hue, 0.42, 0.34));
            brush.Freeze();
            return brush;
        }

        private static Color FromHsv(double h, double s, double v)
        {
            int i = (int)Math.Floor(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
