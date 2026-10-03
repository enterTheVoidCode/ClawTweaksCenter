using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ClawTweaksCenter.Library
{
    /// <summary>One article.</summary>
    public sealed class NewsItem
    {
        public string Id { get; set; }
        public string Source { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Link { get; set; }
        public DateTime PublishedUtc { get; set; }
        /// <summary>The picture, downloaded into the news folder; null for a source without
        /// pictures (VideoCardz) or when the download failed.</summary>
        public string ImageFile { get; set; }
    }

    /// <summary>
    /// Gaming news from three RSS feeds, for the news screen in Recent (user, 2026-10-03).
    ///
    /// ── STRICTLY THE LAST 7 DAYS, AND OLDER IS DELETED ─────────────────────────────────────────
    /// The feeds are short (measured 2026-10-03): PC Gamer 50 items covering only TWO days, This Week
    /// in Video Games 20 over five, VideoCardz 30 over two. So a week only exists if Center keeps what
    /// it has seen: every refresh merges the feeds into %LOCALAPPDATA%\ClawTweaks\Center\news\
    /// news.json by article link. And then everything published more than <see cref="MaxAge"/> ago -
    /// or with no date, or dated in the future beyond a day of clock skew - is removed from that file
    /// AND its picture is deleted from disk, on every refresh and on every read. Pictures no item
    /// points at any more are swept too, so the folder cannot grow.
    ///
    /// ── PICTURES ───────────────────────────────────────────────────────────────────────────────
    ///   PC Gamer             enclosure / media:content, 1920 px on futurecdn - the CDN serves the
    ///                        same image at "-480-80.jpg" (27 KB instead of 260 KB), so that is asked.
    ///                        One in 50 is WebP; Windows' WebP extension decodes it.
    ///   This Week in VG      the first &lt;img&gt; in content:encoded (WordPress).
    ///   VideoCardz           none - the user knew, text only.
    /// Downloaded once per article into news\img\, named by a hash of the article link.
    ///
    /// Plain HTTP GETs of public feeds; nothing about the user is sent.
    /// </summary>
    public static class GamingNews
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

        private sealed class Feed
        {
            public string Name;
            public string Url;
            public bool HasImages;
        }

        /// <summary>In the order of the screen's tabs, the first being the default (user,
        /// 2026-10-03).</summary>
        private static readonly Feed[] Feeds =
        {
            new Feed { Name = "This Week in Video Games", Url = "https://thisweekinvideogames.com/rss/", HasImages = true },
            new Feed { Name = "PC Gamer", Url = "https://www.pcgamer.com/rss/", HasImages = true },
            new Feed { Name = "VideoCardz", Url = "https://videocardz.com/rss-feed", HasImages = false },
        };

        /// <summary>The source names, in tab order.</summary>
        public static IEnumerable<string> SourceNames => Feeds.Select(f => f.Name);

        private static readonly HttpClient Http = CreateHttp();
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);
        private static DateTime _refreshedUtc = DateTime.MinValue;

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks", "Center", "news");
        private static string ImgDir => Path.Combine(Dir, "img");
        private static string StorePath => Path.Combine(Dir, "news.json");

        /// <summary>Full path of an item's picture, or null.</summary>
        public static string ImagePath(NewsItem item) =>
            item?.ImageFile == null ? null : Path.Combine(ImgDir, item.ImageFile);

        /// <summary>What is stored, already purged, newest first. Disk only - no network.</summary>
        public static List<NewsItem> Stored()
        {
            var items = Load();
            if (Purge(items)) Save(items);
            return items.OrderByDescending(i => i.PublishedUtc).ToList();
        }

        /// <summary>
        /// Fetches the three feeds (at most every half hour unless forced), merges, downloads the
        /// new pictures, purges, saves. Returns the stored list, newest first. Never throws for a
        /// feed that fails - the others still count, and the stored items stay.
        /// </summary>
        public static async Task<List<NewsItem>> RefreshAsync(bool force, CancellationToken ct)
        {
            await RefreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var items = Load();
                if (!force && DateTime.UtcNow - _refreshedUtc < RefreshEvery)
                {
                    if (Purge(items)) Save(items);
                    return items.OrderByDescending(i => i.PublishedUtc).ToList();
                }

                var byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
                var fetched = await Task.WhenAll(Feeds.Select(f => FetchFeedAsync(f, ct))).ConfigureAwait(false);
                int added = 0;
                var counts = new List<string>();
                for (int f = 0; f < Feeds.Length; f++)
                {
                    var list = fetched[f];
                    counts.Add(Feeds[f].Name + " " + (list == null ? "failed" : list.Count.ToString(CultureInfo.InvariantCulture)));
                    if (list == null) continue;
                    foreach (var (item, imageUrl) in list)
                    {
                        if (!IsFresh(item.PublishedUtc)) continue;
                        if (byId.TryGetValue(item.Id, out var known))
                        {
                            // Titles get corrected after publishing; the picture stays.
                            known.Title = item.Title;
                            known.Summary = item.Summary;
                            if (known.ImageFile == null && imageUrl != null)
                                known.ImageFile = await DownloadImageAsync(item.Id, imageUrl, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (imageUrl != null) item.ImageFile = await DownloadImageAsync(item.Id, imageUrl, ct).ConfigureAwait(false);
                        byId[item.Id] = item;
                        items.Add(item);
                        added++;
                    }
                }
                int before = items.Count;
                Purge(items);
                Save(items);
                _refreshedUtc = DateTime.UtcNow;
                Core.InstallLog.Write("[News] " + string.Join(", ", counts) + "; " + added + " new, "
                    + (before - items.Count) + " expired, " + items.Count + " kept (last " + MaxAge.TotalDays + " days)");
                return items.OrderByDescending(i => i.PublishedUtc).ToList();
            }
            finally { RefreshLock.Release(); }
        }

        // ── the feeds ────────────────────────────────────────────────────────────────────────────

        private static async Task<List<(NewsItem Item, string ImageUrl)>> FetchFeedAsync(Feed feed, CancellationToken ct)
        {
            try
            {
                using var res = await Http.GetAsync(feed.Url, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    Core.InstallLog.Write("[News] " + feed.Name + " answered " + (int)res.StatusCode);
                    return null;
                }
                string xml = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                XNamespace media = "http://search.yahoo.com/mrss/";
                XNamespace content = "http://purl.org/rss/1.0/modules/content/";
                var result = new List<(NewsItem, string)>();
                foreach (var it in doc.Descendants("item"))
                {
                    string link = ((string)it.Element("link"))?.Trim();
                    string title = Clean((string)it.Element("title"));
                    if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(title)) continue;
                    if (!TryDate((string)it.Element("pubDate"), out var published)) continue;

                    string html = (string)it.Element(content + "encoded");
                    string image = null;
                    if (feed.HasImages)
                    {
                        image = it.Element("enclosure")?.Attribute("url")?.Value
                             ?? it.Element(media + "content")?.Attribute("url")?.Value
                             ?? it.Element(media + "thumbnail")?.Attribute("url")?.Value;
                        if (image == null && html != null)
                        {
                            var m = Regex.Match(html, "<img[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (m.Success) image = WebUtility.HtmlDecode(m.Groups[1].Value);
                        }
                        // futurecdn (PC Gamer) serves every size by name; the list needs 480 px.
                        if (image != null) image = Regex.Replace(image, @"-\d{3,4}-80\.(jpg|png|webp)$", "-480-80.$1");
                    }

                    result.Add((new NewsItem
                    {
                        Id = link,
                        Source = feed.Name,
                        Title = title,
                        Summary = Summary((string)it.Element("description")),
                        Link = link,
                        PublishedUtc = published,
                    }, image != null && image.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? image : null));
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[News] " + feed.Name + " failed: " + ex.GetType().Name);
                return null;
            }
        }

        private static bool TryDate(string s, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            // RFC 822 ("Sat, 03 Oct 2026 16:06:23 +0000"); DateTimeOffset reads it once the day name
            // is gone, and some feeds write "GMT" instead of an offset.
            string t = Regex.Replace(s.Trim(), @"^[A-Za-z]{3},\s*", "").Replace(" GMT", " +0000").Replace(" UT", " +0000");
            if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)
                || DateTimeOffset.TryParseExact(t, "dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                utc = dto.UtcDateTime;
                return true;
            }
            return false;
        }

        /// <summary>The description as plain text, without WordPress's "The post … appeared first
        /// on …" tail, at most 280 characters.</summary>
        private static string Summary(string html)
        {
            string t = Clean(html);
            if (t == null) return null;
            int tail = t.IndexOf("The post ", StringComparison.Ordinal);
            if (tail > 0 && t.IndexOf("appeared first on", tail, StringComparison.Ordinal) > 0) t = t.Substring(0, tail).TrimEnd();
            if (t.EndsWith("[…]", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 3).TrimEnd() + "…";
            return t.Length > 280 ? t.Substring(0, 277).TrimEnd() + "…" : t;
        }

        private static string Clean(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            string t = Regex.Replace(html, "<[^>]+>", " ");
            t = WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length == 0 ? null : t;
        }

        // ── seven days, strictly ─────────────────────────────────────────────────────────────────

        private static bool IsFresh(DateTime publishedUtc)
        {
            var now = DateTime.UtcNow;
            return publishedUtc >= now - MaxAge && publishedUtc <= now + TimeSpan.FromDays(1);
        }

        /// <summary>Removes every item outside the window, deletes its picture, and deletes any
        /// picture no item points at. True when the list changed.</summary>
        private static bool Purge(List<NewsItem> items)
        {
            int removed = items.RemoveAll(i => i == null || string.IsNullOrEmpty(i.Id) || !IsFresh(i.PublishedUtc));
            try
            {
                if (Directory.Exists(ImgDir))
                {
                    var keep = new HashSet<string>(items.Where(i => i.ImageFile != null).Select(i => i.ImageFile), StringComparer.OrdinalIgnoreCase);
                    foreach (string f in Directory.GetFiles(ImgDir))
                        if (!keep.Contains(Path.GetFileName(f)))
                            try { File.Delete(f); } catch { }
                }
            }
            catch { }
            return removed > 0;
        }

        // ── pictures ─────────────────────────────────────────────────────────────────────────────

        private static async Task<string> DownloadImageAsync(string id, string url, CancellationToken ct)
        {
            try
            {
                using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return null;
                byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > 5_000_000) return null;
                string ext = url.Split('?')[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png"
                           : url.Split('?')[0].EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : ".jpg";
                string name = Hash(id) + ext;
                Directory.CreateDirectory(ImgDir);
                File.WriteAllBytes(Path.Combine(ImgDir, name), bytes);
                return name;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static string Hash(string s)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(h, 0, 12).ToLowerInvariant();
        }

        // ── the file ─────────────────────────────────────────────────────────────────────────────

        private static List<NewsItem> Load()
        {
            try
            {
                if (File.Exists(StorePath))
                    return JsonSerializer.Deserialize<List<NewsItem>>(File.ReadAllText(StorePath)) ?? new List<NewsItem>();
            }
            catch (Exception ex) { Core.InstallLog.Write("[News] store unreadable, starting empty: " + ex.GetType().Name); }
            return new List<NewsItem>();
        }

        private static void Save(List<NewsItem> items)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(items));
                File.Move(tmp, StorePath, overwrite: true);
            }
            catch (Exception ex) { Core.InstallLog.Write("[News] could not save: " + ex.Message); }
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
            return http;
        }
    }
}
