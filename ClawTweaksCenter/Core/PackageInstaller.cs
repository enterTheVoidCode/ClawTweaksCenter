using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Installs the ClawTweaks MSIX (with its dependency packages) via Add-AppxPackage. Mirrors
    /// Install.ps1: -ForceApplicationShutdown so a running instance is replaced, and
    /// -ForceUpdateFromAnyVersion so installs over a higher/rolled-back manifest still work.
    /// Appx cmdlets are most reliable under Windows PowerShell 5.1, so we invoke that explicitly.
    /// The signing certificate must already be trusted (see <see cref="CertInstaller"/>).
    /// </summary>
    public static class PackageInstaller
    {
        /// <summary>Finds the .msix/.msixbundle next to <see cref="SetupContext.AssetRoot"/> (root or a Package subfolder).</summary>
        public static string FindPackage()
        {
            string dir = SetupContext.AssetRoot;
            foreach (var d in new[] { dir, Path.Combine(dir, "Package") })
            {
                if (!Directory.Exists(d)) continue;
                foreach (var ext in new[] { "*.msixbundle", "*.msix", "*.appxbundle", "*.appx" })
                {
                    var f = Directory.GetFiles(d, ext, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (f != null) return f;
                }
            }
            return null;
        }

        /// <summary>Collects dependency packages from a Dependencies\x64 (and Dependencies) folder.</summary>
        public static List<string> FindDependencies(string packagePath)
        {
            var deps = new List<string>();
            try
            {
                string root = Path.GetDirectoryName(packagePath);
                foreach (var d in new[] { Path.Combine(root, "Dependencies", "x64"), Path.Combine(root, "Dependencies") })
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d))
                        if (f.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                            deps.Add(f);
                }
            }
            catch { }
            return deps;
        }

        /// <summary>Runs a Windows PowerShell 5.1 command and returns its stdout. Null on failure.</summary>
        private static string RunPowerShell(string command, int timeoutMs, out string stderr)
        {
            stderr = null;
            try
            {
                string winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = File.Exists(winPs) ? winPs : "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                                command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                string outp = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } return null; }
                return outp;
            }
            catch { return null; }
        }

        /// <summary>Currently installed ClawTweaks package version (via Get-AppxPackage), or null if not installed.
        /// Used by the Center menu to warn before a downgrade.</summary>
        public static Version GetInstalledVersion()
        {
            string outp = RunPowerShell(
                "(Get-AppxPackage -Name 'MSIClaw.ClawTweaks*' | Select-Object -First 1 -ExpandProperty Version)",
                15000, out _);
            return Version.TryParse((outp ?? string.Empty).Trim(), out var v) ? v : null;
        }

        /// <summary>
        /// Removes every ClawTweaks registration — the way out, used by the uninstall flow.
        ///
        /// Two things are deliberately NOT done here. The app data is not backed up first: this is
        /// someone leaving, and the flow has already offered a settings backup a step earlier, so a
        /// silent copy in %TEMP% would be clutter they never asked for. And the helper is not killed:
        /// it watches for its own package disappearing and uses that to delete its scheduled task and
        /// its deployed copy before exiting. Killing it first is how those get left behind.
        ///
        /// Bundle registrations are included — a family can carry one that a plain Get-AppxPackage
        /// never shows, and leaving it means the family is still half-registered afterwards.
        /// </summary>
        public static bool RemoveClawTweaks(Action<string> log = null)
        {
            var entries = InspectFamily();
            if (entries.Count == 0)
            {
                log?.Invoke("ClawTweaks is not installed.");
                return true;
            }

            foreach (var e in entries)
                RemoveRegistration(e.FullName, log);

            var left = InspectFamily();
            if (left.Count == 0)
            {
                log?.Invoke("ClawTweaks removed.");
                return true;
            }

            log?.Invoke(left.Count + " registration(s) could not be removed.");
            return false;
        }

        /// <summary>One registration of the ClawTweaks package family as Windows currently sees it.</summary>
        private sealed class FamilyEntry
        {
            public string FullName;
            public bool IsBundle;
            public string Status;
            public bool IsHealthy => string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lists every Main and Bundle registration of the ClawTweaks family. Bundle entries are the
        /// interesting ones: they are invisible to a plain <c>Get-AppxPackage</c>, which only returns
        /// the Main package, so a family can look perfectly normal and still carry a bundle record.
        /// </summary>
        private static List<FamilyEntry> InspectFamily()
        {
            var list = new List<FamilyEntry>();
            // Single quotes and string concatenation only — no double quotes anywhere, so nothing
            // here has to survive RunPowerShell's quote escaping on the way to the command line.
            string outp = RunPowerShell(
                "Get-AppxPackage -PackageTypeFilter Main,Bundle -Name 'MSIClaw.ClawTweaks*' | " +
                "ForEach-Object { $_.PackageFullName + '|' + $_.IsBundle + '|' + $_.Status }",
                20000, out _);
            if (string.IsNullOrWhiteSpace(outp)) return list;

            foreach (var line in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0])) continue;
                list.Add(new FamilyEntry
                {
                    FullName = parts[0].Trim(),
                    IsBundle = string.Equals(parts[1].Trim(), "True", StringComparison.OrdinalIgnoreCase),
                    Status = parts[2].Trim(),
                });
            }
            return list;
        }

        /// <summary>The app's per-user data: profiles, LED composite, fan curves, widget settings.
        /// The publisher hash is fixed for the family and is hardcoded elsewhere in Center too.</summary>
        private static string PackageDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "MSIClaw.ClawTweaks_7eszav2039cvc");

        private static string AppDataBackupFolder => Path.Combine(Path.GetTempPath(),
            "ClawTweaksCenter", "appdata-backup");

        /// <summary>
        /// Copies the package's data folder aside so a removal cannot cost the user their settings.
        ///
        /// WHY THIS EXISTS. Remove-AppxPackage has a -PreserveApplicationData switch that looks made
        /// for exactly this, and it does not apply to us: Windows rejects it with 0x80073CFA — "the
        /// PreserveApplicationData flag can only be used for a package deployed in developer mode".
        /// Our packages are sideloaded normally, so the flag is unusable and a removal really does
        /// delete the data. Measured 2026-08-04, not assumed.
        ///
        /// Files that cannot be read are skipped rather than aborting the backup — a partial copy is
        /// worth more than none, and the caller is told what was missed.
        /// </summary>
        private static bool BackupAppData(Action<string> log)
        {
            try
            {
                string src = PackageDataFolder;
                if (!Directory.Exists(src)) return true;   // nothing to lose

                string dst = AppDataBackupFolder;
                if (Directory.Exists(dst)) { try { Directory.Delete(dst, true); } catch { } }
                Directory.CreateDirectory(dst);

                int copied = 0, skipped = 0;
                foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string rel = file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
                        string target = Path.Combine(dst, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(file, target, true);
                        copied++;
                    }
                    catch { skipped++; }
                }

                log?.Invoke("Backed up app data: " + copied + " file(s)" +
                            (skipped > 0 ? ", " + skipped + " could not be read" : "") + " → " + dst);
                return skipped == 0;
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not back up app data: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Puts the backed-up data back after a reinstall, without overwriting anything the fresh
        /// package has already written. Deliberately called before the Game Bar and the helper start,
        /// so nothing holds settings.dat open while it is restored. The backup is left on disk.
        /// </summary>
        private static void RestoreAppData(Action<string> log)
        {
            try
            {
                string src = AppDataBackupFolder;
                if (!Directory.Exists(src)) return;

                string dst = PackageDataFolder;
                Directory.CreateDirectory(dst);

                int restored = 0, failed = 0;
                foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string rel = file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
                        string target = Path.Combine(dst, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(file, target, true);
                        restored++;
                    }
                    catch { failed++; }
                }

                log?.Invoke("Restored app data: " + restored + " file(s)" +
                            (failed > 0 ? ", " + failed + " failed (backup kept at " + src + ")" : "") + ".");
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not restore app data: " + ex.Message + " (backup kept at " + AppDataBackupFolder + ")");
            }
        }

        /// <summary>
        /// Removes one registration. The caller must have backed the app data up first — see
        /// <see cref="BackupAppData"/> for why -PreserveApplicationData cannot do that job here.
        /// </summary>
        private static bool RemoveRegistration(string fullName, Action<string> log)
        {
            log?.Invoke("Removing conflicting registration " + fullName + "…");
            RunPowerShell("Remove-AppxPackage -Package '" + fullName.Replace("'", "''") + "'",
                          180000, out string err);
            bool stillThere = InspectFamily().Any(e =>
                string.Equals(e.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            if (stillThere)
            {
                log?.Invoke("Could not remove " + fullName + (string.IsNullOrWhiteSpace(err) ? "." : ": " + err.Trim()));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Pre-flight run before every install: looks for registrations that make the deployment's
        /// conflict check fail, and clears them.
        ///
        /// THE SCENARIO THIS HANDLES. Windows keeps a "preferred package version for the package
        /// family", and that record is bundle bookkeeping. Once a .msixbundle has been installed, the
        /// family carries a <c>*_neutral_~_*</c> bundle registration alongside the architecture
        /// package — and a later plain .msix update against that family fails at the Resolved stage
        /// with 0x80073CF3 ("dependency or conflict check"). The mirror case (a bundle over a family
        /// registered from a loose package) fails the same way, which is why the check is written
        /// around the SHAPE of what we are about to install rather than around bundles alone.
        ///
        /// A registration whose Status is not Ok is cleared for the same reason: the update path
        /// cannot repair a damaged registration, it can only trip over it.
        ///
        /// Nothing happens when the family is empty or already matches — this costs one Get-AppxPackage
        /// on a normal install.
        /// </summary>
        public static bool PrepareFamily(string packagePath, Action<string> log = null)
        {
            bool removedAny = false;
            try
            {
                var family = InspectFamily();

                // Logged on every install, not only when something has to be cleaned. A field report
                // that says "the install fails" is unanswerable without knowing what is registered —
                // and bundle records in particular are invisible to a plain Get-AppxPackage.
                log?.Invoke(family.Count == 0
                    ? "No existing ClawTweaks registration — clean install."
                    : "Existing registration(s): " + string.Join(", ", family.Select(e =>
                        e.FullName + " [" + (e.IsBundle ? "bundle" : "package") + ", " + e.Status + "]")));

                if (family.Count == 0) return false;

                bool installingBundle = packagePath != null &&
                    (packagePath.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                     packagePath.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase));

                foreach (var entry in family)
                {
                    // A bundle registration only belongs to a bundle install, and vice versa.
                    bool shapeMismatch = entry.IsBundle != installingBundle;
                    if (!shapeMismatch && entry.IsHealthy) continue;

                    log?.Invoke(shapeMismatch
                        ? "Found a " + (entry.IsBundle ? "bundle" : "non-bundle") + " registration while installing a " +
                          (installingBundle ? "bundle" : "package") + " — that combination fails the conflict check."
                        : "Found a registration in state '" + entry.Status + "' — an update cannot repair it.");

                    // Always secure the data first: a removal here really does delete it.
                    if (!removedAny) BackupAppData(log);
                    removedAny |= RemoveRegistration(entry.FullName, log);

                    // Removing a bundle takes its payload package with it, so re-read rather than
                    // trying to remove an entry that no longer exists.
                    if (InspectFamily().Count == 0) break;
                }
            }
            catch (Exception ex)
            {
                // Never block an install because the pre-flight itself stumbled.
                log?.Invoke("Package family check skipped: " + ex.Message);
            }
            return removedAny;
        }

        /// <summary>Clears every Main/Bundle registration of the family, keeping app data. Last resort
        /// after a conflict-check failure, where the only remedy is for the family to be empty.</summary>
        private static bool CleanFamily(Action<string> log)
        {
            var family = InspectFamily();
            if (family.Count == 0) return false;

            BackupAppData(log);
            bool removedAny = false;
            // Bundles first: removing a bundle takes its payload package with it, so doing it the
            // other way round can leave an orphaned bundle record behind.
            foreach (var entry in family.OrderByDescending(e => e.IsBundle))
            {
                if (InspectFamily().Any(e => string.Equals(e.FullName, entry.FullName, StringComparison.OrdinalIgnoreCase)))
                    removedAny |= RemoveRegistration(entry.FullName, log);
            }
            return removedAny;
        }

        // Deployment errors we can say something useful about. Everything else is reported verbatim.
        private const string HresultConflictCheck = "0x80073CF3";  // dependency/conflict check failed
        private const string HresultOpenFailed = "0x80073CF0";     // the package file could not be opened

        public static bool Install(string packagePath, IEnumerable<string> dependencies, Action<string> log = null)
        {
            // Clear known-bad family state before deployment rather than reacting to its error.
            bool removedRegistrations = PrepareFamily(packagePath, log);

            bool ok = TryAddPackage(packagePath, dependencies, log, out string error);
            if (ok)
            {
                if (removedRegistrations) RestoreAppData(log);
                return true;
            }

            if (error != null && error.IndexOf(HresultConflictCheck, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // The conflict check rejected the update, and the pre-flight did not see why. The only
                // remedy is an empty family, so clear it and try once more. The data is copied aside
                // first, then put back, because a removal deletes it (see BackupAppData).
                log?.Invoke("The package family conflicted with this update. Clearing it and retrying once…");
                if (CleanFamily(log))
                {
                    ok = TryAddPackage(packagePath, dependencies, log, out error);
                    if (ok) { RestoreAppData(log); return true; }
                }
            }
            else if (error != null && error.IndexOf(HresultOpenFailed, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // The deployment service could not reach the file. Measured once on a machine where the
                // very same bytes deployed fine from outside the user profile (see
                // BuildDownloader.StagingRoot), so retry from there before giving up — the download path
                // already lands there, but an install driven from an extracted ZIP or a repointed
                // AssetRoot does not.
                string retryPath = CopyToStagingArea(packagePath, dependencies, log, out var stagedDeps);
                if (retryPath != null)
                {
                    log?.Invoke("Windows could not open the package from this folder. Retrying from a " +
                                "machine-wide location…");
                    if (TryAddPackage(retryPath, stagedDeps, log, out error))
                    {
                        if (removedRegistrations) RestoreAppData(log);
                        return true;
                    }
                }

                // Still refused. Name the two things that actually cause it, so the report is answerable.
                log?.Invoke("Windows could not open the package file. This is not a problem with the package " +
                            "itself — the deployment service (running as SYSTEM) could not read it. Try installing " +
                            "from a folder outside your user profile, and check whether Gaming Services is healthy.");
            }

            return false;
        }

        /// <summary>
        /// Copies the package and its dependencies to <see cref="BuildDownloader.StagingRoot"/> so the
        /// deployment can be retried from a path outside the user profile. Returns the copied package,
        /// or null when it already lives there (nothing to gain) or the copy failed.
        /// </summary>
        private static string CopyToStagingArea(string packagePath, IEnumerable<string> dependencies,
                                                Action<string> log, out List<string> stagedDependencies)
        {
            stagedDependencies = new List<string>();
            try
            {
                string root = BuildDownloader.StagingRoot;
                // Already there — a second copy would fail exactly the same way, and saying "retrying"
                // while changing nothing is worse than the plain error.
                if (packagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

                string dir = Path.Combine(root, "retry");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                string staged = Path.Combine(dir, Path.GetFileName(packagePath));
                File.Copy(packagePath, staged, true);

                // The dependencies travel too: they are passed by path and are read by the same service
                // that could not read the package.
                foreach (var dep in dependencies ?? Enumerable.Empty<string>())
                {
                    string target = Path.Combine(dir, Path.GetFileName(dep));
                    File.Copy(dep, target, true);
                    stagedDependencies.Add(target);
                }
                return staged;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not stage the package for a retry: {ex.Message}");
                stagedDependencies = new List<string>();
                return null;
            }
        }

        private static bool TryAddPackage(string packagePath, IEnumerable<string> dependencies,
                                          Action<string> log, out string error)
        {
            error = null;
            try
            {
                var sb = new StringBuilder();
                sb.Append("Add-AppxPackage -Path '").Append(packagePath.Replace("'", "''")).Append("'");
                var deps = dependencies?.ToList() ?? new List<string>();
                if (deps.Count > 0)
                {
                    sb.Append(" -DependencyPath ");
                    sb.Append(string.Join(",", deps.Select(p => "'" + p.Replace("'", "''") + "'")));
                }
                sb.Append(" -ForceApplicationShutdown -ForceUpdateFromAnyVersion");

                string winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = File.Exists(winPs) ? winPs : "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + sb.ToString().Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                log?.Invoke("Installing package…");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string outp = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(300000)) { try { proc.Kill(); } catch { } log?.Invoke("Install timed out."); return false; }
                if (proc.ExitCode != 0 || !string.IsNullOrWhiteSpace(err))
                {
                    error = (string.IsNullOrWhiteSpace(err) ? outp : err).Trim();
                    log?.Invoke("Install error: " + error);
                    return proc.ExitCode == 0;
                }
                log?.Invoke("Package installed.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                log?.Invoke("Install exception: " + ex.Message);
                return false;
            }
        }
    }
}
