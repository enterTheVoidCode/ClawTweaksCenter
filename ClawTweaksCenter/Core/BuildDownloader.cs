using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using ClawTweaksCenter.Core.Sources;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Downloads a <see cref="BuildSource"/> picked in the Center menu and stages its .msix into a
    /// folder that <see cref="PackageInstaller"/> can treat as <see cref="SetupContext.AssetRoot"/>.
    /// </summary>
    public static class BuildDownloader
    {
        /// <summary>
        /// Shown whenever a download did not arrive whole. Deliberately points at the connection
        /// rather than at ClawTweaks: an interrupted transfer is the one cause the user can act on,
        /// and Center aborts here instead of retrying so a genuinely broken server does not hide
        /// behind an automatic second attempt.
        /// </summary>
        private const string IncompleteDownloadHint =
            "The download did not complete. Check your internet connection and try again.";

        /// <summary>
        /// Where a downloaded package is staged for deployment. NOT the user's Temp folder, and that is
        /// the whole point.
        ///
        /// MEASURED (2026-08-04, one machine, Korean Windows). Deploying the very same bytes failed from
        /// <c>%LOCALAPPDATA%\Temp\ClawTweaksCenter\…</c> with 0x80073CF0 "the package could not be
        /// opened" — a .NET FileNotFoundException, i.e. a failure to REACH the file, not to read its
        /// contents — while the identical .msix copied to a folder outside the profile installed
        /// without complaint. The file hash matched a known-good install byte for byte, so the package
        /// was never the variable; the path was.
        ///
        /// ProgramData rather than another folder in the profile, because the two candidate causes
        /// (something specific to Temp, or the deployment service not getting through the profile at
        /// all) are not distinguishable from one report, and this location is outside both. An
        /// unelevated process may create its own subtree here — the inherited CREATOR OWNER right —
        /// so this costs no UAC.
        ///
        /// Falls back to Temp when ProgramData cannot be created: a locked-down machine must still be
        /// able to install, and Temp is where it worked for everyone else.
        /// </summary>
        public static string StagingRoot
        {
            get
            {
                try
                {
                    string root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "ClawTweaks", "pkg");
                    Directory.CreateDirectory(root);
                    return root;
                }
                catch
                {
                    return Path.Combine(Path.GetTempPath(), "ClawTweaksCenter");
                }
            }
        }

        /// <summary>
        /// Downloads the build's .msix and nothing else.
        ///
        /// There is no installer-ZIP path any more, deleted rather than kept as a fallback: the ZIP
        /// carried the certificate and Install.ps1 for a machine that had neither, and that machine now
        /// gets the Inno setup instead. Center only offers a download while the helper runs, and a
        /// running helper proves a complete install - certificate included - so the package is all
        /// that is ever needed here.
        /// </summary>
        public static async Task<string> DownloadAndStageAsync(
            BuildSource source, Action<string> log = null, IProgress<int> progress = null)
        {
            if (string.IsNullOrEmpty(source.MsixUrl))
                throw new IOException("This version has no package to download.");

            string safeVersion = string.Join("_", source.Version.Split(Path.GetInvalidFileNameChars()));
            string dir = Path.Combine(StagingRoot, safeVersion);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            log?.Invoke($"Downloading the package ({source.Version})…");
            string msixPath = Path.Combine(dir, "package.msix");
            await DownloadFileAsync(source.MsixUrl, msixPath, progress);
            log?.Invoke("Download complete.");
            VerifyStagedPackage(dir, log);
            return dir;
        }

        /// <summary>
        /// Confirms the staged package is a package before Windows is asked to deploy it: readable as
        /// a container, and carrying its manifest.
        ///
        /// WHY. Nothing used to check anything. A transfer that ends early — proxy, flaky Wi-Fi, an
        /// MTU black hole — produces a short file and no error at all, and the first component to
        /// notice was the Windows deployment service, which answers with 0x80073CF0 "the package
        /// could not be opened". That reads like a broken package or a broken Windows and tells the
        /// user nothing about the actual cause. This check separates the two: if it fires, the file
        /// we produced is at fault; if it passes and deployment still refuses, the problem is on the
        /// machine.
        /// </summary>
        private static void VerifyStagedPackage(string dir, Action<string> log)
        {
            string pkg = null;
            foreach (var ext in new[] { "*.msixbundle", "*.msix", "*.appxbundle", "*.appx" })
            {
                var hits = Directory.GetFiles(dir, ext, SearchOption.TopDirectoryOnly);
                if (hits.Length > 0) { pkg = hits[0]; break; }
            }
            if (pkg == null)
                throw new IOException("The download contains no package file. " + IncompleteDownloadHint);

            long bytes = new FileInfo(pkg).Length;
            bool isBundle = pkg.EndsWith("bundle", StringComparison.OrdinalIgnoreCase);
            string manifest = isBundle ? "AppxMetadata/AppxBundleManifest.xml" : "AppxManifest.xml";

            try
            {
                using var zip = ZipFile.OpenRead(pkg);
                if (zip.GetEntry(manifest) == null)
                    throw new IOException("The downloaded package is incomplete — its manifest is missing. " +
                                          IncompleteDownloadHint);
            }
            catch (InvalidDataException)
            {
                throw new IOException("The downloaded package is not readable. " + IncompleteDownloadHint);
            }

            log?.Invoke($"Package verified: {Path.GetFileName(pkg)}, {bytes / (1024.0 * 1024.0):F1} MB.");
        }

        private static async Task DownloadFileAsync(string url, string destPath, IProgress<int> progress)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClawTweaks");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            using var httpStream = await resp.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(destPath);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await httpStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, n);
                read += n;
                if (total.HasValue && total.Value > 0)
                    progress?.Report((int)(read * 100 / total.Value));
            }
            await fileStream.FlushAsync();

            // Content-Length used to feed the progress bar and nothing else, so a stream that ended
            // early just left the loop and the short file travelled all the way to Add-AppxPackage.
            // Compare it, and stop here where the cause is still knowable.
            if (total.HasValue && total.Value > 0 && read != total.Value)
                throw new IOException(IncompleteDownloadHint +
                                      $" (received {read:N0} of {total.Value:N0} bytes)");

            if (read == 0)
                throw new IOException("The download produced an empty file. " + IncompleteDownloadHint);
        }
    }
}
