using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// The user's Epic Games account - step 3 of Doku\ACHIEVEMENTS_Plan.md, section "Step 3: Epic —
    /// measured 2026-10-03". The same route PlayniteAchievements takes.
    ///
    /// ── THE SIGN-IN ────────────────────────────────────────────────────────────────────────────
    /// Epic's own login page in the user's browser, which ends on
    ///   www.epicgames.com/id/api/redirect?clientId=&lt;launcher&gt;&amp;responseType=code
    /// That page answers with JSON carrying an `authorizationCode`; the user copies it, the screen
    /// takes it off the clipboard and hands it to <see cref="SignInWithCodeAsync"/>, which trades it
    /// for a launcher token. See CenterMenuWindow.EpicSignIn.cs for why the browser.
    ///
    /// NOT A DEVICE CODE, ON PURPOSE. The launcher client refuses one (unsupported_grant_type). The
    /// only client that grants one is Fortnite's Switch client, and Epic's activate page then warns
    /// the user that an app they did not start from Fortnite gets full access to the account - which
    /// is true and reads exactly like phishing (measured 2026-10-03; the user chose a login page over it).
    ///
    /// ── THE CLIENT ─────────────────────────────────────────────────────────────────────────────
    /// The Epic Games Launcher's public client id and secret, as PlayniteAchievements, Legendary and
    /// Heroic use them. Epic offers third parties no client that can read another game's
    /// achievements. A launcher token is a full-account token, which is why only the refresh token is
    /// kept, DPAPI-encrypted for the current Windows user like Steam's and Xbox's, and why signing out
    /// also ends the session at Epic.
    ///
    /// Measured lifetimes: access 36 h, refresh about a year.
    /// </summary>
    public static class EpicAccount
    {
        private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
        private const string LauncherBasic = "MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
        private const string AccountBase = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/";

        /// <summary>The page that hands out the authorization code once the browser is signed in.</summary>
        public const string RedirectUrl = "https://www.epicgames.com/id/api/redirect?clientId=" + LauncherClientId + "&responseType=code";

        /// <summary>Where the sign-in screen starts: Epic's login, coming back to <see cref="RedirectUrl"/>.</summary>
        public static string LoginUrl => "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(RedirectUrl);

        private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim RenewLock = new SemaphoreSlim(1, 1);

        private sealed class Stored
        {
            public string DisplayName { get; set; }
            public string AccountId { get; set; }
            public string RefreshToken { get; set; }
        }
        private static Stored _cached;
        private static bool _loaded;

        // In memory only.
        private static string _access;
        private static DateTime _accessExpiresUtc;

        private static string FilePath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "epic.bin");

        public static bool IsSignedIn => Load() != null;
        public static string DisplayName => Load()?.DisplayName;
        public static string AccountId => Load()?.AccountId;

        /// <summary>
        /// The authorization code in what was copied off the redirect page, or null. Accepts the
        /// JSON the page answers with ({"authorizationCode":"…"}, also pretty-printed) and the
        /// launcher-style localhost/launcher/authorized?code=… address.
        /// </summary>
        public static string ExtractAuthorizationCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, "\"authorizationCode\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(text, "localhost/launcher/authorized\\?code=([^&\\s\"'<>]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        /// <summary>Trades the page's authorization code for tokens. Returns the display name. Throws
        /// on failure.</summary>
        public static async Task<string> SignInWithCodeAsync(string code, CancellationToken ct)
        {
            using var doc = await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["token_type"] = "eg1",
            }, ct).ConfigureAwait(false);
            var s = Take(doc.RootElement, new Stored());
            if (string.IsNullOrEmpty(s.AccountId)) throw new IOException("the token answer carried no account id");
            Core.InstallLog.Write("[Accounts] Epic signed in, access valid until " + _accessExpiresUtc.ToString("u"));
            return s.DisplayName;
        }

        /// <summary>A bearer token, renewed first when needed. Null when signed out or renewal
        /// failed. A refresh token Epic rejects signs the account out.</summary>
        public static async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (Load() == null) return null;
            lock (Gate)
                if (_access != null && _accessExpiresUtc - RenewMargin > DateTime.UtcNow) return _access;

            await RenewLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var s = Load();
                if (s == null) return null;
                lock (Gate)
                    if (_access != null && _accessExpiresUtc - RenewMargin > DateTime.UtcNow) return _access;

                using var res = await Http.SendAsync(TokenRequest(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = s.RefreshToken,
                    ["token_type"] = "eg1",
                }), ct).ConfigureAwait(false);
                using var doc = await ParseAsync(res, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    string err = ErrorCode(doc);
                    // invalid_grant / invalid_refresh_token: expired, revoked, or the password
                    // changed. It will never work again.
                    if ((int)res.StatusCode == 400 && (err.Contains("invalid_grant") || err.Contains("invalid_refresh_token")))
                    {
                        Core.InstallLog.Write("[Accounts] Epic refresh token rejected, signing out");
                        SignOut();
                    }
                    else Core.InstallLog.Write("[Accounts] Epic token renewal answered " + (int)res.StatusCode + ": " + err);
                    return null;
                }
                Take(doc.RootElement, s);
                lock (Gate) return _access;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Epic token renewal failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally { RenewLock.Release(); }
        }

        public static void SignOut()
        {
            string access;
            lock (Gate)
            {
                access = _access != null && _accessExpiresUtc > DateTime.UtcNow ? _access : null;
                _cached = null;
                _loaded = true;
                _access = null;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
            // A launcher token is a full-account token: end the session at Epic too, not only here.
            // Best effort - without a live access token the refresh token simply expires unused.
            if (access != null)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Delete, AccountBase + "sessions/kill/" + access);
                        req.Headers.TryAddWithoutValidation("Authorization", "bearer " + access);
                        using var res = await Http.SendAsync(req).ConfigureAwait(false);
                        Core.InstallLog.Write("[Accounts] Epic session ended: " + (int)res.StatusCode);
                    }
                    catch (Exception ex) { Core.InstallLog.Write("[Accounts] Epic session end failed: " + ex.GetType().Name); }
                });
            EpicAccountAchievements.Clear();
        }

        /// <summary>Reads a token answer into <paramref name="s"/>, saves it, and keeps the access
        /// token in memory. The refresh token rotates on every renewal; the new one is saved.</summary>
        private static Stored Take(JsonElement root, Stored s)
        {
            string access = Str(root, "access_token");
            string refresh = Str(root, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) throw new IOException("the token answer is incomplete");
            s.RefreshToken = refresh;
            s.AccountId = Str(root, "account_id") ?? s.AccountId;
            s.DisplayName = Str(root, "displayName") ?? s.DisplayName;
            DateTime expires = root.TryGetProperty("expires_at", out var ea) && ea.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime()
                : DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int secs) ? secs : 3600);
            lock (Gate)
            {
                Save(s);
                _access = access;
                _accessExpiresUtc = expires;
            }
            return s;
        }

        private static HttpRequestMessage TokenRequest(Dictionary<string, string> fields)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, AccountBase + "token") { Content = new FormUrlEncodedContent(fields) };
            req.Headers.TryAddWithoutValidation("Authorization", "basic " + LauncherBasic);
            return req;
        }

        private static async Task<JsonDocument> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
        {
            using var res = await Http.SendAsync(TokenRequest(fields), ct).ConfigureAwait(false);
            var doc = await ParseAsync(res, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode && doc != null) return doc;
            string err = ErrorCode(doc);
            doc?.Dispose();
            throw new IOException("Epic token answered " + (int)res.StatusCode + ": " + err);
        }

        private static async Task<JsonDocument> ParseAsync(HttpResponseMessage res, CancellationToken ct)
        {
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static string ErrorCode(JsonDocument doc) =>
            doc != null ? Str(doc.RootElement, "errorCode") ?? "unknown" : "unknown";

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
                    if (s == null || string.IsNullOrEmpty(s.RefreshToken)) return null;
                    _cached = s;
                }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("[Accounts] Epic account file unreadable, treated as signed out: " + ex.GetType().Name);
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
