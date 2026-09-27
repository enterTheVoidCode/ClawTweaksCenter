namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Which prerequisite tools a widget install may be HELD BACK for.
    ///
    /// 🔴 THE INSTALL USED TO DEMAND ALL FOUR. HidHide, usbip, RTSS and PawnIO, unconditionally -
    /// written when every ClawTweaks machine had every tool. Since 0.4.0.60 the setup asks whether the
    /// virtual controller and RTSS are wanted, and a ClawTweaks Essential machine has neither
    /// driver set on purpose. The old rule sent exactly those users to "Install the missing
    /// prerequisites" for tools they had declined, with no way through to the widget update.
    ///
    /// The rule is the one onboarding already uses (SetupToolChoices), applied to the install:
    ///   PawnIO             always - the sensors and the power readout need it on every edition.
    ///   usbip + HidHide    only when the virtual controller is wanted.
    ///   RTSS               only when RTSS is wanted. It is opt-in since 0.4.0.53.
    ///
    /// ⚠️ THREE STATES for "wanted", and null is the common one - every install from before
    /// 0.4.0.60. There a tool that is simply NOT FOUND does not block: the machine is treated as the
    /// edition it already is. A tool that IS there but broken or on an unsupported version still
    /// blocks, because that is not a decision anybody made - it is a machine in a bad state.
    ///
    /// ⚠️ Not the helper's gate. Whether the virtual controller may MOUNT is decided by the helper
    /// immediately before the mount (UsbipWin2Version, VirtualControllerGate). This only decides
    /// whether Center should stop the install and hand over first.
    /// </summary>
    public static class InstallToolPolicy
    {
        public static bool Blocks(ToolStatus tool)
        {
            if (tool == null || tool.Installed) return false;

            switch ((tool.Name ?? string.Empty).ToLowerInvariant())
            {
                case "pawnio":
                    return true;

                case "usbip":
                case "hidhide":
                    return BlocksByChoice(SetupToolChoices.VirtualControllerWanted, tool);

                case "rtss":
                    return BlocksByChoice(SetupToolChoices.RtssWanted, tool);

                default:
                    return true;
            }
        }

        /// <summary>Why a missing tool did NOT block - for the install log, one line per tool.</summary>
        public static string WhyNotRequired(ToolStatus tool)
        {
            bool? wanted = string.Equals(tool?.Name, "RTSS", System.StringComparison.OrdinalIgnoreCase)
                ? SetupToolChoices.RtssWanted
                : SetupToolChoices.VirtualControllerWanted;
            return wanted == false ? "declined in setup" : "not installed, never asked - optional";
        }

        private static bool BlocksByChoice(bool? wanted, ToolStatus tool)
        {
            if (wanted == true) return true;    // asked, said yes, and it is not there: a real task
            if (wanted == false) return false;  // asked, said no
            return !IsSimplyAbsent(tool);       // never asked: only a bad state blocks
        }

        /// <summary>ToolDetect's plain miss. Everything else on a not-installed tool (BROKEN,
        /// UNSUPPORTED VERSION) means something is on the machine and in a bad state.</summary>
        private static bool IsSimplyAbsent(ToolStatus tool) =>
            string.Equals(tool.Detail, "not found", System.StringComparison.Ordinal);
    }
}
