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
        private const string AppDisplayName = "ClawTweaks Center";
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

        private static string UninstallRegistryKey =>
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";

        /// <summary>True when the currently running exe already lives in <see cref="InstallDir"/>.</summary>
        public static bool IsRunningFromInstallDir()
        {
            string current = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            return string.Equals(
                current?.TrimEnd('\\'),
                InstallDir.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
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
        public static void LaunchInstalledAndExit(Action<string> log = null)
        {
            // Unelevated even if we happen to be elevated right now — see ElevationGate.LaunchUnelevated
            // for why inheriting the token here is what broke UAC prompting for everything downstream.
            ElevationGate.LaunchUnelevated(InstalledExePath, log);
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
        public static bool InstallAndRelaunch(Action<string> log = null, bool desktopShortcut = true)
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
                ElevationGate.LaunchUnelevated(InstalledExePath, log);
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
        /// Closes every Center running from <see cref="InstallDir"/>, leaving this process alone.
        /// Used by both the install path (the exe it is about to overwrite must not be in use) and the
        /// uninstall path (a running copy keeps its own folder alive).
        /// </summary>
        private static void CloseOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;

            // Matched on the exe in OUR install folder, not on process name: the distributed file is
            // called CTW_Center_<version>_Setup.exe and the installed one CTW_Center.exe, so a name
            // match would either miss instances or catch a portable copy the user is running from their
            // Downloads folder, which this has no business closing.
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == self) { p.Dispose(); continue; }
                try
                {
                    string path = p.MainModule?.FileName;
                    if (path == null ||
                        !path.StartsWith(InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (p.CloseMainWindow() && p.WaitForExit(3000)) continue;
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch
                {
                    // Access denied on a process we can't inspect, or it exited while we looked at it.
                    // Either way the retried rmdir above is the backstop.
                }
                finally { try { p.Dispose(); } catch { } }
            }
        }

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

            key.SetValue("DisplayName", AppDisplayName);
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
