using System;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// WHAT THE INNO SETUP WAS TOLD, read straight from the registry it writes.
    ///
    /// Since 0.4.0.60 the setup asks whether the virtual controller and RTSS are wanted when their
    /// tools are missing, and records the answers under HKLM\SOFTWARE\ClawTweaks\Setup. Center
    /// reads them for one reason: an onboarding that walks somebody through "Enable virtual
    /// controller" on a machine that deliberately has no HidHide and no usbip is asking them to
    /// switch on something that cannot start, with a button the helper refuses.
    ///
    /// ⚠️ THREE STATES. null means the question was never asked - every installation from before
    /// 0.4.0.60, and every upgrade on a machine that already had the tools. Reading null as "no"
    /// would hide the step from people who never declined anything, so callers fall back to what
    /// the machine actually has (ToolDetect) instead.
    ///
    /// ⚠️ Not cached beyond the process. Values only change when a setup runs, and a setup closes
    /// Center first.
    /// </summary>
    public static class SetupToolChoices
    {
        private const string KeyPath = @"SOFTWARE\ClawTweaks\Setup";

        private static readonly object Sync = new object();
        private static bool _loaded;
        private static bool? _virtualController;
        private static bool? _rtss;

        public static bool? VirtualControllerWanted { get { Load(); return _virtualController; } }

        public static bool? RtssWanted { get { Load(); return _rtss; } }

        /// <summary>Forget the cached read. Only needed if a setup ran while Center stayed open.</summary>
        public static void Invalidate()
        {
            lock (Sync) { _loaded = false; }
        }

        private static void Load()
        {
            lock (Sync)
            {
                if (_loaded) return;
                _loaded = true;
                _virtualController = null;
                _rtss = null;

                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = baseKey.OpenSubKey(KeyPath))
                    {
                        if (key == null) return;
                        _virtualController = ReadBool(key, "VirtualControllerWanted");
                        _rtss = ReadBool(key, "RtssWanted");
                    }
                }
                catch
                {
                    // Fails to "never asked", which is the safe direction: the caller then reads the
                    // machine, exactly as it did before this file existed.
                }
            }
        }

        private static bool? ReadBool(RegistryKey key, string name)
        {
            object raw = key.GetValue(name, null);
            if (raw == null) return null;
            try { return Convert.ToInt32(raw) != 0; }
            catch { return null; }
        }
    }
}
