using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Reads the curated Center manifest and answers two separate questions about the running
    /// ClawTweaksSetup.exe's own version (ClawTweaksSetup.csproj &lt;Version&gt;, a separate number from
    /// the main ClawTweaks app version):
    ///
    ///   1. "Is this build too old to be trusted?" — <c>minimumSetupVersion</c>, a FLOOR. Only bumped
    ///      when an older Center is known broken, so it normally flags nobody. Produces a warning.
    ///   2. "Is there a newer build?" — <c>latestSetupVersion</c> + URL + SHA-256. Produces the
    ///      offer that <see cref="CenterUpdater"/> acts on.
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

            // --- update offer (all four are set together or not at all; see IsUpdateOffered) ---
            public Version LatestVersion;
            public string LatestUrl;
            public string LatestSha256;

            /// <summary>True only when the manifest advertises a NEWER build AND everything needed to
            /// fetch it safely is present and well-formed. A half-filled manifest entry must never
            /// surface an Update button that then can't verify what it downloaded.</summary>
            public bool IsUpdateOffered =>
                LatestVersion != null && LatestVersion > RunningVersion &&
                LatestUrl != null && LatestSha256 != null;
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

                // The update half is optional and independently validated: a manifest that only has
                // the floor (schemaVersion 1) still parses fine and simply offers no update.
                if (Version.TryParse(GetString(root, "latestSetupVersion") ?? "", out var latest))
                    result.LatestVersion = latest;
                result.LatestUrl = SanitizeUrl(GetString(root, "latestSetupUrl"));
                result.LatestSha256 = SanitizeSha256(GetString(root, "latestSetupSha256"));

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>
        /// The manifest is fetched over the network, so its URL is untrusted input that we are about to
        /// download an EXECUTABLE from. Pin it to HTTPS on the hosts GitHub actually serves release
        /// assets from, so a compromised/typo'd manifest can't redirect the update to somewhere else.
        /// Returns null (= no update offered) rather than throwing on anything unexpected.
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
            if (!allowed) return null;

            // Must be the exe itself — never a zip/installer/script we'd then have to unpack and run.
            if (!uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

            return uri.AbsoluteUri;
        }

        /// <summary>Exactly 64 hex digits, lowercased for comparison. Anything else = no update offered;
        /// the hash is the only thing standing between "we downloaded a file" and "we run it".</summary>
        private static string SanitizeSha256(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return null;
            sha = sha.Trim().Replace("-", "").ToLowerInvariant();
            return Regex.IsMatch(sha, "^[0-9a-f]{64}$") ? sha : null;
        }
    }
}
