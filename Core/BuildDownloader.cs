using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using ClawTweaksSetup.Core.Sources;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Downloads a <see cref="BuildSource"/> picked in the Center menu and stages it into a folder
    /// that <see cref="PackageInstaller"/>/<see cref="CertInstaller"/> can treat as
    /// <see cref="SetupContext.AssetRoot"/> — either just the .msix (cert already trusted) or the
    /// full installer ZIP, extracted flat (matches how Build-Setup.ps1 zips the release folder).
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

        public static async Task<string> DownloadAndStageAsync(
            BuildSource source, bool certAlreadyTrusted, Action<string> log = null, IProgress<int> progress = null)
        {
            string safeVersion = string.Join("_", source.Version.Split(Path.GetInvalidFileNameChars()));
            string dir = Path.Combine(Path.GetTempPath(), "ClawTweaksCenter", safeVersion);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            bool msixOnly = certAlreadyTrusted && source.MsixUrl != null;

            if (msixOnly)
            {
                log?.Invoke($"Cert already trusted — downloading just the .msix ({source.Version})…");
                string msixPath = Path.Combine(dir, "package.msix");
                await DownloadFileAsync(source.MsixUrl, msixPath, progress);
                log?.Invoke("Download complete.");
                VerifyStagedPackage(dir, log);
                return dir;
            }

            log?.Invoke($"Downloading installer ({source.Version})…");
            string zipPath = Path.Combine(dir, "installer.zip");
            await DownloadFileAsync(source.ZipUrl, zipPath, progress);
            log?.Invoke("Extracting…");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true);
            }
            catch (InvalidDataException)
            {
                // A truncated archive normally trips here, because a ZIP's central directory sits at
                // the very end of the file.
                throw new IOException("The downloaded installer archive is damaged. " + IncompleteDownloadHint);
            }
            try { File.Delete(zipPath); } catch { }
            VerifyStagedPackage(dir, log);
            log?.Invoke("Ready.");
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
