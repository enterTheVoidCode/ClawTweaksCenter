using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Core.Sources
{
    /// <summary>
    /// Lists ClawTweaks widget builds from the CENTER repo's GitHub releases, split into stable
    /// releases and test builds. Unauthenticated — a handful of manual refreshes per session stays
    /// well under GitHub's 60/hour anonymous rate limit.
    ///
    /// ── Where widget builds live, and why this reads the tag ─────────────────────────────────────
    /// Since 0.2.39 the widget MSIX is published in the Center repo, next to Center's own Velopack
    /// feed (Doku/PLAN_User_Version_Transition_Velopack.md §5 in the app repo). EVERY widget release
    /// is a prerelease, so the Latest badge and the GitHub front page stay on the Center release. That
    /// uses up the prerelease flag: it no longer says stable or test. The tag does:
    ///
    ///   widget-&lt;ver&gt;        stable  → "Release"
    ///   widget-test-&lt;ver&gt;   test    → "Test build"
    ///   anything else        ignored (Center releases, legacy tags)
    ///
    /// The test prefix is checked first, because "widget-test-" also starts with "widget-".
    ///
    /// ── No installer ZIP ─────────────────────────────────────────────────────────────────────────
    /// A build is listed only when it carries a .msix. The app repo's _Installer.zip releases are not
    /// read at all: those are for Centers before 0.2.39, and once the transition release is out the
    /// app repo carries only the Inno setup. This Center never installs a build from a ZIP.
    /// </summary>
    public static class GitHubReleaseSource
    {
        // per_page=30: Center releases sit between the widget releases, and the publish script caps
        // widget releases between two Center releases at nine, so 30 always reaches past the last
        // few of each kind.
        private const string ApiUrl = "https://api.github.com/repos/enterTheVoidCode/ClawTweaksCenter/releases?per_page=30";

        private const string TestTagPrefix = "widget-test-";
        private const string StableTagPrefix = "widget-";

        public static async Task<(List<BuildSource> Releases, List<BuildSource> TestBuilds)> FetchAsync()
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            string json = await http.GetStringAsync(ApiUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var releases = new List<BuildSource>();
            var testBuilds = new List<BuildSource>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (GetBool(el, "draft")) continue;

                string tag = GetString(el, "tag_name") ?? "";
                if (!TryClassify(tag, out bool isTest, out string version)) continue;

                string name = GetString(el, "name") ?? tag;
                DateTime when = DateTime.TryParse(GetString(el, "published_at"), out var d) ? d : DateTime.MinValue;

                string msixUrl = null; long? size = null;
                if (el.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string assetName = GetString(asset, "name") ?? "";
                        if (assetName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                            assetName.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
                        {
                            msixUrl = GetString(asset, "browser_download_url");
                            size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : (long?)null;
                            break;
                        }
                    }
                }

                if (msixUrl == null) continue; // a widget tag without its package: nothing installable

                var build = new BuildSource
                {
                    Origin = isTest ? "Test build" : "Release",
                    Version = version,
                    Title = name,
                    When = when,
                    SizeBytes = size,
                    MsixUrl = msixUrl,
                    Body = GetString(el, "body"),
                };
                (isTest ? testBuilds : releases).Add(build);
            }

            releases = releases.OrderByDescending(b => b.When).Take(2).ToList();
            testBuilds = testBuilds.OrderByDescending(b => b.When).Take(3).ToList();
            return (releases, testBuilds);
        }

        /// <summary>Reads stable/test and the bare version out of a widget tag. False for every tag
        /// that is not a widget release.</summary>
        private static bool TryClassify(string tag, out bool isTest, out string version)
        {
            isTest = false;
            version = null;
            if (tag.StartsWith(TestTagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                isTest = true;
                version = tag.Substring(TestTagPrefix.Length);
            }
            else if (tag.StartsWith(StableTagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                version = tag.Substring(StableTagPrefix.Length);
            }
            return !string.IsNullOrWhiteSpace(version);
        }

        private static string GetString(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static bool GetBool(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.True;
    }
}
