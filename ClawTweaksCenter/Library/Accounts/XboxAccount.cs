using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// The user's Xbox (Microsoft) account, signed in with a device code - step 2 of
    /// Doku\ACHIEVEMENTS_Plan.md, section "Step 2: Xbox — measured 2026-10-03".
    ///
    /// ── THE CHAIN ──────────────────────────────────────────────────────────────────────────────
    ///   login.microsoftonline.com/consumers   device code -> MSA access (1 h) + refresh token
    ///   user.auth.xboxlive.com                RpsTicket "d=" + access -> Xbox Live user token
    ///   xsts.auth.xboxlive.com                user token -> XSTS (~16 h) + uhs + XUID
    ///   every Xbox Live call                  Authorization: XBL3.0 x=&lt;uhs&gt;;&lt;XSTS&gt;
    ///
    /// Only the MSA refresh token is kept on disk (DPAPI, current user, same as SteamAccount); the
    /// rest is derived from it and lives in memory. The refresh token ROTATES - every renewal hands
    /// out a new one, and the new one is saved.
    ///
    /// ── THE CLIENT ID ──────────────────────────────────────────────────────────────────────────
    /// Center's own Entra registration ("ClawTweaks", personal Microsoft accounts only, public
    /// client flows on). Not a secret: a public client has none, and this id is what the consent
    /// screen shows the user. NOT Playnite's id, which is what PlayniteAchievements borrows.
    /// </summary>
    public static class XboxAccount
    {
        private const string ClientId = "5a401ee8-e966-4fc1-8196-22df20932851";
        private const string Scope = "XboxLive.signin offline_access";
        private const string MsaBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";

        /// <summary>Renew this long before a token runs out.</summary>
        private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim RenewLock = new SemaphoreSlim(1, 1);

        private sealed class Stored
        {
            public string Gamertag { get; set; }
            public string Xuid { get; set; }
            public string RefreshToken { get; set; }
        }
        private static Stored _cached;
        private static bool _loaded;

        // In memory only.
        private static string _xstsHeader;
        private static DateTime _xstsExpiresUtc;

        private static string FilePath => Path.Combine(Core.CenterDataBackup.DataDir, "accounts", "xbox.bin");

        public static bool IsSignedIn => Load() != null;
        public static string Gamertag => Load()?.Gamertag;
        public static string Xuid => Load()?.Xuid;

        /// <summary>What the sign-in screen shows: the code, and the page to type it on.</summary>
        public sealed class DeviceCode
        {
            public string UserCode;
            public string VerificationUri;
        }

        /// <summary>
        /// Runs the device-code sign-in to the end. <paramref name="onCode"/> gets the code to show
        /// (on a worker thread). Returns the gamertag. Throws on failure, OperationCanceledException
        /// when cancelled, TimeoutException when nobody signed in before the code expired.
        /// </summary>
        public static async Task<string> SignInAsync(Action<DeviceCode> onCode, CancellationToken ct)
        {
            using var dcRes = await Http.PostAsync(MsaBase + "devicecode", Form(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
            }), ct).ConfigureAwait(false);
            using var dc = await ParseAsync(dcRes, ct).ConfigureAwait(false);
            if (!dcRes.IsSuccessStatusCode) throw new IOException("devicecode answered " + (int)dcRes.StatusCode + ": " + Error(dc));

            string device = dc.RootElement.GetProperty("device_code").GetString();
            string code = dc.RootElement.GetProperty("user_code").GetString();
            string uri = dc.RootElement.GetProperty("verification_uri").GetString();
            int interval = Math.Max(1, dc.RootElement.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5);
            int expires = dc.RootElement.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900;
            // The QR carries the bare page, NOT "?otc=<code>": the pre-filled variant sends the
            // phone down a Microsoft flow that refuses third-party apps ("the application is a
            // first party application ... users are not permitted to consent", 2026-10-03), while
            // the same code typed on the bare page signs in fine (four test runs that day).
            onCode?.Invoke(new DeviceCode { UserCode = code, VerificationUri = uri });

            var end = DateTime.UtcNow.AddSeconds(expires);
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                using var res = await Http.PostAsync(MsaBase + "token", Form(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = device,
                }), ct).ConfigureAwait(false);
                using var doc = await ParseAsync(res, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    string access = doc.RootElement.GetProperty("access_token").GetString();
                    string refresh = doc.RootElement.GetProperty("refresh_token").GetString();
                    var (header, expiresUtc, xuid, gamertag) = await XboxLiveAsync(access, ct).ConfigureAwait(false);
                    var s = new Stored { Gamertag = gamertag, Xuid = xuid, RefreshToken = refresh };
                    lock (Gate)
                    {
                        Save(s);
                        _xstsHeader = header;
                        _xstsExpiresUtc = expiresUtc;
                    }
                    // Never the gamertag: the log is attached to bug reports.
                    Core.InstallLog.Write("[Accounts] Xbox signed in" + (string.IsNullOrEmpty(gamertag) ? ", no gamertag" : "")
                        + ", XSTS valid until " + expiresUtc.ToString("u"));
                    return gamertag;
                }
                string err = Error(doc);
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { interval += 5; continue; }
                // authorization_declined, expired_token, bad_verification_code
                throw new InvalidOperationException("sign-in ended: " + err);
            }
            throw new TimeoutException("the code expired");
        }

        /// <summary>
        /// The Authorization header for Xbox Live calls, renewed first when needed. Null when signed
        /// out or when renewal failed. A refresh token Microsoft rejects signs the account out.
        /// </summary>
        public static async Task<string> GetAuthHeaderAsync(CancellationToken ct)
        {
            if (Load() == null) return null;
            lock (Gate)
                if (_xstsHeader != null && _xstsExpiresUtc - RenewMargin > DateTime.UtcNow) return _xstsHeader;

            await RenewLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var s = Load();
                if (s == null) return null;
                lock (Gate)
                    if (_xstsHeader != null && _xstsExpiresUtc - RenewMargin > DateTime.UtcNow) return _xstsHeader;

                using var res = await Http.PostAsync(MsaBase + "token", Form(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = ClientId,
                    ["refresh_token"] = s.RefreshToken,
                    ["scope"] = Scope,
                }), ct).ConfigureAwait(false);
                using var doc = await ParseAsync(res, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    string err = Error(doc);
                    // invalid_grant: revoked, expired, or the password changed. It will never work again.
                    if (err == "invalid_grant")
                    {
                        Core.InstallLog.Write("[Accounts] Xbox refresh token rejected, signing out");
                        SignOut();
                    }
                    else Core.InstallLog.Write("[Accounts] Xbox token renewal answered " + (int)res.StatusCode + ": " + err);
                    return null;
                }

                string access = doc.RootElement.GetProperty("access_token").GetString();
                if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                    s.RefreshToken = rt.GetString();
                var (header, expiresUtc, xuid, gamertag) = await XboxLiveAsync(access, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(gamertag)) s.Gamertag = gamertag;
                if (!string.IsNullOrEmpty(xuid)) s.Xuid = xuid;
                lock (Gate)
                {
                    Save(s);
                    _xstsHeader = header;
                    _xstsExpiresUtc = expiresUtc;
                }
                return header;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Xbox token renewal failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally { RenewLock.Release(); }
        }

        public static void SignOut()
        {
            lock (Gate)
            {
                _cached = null;
                _loaded = true;
                _xstsHeader = null;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
            XboxAccountAchievements.Clear();
        }

        /// <summary>MSA access token -> Xbox Live user token -> XSTS. Returns the header, its expiry,
        /// the XUID and - when Xbox hands it out - the gamertag.</summary>
        private static async Task<(string header, DateTime expiresUtc, string xuid, string gamertag)> XboxLiveAsync(string msaAccess, CancellationToken ct)
        {
            using var user = await PostXblAsync("https://user.auth.xboxlive.com/user/authenticate", new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msaAccess },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            }, ct).ConfigureAwait(false);
            string userToken = user.RootElement.GetProperty("Token").GetString();

            using var xsts = await PostXblAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
            {
                // NO OptionalDisplayClaims: asking for "gtg" that way made XSTS answer 400 with XErr
                // 2148916279 (2026-10-03), while the bare request - the one every measurement used -
                // succeeds. The gamertag is read below if XSTS hands it out on its own.
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken } },
                RelyingParty = "http://xboxlive.com",
                TokenType = "JWT",
            }, ct).ConfigureAwait(false);
            var xui = xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
            string uhs = xui.GetProperty("uhs").GetString();
            string xid = xui.TryGetProperty("xid", out var x) ? x.GetString() : null;
            string gtg = xui.TryGetProperty("gtg", out var g) ? g.GetString() : null;
            DateTime notAfter = xsts.RootElement.TryGetProperty("NotAfter", out var na) && na.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime() : DateTime.UtcNow.AddHours(1);
            return ("XBL3.0 x=" + uhs + ";" + xsts.RootElement.GetProperty("Token").GetString(), notAfter, xid, gtg);
        }

        private static async Task<JsonDocument> PostXblAsync(string url, object body, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-xbl-contract-version", "1");
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var doc = await ParseAsync(res, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode && doc != null) return doc;
            // XSTS explains a refusal in XErr: 2148916233 no Xbox profile yet, 2148916238 a child
            // account without permission. Logged as the number, which is what the docs list.
            string xerr = doc != null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("XErr", out var xe) ? xe.ToString() : null;
            doc?.Dispose();
            throw new IOException(new Uri(url).Host + " answered " + (int)res.StatusCode + (xerr != null ? ", XErr " + xerr : ""));
        }

        private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new FormUrlEncodedContent(fields);

        private static async Task<JsonDocument> ParseAsync(HttpResponseMessage res, CancellationToken ct)
        {
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { return null; }
        }

        private static string Error(JsonDocument doc) =>
            doc != null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
                ? e.GetString() : "unknown";

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
                    Core.InstallLog.Write("[Accounts] Xbox account file unreadable, treated as signed out: " + ex.GetType().Name);
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
