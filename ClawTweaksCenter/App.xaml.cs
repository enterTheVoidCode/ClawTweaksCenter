using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClawTweaksCenter.Core;

namespace ClawTweaksCenter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Uninstall callback (registered as the Add/Remove Programs UninstallString) — clean up
            // and exit immediately, never reaching any window.
            //
            // No elevation: Center installs per-user now (%LOCALAPPDATA%\Programs + HKCU), so removing
            // it touches nothing outside this user's own profile. An OLD machine-wide install from
            // before the move is deliberately left alone — it has its own HKLM Add/Remove Programs
            // entry that elevates itself, which is the honest way for the user to remove it.
            if (Array.Exists(e.Args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                SelfInstaller.Uninstall();
                Shutdown();
                return;
            }

            ApplyDebugDeviceOverride(e.Args);

            // A gamepad-driven UI (live XInput polling, background fetches, remote images in the
            // release notes) has plenty of surface for a rare, hard-to-repro exception somewhere deep
            // in a WPF layout/render pass. None of that should be able to take the whole app down —
            // log it so a recurrence is diagnosable, and keep going instead of crashing.
            DispatcherUnhandledException += (_, ex) =>
            {
                LogCrash(ex.Exception, "DispatcherUnhandledException");
                ex.Handled = true;
            };

            // The dispatcher handler above only catches UI-thread exceptions. A fault on a background
            // thread (Task.Run install work, the elevated relaunch, a finalizer) is fatal in .NET and
            // invisible without these — they can't stop the process from ending, but they capture WHAT
            // and WHERE before it does, so the install-crash is actually diagnosable from the log.
            AppDomain.CurrentDomain.UnhandledException += (_, ea) =>
                LogCrash(ea.ExceptionObject as Exception, $"AppDomain.UnhandledException (terminating={ea.IsTerminating})");
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ea) =>
            {
                LogCrash(ea.Exception, "UnobservedTaskException");
                ea.SetObserved();
            };

            // Gate #0: Center must be running from its installed location (per-user, see
            // SelfInstaller.InstallDir) before
            // anything else — including the widget MSIX — can be installed. A naked/portable run
            // shows the install-self prompt and relaunches from there; this window never opens the
            // rest of the app itself.
            if (!SelfInstaller.IsRunningFromInstallDir())
            {
                var installedVersion = SelfInstaller.GetInstalledVersion();
                var runningVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

                InstallCenterMode mode;
                if (installedVersion == null) mode = InstallCenterMode.Install;
                else if (installedVersion < runningVersion) mode = InstallCenterMode.Update;
                else mode = InstallCenterMode.AlreadyInstalled;

                // All three modes show a window and require an explicit user action before anything
                // happens — running a Setup file must never silently do something other than what was
                // double-clicked (AlreadyInstalled points the user at the Start Menu / Game Bar widget
                // instead of launching a different already-installed copy out from under them).
                //
                // autoStart: set only when this launch IS the elevated relaunch triggered by that
                // explicit click (see InstallCenterWindow.ResumeArg / ElevationGate) — the user already
                // acted once and already granted the UAC prompt, so this proceeds straight into the
                // install instead of making them click Install/Update a second time for no extra signal.
                bool autoStart = Array.Exists(e.Args, a => a == InstallCenterWindow.ResumeArg);
                ShowForeground(new InstallCenterWindow(mode, installedVersion, runningVersion, autoStart));
                return;
            }

            // The local-package install wizard (MainWindow) is DELIBERATELY DISABLED: even when a sibling
            // .msix/.cer is present (the packaged "Center + msix" bundle), Center never runs the local
            // install path. It isn't needed — Center is installed as a Windows app and then downloads the
            // actual ClawTweaks widget releases from GitHub / nightlies from Google Drive via
            // CenterMenuWindow (which does the full tools → cert → MSIX install for a chosen build). The
            // local-msix entry also had an unresolved startup issue on the packaged bundle. User decision
            // 2026-07-23: always go to the Center menu regardless of any sibling package.
            ShowForeground(new CenterMenuWindow());
        }

        /// <summary>
        /// Shows a window and actually brings it to the front.
        ///
        /// Plain Show() is not enough when Center is started from the widget: the Game Bar owns the
        /// foreground at that moment, and Windows refuses to let another process take it - the window
        /// opens BEHIND everything and only the taskbar button flashes. The launching side hands over
        /// the right first (helper: AllowSetForegroundWindow, see Program.HotkeyHandlers.cs), and this
        /// is the other half that claims it. Both are needed; either alone does nothing.
        ///
        /// The brief Topmost flip is the fallback for the case where the grant did not arrive (started
        /// from the Start menu while a fullscreen game is up, say). It raises the window without
        /// leaving it permanently on top, which would be worse than the original problem.
        /// </summary>
        private static void ShowForeground(Window window)
        {
            window.Show();
            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();

                var hWnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
            }
            catch (Exception ex)
            {
                // Never let a focus tweak stop the window from being usable.
                LogCrash(ex);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void LogCrash(Exception ex, string source = null)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(source != null ? source + ": " : "")}{(ex?.ToString() ?? "(no exception object)")}\n\n";
            // %TEMP% (legacy location) AND a stable, discoverable path next to the other Center logs
            // (center_onboarding.log) so a user can find and send it after an install crash.
            foreach (var path in new[]
            {
                Path.Combine(Path.GetTempPath(), "ClawTweaksCenter_crash.log"),
                SafeLocalAppDataCrashPath(),
            })
            {
                if (path == null) continue;
                try { File.AppendAllText(path, line); } catch { }
            }
        }

        private static string SafeLocalAppDataCrashPath()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "center_crash.log");
            }
            catch { return null; }
        }

        /// <summary>
        /// Debug-only: --device=8ai or --device=8ex lets the Center's device-specific UI (icon,
        /// per-device version gating) be exercised without the actual hardware.
        /// </summary>
        private static void ApplyDebugDeviceOverride(string[] args)
        {
            foreach (var arg in args)
            {
                string v = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (v.StartsWith("device=")) v = v.Substring("device=".Length);
                else continue;

                switch (v)
                {
                    case "8ai": case "a2vm":
                        DeviceDetect.DebugOverrideModel = DeviceDetect.Model.A2VM;
                        return;
                    case "8ex": case "ex": case "cg3em":
                        DeviceDetect.DebugOverrideModel = DeviceDetect.Model.Ex;
                        return;
                    case "unknown": case "none":
                        DeviceDetect.DebugOverrideModel = DeviceDetect.Model.Unknown;
                        return;
                }
            }
        }
    }
}
