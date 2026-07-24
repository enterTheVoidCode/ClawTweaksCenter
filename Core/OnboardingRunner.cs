using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Enums;

namespace ClawTweaksSetup.Core
{
    public enum OnboardingStepState { Unknown, Pending, Working, Ok, Error }

    public sealed class OnboardingStep
    {
        public string Title;
        public OnboardingStepState State = OnboardingStepState.Unknown;
        public string Detail = "";
        /// <summary>False while the helper hasn't confirmed the target isn't already satisfied —
        /// the UI greys the run button out instead of guessing.</summary>
        public bool Actionable = false;
    }

    /// <summary>
    /// The onboarding steps, run as a top-to-bottom DEPENDENCY CHAIN — each later step only unlocks once
    /// its prerequisite is genuinely satisfied, and an upstream step greys out once a downstream target is
    /// already active (e.g. the HW-controller check is moot once the virtual controller is running):
    ///   0 HW controller health   — can ClawTweaks open + drive the physical Claw HID (helper-side probe,
    ///                               catches the rare "HID held by another process" state)? Gates the rest.
    ///   1 Disable MSI Center M    — only once the controller is healthy (and Center M is still active).
    ///   2 Enable virtual controller — only once Center M is off; enables, waits, then RE-DIAGNOSES the
    ///                               virtual pad and ROLLS BACK to the hardware controller on failure.
    ///   3 Set Game Bar position   — place ClawTweaks at slot 3 (after the fixed MS widgets).
    ///   4 Activate auto-jump      — feed the known position to the helper (RB-hop nav), needs step 3.
    /// Every write/read goes through HelperPipeClient, which speaks the exact same wire protocol as the
    /// widget over the helper's second ("ClawTweaksCenter") pipe — no helper logic is duplicated.
    /// </summary>
    public sealed class OnboardingRunner
    {
        // Match the widget/helper semantics: 1 = default = auto-jump off. The real value is read back from
        // the helper (Function.GameBarWidgetPosition) and applied on first arrival — this is only the value
        // shown until then. (Was 3, a hardcoded guess that ignored the helper and always showed "3".)
        private const int AutoJumpPositionDefault = 1;

        /// <summary>The Game Bar slot the user says ClawTweaks sits at (1-based). The exact position is
        /// not readable (see RE_GameBar_WidgetBar_Order.md), so the user enters/confirms it in the auto-
        /// jump step; the helper taps RB (value − 1) times to hop onto it. Default 3.</summary>
        public int AutoJumpPositionValue { get; set; } = AutoJumpPositionDefault;

        public const int StepHwHealth = 0;
        public const int StepCenterM = 1;
        public const int StepVirtualController = 2;
        public const int StepAddToBar = 3;
        public const int StepAutoJump = 4;

        public HelperPipeClient PipeClient { get; }

        public IReadOnlyList<OnboardingStep> Steps { get; } = new List<OnboardingStep>
        {
            new OnboardingStep { Title = "Check hardware controller health" },
            new OnboardingStep { Title = "Disable MSI Center M" },
            new OnboardingStep { Title = "Enable virtual controller" },
            new OnboardingStep { Title = "Add ClawTweaks to the Game Bar" },
            new OnboardingStep { Title = "Activate Game Bar auto-jump" },
        };

        public event Action StepsChanged;
        public bool IsConnecting { get; private set; }
        public bool IsConnected => PipeClient.IsConnected;

        // ── Live state that drives the dependency-chain gating ───────────────────────────────
        private bool? _centerMRunning;      // from MsiCenterActive pushes (null until known)
        private bool? _controllerEnabled;   // from ControllerEmulationEnabled pushes (null until known)
        private bool _hwHealthy;            // last HW-health probe verdict == ok (or virtual already active)
        private bool _hwProbedThisSession;  // the probe actually ran (so we don't grey-check step 0 prematurely)
        private bool _verifiedThisSession;  // virtual pad confirmed present this session
        private bool? _favorited;           // from GameBarWidgetFavorited pushes — is CTW in the Game Bar
                                            // home bar? null until the widget reports (it only runs when
                                            // Game Bar has activated it). See RE_GameBar_WidgetBar_Order.md.
        private int? _autoJumpStoredPos;    // persisted Game Bar slot from the helper (1-based); >1 = the
                                            // user already configured auto-jump → step auto-completes.
        private bool _autoJumpPosApplied;   // the helper's slot has been reflected into the stepper once
        private bool _settling;             // a background status-settle loop is already running

