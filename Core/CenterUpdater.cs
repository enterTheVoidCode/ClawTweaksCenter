using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Center updating itself: fetches the newer CTW_Center_&lt;version&gt;_Setup.exe advertised by
    /// <see cref="SetupVersionCheck"/>, verifies it, and hands over to it. The new exe then runs the
    /// ordinary Gate #0 flow in App.OnStartup (not running from Program Files → Update prompt → one
    /// UAC → <see cref="SelfInstaller.InstallAndRelaunch"/>), so there is exactly ONE install path and
    /// this class adds no second way to write to Program Files.
    ///
    /// ── Why this is shaped the way it is: antivirus ──────────────────────────────────────────────
    /// "Download an executable and run it" is the textbook dropper behaviour, and Center is unsigned,
    /// so Defender's ML has nothing but behaviour to judge us on. The project already learned this
    /// once — see HelperControl's note on why the helper deploys itself via its own signed --setup
    /// instead of the setup writing an exe + scheduled task, and why Install.ps1 stopped doing
    /// script-driven persistence. Every rule below exists to keep this path boring:
    ///
    ///   • NEVER silent. The download only starts from an explicit click on an Update button. A
    ///     background self-download is the single most heuristic-tripping thing we could do, and it is
    ///     also the thing a user cannot consent to.
    ///   • Stable, named destination — %LOCALAPPDATA%\ClawTweaks\Updates, alongside center_crash.log —
    ///     and the release asset keeps its real filename. A recognisable path with a recognisable name
    ///     scores far better than a random name in %TEMP%, which is where droppers stage.
    ///   • SHA-256 from the manifest, verified before the file is ever launched, mismatch = delete.
    ///     This is what makes "we run what we downloaded" an actual claim rather than a hope.
    ///   • No cmd.exe, no PowerShell, no schtasks, no registry writes, no self-overwrite, and no
    ///     in-memory execution. Plain File I/O and one ShellExecute of a file on disk.
    ///   • Runs unelevated. Center is asInvoker (see ElevationGate); the download needs no rights at
    ///     all, and the elevation happens later, inside the new exe, where the user sees why.
    ///
    /// One deliberate non-decision, so nobody "fixes" it later: we do NOT attach a Mark-of-the-Web
    /// zone identifier to the downloaded file. We also don't strip one — File.Create simply never
    /// creates it. Adding it would make SmartScreen block an unsigned exe behind a scary
    /// "unrecognised app" dialog on every single update, and it would buy nothing: the manifest's
    /// SHA-256 already pins exactly which bytes we will launch, which is a stronger statement than
    /// "this came from the internet". Defender's real-time protection scans the write either way.
    /// </summary>
    public static class CenterUpdater
    {
        /// <summary>Next to center_crash.log — one obvious ClawTweaks-owned folder rather than a
        /// throwaway temp path (see the antivirus note on the class).</summary>
        public static string UpdateDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Updates");

        public sealed class DownloadResult
        {
            public bool Success;
            public string ExePath;
            public string Error;
        }

        /// <summary>
        /// Downloads the advertised build and verifies its SHA-256. Never throws — a failure comes back
        /// as <see cref="DownloadResult.Error"/> so the caller can show it and leave the user on a
        /// working screen with the manual GitHub route still open to them.
        /// </summary>
        public static async Task<DownloadResult> DownloadAsync(
            SetupVersionCheck.Result check, Action<string> log = null, IProgress<int> progress = null)
        {
            if (check == null || !check.IsUpdateOffered)
                return new DownloadResult { Error = "No verified update is available." };

            string destPath = null;
            try
            {
                Directory.CreateDirectory(UpdateDir);

                // Keep the asset's own name (CTW_Center_<version>_Setup.exe). GetFileName on the URL
                // path is the sanitiser here: it cannot produce a directory separator, and SanitizeUrl
                // already guaranteed the .exe suffix.
                string fileName = Path.GetFileName(new Uri(check.LatestUrl).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"CTW_Center_{check.LatestVersion}_Setup.exe";
                destPath = Path.Combine(UpdateDir, fileName);

                // A verified copy from an interrupted-then-retried run is worth reusing; an unverified
                // leftover is not, and gets overwritten below.
                if (File.Exists(destPath) && HashMatches(destPath, check.LatestSha256))
                {
                    log?.Invoke($"Already downloaded and verified: {fileName}");
                    return new DownloadResult { Success = true, ExePath = destPath };
                }

                log?.Invoke($"Downloading ClawTweaks Center {check.LatestVersion}…");
                await DownloadFileAsync(check.LatestUrl, destPath, progress).ConfigureAwait(false);

                log?.Invoke("Verifying…");
                if (!HashMatches(destPath, check.LatestSha256))
                {
                    // Deleted rather than kept: an exe whose contents we cannot vouch for must not be
                    // left lying in a ClawTweaks folder where a later run (or a user) might launch it.
                    TryDelete(destPath);
                    return new DownloadResult
                    {
                        Error = "The downloaded file did not match the expected checksum and was deleted. " +
                                "Please download the update manually from GitHub.",
                    };
                }

                log?.Invoke("Verified.");
                CleanUpOlderDownloads(destPath);
                return new DownloadResult { Success = true, ExePath = destPath };
            }
            catch (Exception ex)
            {
                if (destPath != null) TryDelete(destPath);
                return new DownloadResult { Error = ex.Message };
            }
        }

        /// <summary>
        /// Starts the verified installer and reports whether it actually launched. The caller shuts
        /// this process down on true — the new exe copies itself over ours in Program Files, which a
        /// running process would block with a sharing violation.
        ///
        /// Deliberately started WITHOUT InstallCenterWindow.ResumeArg: that flag means "this launch is
        /// the elevated relaunch of an install the user already confirmed", and it is not true here.
        /// The new exe shows its own Update gate with both version numbers, so what gets installed over
        /// what is on screen before any UAC prompt appears.
        /// </summary>
        public static bool LaunchInstaller(string exePath, Action<string> log = null)
        {
            // Unelevated on purpose: if this Center is elevated for any reason, the installer must not
            // inherit that, or its own elevation gate finds IsAdmin() already true and installs without
            // ever prompting. See ElevationGate.LaunchUnelevated.
            return ElevationGate.LaunchUnelevated(exePath, log);
        }

        private static async Task DownloadFileAsync(string url, string destPath, IProgress<int> progress)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            using (var httpStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var fileStream = File.Create(destPath))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await httpStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                    read += n;
                    if (total.HasValue && total.Value > 0)
                        progress?.Report((int)(read * 100 / total.Value));
                }
            }
        }

        private static bool HashMatches(string path, string expectedSha256)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                string actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Keeps only the build just downloaded. Without this the folder slowly fills with
        /// ~63 MB exes, one per update — and a pile of stale installers is exactly the kind of thing
        /// that later gets launched by accident.</summary>
        private static void CleanUpOlderDownloads(string keepPath)
        {
            try
            {
                foreach (string file in Directory.GetFiles(UpdateDir, "*.exe"))
                {
                    if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                        TryDelete(file);
                }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
