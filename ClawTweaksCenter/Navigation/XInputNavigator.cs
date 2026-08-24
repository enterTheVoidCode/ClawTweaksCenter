using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ClawTweaksCenter.Navigation
{
    /// <summary>
    /// Polls the Claw's gamepad and raises a single <see cref="ButtonPressed"/> event on the rising
    /// edge of A / B / X / Y / Menu. The wizard maps those to fixed actions (there is no roaming
    /// focus) so the user always sees exactly which button does what.
    ///
    /// We poll on a DispatcherTimer because XInput has no message-loop hook. All four user slots are
    /// OR-ed together so the controller works regardless of which slot it occupies.
    /// </summary>
    public sealed class XInputNavigator : IDisposable
    {
        public event Action<PadButton> ButtonPressed;

        /// <summary>Raised continuously while the user pushes up/down (D-Pad or left stick). Positive = down.</summary>
        public event Action<double> ScrollRequested;

        /// <summary>Raised continuously while the user pushes the RIGHT stick up/down. Positive = down.
        /// Kept separate from <see cref="ScrollRequested"/> so a screen that binds the D-Pad to a
        /// discrete grid selection (CenterMenuWindow's build picker) can still offer stick scrolling
        /// without the two fighting over the same input.</summary>
        public event Action<double> RightStickScrollRequested;

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
        public event Action<PadButton> RightStickFlicked;

        private readonly Window _window;
        private readonly DispatcherTimer _timer;
        private ushort _prevButtons;
        private ushort _prevStickDirBits;
        private ushort _prevRightStickDirBits;
        private ushort _prevTriggerBits;
        private const short StickDeadzone = 12000;

        // Analogue triggers turned into presses. XINPUT_GAMEPAD_TRIGGER_THRESHOLD is Microsoft's own
        // value for "this counts as pulled". Edge-triggered against _prevTriggerBits for the same
        // reason the left stick is: a held trigger would otherwise fire a group change on every one
        // of the 25 ticks per second, and the user would never see the group they aimed for.
        private const byte TriggerThreshold = 30;
        private const ushort TriggerLeft = 0x0001;
        private const ushort TriggerRight = 0x0002;

        // Virtual D-Pad bits for the left stick — deliberately the same values as the real
        // XINPUT_GAMEPAD_DPAD_* constants below so a screen bound to PadButton.Up/Down/Left/Right
        // (CenterMenuWindow's grid selection) reacts identically whether the user used the D-Pad or
        // the left stick. Edge-triggered exactly like the real D-Pad (one Raise per push past the
        // deadzone), not continuous — the left stick had no effect at all on the grid before this,
        // since only the physical D-Pad bits were ever edge-detected.
        private const ushort StickDirUp = 0x0001;
        private const ushort StickDirDown = 0x0002;
        private const ushort StickDirLeft = 0x0004;
        private const ushort StickDirRight = 0x0008;

        public XInputNavigator(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(40),
            };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_window.IsActive) { _prevButtons = 0; _prevStickDirBits = 0; _prevTriggerBits = 0; return; }
            if (!TryPollCombined(out ushort buttons, out short lx, out short ly, out short rx, out short ry, out byte lt, out byte rt))
            { _prevButtons = 0; _prevStickDirBits = 0; _prevTriggerBits = 0; return; }

            // Continuous scroll from D-Pad up/down or left-stick Y (fires every tick while held).
            double scroll = 0;
            if ((buttons & XINPUT_GAMEPAD_DPAD_UP) != 0) scroll -= 46;
            if ((buttons & XINPUT_GAMEPAD_DPAD_DOWN) != 0) scroll += 46;
            if (ly > StickDeadzone) scroll -= 46 * (ly / 32767.0);
            if (ly < -StickDeadzone) scroll += 46 * (-ly / 32767.0);
            if (Math.Abs(scroll) > 0.5) ScrollRequested?.Invoke(scroll);

            // Right-stick Y drives its own independent scroll signal (see RightStickScrollRequested).
            double rscroll = 0;
            if (ry > StickDeadzone) rscroll -= 46 * (ry / 32767.0);
            if (ry < -StickDeadzone) rscroll += 46 * (-ry / 32767.0);
            if (Math.Abs(rscroll) > 0.5) RightStickScrollRequested?.Invoke(rscroll);

            // Left stick as a virtual D-Pad: edge-triggered exactly like the real D-Pad (one Raise per
            // push past the deadzone, not one per tick while held), so screens bound to
            // PadButton.Up/Down/Left/Right react identically either way. Runs unconditionally (not
            // gated behind the button "pressed == 0" check below) since a stick push alone never sets
            // any wButtons bit.
            ushort stickDirBits = 0;
            if (ly > StickDeadzone) stickDirBits |= StickDirUp;
            if (ly < -StickDeadzone) stickDirBits |= StickDirDown;
            if (lx > StickDeadzone) stickDirBits |= StickDirRight;
            if (lx < -StickDeadzone) stickDirBits |= StickDirLeft;
            ushort stickPressed = (ushort)(stickDirBits & ~_prevStickDirBits);
            _prevStickDirBits = stickDirBits;
            if ((stickPressed & StickDirUp) != 0) Raise(PadButton.Up);
            if ((stickPressed & StickDirDown) != 0) Raise(PadButton.Down);
            if ((stickPressed & StickDirLeft) != 0) Raise(PadButton.Left);
            if ((stickPressed & StickDirRight) != 0) Raise(PadButton.Right);

            // Right stick as its own edge-triggered four-way flick, alongside the continuous scroll
            // signal above. Both are raised for the same push, and that is deliberate: a screen binds
            // whichever of the two it needs and never sees the other.
            ushort rightDirBits = 0;
            if (ry > StickDeadzone) rightDirBits |= StickDirUp;
            if (ry < -StickDeadzone) rightDirBits |= StickDirDown;
            if (rx > StickDeadzone) rightDirBits |= StickDirRight;
            if (rx < -StickDeadzone) rightDirBits |= StickDirLeft;
            ushort rightPressed = (ushort)(rightDirBits & ~_prevRightStickDirBits);
            _prevRightStickDirBits = rightDirBits;
            if ((rightPressed & StickDirUp) != 0) RightStickFlicked?.Invoke(PadButton.Up);
            if ((rightPressed & StickDirDown) != 0) RightStickFlicked?.Invoke(PadButton.Down);
            if ((rightPressed & StickDirLeft) != 0) RightStickFlicked?.Invoke(PadButton.Left);
            if ((rightPressed & StickDirRight) != 0) RightStickFlicked?.Invoke(PadButton.Right);

            // Triggers as edge-triggered presses. Same shape as the virtual D-Pad above and, like it,
            // run unconditionally — a trigger sets no wButtons bit, so the "pressed == 0" early-out
            // below would swallow it entirely.
            ushort triggerBits = 0;
            if (lt >= TriggerThreshold) triggerBits |= TriggerLeft;
            if (rt >= TriggerThreshold) triggerBits |= TriggerRight;
            ushort triggerPressed = (ushort)(triggerBits & ~_prevTriggerBits);
            _prevTriggerBits = triggerBits;
            if ((triggerPressed & TriggerLeft) != 0) Raise(PadButton.LT);
            if ((triggerPressed & TriggerRight) != 0) Raise(PadButton.RT);

            ushort pressed = (ushort)(buttons & ~_prevButtons);
            _prevButtons = buttons;
            if (pressed == 0) return;

            if ((pressed & XINPUT_GAMEPAD_A) != 0) Raise(PadButton.A);
            if ((pressed & XINPUT_GAMEPAD_B) != 0) Raise(PadButton.B);
            if ((pressed & XINPUT_GAMEPAD_X) != 0) Raise(PadButton.X);
            if ((pressed & XINPUT_GAMEPAD_Y) != 0) Raise(PadButton.Y);
            if ((pressed & XINPUT_GAMEPAD_START) != 0) Raise(PadButton.Menu); // Menu/☰ button
            if ((pressed & XINPUT_GAMEPAD_BACK) != 0) Raise(PadButton.View);   // Select/View button
            if ((pressed & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0) Raise(PadButton.LB);
            if ((pressed & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0) Raise(PadButton.RB);
            if ((pressed & XINPUT_GAMEPAD_RIGHT_THUMB) != 0) Raise(PadButton.R3);

            // Discrete D-Pad edges, in addition to the continuous ScrollRequested above — screens
            // with a real grid/list selection (CenterMenuWindow) bind these; phases that don't bind
            // them (MainWindow) simply never see them.
            if ((pressed & XINPUT_GAMEPAD_DPAD_UP) != 0) Raise(PadButton.Up);
            if ((pressed & XINPUT_GAMEPAD_DPAD_DOWN) != 0) Raise(PadButton.Down);
            if ((pressed & XINPUT_GAMEPAD_DPAD_LEFT) != 0) Raise(PadButton.Left);
            if ((pressed & XINPUT_GAMEPAD_DPAD_RIGHT) != 0) Raise(PadButton.Right);
        }

        private void Raise(PadButton b) => ButtonPressed?.Invoke(b);

        #region XInput P/Invoke
        private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const ushort XINPUT_GAMEPAD_BACK = 0x0020;  // Select / View
        private const ushort XINPUT_GAMEPAD_START = 0x0010; // Menu (☰)
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_A = 0x1000;
        private const ushort XINPUT_GAMEPAD_B = 0x2000;
        private const ushort XINPUT_GAMEPAD_X = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y = 0x8000;

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

        private static bool TryPollCombined(out ushort buttons, out short leftStickX, out short leftStickY,
                                            out short rightStickX, out short rightStickY,
                                            out byte leftTrigger, out byte rightTrigger)
        {
            buttons = 0; leftStickX = 0; leftStickY = 0; rightStickX = 0; rightStickY = 0; leftTrigger = 0; rightTrigger = 0;
            bool any = false;
            for (uint i = 0; i < 4; i++)
            {
                var state = new XINPUT_STATE();
                if (XInputGetState(i, ref state) != ERROR_SUCCESS) continue;
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
            return any;
        }
        #endregion
    }
}
