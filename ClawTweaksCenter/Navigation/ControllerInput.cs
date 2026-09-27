using System;

namespace ClawTweaksCenter.Navigation
{
    internal readonly record struct ControllerState(ushort Buttons, short LeftX, short LeftY,
        short RightX, short RightY, byte LeftTrigger, byte RightTrigger);

    // Input interpretation stays separate from the driver and dispatcher: polling may block,
    // and foreground ownership can change before the UI gets to act on a sample.
    internal sealed class ControllerInput
    {
        internal event Action<PadButton> ButtonPressed;
        internal event Action<double> ScrollRequested;
        internal event Action<double> RightStickScrollRequested;
        internal event Action<PadButton> RightStickFlicked;
        internal event Action<PadButton> ButtonRepeated;

        private readonly Func<bool> _hasForeground;
        private readonly Func<ControllerState?> _poll;
        // Must complete the callback before returning. The poll thread pauses during UI delivery,
        // so held-state resets and decoding never mutate this state concurrently.
        private readonly Action<Action> _dispatch;
        private readonly Func<DateTime> _utcNow;
        private bool _awaitingNeutral = true;
        private long _focusGeneration;

        internal ControllerInput(Func<bool> hasForeground, Func<ControllerState?> poll,
                                 Action<Action> dispatch, Func<DateTime> utcNow)
        {
            _hasForeground = hasForeground;
            _poll = poll;
            _dispatch = dispatch;
            _utcNow = utcNow;
        }

        private ushort _prevButtons;
        private ushort _prevStickDirBits;
        private ushort _prevRightStickDirBits;
        private ushort _prevTriggerBits;
        private const short StickDeadzone = 12000;

        // AUTO-REPEAT for the four directions. The numbers are the usual key-repeat shape, and each
        // of the three is answering a different complaint:
        //   the delay    long enough that a single press never repeats by accident
        //   the interval one step per ~150 ms, about as fast as a cover grid can be read
        //   the sprint   a library is hundreds of tiles long, and holding down for five seconds to
        //                cross it is what made this a request in the first place
        // Rounded to the 40 ms tick, because that is the resolution this can actually have.
        private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan RepeatSprintAfter = TimeSpan.FromMilliseconds(1600);
        private static readonly TimeSpan RepeatSprintInterval = TimeSpan.FromMilliseconds(80);

        private ushort _prevNavDirBits;
        private readonly DateTime[] _navHeldSince = new DateTime[4];
        private readonly DateTime[] _navLastRepeat = new DateTime[4];
        private static readonly PadButton[] NavDirButtons = { PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right };

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

        internal static bool IsForegroundWindow(IntPtr window, IntPtr foreground)
            => window != IntPtr.Zero && window == foreground;

