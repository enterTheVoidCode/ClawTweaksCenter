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
        }

        public static readonly ToolInfo HidHide = new ToolInfo
        {
            Name = "HidHide",
            Why = "Hides the Claw's physical gamepad so games see only the virtual controller. " +
                  "Without it every input is registered twice.",
            PageUrl = "https://github.com/nefarius/HidHide/releases/latest",
            WhatToGet = "Download HidHide_x.x.x.x_x64.exe and run it.",
            NeedsReboot = true,
        };

        public static readonly ToolInfo Usbip = new ToolInfo
        {
            Name = "usbip",
            Why = "Provides the virtual controller itself (the VIIPER backend).",
            PageUrl = "https://github.com/vadimgrn/usbip-win2/releases/latest",
            WhatToGet = "Download the USBip-x.x.x.x.exe installer and run it. It installs a driver, " +
                        "so Windows will ask you to confirm the driver publisher.",
            NeedsReboot = true,
        };

        public static readonly ToolInfo Rtss = new ToolInfo
        {
            Name = "RTSS",
            Why = "Applies the per-game FPS cap and draws the on-screen display.",
            PageUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/",
            WhatToGet = "Download RivaTuner Statistics Server and run the installer.",
            NeedsReboot = false,
        };

        public static readonly ToolInfo PawnIO = new ToolInfo
        {
            Name = "PawnIO",
            Why = "Reads the CPU sensors ClawTweaks shows in the widget.",
            PageUrl = "https://pawnio.eu/",
            WhatToGet = "Download PawnIO Setup and run it.",
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
