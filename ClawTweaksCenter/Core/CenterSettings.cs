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
        /// Center is the Windows full-screen experience start app - the thing the shell boots into
        /// instead of the desktop. Read ONCE per process from the same two registry values
        /// FseHelperStart.IsFseStartApp reads; Windows starts Center itself in that mode, so a change
        /// of the choice always comes with a fresh process.
        ///
        /// ── WHAT IT CHANGES, AND WHY THOSE FOUR ─────────────────────────────────────────────────
        /// In FSE there is no desktop, no tray and no taskbar behind Center. A window that hides or
        /// minimises itself there does not go "to the tray" - it goes to nowhere the user can reach,
        /// and the library is simply gone (user, 2026-09-16). So four settings stop being choices:
        ///
        ///   RunInBackground           always ON  - the process must survive every "close"
        ///   LaunchBehavior            never Close - a game start must not end the process
        ///   OpenLibraryAtStartup      always ON  - the library IS the home screen
        ///   StartCenterWithClawTweaks always OFF - the shell starts Center, the helper need not
        ///
        /// The getters below return the forced value in FSE and the stored one otherwise; the stored
        /// value is left untouched, so leaving FSE brings the user's own choice back. The settings
        /// screen shows the four rows greyed out with the forced value - a switch that flips and
        /// changes nothing would read as broken.
        /// </summary>
        public static bool FseMode
        {
            get
            {
                if (_fseMode == null) _fseMode = FseHelperStart.IsFseStartApp(out _);
                return _fseMode.Value;
            }
        }
        private static bool? _fseMode;

        /// <summary>
        /// The one-time hint outside FSE that Center can be the full-screen start app has been
        /// posted. A flag rather than the notification key alone: read notifications are dropped
        /// after thirty days, and the key would then let the hint come back.
        /// </summary>
        public static bool FseHintPosted
        {
            get => ReadBool("FseHintPosted", false);
            set => WriteBool("FseHintPosted", value);
        }

        /// <summary>
        /// Backups and the full reset take Center's own data with them - library, Center settings,
        /// the user's cover picks (Core/CenterDataBackup.cs). OFF by default and remembered once
        /// ticked (user, 2026-09-16): the widget-only backup is what everyone had until now, and the
        /// automatic safety copies before a reset follow this switch too.
        /// </summary>
        public static bool BackupIncludesCenter
        {
            get => ReadBool("BackupIncludesCenter", false);
            set => WriteBool("BackupIncludesCenter", value);
        }

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
        /// Draw the mirrored covers under the Recent reel.
        ///
        /// ON by default - it is what makes Recent read as a shelf rather than a row - but it is a
        /// VisualBrush per tile and squarely a matter of taste, and both are reasons for a switch
        /// rather than a constant. Turning it off gives the covers the height the mirror was using.
        /// </summary>
        public static bool RecentReflections
        {
            get => ReadBool("RecentReflections", true);
            set => WriteBool("RecentReflections", value);
        }

        /// <summary>
        /// Let the apps added by hand (My Apps) onto the Recent reel, by their last start from the
        /// library. OFF by default: My Apps are mostly tools, and Recent is meant to hold games.
        /// </summary>
        public static bool ShowOwnAppsInRecent
        {
            get => ReadBool("ShowOwnAppsInRecent", false);
            set => WriteBool("ShowOwnAppsInRecent", value);
        }

        /// <summary>
        /// A folder of the user's own pictures, used as a source of cover art and of the Center
        /// background. Empty until they name one.
        ///
        /// ONE FOLDER, NAMED ONCE, AND READ RECURSIVELY. Center is a gamepad surface: a Windows file
        /// dialog is a mouse, and asking for one every time somebody wants a different cover is the
        /// part that would not get used. A standing folder turns it into a grid of pictures the
        /// D-pad already walks. See Library/UserImageLibrary.cs.
        /// </summary>
        public static string UserImageFolder
        {
            get => ReadString("UserImageFolder", string.Empty);
            set => WriteString("UserImageFolder", value ?? string.Empty);
        }

        /// <summary>
        /// A picture drawn behind the whole Center window, or empty for the flat background.
        ///
        /// It holds a path INSIDE Center's own art cache, never the file the user picked: the picture
        /// is copied there when it is chosen, so emptying Downloads later cannot take the background
        /// away. A path that no longer exists is treated as "no background" rather than drawn as a
        /// blank - see CenterMenuWindow.UserArt.cs.
        /// </summary>
        public static string BackgroundImagePath
        {
            get => ReadString("BackgroundImagePath", string.Empty);
            set => WriteString("BackgroundImagePath", value ?? string.Empty);
        }

        /// <summary>
        /// Which of Center's own default backgrounds has already been handed out, so it happens
        /// EXACTLY ONCE.
        ///
        /// NOT "is BackgroundImagePath empty". That question cannot tell somebody who has never seen
        /// a background from somebody who chose "No background" on purpose - and the second of those
        /// is a decision we would overrule on every start. A stamp answers it: the seed runs only
        /// while this is behind, and it writes the stamp whether or not it put a picture in place.
        /// Same shape as the widget's versioned defaults migrations, for the same reason.
        /// </summary>
        public static int BackgroundSeedVersion
        {
            get => ReadInt("BackgroundSeedVersion", 0);
            set => WriteInt("BackgroundSeedVersion", value);
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
            get => FseMode || ReadBool("OpenLibraryAtStartup", false);   // forced on in FSE, see FseMode
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
            // Forced OFF in FSE (see FseMode). The HELPER reads the registry value directly, not this
            // getter, so it may still launch a second Center at boot - the single-instance gate turns
            // that into a raise of the running window, which is harmless. This getter is what the
            // settings screen shows.
            get => !FseMode && ReadBool("StartCenterWithClawTweaks", false);
            set => WriteBool("StartCenterWithClawTweaks", value);
        }

        /// <summary>
        /// Onboarding has not been shown yet on this machine.
        ///
        /// WRITTEN BY THE INSTALLER, cleared here once onboarding is actually on screen. Like
        /// StartCenterWithClawTweaks above, this registry value is a contract between two programs
        /// in two repos, so the name must not change on one side alone.
        ///
        /// ── WHY A STATE AND NOT AN ARGUMENT ──────────────────────────────────────────────────
        /// The installer used to hand onboarding over as `--onboarding`, on a post-restart RunOnce.
        /// That only works if the process it starts is the FIRST Center of the session, and it is
        /// not: measured 2026-09-07, the Center that came up after the restart carried
        ///
        ///     "…\ClawTweaksCenter\current\CTW_Center.exe"      - no arguments at all
        ///
        /// because AnyFSE had already launched it as the full-screen home app. The RunOnce copy then
        /// met the single-instance gate, where --onboarding signals CommandShowHome, and onboarding
        /// was never shown. Nothing failed; it just did not happen.
        ///
        /// An argument is an instruction to ONE process, and on a machine with a shell launcher we
        /// do not get to decide which process that is. A state is read by whichever Center starts.
        ///
        /// ⚠️ CLEARED WHEN SHOWN, not when finished - see CenterMenuWindow. Clearing it on
        /// completion would re-open onboarding on every start until the user walks it to the end,
        /// which is a worse failure than missing it once.
        /// </summary>
        public static bool OnboardingPending
        {
            get => ReadBool("OnboardingPending", false);
            set => WriteBool("OnboardingPending", value);
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
                var stored = raw >= 0 && raw <= (int)LaunchBehavior.StayOpen ? (LaunchBehavior)raw : LaunchBehavior.Close;
                // Never Close in FSE (see FseMode): the process is the home screen, and a game start
                // that ended it would leave the session with no launcher. Minimize keeps the running
                // screen's restore-on-exit path intact.
                return FseMode && stored == LaunchBehavior.Close ? LaunchBehavior.Minimize : stored;
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

        /// <summary>One column MORE THAN THE DEFAULT in the library grid, covers scaled down to match.
        ///
        /// Relative, never an absolute count: the column number is DERIVED from the window width
        /// (MeasureGridMetrics), so "7" would only be true on the window size it was written for.
        /// On the Claw's panel the default lands on 6 and this makes it 7.
        ///
        /// Off by default. The default itself became one step denser on 2026-09-04 after the user saw
        /// it on device - so "off" today is what "on" was yesterday, and this switch now goes one
        /// further still.
        ///
        /// The reel (Recent) is unaffected by construction: it takes its size from the HEIGHT and sets
        /// its own column count, so it never reaches this.</summary>
        public static bool DenseLibraryGrid
        {
            get => ReadBool("DenseLibraryGrid", false);
            set => WriteBool("DenseLibraryGrid", value);
        }

        /// <summary>
        /// The library tab strip, as the user arranged it: order and which tabs are hidden, in ONE
        /// line. See <see cref="Library.LibraryTabs"/> for the format and for why it is one value
        /// rather than an order plus a hidden list.
        ///
        /// Empty by default, which means "every tab, in declaration order" - so an installation that
        /// never opens the editor behaves exactly as it did before this setting existed.
        /// </summary>
        public static string LibraryTabs
        {
            get => ReadString("LibraryTabs", string.Empty);
            set => WriteString("LibraryTabs", value ?? string.Empty);
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
            get => FseMode || ReadBool("RunInBackground", false);   // forced on in FSE, see FseMode
            set => WriteBool("RunInBackground", value);
        }

        /// <summary>
        /// Short sounds for navigating the library, confirming, going back and starting a game.
        /// See Audio/UiSounds.cs.
        ///
        /// On by default: a pad interface is where a click per move is expected, and without sound
        /// files it is silent anyway.
        /// </summary>
        public static bool InterfaceSounds
        {
            get => ReadBool("InterfaceSounds", true);
            set => WriteBool("InterfaceSounds", value);
        }

        /// <summary>
        /// Which sound set the navigation sound uses, and the one for going back.
        ///
        /// STORED AS THE NAME, like Language and for the same reason: the list of sets is expected
        /// to grow, and an ordinal moves silently when one is inserted. An unknown name reads as the
        /// default set (UiSounds.Normalize), so a hand-edited value cannot turn a sound off.
        /// </summary>
        public static string NavigateSound
        {
            get => ReadString("NavigateSound", Audio.UiSounds.DefaultVariant);
            set => WriteString("NavigateSound", value);
        }

        /// <summary>The set behind the B sound. See <see cref="NavigateSound"/>.</summary>
        public static string BackSound
        {
            get => ReadString("BackSound", Audio.UiSounds.DefaultVariant);
            set => WriteString("BackSound", value);
        }

        /// <summary>
        /// Music while the library is on screen. Off by default - music is something to switch on,
        /// not something to find playing.
        /// </summary>
        public static bool BackgroundMusic
        {
            get => ReadBool("BackgroundMusic", false);
            set => WriteBool("BackgroundMusic", value);
        }

        /// <summary>Loudness of the interface sounds, 0..100. Clamped on read, so a hand-edited value
        /// cannot blow past full scale.
        ///
        /// 48, a fifth below the 60 this shipped with (user, 2026-09-12). Off the round-number grid
        /// on purpose - the volume rows snap to it, so the first press either way lands on 40 or 60
        /// and nobody is stuck stepping 48 / 58 / 68.</summary>
        public static int EffectsVolume
        {
            get => Math.Clamp(ReadInt("EffectsVolume", 48), 0, 100);
            set => WriteInt("EffectsVolume", Math.Clamp(value, 0, 100));
        }

        /// <summary>Loudness of the background music, 0..100. Lower than the effects by default: it
        /// runs under everything for as long as the library is open. 28, a fifth below the 35 this
        /// shipped with (user, 2026-09-12).</summary>
        public static int MusicVolume
        {
            get => Math.Clamp(ReadInt("MusicVolume", 28), 0, 100);
            set => WriteInt("MusicVolume", Math.Clamp(value, 0, 100));
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

        // ── How often Center looks for updates, and how often it says so ────────────────────────
        //
        // THREE SEPARATE INTERVALS, one per source (user, 2026-09-13). They are genuinely different
        // questions: the widget list is already fetched at every start, so its interval decides how
        // often a FINDING becomes a notification; the driver check and the Windows Update search do
        // not run at all on their own, so theirs decide whether the check happens.
        //
        // Stored in WEEKS, 1-4, with 0 meaning off. ⚠️ The user asked for 1, 2, 3 and 4 weeks and did
        // not ask for an off switch - it is here because a background check that reaches the network
        // and cannot be turned off is not a setting, it is a behaviour. Say so if it should go.
        public const int IntervalOff = 0;
        public const int IntervalMinWeeks = 1;
        public const int IntervalMaxWeeks = 4;

        /// <summary>Weeks between driver checks in the background. 0 = never.</summary>
        public static int DriverCheckIntervalWeeks
        {
            get => ClampInterval(ReadInt("DriverCheckIntervalWeeks", 1));
            set => WriteInt("DriverCheckIntervalWeeks", ClampInterval(value));
        }

        /// <summary>Weeks between Windows Update searches in the background. 0 = never.
        ///
        /// ⚠️ The one that actually costs something: measured 13.6 s and 29.1 s against Microsoft's
        /// servers. Everything about when it may run is in the caller, not here.</summary>
        public static int WindowsUpdateCheckIntervalWeeks
        {
            get => ClampInterval(ReadInt("WindowsUpdateCheckIntervalWeeks", 1));
            set => WriteInt("WindowsUpdateCheckIntervalWeeks", ClampInterval(value));
        }

        /// <summary>Weeks between widget-update NOTIFICATIONS. The list itself is fetched at every
        /// start either way, so this throttles the message and not the search. 0 = never.</summary>
        public static int WidgetUpdateNotifyIntervalWeeks
        {
            get => ClampInterval(ReadInt("WidgetUpdateNotifyIntervalWeeks", 1));
            set => WriteInt("WidgetUpdateNotifyIntervalWeeks", ClampInterval(value));
        }

        // ── FseStartsHelper: REMOVED FROM THE INTERFACE AND DISCONNECTED 2026-09-15 ──────────────
        //
        // Whether Center asked the helper's scheduled task to run when Windows booted into the full
        // screen experience and Center was the home app (Core/FseHelperStart.cs). It was the only
        // row under an "Experimental" heading in Center's settings screen.
        //
        // It is gone because it was measured and did nothing: across four boots on 2026-09-14 the
        // logon trigger fired at about +16.7s and Center ran at about +21.7s, so the scheduler
        // refused every request as a duplicate (event 322). The startup problem it was aimed at was
        // solved in the scheduled task instead — Doku/TODO_Scheduled_Task_Fast_Controller.md in the
        // helper repo.
        //
        // THE STORED VALUE IS DELIBERATELY NOT CLEANED UP. A registry value nobody reads costs
        // nothing, and a migration that deletes it would be new code written to undo an experiment
        // that was off by default anyway. Anyone re-enabling this reads the same name back.
        //
        // public static bool FseStartsHelper
        // {
        //     get => ReadBool("FseStartsHelper", false);
        //     set => WriteBool("FseStartsHelper", value);
        // }

        /// <summary>Whether widget TEST builds count as something worth a notification. Off: a test
        /// build is an invitation to help, not an update somebody is waiting for.</summary>
        public static bool WidgetNotifyTestBuilds
        {
            get => ReadBool("WidgetNotifyTestBuilds", false);
            set => WriteBool("WidgetNotifyTestBuilds", value);
        }

        // The last time each source was actually checked. Stored as an ISO-8601 UTC string: a DWORD
        // cannot hold a date, and a local-time string would jump an hour twice a year and let a
        // weekly check fire early or late for no visible reason.
        public static DateTime? DriverCheckLastUtc
        {
            get => ReadUtc("DriverCheckLastUtc");
            set => WriteUtc("DriverCheckLastUtc", value);
        }

        public static DateTime? WindowsUpdateCheckLastUtc
        {
            get => ReadUtc("WindowsUpdateCheckLastUtc");
            set => WriteUtc("WindowsUpdateCheckLastUtc", value);
        }

        public static DateTime? WidgetNotifyLastUtc
        {
            get => ReadUtc("WidgetNotifyLastUtc");
            set => WriteUtc("WidgetNotifyLastUtc", value);
        }

        /// <summary>True when <paramref name="last"/> is longer ago than the interval - and when the
        /// interval is off, always false.
        ///
        /// A last-check time in the FUTURE also counts as due. That is not hypothetical: the clock
        /// moves backwards after a CMOS reset or a timezone fix, and a stored future date would
        /// otherwise silence the check until it caught up.</summary>
        public static bool IsCheckDue(DateTime? last, int intervalWeeks)
        {
            if (intervalWeeks <= IntervalOff) return false;
            if (last == null) return true;
            var now = DateTime.UtcNow;
            if (last > now) return true;
            return now - last.Value >= TimeSpan.FromDays(7.0 * intervalWeeks);
        }

        private static int ClampInterval(int weeks) =>
            weeks <= IntervalOff ? IntervalOff : Math.Min(weeks, IntervalMaxWeeks);

        private static DateTime? ReadUtc(string name)
        {
            string raw = ReadString(name, null);
            if (string.IsNullOrEmpty(raw)) return null;
            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : (DateTime?)null;
        }

        private static void WriteUtc(string name, DateTime? value) =>
            WriteString(name, value?.ToUniversalTime().ToString("o") ?? string.Empty);

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