        private void Notify() => StepsChanged?.Invoke();

        /// <summary>The Center runs a SINGLE shared HelperPipeClient (the helper's ClawTweaksCenter pipe
        /// accepts only one instance — see NamedPipeServer maxNumberOfServerInstances). Onboarding and
        /// maintenance must therefore reuse the same connection; pass the shared client in. A null client
        /// (standalone/tests) falls back to a private one.</summary>
        public OnboardingRunner(HelperPipeClient sharedClient = null)
        {
            PipeClient = sharedClient ?? new HelperPipeClient();
            PipeClient.PropertyUpdated += (function, content) =>
            {
                bool value = string.Equals(content, "True", StringComparison.OrdinalIgnoreCase);
                if (function == Function.MsiCenterActive) { _centerMRunning = value; RecomputeGating(); }
                else if (function == Function.ControllerEmulationEnabled) { _controllerEnabled = value; RecomputeGating(); }
                else if (function == Function.GameBarWidgetFavorited) { _favorited = value; RecomputeGating(); }
                else if (function == Function.GameBarWidgetPosition)
                {
                    if (int.TryParse(content, out var p) && p >= 1 && p <= 10)
                    {
                        _autoJumpStoredPos = p;
                        // Reflect the helper's real slot in the stepper the first time we learn it — don't
                        // clobber a later manual edit the user makes in the stepper.
                        if (!_autoJumpPosApplied) { AutoJumpPositionValue = p; _autoJumpPosApplied = true; }
                    }
                    RecomputeGating();
                }
            };
        }

        /// <summary>Whether the physical controller is fit to take over: the probe said "ok", OR the
        /// virtual controller is already running (proof the takeover already happened).</summary>
        private bool HwOk => _hwHealthy || _controllerEnabled == true;

