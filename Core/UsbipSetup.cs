using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Downloads + verifies + installs usbip-win2 (the VIIPER backend prerequisite). Ported 1:1 from
    /// the helper's UsbipInstaller: pinned signed release URL, Authenticode + publisher verification,
    /// then the interactive Inno installer (/NORESTART — the visible wizard is required so the user
    /// confirms the driver-install prompt). Exit 3010 = success but a reboot is required.
    /// </summary>
    public static class UsbipSetup
    {
        private const string DownloadUrl =
            "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe";

        private static readonly string[] ExpectedSignerSubstrings = { "Scheibling", "Cloudyne" };

        public enum Result { Failed, Success, RebootRequired }

        /// <summary>Blocks until done. Returns Success, RebootRequired (exit 3010) or Failed.</summary>
        public static Result Run(Action<string> log = null)
        {
            string exePath = null;
            try
            {
                exePath = Path.Combine(Path.GetTempPath(), "ClawTweaks_USBip-setup.exe");
                log?.Invoke("Downloading signed usbip-win2 installer…");
                if (!Download(DownloadUrl, exePath)) { log?.Invoke("Download failed."); return Result.Failed; }

                if (!AuthenticodeVerifier.IsSignedBy(exePath, ExpectedSignerSubstrings))
                {
                    log?.Invoke("Signature verification failed — refusing to run the installer.");
                    return Result.Failed;
                }

                log?.Invoke("Signature OK — launching installer (confirm the administrator and driver prompts)…");
                // Elevated via ShellExecute so this works with Center running unelevated; see
                // ToolInstaller.RunElevated for why UseShellExecute = false cannot work here.
                int code = ToolInstaller.RunElevated(exePath, "/NORESTART", 10 * 60 * 1000, log);
                if (code == ToolInstaller.UserDeclinedUac || code == ToolInstaller.LaunchFailed) return Result.Failed;
                log?.Invoke($"Installer finished (exit {code}).");
                return code == 3010 ? Result.RebootRequired : (code == 0 ? Result.Success : Result.Failed);
            }
            catch (Exception ex)
            {
                log?.Invoke("usbip install error: " + ex.Message);
                return Result.Failed;
            }
            finally
            {
                if (exePath != null) { try { File.Delete(exePath); } catch { } }
            }
        }

        private static bool Download(string url, string destPath)
        {
            try
            {
                using var http = new HttpClient();
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
