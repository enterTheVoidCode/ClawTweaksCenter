using System;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Center's own handful of remembered preferences.
    ///
    /// HKCU, deliberately — same reasoning as <see cref="SelfInstaller"/>'s uninstall entry: it is the
    /// current user's own hive, so reading and writing needs no administrator rights and Center's
    /// "never elevates" property survives. A file under %LOCALAPPDATA% would do the same job; the
    /// registry is used because the uninstall entry already lives there, so uninstalling can drop both
    /// with one key delete instead of hunting for stray files.
    ///
    /// Every accessor is best-effort: a failed read returns the default and a failed write is dropped.
    /// A preference that cannot be stored must never stop the app from running.
    /// </summary>
    public static class CenterSettings
    {
        private const string KeyPath = @"Software\ClawTweaks\Center";

        /// <summary>
        /// Borderless fullscreen instead of a normal resizable window.
        ///
        /// Defaults to TRUE. Center is driven with a gamepad on a handheld, where a windowed app sits
        /// behind whatever is already fullscreen (Steam Big Picture being the case that prompted this)
        /// and both apps then react to the same stick input. See Ui/WindowMode.cs.
        /// </summary>
        public static bool BorderlessFullscreen
        {
            get => ReadBool("BorderlessFullscreen", true);
            set => WriteBool("BorderlessFullscreen", value);
        }

        /// <summary>Removes everything this class stored. Called from the uninstall path.</summary>
        public static void Clear()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); }
            catch { }
        }

        private static bool ReadBool(string name, bool fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                object raw = key?.GetValue(name);
                if (raw == null) return fallback;
                return Convert.ToInt32(raw) != 0;
            }
            catch { return fallback; }
        }

        private static void WriteBool(string name, bool value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