        /// <summary>
        /// Recomputes each step's State/Detail/Actionable from the live flags so the chain unlocks
        /// top-to-bottom and upstream steps grey out once downstream targets are already satisfied.
        /// Only touches steps that are NOT mid-run (Working) so it never stomps an in-flight action.
        /// </summary>
        private void RecomputeGating()
        {
            // Step 0 — HW controller health.
            var hw = Steps[StepHwHealth];
            if (hw.State != OnboardingStepState.Working)
            {
                if (_controllerEnabled == true)
                {
                    // Virtual controller already running → the physical takeover clearly works. Greyed OK.
                    hw.State = OnboardingStepState.Ok; hw.Actionable = false;
                    hw.Detail = "Virtual controller already active — controller is healthy.";
                }
                else if (_hwProbedThisSession)
                {
                    hw.State = _hwHealthy ? OnboardingStepState.Ok : OnboardingStepState.Error;
                    hw.Actionable = true; // always re-runnable
                }
                else
                {
                    hw.State = OnboardingStepState.Pending; hw.Actionable = true;
                    if (string.IsNullOrEmpty(hw.Detail)) hw.Detail = "Not checked yet.";
                }
            }

            // Step 1 — Disable MSI Center M. Gated on a healthy controller.
            var cm = Steps[StepCenterM];
            if (cm.State != OnboardingStepState.Working)
            {
                if (_centerMRunning == false)
                {
                    cm.State = OnboardingStepState.Ok; cm.Actionable = false; cm.Detail = "Already disabled.";
                }
                else if (!HwOk)
                {
                    cm.State = OnboardingStepState.Pending; cm.Actionable = false;
                    cm.Detail = "Check the controller first.";
                }
                else if (_centerMRunning == true)
                {
                    cm.State = OnboardingStepState.Pending; cm.Actionable = true; cm.Detail = "Currently running.";
                }
                else
                {
                    cm.State = OnboardingStepState.Pending; cm.Actionable = false; cm.Detail = "Checking…";
                }
            }

            // Step 2 — Enable virtual controller. Gated on Center M off AND controller healthy.
            var vc = Steps[StepVirtualController];
            if (vc.State != OnboardingStepState.Working)
            {
                if (_controllerEnabled == true)
                {
                    vc.State = OnboardingStepState.Ok; vc.Actionable = false;
                    if (!_verifiedThisSession) vc.Detail = "Already enabled.";
                }
                else if (_centerMRunning == true)
                {
                    vc.State = OnboardingStepState.Pending; vc.Actionable = false;
                    vc.Detail = "Disable MSI Center M first.";
                }
                else if (!HwOk)
                {
                    vc.State = OnboardingStepState.Pending; vc.Actionable = false;
                    vc.Detail = "Check the controller first.";
                }
                else
                {
                    vc.State = OnboardingStepState.Pending; vc.Actionable = true; vc.Detail = "Currently disabled.";
                }
            }

            // Step 3 — Add ClawTweaks to the Game Bar. The user favorites CTW in the Game Bar; the Run
            // button re-CHECKS presence via the widget's Favorited state (the only reliable signal — the
            // Game Bar profiles don't persist bar membership; see RE_GameBar_WidgetBar_Order.md). It also
            // auto-completes on the live FavoritedChanged push, so the button is a manual fallback.
            var bar = Steps[StepAddToBar];
            if (bar.State != OnboardingStepState.Working)
            {
                bool ready = _verifiedThisSession || _controllerEnabled == true;
                if (!ready)
                {
                    bar.State = OnboardingStepState.Pending; bar.Actionable = false;
                    bar.Detail = "Enable the virtual controller first.";
                }
                else if (_favorited == true)
                {
                    bar.State = OnboardingStepState.Ok; bar.Actionable = false;
                    bar.Detail = "ClawTweaks is in your Game Bar.";
                }
                else
                {
                    // Ready, but CTW isn't (yet) reported as favorited — offer a manual re-check button.
                    bar.State = OnboardingStepState.Pending; bar.Actionable = true;
                    bar.Detail = _favorited == false
                        ? "Not in the bar yet — favorite ClawTweaks in the Game Bar (Win+G), then Check."
                        : "Open the Game Bar (Win+G), favorite ClawTweaks, then Check.";
                }
            }

            // Step 4 — Activate auto-jump. Needs CTW actually in the bar (step 3). The user enters the
            // slot number in the UI (AutoJumpPositionValue); the helper taps RB (value − 1) times to hop
            // onto it. The exact position is not readable, so the user confirms it.
            var aj = Steps[StepAutoJump];
            if (aj.State != OnboardingStepState.Working)
            {
                bool present = _favorited == true;
                // A stored position > 1 means the user has already configured auto-jump (helper persists
                // it), so complete the step automatically — position 1 = default/off = still to do. Keep it
                // re-runnable so the slot can still be changed.
                if (present && aj.State != OnboardingStepState.Ok && _autoJumpStoredPos is int sp && sp > 1)
                {
                    aj.State = OnboardingStepState.Ok;
                    aj.Actionable = true;
                    aj.Detail = $"Auto-jump active (position {sp}).";
                }
                else
                {
                    aj.Actionable = present && aj.State != OnboardingStepState.Ok;
                    if (!present) aj.Detail = "Add ClawTweaks to the Game Bar first.";
                    else if (aj.State != OnboardingStepState.Ok) aj.Detail = "Enter the slot ClawTweaks sits at, then Run.";
                }
            }

            Notify();
        }

