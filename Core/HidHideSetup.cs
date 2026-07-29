using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Direct-download fallback for HidHide, used only when winget fails
    /// (see <see cref="ToolInstaller.InstallHidHide"/>). Mirrors the helper's Install-HidHide fallback
    /// in Setup-Tools.ps1 — see the parity note on <see cref="ToolDetect"/>.
    ///
    /// Why HidHide gets a fallback when RTSS does not: without it the physical pad can never be hidden,
    /// and the failure is silent. A Claw 8 EX ran for days that way — the Game Bar stopped reacting to
    /// any controller input at all, and because every surface reported HidHide as "installed" (a
    /// leftover registry key), nothing ever offered a repair. When winget is the only channel, a broken
    /// winget is a permanent outage. Keep this list to the tools where that is true: HidHide and PawnIO.
    ///
    /// The MSI is verified before it runs — Authenticode plus a publisher check, the same discipline as
    /// <see cref="UsbipSetup"/>. Unlike usbip the URL cannot be pinned: HidHide's asset filenames carry
    /// the version (HidHide_1.5.x_x64.msi), so a hard-coded /latest/download/&lt;name&gt; would 404 for
    /// everyone the moment a release ships. The release API is asked for the current asset instead, and
    /// the signature check is what makes that acceptable.
    /// </summary>
    public static class HidHideSetup
    {
        private const string ReleaseApiUrl = "https://api.github.com/repos/nefarius/HidHide/releases/latest";
        private const string SignerSubstring = "Nefarius";

        /// <summary>Blocks until done. True only when HidHide is actually detectable afterwards —
        /// msiexec's exit code is a hint, ToolDetect is the authority.</summary>
        public static bool Run(Action<string> log = null)
        {
            string msiPath = null;
            try
            {
                string url = ResolveMsiUrl(log);
                if (url == null) return false;

                msiPath = Path.Combine(Path.GetTempPath(), "ClawTweaks_HidHide_setup.msi");
                log?.Invoke("Downloading the HidHide installer…");
                if (!Download(url, msiPath)) { log?.Invoke("Download failed."); return false; }

                if (!AuthenticodeVerifier.IsSignedBy(msiPath, SignerSubstring))
                {
                    log?.Invoke("Signature verification failed — refusing to run the HidHide installer.");
                    return false;
                }

                log?.Invoke("Signature OK — installing HidHide…");
                // Elevated via ShellExecute so this works with Center unelevated; see
                // ToolInstaller.RunElevated. /qn keeps msiexec silent — the UAC prompt is the only
                // dialog the user sees, which is the point of Center staying unelevated.
                int code = ToolInstaller.RunElevated("msiexec.exe", $"/i \"{msiPath}\" /qn /norestart",
                                                     10 * 60 * 1000, log);
                if (code == ToolInstaller.UserDeclinedUac || code == ToolInstaller.LaunchFailed) return false;

                // 3010 = installed, reboot required. That is the NORMAL outcome for a driver, not an
                // error — treating it as one would report a perfectly good install as failed.
                if (code != 0 && code != 3010)
                {
                    log?.Invoke($"HidHide installer exited with code {code}.");
                    return false;
                }

                bool installed = ToolDetect.HidHide().Installed;
                if (!installed)
                {
                    log?.Invoke(code == 3010
                        ? "HidHide installed but needs a reboot before it can be used."
                        : "HidHide installer reported success but the driver is still not detectable.");
                }
                return installed;
            }
            catch (Exception ex)
            {
                log?.Invoke("HidHide install error: " + ex.Message);
                return false;
            }
            finally
            {
                if (msiPath != null) { try { File.Delete(msiPath); } catch { } }
            }
        }

        private static string ResolveMsiUrl(Action<string> log)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string json = http.GetStringAsync(ReleaseApiUrl).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                {
                    log?.Invoke("HidHide release has no assets.");
                    return null;
                }

                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

                    // Same pinning rule as SetupVersionCheck.SanitizeUrl: this URL comes off the network
                    // and we are about to download an installer from it.
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    if (uri.Scheme != Uri.UriSchemeHttps) continue;
                    if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) &&
                        !uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase)) continue;

                    return url;
                }

                log?.Invoke("No .msi asset found in the latest HidHide release.");
                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not reach the HidHide release feed: " + ex.Message);
                return null;
            }
        }

        private static bool Download(string url, string destPath)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");
                using var resp = http.GetAsync(url).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using (var fs = File.Create(destPath))
                    resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                var fi = new FileInfo(destPath);
                return fi.Exists && fi.Length > 0;
            }
            catch { return false; }
        }
    }
}
