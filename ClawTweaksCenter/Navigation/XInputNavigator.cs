using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClawTweaksCenter.Navigation
{
    /// <summary>
    /// Polls the Claw's gamepad and raises a single <see cref="ButtonPressed"/> event on the rising
    /// edge of A / B / X / Y / Menu. The wizard maps those to fixed actions (there is no roaming
    /// focus) so the user always sees exactly which button does what.
    /// Events are delivered synchronously on the window's dispatcher only while its HWND owns the
    /// foreground. Driver polling stays on the dedicated background thread.
    ///
    /// XInput has no message-loop hook, so this polls. Two things about HOW it polls are load-bearing
    /// and were both paid for by the same bug report (2026-09-14: the library goes black and stands
    /// still for seconds while the virtual pad is mounted, having scrolled perfectly a moment before).
    ///
    /// 1. IT POLLS ON ITS OWN THREAD, not on a DispatcherTimer. XInputGetState is a call into the
    ///    driver stack and it is at its slowest exactly when the device tree is busy - which is the
    ///    whole mount window. On the UI thread that time is taken straight out of WPF's layout and
    ///    render budget, and unrendered WPF is literally black. Off it, a slow XInput call delays
    ///    input by a frame and nothing else. Only event delivery reaches the UI thread, where native
    ///    foreground ownership is checked again before any subscriber can act.
    ///
    /// 2. EMPTY SLOTS ARE NOT ASKED EVERY ROUND. Microsoft's own guidance for XInputGetState is to
    ///    space out the search for new controllers rather than query an empty user slot per frame.
    ///    During the mount ALL FOUR are empty - the pad has left XInput mode and the virtual one has
    ///    not arrived - so the naive loop hits its worst case for several seconds at the worst
    ///    possible moment. Connected slots are polled every round; the rest are rescanned on the
    ///    interval below.
    ///
    /// All connected slots are still OR-ed together, so the controller works regardless of which slot
    /// it occupies and a second pad still works alongside the first.
    /// </summary>
    public sealed class XInputNavigator : IDisposable
    {
        public event Action<PadButton> ButtonPressed
        {
            add => _input.ButtonPressed += value;
            remove => _input.ButtonPressed -= value;
        }

        /// <summary>Raised continuously while the user pushes up/down (D-Pad or left stick). Positive = down.</summary>
        public event Action<double> ScrollRequested
        {
            add => _input.ScrollRequested += value;
            remove => _input.ScrollRequested -= value;
        }

        /// <summary>Raised continuously while the user pushes the RIGHT stick up/down. Positive = down.
        /// Kept separate from <see cref="ScrollRequested"/> so a screen that binds the D-Pad to a
        /// discrete grid selection (CenterMenuWindow's build picker) can still offer stick scrolling
        /// without the two fighting over the same input.</summary>
        public event Action<double> RightStickScrollRequested
        {
            add => _input.RightStickScrollRequested += value;
            remove => _input.RightStickScrollRequested -= value;
        }

        /// <summary>
        /// A FLICK of the right stick: one raise per push past the deadzone, in one of the four
        /// directions, reported as the matching PadButton.
        ///
        /// Separate from <see cref="RightStickScrollRequested"/> because the two answer different
        /// questions. That one is a rate - it fires every tick while the stick is held, which is what
        /// scrolling wants and what a discrete choice must never be given: held for half a second it
        /// would flip a setting a dozen times. This one fires once per push, and covers the X axis
        /// the scroll signal never had.
        /// </summary>
        public event Action<PadButton> RightStickFlicked
        {
            add => _input.RightStickFlicked += value;
            remove => _input.RightStickFlicked -= value;
        }

        /// <summary>
        /// A direction that is being HELD, raised over and over until it is let go: D-pad or left
        /// stick, the two inputs that move a selection.
        ///
        /// Separate from <see cref="ButtonPressed"/> because the two are not interchangeable. A
        /// press is a decision; a repeat is the same decision continuing, and a screen where that
        /// would be wrong - a switch, a value, a confirmation - simply does not subscribe. The
        /// library binds it for its shelves; everything else in Center ignores it and behaves
        /// exactly as before.
        ///
        /// ⚠️ NOT the right stick. In the library that stick changes the sort order and the
        /// grouping, and a held stick would cycle through them several times a second and land
        /// wherever it was let go. It has no "further in the same direction" to offer.
        /// </summary>
        public event Action<PadButton> ButtonRepeated
        {
            add => _input.ButtonRepeated += value;
            remove => _input.ButtonRepeated -= value;
        }

        private readonly Window _window;
        private readonly ControllerInput _input;

        // The poll loop and its stop signal. The thread is IsBackground: whatever else goes wrong at
        // shutdown, it can never be the reason Center stays in the process list.
        private readonly System.Threading.ManualResetEventSlim _stop = new System.Threading.ManualResetEventSlim(false);
        private System.Threading.Thread _pollThread;
        private volatile bool _running;

        // Captured on the UI thread; native foreground checks may read it from the poll thread.
        private IntPtr _windowHandle;

        /// <summary>Which user slots answered last time we looked. Only these are polled every round.</summary>
        private readonly bool[] _slotConnected = new bool[4];

        /// <summary>When all four slots were last swept for newly arrived pads.</summary>
        private DateTime _lastFullScan = DateTime.MinValue;

        private const int TickMs = 40;

        /// <summary>
        /// How often the slots nobody answered from are asked again. Half a second is far below what
        /// anyone notices when picking a controller up, and a twelfth of the calls that asking every
        /// 40 ms would make - which is the entire point.
        /// </summary>
        private static readonly TimeSpan FullScanInterval = TimeSpan.FromMilliseconds(500);

        public XInputNavigator(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));

            // Activated/Deactivated delivery can lag behind a native foreground switch. Cache only
            // the HWND, then ask Win32 each time; another window in this process (such as a dialog)
            // must not let the underlying Center screen consume the same controller input.
            CaptureWindowHandle();
            _window.SourceInitialized += OnSourceInitialized;
            _input = new ControllerInput(HasForeground, PollController,
                action => _window.Dispatcher.Invoke(action), () => DateTime.UtcNow);
        }

        private void OnSourceInitialized(object sender, EventArgs e) => CaptureWindowHandle();

        private void CaptureWindowHandle()
            => System.Threading.Volatile.Write(ref _windowHandle, new WindowInteropHelper(_window).Handle);

        private bool HasForeground()
            => ControllerInput.IsForegroundWindow(System.Threading.Volatile.Read(ref _windowHandle), GetForegroundWindow());

        public void Start()
        {
            if (_running) return;
            _running = true;
            _stop.Reset();
            _pollThread = new System.Threading.Thread(PollLoop)
            {
                IsBackground = true,
                Name = "CenterPadPoll",
            };
            _pollThread.Start();
            Ui.UiStallTrace.Write($"poll loop started ({TickMs}ms, empty slots rescanned every {FullScanInterval.TotalMilliseconds:F0}ms)");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _stop.Set();

            // BOUNDED join, deliberately. This is called from the UI thread, and the poll thread may
            // at this instant be sitting inside a subscriber's Dispatcher.Invoke waiting for that very
            // thread - joining without a limit is then a two-party deadlock with the window half
            // closed. A bounded wait turns the worst case into a quarter second and a background
            // thread that dies on its own; the Invoke it is stuck in throws once the dispatcher
            // shuts down, and PollLoop swallows that.
            try { _pollThread?.Join(250); } catch { }
            _pollThread = null;
        }

        public void Dispose()
        {
            System.Threading.Volatile.Write(ref _windowHandle, IntPtr.Zero);
            _window.SourceInitialized -= OnSourceInitialized;
            Stop();
            try { _stop.Dispose(); } catch { }
        }

        /// <summary>
        /// The loop. Waits on the stop signal instead of sleeping, so Stop() is acted on at once
        /// rather than up to a tick later.
        /// </summary>
        private void PollLoop()
        {
            var roundClock = System.Diagnostics.Stopwatch.StartNew();
            long lastRoundEnd = 0;

            // This thread's OWN scheduling latency, with the collector accounted for. It is the third
            // witness: a gap here is not the dispatcher's doing and not a driver's - it is this
            // thread not being given the CPU, which is either a collection or the machine being busy.
            Ui.UiStallTrace.GcMark gapMark = Ui.UiStallTrace.MarkGc();
            Ui.UiStallTrace.CpuMark gapCpuMark = Ui.UiStallTrace.MarkCpu();

            while (_running)
            {
                if (_stop.Wait(TickMs)) break;

                long gap = roundClock.ElapsedMilliseconds - lastRoundEnd;
                Ui.UiStallTrace.GcMark gapGc = gapMark;
                gapMark = Ui.UiStallTrace.MarkGc();
                Ui.UiStallTrace.CpuMark gapCpu = gapCpuMark;
                gapCpuMark = Ui.UiStallTrace.MarkCpu();
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    // Anything at all - including the TaskCanceledException a Dispatcher.Invoke
                    // raises once the window is shutting down. An input poll is never worth taking
                    // the process down for.
                    Ui.UiStallTrace.Write($"poll round threw: {ex.GetType().Name}: {ex.Message}");
                }
                lastRoundEnd = roundClock.ElapsedMilliseconds;

                if (gap > TickMs + Ui.UiStallTrace.GapWarnMs)
                    Ui.UiStallTrace.Write($"gap {gap}ms between rounds (asked for {TickMs}ms) - " +
                                          $"{Ui.UiStallTrace.SinceGc(gapGc)} - {Ui.UiStallTrace.Witnesses()} - " +
                                          $"{Ui.UiStallTrace.SinceCpu(gapCpu)}");

                MeasureUiResponsiveness();
                ReportIfUiStillBlocked();
            }

            Ui.UiStallTrace.Write("poll loop ended");
        }

        private void PollOnce() => _input.PollOnce();

        /// <summary>
        /// Asks the UI thread how busy it is, without asking it to do anything.
        ///
        /// A no-op queued at Input priority - the same priority the old DispatcherTimer used - and
        /// timed from queueing to running. That number IS the stutter the user sees: while it is
        /// large, WPF is not laying out or rendering either.
        ///
        /// It is here because the fix above could be right about the poll and still leave the
        /// symptom, and then the next question has to be answerable from the same log: the mount
        /// window is also full of PnP broadcasts (HidHide hiding the physical pad, VIIPER creating
        /// the virtual one, the phantom cleanup uninstalling stale nodes), and those go through the
        /// same message pump. A small "poll" next to a large "ui" says so in one line.
        ///
        /// Fire-and-forget on purpose: never waited on, so a stalled UI thread delays the report, not
        /// the poll. At most one in flight, so a long stall produces one line and not a queue.
        /// </summary>
        private void MeasureUiResponsiveness()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _uiProbeInFlight, 1, 0) != 0) return;

            var queuedAt = System.Diagnostics.Stopwatch.StartNew();
            Ui.UiStallTrace.GcMark gc = Ui.UiStallTrace.MarkGc();
            System.Threading.Volatile.Write(ref _probeQueuedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            System.Threading.Volatile.Write(ref _probeReported, 0);
            _probeCpu = Ui.UiStallTrace.MarkCpu();
            try
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    long waited = queuedAt.ElapsedMilliseconds;
                    System.Threading.Volatile.Write(ref _uiProbeInFlight, 0);
                    if (waited > Ui.UiStallTrace.UiWarnMs)
                        Ui.UiStallTrace.Write($"ui thread took {waited}ms to run a no-op at input priority - " +
                                              $"{Ui.UiStallTrace.SinceGc(gc)} - {Ui.UiStallTrace.Witnesses()}");
                }));
            }
            catch
            {
                // The dispatcher is shutting down. Release the slot so a restart is not blocked.
                System.Threading.Volatile.Write(ref _uiProbeInFlight, 0);
            }
        }

        private long _probeQueuedAt;
        private int _probeReported;
        private Ui.UiStallTrace.CpuMark _probeCpu;

        /// <summary>
        /// Asks the witnesses WHILE the UI thread is still stuck, from this thread.
        ///
        /// 🔴 THE FIRST VERSION OF THIS ASKED FROM THE WRONG PLACE. It read them inside the probe's
        /// own callback, which runs on the UI thread - so by then the blocking work had finished and
        /// the "currently running dispatcher operation" it reported was the probe itself, every time
        /// ("MeasureUiResponsiveness ... running for 0ms" in the log of 2026-09-14 21:24). And that
        /// was not a slip in one line: the dispatcher is single-threaded, so an observer that runs ON
        /// it can never, by construction, catch the operation that is blocking it.
        ///
        /// Only another thread can. This one is already awake every 40 ms, so when the probe it
        /// queued has not run for a while it takes the reading right then - at which point
        /// <see cref="Ui.UiStallTrace.Witnesses"/> names either the dispatcher operation that is
        /// actually holding the thread, or no operation at all, which is the answer that matters:
        /// the thread is then inside a window message rather than inside our code.
        ///
        /// Once per stall, not once per round - a 1.2 s stall would otherwise write thirty lines.
        /// </summary>
        private void ReportIfUiStillBlocked()
        {
            const int OverdueMs = 300;

            if (System.Threading.Volatile.Read(ref _uiProbeInFlight) == 0) return;
            long queued = System.Threading.Volatile.Read(ref _probeQueuedAt);
            if (queued == 0) return;

            long waited = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - queued)
                                 / (System.Diagnostics.Stopwatch.Frequency / 1000.0));
            if (waited < OverdueMs) return;
            if (System.Threading.Interlocked.Exchange(ref _probeReported, 1) != 0) return;

            Ui.UiStallTrace.Write($"ui thread STILL BLOCKED after {waited}ms (read from the poll thread, " +
                                  $"while it is down) - {Ui.UiStallTrace.Witnesses()} - " +
                                  $"{Ui.UiStallTrace.SinceCpu(_probeCpu)}");
        }

        private int _uiProbeInFlight;

        #region XInput P/Invoke
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll")]
        private static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);

        private const uint ERROR_SUCCESS = 0;

        /// <summary>
        /// Reads every slot that is known to hold a pad, and - no more often than
        /// <see cref="FullScanInterval"/> - the ones that are not, to notice a pad that has arrived.
        ///
        /// ⚠️ THE SWEEP IS THE EXPENSIVE HALF, and it is expensive in proportion to how many slots are
        /// empty. With a pad connected that is three calls twice a second. With NONE connected - the
        /// mount window - it is four, and they are the slow kind, which is why they are not made
        /// twenty-five times a second on the thread that draws the screen.
        /// </summary>
        private ControllerState? PollController()
            => TryPollCombined(out ushort buttons, out short lx, out short ly, out short rx,
                               out short ry, out byte lt, out byte rt)
                ? new ControllerState(buttons, lx, ly, rx, ry, lt, rt)
                : null;

        private bool TryPollCombined(out ushort buttons, out short leftStickX, out short leftStickY,
                                     out short rightStickX, out short rightStickY,
                                     out byte leftTrigger, out byte rightTrigger)
        {
            buttons = 0; leftStickX = 0; leftStickY = 0; rightStickX = 0; rightStickY = 0; leftTrigger = 0; rightTrigger = 0;
            bool any = false;

            var now = DateTime.UtcNow;
            bool sweep = now - _lastFullScan >= FullScanInterval;
            if (sweep) _lastFullScan = now;

            var cost = System.Diagnostics.Stopwatch.StartNew();
            Ui.UiStallTrace.GcMark gc = Ui.UiStallTrace.MarkGc();
            int asked = 0;

            for (uint i = 0; i < 4; i++)
            {
                // A slot nobody answered from is worth a call only on the sweep. A slot that DID
                // answer is read every round - that one is cheap, and it is the user's controller.
                if (!_slotConnected[i] && !sweep) continue;

                var state = new XINPUT_STATE();
                asked++;
                if (XInputGetState(i, ref state) != ERROR_SUCCESS)
                {
                    // Gone, or never there. Either way stop paying for it every round.
                    _slotConnected[i] = false;
                    continue;
                }
                _slotConnected[i] = true;
                any = true;
                buttons |= state.Gamepad.wButtons;
                // Cast to int before Math.Abs: a stick pushed to its exact extreme reports
                // short.MinValue (-32768), and Math.Abs(short) — the exact overload C# picks here —
                // throws OverflowException for MinValue since +32768 doesn't fit back in a short.
                // Math.Abs(int) has no such problem. This was the real crash-on-scroll bug.
                if (Math.Abs((int)state.Gamepad.sThumbLX) > Math.Abs((int)leftStickX)) leftStickX = state.Gamepad.sThumbLX;
                if (Math.Abs((int)state.Gamepad.sThumbLY) > Math.Abs((int)leftStickY)) leftStickY = state.Gamepad.sThumbLY;
                if (Math.Abs((int)state.Gamepad.sThumbRX) > Math.Abs((int)rightStickX)) rightStickX = state.Gamepad.sThumbRX;
                if (Math.Abs((int)state.Gamepad.sThumbRY) > Math.Abs((int)rightStickY)) rightStickY = state.Gamepad.sThumbRY;
                // Triggers combine as a maximum across slots, matching how the buttons are OR-ed:
                // whichever pad the user actually holds is the one that decides.
                if (state.Gamepad.bLeftTrigger > leftTrigger) leftTrigger = state.Gamepad.bLeftTrigger;
                if (state.Gamepad.bRightTrigger > rightTrigger) rightTrigger = state.Gamepad.bRightTrigger;
            }

            long ms = cost.ElapsedMilliseconds;
            if (ms > Ui.UiStallTrace.PollWarnMs)
            {
                int connected = 0;
                foreach (bool c in _slotConnected) if (c) connected++;
                Ui.UiStallTrace.Write($"XInput took {ms}ms for {asked} slot(s){(sweep ? " (sweep)" : "")}, " +
                                   $"{connected} connected - {Ui.UiStallTrace.SinceGc(gc)}");
            }

            return any;
        }
        #endregion
    }
}
