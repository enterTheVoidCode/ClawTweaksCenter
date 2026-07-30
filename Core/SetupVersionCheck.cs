using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Reads the curated Center manifest and answers three questions. The first two are about the
    /// running ClawTweaksSetup.exe's own version (ClawTweaksSetup.csproj &lt;Version&gt;, a separate
    /// number from the main ClawTweaks app version); the third is about OTHER software entirely.
    ///
    ///   1. "Is this build too old to be trusted?" — <c>minimumSetupVersion</c>, a FLOOR. Only bumped
    ///      when an older Center is known broken, so it normally flags nobody. Produces a warning.
    ///   2. "Is there a newer build?" — <c>latestSetupVersion</c> (+ an optional page to send the user
    ///      to). Produces a NOTIFICATION, and nothing more.
    ///   3. "Which ClawTweaks builds may still be installed?" — <c>minimumClawTweaksVersion</c>, the
    ///      floor for the APP builds the picker offers. Do not confuse it with (1): they are different
    ///      version numbers on different software. It exists because a build's install ROUTINE can go
    ///      obsolete while its binaries are fine — someone installing a pre-0.1.8.51 release today
    ///      lands on the retired machine-wide layout and needs migrating all over again. Curating it
    ///      in the manifest means retiring a build takes effect everywhere at the next Center launch,
    ///      without shipping a new Center.
    ///
    /// ── Center does not update itself ────────────────────────────────────────────────────────────
    /// There used to be a CenterUpdater that downloaded the advertised exe, checked its SHA-256 and
    /// launched it. It is gone. "Fetch an executable and run it" is the dropper shape, performed by an
    /// unsigned app, and no amount of care around it changes how Defender's ML scores the behaviour.
    /// The manifest now only says a newer build EXISTS; the user opens the page, downloads it and runs
    /// it themselves. That is why <c>latestSetupSha256</c> is no longer read: we never fetch bytes, so
    /// there are no bytes to pin. Do not add a download path back here — if it ever comes back, it
    /// belongs in something signed.
    ///
    /// Same hosting pattern as manifest/claw-drivers.json (MsiClawDriverCheckService): a JSON file on
    /// the 'master' branch, fetched via raw.githubusercontent.com so the URL never churns with
    /// per-release branches.
    ///
    /// The floor exists because the standalone Center picker downloads and runs old, already-built
    /// exes (GitHub releases/test builds/Drive nightlies) — a Center-side bug fix landing in source
    /// doesn't retroactively fix binaries a user already downloaded.
    /// </summary>
    public static class SetupVersionCheck
    {
        private const string ManifestUrl =
            "https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaks/master/manifest/setup-manifest.json";

        public sealed class Result
        {
            // --- floor check ---
            public bool Outdated;
            public Version MinimumVersion;
            public Version RunningVersion;
            public string Message;

            // --- update notice ---
            public Version LatestVersion;

            // --- floor for the ClawTweaks APP builds Center offers, NOT for Center itself ---

            /// <summary>The oldest ClawTweaks build Center may still install, or null when the manifest
            /// doesn't set one. See <see cref="AppVersionMessage"/> for why this exists at all.</summary>
            public Version MinimumAppVersion;

            /// <summary>Shown on the blocked build's tile and on its confirm screen. Curated in the
            /// manifest so the reason can be specific to whatever made those builds unwanted, rather
            /// than a generic "too old" baked into a Center release years earlier.</summary>
            public string AppVersionMessage;

            /// <summary>Where to send the user to get it. Always non-null once a newer version is
            /// advertised — it falls back to the repo's releases page, so the notice can never end up
            /// telling someone about an update without telling them where to find it.</summary>
            public string LatestPageUrl;

            /// <summary>True when the manifest advertises a NEWER build than the one running. That is
            /// the entire condition now: this only produces a notice, so there is nothing left that
            /// could fail unsafely (it used to also require a pinned URL + SHA-256, because it fed a
            /// downloader — see the note on the class for why that is gone).</summary>
            public bool IsUpdateOffered => LatestVersion != null && LatestVersion > RunningVersion;
        }

        /// <summary>Null on any failure (offline, manifest missing/malformed) — this check must never
        /// block or nag the user when it simply couldn't run.</summary>
        public static async Task<Result> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");
                string json = await http.GetStringAsync(ManifestUrl).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string minStr = GetString(root, "minimumSetupVersion");
                if (string.IsNullOrEmpty(minStr) || !Version.TryParse(minStr, out var minVersion))
                    return null;

                var running = Assembly.GetExecutingAssembly().GetName().Version;
                var result = new Result
                {
                    Outdated = running < minVersion,
                    MinimumVersion = minVersion,
                    RunningVersion = running,
                    Message = GetString(root, "message")
                        ?? "This ClawTweaks Center build is outdated. Please download the latest build.",
                };

                // The update half is optional: a manifest that only has the floor (schemaVersion 1)
                // still parses fine and simply offers no update.
                if (Version.TryParse(GetString(root, "latestSetupVersion") ?? "", out var latest))
                    result.LatestVersion = latest;

                // The app floor is optional and INDEPENDENT of everything above: an unparseable or
                // absent value simply leaves MinimumAppVersion null, which the picker reads as "no
                // floor". That fail-open is the whole safety story for this feature — a typo in the
                // manifest must never be able to make every build in the list uninstallable.
                if (Version.TryParse(GetString(root, "minimumClawTweaksVersion") ?? "", out var minApp))
                {
                    result.MinimumAppVersion = minApp;
                    result.AppVersionMessage = GetString(root, "clawTweaksVersionMessage")
                        ?? $"No longer supported — install {minApp} or newer";
                }

                // Prefer an explicit page, fall back to the direct asset URL older manifests carry, and
                // finally to the releases page — which always exists, so a manifest that advertises a
                // version but no location still produces a usable notice instead of a dead end.
                result.LatestPageUrl =
                    SanitizeUrl(GetString(root, "latestSetupPage")) ??
                    SanitizeUrl(GetString(root, "latestSetupUrl")) ??
                    ReleasesPageUrl;

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>The project's releases page — the last-resort destination, and the reason a user can
        /// never be told "there is an update" without being told where to get it.</summary>
        private const string ReleasesPageUrl = "https://github.com/enterTheVoidCode/ClawTweaks/releases";

        /// <summary>
        /// The manifest is fetched over the network, so its URL is untrusted input — and we are about to
        /// hand it to the user's BROWSER. Pin it to HTTPS on GitHub's own hosts so a compromised or
        /// typo'd manifest cannot send someone off to an arbitrary site that looks like a download page.
        /// Returns null rather than throwing on anything unexpected; the caller then falls back to
        /// <see cref="ReleasesPageUrl"/>.
        ///
        /// Note this is deliberately laxer than it used to be about the path: it accepted only ".exe"
        /// back when the URL fed a downloader that would then RUN the file. Now it can also be a plain
        /// release page, which has no extension at all — the host pin is what still matters.
        /// </summary>
        private static string SanitizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps) return null;

            string host = uri.Host.ToLowerInvariant();
            bool allowed =
                host == "github.com" ||
                host == "objects.githubusercontent.com" ||
                host == "release-assets.githubusercontent.com";
            return allowed ? uri.AbsoluteUri : null;
        }
    }
}
