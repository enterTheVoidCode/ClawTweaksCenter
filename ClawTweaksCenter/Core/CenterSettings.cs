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
    /// <summary>
    /// What happens to the Center window after a game starts. Stored as its ordinal, so entries may
    /// only ever be APPENDED - an existing installation carries the number, not the name.
    /// </summary>
    public enum LaunchBehavior
    {
        /// <summary>Exit. The default, and the only one that frees the memory Center is holding.</summary>
        Close,
        /// <summary>Minimise and stay running, so coming back is instant and the library is already
        /// scanned.</summary>
        Minimize,
        /// <summary>Leave the window as it is, behind the game.</summary>
        StayOpen,
    }

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
        /// <summary>
        /// Hold the library behind a blur until the VIRTUAL controller is usable.
        ///
        /// OFF by default, and it has to be opt-in rather than clever: the wait is only right for a
        /// machine that boots straight into the library with the virtual pad as its standard, and on
        /// any other setup an overlay between the user and their games is a regression. The user asks
        /// for it or it does not happen.
        ///
        /// It does nothing at all in hardware-controller mode - there is no mount to wait for - and
        /// nothing when the pad is already up, which is the normal case for a Center started by hand.
        /// </summary>
        public static bool WaitForVirtualController
        {
            get => ReadBool("WaitForVirtualController", false);
            set => WriteBool("WaitForVirtualController", value);
        }

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

        /// <summary>
        /// What Center does once a game has been started.
        ///
        /// Both of the non-closing options are safe, which is worth writing down because the launch
        /// path used to say otherwise. The XInput poller returns immediately unless the window is
        /// active (XInputNavigator.OnTick), so a background Center does NOT fight the game for the
        /// sticks - and the window is not topmost, so it cannot sit over one either.
        /// </summary>
        public static LaunchBehavior LaunchBehavior
        {
            get
            {
                int raw = ReadInt("LaunchBehavior", (int)LaunchBehavior.Close);
                return raw >= 0 && raw <= (int)LaunchBehavior.StayOpen ? (LaunchBehavior)raw : LaunchBehavior.Close;
            }
            set => WriteInt("LaunchBehavior", (int)value);
        }

        /// <summary>
        /// Keep running in the tray instead of exiting - see Library/GameRunTracker and
        /// CenterMenuWindow.Tray.cs.
        ///
        /// Off by default: a resident background process is exactly the kind of thing a user should
        /// opt into, not discover. Once on, it governs EVERY way Center would otherwise fully exit
        /// (the titlebar X, the Home/hand-off screens' "Exit" action, and - deliberately - even
        /// LaunchBehavior.Close after starting a game): the point of the setting is "always resident
        /// for an instant reopen from ClawTweaks", and an explicit game-launch close silently
        /// bypassing that would undo the very thing the user turned on.
        /// </summary>
        /// <summary>
        /// Whether the library's info screen has been shown once. It opens by itself the first time
        /// the library is opened and never again on its own.
        ///
        /// Remembered rather than shown every time BECAUSE it is the answer to questions asked once:
        /// where covers come from, and why some are missing. A panel that reappears on every visit
        /// is a panel people learn to dismiss without reading, which costs exactly the users who
        /// have not set a key yet.
        /// </summary>
        /// <summary>
        /// Immersive mode: the library dims its own furniture once the user stops touching anything.
        ///
        /// OPT IN, and it stays that way. It hides the footer, which is where every button on the
        /// screen is named - useful once you know the library, and a dead end on the first visit.
        /// </summary>
        /// <summary>Z-A instead of A-Z. One bool rather than an enum: there are two orders, and an
        /// enum with two members is a bool that needs a migration when a third never arrives.</summary>
        public static bool LibrarySortDescending
        {
            get => ReadBool("LibrarySortDescending", false);
            set => WriteBool("LibrarySortDescending", value);
        }

        /// <summary>Group the flat tabs by where the game came from. Only some tabs can group at all
        /// (see CenterMenuWindow.GroupingKind); the setting is remembered for all of them together,
        /// because "grouped" is a habit, not a per-tab decision.</summary>
        public static bool LibraryGrouped
        {
            get => ReadBool("LibraryGrouped", true);
            set => WriteBool("LibraryGrouped", value);
        }

        public static bool ImmersiveMode
        {
            get => ReadBool("ImmersiveMode", false);
            set => WriteBool("ImmersiveMode", value);
        }

        public static bool LibraryInfoSeen
        {
            get => ReadBool("LibraryInfoSeen", false);
            set => WriteBool("LibraryInfoSeen", value);
        }

        /// <summary>
        /// Start Steam, silently, when the library opens.
        ///
        /// WHAT IT BUYS: the first Steam game of a session otherwise waits for a cold client, and a
        /// cold client started by the steam:// handler comes up with its full window in front of the
        /// game. The launch path already prewarms it (GameLibrary.PrewarmSteamIfNeeded); doing it
        /// when the library opens moves that wait to a moment where nobody is waiting on it.
        ///
        /// OFF BY DEFAULT, and it stays that way. Starting somebody else's application on their
        /// behalf is a thing to opt into, not to discover - and on a machine with no Steam games it
        /// would be pure cost.
        ///
        /// "-silent" is Steam's own switch for coming up in the tray only. It is what keeps this out
        /// of the way of the library it was started from.
        /// </summary>
        public static bool StartSteamWithLibrary
        {
            get => ReadBool("StartSteamWithLibrary", false);
            set => WriteBool("StartSteamWithLibrary", value);
        }

        public static bool RunInBackground
        {
            get => ReadBool("RunInBackground", false);
            set => WriteBool("RunInBackground", value);
        }

        /// <summary>
        /// The interface language, as the user chose it - including "follow the OS", which is the
        /// default and what a fresh installation runs on.
        ///
        /// STORED AS THE NAME, not as the ordinal, unlike LaunchBehavior above. The two are stored
        /// differently on purpose: LaunchBehavior has three members that will never be reordered,
        /// whereas the language list is expected to grow and would most naturally grow in
        /// alphabetical order - and an ordinal moves silently when a member is inserted, turning
        /// somebody's German into French on an update. A name cannot do that, and an unknown name
        /// falls back to System, which is the right answer for a language we no longer ship.
        /// </summary>
        public static UiLanguage Language
        {
            get
            {
                string raw = ReadString("Language", string.Empty);
                return Enum.TryParse(raw, out UiLanguage parsed) && Enum.IsDefined(typeof(UiLanguage), parsed)
                    ? parsed
                    : UiLanguage.System;
            }
            set => WriteString("Language", value.ToString());
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

        private static int ReadInt(string name, int fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                object raw = key?.GetValue(name);
                return raw == null ? fallback : Convert.ToInt32(raw);
            }
            catch { return fallback; }
        }

        private static void WriteInt(string name, int value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue(name, value, RegistryValueKind.DWord);
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
