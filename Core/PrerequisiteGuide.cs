using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// The download page and the reason-it-is-needed for each prerequisite tool, plus the little bit of
    /// shell plumbing that opens a page in the user's browser.
    ///
    /// ── Why Center does not install these any more ───────────────────────────────────────────────
    /// It used to: winget for HidHide/RTSS/PawnIO, and a download-verify-then-ShellExecute("runas")
    /// path for usbip, PawnIO and the HidHide MSI fallback. Every one of those raised a UAC prompt out
    /// of Center, and the driver ones were literally "fetch an executable from the internet and run it
    /// elevated" — the textbook dropper sequence, performed by an unsigned app. Defender's ML has
    /// nothing but behaviour to judge Center on, and this project has already been bitten once by
    /// exactly that scoring (Behavior:Win32/Persistence.A!ml on the old script-driven deploy+task).
    ///
    /// So the model is now: Center DETECTS (see <see cref="ToolDetect"/>, unchanged and still the
    /// authority), NAMES what is missing, opens the vendor's own page, and re-checks on demand. The
    /// user downloads and runs the vendor installer themselves, from the vendor, with the vendor's own
    /// signature and the vendor's own elevation prompt. Center never downloads an executable and never
    /// elevates anything.
    ///
    /// This is also what makes the claim "Center never asks for administrator rights" actually true.
    /// A single surviving runas — usbip's installer, say — would have made it a lie, and users would
    /// have found it immediately.
    ///
    /// If automated installs are ever wanted back, they belong in the HELPER, not here: it is signed,
    /// it is already elevated via its scheduled task, and it already carries the winget + vendor-MSI
    /// fallback logic (see the parity note on <see cref="ToolDetect"/>).
    /// </summary>
    public static class PrerequisiteGuide
    {
        public sealed class ToolInfo
        {
            /// <summary>Must match <see cref="ToolStatus.Name"/> so a detect result can be paired up.</summary>
            public string Name;
            public string Why;
            public string PageUrl;
            /// <summary>Shown under the link — what the user is looking for on that page.</summary>
            public string WhatToGet;
            /// <summary>True for the ones that need a restart before they actually work.</summary>
            public bool NeedsReboot;
            /// <summary>
            /// The winget package id, shown as a command the USER can paste into their own terminal.
            /// Center does not run it — that would put us back to spawning elevated installers (see the
            /// class note). It is offered because winget picks the right architecture by itself, which
            /// is exactly where hand-picking a release asset goes wrong; see <see cref="Usbip"/>.
            /// </summary>
            public string WingetId;

            /// <summary>The full command line to show. Null when there is no winget package.</summary>
            public string WingetCommand =>
                WingetId == null ? null : $"winget install --id {WingetId} -e";
        }

        public static readonly ToolInfo HidHide = new ToolInfo
        {
            Name = "HidHide",
            Why = "Hides the Claw's physical gamepad so games see only the virtual controller. " +
                  "Without it every input is registered twice.",
            PageUrl = "https://github.com/nefarius/HidHide/releases/latest",
            WhatToGet = "Download the x64 installer (HidHide_x.x.x.x_x64.exe) and run it.",
            WingetId = "Nefarius.HidHide",
            NeedsReboot = true,
        };

        public static readonly ToolInfo Usbip = new ToolInfo
        {
            Name = "usbip",
            Why = "Provides the virtual controller itself (the VIIPER backend).",
            PageUrl = "https://github.com/vadimgrn/usbip-win2/releases/latest",
            // The release page offers exactly two assets and lists them ALPHABETICALLY, which puts
            // arm64 ABOVE x64 — so the first download link on the page is the wrong one for the Claw.
            // Picking it fails LATE and confusingly: the installer runs, copies every file, and only
            // then dies with "Unable to execute file: C:\Program Files\USBip\devnode.exe -
            // CreateProcess failed; code 216". 216 is ERROR_EXE_MACHINE_TYPE_MISMATCH — the binaries
            // are for another architecture — but the message talks about the Windows version, so it
            // reads like an OS problem. Hit on an MSI Claw (x64) 2026-07-30, twice, by a user who had
            // already been told "take the x64 build": naming the architecture is not enough, the
            // filename has to be spelled out, because both assets differ by four characters.
            WhatToGet = "Take the file ending in -x64.exe (e.g. USBip-0.9.7.8-x64.exe). The page lists " +
                        "the -arm64 build FIRST — that one is wrong for the Claw: it copies every file " +
                        "and THEN fails at the driver step with \"CreateProcess failed; code 216\", " +
                        "leaving usbip looking installed while the driver is missing. Watch that the " +
                        "installer finishes without an error box, and reboot afterwards.",
            // No winget id ON PURPOSE. The obvious candidate, USBIPD-WIN.usbipd, is dorssel/usbipd-win
            // — a different project for sharing USB devices into WSL. It is not vadimgrn/usbip-win2 and
            // does not provide the UDE driver VIIPER needs, so suggesting it would send users to
            // install the wrong software entirely. Recommending it here was an error, made worse by
            // ToolDetect then accepting its 'usbipd' service as proof; both are fixed.
            WingetId = null,
            NeedsReboot = true,
        };

        public static readonly ToolInfo Rtss = new ToolInfo
        {
            Name = "RTSS",
            Why = "Applies the per-game FPS cap and draws the on-screen display.",
            PageUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/",
            WhatToGet = "Download RivaTuner Statistics Server and run the installer.",
            WingetId = "Guru3D.RTSS",
            NeedsReboot = false,
        };

        public static readonly ToolInfo PawnIO = new ToolInfo
        {
            Name = "PawnIO",
            Why = "Reads the CPU sensors ClawTweaks shows in the widget.",
            PageUrl = "https://pawnio.eu/",
            WhatToGet = "Download PawnIO Setup and run it.",
            WingetId = "namazso.PawnIO",
            NeedsReboot = false,
        };

        /// <summary>All four, in the order the install screen lists them.</summary>
        public static IReadOnlyList<ToolInfo> All { get; } = new[] { HidHide, Usbip, Rtss, PawnIO };

        public static ToolInfo For(string toolName)
        {
            foreach (var t in All)
                if (string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        /// <summary>
        /// Opens a URL in the user's default browser. UseShellExecute is required — without it .NET
        /// tries to execute the URL as a file path and throws. Never throws: a failed browser launch
        /// must not take down the screen the user is reading, and the URL is on that screen anyway.
        /// </summary>
        public static bool OpenPage(string url, Action<string> log = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not open {url}: {ex.Message}");
                return false;
            }
        }
    }
}
