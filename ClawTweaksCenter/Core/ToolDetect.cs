using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>Result of a single tool's presence check.</summary>
    public sealed class ToolStatus
    {
        public string Name { get; set; }
        public bool Installed { get; set; }
        public string Detail { get; set; }   // where it was found, or why it's considered missing
    }

    /// <summary>
    /// Detects the required prerequisite tools (HidHide, usbip-win2, RTSS) using pure registry /
    /// service-key / file checks — no PowerShell, so it is instant and safe to run repeatedly.
    ///
    /// ══ PARITY IS THE GOAL — Center and the helper must agree, on BOTH detection and install ══
    ///
    /// Four surfaces answer "is this tool installed?": this class, Setup-Tools.ps1's Test-*Installed,
    /// HidHideHelper.IsInstalled, and RTSSHelper. When they disagree the user sees a green tick for
    /// something that does not work, and — worse — the side that believes it is installed skips the
    /// install, so nothing ever repairs it. That is not hypothetical: it cost a Claw 8 EX user days of
    /// a dead Game Bar (2026-07-29), because a leftover registry key counted as an install everywhere.
    ///
    /// So: when you touch tool DETECTION on either side, change BOTH, or write down here why the
    /// difference is deliberate. One deliberate difference exists today: the helper opens the driver
    /// control device and can go further than this class does — treat the helper's answer as the
    /// authority, this one as the cheap no-PowerShell approximation.
    ///
    /// ══ Center no longer INSTALLS these ══
    /// The parity rule above used to cover installing too, and Center carried winget calls plus a
    /// download-verify-then-runas path to match the helper. That is gone: Center is an unelevated app
    /// now, and fetching executables to run elevated was both its last source of UAC prompts and the
    /// riskiest thing it did from an antivirus-heuristics point of view. Center detects and points the
    /// user at the vendor (PrerequisiteGuide); the helper keeps its install paths, because it is signed
    /// and already elevated. So the two sides are deliberately asymmetric here — do not "restore
    /// parity" by giving Center install logic back.
    ///
    /// ViGEm is intentionally NOT checked here: it is OBSOLETE. VIIPER (usbip) is the virtual-controller
    /// backend. The helper still installs and gates on ViGEmBus for the legacy backend, which is a known
    /// remaining divergence and should go away with the legacy path, not be copied into Center.
    /// </summary>
    public static class ToolDetect
    {
        public static ToolStatus HidHide()
        {
            // 1) The driver's own control device — the same thing the helper opens, so Center and the
            //    helper now answer the same question instead of two loosely related ones. Needs no
            //    elevation (measured: opens outright unelevated; see ProbeDevice for why a denial would
            //    also count as present).
            if (ProbeDevice(@"\\.\HidHide") != DeviceProbe.Absent)
                return Ok("HidHide", "driver control device present");

            // 2) HidHideCLI on disk
            foreach (var cli in new[]
            {
                @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
                @"C:\Program Files\Nefarius Software Solutions e.U\HidHide\x64\HidHideCLI.exe",
                @"C:\Program Files\HidHide\x64\HidHideCLI.exe",
            })
            {
                if (File.Exists(cli))
                    return Ok("HidHide", cli);
            }

            // 3) Kernel-driver service registered AND its binary actually on disk.
            //     The service key alone is NOT proof: a failed/rolled-back install, an AV removal, or a
            //     third-party tool that bundles HidHide and then uninstalls it all leave the key behind
            //     while HidHide.sys is gone. Counting that as "installed" is what let a Claw 8 EX run for
            //     days with the physical pad unhidden (doubled input) while every surface in ClawTweaks
            //     claimed HidHide was fine and the setup skipped the install. Same rule as RTSS below:
            //     the registry is a POINTER to an install, never proof of one.
            bool serviceKey = ServiceKeyExists("HidHide");
            bool vendorKey = RegKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide");
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "HidHide.sys");

            if (serviceKey && File.Exists(sys))
                return Ok("HidHide", "driver service + HidHide.sys present");

            if (serviceKey || vendorKey)
                return new ToolStatus
                {
                    Name = "HidHide",
                    Installed = false,
                    Detail = "BROKEN: leftover registry entries but no driver binary — reinstall and reboot"
                };

            return Missing("HidHide");
        }

        /// <summary>
        /// usbip-win2 — and specifically its UDE DRIVER, which is the only part VIIPER can use.
        ///
        /// ══ The exe is not the install. The driver is. ══
        /// This used to return "installed" as soon as C:\Program Files\USBip\usbip.exe existed, and
        /// that is exactly how a Claw ended up with a green tick and no virtual controller
        /// (2026-07-30). usbip-win2's installer copies its files FIRST and registers the driver LAST,
        /// via devnode.exe. If devnode.exe fails — as it does when the wrong architecture was
        /// downloaded, "CreateProcess failed; code 216" — the user is left with every file on disk, a
        /// complete Add/Remove Programs entry, and NO driver service. Measured on that machine: usbip
        /// 0.9.7.8 listed in ARP, all four exes present, and not one of usbip2_ude / usbip_vhci
        /// registered. Same lesson as HidHide above and [[hidhide-remnant-not-an-install]]: a file or a
        /// registry key is a POINTER to an install, never proof of one.
        ///
        /// ══ 'usbipd' is a DIFFERENT PROJECT and must not be accepted ══
        /// The old service list included "usbipd", which belongs to dorssel/usbipd-win — a tool for
        /// sharing USB devices into WSL. It is not vadimgrn/usbip-win2 and it does not provide the UDE
        /// backend VIIPER needs. Accepting it would report a working prerequisite for software that
        /// cannot drive the virtual controller at all. Do not add it back.
        /// </summary>
        public static ToolStatus Usbip()
        {
            // The driver service AND its binary. Both, for the same reason HidHide checks both.
            foreach (var svc in new[] { "usbip2_ude", "usbip_vhci" })
            {
                if (!ServiceKeyExists(svc)) continue;

                // Registered driver, but is it a version we can actually drive? See UsbipVersionStatus.
                string tooNew = UnsupportedUsbipVersion();
                if (tooNew != null)
                    return new ToolStatus
                    {
                        Name = "usbip",
                        Installed = false,
                        Detail = $"UNSUPPORTED VERSION: usbip {tooNew} is installed, ClawTweaks needs " +
                                 $"{MaxSupportedUsbipVersion}. From {MinBrokenUsbipVersion} on, the " +
                                 "virtual controller shows up TWICE and games see every input doubled. " +
                                 $"Uninstall usbip, install {MaxSupportedUsbipVersion} from the link on " +
                                 "this page, and reboot.",
                    };

                return Ok("usbip", $"UDE driver service '{svc}' registered");
            }

            // Files present but no driver: the half-finished install described above. Called out
            // explicitly rather than as a plain "missing", because "reinstall it" is useless advice
            // when the files are already there — the user needs to know the DRIVER step is what failed.
            bool filesPresent =
                File.Exists(@"C:\Program Files\USBip\usbip.exe") ||
                File.Exists(@"C:\Program Files\usbip-win2\usbip.exe") ||
                ArpDisplayNameContains("usbip");

            if (filesPresent)
                return new ToolStatus
                {
                    Name = "usbip",
                    Installed = false,
                    Detail = "BROKEN: usbip's files are installed but its driver is not registered — " +
                             "the installer's driver step (devnode.exe) failed. The usual cause is the " +
                             "-arm64 download, which the release page lists above the x64 one. " +
                             "Uninstall usbip, re-run the installer whose name ends in -x64.exe, and " +
                             "reboot.",
                };

            return Missing("usbip");
        }

        /// <summary>
        /// RTSS presence — deliberately mirrors Shared RTSSHelper.IsInstalled(): only an RTSS.exe that
        /// actually exists on disk counts. A bare registry key must NEVER count: an NSIS uninstall leaves
        /// Unwinder\RTSS (incl. its InstallDir value) behind, so trusting the key reported RTSS as
        /// installed on machines where it was long gone. That made this screen disagree with the helper —
        /// which correctly saw it missing — so the wizard skipped installing RTSS while onboarding could
        /// never finalize, with no way out for the user. The registry is used only as a POINTER to the
        /// install dir; the file check is the proof.
        /// </summary>
        public static ToolStatus Rtss()
        {
            // 1) Registry InstallDir → verify the exe is really there (orphan-key safe).
            foreach (var key in new[]
            {
                @"SOFTWARE\WOW6432Node\Unwinder\RTSS",
                @"SOFTWARE\Unwinder\RTSS",
                @"SOFTWARE\WOW6432Node\Guru3D\RTSS",
                @"SOFTWARE\Guru3D\RTSS",
            })
            {
                var dir = RegValue(RegistryHive.LocalMachine, key, "InstallDir");
                if (string.IsNullOrEmpty(dir)) continue;
                var exe = Path.Combine(dir, "RTSS.exe");
                if (File.Exists(exe))
                    return Ok("RTSS", exe);
            }

            // 2) Default install locations.
            foreach (var exe in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "RivaTuner Statistics Server", "RTSS.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "RivaTuner Statistics Server", "RTSS.exe"),
            })
            {
                if (File.Exists(exe))
                    return Ok("RTSS", exe);
            }

            return Missing("RTSS");
        }

        /// <summary>
        /// PawnIO presence — ported 1:1 from the helper's CheckPawnIODriverInstalled()
        /// (XboxGamingBarHelper/Performance/PerformanceManager.cs): PawnIO exposes no registry/file
        /// marker the helper trusts, so "installed" means its device object actually opens, nothing
        /// else. Same check used for the TDP Method card's Install/Installed status in the main app.
        /// </summary>
        public static ToolStatus PawnIO()
        {
            switch (ProbeDevice(@"\\.\PawnIO"))
            {
                case DeviceProbe.Opened: return Ok("PawnIO", "device opened");
                case DeviceProbe.PresentNoAccess: return Ok("PawnIO", "device present (access denied — unelevated)");
                default: return Missing("PawnIO");
            }
        }

        /// <summary>Outcome of opening a driver's control device.</summary>
        private enum DeviceProbe
        {
            /// <summary>Handle obtained — the driver is there and we may talk to it.</summary>
            Opened,
            /// <summary>ERROR_ACCESS_DENIED. The device EXISTS; we merely lack rights to open it.</summary>
            PresentNoAccess,
            /// <summary>ERROR_FILE_NOT_FOUND — no such device object, i.e. the driver is not loaded.</summary>
            Absent
        }

        /// <summary>
        /// Asks whether a kernel driver's control device exists, WITHOUT needing to be elevated.
        ///
        /// The distinction that makes this work: opening a device you have no rights to fails with
        /// ERROR_ACCESS_DENIED, while opening one that does not exist fails with ERROR_FILE_NOT_FOUND.
        /// Access-denied is therefore positive proof of presence, not a failure to detect.
        ///
        /// This used to be missed, and it cost a UAC prompt: PawnIO's device denies an unelevated
        /// open, the old check treated "no handle" as "not installed", and Center had to elevate the
        /// whole install flow just to find out whether a driver was already there. Center runs
        /// unelevated and never prompts at all now — but the principle behind the fix stands on its
        /// own: asking a yes/no question about the system is not a privileged action.
        ///
        /// Measured unelevated on an MSI Claw with both drivers installed and working:
        ///   \\.\PawnIO  -> rc 5 (denied, present)   \\.\HidHide -> rc 0 (opens)   bogus name -> rc 2
        /// </summary>
        private static DeviceProbe ProbeDevice(string devicePath)
        {
            IntPtr handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle.ToInt64() != -1)
            {
                CloseHandle(handle);
                return DeviceProbe.Opened;
            }

            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED
                ? DeviceProbe.PresentNoAccess
                : DeviceProbe.Absent;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const int ERROR_ACCESS_DENIED = 5;

        #region helpers
        /// <summary>Newest usbip-win2 our bundled libviiper can drive.</summary>
        public const string MaxSupportedUsbipVersion = "0.9.7.7";

        /// <summary>First usbip-win2 that breaks it — see <see cref="UnsupportedUsbipVersion"/>.</summary>
        public const string MinBrokenUsbipVersion = "0.9.7.8";

        /// <summary>
        /// Returns the installed usbip version string when it is NEWER than we support, otherwise null.
        ///
        /// WHY A VERSION GATE AT ALL. Our bundled libviiper (VIIPER v0.6.x) attaches the virtual pad
        /// through the vhci PLUGIN_HARDWARE IOCTL. usbip-win2 0.9.7.8 grew that struct by 16 bytes, so
        /// libviiper's attach fails there and falls back to spawning `usbip attach` out of process,
        /// fire-and-forget. ClawTweaks attaches too, and when the stray child finally lands — measured
        /// 23 SECONDS later, mid-game, on 2026-07-30 — Windows has TWO virtual Xbox pads mirroring the
        /// same input. On 0.9.7.7 libviiper's own attach succeeds, so only ONE attach path exists.
        /// HandheldCompanion pins the identical version for the identical reason.
        ///
        /// FAILS OPEN, like every other version gate in this app: if the version cannot be read or
        /// parsed we say nothing and let the install count. Locking someone out of onboarding over an
        /// unreadable version string would be worse than the doubled pad.
        /// </summary>
        private static string UnsupportedUsbipVersion()
        {
            string raw = ReadUsbipVersion();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!Version.TryParse(raw.Trim(), out var found)) return null;
            if (!Version.TryParse(MaxSupportedUsbipVersion, out var max)) return null;
            return found > max ? raw.Trim() : null;
        }

        /// <summary>
        /// usbip's version, from the CLI binary's file version first (it is the artefact that actually
        /// has to match the driver), falling back to the Add/Remove Programs entry.
        /// </summary>
        private static string ReadUsbipVersion()
        {
            foreach (var exe in new[]
            {
                @"C:\Program Files\USBip\usbip.exe",
                @"C:\Program Files\usbip-win2\usbip.exe",
            })
            {
                try
                {
                    if (!File.Exists(exe)) continue;
                    string v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch { /* unreadable — fall through to ARP, then fail open */ }
            }

            return ArpDisplayVersionFor("usbip");
        }

        /// <summary>DisplayVersion of the first ARP entry whose DisplayName contains <paramref name="needle"/>.</summary>
        private static string ArpDisplayVersionFor(string needle)
        {
            foreach (var root in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(root);
                    if (key == null) continue;

                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        var name = sub?.GetValue("DisplayName") as string;
                        if (name == null || name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var version = sub.GetValue("DisplayVersion") as string;
                        if (!string.IsNullOrWhiteSpace(version)) return version;
                    }
                }
                catch { /* fail open */ }
            }
            return null;
        }

        private static ToolStatus Ok(string name, string detail) =>
            new ToolStatus { Name = name, Installed = true, Detail = detail };

        private static ToolStatus Missing(string name) =>
            new ToolStatus { Name = name, Installed = false, Detail = "not found" };

        private static bool ServiceKeyExists(string serviceName) =>
            RegKeyExists(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{serviceName}");

        private static bool RegKeyExists(RegistryHive hive, string subKey)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(subKey);
                return k != null;
            }
            catch { return false; }
        }

        private static string RegValue(RegistryHive hive, string subKey, string valueName)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(subKey);
                return k?.GetValue(valueName) as string;
            }
            catch { return null; }
        }

        private static bool ArpDisplayNameContains(string needle)
        {
            foreach (var root in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(root);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var k = key.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
                catch { /* ignore and try next root */ }
            }
            return false;
        }
        #endregion
    }
}