        internal void PollOnce()
        {
            if (!_hasForeground()) { SuspendInput(); return; }
            var sample = _poll();
            // The driver can take long enough for the user to switch back to the game meanwhile.
            if (!sample.HasValue || !_hasForeground()) { SuspendInput(); return; }
            var (buttons, lx, ly, rx, ry, lt, rt) = sample.Value;

            if (_awaitingNeutral)
            {
                // A button or stick held in the game must not become a new Center action when its
                // window comes back. Use the same deadzones as ordinary input interpretation.
                if (buttons != 0 || Math.Abs((int)lx) > StickDeadzone || Math.Abs((int)ly) > StickDeadzone ||
                    Math.Abs((int)rx) > StickDeadzone || Math.Abs((int)ry) > StickDeadzone ||
                    lt >= TriggerThreshold || rt >= TriggerThreshold) return;
                ForgetHeldInput();
                _awaitingNeutral = false;
                return;
            }

            // Continuous scroll from D-Pad up/down or left-stick Y (fires every tick while held).
            double scroll = 0;
            if ((buttons & XINPUT_GAMEPAD_DPAD_UP) != 0) scroll -= 46;
            if ((buttons & XINPUT_GAMEPAD_DPAD_DOWN) != 0) scroll += 46;
            if (ly > StickDeadzone) scroll -= 46 * (ly / 32767.0);
            if (ly < -StickDeadzone) scroll += 46 * (-ly / 32767.0);
            if (Math.Abs(scroll) > 0.5) Publish(ScrollRequested, scroll);

            // Right-stick Y drives its own independent scroll signal (see RightStickScrollRequested).
            double rscroll = 0;
            if (ry > StickDeadzone) rscroll -= 46 * (ry / 32767.0);
            if (ry < -StickDeadzone) rscroll += 46 * (-ry / 32767.0);
            if (Math.Abs(rscroll) > 0.5) Publish(RightStickScrollRequested, rscroll);

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
            if ((rightPressed & StickDirUp) != 0) Publish(RightStickFlicked, PadButton.Up);
            if ((rightPressed & StickDirDown) != 0) Publish(RightStickFlicked, PadButton.Down);
            if ((rightPressed & StickDirLeft) != 0) Publish(RightStickFlicked, PadButton.Left);
            if ((rightPressed & StickDirRight) != 0) Publish(RightStickFlicked, PadButton.Right);

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

            // Before the early-out below: a D-pad HELD sets no new bit, so "pressed == 0" is the
            // normal state while a direction is being held down - which is the entire case this has
            // to see.
            TickDirectionRepeat((ushort)(buttons & 0x000F), stickDirBits);

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

        /// <summary>
        /// The repeats for a held direction. D-pad and left stick are ONE input here - they mean the
        /// same thing to every screen, and holding one while nudging the other should not restart
        /// the clock.
        ///
        /// It only repeats; the first raise stays where it was, on the edge, so a screen that
        /// ignores repeats keeps exactly the behaviour it had.
        /// </summary>
        private void TickDirectionRepeat(ushort dpadBits, ushort stickBits)
        {
            ushort dirBits = (ushort)(dpadBits | stickBits);
            var now = _utcNow();

            for (int i = 0; i < NavDirButtons.Length; i++)
            {
                ushort mask = (ushort)(1 << i);   // Up, Down, Left, Right - the XInput D-pad order
                bool held = (dirBits & mask) != 0;
                if (!held) { _navHeldSince[i] = default; continue; }

                if ((_prevNavDirBits & mask) == 0)
                {
                    // Freshly pushed: the edge raise has already gone out elsewhere this tick.
                    _navHeldSince[i] = now;
                    _navLastRepeat[i] = now;
                    continue;
                }

                TimeSpan held_for = now - _navHeldSince[i];
                if (held_for < RepeatDelay) continue;

                TimeSpan interval = held_for >= RepeatSprintAfter ? RepeatSprintInterval : RepeatInterval;
                if (now - _navLastRepeat[i] < interval) continue;

                _navLastRepeat[i] = now;
                Publish(ButtonRepeated, NavDirButtons[i]);
            }

            _prevNavDirBits = dirBits;
        }

        /// <summary>Everything held is released the moment we stop seeing the pad - the window lost
        /// focus, or the controller went away. Without this a direction that was held at that moment
        /// would still count as held when it comes back, and repeat immediately.</summary>
        private void ForgetHeldInput()
        {
            _prevButtons = 0;
            _prevStickDirBits = 0;
            _prevRightStickDirBits = 0;
            _prevTriggerBits = 0;
            _prevNavDirBits = 0;
            Array.Clear(_navHeldSince);
            Array.Clear(_navLastRepeat);
        }

        private void Raise(PadButton b) => Publish(ButtonPressed, b);

        private void SuspendInput()
        {
            ForgetHeldInput();
            _awaitingNeutral = true;
            _focusGeneration++;
        }

        private void Publish<T>(Action<T> handler, T value)
        {
            if (handler == null) return;
            long generation = _focusGeneration;
            _dispatch(() =>
            {
                // Production dispatch is synchronous onto the UI thread. Check here, immediately
                // before the subscriber acts, because ownership may change while dispatch waits.
                if (!_hasForeground()) { SuspendInput(); return; }
                if (!_awaitingNeutral && generation == _focusGeneration) handler(value);
            });
        }

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

    }
}