        /// <summary>Connects (if needed), asks the helper for a fresh status snapshot, and — unless the
        /// virtual controller is already running — runs the HW-health probe so step 0 shows its verdict
        /// immediately (matching how steps 1/2 auto-populate from the status push).</summary>
        public async Task RefreshStatusAsync(Action<string> log = null)
        {
            if (IsConnecting) return;
            IsConnecting = true;
            Notify();
            try
            {
                if (!PipeClient.IsConnected)
                {
                    // Generous window: right after an in-app update the pipe-serving helper is being
                    // swapped (old keeper killed, new one launched once the single-instance mutex frees).
                    bool connected = await PipeClient.ConnectAsync(TimeSpan.FromSeconds(45), log).ConfigureAwait(false);
                    if (!connected)
                    {
                        foreach (var s in Steps) { s.State = OnboardingStepState.Error; s.Detail = "Could not connect to the helper."; s.Actionable = false; }
                        return;
                    }
                }

                PipeClient.RequestStatusRefresh();
                // Right after an install/helper restart the virtual controller can take several seconds to
                // (re)mount, and the Center pipe only learns state when it ASKS — so keep re-requesting in
                // the background until it resolves, so step 3 (and favorited/auto-jump) tick themselves
                // without a manual Refresh. Fire-and-forget; RecomputeGating runs on each push.
                _ = SettleStatusAsync();
            }
            finally
            {
                IsConnecting = false;
                RecomputeGating();
            }

            // Auto-probe the HW controller after the snapshot request so step 0 has a verdict without a
            // click. Skipped when the virtual controller is already active (probe would be moot + greyed).
            if (_controllerEnabled != true)
                await ProbeHwHealthAsync(log).ConfigureAwait(false);
        }

