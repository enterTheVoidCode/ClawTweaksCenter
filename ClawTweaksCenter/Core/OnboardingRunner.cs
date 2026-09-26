using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Enums;

namespace ClawTweaksCenter.Core
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

        /// <summary>Not shown at all. Different from "not actionable": a gated step is waiting for
        /// an earlier one and belongs on screen, a hidden step does not apply to this installation.
        /// Set for the virtual-controller steps in ClawTweaks Essential (see EssentialMode).</summary>
        public bool Hidden = false;
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

        /// <summary>Game Bar's own "always open to Home" switch. The ESSENTIAL counterpart of the
        /// auto-jump: turned off, Game Bar reopens the widget you used last, which lands you in
        /// ClawTweaks with no virtual pad needed to tap RB. Hidden outside Essential mode, where
        /// the auto-jump already does the job.</summary>
        public const int StepGameBarHome = 5;

        public HelperPipeClient PipeClient { get; }

        public IReadOnlyList<OnboardingStep> Steps { get; } = new List<OnboardingStep>
        {
            new OnboardingStep { Title = "Check hardware controller health" },
            new OnboardingStep { Title = "Disable MSI Center M" },
            new OnboardingStep { Title = "Enable virtual controller" },
            new OnboardingStep { Title = "Add ClawTweaks to the Game Bar" },
            new OnboardingStep { Title = "Activate Game Bar auto-jump" },
            // ⚠️ THE USER'S OWN WORDING, near enough verbatim (2026-09-26). Do not "improve" it
            // into a description of the mechanism - the step says what it does for you, not what
            // registry value it writes.
            new OnboardingStep { Title = "Always open the last Game Bar widget" },
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
        private bool? _gameBarOpensOnHome;  // from the helper: true = always Home (what we want off),
                                            // null = not read yet, or unreadable

        /// <summary>
        /// 🔴 THIS INSTALLATION IS NOT GOING TO RUN THE VIRTUAL CONTROLLER, so two of the five
        /// steps are asking for something impossible (user, 2026-09-26: after an Essential install
        /// "kam das normale onboarding das nicht mehr so richtig passt").
        ///
        /// Step 2 enables the virtual controller, which the helper now refuses when its drivers are
        /// absent. Step 4 feeds the Game Bar slot to the auto-jump, which only ever fires while
        /// controller emulation is active - it would store a number nothing reads.
        ///
        /// ⚠️ NOT "the virtual controller is currently off". Somebody in hardware mode who HAS the
        /// drivers can switch at any moment, and taking the steps away would remove their way back.
        /// True only when it is genuinely out of play: the setup was asked and said no, or the
        /// drivers are not on the machine.
        ///
        /// ⚠️ Probed once and cached. ToolDetect touches the registry, the file system and a
        /// device handle, and RecomputeGating runs on every status push.
        /// </summary>
        public bool EssentialMode { get; private set; }
        public string EssentialTitle { get; private set; } = "";
        public string EssentialDetail { get; private set; } = "";

        private bool _essentialProbed;
        private string _essentialMissing = "";

        /// <summary>The step indexes the UI should draw, in order. Everything that renders or
        /// navigates the step cards goes through this - an index that is hidden must not be
        /// reachable by the D-pad either.</summary>
        public List<int> VisibleStepIndexes
        {
            get
            {
                var list = new List<int>();
                for (int i = 0; i < Steps.Count; i++)
                    if (!Steps[i].Hidden) list.Add(i);
                return list;
            }
        }

        private void RefreshEssentialMode()
        {
            if (!_essentialProbed)
            {
                _essentialProbed = true;
                bool usbip = false, hidHide = false;
                try { usbip = ToolDetect.Usbip().Installed; } catch { usbip = true; }
                try { hidHide = ToolDetect.HidHide().Installed; } catch { hidHide = true; }

                _essentialMissing = "";
                if (!usbip) _essentialMissing = "usbip-win2";
                if (!hidHide) _essentialMissing = _essentialMissing.Length == 0 ? "HidHide" : _essentialMissing + " + HidHide";
            }

            bool declined = SetupToolChoices.VirtualControllerWanted == false;
            bool toolsMissing = _essentialMissing.Length > 0;

            // The live state still wins: if the virtual controller is actually running, nothing here
            // applies, whatever the setup once recorded.
            EssentialMode = _controllerEnabled != true && (declined || toolsMissing);

            if (EssentialMode)
            {
                // ⚠️ Two different situations and only one of them is a choice. "You picked
                // Essential" over a failed driver install credits the user with a decision they did
                // not make.
                //
                // 🔴 LOCALISED HERE, AS A FORMAT. These were built by string concatenation, which
                // is a line that can never be translated: the lookup keys on the whole English
                // sentence and a sentence with a tool name glued into it matches nothing. Same trap
                // the OSD cards hit. The caller therefore does NOT run these through Loc again.
                if (declined)
                {
                    EssentialTitle = Loc.T("ClawTweaks Essential");
                    EssentialDetail = toolsMissing
                        ? Loc.F("You chose the hardware controller during setup. Install {0} to switch.", _essentialMissing)
                        : Loc.T("You chose the hardware controller during setup.");
                }
                else
                {
                    EssentialTitle = Loc.T("Virtual controller not available");
                    EssentialDetail = Loc.F("{0} is not installed, so its steps are hidden.", _essentialMissing);
                }
            }

            Steps[StepVirtualController].Hidden = EssentialMode;
            Steps[StepAutoJump].Hidden = EssentialMode;
            // The two are alternatives, never both: auto-jump taps RB with the virtual pad, this
            // one changes Game Bar's own behaviour and needs no pad at all.
            Steps[StepGameBarHome].Hidden = !EssentialMode;
        }

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
                else if (function == Function.Setup_GameBarOpensOnHome)
                {
                    // "" means the helper could not read it - that stays null, so the step says
                    // nothing rather than claiming a setting is wrong.
                    _gameBarOpensOnHome = content == "1" ? true : (content == "0" ? (bool?)false : null);
                    RecomputeGating();
                }
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
            RefreshEssentialMode();

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
                // 🔴 THE PREREQUISITE IS NOT THE SAME IN BOTH MODES (user, 2026-09-26: "das gamebar
                // hinzufuegen punkt ging nicht los da der vorangegangene punkt nun fehlt"). This
                // used to wait for the virtual controller, which in ClawTweaks Essential never
                // arrives - so the step sat behind a prerequisite that had been removed from the
                // page and the chain dead-ended after "Disable MSI Center M".
                //
                // Adding CTW to the Game Bar has nothing to do with the virtual pad anyway. In
                // Essential mode the real prerequisite is the one before it: a healthy controller
                // and Center M out of the way.
                bool ready = EssentialMode
                    ? (HwOk && _centerMRunning != true)
                    : (_verifiedThisSession || _controllerEnabled == true);
                if (!ready)
                {
                    bar.State = OnboardingStepState.Pending; bar.Actionable = false;
                    bar.Detail = EssentialMode
                        ? "Disable MSI Center M first."
                        : "Enable the virtual controller first.";
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

            // Step 5 — Game Bar's own "open to Home" switch (Essential only). Needs CTW in the bar
            // for the same reason the auto-jump does: reopening the last widget only helps once
            // ClawTweaks can BE the last widget.
            var gb = Steps[StepGameBarHome];
            if (gb.State != OnboardingStepState.Working)
            {
                if (_favorited != true)
                {
                    gb.State = OnboardingStepState.Pending; gb.Actionable = false;
                    gb.Detail = "Add ClawTweaks to the Game Bar first.";
                }
                else if (_gameBarOpensOnHome == false)
                {
                    gb.State = OnboardingStepState.Ok; gb.Actionable = true;
                    gb.Detail = "On. ClawTweaks opens straight away.";
                }
                else if (_gameBarOpensOnHome == true)
                {
                    gb.State = OnboardingStepState.Pending; gb.Actionable = true;
                    gb.Detail = "Turns off the jump back to Home, so ClawTweaks opens straight away.";
                }
                else
                {
                    // ⚠️ UNREADABLE IS NOT WRONG. Saying "always opens on Home" over a setting we
                    // could not read would send the user to change something already correct.
                    gb.State = OnboardingStepState.Pending; gb.Actionable = true;
                    gb.Detail = "Checking Game Bar…";
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
                    aj.Detail = Loc.F("Auto-jump active (position {0}).", sp);
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

                // 🔴 COMPUTE IT FIRST. EssentialMode is normally set in RecomputeGating, which
                // runs in this method's finally - i.e. AFTER the line below. On the first refresh
                // it would still be its default false, the probe would be skipped, and the step
                // would sit on "Checking Game Bar..." with nothing ever asking again.
                RefreshEssentialMode();

                // The Game Bar switch is not part of the status snapshot - nothing pushes it, so it
                // has to be asked for. Only in Essential mode, where the step that shows it exists.
                if (EssentialMode)
                {
                    _ = Task.Run(async () =>
                    {
                        string r = await PipeClient.GameBarOpensOnHomeAsync(TimeSpan.FromSeconds(6))
                                                   .ConfigureAwait(false);
                        _gameBarOpensOnHome = r == "1" ? true : (r == "0" ? (bool?)false : null);
                        RecomputeGating();
                        Notify();
                    });
                }
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
                case StepGameBarHome: await RunGameBarHomeAsync(log).ConfigureAwait(false); break;
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
        /// Turns Game Bar's "always open to Home" switch OFF, then READS IT BACK.
        ///
        /// 🔴 The read-back is the point, not politeness. Measured 2026-09-26: the write DOES
        /// survive Game Bar closing, but a running Game Bar keeps showing and using its cached
        /// value until it restarts, and any other Game Bar setting the user changes in the same
        /// session writes that stale cache back out. Reporting "done" from the write alone would
        /// be a claim this code cannot make.
        /// </summary>
        private async Task RunGameBarHomeAsync(Action<string> log = null)
        {
            var step = Steps[StepGameBarHome];
            step.State = OnboardingStepState.Working;
            step.Detail = "Changing the setting…";
            Notify();

            string result = await PipeClient.GameBarOpensOnHomeAsync(TimeSpan.FromSeconds(8), setTo: false)
                                            .ConfigureAwait(false);
            _gameBarOpensOnHome = result == "1" ? true : (result == "0" ? (bool?)false : null);

            if (_gameBarOpensOnHome == false)
            {
                step.State = OnboardingStepState.Ok;
                // ⚠️ A RUNNING GAME BAR WILL NOT NOTICE. The value is stored, and measured to
                // survive Game Bar closing - but Game Bar reads it once and keeps it, so the
                // behaviour only changes after it restarts. Promising it for the next Win+G would
                // be a promise the very next press breaks.
                step.Detail = "Done. Takes effect after Game Bar restarts.";
            }
            else if (_gameBarOpensOnHome == true)
            {
                // The write did not take. Measured behaviour says this should not happen, so say
                // what to do rather than guess why.
                step.State = OnboardingStepState.Error;
                step.Detail = "Game Bar kept the setting. Close Game Bar and run this again.";
            }
            else
            {
                step.State = OnboardingStepState.Error;
                step.Detail = "Could not read the Game Bar setting.";
            }
            step.Actionable = true;
            Notify();
            log?.Invoke(step.Detail);
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
                step.Detail = Loc.F("Enabled and verified ({0}).", health.VirtualPadName ?? Loc.T("virtual pad"));
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
            step.Detail = sent ? Loc.F("Auto-jump set to position {0}.", pos) : "Could not reach the helper.";
            step.Actionable = false;
            RecomputeGating();
        }
    }
}
