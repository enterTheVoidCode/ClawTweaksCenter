using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Center runs unelevated by default (see app.manifest) and only needs admin for a handful of
    /// genuinely privileged actions: self-installing into Program Files, installing the certificate/
    /// MSIX/drivers, and uninstalling. Everything else — including every command sent to the Helper
    /// (TDP, RGB, fan curves, ...) — works unelevated, the same way the sandboxed Game Bar widget
    /// already talks to the elevated Helper today; UAC only gates creating a NEW elevated process, not
    /// sending a pipe message to one that's already running elevated.
    ///
    /// Call <see cref="EnsureElevatedOrRelaunch"/> as the first line of each of those privileged
    /// actions. It is a plain IsInRole check, so calling it from several different actions across one
    /// run is cheap: the first privileged click relaunches Center elevated (one UAC prompt); every
    /// later call in that same (now-elevated) process just falls through without prompting again.
    /// </summary>
    public static class ElevationGate
    {
        /// <summary>Plain IsInRole check — cheap enough to call before deciding whether a relaunch (and
        /// the file/arg plumbing that goes with resuming after one) is even needed.</summary>
        public static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>
        /// Returns true if already elevated (continue with the privileged action). Otherwise relaunches
        /// Center elevated with the same command-line arguments and shuts down this instance, then
        /// returns false. Also returns false (without relaunching) if the user declined the UAC prompt —
        /// callers must check the return value and stop, not assume elevation happened.
        /// </summary>
        public static bool EnsureElevatedOrRelaunch(string[] args)
        {
            if (IsAdmin())
                return true;

            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Join(" ", (args ?? Array.Empty<string>()).Select(EscapeArg)),
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch (Exception)
            {
                // Any relaunch failure — the user declined the UAC prompt (ERROR_CANCELLED 1223), or the
                // shell couldn't start the elevated process at all — must NOT propagate: on a background
                // thread it would crash the whole app, and even on the UI thread the caller can present a
                // clean "administrator rights are required" message instead. Returning false (without
                // shutting down) leaves this instance usable so that message can be shown.
                return false;
            }

            Application.Current.Shutdown();
            return false;
        }

        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            return arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }
}
