using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// The user's RetroAchievements account. Measured 2026-10-03, see Doku\ACHIEVEMENTS_Plan.md §5e.
    ///
    /// ── NO SIGN-IN SCREEN: TAKEN OVER FROM PLAYNITEACHIEVEMENTS ─────────────────────────────────
    /// RetroAchievements has no OAuth, no QR and no device code: its Web API takes the user name and
    /// a personal 32-character API key (retroachievements.org → Settings → Keys) on every request.
    /// Typing that key with a controller is not a sign-in anyone finishes. And Center only shows
    /// RetroAchievements for ROMs PlayniteAchievements has already matched by hash (user, 2026-10-03:
    /// no hashing of our own) - so PlayniteAchievements is set up by definition, and it already holds
    /// both values in its config.json. The account row copies them from there when the user presses
    /// A, checks them against the API once, and keeps its own DPAPI-encrypted copy like the other
    /// stores. Nothing is read from that config without the button press.
    ///
    /// THE KEY TRAVELS IN THE QUERY STRING - that is how RetroAchievements' API is built. So no
    /// request URL is ever logged, and neither is the user name.
    /// </summary>
    public static class RetroAchievementsAccount
    {
        private const string ApiBase = "https://retroachievements.org/API/";

        /// <summary>PlayniteAchievements' extension id - the folder name under ExtensionsData.</summary>
        private const string PlayniteAchievementsId = "e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b";

        private static readonly HttpClient Http = CreateHttp();
        private static readonly object Gate = new object();

        private sealed class Stored
        {
            public string User { get; set; }
            public string ApiKey { get; set; }
        }
        private static Stored _cached;
        private static bool _loaded;

        private static string FilePath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "retroachievements.bin");

        /// <summary>PlayniteAchievements' data folder (its achievement_cache.db and config.json).</summary>
        public static string PlayniteAchievementsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "ExtensionsData", PlayniteAchievementsId);

        public static bool IsSignedIn => Load() != null;
        public static string UserName => Load()?.User;

        /// <summary>True when PlayniteAchievements holds a user name and API key to take over. Reads
        /// only whether they are there.</summary>
        public static bool PlayniteAchievementsHasAccount => ReadPlayniteAchievements() != null;

        public enum SignInResult { Ok, NotSetUp, Rejected, Unreachable }

        /// <summary>Copies the user name and API key from PlayniteAchievements, checks them with one
        /// profile request, and keeps them. Never throws for an expected failure.</summary>
        public static async Task<SignInResult> SignInFromPlayniteAchievementsAsync(CancellationToken ct)
        {
            var found = ReadPlayniteAchievements();
            if (found == null) return SignInResult.NotSetUp;
            try
            {
                using var res = await Http.GetAsync(Url(found, "API_GetUserProfile.php", null), ct).ConfigureAwait(false);
                // 401 for a wrong key, 404 / 422 for an unknown user; all mean the same to the user.
                if ((int)res.StatusCode >= 400 && (int)res.StatusCode < 500)
                {
                    Core.InstallLog.Write("[Accounts] RetroAchievements rejected the key from PlayniteAchievements: " + (int)res.StatusCode);
                    return SignInResult.Rejected;
                }
                if (!res.IsSuccessStatusCode)
                {
                    Core.InstallLog.Write("[Accounts] RetroAchievements answered " + (int)res.StatusCode);
                    return SignInResult.Unreachable;
                }
                string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                // The profile carries the name with the account's own capitalisation.
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("User", out var u)
                    && u.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(u.GetString()))
                    found.User = u.GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] RetroAchievements check failed: " + ex.GetType().Name);
                return SignInResult.Unreachable;
            }
            Save(found);
            Core.InstallLog.Write("[Accounts] RetroAchievements signed in (taken over from PlayniteAchievements)");
            RetroAchievementsAchievements.RefreshInBackground(force: true);
            return SignInResult.Ok;
        }

        public static void SignOut()
        {
            lock (Gate)
            {
                _cached = null;
                _loaded = true;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
            RetroAchievementsAchievements.Clear();
        }

        /// <summary>
        /// GETs one Web API endpoint and parses the answer. Null when signed out, on a failure (logged
        /// by endpoint and status only) or when the answer is not JSON. A 401 means the key was
        /// revoked or replaced on the website: that signs the account out, as a rejected refresh
        /// token does for the other stores.
        /// </summary>
        internal static async Task<JsonDocument> GetAsync(string endpoint, IDictionary<string, string> query, CancellationToken ct)
        {
            var s = Load();
            if (s == null) return null;
            using var res = await Http.GetAsync(Url(s, endpoint, query), ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Core.InstallLog.Write("[Achievements] RetroAchievements " + endpoint + " answered " + (int)res.StatusCode);
                if ((int)res.StatusCode == 401)
                {
                    Core.InstallLog.Write("[Accounts] RetroAchievements key rejected, signing out");
                    SignOut();
                }
                return null;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static string Url(Stored s, string endpoint, IDictionary<string, string> query)
        {
            var b = new System.Text.StringBuilder(ApiBase).Append(endpoint)
                .Append("?u=").Append(Uri.EscapeDataString(s.User))
                .Append("&y=").Append(Uri.EscapeDataString(s.ApiKey));
            if (query != null)
                foreach (var kv in query)
                    b.Append('&').Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value));
            return b.ToString();
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // RetroAchievements asks API users to identify themselves.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ClawTweaksCenter (+https://github.com/enterTheVoidCode/ClawTweaksCenter)");
            return http;
        }

        /// <summary>The user name and key in PlayniteAchievements' config.json
        /// (Persisted.ProviderSettings.RetroAchievements.RaUsername / RaWebApiKey), or null.</summary>
        private static Stored ReadPlayniteAchievements()
        {
            try
            {
                string path = Path.Combine(PlayniteAchievementsDir, "config.json");
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (!(root.TryGetProperty("Persisted", out var p) && p.TryGetProperty("ProviderSettings", out var ps)
                      && ps.TryGetProperty("RetroAchievements", out var ra) && ra.ValueKind == JsonValueKind.Object))
                    return null;
                string user = Str(ra, "RaUsername"), key = Str(ra, "RaWebApiKey");
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key)) return null;
                return new Stored { User = user.Trim(), ApiKey = key.Trim() };
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] PlayniteAchievements config unreadable: " + ex.GetType().Name);
                return null;
            }
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ── storage, as SteamAccount ─────────────────────────────────────────────────────────────

        private static Stored Load()
        {
            lock (Gate)
            {
                if (_loaded) return _cached;
                _loaded = true;
                try
                {
                    if (!File.Exists(FilePath)) return null;
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                    var s = JsonSerializer.Deserialize<Stored>(plain);
                    if (s == null || string.IsNullOrEmpty(s.User) || string.IsNullOrEmpty(s.ApiKey)) return null;
                    _cached = s;
                }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("[Accounts] RetroAchievements account file unreadable, treated as signed out: " + ex.GetType().Name);
                    _cached = null;
                }
                return _cached;
            }
        }

        private static void Save(Stored s)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), null, DataProtectionScope.CurrentUser);
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, cipher);
                File.Move(tmp, FilePath, overwrite: true);
                _cached = s;
                _loaded = true;
            }
        }
    }
}
