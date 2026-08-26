using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ClawTweaksCenter.Library
{
    /// <summary>One line of a profile panel: what it is, and what it is set to.</summary>
    public sealed class ProfileLine
    {
        public string Label;
        public string Value;
    }

    /// <summary>
    /// The VALUES inside a per-game profile, for the two panels on the launch screen.
    ///
    /// ⚠️ THIS NEEDED NO PIPE, and that is worth stating because the handover notes say it does. The
    /// open item in the app repo's CLAUDE.md ("der Sprachkanal fuer Profil-DETAILS") is about a
    /// Center-to-helper channel with new ordinal Function values mirrored across two repos. That is
    /// still true for LIVE state - what is applied right now, whether the helper is even running -
    /// but the two values the user cares about most, TDP and the FPS cap, are plain elements in a
    /// plain XML file that Center already opens for the badge. Reading them is a file read.
    ///
    /// ⚠️ READ ONLY, for the reason in ClawProfiles: the helper holds every profile in memory and
    /// rewrites the file on its next save, so anything written here disappears without a word.
    ///
    /// THREE RULES DECIDE WHAT APPEARS, and they are what keeps the panel honest:
    ///
    ///   1. UNSET IS NOT ZERO. Most numeric fields use -1 for "leave this to Windows" and the
    ///      xsi:nil attribute for "never set". Printing either as a number invents a setting the
    ///      user never made - the same rule the widget's own CPU summary follows.
    ///   2. THE POWER SPLIT IS RESOLVED, not ignored. A profile with PowerSourceSplit on carries a
    ///      second value in <c>&lt;Field&gt;_Plugged</c>, and the base is the UNPLUGGED one (see
    ///      unplugged-is-the-primary-state in the app repo). Showing the base while the device is on
    ///      mains would advertise a cap the driver is not running - that exact mistake shipped once
    ///      in the OSD hint and took a measurement to find.
    ///   3. NOTHING SET MEANS NO PANEL. An empty box titled "Performance" says the profile is empty
    ///      when it usually means we could not read it.
    /// </summary>
    public static class ClawProfileDetails
    {
        /// <summary>What one game's profile holds. Either list can be empty; both empty means the
        /// launch screen draws nothing on that side.</summary>
        public sealed class Details
        {
            public readonly List<ProfileLine> Performance = new List<ProfileLine>();
            public readonly List<ProfileLine> Controller = new List<ProfileLine>();
            public bool HasAny => Performance.Count > 0 || Controller.Count > 0;
        }

        public static Details For(GameEntry game)
        {
            var d = new Details();
            string file = ClawProfiles.PerformanceFileFor(game);
            if (file == null) return d;

            XElement root;
            try { root = XDocument.Load(file).Root; }
            catch { return d; }
            if (root == null) return d;

            bool plugged = OnMains();
            var p = new Reader(root, plugged);

            BuildPerformance(p, d.Performance);
            BuildController(p, d.Controller);
            return d;
        }

        // ── Performance ─────────────────────────────────────────────────────────────────────────

        private static void BuildPerformance(Reader p, List<ProfileLine> lines)
        {
            int tdp = p.Int("TDP");
            if (tdp > 0) Add(lines, "TDP", tdp + " W");

            // The boost watts are only a setting while the boost itself is on; the number survives in
            // the file after it is switched off.
            if (p.Bool("TDPBoostEnabled") == true)
            {
                int fppt = p.Int("TDPBoostFPPTWatts");
                Add(lines, "TDP boost", fppt > 0 ? fppt + " W" : "On");
            }

            string cap = FpsCap(p);
            if (cap != null) Add(lines, "FPS cap", cap);

            string boost = BoostMode(p);
            if (boost != null) Add(lines, "CPU boost", boost);

            // The two frequency ceilings, one per core class. 0 is "unlimited" here rather than -1,
            // because the field predates the unset convention.
            int fP = p.Int("CPUMaxFrequencyClass1MHz");
            int fE = p.Int("CPUMaxFrequencyMHz");
            if (fP > 0) Add(lines, "P-core max", fP + " MHz");
            if (fE > 0) Add(lines, "E-core max", fE + " MHz");

            string states = Pair(p.Int("MaxCPUStateClass1"), p.Int("MinCPUStateClass1"), "%");
            if (states != null) Add(lines, "P-core state", states);
            states = Pair(p.Int("MaxCPUState"), p.Int("MinCPUState"), "%");
            if (states != null) Add(lines, "E-core state", states);

            int epp = p.Int("CPUEPPClass1");
            if (epp >= 0) Add(lines, "P-core EPP", epp.ToString(CultureInfo.InvariantCulture));
            epp = p.Int("CPUEPP");
            if (epp >= 0) Add(lines, "E-core EPP", epp.ToString(CultureInfo.InvariantCulture));

            int rr = p.Int("RefreshRate");
            if (rr > 0) Add(lines, "Refresh rate", rr + " Hz");

            if (p.Bool("HDREnabled") == true) Add(lines, "HDR", "On");
        }

        /// <summary>
        /// The cap, resolved the way the helper resolves it (Program.ProfileHandlers): FpsCapMode 1
        /// means the Intel driver cap and the number lives in IntelFpsTier; anything else means RTSS
        /// and the number lives in FPSLimit. Either is only a cap while its number is above zero -
        /// "Intel mode selected, currently off" is a real and common state.
        /// </summary>
        private static string FpsCap(Reader p)
        {
            int mode = p.Int("FpsCapMode");
            if (mode == 1)
            {
                int tier = p.Int("IntelFpsTier");
                if (tier <= 0) return null;
                return MigrateTier(tier) + " fps (Intel)";
            }

            int limit = p.Int("FPSLimit");
            return limit > 0 ? limit + " fps (RTSS)" : null;
        }

        /// <summary>Legacy 1/2/3 tiers became real frame rates; the helper migrates them on apply and
        /// old profiles still carry the small numbers. Mirrored from IntelGpuManager.MigrateTierToFps
        /// - if that mapping ever changes, this is the second place.</summary>
        private static int MigrateTier(int tier)
        {
            switch (tier)
            {
                case 1: return 60;
                case 2: return 40;
                case 3: return 30;
                default: return tier;
            }
        }

        /// <summary>
        /// ⚠️ POSITIONAL, and mirrored by hand from the widget's CpuBoostModeComboBox. An entry
        /// inserted in the middle over there renames every mode below it here, silently. Out-of-range
        /// falls back to the number rather than guessing a name - a wrong name is worse than none.
        ///
        /// -1 means the mode was never set, in which case the old CPUBoost bool is the answer: that
        /// is exactly how the helper's EffectiveCPUBoostMode resolves it, including resolving true
        /// to 1 rather than 2.
        /// </summary>
        private static readonly string[] BoostModeNames =
        {
            "Disabled", "Enabled", "Aggressive", "Efficient Enabled",
            "Efficient Aggressive", "Aggressive At Guaranteed", "Efficient Aggressive At Guaranteed",
        };

        private static string BoostMode(Reader p)
        {
            int mode = p.Int("CPUBoostMode");
            if (mode < 0)
            {
                bool? on = p.Bool("CPUBoost");
                if (on == null) return null;
                mode = on.Value ? 1 : 0;
            }
            return mode >= 0 && mode < BoostModeNames.Length
                ? BoostModeNames[mode]
                : "Mode " + mode.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"100 / 30 %" when either half is set, with the unset half named rather than
        /// dropped - a lone number would not say which of the two it is.</summary>
        private static string Pair(int max, int min, string unit)
        {
            if (max < 0 && min < 0) return null;
            string a = max < 0 ? "OS" : max.ToString(CultureInfo.InvariantCulture);
            string b = min < 0 ? "OS" : min.ToString(CultureInfo.InvariantCulture);
            return a + " / " + b + " " + unit;
        }

        // ── Controller ──────────────────────────────────────────────────────────────────────────

        private static void BuildController(Reader p, List<ProfileLine> lines)
        {
            if (p.Bool("ControllerProfileEnabled") != true) return;

            string m1 = Button(p, "ControllerButtonM1");
            string m2 = Button(p, "ControllerButtonM2");
            if (m1 != null) Add(lines, "M1", m1);
            if (m2 != null) Add(lines, "M2", m2);

            int gyro = p.Int("ControllerGyroTarget");
            if (gyro > 0) Add(lines, "Gyro", GyroTarget(gyro));

            string dz = Pair2(p.Int("ControllerLeftStickDeadzone"), p.Int("ControllerRightStickDeadzone"), "%");
            if (dz != null) Add(lines, "Stick deadzone", dz);

            string lt = Pair2(p.Int("ControllerLeftTriggerStart"), p.Int("ControllerLeftTriggerEnd"), "%");
            if (lt != null) Add(lines, "Left trigger", lt);
            string rt = Pair2(p.Int("ControllerRightTriggerStart"), p.Int("ControllerRightTriggerEnd"), "%");
            if (rt != null) Add(lines, "Right trigger", rt);

            if (p.Bool("ControllerHairTriggers") == true) Add(lines, "Hair triggers", "On");
            if (p.Bool("ControllerNintendoLayout") == true) Add(lines, "Layout", "Nintendo");

            var vib = p.Bool("ControllerVibration");
            if (vib == false) Add(lines, "Vibration", "Off");
            else
            {
                int intensity = p.Int("ControllerVibrationIntensity");
                if (intensity >= 0) Add(lines, "Vibration", intensity + " %");
            }
        }

        /// <summary>
        /// ⚠️ POSITIONAL AGAIN, mirrored from the widget's ControllerGamepadActionComboBox, where the
        /// item's INDEX is the stored GamepadAction. Same failure as the boost names: an inserted
        /// entry renames everything below it, in another repo, with nothing checking. Anything out of
        /// range prints its number.
        ///
        /// Only Type 0 (a gamepad button) is decoded. Keyboard, mouse and macro mappings are their
        /// own shapes and would each need their own vocabulary; they are named by kind instead of
        /// being spelled out, which is the honest half of the answer rather than a wrong whole one.
        /// </summary>
        private static readonly string[] GamepadActionNames =
        {
            "Disabled",
            "LS Click", "LS Up", "LS Down", "LS Left", "LS Right",
            "RS Click", "RS Up", "RS Down", "RS Left", "RS Right",
            "D-Pad Up", "D-Pad Down", "D-Pad Left", "D-Pad Right",
            "A", "B", "X", "Y",
            "LB", "LT", "RB", "RT",
            "Select", "Start",
            "Xbox Button",
        };

        private static string Button(Reader p, string element)
        {
            string json = p.Text(element);
            if (string.IsNullOrWhiteSpace(json)) return null;

            int type = JsonInt(json, "Type");
            if (type != 0)
            {
                switch (type)
                {
                    case 1: return "Keyboard";
                    case 2: return "Mouse";
                    case 3: return "Macro";
                    default: return null;
                }
            }

            int action = JsonInt(json, "GamepadAction");
            if (action <= 0) return null;   // 0 is Disabled, which is not a remap worth a line
            return action < GamepadActionNames.Length
                ? GamepadActionNames[action]
                : "Action " + action.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A single integer out of the mapping blob. Deliberately not a JSON parser: the
        /// blob is written by us, has no nesting at this level, and pulling in a dependency for one
        /// number would be the larger change.</summary>
        private static int JsonInt(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        private static string GyroTarget(int target)
        {
            switch (target)
            {
                case 1: return "Right stick";
                case 2: return "Left stick";
                case 3: return "Mouse";
                default: return "On";
            }
        }

        /// <summary>Like Pair, but both halves have to be set for the line to mean anything - a
        /// trigger start without its end is not half a setting, it is one value out of a pair whose
        /// other half the helper is leaving alone.</summary>
        private static string Pair2(int a, int b, string unit)
        {
            if (a < 0 && b < 0) return null;
            string x = a < 0 ? "-" : a.ToString(CultureInfo.InvariantCulture);
            string y = b < 0 ? "-" : b.ToString(CultureInfo.InvariantCulture);
            return x + " / " + y + " " + unit;
        }

        private static void Add(List<ProfileLine> lines, string label, string value) =>
            lines.Add(new ProfileLine { Label = label, Value = value });

        // ── Reading ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pulls one field, resolving the power split. The plugged slot only exists when the profile
        /// has PowerSourceSplit on, and an empty (xsi:nil) slot inherits the base - the same
        /// resolution the helper's Effective* methods do.
        /// </summary>
        private sealed class Reader
        {
            private readonly XElement _root;
            private readonly bool _plugged;

            public Reader(XElement root, bool plugged)
            {
                _root = root;
                _plugged = plugged && string.Equals(
                    (string)root.Element("PowerSourceSplit"), "true", StringComparison.OrdinalIgnoreCase);
            }

            public string Text(string name)
            {
                if (_plugged)
                {
                    string over = (string)_root.Element(name + "_Plugged");
                    if (!string.IsNullOrEmpty(over)) return over;
                }
                return (string)_root.Element(name);
            }

            /// <summary>-1 for missing, nil or unparsable, which is also the app's own "unset".</summary>
            public int Int(string name)
            {
                string s = Text(name);
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
            }

            /// <summary>null when the element is absent or nil - which is NOT the same as false, and
            /// the difference decides whether a line appears at all.</summary>
            public bool? Bool(string name)
            {
                string s = Text(name);
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        /// <summary>
        /// True while the device is on mains. 1 means plugged; 0 means battery; 255 means Windows does
        /// not know, and that is answered with FALSE on purpose - unplugged is this product's primary
        /// state, so an unknown power source shows the values a handheld actually runs on.
        /// </summary>
        private static bool OnMains()
        {
            try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 1; }
            catch { return false; }
        }
    }
}
