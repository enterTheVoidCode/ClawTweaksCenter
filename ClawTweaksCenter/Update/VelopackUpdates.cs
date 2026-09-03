using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace ClawTweaksCenter.Update
{
    /// <summary>
    /// Center's Velopack update path — OFF unless somebody turns it on.
    ///
    /// ── Read this before changing anything here ─────────────────────────────────────────────────
    /// Center deleted its self-updater once already, on purpose. "Download an exe and run it" is the
    /// dropper shape, and checking the bytes afterwards does not change that; what shipped instead
    /// was a notice plus a link. Velopack changes the BUILD SHAPE of that path — signed packages, a
    /// fixed install layout, deltas — but not the fact that a background process fetches code and
    /// starts it. Re-introducing it is therefore a deliberate reversal of a deliberate decision, and
    /// that is why every switch below defaults to the old behaviour.
    ///
    /// ── The whole feature lives in this folder ──────────────────────────────────────────────────
    /// Exactly two things outside it know this exists:
    ///
    ///   1. the Velopack PackageReference in ClawTweaksCenter.csproj
    ///   2. one line at the top of App.OnStartup: Update.VelopackUpdates.Bootstrap();
    ///
    /// Delete the folder, delete those two, and Center is back to where it was. The settings live
    /// here too rather than in CenterSettings for exactly that reason - see REMOVAL.md next door.
    ///
    /// ── What it does NOT do ─────────────────────────────────────────────────────────────────────
    /// It does not install, it does not create shortcuts, it does not write an ARP entry and it does
    /// not touch SelfInstaller. Center still installs itself. That boundary is what keeps the guided
    /// leave screen intact: Windows Settings → Uninstall has to land there, not in an immediate
    /// deletion, and a second uninstall owner is how that gets lost.
    /// </summary>
    public static class VelopackUpdates
    {
        private const string KeyPath = @"Software\ClawTweaks\Center";

        /// <summary>The Center repo's own releases. Updates ship from there, not from the ClawTweaks
        /// repo - and the manifest below is NOT setup-manifest.json, which lives in the other repo
        /// and answers a different question (which APP builds may be installed).</summary>
        private const string DefaultFeedUrl = "https://github.com/enterTheVoidCode/ClawTweaksCenter";

        private const string DefaultManifestUrl =
            "https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaksCenter/master/update-manifest.json";

        private static bool _bootstrapped;

        /// <summary>
        /// The master switch. OFF unless the value exists and is 1.
        ///
        /// Read fresh every time rather than cached: turning it off is the emergency handle, and an
        /// emergency handle that needs a restart to take effect is half a handle.
        /// </summary>
        public static bool Enabled
        {
            get => ReadDword("VelopackUpdates", 0) == 1;
            set => WriteDword("VelopackUpdates", value ? 1 : 0);
        }

        /// <summary>
        /// Allows a silent update to actually apply itself. Separate from <see cref="Enabled"/> on
        /// purpose: checking for updates and installing one without being asked are different
        /// promises, and somebody may want the first without the second.
        /// </summary>
        public static bool SilentUpdatesAllowed
        {
            get => ReadDword("VelopackSilentUpdates", 0) == 1;
            set => WriteDword("VelopackSilentUpdates", value ? 1 : 0);
        }

        /// <summary>
        /// A local folder to read releases from instead of GitHub, for rehearsing the whole cycle
        /// offline.
        ///
        /// This is the answer to "can the release cycle be simulated locally, repeatedly, without
        /// GitHub releases" - yes: `vpk pack --outputDir C:\feed` writes a complete feed into a
        /// folder and SimpleFileSource reads one, so N → N+1 → N+2 can be rehearsed as often as
        /// needed. It is also the ONLY way to practise the multi-hop path: against real releases
        /// every attempt costs a public artifact that cannot be taken back.
        ///
        /// ⚠️ A local feed does NOT stand in for a real one. The folder carries no Mark of the Web,
        /// so it says nothing about how AV reacts to a downloaded-and-started update, nothing about
        /// the HTTPS certificate chain, and nothing about a connection dropping mid-download.
        /// </summary>
        public static string FeedOverride
        {
            get => ReadString("VelopackFeedOverride", null);
            set => WriteString("VelopackFeedOverride", value);
        }

        /// <summary>Where the essential-version manifest is read from. Overridable for the same
        /// rehearsal reason as the feed - a local path works.</summary>
        public static string ManifestOverride
        {
            get => ReadString("VelopackManifestOverride", null);
            set => WriteString("VelopackManifestOverride", value);
        }

        /// <summary>
        /// Velopack's entry-point hook. MUST be the first thing that runs, before any window and
        /// before any other startup work.
        ///
        /// It is what handles the install/update/uninstall callbacks Velopack's own Update.exe makes
        /// into this binary. On a Center that was NOT installed by Velopack there is no such callback
        /// and this returns immediately - which is what makes calling it unconditionally safe, and
        /// why it sits outside the <see cref="Enabled"/> check. Gating it would mean a machine that
        /// once had the feature on, and then turned it off, could no longer complete a hook that was
        /// already in flight.
        ///
        /// Every failure is swallowed. This runs before the crash logger is wired and before the
        /// first window; an updater that cannot start must never be the reason Center does not.
        /// </summary>
        public static void Bootstrap(string[] args = null)
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            try
            {
                VelopackApp.Build()
                    // FAST CALLBACK, and the name matters: per Velopack's own docs, a FastCallback
                    // hook runs the given code and then Velopack calls Exit() itself. Without a hook
                    // registered here, a launch carrying one of these transient arguments (install,
                    // update, uninstall) falls straight through into Center's normal startup - the
                    // self-install gate, the single-instance gate, the tray icon, a full window - and
                    // THAT is what showed up as "Center opens mid-setup", uninvited, when Velopack's
                    // own Setup.exe launched the freshly installed exe with --veloapp-install.
                    // Measured 2026-09-03: an 18-second gap in the installer log between "Velopack
                    // Center installed" and the next installer line, exactly the width of a user
                    // noticing an unexpected window and closing it by hand.
                    //
                    // Logging only. There is nothing else useful to do at an install/update/uninstall
                    // moment before Center's own settings and windows exist - and Velopack terminates
                    // the process right after this returns regardless of what runs here.
                    .OnAfterInstallFastCallback(v => Log("veloapp hook: after install " + v))
                    .OnAfterUpdateFastCallback(v => Log("veloapp hook: after update " + v))
                    .OnBeforeUpdateFastCallback(v => Log("veloapp hook: before update " + v))
                    .OnBeforeUninstallFastCallback(v => Log("veloapp hook: before uninstall " + v))
                    .Run();
            }
            catch (Exception ex) { Log("bootstrap failed: " + ex.Message); }

            if (args != null && Array.Exists(args, a =>
                    a.Equals(SelfTestArg, StringComparison.OrdinalIgnoreCase)))
                RunSelfTest();
        }

        /// <summary>The rehearsal entry point. Handled here rather than in App so that removing this
        /// folder removes it too - see REMOVAL.md.</summary>
        public const string SelfTestArg = "--velopack-selftest";

        /// <summary>
        /// Runs one whole cycle - check, decide, apply - and ends the process.
        ///
        /// ⚠️ It exits BEFORE Center's own startup, on purpose. Everything after this point in
        /// App.OnStartup assumes a user is looking: the self-install gate opens a window, the
        /// single-instance gate can hand the launch to a resident Center, and the tray icon appears.
        /// A rehearsal that triggered any of that would be testing Center, not the updater.
        ///
        /// It exists because nothing in the UI calls the updater yet, and a release cycle that can
        /// only be exercised by hand is one that gets exercised once.
        /// </summary>
        private static void RunSelfTest()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly()
                                   .GetName().Version?.ToString();
            Log($"--- self-test: running {version}, enabled={Enabled}, silent={SilentUpdatesAllowed} ---");

            try
            {
                var update = CheckAsync().GetAwaiter().GetResult();
                if (update == null)
                {
                    Log("self-test: no update offered");
                }
                else
                {
                    string target = update.TargetFullRelease?.Version?.ToString();
                    bool silent = ShouldUpdateSilently(update).GetAwaiter().GetResult();
                    Log($"self-test: offered {target}, silent={silent}");

                    // Only the silent verdict applies anything. A rehearsal must exercise the SAME
                    // gate the product uses, or it proves nothing about the product.
                    if (silent) ApplyAsync(update).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { Log("self-test threw: " + ex); }

            Log("--- self-test done ---");
            Environment.Exit(0);
        }

        /// <summary>
        /// Looks for a newer release. Returns null when there is none, when the feature is off, or
        /// when this Center is not a Velopack installation.
        ///
        /// ⚠️ THAT LAST CASE IS THE NORMAL ONE TODAY, and it is a build-shape limit rather than a
        /// bug: Velopack updates by replacing the folder IT owns
        /// (%LOCALAPPDATA%\{packId}\{current, packages, Update.exe}). A Center that SelfInstaller put
        /// in place is, to UpdateManager, simply not installed - it throws NotInstalledException.
        /// "Update only, never install" is therefore a contradiction at the mechanism level, and
        /// resolving it is the W1-versus-W2 decision in PLAN_Velopack_Updates.md §3 that has not been
        /// taken. Until it is, this returns null on a normal install and the feature is inert.
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            if (!Enabled) return null;

            try
            {
                var mgr = CreateManager();
                return mgr == null ? null : await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // NotInstalledException lands here too, and deliberately reads as "no update" rather
                // than as an error: on today's installs it is the expected answer, and surfacing it
                // would put a permanent fault in front of a user who has nothing to fix.
                Log("check failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Downloads and applies an update, then restarts Center.
        ///
        /// ⚠️ THIS DOES NOT RETURN. ApplyUpdatesAndRestart ends the process.
        ///
        /// Never call it from a path the user cannot see. It is the reason
        /// <see cref="SilentUpdatesAllowed"/> exists as its own switch, and the reason
        /// <see cref="ShouldUpdateSilently"/> fails CLOSED.
        /// </summary>
        public static async Task<bool> ApplyAsync(UpdateInfo update)
        {
            if (!Enabled || update == null) return false;

            try
            {
                var mgr = CreateManager();
                if (mgr == null) return false;

                await mgr.DownloadUpdatesAsync(update).ConfigureAwait(false);
                Log("applying " + update.TargetFullRelease?.Version + " and restarting");
                mgr.ApplyUpdatesAndRestart(update);
                return true;
            }
            catch (Exception ex)
            {
                Log("apply failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Whether this particular version may install itself without asking.
        ///
        /// ── Why a manifest and not a flag on the release ────────────────────────────────────────
        /// Velopack answers exactly one question: "is there something newer". It has no notion of an
        /// urgent version. The marking has to be carried by something we own, and a small file in
        /// the Center repo is the smallest such thing - it can be changed after a release has gone
        /// out, which a baked-in flag cannot.
        ///
        /// ── ⚠️ FAILS CLOSED, and this is the opposite of setup-manifest.json ────────────────────
        /// That file fails OPEN on purpose: offline, missing field, unparsable version ⇒ installable,
        /// because locking somebody out for having no network is worse than letting an old build
        /// through. Here the asymmetry runs the other way: the failure mode of guessing wrong is
        /// restarting somebody's app underneath them. No manifest, no answer, no silent update.
        /// </summary>
        public static async Task<bool> ShouldUpdateSilently(UpdateInfo update)
        {
            if (!Enabled || !SilentUpdatesAllowed || update == null) return false;

            string version = update.TargetFullRelease?.Version?.ToString();
            if (string.IsNullOrWhiteSpace(version)) return false;

            var manifest = await UpdateManifest.FetchAsync(ManifestOverride ?? DefaultManifestUrl)
                                               .ConfigureAwait(false);
            if (manifest == null)
            {
                Log("no manifest - not updating silently");
                return false;
            }

            bool essential = manifest.IsEssential(version);
            Log($"manifest says {version} is " + (essential ? "essential" : "not essential"));
            return essential;
        }

        /// <summary>
        /// Builds an UpdateManager over the configured source.
        ///
        /// A path in <see cref="FeedOverride"/> becomes a SimpleFileSource; otherwise the Center
        /// repo's GitHub releases. Prereleases are excluded - a test build must never reach somebody
        /// who only ever installed a stable one.
        /// </summary>
        private static UpdateManager CreateManager()
        {
            string local = FeedOverride;
            if (!string.IsNullOrWhiteSpace(local))
            {
                if (!Directory.Exists(local))
                {
                    Log("feed override points nowhere: " + local);
                    return null;
                }
                Log("using local feed " + local);
                return new UpdateManager(new SimpleFileSource(new DirectoryInfo(local)));
            }

            return new UpdateManager(new GithubSource(DefaultFeedUrl, accessToken: null, prerelease: false));
        }

        // ── Settings plumbing, kept here so removing the folder removes the settings too ──────────

        private static int ReadDword(string name, int fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key?.GetValue(name) is int i ? i : fallback;
            }
            catch { return fallback; }
        }

        private static void WriteDword(string name, int value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key?.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        private static string ReadString(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key?.GetValue(name) as string ?? fallback;
            }
            catch { return fallback; }
        }

        private static void WriteString(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (string.IsNullOrEmpty(value)) key?.DeleteValue(name, false);
                    else key?.SetValue(name, value, RegistryValueKind.String);
                }
            }
            catch { }
        }

        /// <summary>
        /// One line per event, next to Center's other logs.
        ///
        /// Not Debug-level and not silent: an update path that fails quietly is indistinguishable
        /// from one that was never switched on, and this project has paid for that confusion in
        /// several other subsystems already.
        /// </summary>
        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClawTweaks Center");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "velopack.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
