using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Helper orchestration around an install: query/run the helper's scheduled task, stop a running
    /// helper, and kick the Game Bar so the widget (re)deploys + starts the helper.
    ///
    /// Everything here runs UNELEVATED — Center no longer elevates itself at all. That is not a
    /// limitation for any of it: schtasks /Query and /Run work per-user, stopping a helper goes through
    /// the shared handover (the helper exits itself, so no cross-integrity Kill is needed), and reading
    /// another process's elevation state only needs PROCESS_QUERY_LIMITED_INFORMATION. Anything added
    /// here must hold to that — an API that quietly needs PROCESS_ALL_ACCESS fails on EVERY call rather
    /// than obviously once, which is exactly how IsProcessElevated silently broke.
    ///
    /// We deliberately do NOT create the scheduled task or copy the helper exe from here. The helper
    /// does that itself, in three steps (rebuilt 2026-07-29 after KB5101684 broke elevating an exe
    /// under WindowsApps): the unelevated MSIX helper deploys the payload to LocalCache, then elevates
    /// only the DEPLOYED copy with --setup-task-only to register the scheduled task (that is the one
    /// UAC), and afterwards starts the task without any further prompt. An earlier comment here
    /// described a single "--setup" argument doing all of it - that argument no longer exists.
    /// Keeping the persistence in that compiled, signed path is far less likely to trip Defender's
    /// persistence ML than a setup writing an exe + task, which is the same reason Install.ps1 stopped
    /// doing script-driven persistence.
    ///
    /// We also do NOT touch the scheduled task itself: it carries no version (it hangs off the package
    /// family), and updates staying UAC-free depends on it surviving.
    /// </summary>
    public static class HelperControl
    {
        private const string TaskName = @"ClawTweaks\ClawTweaksHelper";
        private const string HelperProcess = "XboxGamingBarHelper";

        /// <summary>Package family the handover files sit under (LocalCache\Local).</summary>
        private const string PackageFamily = "MSIClaw.ClawTweaks_7eszav2039cvc";

        /// <summary>
        /// Stops every running helper BEFORE a package install, the polite way: ask via the shared
        /// handover protocol, fall back to a kill only for what does not answer.
        ///
        /// Why before and not after: a helper that survives the package swap owns MSI WMI/EC, the
        /// HidHide/ViGEm mounts and the single-instance mutex while the new build comes up.
        /// Add-AppxPackage's -ForceApplicationShutdown does not reach it - the deployed helper is a
        /// plain exe outside the package, not an app-lifecycle process. Center used to handle this
        /// after installing, with its own five-second grace period and its own kill loop; that policy
        /// is gone in favour of the one protocol the helper and Install.ps1 already use.
        ///
        /// Returns (handedOver, killed) so the caller can report which route was taken.
        /// </summary>
        public static (int handedOver, int killed) StopHelpers(string reason, Action<string> log = null)
        {
            string folder = Shared.IPC.HelperHandover.ResolveFolder(PackageFamily);
            int handedOver = 0, killed = 0;

            foreach (var p in Process.GetProcessesByName(HelperProcess))
            {
                try
                {
                    if (Shared.IPC.HelperHandover.TryOrderlyShutdown(folder, p, reason, log))
                    {
                        handedOver++;
                        continue;
                    }

                    log?.Invoke($"Helper PID={p.Id} did not hand over - stopping it directly.");
                    p.Kill();
                    if (p.WaitForExit(5000)) killed++;
                    else log?.Invoke($"Helper PID={p.Id} did not exit within 5s.");
                }
                catch (Exception ex)
                {
                    // Already gone, or a higher-integrity instance we cannot touch - best effort.
                    log?.Invoke($"Helper PID={p.Id}: {ex.Message}");
                }
                finally { try { p.Dispose(); } catch { } }
            }

            if (handedOver > 0 || killed > 0)
            {
                // Let the kernel tear down the freed process's driver handles and MMIO maps before the
                // replacement opens them.
                try { System.Threading.Thread.Sleep(1000); } catch { }
                log?.Invoke($"Helpers stopped: {handedOver} handed over, {killed} terminated.");
            }

            return (handedOver, killed);
        }

        public static int HelperCount() => Process.GetProcessesByName(HelperProcess).Length;
        public static bool HelperRunning() => HelperCount() > 0;

        /// <summary>PIDs of currently running helper processes — a stale instance from before an
        /// update lingers (Add-AppxPackage's -ForceApplicationShutdown doesn't reach it, it's a plain
        /// exe, not an app-lifecycle-managed process), so "any helper running" alone is a false
        /// positive for "the fresh post-install helper came up". Snapshot this before triggering a
        /// (re)install and only treat a PID outside the snapshot as the real signal.</summary>
        public static int[] GetHelperPids() => Process.GetProcessesByName(HelperProcess).Select(p => p.Id).ToArray();

        /// <summary>Best-effort: true while a UAC elevation prompt is up (e.g. the helper elevating its
        /// deployed copy to register the scheduled task). Lets a caller tell the user to confirm it.</summary>
        public static bool IsUacPromptShowing() => Process.GetProcessesByName("consent").Length > 0;

        /// <summary>
        /// True if the given process is actually running elevated (High/System integrity), checked
        /// via its token's TokenElevation — NOT just "a process with this name exists". A new
        /// XboxGamingBarHelper PID can appear before the elevation request is even shown, let alone
        /// confirmed - the unelevated MSIX helper deploys the payload first and only then elevates the
        /// deployed copy to register the task - so "PID exists" alone is not proof the UAC was
        /// confirmed. This is the actual, verifiable signal instead of guessing from timing.
        ///
        /// Opens the process with PROCESS_QUERY_LIMITED_INFORMATION, deliberately NOT via
        /// Process.Handle. Process.Handle asks for PROCESS_ALL_ACCESS, which a Medium-integrity process
        /// can never get on a High-integrity one: since Center stopped elevating itself, that threw
        /// "Access denied" for EVERY helper and this method always returned false. The visible symptom
        /// was the post-install monitor sitting on "Waiting for the ClawTweaks helper to start" until
        /// its 60s timeout even though the helper had been up for ages. Measured unelevated against a
        /// live elevated helper (2026-07-30): Process.Handle -> Access denied,
        /// PROCESS_QUERY_LIMITED_INFORMATION -> elevated=True. That right exists precisely to be
        /// grantable across integrity levels for a same-user process, and TOKEN_QUERY on the token it
        /// yields is enough for TokenElevation.
        /// </summary>
        public static bool IsProcessElevated(int pid)
        {
            IntPtr procHandle = IntPtr.Zero;
            IntPtr tokenHandle = IntPtr.Zero;
            IntPtr tokenInfo = IntPtr.Zero;
            try
            {
                procHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (procHandle == IntPtr.Zero) return false;

                if (!OpenProcessToken(procHandle, TOKEN_QUERY, out tokenHandle)) return false;

                tokenInfo = Marshal.AllocHGlobal(sizeof(int));
                if (!GetTokenInformation(tokenHandle, TokenElevation, tokenInfo, sizeof(int), out _)) return false;

                return Marshal.ReadInt32(tokenInfo) != 0;
            }
            catch { return false; }
            finally
            {
                if (tokenInfo != IntPtr.Zero) Marshal.FreeHGlobal(tokenInfo);
                if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
                if (procHandle != IntPtr.Zero) CloseHandle(procHandle);
            }
        }

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True if the helper's scheduled task is registered.</summary>
        public static bool ScheduledTaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>Runs the helper's scheduled task if it exists. Returns true if launched.</summary>
        public static bool RunScheduledTask()
        {
            if (!ScheduledTaskExists()) return false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Run /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                return p != null && p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>Best-effort: open the Xbox Game Bar so the widget loads and deploys the helper.</summary>
        public static bool OpenGameBar()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-gamingoverlay://",
                    UseShellExecute = true,
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>Polls until the helper process appears or the timeout elapses.</summary>
        public static async Task<bool> WaitForHelperAsync(int timeoutMs, IProgress<int> percent = null)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (HelperRunning()) { percent?.Report(100); return true; }
                percent?.Report((int)Math.Min(99, sw.ElapsedMilliseconds * 100 / timeoutMs));
                await Task.Delay(500);
            }
            return HelperRunning();
        }

        /// <summary>Best-effort: closes the Game Bar overlay again by simulating Win+G a second time —
        /// there's no dedicated "close" URI, Win+G is a toggle. Used right after OpenGameBar() so the
        /// underlying window is visible again quickly instead of staying covered by the overlay.</summary>
        public static void CloseGameBarBestEffort()
        {
            try
            {
                const byte VK_LWIN = 0x5B, VK_G = 0x47;
                const uint KEYEVENTF_KEYUP = 0x0002;
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_G, 0, 0, UIntPtr.Zero);
                keybd_event(VK_G, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
