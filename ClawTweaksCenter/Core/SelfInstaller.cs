using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Installs CTW_Center.exe itself as a regular Windows app (install folder + Start Menu shortcut +
    /// Add/Remove Programs entry) instead of running as a portable exe from wherever it was extracted.
    /// This is the gate the rest of the app (widget MSIX install, onboarding) sits behind: nothing else
    /// runs until Center is running from its installed location.
    ///
    /// ── PER-USER, and that is the whole point ────────────────────────────────────────────────────
    /// Everything here writes inside the current user's own profile: %LOCALAPPDATA%\Programs, the
    /// user's Start Menu, and HKCU's Uninstall hive. None of it needs administrator rights, so
    /// installing, updating and uninstalling Center are all UAC-free. This is the same model VS Code
    /// and Discord use, not a workaround.
    ///
    /// It used to install to %ProgramFiles% with an HKLM key and a machine-wide shortcut, which forced
    /// a UAC prompt on every install and every update. That was the single largest reason Center could
    /// not be a plain unelevated app — see <see cref="ElevationGate"/> for what is left (almost
    /// nothing) and <see cref="LegacyInstallDir"/> for how the old location is handled.
    ///
    /// No build dependency (no Inno/WiX) — copy-self-and-relaunch. Uninstall is registered as a real
    /// Add/Remove Programs entry that calls back into the installed exe with --uninstall.
    /// </summary>
    public static class SelfInstaller
    {
        /// <summary>
        /// The product name, and it is NOT just a label: it names the install folder
        /// (%LOCALAPPDATA%\Programs\ClawTweaks Center), the legacy Program Files folder, the Start
        /// Menu shortcut, the Desktop shortcut and that shortcut's description.
        ///
        /// !! CHANGING THIS MOVES THE INSTALL FOLDER and orphans every existing installation, which
        /// is why the Settings -> Apps caption got its own constant below instead of being folded in
        /// here. The ClawTweaks installer also hard-codes this path to find CTW_Center.exe.
        /// </summary>
        private const string AppDisplayName = "ClawTweaks Center";

        /// <summary>
        /// What Settings -> Apps shows, and nothing else.
        ///
        /// Three ClawTweaks rows sit there and they used to be told apart by a version number, which
        /// is a distinguishing mark for a developer and none at all for a user. The other two now say
        /// what they are - "ClawTweaks (Game Bar widget)" and "ClawTweaks Core (uninstall this for a
        /// full clean-up)" - so this one says what IT is rather than reading like the whole product.
        ///
        /// It is deliberately NOT hidden. SystemComponent=1 would take the row out of the list
        /// entirely, and hiding an uninstall entry is a known scoring signal for unwanted software -
        /// a price this project cannot pay while it ships unsigned. A row that explains itself is the
        /// cheaper answer to the same problem (user's decision, 2026-09-02).
        ///
        /// !! It must still CONTAIN "ClawTweaks Center". The ClawTweaks installer finds this entry by
        /// substring (ArpUninstallCommand), and that is what runs the guided Leave screen during an
        /// uninstall. Drop those two words and the offboarding silently stops being found, while the
        /// warning tells the user Center is not installed - with Center sitting right there.
        /// </summary>
        private const string ArpDisplayName = "ClawTweaks Center (Manage Updates and Library)";

        private const string UninstallKeyName = "ClawTweaksCenter";
        private const string ExeName = "CTW_Center.exe";

        /// <summary>
        /// %LOCALAPPDATA%\Programs\ClawTweaks Center. "Programs" is the convention Windows itself uses
        /// for per-user installs, so this sits next to whatever else the user has installed that way
        /// rather than inventing a new home.
        /// </summary>
        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppDisplayName);

        /// <summary>Where Center used to install (needs admin to write, and to remove). Kept only so an
        /// existing machine-wide install can be RECOGNISED — never written to.</summary>
        public static string LegacyInstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppDisplayName);

        private static string InstalledExePath => Path.Combine(InstallDir, ExeName);

        /// <summary>Full path of the INSTALLED Center exe - what an external full-screen launcher
        /// (AnyFSE) has to be pointed at. Deliberately not the running exe: a portable copy sitting in
        /// Downloads is not a path anyone should configure another program against.</summary>
        public static string InstalledExe =>
            VelopackStableExePath() ?? InstalledExePath;

        // ---- Velopack layout awareness ----------------------------------------------------------
        //
        // Center can be installed two ways: the classic copy-self into %LOCALAPPDATA%\Programs, and a
        // Velopack installation at %LOCALAPPDATA%\<packId>\{current, packages, Update.exe}. Both count
        // as "installed" everywhere below.
        //
        // These are PLAIN FILE CHECKS on purpose: nothing here calls into the Velopack package. The
        // updater lives in Update\ and is meant to come out in three steps (Update\REMOVAL.md); if
        // this file needed it, deleting that folder would break Center's startup gate instead of just
        // removing a feature. The layout is a fact on disk, so a fact on disk is what gets read.

        /// <summary>The Velopack root when the running exe sits in one - the folder holding
        /// <c>current\</c> and <c>Update.exe</c> - otherwise null.</summary>
        private static string VelopackRootOfRunningExe()
        {
            try
            {
                string dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                if (string.IsNullOrEmpty(dir)) return null;

                if (!string.Equals(Path.GetFileName(dir.TrimEnd('\\')), "current",
                                   StringComparison.OrdinalIgnoreCase))
                    return null;

                string root = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (string.IsNullOrEmpty(root)) return null;

                // Update.exe is the load-bearing half. "current" alone is a folder name anyone could
                // have, and being wrong here means Center skips its install gate somewhere that is
                // not an installation at all.
                return File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
            }
            catch { return null; }
        }

        /// <summary>The path an external launcher should be pointed at for a Velopack install, or
        /// null. Deliberately the STUB beside Update.exe rather than the exe inside <c>current\</c>:
        /// the stub keeps its path across updates, the versioned content directory need not.</summary>
        private static string VelopackStableExePath()
        {
            string root = VelopackRootOfRunningExe();
            if (root == null) return null;
            string stub = Path.Combine(root, ExeName);
            return File.Exists(stub) ? stub : Path.Combine(root, "current", ExeName);
        }

        /// <summary>True when this process is running out of a Velopack installation.</summary>
        public static bool IsRunningFromVelopackInstall() => VelopackRootOfRunningExe() != null;

        private static string UninstallRegistryKey =>
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";

        /// <summary>
        /// True when the running exe is where an INSTALLED Center belongs - either the classic
        /// <see cref="InstallDir"/> or a Velopack installation.
        ///
        /// WARNING: this is the gate that decides whether Center starts normally or shows the
        /// install-self window. Without the Velopack arm, a Center that Velopack installed concludes
        /// it is NOT installed, offers to install itself, and copies a SECOND copy into the classic
        /// location - measured on 2026-09-03, and the reason this arm exists at all.
        /// </summary>
        public static bool IsRunningFromInstallDir()
        {
            string current = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            if (string.Equals(
                    current?.TrimEnd('\\'),
                    InstallDir.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            return IsRunningFromVelopackInstall();
        }

        /// <summary>True when a previous run already installed Center to <see cref="InstallDir"/>,
        /// regardless of what version.</summary>
        public static bool IsInstalled() => File.Exists(InstalledExePath);

        /// <summary>
        /// True when an OLD machine-wide install is still sitting in Program Files. Removing it needs
        /// admin, which per-user Center deliberately does not ask for — so this exists to TELL the user
        /// about it, not to clean it up behind their back. Its own Add/Remove Programs entry still
        /// works and self-elevates, so Windows Settings is the honest place to point them at.
        /// </summary>
        public static bool LegacyInstallPresent()
        {
            try { return File.Exists(Path.Combine(LegacyInstallDir, ExeName)); }
            catch { return false; }
        }

        /// <summary>Version of the currently INSTALLED exe (read straight off the file, not the
        /// registry — can't drift out of sync with what's actually there), or null if not installed.</summary>
        public static Version GetInstalledVersion() => VersionOf(InstalledExePath);

        /// <summary>Version of the old machine-wide copy, or null if there isn't one.</summary>
        public static Version GetLegacyInstalledVersion() => VersionOf(Path.Combine(LegacyInstallDir, ExeName));

        /// <summary>
        /// Removes the OLD machine-wide install by running its OWN uninstaller — which elevates itself,
        /// so this stays true to Center never raising a prompt of its own.
        ///
        /// That works for every version that ever shipped, in both eras: the earliest Center declared
        /// requireAdministrator in its manifest, so Windows elevates it the moment it starts; later ones
        /// are asInvoker but call ElevationGate.EnsureElevatedOrRelaunch on --uninstall. Either way the
        /// UAC prompt comes from the old exe, about removing itself, which is exactly what it says on
        /// the dialog.
        ///
        /// The command is read from the registry rather than built from a path, so what runs is
        /// literally what Windows would run from Settings → Apps — including for any old layout that
        /// differed from what this build would guess.
        /// </summary>
        public static bool RemoveLegacyInstall(Action<string> log = null)
        {
            string command = LegacyUninstallCommand();
            if (command == null)
            {
                log?.Invoke("No uninstaller found for the older version.");
                return false;
            }

            // "C:\...\CTW_Center.exe" --uninstall  →  exe + args. Only the quoted form is produced by
            // any version's RegisterUninstallEntry; an unquoted path is handled by taking the whole
            // string as the exe, which is right for a path with no spaces and no worse than failing.
            string exe = command, args = "";
            if (command.StartsWith("\""))
            {
                int close = command.IndexOf('"', 1);
                if (close > 0)
                {
                    exe = command.Substring(1, close - 1);
                    args = command.Substring(close + 1).Trim();
                }
            }

            try
            {
                // UseShellExecute, but deliberately NO Verb = "runas": we are not the one asking for
                // rights. The old exe raises its own prompt. If we set runas here, the prompt would name
                // OUR process and Center would be back to elevating things.
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not start the old uninstaller: {ex.Message}");
                return false;
            }
        }

        /// <summary>The old machine-wide install's UninstallString from HKLM, or a reconstructed one if
        /// the exe is there but the registry entry isn't. Null when there is nothing to remove. Only
        /// meaningful while <see cref="LegacyInstallPresent"/> is true — that is what guarantees the exe
        /// this command points at actually exists.</summary>
        public static string LegacyUninstallCommand()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(UninstallRegistryKey);
                string value = key?.GetValue("UninstallString") as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }

            // Registry entry gone but the folder left behind — a half-finished removal. The exe still
            // understands --uninstall, so it can still clean up after itself.
            string exe = Path.Combine(LegacyInstallDir, ExeName);
            return File.Exists(exe) ? $"\"{exe}\" --uninstall" : null;
        }

        private static Version VersionOf(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                var info = FileVersionInfo.GetVersionInfo(exePath);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
            catch { return null; }
        }

        /// <summary>Launches the already-installed copy as-is (no copy/relaunch dance) — used when the
        /// running exe is the same version or older than what's already installed, so there's nothing
        /// to install or update. Caller should shut down right after calling this.</summary>
        public static void LaunchInstalledAndExit(Action<string> log = null, string args = null)
        {
            // Unelevated even if we happen to be elevated right now — see ElevationGate.LaunchUnelevated
            // for why inheriting the token here is what broke UAC prompting for everything downstream.
            ElevationGate.LaunchUnelevated(InstalledExePath, log, args);
        }

        /// <summary>
        /// True when the running executable is the self-contained single-file build, i.e. when copying
        /// that one file somewhere else still yields a runnable program.
        ///
        /// Two signals, both cheap and both decisive in the negative:
        ///   • a managed assembly of the same base name sitting next to the exe — that is the plain
        ///     build layout, where the exe is only a launcher for the .dll beside it;
        ///   • an exe far too small to contain a bundled runtime. The single-file build is tens of
        ///     megabytes; an apphost is a few hundred kilobytes.
        /// Neither can misfire on a real single-file build: it has no sibling .dll (everything is
        /// inside the bundle) and it is never small.
        /// </summary>
        private static bool IsSelfContainedSingleFile(string exePath, out string reason)
        {
            reason = null;
            try
            {
                string dir = Path.GetDirectoryName(exePath);
                string sibling = Path.Combine(dir, Path.GetFileNameWithoutExtension(exePath) + ".dll");
                if (File.Exists(sibling))
                {
                    reason = "it is a launcher for the .dll next to it, not a standalone build.";
                    return false;
                }

                const long MinimumBundleBytes = 8L * 1024 * 1024;
                long size = new FileInfo(exePath).Length;
                if (size < MinimumBundleBytes)
                {
                    reason = $"it is only {size / 1024} KB, too small to carry its own runtime.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Unreadable path: say so and refuse, rather than write an install that may be broken.
                reason = $"its own file could not be inspected ({ex.Message}).";
                return false;
            }
        }

        /// <summary>
        /// Copies the running exe to <see cref="InstallDir"/>, creates a Start Menu shortcut, registers
        /// an Add/Remove Programs entry, then launches the installed copy and exits this process.
        /// Caller should not do anything after this returns true — the app is about to shut down.
        ///
        /// Needs no administrator rights: every target is inside the current user's profile.
        /// </summary>
        /// <param name="desktopShortcut">Also drop an icon on this user's Desktop. Offered as a
        /// pre-ticked box on the install screen; the Start Menu entry is not optional, because it is
        /// what Settings → Apps and the Start search both rely on.</param>
        /// <param name="relaunchArgs">
        /// Passed to the installed copy when it is started. The installer's post-reboot hand-off uses
        /// it to carry --onboarding across the self-install, so the first thing a new user sees after
        /// the restart is the onboarding list rather than Home with a tile to find.
        /// </param>
        public static bool InstallAndRelaunch(Action<string> log = null, bool desktopShortcut = true,
                                              string relaunchArgs = null)
        {
            try
            {
                string sourceExe = Process.GetCurrentProcess().MainModule.FileName;
                string sourceDir = Path.GetDirectoryName(sourceExe);

                // Installing copies ONE file. That only produces a working install if this exe is the
                // self-contained single-file build — a plain `dotnet build` apphost is a ~300 KB stub
                // that loads its runtime and its own .dll from the folder it sits in, and a lone copy
                // of it dies before Main with "The application to execute does not exist". The install
                // then looks finished, but Center never starts again and even the Add/Remove Programs
                // entry does nothing, because that calls the same dead exe (hit on 0.1.9.7 — the two
                // builds carry the SAME filename, so the wrong one is easy to run).
                if (!IsSelfContainedSingleFile(sourceExe, out string why))
                {
                    log?.Invoke($"This copy of Center cannot install itself: {why} "
                        + "Use the single-file build from the release page (or publish/), not the "
                        + "executable from a plain build output.");
                    return false;
                }

                log?.Invoke($"Installing to {InstallDir}...");
                Directory.CreateDirectory(InstallDir);

                // Close the INSTALLED Center first. Updating means overwriting CTW_Center.exe, and a
                // running copy of it holds its own file — the copy below then fails with a sharing
                // violation and the whole update reports an error, which is what users hit. The exe
                // being replaced is a different file from the one running this code (the downloaded
                // Setup exe), so nothing here is closing itself.
                CloseOtherInstances();
                CopyWithRetry(sourceExe, InstalledExePath, log);

                // Release-folder run (msix + cer + Dependencies sit next to the exe, as Build-Setup.ps1
                // assembles it) — bring those along too, so PackageInstaller/CertInstaller still find
                // them via AssetRoot (= the exe's own directory) after relaunching from the install
                // folder. A standalone/portable run has none of these next to it — nothing to copy, the
                // normal CenterMenuWindow browse-and-download path (staging into %TEMP%) takes over.
                CopySiblingIfPresent(sourceDir, "*.msix");
                CopySiblingIfPresent(sourceDir, "*.msixbundle");
                CopySiblingIfPresent(sourceDir, "*.cer");
                CopySiblingDirIfPresent(sourceDir, "Dependencies");

                CreateStartMenuShortcut();

                // Best-effort, and separately caught: a Desktop that cannot be written to (redirected
                // to a OneDrive folder that is offline, say) is an odd machine, not a failed install.
                // Letting that throw would abort an install that has already copied the exe and would
                // otherwise have worked.
                if (desktopShortcut)
                {
                    try { CreateDesktopShortcut(); }
                    catch (Exception ex) { log?.Invoke($"Could not create the desktop icon: {ex.Message}"); }
                }
                else
                {
                    // Unticked on a re-install/update: remove the icon a previous install left behind,
                    // so the box reflects what is actually on the desktop rather than only ever adding.
                    try { if (File.Exists(DesktopShortcutPath())) File.Delete(DesktopShortcutPath()); }
                    catch { }
                }

                RegisterUninstallEntry();

                // Started through the shell rather than directly. Normally this process is unelevated
                // and it makes no difference — but a user can always right-click → Run as administrator,
                // and then a plain start would hand the installed copy an admin token it would keep for
                // its whole run. See ElevationGate.LaunchUnelevated.
                log?.Invoke("Relaunching from install location...");
                ElevationGate.LaunchUnelevated(InstalledExePath, log, relaunchArgs);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Install failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes the Start Menu shortcut and Add/Remove Programs entry immediately, closes any OTHER
        /// running Center, then spawns a short-lived cmd.exe that deletes the install folder once this
        /// process has exited — a running exe cannot delete its own file.
        ///
        /// Per-user only, and therefore admin-free. A leftover machine-wide install from before the
        /// move (<see cref="LegacyInstallPresent"/>) is NOT touched: it has its own HKLM Add/Remove
        /// Programs entry that elevates itself when the user runs it from Windows Settings.
        /// </summary>
        public static void Uninstall()
        {
            foreach (var shortcut in new[] { StartMenuShortcutPath(), DesktopShortcutPath() })
            {
                try { if (File.Exists(shortcut)) File.Delete(shortcut); }
                catch { }
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKey, throwOnMissingSubKey: false);
            }
            catch { }

            // Center's remembered preferences (window mode). Same hive, same lack of admin rights.
            CenterSettings.Clear();

            // Uninstalling from Settings → Apps starts a SECOND Center just to run --uninstall, so the
            // one the user already had open is still holding CTW_Center.exe. Measured 2026-07-30: the
            // shortcut and registry entry went, the folder deletion hit a sharing violation, and the
            // machine was left with an orphaned exe and no way to uninstall it from Settings any more.
            // Close the others first — the user is uninstalling, so leaving one running to keep its own
            // files alive is not a kindness.
            CloseOtherInstances();

            try
            {
                // Retried, not a single fixed delay. Even after closing the others there is no instant
                // at which the handles are guaranteed released: this process is still alive right now
                // (the caller shuts down immediately after), and Windows frees a terminated process's
                // file handles asynchronously. Three attempts at ~2 s, ~5 s and ~10 s covers a slow
                // shutdown without leaving a cmd.exe hanging around for a minute. rmdir on an already
                // deleted folder is a no-op, so the later attempts cost nothing in the normal case.
                //
                // Deliberately NOT a `for /L` loop with a `goto`: cmd handles a label inside a
                // parenthesized block unreliably, which is what silently left the folder behind the
                // first time this was written. Three flat statements have no such trap.
                string dir = InstallDir;
                string once = $"rmdir /S /Q \"{dir}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"timeout /t 2 /nobreak >nul & {once} & " +
                                $"timeout /t 3 /nobreak >nul & {once} & " +
                                $"timeout /t 5 /nobreak >nul & {once}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                Process.Start(psi);
            }
            catch { }
        }

        /// <summary>
        /// Closes every OTHER Center process, skipping our own. Asks the window to close first and only
        /// kills what doesn't go — a Center sitting mid-install would otherwise be torn down between two
        /// file copies.
        /// </summary>
        /// <summary>
        /// Copies over a file that was in use a moment ago.
        ///
        /// <see cref="CloseOtherInstances"/> waits for the processes to exit, but Windows releases a
        /// terminated process's file handles asynchronously — there is no instant at which the target
        /// is guaranteed writable, so a single attempt straight afterwards can still hit a sharing
        /// violation. Same reasoning as the retried folder delete on the uninstall path.
        ///
        /// The last attempt is deliberately allowed to throw: a genuinely blocked install must fail
        /// loudly rather than leave a half-updated folder behind a success message.
        /// </summary>
        private static void CopyWithRetry(string source, string destination, Action<string> log)
        {
            const int attempts = 4;
            for (int i = 1; ; i++)
            {
                try
                {
                    File.Copy(source, destination, overwrite: true);
                    return;
                }
                catch (IOException) when (i < attempts)
                {
                    log?.Invoke($"Waiting for the running Center to close ({i}/{attempts - 1})...");
                    System.Threading.Thread.Sleep(i * 500);
                }
                catch (UnauthorizedAccessException) when (i < attempts)
                {
                    log?.Invoke($"Waiting for the running Center to close ({i}/{attempts - 1})...");
                    System.Threading.Thread.Sleep(i * 500);
                }
            }
        }

        /// <summary>
        /// Ends every OTHER installed Center, so its exe can be replaced.
        ///
        /// ⚠️ MATCHED BY PROCESS NAME, NOT BY Process.MainModule. MainModule needs PROCESS_VM_READ,
        /// and on 2026-08-24 it was measured failing with "access denied" for our OWN Center - the
        /// process was pid 8292, the log says "pid 8292 could not be handled", and the summary line
        /// said "0 found, 0 ended" while the copy that followed failed three times and told the user
        /// to try again as an administrator, which would not have helped either.
        ///
        /// The name is SHARPER than the path test it replaces, not weaker, and that is a property of
        /// how these files are named: the installed file is CTW_Center.exe, the distributed one is
        /// CTW_Center_&lt;version&gt;_Setup.exe. Their process names therefore differ, so matching
        /// "CTW_Center" cannot catch a portable copy the user is running out of Downloads - the case
        /// the old path test existed to avoid.
        ///
        /// The path is still CHECKED where it can be read (QueryFullProcessImageName, which only
        /// needs PROCESS_QUERY_LIMITED_INFORMATION and is granted where MainModule is not). It is a
        /// veto, not a requirement: an unreadable path must never again be the reason a process is
        /// left holding the file being replaced.
        ///
        /// It logs what it found and what it did, including failures. A silent step is unanswerable
        /// afterwards - "the update did nothing" and "the update never looked" leave the same trace.
        /// </summary>
        private static void CloseOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;
            string name = Path.GetFileNameWithoutExtension(InstalledExePath);
            string dir = InstallDir.TrimEnd('\\');
            int found = 0, ended = 0;

            InstallLog.Write("CloseOtherInstances: looking for '" + name + "' processes under " + dir);

            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch (Exception ex)
            {
                InstallLog.Write("CloseOtherInstances: could not enumerate: " + ex.Message);
                return;
            }

            foreach (var proc in candidates)
            {
                if (proc.Id == self) { proc.Dispose(); continue; }
                try
                {
                    string path = TryGetProcessPath(proc.Id);
                    if (path != null && !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    {
                        InstallLog.Write("CloseOtherInstances: pid " + proc.Id + " is " + path
                                         + " - outside the install folder, leaving it alone.");
                        continue;
                    }

                    found++;
                    InstallLog.Write("CloseOtherInstances: pid " + proc.Id + " is "
                                     + (path ?? "(path unreadable)") + " - ending it.");

                    // ⚠️ NOT CloseMainWindow(). Center is a tray-resident app: with "Run in
                    // background" on, its Closing handler CANCELS the close and hides the window
                    // instead - by design, that is what the button does. Posting WM_CLOSE therefore
                    // cannot end it, and the three-second wait that followed was spent proving that
                    // every single time. A background instance has no main window at all.
                    //
                    // Nothing is lost by killing it: settings are written to the registry as they
                    // change, and the user asked to replace this exe.
                    proc.Kill();
                    if (proc.WaitForExit(5000))
                    {
                        ended++;
                        InstallLog.Write("CloseOtherInstances: pid " + proc.Id + " exited.");
                    }
                    else InstallLog.Write("CloseOtherInstances: pid " + proc.Id + " did NOT exit within 5 s.");
                }
                catch (Exception ex)
                {
                    // Named rather than swallowed: "could not be killed" and "was never there" leave
                    // exactly the same silence otherwise, and only the first of the two explains a
                    // copy that then fails.
                    InstallLog.Write("CloseOtherInstances: pid " + proc.Id + " could not be handled: "
                                     + ex.GetType().Name + " - " + ex.Message);
                }
                finally { try { proc.Dispose(); } catch { } }
            }

            InstallLog.Write("CloseOtherInstances: " + found + " found, " + ended + " ended.");
        }

        /// <summary>
        /// Another process's image path, or null when it cannot be read.
        ///
        /// QueryFullProcessImageName rather than Process.MainModule: it needs only
        /// PROCESS_QUERY_LIMITED_INFORMATION, which is granted for a process of the same user even
        /// where the VM read MainModule performs is refused. Null is a normal answer here and the
        /// caller treats it as "unknown", never as "does not match".
        /// </summary>
        private static string TryGetProcessPath(int pid)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero) return null;

                var buffer = new System.Text.StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            catch { return null; }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags,
            System.Text.StringBuilder exeName, ref int size);

        private static void CopySiblingIfPresent(string sourceDir, string searchPattern)
        {
            foreach (string file in Directory.GetFiles(sourceDir, searchPattern))
                File.Copy(file, Path.Combine(InstallDir, Path.GetFileName(file)), overwrite: true);
        }

        private static void CopySiblingDirIfPresent(string sourceDir, string dirName)
        {
            string src = Path.Combine(sourceDir, dirName);
            if (!Directory.Exists(src)) return;

            string dest = Path.Combine(InstallDir, dirName);
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                string destFile = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                File.Copy(file, destFile, overwrite: true);
            }
        }

        /// <summary>The CURRENT USER's Start Menu, not the machine-wide one — writing to
        /// CommonStartMenu needs admin, which is exactly what this install path no longer asks for.</summary>
        private static string StartMenuShortcutPath()
        {
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            return Path.Combine(startMenu, "Programs", $"{AppDisplayName}.lnk");
        }

        /// <summary>
        /// This user's own Desktop. Same admin-free story as the Start Menu above —
        /// CommonDesktopDirectory (the "all users" desktop) would need rights Center never asks for.
        /// </summary>
        private static string DesktopShortcutPath()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(desktop, $"{AppDisplayName}.lnk");
        }

        /// <summary>
        /// Optional desktop icon, offered as a pre-ticked box on the install screen.
        ///
        /// Note what is deliberately NOT here: pinning to the taskbar. There is no supported way to do
        /// it — the shell's "Pin to taskbar" verb has been blocked to programs since Windows 10, and
        /// the WinRT TaskbarManager needs a packaged app, which this unpackaged WPF exe is not. What is
        /// left is writing the undocumented Taskband blob, which Windows 11 validates and discards, and
        /// which is exactly the shape of self-pinning behaviour that has already had this project's
        /// binaries flagged once. The user pins it themselves from this shortcut.
        /// </summary>
        private static void CreateDesktopShortcut() => CreateShortcut(DesktopShortcutPath());

        private static void CreateStartMenuShortcut() => CreateShortcut(StartMenuShortcutPath());

        /// <summary>
        /// Creates a .lnk via the WScript.Shell COM object (no extra NuGet package — ships with
        /// Windows).
        /// </summary>
        private static void CreateShortcut(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                try
                {
                    shortcut.TargetPath = InstalledExePath;
                    shortcut.WorkingDirectory = InstallDir;
                    shortcut.Description = AppDisplayName;
                    shortcut.Save();
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>HKCU, not HKLM. The entry shows up in Settings → Apps for this user and needs no
        /// admin to create or delete.</summary>
        private static void RegisterUninstallEntry()
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey);
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

            key.SetValue("DisplayName", ArpDisplayName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "ClawTweaks");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", InstalledExePath);
            key.SetValue("UninstallString", $"\"{InstalledExePath}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            try
            {
                long sizeKb = new FileInfo(InstalledExePath).Length / 1024;
                key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
