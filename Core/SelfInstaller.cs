using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClawTweaksSetup.Core
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
        /// Copies the running exe to <see cref="InstallDir"/>, creates a Start Menu shortcut, registers
        /// an Add/Remove Programs entry, then launches the installed copy and exits this process.
        /// Caller should not do anything after this returns true — the app is about to shut down.
        ///
        /// Needs no administrator rights: every target is inside the current user's profile.
        /// </summary>
        public static bool InstallAndRelaunch(Action<string> log = null)
        {
            try
            {
                string sourceExe = Process.GetCurrentProcess().MainModule.FileName;
                string sourceDir = Path.GetDirectoryName(sourceExe);

                log?.Invoke($"Installing to {InstallDir}...");
                Directory.CreateDirectory(InstallDir);
                File.Copy(sourceExe, InstalledExePath, overwrite: true);

                // Release-folder run (msix + cer + Dependencies sit next to the exe, as Build-Setup.ps1
                // assembles it) — bring those along too, so PackageInstaller/CertInstaller still find
                // them via AssetRoot (= the exe's own directory) after relaunching from the install
                // folder. A standalone/portable run has none of these next to it — nothing to copy, the
                // normal CenterMenuWindow browse-and-download path (staging into %TEMP%) takes over.
                CopySiblingIfPresent(sourceDir, "*.msix");
                CopySiblingIfPresent(sourceDir, "*.msixbundle");
                CopySiblingIfPresent(sourceDir, "*.cer");
                CopySiblingIfPresent(sourceDir, "Setup-Tools.ps1");
                CopySiblingDirIfPresent(sourceDir, "Dependencies");

                CreateStartMenuShortcut();
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
            try
            {
                string shortcut = StartMenuShortcutPath();
                if (File.Exists(shortcut)) File.Delete(shortcut);
            }
            catch { }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKey, throwOnMissingSubKey: false);
            }
            catch { }

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
        /// Creates a .lnk via the WScript.Shell COM object (no extra NuGet package — ships with
        /// Windows).
        /// </summary>
        private static void CreateStartMenuShortcut()
        {
            string path = StartMenuShortcutPath();
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
