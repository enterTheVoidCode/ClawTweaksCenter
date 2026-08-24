namespace ClawTweaksCenter.Navigation
{
    /// <summary>
    /// The controller buttons the wizard reacts to. Navigation is deliberately NOT a roaming focus
    /// model — instead every on-screen action is bound to a fixed button and shown with its glyph,
    /// so the user always knows exactly which button does what (handheld-friendly).
    /// </summary>
    public enum PadButton
    {
        A,     // primary / confirm action of the current phase
        B,     // back
        X,     // (reserved for a secondary action if a phase needs it)
        Y,     // re-check / refresh
        Menu,  // continue to next phase (blocked until the phase allows it)
        View,  // Select / Back button (XINPUT_GAMEPAD_BACK) - opens the library settings

        // Shoulders and triggers. LB/RB switch the MAIN tabs (Start / Library), LT/RT switch the
        // grouping INSIDE the library. Like the face buttons these are edge-triggered — the triggers
        // are analogue, so XInputNavigator turns them into presses against a threshold rather than
        // reporting a value. A held trigger must produce exactly one group change, not 25 per second.
        LB, RB, LT, RT,

        /// <summary>
        /// Right stick CLICK (XINPUT_GAMEPAD_RIGHT_THUMB).
        ///
        /// Added because the right stick's four directions were already spoken for - sorting on one
        /// axis, grouping on the other - and immersive mode still needed a gesture of its own to
        /// bring the button hints back. A click is not a direction, so it collides with neither.
        ///
        /// NOT rendered as a footer chip: there is no bundled artwork for a stick click, and the one
        /// screen that uses it names it in the hint it replaces.
        /// </summary>
        R3,

        // Discrete D-Pad presses (edge-triggered, like the face buttons above) — NOT rendered as
        // footer chips (no fixed-button glyph makes sense for "move"). Used by screens with a real
        // list/grid selection, e.g. CenterMenuWindow's build picker. Windows that don't bind these
        // simply never see them dispatched (MainWindow's phases never register them).
        Up, Down, Left, Right,
    }
}
