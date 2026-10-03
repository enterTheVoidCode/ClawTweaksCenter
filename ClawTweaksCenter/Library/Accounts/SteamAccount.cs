using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace ClawTweaksCenter.Library.Accounts
{
    /// <summary>
    /// The user's Steam account, signed in by QR code. The first step of Doku\ACHIEVEMENTS_Plan.md:
    /// achievements read from the ACCOUNT, not from the stats blobs Steam only writes in batches.
    ///
    /// ── THE HANDSHAKE, AND ONLY THE HANDSHAKE ──────────────────────────────────────────────────
    /// SteamKit2 opens an ANONYMOUS connection to a Steam CM server, runs the auth session over it
    /// and disconnects. SteamUser.LogOn is never sent: that is the call that would take the session
    /// away from the desktop client (SteamKit #540). The auth service only issues tokens.
    ///
    ///   Connect -> BeginAuthSessionViaQRAsync -> phone scans and confirms -> tokens -> Disconnect
    ///
    /// From then on everything is plain HTTPS against api.steampowered.com with access_token=.
    ///
    /// ── TWO TOKENS ─────────────────────────────────────────────────────────────────────────────
    /// The ACCESS token is short-lived (its exp claim says how long; about a day is the expectation,
    /// not measured yet) and is renewed from the REFRESH token, which lives for months. The refresh
    /// token is a credential - whoever holds it IS the user - so it is stored DPAPI-encrypted for
    /// the current Windows user, in a file of its own, never in the registry beside plain settings.
    /// CenterDataBackup copies the data folder; an encrypted blob restored on another machine or
    /// for another user simply does not decrypt and reads as "not signed in".
    /// </summary>
    public static class SteamAccount
    {
        /// <summary>What the phone shows as the device asking to sign in.</summary>
        private const string DeviceName = "ClawTweaks Center";

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Renew this long before the access token runs out, so a call that starts just
        /// before expiry does not fail halfway.</summary>
        private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(10);

        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim RenewLock = new SemaphoreSlim(1, 1);
        private static Stored _cached;
        private static bool _loaded;

        private sealed class Stored
        {
            public string AccountName { get; set; }
            public ulong SteamId { get; set; }
            public string RefreshToken { get; set; }
            public string AccessToken { get; set; }
            public DateTime AccessExpiresUtc { get; set; }
        }

        private static string FilePath => Path.Combine(
            Core.CenterDataBackup.DataDir, "accounts", "steam.bin");

        public static bool IsSignedIn => Load() != null;

        /// <summary>The Steam login name, as the auth service returned it. Null when signed out.</summary>
        public static string AccountName => Load()?.AccountName;

        /// <summary>The 64-bit SteamID, taken from the token itself. 0 when signed out.</summary>
        public static ulong SteamId => Load()?.SteamId ?? 0;

        /// <summary>
        /// Runs the QR sign-in to the end: connects, hands every challenge URL to
        /// <paramref name="onChallenge"/> (Steam replaces it every half minute or so, and the QR on
        /// screen has to follow), waits for the phone, stores the tokens, disconnects.
        ///
        /// Returns the account name. Throws on failure; OperationCanceledException when cancelled.
        /// <paramref name="onChallenge"/> is called on a worker thread.
        /// </summary>
        public static async Task<string> SignInWithQrAsync(Action<string> onChallenge, CancellationToken ct)
        {
            return await WithConnectionAsync(async client =>
            {
                var session = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                {
                    DeviceFriendlyName = DeviceName,
                    // A client-app audience: GenerateAccessTokenForApp renews only those (SteamKit's
                    // own doc on it). Whether the Web API accepts this token for achievements is
                    // the first measurement - Doku\ACHIEVEMENTS_Plan.md section 7.
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                    IsPersistentSession = true,
                }).ConfigureAwait(false);

                session.ChallengeURLChanged = () => onChallenge?.Invoke(session.ChallengeURL);
                onChallenge?.Invoke(session.ChallengeURL);

                AuthPollResult result = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);

                ulong steamId = ReadSteamId(result.AccessToken) ?? ReadSteamId(result.RefreshToken) ?? 0;
                if (steamId == 0) throw new InvalidOperationException("Steam returned a token without a SteamID.");

                var stored = new Stored
                {
                    AccountName = result.AccountName,
                    SteamId = steamId,
                    RefreshToken = result.RefreshToken,
                    AccessToken = result.AccessToken,
                    AccessExpiresUtc = ReadExpiry(result.AccessToken) ?? DateTime.UtcNow.AddHours(1),
                };
                Save(stored);
                // Never the account name: the log is attached to bug reports.
                Core.InstallLog.Write("[Accounts] Steam signed in"
                    + ", access token valid until " + stored.AccessExpiresUtc.ToString("u")
                    + ", refresh token until " + (ReadExpiry(result.RefreshToken)?.ToString("u") ?? "?"));
                return stored.AccountName;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// A valid access token, renewed first when it is about to run out. Null when signed out or
        /// when renewal failed - the caller falls back to the local blobs, it does not retry.
        ///
        /// A refresh token Steam rejects (revoked on the phone, expired) signs the account out: it
        /// will never work again, and keeping it would retry it on every call.
        /// </summary>
        public static async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            var s = Load();
            if (s == null) return null;
            if (s.AccessExpiresUtc - RenewMargin > DateTime.UtcNow) return s.AccessToken;

            await RenewLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                s = Load();
                if (s == null) return null;
                if (s.AccessExpiresUtc - RenewMargin > DateTime.UtcNow) return s.AccessToken;

                return await WithConnectionAsync(async client =>
                {
                    AccessTokenGenerateResult r;
                    try
                    {
                        r = await client.Authentication.GenerateAccessTokenForAppAsync(
                            new SteamID(s.SteamId), s.RefreshToken, allowRenewal: true).ConfigureAwait(false);
                    }
                    catch (AuthenticationException ex)
                    {
                        Core.InstallLog.Write("[Accounts] Steam refresh token rejected (" + ex.Result + "), signing out");
                        SignOut();
                        return null;
                    }

                    s.AccessToken = r.AccessToken;
                    s.AccessExpiresUtc = ReadExpiry(r.AccessToken) ?? DateTime.UtcNow.AddHours(1);
                    // Only handed out when Steam decided to renew it; empty otherwise.
                    if (!string.IsNullOrEmpty(r.RefreshToken)) s.RefreshToken = r.RefreshToken;
                    Save(s);
                    Core.InstallLog.Write("[Accounts] Steam access token renewed, valid until "
                        + s.AccessExpiresUtc.ToString("u")
                        + (string.IsNullOrEmpty(r.RefreshToken) ? "" : " (refresh token renewed too)"));
                    return s.AccessToken;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Steam token renewal failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally { RenewLock.Release(); }
        }

        /// <summary>Forgets the tokens on this machine. Steam's own list of signed-in devices still
        /// shows the session until it is removed there or the refresh token runs out.</summary>
        public static void SignOut()
        {
            lock (Gate)
            {
                _cached = null;
                _loaded = true;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
            // What was fetched under this account goes with it.
            SteamAccountAchievements.Clear();
            StoreCatalog.ForgetWishlist();
        }

        /// <summary>
        /// Connects anonymously, runs <paramref name="work"/>, disconnects - whatever happens.
        ///
        /// The callback pump runs on its own task for the whole connection: SteamKit2 delivers the
        /// connect result and the service responses through it, and an auth call made without a pump
        /// running simply never completes.
        /// </summary>
        private static async Task<T> WithConnectionAsync<T>(Func<SteamClient, Task<T>> work, CancellationToken ct)
        {
            var client = new SteamClient();
            var manager = new CallbackManager(client);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub1 = manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult(true));
            using var sub2 = manager.Subscribe<SteamClient.DisconnectedCallback>(_ => connected.TrySetResult(false));

            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                        await manager.RunWaitCallbackAsync(pumpCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                client.Connect();
                var first = await Task.WhenAny(connected.Task, Task.Delay(ConnectTimeout, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (first != connected.Task || !connected.Task.Result)
                    throw new IOException("Could not reach Steam.");

                return await work(client).ConfigureAwait(false);
            }
            finally
            {
                try { client.Disconnect(); } catch { }
                pumpCts.Cancel();
                try { await pump.ConfigureAwait(false); } catch { }
            }
        }

        // ── storage ─────────────────────────────────────────────────────────────────────────────

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
                    if (s == null || s.SteamId == 0 || string.IsNullOrEmpty(s.RefreshToken)) return null;
                    _cached = s;
                }
                catch (Exception ex)
                {
                    // Another machine's or another user's file (restored backup), or a damaged one.
                    Core.InstallLog.Write("[Accounts] Steam account file unreadable, treated as signed out: " + ex.GetType().Name);
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

        // ── the JWT ────────────────────────────────────────────────────────────────────────────
        // Steam's tokens are JWTs. Only the payload is read, and only for two claims; the signature
        // is not checked because nothing here trusts the token - Steam does, when it gets it back.

        private static ulong? ReadSteamId(string jwt)
        {
            var payload = ReadPayload(jwt);
            if (payload == null) return null;
            using (payload)
            {
                if (payload.RootElement.TryGetProperty("sub", out var sub)
                    && ulong.TryParse(sub.GetString(), out ulong id) && id != 0)
                    return id;
            }
            return null;
        }

        private static DateTime? ReadExpiry(string jwt)
        {
            var payload = ReadPayload(jwt);
            if (payload == null) return null;
            using (payload)
            {
                if (payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long secs))
                    return DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            }
            return null;
        }

        private static JsonDocument ReadPayload(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                string b64 = parts[1].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            }
            catch { return null; }
        }
    }
}
