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

        /// <summary>
        /// Draw ROM tiles square instead of the 2:3 capsule the store tabs use.
        ///
        /// Off by default, because most cover art really is 2:3. It exists because Playnite's ROM art
        /// is not: a lot of it is square box scans and icons, and forcing those into a tall tile
        /// either crops the picture or leaves bars down the sides. This is a property of one user's
        /// collection, not something we can detect per game, so it is a setting.
        /// </summary>
        public static bool SquareRomArt
        {
            get => ReadBool("SquareRomArt", false);
            set => WriteBool("SquareRomArt", value);
        }

        /// <summary>
        /// The user's own SteamGridDB API key, or empty.
        ///
        /// NEVER SHIPPED WITH ONE. A key in the repository is a credential in the repository, and it
        /// would be extracted from the exe and burned through by strangers within a week - the quota
        /// is per key, so the first person to abuse it takes the feature away from everybody else.
        /// The user pastes their own, it lives in their own hive, and nothing here works without it.
        /// </summary>
        public static string SteamGridDbApiKey
        {
            get => ReadString("SteamGridDbApiKey", string.Empty);
            set => WriteString("SteamGridDbApiKey", value ?? string.Empty);
        }

        /// <summary>
        /// Open straight into the game library instead of the start screen.
        ///
        /// Off by default: Center is an installer and control panel first, and someone who has just
        /// double-clicked it usually wants the thing they installed it for. Once the machine is set
        /// up that reverses, which is exactly why this is a setting and not a guess.
        /// </summary>
        public static bool OpenLibraryAtStartup
        {
            get => ReadBool("OpenLibraryAtStartup", false);
            set => WriteBool("OpenLibraryAtStartup", value);
        }

        /// <summary>
        /// Let the ClawTweaks helper start Center when it starts.
        ///
        /// READ BY THE HELPER, written here. The two live in different processes and different repos,
        /// and this registry value is the whole contract between them - so the name must not change
        /// without changing it on the helper side as well.
        ///
        /// The helper must launch Center ASYNCHRONOUSLY. Its own job is to have the controller alive
        /// within a second of boot, and a games library is never worth delaying that.
        /// </summary>
        public static bool StartCenterWithClawTweaks
        {
            get => ReadBool("StartCenterWithClawTweaks", false);
            set => WriteBool("StartCenterWithClawTweaks", value);
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

        private static string ReadString(string name, string fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(name) as string ?? fallback;
            }
            catch { return fallback; }
        }

        private static void WriteString(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue(name, value ?? string.Empty, RegistryValueKind.String);
            }
            catch { }
        }
    }
}
