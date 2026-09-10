using System.Runtime.InteropServices;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Whether the device is on mains.
    ///
    /// -- Why this is not read from the helper --------------------------------------------------
    /// Everything else about the battery on these screens comes from the helper's metrics bundle,
    /// and deliberately so: it resolves the charge and the runtime Windows-first, which is what
    /// makes them work on a Claw 8 EX whose battery exposes no rate sensor at all. The AC line is
    /// the one fact that bundle does not carry, so there is no second answer to disagree with -
    /// and without it, "plugged in and full" and "on battery with no runtime estimate" are the
    /// same absence of information.
    ///
    /// -- It lives here because there were TWO copies ---------------------------------------------
    /// The profile detail page had this P/Invoke, and the library footer was about to grow an
    /// identical one (2026-09-10). Two declarations of the same Win32 call is how the two of them
    /// end up answering 255 differently.
    /// </summary>
    internal static class PowerLine
    {
        /// <summary>
        /// 1 means plugged, 0 means battery, 255 means Windows does not know - and unknown is
        /// answered with FALSE on purpose. Unplugged is this product's primary state, so a screen
        /// that cannot tell shows what a handheld actually runs on.
        /// </summary>
        public static bool OnMains()
        {
            try { return GetSystemPowerStatus(out var status) && status.ACLineStatus == 1; }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}