        /// <summary>Re-requests the helper status a few times so a virtual controller that mounts a few
        /// seconds after an install/restart is detected automatically (no manual Refresh). Stops early
        /// once the controller reports enabled. Guarded so only one loop runs at a time.</summary>
        private async Task SettleStatusAsync()
        {
            if (_settling) return;
            _settling = true;
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    if (!PipeClient.IsConnected) break;
                    if (_controllerEnabled == true) break; // resolved — nothing more to wait for
                    PipeClient.RequestStatusRefresh();
                }
            }
            finally { _settling = false; }
        }

        public async Task RunStepAsync(int index, Action<string> log = null)
        {
            if (!Steps[index].Actionable && Steps[index].State != OnboardingStepState.Unknown) return;
            if (!PipeClient.IsConnected)
            {
                await RefreshStatusAsync(log).ConfigureAwait(false);
                if (!PipeClient.IsConnected) return;
            }

            switch (index)
            {
                case StepHwHealth: await ProbeHwHealthAsync(log).ConfigureAwait(false); break;
                case StepCenterM: await RunCenterMAsync().ConfigureAwait(false); break;
                case StepVirtualController: await RunVirtualControllerAsync(log).ConfigureAwait(false); break;
                case StepAddToBar: await RunCheckPresenceAsync().ConfigureAwait(false); break;
                case StepAutoJump: RunAutoJump(); break;
            }
        }

        /// <summary>Re-checks whether ClawTweaks is favorited into the Game Bar: asks the helper for a
        /// fresh status snapshot and waits briefly for the widget's Favorited push. The widget only runs
        /// once the Game Bar has activated it, so if nothing comes back the user is told to open the Game
        /// Bar and favorite CTW first, then Check again. RecomputeGating renders the resulting state.</summary>
        private async Task RunCheckPresenceAsync()
        {
            var step = Steps[StepAddToBar];
            step.State = OnboardingStepState.Working; step.Detail = "Checking the Game Bar…"; Notify();

            PipeClient.RequestStatusRefresh();
            for (int i = 0; i < 8 && _favorited != true; i++)
                await Task.Delay(400).ConfigureAwait(false);

            // Hand back to RecomputeGating (Ok when favorited, otherwise pending + re-check guidance).
            step.State = OnboardingStepState.Pending;
            RecomputeGating();
        }

        /// <summary>Runs the helper-side HW-controller health probe and reflects the verdict in step 0.</summary>
        private async Task ProbeHwHealthAsync(Action<string> log = null)
        {
            var step = Steps[StepHwHealth];
            step.State = OnboardingStepState.Working; step.Detail = "Checking the controller…"; Notify();

            string payload = await PipeClient.RequestControllerHealthAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            _hwProbedThisSession = true;

            var health = ControllerHwHealthPayload.Parse(payload);
            _hwHealthy = health.Verdict == "ok";
            step.State = _hwHealthy ? OnboardingStepState.Ok : OnboardingStepState.Error;
            step.Detail = health.FriendlyDetail;
            step.Actionable = true;
            RecomputeGating();
        }

        private async Task RunCenterMAsync()
        {
            var step = Steps[StepCenterM];
            step.State = OnboardingStepState.Working; step.Detail = "Disabling…"; Notify();

            bool ok = await PipeClient.SetAndWaitForConfirmationAsync(
                Function.MsiCenterActive, false, "False", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            if (ok) { _centerMRunning = false; step.State = OnboardingStepState.Ok; step.Detail = "Disabled."; step.Actionable = false; }
            else { step.State = OnboardingStepState.Error; step.Detail = "Did not confirm in time."; }
            RecomputeGating();
        }

        /// <summary>
        /// Enables the virtual controller (DefaultControllerMode = Virtual), waits for the helper to
        /// confirm, then RE-DIAGNOSES that a virtual pad actually mounted. If the diagnosis fails, ROLLS
        /// BACK to the hardware controller (DefaultControllerMode = 0) so the user is never left with a
        /// dead controller — exactly the failure path the onboarding must guard.
        /// </summary>
        private async Task RunVirtualControllerAsync(Action<string> log = null)
        {
            var step = Steps[StepVirtualController];
            step.State = OnboardingStepState.Working; step.Detail = "Enabling…"; Notify();

            // DefaultControllerMode (0 = Hardware, 1 = Virtual) is the authoritative source the helper
            // persists; ControllerEmulationEnabled is derived from it. Drive the source, not the legacy bool.
            bool enabled = await PipeClient.SetAndWaitForConfirmationAsync(
                Function.DefaultControllerMode, 1, "1", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            if (!enabled)
            {
                step.State = OnboardingStepState.Error; step.Detail = "Did not enable in time.";
                RecomputeGating();
                return;
            }
            _controllerEnabled = true;

            // Post-activation diagnosis. Let the virtual pad AND the physical controller finish
            // (re)initialising before probing: the ViGEm pad enumerates within ~1s, but the controller
            // re-inits a moment later (visible as the LEDs blinking ~2s in), so an immediate probe can
            // report "ready" prematurely. Wait for it to settle, then require the pad on TWO consecutive
            // probes so a transient enumeration blip doesn't pass as healthy.
            step.Detail = "Waiting for the virtual controller to settle…"; Notify();
            await Task.Delay(5000).ConfigureAwait(false);
            step.Detail = "Verifying the virtual pad…"; Notify();

            bool healthy = false;
            HealthResult health = null;
            int consecutive = 0;
            for (int attempt = 0; attempt < 6 && !healthy; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000).ConfigureAwait(false);
                health = await Task.Run(() => ControllerHealth.Probe()).ConfigureAwait(false);
                if (health.VirtualPadCount >= 1) { consecutive++; if (consecutive >= 2) healthy = true; }
                else consecutive = 0;
            }

            if (healthy)
            {
                _verifiedThisSession = true;
                step.State = OnboardingStepState.Ok;
                step.Detail = $"Enabled and verified ({health.VirtualPadName ?? "virtual pad"}).";
                step.Actionable = false;
            }
            else
            {
                // Roll back to the hardware controller so the user keeps a working gamepad.
                log?.Invoke("Virtual pad did not mount — rolling back to the hardware controller.");
                await PipeClient.SetAndWaitForConfirmationAsync(
                    Function.DefaultControllerMode, 0, "0", TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                _controllerEnabled = false;
                _verifiedThisSession = false;
                step.State = OnboardingStepState.Error;
                step.Detail = "No virtual pad detected — rolled back to the hardware controller.";
            }
            RecomputeGating();
        }

        // Note: the programmatic widget-order rewrite was proven impossible on-device — the order/
        // membership is not persisted anywhere, only reconstructed at runtime in GameBar.exe (see
        // reverse_engineered/RE_GameBar_WidgetBar_Order.md). So the user enters the slot and the helper
        // navigates to it by RB hops; we neither read nor move the widget.

        private void RunAutoJump()
        {
            var step = Steps[StepAutoJump];
            int pos = AutoJumpPositionValue < 1 ? 1 : (AutoJumpPositionValue > 10 ? 10 : AutoJumpPositionValue);
            bool sent = PipeClient.SetProperty(Function.GameBarWidgetPosition, pos);
            step.State = sent ? OnboardingStepState.Ok : OnboardingStepState.Error;
            step.Detail = sent ? $"Auto-jump set to position {pos}." : "Could not reach the helper.";
            step.Actionable = false;
            RecomputeGating();
        }
    }
}
