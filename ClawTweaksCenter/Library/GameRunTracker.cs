using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Watches a just-launched game and reports back when it ends, so Center can restore itself the
    /// way Playnite does (see GamesEditor.Controllers_Stopped in the Playnite source, and
    /// GenericGameController's polling loop that feeds it).
    ///
    /// TWO tracking strategies, matching the two shapes GameLibrary.Launch can hand back:
    ///
    ///   1. A real Process handle (ROMs launched directly, Misc entries) - the easy case, an actual
    ///      OS wait, no polling at all.
    ///   2. Everything else (Steam, Epic, Xbox: the launch call returns Steam itself, the launcher, or
    ///      explorer, never the game) - Playnite's "Directory" tracking mode: poll for ANY running
    ///      process whose main module sits under the game's install folder. It has to wait for that
    ///      process to APPEAR first, because a store's own launcher takes a few seconds to hand off to
    ///      the real game.
    ///
    /// A game with neither (no process, no install directory - only possible for a Misc entry started
    /// through shell:appsFolder, since Misc deliberately carries no InstallDir) is not trackable at
    /// all. Center just stays minimized until the user brings it back by hand, which is the honest
    /// fallback rather than a fabricated "it ended" signal.
    /// </summary>
    public static class GameRunTracker
    {
        // Matches Playnite's own default (GenericGameController.StartTracking's trackingFrequency).
        private const int PollIntervalMs = 2000;

        // How long to wait for the store's launcher to hand off to a directory-matching process
        // before giving up on tracking this launch at all.
        private const int StartupTimeoutMs = 60_000;

        // A poll that took far longer than its interval means the machine was suspended mid-wait, not
        // that the game vanished between two checks - a handheld sleeps far more often mid-session
        // than a desktop Playnite normally runs on, so this matters more here, not less.
        private const int SuspendGuardMs = PollIntervalMs + 30_000;

        // A process we started that is gone again within this window is taken to be a launcher
        // stub, not the game: many tools start the real program and exit straight away.
        private const int StubWindowMs = 30_000;

        // How long to look for the program a stub handed off to. It has been started by the time
        // the stub exits, so this is a grace period, not a store hand-off.
        private const int StubHandOffMs = 6_000;

        /// <summary>
        /// Starts watching in the background. <paramref name="onEnded"/> runs on whatever thread the
        /// watch finishes on - callers that touch UI must marshal it themselves (see
        /// CenterMenuWindow.Library.cs, which posts it through Dispatcher.Invoke).
        /// </summary>
        public static void Track(GameEntry game, Process directProcess, CancellationToken ct, Action onEnded)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (directProcess != null)
                    {
                        var alive = Stopwatch.StartNew();
                        await directProcess.WaitForExitAsync(ct).ConfigureAwait(false);

                        // LAUNCHER STUBS. The process we started is gone after a few seconds, but
                        // the program it handed off to is running from the same folder. Watch that
                        // folder instead of calling the game ended.
                        string stubDir = ProgramFolderOf(game);
                        if (alive.ElapsedMilliseconds < StubWindowMs && stubDir != null
                            && await WaitForAppearAsync(stubDir, StubHandOffMs, ct).ConfigureAwait(false))
                        {
                            Core.InstallLog.Write("[RunTracker] '" + game?.Title + "' exited after "
                                + alive.ElapsedMilliseconds + " ms and left a program running in " + stubDir
                                + " - a launcher stub; watching the folder.");
                            await WaitForDisappearAsync(stubDir, ct).ConfigureAwait(false);
                        }
                    }
                    else if (!string.IsNullOrEmpty(game?.InstallDir))
                    {
                        if (!await WaitForAppearAsync(game.InstallDir, StartupTimeoutMs, ct).ConfigureAwait(false)) return;
                        await WaitForDisappearAsync(game.InstallDir, ct).ConfigureAwait(false);
                    }
                    else if (ProgramFolderOf(game) is string folder)
                    {
                        // A shortcut started through the shell hands back no process. Its target's
                        // folder is the next best thing - the same as the store case above.
                        if (!await WaitForAppearAsync(folder, StartupTimeoutMs, ct).ConfigureAwait(false)) return;
                        await WaitForDisappearAsync(folder, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        return; // nothing to watch - see the class doc comment
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { return; }

                if (!ct.IsCancellationRequested) onEnded?.Invoke();
            }, ct);
        }

        /// <summary>
        /// The folder of a Misc entry's program, when it is safe to watch - or null.
        ///
        /// Misc entries carry no InstallDir on purpose (see MiscStore.ToGameEntry), so this is the
        /// exe's own folder. A folder that other programs share - the Windows directory, a drive root,
        /// Program Files itself, the Desktop - would read any unrelated process as "still running"
        /// and keep Center hidden for good, so those are refused.
        /// </summary>
        private static string ProgramFolderOf(GameEntry game)
        {
            if (game == null || game.Store != GameStore.Misc || string.IsNullOrEmpty(game.ExePath)) return null;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))?.TrimEnd('\\');
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                if (string.Equals(Path.GetPathRoot(dir)?.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)) return null;

                foreach (var f in new[]
                {
                    Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                    Environment.SpecialFolder.SystemX86, Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                    Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolder.MyDocuments,
                })
                {
                    string shared = Environment.GetFolderPath(f)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(shared) && string.Equals(shared, dir, StringComparison.OrdinalIgnoreCase)) return null;
                }

                // Anything under the Windows directory is shared by definition.
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows)?.TrimEnd('\\');
                if (!string.IsNullOrEmpty(win) && dir.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                return dir;
            }
            catch { return null; }
        }

        private static async Task<bool> WaitForAppearAsync(string installDir, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (AnyProcessUnder(installDir)) return true;
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task WaitForDisappearAsync(string installDir, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!AnyProcessUnder(installDir)) return;

                var sw = Stopwatch.StartNew();
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                sw.Stop();
                // See SuspendGuardMs above - re-check rather than treat a long tick as "still running
                // but we happened not to notice it stop".
                if (sw.ElapsedMilliseconds > SuspendGuardMs) continue;
            }
        }

        /// <summary>Any currently running process whose image lives under the install folder.
        /// Mirrors Playnite's MonitorDirectory: it is the only strategy that works without a process
        /// handle, because it needs no cooperation from whatever actually launched the game.</summary>
        private static bool AnyProcessUnder(string installDir)
        {
            string root = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    string path = ImagePathOf(proc.Id);
                    if (!string.IsNullOrEmpty(path) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The image path through PROCESS_QUERY_LIMITED_INFORMATION, or null.
        ///
        /// NOT Process.MainModule: that needs PROCESS_VM_READ, which an unelevated Center is denied on
        /// an ELEVATED process. A program started through the UAC prompt (see MiscSource.Launch) would
        /// then be invisible here and read as "ended" the moment it started. The limited right is
        /// granted across the elevation boundary, and it is also far cheaper than enumerating modules.
        /// System processes still refuse it - not a match, not an error.
        /// </summary>
        private static string ImagePathOf(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
