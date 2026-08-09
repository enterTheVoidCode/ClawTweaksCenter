using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Core.Sources
{
    /// <summary>
    /// Lists the last few nightlies from the shared "Nightlys" Google Drive folder (public,
    /// "anyone with the link") via the Drive API v3, using an API key — no OAuth/login needed since
    /// the folder is publicly readable. Only full installer ZIPs are uploaded there (no separate
    /// msix), so every nightly always goes through <see cref="BuildDownloader"/>'s full-zip path.
    /// </summary>
    public static class GoogleDriveSource
    {
        private const string FolderId = "1yLUyaYy20eZHFWy0ygyP6LbJsApAHBXF";

        // Read-only key so end users get a working nightly list out of the box, with no per-user
        // setup step.
        //
        // THIS IS PUBLIC ON PURPOSE. Please do not "fix" it by moving it out of the repository —
        // that was considered and rejected, for reasons that do not change:
        //
        //   • It ships INSIDE the exe either way. Any client-embedded key is extractable from the
        //     binary, so removing it from source would have made it harder to find, never secret.
        //   • A desktop app cannot be restricted. Google's application restrictions are HTTP
        //     referrer, IP range, Android app and iOS app — a WPF exe is none of those. The API
        //     restriction below is the strongest control available here, not a first step.
        //   • Nothing confidential is behind it. The Drive folder is shared "anyone with the link";
        //     the key grants no access a browser would not already have.
        //
        // What it therefore protects is availability, not secrecy: someone abusing the key burns the
        // project's Drive quota, and the nightly list stops working for real users. The controls that
        // actually matter live in Google Cloud Console — API restriction limited to the Drive API,
        // plus a daily quota cap. If it is ever abused: issue a new key, disable the old one, rebuild.
        //
        // Expect GitHub secret scanning to flag this line. That alert is anticipated.
        private const string ApiKey = "AIzaSyCnOwAdpy8Z3CFkCp0nNz2SovHyuBPFD2o";

        private static readonly Regex VersionRegex = new Regex(@"ClawTweaks_([\d.]+)_Installer\.zip", RegexOptions.IgnoreCase);

        public static async Task<List<BuildSource>> FetchAsync()
        {
            string apiKey = ApiKey;

            string url = "https://www.googleapis.com/drive/v3/files"
                + "?q=" + Uri.EscapeDataString($"'{FolderId}' in parents and trashed=false")
                + "&orderBy=" + Uri.EscapeDataString("modifiedTime desc")
                + "&pageSize=5"
                + "&fields=" + Uri.EscapeDataString("files(id,name,modifiedTime,size)")
                + "&key=" + apiKey;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");

            string json = await http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var result = new List<BuildSource>();
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var f in files.EnumerateArray())
            {
                string id = f.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string name = f.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (id == null || name == null) continue;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                DateTime when = f.TryGetProperty("modifiedTime", out var mt) && DateTime.TryParse(mt.GetString(), out var d)
                    ? d : DateTime.MinValue;
                long? size = f.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.String &&
                             long.TryParse(sz.GetString(), out var sizeVal) ? sizeVal : (long?)null;

                var m = VersionRegex.Match(name);
                string version = m.Success ? m.Groups[1].Value : name;

                result.Add(new BuildSource
                {
                    Origin = "Nightly",
                    Version = version,
                    Title = name,
                    When = when,
                    SizeBytes = size,
                    ZipUrl = $"https://www.googleapis.com/drive/v3/files/{id}?alt=media&key={apiKey}",
                    MsixUrl = null,
                });
            }

            return result.OrderByDescending(b => b.When).Take(3).ToList();
        }
    }
}
