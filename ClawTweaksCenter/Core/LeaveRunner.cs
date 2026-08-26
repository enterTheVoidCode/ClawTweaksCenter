using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Enums;

namespace ClawTweaksCenter.Core
{
    public enum LeaveStepState { Pending, Working, Ok, Warning, Error, Blocked }

    public sealed class LeaveStep
    {
        public string Title;
        public string What;                       // one line: what pressing A does
        public LeaveStepState State = LeaveStepState.Pending;
        public string Detail = "";
        public bool Actionable = true;
        /// <summary>Asks before running. The two removals do; the restores do not.</summary>
        public bool NeedsConfirm;
    }

    /// <summary>
    /// "Uninstall ClawTweaks" — the way out, run as an ordered list the user steps through.
    ///
    /// THE ORDER IS NOT COSMETIC, and getting it wrong makes two steps impossible rather than ugly:
    ///
    ///   0 Restore the device      — charge limit, fan and controller are HARDWARE state the helper
    ///                               owns. Nothing else can put them back, so this has to happen
    ///                               while ClawTweaks is still installed and its helper is answering.
    ///   1 Turn MSI Center M on    — also over the helper pipe, for the same reason. After step 2
    ///                               there is no pipe left to ask.
    ///   2 Uninstall ClawTweaks    — removing the package is what makes the helper clean up after
    ///                               itself: it notices the package is gone, deletes its scheduled
    ///                               task and its deployed copy, and exits (Program.PackageLifecycle
    ///                               PerformUninstallCleanupAndExit). Doing this FIRST would leave a
    ///                               device with our fan curve and charge limit still applied and no
    ///                               software left that could undo them.
    ///   3 Uninstall Center        — last, because it ends this process.
    ///
    /// Steps 0-2 need ClawTweaks installed; step 3 never does. That asymmetry is deliberate and is
    /// the point of <see cref="CanRestore"/>: someone who already removed ClawTweaks must still be
    /// able to remove Center, with a warning, rather than be trapped in a wizard that cannot finish.
    /// </summary>
    public sealed class LeaveRunner
    {
        public const int StepRestore = 0;
        public const int StepCenterM = 1;
        public const int StepRemoveApp = 2;
        public const int StepRemoveCenter = 3;

        public HelperPipeClient PipeClient { get; }

        public LeaveRunner(HelperPipeClient sharedClient = null)
        {
            PipeClient = sharedClient ?? new HelperPipeClient();
        }

        public IReadOnlyList<LeaveStep> Steps { get; } = new List<LeaveStep>
        {
            new LeaveStep
            {
                Title = "Restore the device",
                What  = "Puts the charge limit, the fan and the controller back.",
                NeedsConfirm = true,
            },
            new LeaveStep
            {
                Title = "Turn MSI Center M back on",
                What  = "Re-enables MSI's tasks, service and Game Bar widget.",
            },
            new LeaveStep
            {
                Title = "Uninstall ClawTweaks",
                What  = "Removes the app, its background helper and its scheduled task.",
                NeedsConfirm = true,
            },
            new LeaveStep
            {
                Title = "Uninstall Center",
                What  = "Removes this app and closes it.",
                NeedsConfirm = true,
            },
        };

        public event Action StepsChanged;
        private void Notify() => StepsChanged?.Invoke();

        // ── Gating ───────────────────────────────────────────────────────────────────────────

        /// <summary>ClawTweaks is installed (set by the window from its own version check).</summary>
        public bool ClawTweaksInstalled { get; set; }

        /// <summary>True once the helper has answered on the pipe at least once this session.</summary>
        public bool HelperHealthy => PipeClient.IsConnected;

        /// <summary>The three device restores and the Center M switch can only run through the
        /// helper, and the helper only exists while ClawTweaks is installed.</summary>
        public bool CanRestore => ClawTweaksInstalled && HelperHealthy;

        public bool IsConnecting { get; private set; }

