using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Core
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
        /// Copies the helper's files out of the freshly installed package into the stable folder the
        /// scheduled task points at.
        ///
        /// Called after <see cref="StopHelpers"/> and the package install, so nothing is running and no
        /// file is locked. That ordering is the entire point: doing this from the helper's own startup
        /// means the deploying process and the process being replaced are the same one.
        ///
        /// Unelevated throughout — the source is the package folder (world-readable) and the target is
        /// the user's own LocalCache.
        /// </summary>
        public static bool DeployHelperFiles(Action<string> log = null)
        {
            try
            {
                string packageRoot = GetInstalledPackagePath(out string version);
                if (string.IsNullOrEmpty(packageRoot))
                {
                    log?.Invoke("Could not locate the installed package — helper files not deployed.");
                    return false;
                }

                string sourceDir = System.IO.Path.Combine(packageRoot, "XboxGamingBarHelper");
                if (!System.IO.Directory.Exists(sourceDir)) sourceDir = packageRoot;

                string helperFolder = Shared.Deployment.HelperFileDeployment.ResolveHelperFolder(PackageFamily);
                var result = Shared.Deployment.HelperFileDeployment.Deploy(sourceDir, helperFolder, version, log);
                if (!result.Success)
                    log?.Invoke("Helper files were not deployed — the widget will retry when it starts the helper.");
                return result.Success;
            }
            catch (Exception ex)
            {
                log?.Invoke("Deploying helper files failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Install location and version of the registered package, via PowerShell's Get-AppxPackage —
        /// the same route Center already uses to install, and one that needs no package identity of our
        /// own and no extra capability.
        /// </summary>
        private static string GetInstalledPackagePath(out string version)
        {
            version = null;
            try
            {
                string winPs = System.IO.Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = System.IO.File.Exists(winPs) ? winPs : "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                        + "\"$p = Get-AppxPackage -Name MSIClaw.ClawTweaks | Select-Object -First 1; "
                        + "if ($p) { $p.InstallLocation; $p.Version }\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var result = ProcessRunner.Run(psi, 30000);
                if (result == null || result.TimedOut) return null;

                var lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return null;
                version = lines[1].Trim();
                return lines[0].Trim();
            }
            catch { return null; }
        }

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

        /// <summary>Name of the file the helper refreshes every 2s while it is up.</summary>
        private const string HeartbeatFileName = "helper_heartbeat.json";

        /// <summary>
        /// How fresh the heartbeat must be for us to call the helper alive.
        ///
        /// KEPT IN STEP WITH THE HELPER'S OWN HeartbeatAliveWindowSeconds (Program.cs). Both sides
        /// must agree on what "alive" means: the helper uses this window to decide whether to yield
        /// to a running instance, and we use it to decide whether one needs starting. If the two ever
        /// disagree, the machine can reach a state where Center sees a helper the helper itself is
        /// about to displace - which is exactly the state measured on 2026-09-14, only with a much
        /// coarser test on our side.
        /// </summary>
        private const int HeartbeatAliveWindowSeconds = 15;

        /// <summary>
        /// True only when a helper is PROVABLY alive: a heartbeat that is fresh AND a process that
        /// still carries its pid.
        ///
        /// WHY NOT <see cref="HelperRunning"/>. That one asks whether a process with the helper's name
        /// exists, and MEASURED 2026-09-14, across two reboots, that is not the same question. At
        /// +21.9s a process named XboxGamingBarHelper was there; the helper that actually performed the
        /// controller mount started at +36.4s and its FIRST act was to ask that process to shut down.
        /// The heartbeat at that moment was 64s old - stale from before the reboot - so by the helper's
        /// own standard nothing was alive. Center had the weaker test and skipped the start it exists
        /// to make.
        ///
        /// FAILS TOWARD "NOT ALIVE" on every doubt - missing file, unreadable, unparsable, clock skew,
        /// dead pid. The cost of being wrong that way is one superfluous schtasks /Run, which the
        /// task's IgnoreNew policy absorbs. The cost of the other direction is the mount landing in the
        /// middle of the user's first navigation, which is the problem being fixed.
        /// </summary>
        public static bool HelperAlive(out string detail)
        {
            detail = "";
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", PackageFamily, "LocalState", HeartbeatFileName);

                if (!System.IO.File.Exists(path)) { detail = "no heartbeat file"; return false; }

                int pid;
                long timestamp;
                using (var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path)))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out pid))
                    { detail = "heartbeat has no pid"; return false; }
                    if (!root.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetInt64(out timestamp))
                    { detail = "heartbeat has no timestamp"; return false; }
                }

                long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
                if (age < 0 || age > HeartbeatAliveWindowSeconds)
                {
                    detail = $"heartbeat of PID={pid} is {age}s old (limit {HeartbeatAliveWindowSeconds}s)";
                    return false;
                }

                // A fresh heartbeat outlives a hard-killed helper by up to the window above, so the pid
                // has to be confirmed too - the same second check the helper makes before yielding.
                try { using (Process.GetProcessById(pid)) { } }
                catch { detail = $"heartbeat of PID={pid} is {age}s old but that process is gone"; return false; }

                detail = $"PID={pid}, heartbeat {age}s old";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"heartbeat read threw: {ex.Message}";
                return false;
            }
        }

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

        /// <summary>
        /// What a task query could establish. The third case is the point of this type.
        ///
        /// MEASURED 2026-09-14, 13:14:42, 25s into a boot: schtasks /Query did not finish inside the
        /// timeout while the task not only existed but was RUNNING - it had started the helper nine
        /// seconds earlier. The old code did `WaitForExit(5000); return p.ExitCode == 0;`, and since
        /// WaitForExit had not actually waited for the exit, reading ExitCode THREW, the catch turned
        /// that into false, and a timeout became indistinguishable from "no such task". The guard that
        /// releases the whole FSE start path said no, for a task that was live.
        /// </summary>
        public enum TaskPresence
        {
            /// <summary>schtasks answered and the task is there.</summary>
            Present,
            /// <summary>schtasks answered and the task is not there.</summary>
            Absent,
            /// <summary>No answer in time, or the query could not be run. Nothing is known.</summary>
            Unknown
        }

        /// <summary>
        /// Timeout for a schtasks call. Generous ON PURPOSE: the caller that matters runs seconds into
        /// a boot, against a disk that is serving the logon storm, and there a process start alone
        /// outlasted the old 5s.
        /// </summary>
        private const int SchtasksTimeoutMs = 20000;

        /// <summary>
        /// Asks whether the helper's scheduled task is registered, and says honestly when it could not
        /// find out. <paramref name="detail"/> is meant for a log line, not for the user.
        /// </summary>
        public static TaskPresence QueryScheduledTask(out string detail)
        {
            detail = "";
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
                var process = ProcessRunner.Run(psi, SchtasksTimeoutMs);
                if (process == null) { detail = "schtasks did not start"; return TaskPresence.Unknown; }

                if (process.TimedOut)
                {
                    detail = $"schtasks /Query did not answer within {SchtasksTimeoutMs}ms";
                    return TaskPresence.Unknown;
                }

                int code = process.ExitCode;
                detail = $"schtasks /Query exit {code}";
                return code == 0 ? TaskPresence.Present : TaskPresence.Absent;
            }
            catch (Exception ex)
            {
                detail = $"schtasks /Query threw: {ex.Message}";
                return TaskPresence.Unknown;
            }
        }

        /// <summary>
        /// True if the helper's scheduled task is registered.
        ///
        /// An inconclusive query reads as "not registered" here, which is right for the one caller
        /// left on this overload: it only decides whether to tell the user a UAC prompt is coming, and
        /// an unnecessary heads-up costs nothing. Anything that ACTS on the answer should use
        /// <see cref="QueryScheduledTask"/> and decide for itself what to do with Unknown.
        /// </summary>
        public static bool ScheduledTaskExists() => QueryScheduledTask(out _) == TaskPresence.Present;

        /// <summary>Runs the helper's scheduled task if it exists. Returns true if launched.</summary>
        public static bool RunScheduledTask() => RunScheduledTask(out _);

        /// <summary>
        /// Starts the helper's scheduled task. <paramref name="detail"/> carries schtasks' own verdict
        /// so a caller can log WHY a start did not happen.
        ///
        /// NO PRE-CHECK any more. It used to call ScheduledTaskExists() first, which doubled the
        /// schtasks processes at the worst possible moment - seconds into a boot - to learn something
        /// /Run reports by itself: a missing task comes back as a non-zero exit. The pre-check could
        /// also veto a perfectly good task on nothing but its own timeout (see QueryScheduledTask).
        ///
        /// A zero exit means the REQUEST was accepted, not that a helper started. The task is
        /// IgnoreNew, so a request arriving while an instance is already running is refused by the
        /// scheduler afterwards - visible only as the task's LastTaskResult 0x800710E0, never here.
        /// </summary>
        public static bool RunScheduledTask(out string detail)
        {
            detail = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Run /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var process = ProcessRunner.Run(psi, SchtasksTimeoutMs);
                if (process == null) { detail = "schtasks did not start"; return false; }

                if (process.TimedOut)
                {
                    detail = $"schtasks /Run did not answer within {SchtasksTimeoutMs}ms";
                    return false;
                }

                int code = process.ExitCode;
                detail = $"schtasks /Run exit {code}";
                return code == 0;
            }
            catch (Exception ex)
            {
                detail = $"schtasks /Run threw: {ex.Message}";
                return false;
            }
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
