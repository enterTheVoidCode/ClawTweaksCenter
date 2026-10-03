using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>The sections of the Store tab, in the order LT/RT walk them.</summary>
    public enum StoreSection
    {
        /// <summary>The account's own Steam games that are not installed - the tab's old content.</summary>
        NotInstalled,
        SteamDeals,
        Wishlist,
        GamePass,
    }

    /// <summary>
    /// What the Store tab shows besides the user's own uninstalled games: Steam's current deals, the
    /// Steam wishlist with its prices, and the Game Pass PC catalogue (user, 2026-10-03). Measured the
    /// same day, see Doku\ACHIEVEMENTS_Plan.md.
    ///
    ///   Steam deals   store.steampowered.com/search/results?specials=1&amp;filter=topsellers&amp;json=1 -
    ///                 public, the best-selling discounted games, 50 at a time; then
    ///                 IStoreBrowseService/GetItems (public) for price, discount, end date and art.
    ///   Wishlist      IWishlistService/GetWishlist with the signed-in account's token, then GetItems.
    ///                 48 of 48 priced, 39 on sale, all with an end date (measured).
    ///   Game Pass     catalog.gamepass.com/sigls/v2 lists (public) - "all PC games" plus "recently
    ///                 added" and "leaving soon" as notes - then displaycatalog.mp.microsoft.com for
    ///                 name and poster, 20 products a call.
    ///
    /// Entries are ordinary <see cref="GameEntry"/> objects with <see cref="GameEntry.Offer"/> set and
    /// ArtPath holding a URL, so the shelf draws them like any other cover. Each section is read once
    /// and kept for <see cref="KeepFor"/>.
    /// </summary>
    public static class StoreCatalog
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
        private const int SteamDealCount = 100;
        private const string SteamAssets = "https://shared.akamai.steamstatic.com/store_item_assets/";

        private const string GamePassAllPc = "fdd9e2a7-0fee-49f6-ad69-4354098401ff";
        private const string GamePassRecent = "f13cf6b4-57e6-4459-89df-6aec18cf0538";
        private const string GamePassLeaving = "393f05bf-e596-4ef6-9487-6d4fa0eab987";

        private static readonly HttpClient Http = CreateHttp();
        private static readonly object Gate = new object();
        private static readonly Dictionary<StoreSection, (DateTime at, List<GameEntry> items)> Cache = new Dictionary<StoreSection, (DateTime, List<GameEntry>)>();
        private static readonly HashSet<StoreSection> Loading = new HashSet<StoreSection>();

        /// <summary>A section finished loading. Worker thread.</summary>
        public static event Action<StoreSection> Changed;

        private static HttpClient CreateHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ClawTweaksCenter");
            return c;
        }

        /// <summary>The section's entries, or null while it has not been read yet (the read is then
        /// started, and <see cref="Changed"/> says when it lands).</summary>
        public static List<GameEntry> ItemsOf(StoreSection section)
        {
            lock (Gate)
            {
                bool fresh = Cache.TryGetValue(section, out var hit) && DateTime.UtcNow - hit.at < KeepFor;
                if (!fresh && Loading.Add(section)) _ = LoadAsync(section);
                return hit.items;
            }
        }

        /// <summary>True while a section is being read for the first time.</summary>
        public static bool IsLoading(StoreSection section)
        {
            lock (Gate) return Loading.Contains(section) && !Cache.ContainsKey(section);
        }

        private static async Task LoadAsync(StoreSection section)
        {
            List<GameEntry> items = null;
            try
            {
                switch (section)
                {
                    case StoreSection.SteamDeals: items = await SteamDealsAsync().ConfigureAwait(false); break;
                    case StoreSection.Wishlist: items = await WishlistAsync().ConfigureAwait(false); break;
                    case StoreSection.GamePass: items = await GamePassAsync().ConfigureAwait(false); break;
                }
                Core.InstallLog.Write("[Store] " + section + ": " + (items?.Count.ToString() ?? "nothing"));
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Store] " + section + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            lock (Gate)
            {
                Loading.Remove(section);
                // A failed read keeps the last good list; with none, an empty one, so the shelf says
                // "nothing here" instead of spinning forever.
                if (items != null || !Cache.ContainsKey(section))
                    Cache[section] = (DateTime.UtcNow, items ?? new List<GameEntry>());
            }
            Changed?.Invoke(section);
        }

        /// <summary>Forgets the wishlist - after a sign-in or sign-out it belongs to someone else.</summary>
        public static void ForgetWishlist()
        {
            lock (Gate) Cache.Remove(StoreSection.Wishlist);
        }

        // ── Steam ────────────────────────────────────────────────────────────────────────────────

        private static async Task<List<GameEntry>> SteamDealsAsync()
        {
            var ids = new List<uint>();
            for (int start = 0; start < SteamDealCount; start += 50)
            {
                using var doc = await GetJsonAsync("https://store.steampowered.com/search/results/?specials=1&filter=topsellers&json=1&count=50&start=" + start
                    + "&cc=" + Country() + "&l=" + SteamAchievements.SteamLanguage()).ConfigureAwait(false);
                if (doc == null || !doc.RootElement.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array) break;
                foreach (var i in list.EnumerateArray())
                {
                    // The search answer carries a name and a capsule URL; the appid is inside the URL.
                    var m = Regex.Match(Str(i, "logo") ?? "", "/apps/(\\d+)/");
                    if (m.Success && uint.TryParse(m.Groups[1].Value, out uint id) && !ids.Contains(id)) ids.Add(id);
                }
                if (list.GetArrayLength() < 50) break;
            }
            return await SteamItemsAsync(ids, null, onlyDiscounted: true).ConfigureAwait(false);
        }

        private static async Task<List<GameEntry>> WishlistAsync()
        {
            if (!Accounts.SteamAccount.IsSignedIn) return new List<GameEntry>();
            string token = await Accounts.SteamAccount.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false);
            ulong me = Accounts.SteamAccount.SteamId;
            if (token == null || me == 0) return null;

            using var doc = await GetJsonAsync("https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid=" + me + "&access_token=" + token).ConfigureAwait(false);
            var ids = new List<uint>();
            if (doc != null && doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                // The user's own order: priority, as they sorted it on Steam.
                foreach (var i in items.EnumerateArray().OrderBy(x => x.TryGetProperty("priority", out var p) && p.TryGetInt32(out int v) ? v : int.MaxValue))
                    if (i.TryGetProperty("appid", out var a) && a.TryGetUInt32(out uint id)) ids.Add(id);
            var list = await SteamItemsAsync(ids, token, onlyDiscounted: false).ConfigureAwait(false);
            // Deals first - that is what this section is opened for - each part in wishlist order.
            return list.Where(g => g.Offer.DiscountPercent > 0).Concat(list.Where(g => g.Offer.DiscountPercent <= 0)).ToList();
        }

        /// <summary>Name, price, discount and art for a list of appids, 50 per call, in the given
        /// order. The account's own games are marked as owned.</summary>
        private static async Task<List<GameEntry>> SteamItemsAsync(List<uint> ids, string token, bool onlyDiscounted)
        {
            var owned = new HashSet<int>();
            try { foreach (var k in SteamOwned.Read().Keys) owned.Add(k); } catch { }

            var byId = new Dictionary<uint, GameEntry>();
            for (int i = 0; i < ids.Count; i += 50)
            {
                var input = new
                {
                    ids = ids.Skip(i).Take(50).Select(a => new { appid = a }).ToArray(),
                    context = new { language = SteamAchievements.SteamLanguage(), country_code = Country() },
                    data_request = new { include_assets = true },
                };
                string url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(JsonSerializer.Serialize(input))
                    + (token != null ? "&access_token=" + token : "");
                using var doc = await GetJsonAsync(url).ConfigureAwait(false);
                if (doc == null || !doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("store_items", out var items)) continue;
                foreach (var s in items.EnumerateArray())
                {
                    if (!s.TryGetProperty("appid", out var a) || !a.TryGetUInt32(out uint appid)) continue;
                    string name = Str(s, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    var offer = new StoreOffer
                    {
                        StoreUri = "steam://store/" + appid,
                        Owned = owned.Contains((int)appid),
                    };
                    if (s.TryGetProperty("best_purchase_option", out var bpo))
                    {
                        if (bpo.TryGetProperty("discount_pct", out var dp) && dp.TryGetInt32(out int pct)) offer.DiscountPercent = pct;
                        offer.Price = Str(bpo, "formatted_final_price");
                        offer.OriginalPrice = Str(bpo, "formatted_original_price");
                        if (bpo.TryGetProperty("active_discounts", out var ad) && ad.ValueKind == JsonValueKind.Array && ad.GetArrayLength() > 0
                            && ad[0].TryGetProperty("discount_end_date", out var end) && end.TryGetInt64(out long unix) && unix > 0)
                            offer.DiscountEnds = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                    }
                    if (onlyDiscounted && offer.DiscountPercent <= 0) continue;
                    if (offer.Owned) offer.Note = Core.Loc.T("In your library");

                    byId[appid] = new GameEntry
                    {
                        Id = appid.ToString(CultureInfo.InvariantCulture),
                        Store = GameStore.Steam,
                        Title = name,
                        Installed = false,
                        ArtPath = SteamCover(s),
                        Offer = offer,
                    };
                }
            }
            return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        /// <summary>The 600x900 library capsule, the shape every other cover on the shelf has.</summary>
        private static string SteamCover(JsonElement item)
        {
            if (!item.TryGetProperty("assets", out var a)) return null;
            string format = Str(a, "asset_url_format");
            // The hashed file names only: a bare name ("portrait.png", seen 2026-10-03) answered 404
            // at that path. Without a hashed capsule, the header - always present - is the cover.
            string file = new[] { Str(a, "library_capsule_2x"), Str(a, "library_capsule"), Str(a, "header") }
                .FirstOrDefault(f => f != null && f.Contains("/"));
            if (format == null || file == null) return null;
            return SteamAssets + format.Replace("${FILENAME}", file);
        }

        // ── Game Pass ────────────────────────────────────────────────────────────────────────────

        private static async Task<List<GameEntry>> GamePassAsync()
        {
            var all = await GamePassListAsync(GamePassAllPc).ConfigureAwait(false);
            var recent = new HashSet<string>(await GamePassListAsync(GamePassRecent).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
            var leaving = new HashSet<string>(await GamePassListAsync(GamePassLeaving).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
            if (all.Count == 0) return null;

            // Installed Xbox games, by package family name, to mark what is already here.
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var g in new XboxSource().ScanAsync(CancellationToken.None).GetAwaiter().GetResult())
                {
                    string pfn = Accounts.XboxAccountAchievements.PfnOf(g.Id);
                    if (pfn != null) installed.Add(pfn);
                }
            }
            catch { }

            // New ones first, then the catalogue's own order; leaving-soon ones stay where they are
            // and say so.
            var order = all.Where(recent.Contains).Concat(all.Where(id => !recent.Contains(id))).ToList();
            var byId = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            string market = Country();
            string lang = GamePassLanguage();
            for (int i = 0; i < order.Count; i += 20)
            {
                string ids = string.Join(",", order.Skip(i).Take(20));
                using var doc = await GetJsonAsync("https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds=" + ids + "&market=" + market + "&languages=" + lang).ConfigureAwait(false);
                if (doc == null || !doc.RootElement.TryGetProperty("Products", out var products)) continue;
                foreach (var p in products.EnumerateArray())
                {
                    string id = Str(p, "ProductId");
                    if (id == null || !p.TryGetProperty("LocalizedProperties", out var lps) || lps.GetArrayLength() == 0) continue;
                    var lp = lps[0];
                    string title = Str(lp, "ProductTitle");
                    if (string.IsNullOrEmpty(title)) continue;
                    string pfn = p.TryGetProperty("Properties", out var props) ? Str(props, "PackageFamilyName") : null;

                    string note = pfn != null && installed.Contains(pfn) ? Core.Loc.T("Installed")
                                : leaving.Contains(id) ? Core.Loc.T("Leaving soon")
                                : recent.Contains(id) ? Core.Loc.T("New")
                                : null;
                    byId[id] = new GameEntry
                    {
                        Id = id,
                        Store = GameStore.Xbox,
                        Title = title,
                        Installed = false,
                        ArtPath = GamePassPoster(lp),
                        Offer = new StoreOffer
                        {
                            Note = note,
                            // The Xbox app's own page, with its Install button; the Microsoft Store
                            // when the Xbox app is not there to take the link.
                            StoreUri = "msxbox://game/?productId=" + id,
                            FallbackUri = "ms-windows-store://pdp/?productid=" + id,
                        },
                    };
                }
            }
            return order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        private static async Task<List<string>> GamePassListAsync(string listId)
        {
            var ids = new List<string>();
            using var doc = await GetJsonAsync("https://catalog.gamepass.com/sigls/v2?id=" + listId + "&language=" + GamePassLanguage() + "&market=" + Country()).ConfigureAwait(false);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // The first element is the list's own header ({siglId, title, ...}); the rest are {id}.
                string id = Str(e, "id");
                if (id != null) ids.Add(id);
            }
            return ids;
        }

        /// <summary>The portrait poster - the shape of every other cover - else the box art.</summary>
        private static string GamePassPoster(JsonElement lp)
        {
            if (!lp.TryGetProperty("Images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
            string Find(string purpose) => images.EnumerateArray().Where(i => Str(i, "ImagePurpose") == purpose)
                .Select(i => Str(i, "Uri")).FirstOrDefault(u => u != null);
            string uri = Find("Poster") ?? Find("BoxArt");
            if (uri == null) return null;
            if (uri.StartsWith("//")) uri = "https:" + uri;
            // The catalogue serves the full-size image; the CDN scales it on request.
            return uri + (uri.Contains("?") ? "&" : "?") + "w=400";
        }

        // ── shared ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The user's country for prices and the catalogue, from Windows' region. DE when
        /// Windows has none to give.</summary>
        private static string Country()
        {
            try
            {
                string c = new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
                return string.IsNullOrEmpty(c) || c.Length != 2 ? "DE" : c.ToUpperInvariant();
            }
            catch { return "DE"; }
        }

        private static string GamePassLanguage()
        {
            switch (Core.Loc.Current)
            {
                case Core.UiLanguage.German: return "de-de";
                case Core.UiLanguage.French: return "fr-fr";
                case Core.UiLanguage.Korean: return "ko-kr";
                case Core.UiLanguage.Spanish: return "es-es";
                case Core.UiLanguage.Russian: return "ru-ru";
                case Core.UiLanguage.Greek: return "el-gr";
                case Core.UiLanguage.ChineseSimplified: return "zh-cn";
                case Core.UiLanguage.ChineseTraditional: return "zh-tw";
                case Core.UiLanguage.Italian: return "it-it";
                case Core.UiLanguage.Portuguese: return "pt-br";
                case Core.UiLanguage.Japanese: return "ja-jp";
                case Core.UiLanguage.Polish: return "pl-pl";
                default: return "en-us";
            }
        }

        private static async Task<JsonDocument> GetJsonAsync(string url)
        {
            using var res = await Http.GetAsync(url).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Core.InstallLog.Write("[Store] " + new Uri(url).Host + " answered " + (int)res.StatusCode);
                return null;
            }
            string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
