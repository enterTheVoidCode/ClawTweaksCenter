using System;
using System.Diagnostics;
using System.IO;
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
                        await directProcess.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    else if (!string.IsNullOrEmpty(game?.InstallDir))
                    {
                        if (!await WaitForAppearAsync(game.InstallDir, ct).ConfigureAwait(false)) return;
                        await WaitForDisappearAsync(game.InstallDir, ct).ConfigureAwait(false);
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

        private static async Task<bool> WaitForAppearAsync(string installDir, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(StartupTimeoutMs);
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

        /// <summary>Any currently running process whose main module lives under the install folder.
        /// Mirrors Playnite's MonitorDirectory: it is the only strategy that works without a process
        /// handle, because it needs no cooperation from whatever actually launched the game.</summary>
        private static bool AnyProcessUnder(string installDir)
        {
            string root = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        string path = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    // A system/elevated process denies MainModule to an unelevated reader - not a
                    // match, not an error. Center never elevates (see CenterSettings and the CLAUDE.md
                    // rule this repo is built under), so this is the expected, normal outcome for most
                    // of what Process.GetProcesses() returns.
                    catch { }
                }
            }
            return false;
        }
    }
}
