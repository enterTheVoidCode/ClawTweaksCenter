using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// MSI Center M control for the post-install finalization phase. It fights ClawTweaks for the
    /// controller and LED, so after installing we offer to close it now and (optionally) uninstall it.
    /// </summary>
    public static class CenterM
    {
        // MSI Center M's process name(s). MSI has shipped a few variants over versions.
        private static readonly string[] ProcessNames = { "MSI Center M", "MSI_Center_M", "MSICenterM", "MSI Center", "MSI_Center" };

        public static bool IsRunning()
        {
            foreach (var n in ProcessNames)
                if (Process.GetProcessesByName(n).Length > 0) return true;
            return false;
        }

        /// <summary>Ends all MSI Center M processes for this session. Returns true if any were closed.</summary>
        public static bool CloseNow(Action<string> log = null)
        {
            bool any = false;
            foreach (var n in ProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { p.Kill(); p.WaitForExit(3000); any = true; log?.Invoke($"Closed {p.ProcessName}."); }
                    catch (Exception ex) { log?.Invoke($"Could not close {n}: {ex.Message}"); }
                    finally { p.Dispose(); }
                }
            }
            if (!any) log?.Invoke("MSI Center M was not running.");
            return any;
        }

        /// <summary>
        /// Whether MSI's Quick Settings Game Bar widget is registered for this user.
        ///
        /// Asked after re-enabling Center M, because "Center M is back" and "all of Center M is back"
        /// are different answers. Disabling Center M removes this package with -AllUsers, and that
        /// takes the staged copy Windows would re-register from with it — measured on the dev machine
        /// 2026-08-26, with a control cell. When it is gone, only reinstalling MSI Center M restores
        /// the widget, and the flow says so instead of reporting a clean success.
        /// </summary>
        public static bool IsGameBarWidgetInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    "\"(Get-AppxPackage -Name '9426MICRO-STARINTERNATION.MSIQuickSettings' | " +
                    "Select-Object -First 1 -ExpandProperty PackageFullName)\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string outp = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(20000)) { try { proc.Kill(); } catch { } return false; }
                return !string.IsNullOrWhiteSpace(outp);
            }
            catch { return false; }
        }

        /// <summary>Finds the MSI Center (M) uninstall command in Add/Remove Programs, if present.</summary>
        public static bool IsInstalled() => FindUninstall(out _, out _);

        private static bool FindUninstall(out string display, out string cmd)
        {
            display = null; cmd = null;
            foreach (var root in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(root);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var k = key.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.IndexOf("MSI Center", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var quiet = k.GetValue("QuietUninstallString") as string;
                        var normal = k.GetValue("UninstallString") as string;
                        cmd = !string.IsNullOrEmpty(quiet) ? quiet : normal;
                        display = name;
                        if (!string.IsNullOrEmpty(cmd)) return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