        /// <summary>Connects to the helper so the gating above reflects reality rather than a
        /// guess. Safe to call repeatedly; the window calls it on entry and on Ⓨ.</summary>
        public async Task RefreshAsync(Action<string> log = null)
        {
            if (IsConnecting) return;
            IsConnecting = true;
            Recompute();
            try
            {
                if (!PipeClient.IsConnected && ClawTweaksInstalled)
                    await PipeClient.ConnectAsync(TimeSpan.FromSeconds(20), log).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                IsConnecting = false;
                Recompute();
            }
        }

        /// <summary>
        /// Rewrites every step's state from the live gating. Always recomputed in full, never
        /// patched incrementally — calling it once too often is then free, and calling it once too
        /// rarely is the only way to be wrong.
        /// </summary>
        public void Recompute()
        {
            foreach (var s in Steps)
                if (s.State == LeaveStepState.Working) { Notify(); return; }   // never stomp an in-flight step

            for (int i = 0; i < Steps.Count; i++)
            {
                var s = Steps[i];
                if (s.State == LeaveStepState.Ok || s.State == LeaveStepState.Warning) continue;

                if (i == StepRemoveCenter) { s.State = LeaveStepState.Pending; s.Actionable = true; s.Detail = ""; continue; }

                if (!ClawTweaksInstalled)
                {
                    s.State = LeaveStepState.Blocked;
                    s.Actionable = false;
                    s.Detail = "ClawTweaks is not installed.";
                }
                else if (!HelperHealthy)
                {
                    s.State = LeaveStepState.Blocked;
                    s.Actionable = false;
                    s.Detail = IsConnecting ? "Connecting to the helper…" : "The helper is not answering.";
                }
                else
                {
                    s.State = LeaveStepState.Pending;
                    s.Actionable = true;
                    s.Detail = "";
                }
            }
            Notify();
        }

        // ── Step 0: restore the device ───────────────────────────────────────────────────────

