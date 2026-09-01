using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// What is left of Center's elevation handling now that Center never elevates: a way to ASK whether
    /// this process happens to be elevated, and a way to make sure something it starts is NOT.
    ///
    /// There used to be an EnsureElevatedOrRelaunch here — call it first in a privileged action, and it
    /// relaunched Center with a UAC prompt. Every one of its callers is gone: the self-install moved
    /// into the user's own profile, the driver installs went back to the vendors, and the certificate
    /// import went to Windows' own wizard. See app.manifest for the full picture. Do not bring it back
    /// without moving one of those decisions back first — a relaunch-with-UAC that exists "just in
    /// case" is how the app quietly stops being an unelevated app again.
    ///
    /// <see cref="IsAdmin"/> survives because a user can always right-click → Run as administrator, and
    /// <see cref="LaunchUnelevated"/> needs to know when that happened.
    /// </summary>
    public static class ElevationGate
    {
        /// <summary>Whether THIS process is running elevated. Not a permission request — a fact about
        /// the current token, used to decide whether a child needs de-elevating.</summary>
        public static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>
        /// Starts an exe WITHOUT passing on our own elevation, and returns whether it started.
        ///
        /// This exists because a child process inherits its parent's token: anything Center launches
        /// while elevated comes up elevated too, and stays that way for its whole life. That silently
        /// defeated the asInvoker design — after a self-install (which back then elevated for the
        /// Program Files copy), the relaunched Center ran at High integrity, so every later privileged
        /// action inside it found IsAdmin() already true and never prompted. Measured on-device
        /// 2026-07-29: an update installed into Program Files with no UAC prompt at all, on a machine
        /// with UAC at the normal prompt level and a non-admin user.
        ///
        /// Center no longer elevates itself, so that exact sequence can't recur — but a user can always
        /// right-click → Run as administrator, and then the installed copy this starts would inherit an
        /// admin token and run that way indefinitely. Still worth the indirection.
        ///
        /// The de-elevation works by handing the path to the ALREADY-RUNNING shell, which lives at
        /// Medium integrity: explorer.exe is single-instance, so this exe hands the request to that
        /// existing explorer and exits, and the target ends up as the shell's child — unelevated —
        /// exactly as if the user had double-clicked it. It is only needed when we ARE elevated;
        /// otherwise a plain start already produces the right token and is the more direct path.
        /// </summary>
        /// <param name="args">
        /// Optional command line for the child. ⚠️ It CANNOT survive the shell indirection below:
        /// "explorer.exe path" opens the file and drops everything after it. That path is only taken
        /// when THIS process is elevated, so a caller that needs arguments must be unelevated - which
        /// the installer's RunOnce hand-off is. When they would be dropped, this says so rather than
        /// starting a process that quietly does something other than what was asked.
        /// </param>
        public static bool LaunchUnelevated(string exePath, Action<string> log = null, string args = null)
        {
            try
            {
                // No shell running (rare, but it happens on a freshly-crashed/restarting explorer):
                // the indirection would go nowhere and silently launch nothing, which is worse than
                // launching with the wrong token. Start it directly and say so in the log.
                bool viaShell = IsAdmin() && Process.GetProcessesByName("explorer").Length > 0;
                if (!viaShell)
                {
                    if (IsAdmin())
                        log?.Invoke("No running shell to de-elevate through — starting with this process's token.");
                    var psi = new ProcessStartInfo(exePath) { UseShellExecute = true };
                    if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
                    Process.Start(psi);
                    return true;
                }

                if (!string.IsNullOrEmpty(args))
                    log?.Invoke($"De-elevating through the shell, so the arguments '{args}' are dropped.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = "\"" + exePath + "\"",
                    UseShellExecute = false,
                });
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not start {System.IO.Path.GetFileName(exePath)}: {ex.Message}");
                return false;
            }
        }
    }
}