        /// <summary>
        /// Puts the machine back the way ClawTweaks found it.
        ///
        /// ORDER INSIDE THE STEP, and it is the mirror of the step order above: the full reset runs
        /// FIRST because it wipes the helper's settings store, and only then are the three hardware
        /// values written. The other way round, the reset would erase the "charge limit off" and
        /// "fan on firmware" we had just persisted, and the next helper start would re-apply whatever
        /// had been stored before. Wipe the settings, then put the hardware back.
        ///
        /// Only two of the four have a confirmation to wait for (the reset replies with a result, the
        /// controller mode echoes back). The charge limit and the fan are acknowledged but not
        /// readable from here, so they are reported as sent, not as verified — which is the honest
        /// word for what we know.
        /// </summary>
        public async Task RestoreDeviceAsync()
        {
            var step = Steps[StepRestore];
            step.State = LeaveStepState.Working; step.Detail = "Resetting all ClawTweaks settings…"; Notify();

            var reset = await new MaintenanceRunner(PipeClient).ResetAsync().ConfigureAwait(false);
            if (!reset.Ok)
            {
                step.State = LeaveStepState.Error;
                step.Detail = reset.Error ?? "The reset did not finish.";
                Notify();
                return;
            }

            step.Detail = "Restoring the hardware…"; Notify();

            // Charge limit off. "false:90" — the percentage is irrelevant when disabled, and 90 is
            // the helper's own default for a value it has never been told.
            bool chargeSent = PipeClient.SendRequest("MsiChargeLimit", "false:90");

            // Fan back to firmware Auto. -1 is the helper's "disabled → firmware control" value: it
            // runs MSI's clean hand-back (ApplyFirmwareAutoBaseline) rather than writing a curve.
            bool fanSent = PipeClient.SendRequest("MsiFanControl", -1);

            // Hardware controller. DefaultControllerMode is the value the helper persists and
            // everything else derives from, so it is the one to drive.
            bool controller = await PipeClient.SetAndWaitForConfirmationAsync(
                Function.DefaultControllerMode, 0, "0", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var parts = new List<string>();
            parts.Add("Settings reset.");
            parts.Add(controller ? "Hardware controller confirmed." : "The controller did not confirm — check it before you unplug.");
            parts.Add(chargeSent && fanSent
                ? "Charge limit off and fan handed back to firmware."
                : "Some hardware commands could not be sent.");
            if (!string.IsNullOrEmpty(reset.Path)) parts.Add("A backup of your settings is at " + reset.Path);

            step.State = controller && chargeSent && fanSent ? LeaveStepState.Ok : LeaveStepState.Warning;
            step.Detail = string.Join(" ", parts);
            step.Actionable = true;     // re-runnable: it costs nothing and a failed restore has to be retryable
            Notify();
        }

        // ── Step 1: MSI Center M ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Hands the device back to MSI's own software, then CHECKS what actually came back.
        ///
        /// The check exists because the answer is routinely "not all of it". Disabling Center M
        /// removes its Game Bar widget package, and that removal takes the staged copy Windows would
        /// re-register from — so the tasks and the service return and the widget does not. Reporting
        /// that plainly is the difference between a user who reinstalls MSI Center M and one who
        /// thinks ClawTweaks broke their machine.
        /// </summary>
        public async Task ReenableCenterMAsync()
        {
            var step = Steps[StepCenterM];

            if (!CenterM.IsInstalled())
            {
                step.State = LeaveStepState.Warning;
                step.Detail = "MSI Center M is not installed on this device.";
                step.Actionable = false;
                Notify();
                return;
            }

            step.State = LeaveStepState.Working; step.Detail = "Turning MSI Center M back on…"; Notify();

            bool ok = await PipeClient.SetAndWaitForConfirmationAsync(
                Function.MsiCenterActive, true, "True", TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            if (!ok)
            {
                step.State = LeaveStepState.Error;
                step.Detail = "The helper did not confirm in time.";
                Notify();
                return;
            }

            // Give the re-registration a moment before asking whether it landed.
            await Task.Delay(2000).ConfigureAwait(false);

            if (CenterM.IsGameBarWidgetInstalled())
            {
                step.State = LeaveStepState.Ok;
                step.Detail = "MSI Center M and its Game Bar widget are back.";
                step.Actionable = false;
            }
            else
            {
                step.State = LeaveStepState.Warning;
                step.Detail = "MSI Center M is back, but its Game Bar widget could not be restored. " +
                              "Reinstall MSI Center M to get the widget back.";
                step.Actionable = true;
            }
            Notify();
        }

        // ── Step 2: remove ClawTweaks ────────────────────────────────────────────────────────

        /// <summary>
        /// Removes the ClawTweaks package. The helper is deliberately NOT killed first: it watches
        /// for its own package disappearing and uses that to remove its scheduled task and its
        /// deployed copy before exiting. Killing it would leave both behind.
        /// </summary>
        public async Task RemoveClawTweaksAsync(Action<string> log = null)
        {
            var step = Steps[StepRemoveApp];
            step.State = LeaveStepState.Working; step.Detail = "Removing ClawTweaks…"; Notify();

            bool removed = await Task.Run(() => PackageInstaller.RemoveClawTweaks(log)).ConfigureAwait(false);

            if (removed)
            {
                ClawTweaksInstalled = false;
                step.State = LeaveStepState.Ok;
                step.Detail = "ClawTweaks is removed. Its helper clears its own scheduled task and exits — " +
                              "give it a minute.";
                step.Actionable = false;
            }
            else
            {
                step.State = LeaveStepState.Error;
                step.Detail = "The package could not be removed. Close the Game Bar and try again, " +
                              "or remove ClawTweaks from Settings → Apps.";
            }

            // The two steps above this one just lost their prerequisite. Recompute skips anything
            // already Ok or Warning, so this cannot undo a result the user has already seen - it only
            // moves the steps that can no longer run into Blocked, with the reason on them.
            Recompute();
        }
    }
}
