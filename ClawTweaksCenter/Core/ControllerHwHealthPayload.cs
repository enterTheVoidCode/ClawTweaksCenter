using System;
using System.Collections.Generic;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Parses the compact "key=value;…" HW-controller health payload the helper sends over the Center
    /// pipe (see PushCenterHealthSnapshot / MSIClawHidController.ProbeHwHealth) into flags plus a
    /// user-facing detail line for onboarding step 0. No JSON dependency — the helper deliberately keeps
    /// this a flat key=value string.
    /// </summary>
    public sealed class ControllerHwHealthPayload
    {
        public bool Present;
        public bool Openable;
        public bool Responsive;
        public int Mode = -1;
        public string Verdict = "unknown";
        public string Detail = "";

        /// <summary>A short line for the step's subtitle, tuned to each verdict.</summary>
        public string FriendlyDetail
        {
            get
            {
                switch (Verdict)
                {
                    case "ok": return "Controller healthy — ready to mount as a virtual pad.";
                    case "missing": return "MSI Claw controller not found. Connect/enable it (MSI Center M may hold it).";
                    case "blocked": return "Controller HID is held by another process — close conflicting controller software and re-check.";
                    case "unresponsive": return "Controller HID opened but did not respond — try re-checking; a reboot may be needed.";
                    default: return string.IsNullOrEmpty(Detail) ? "Could not determine controller health." : Detail;
                }
            }
        }

        public static ControllerHwHealthPayload Parse(string payload)
        {
            var r = new ControllerHwHealthPayload();
            if (string.IsNullOrWhiteSpace(payload)) { r.Verdict = "timeout"; r.Detail = "The helper did not answer the health probe."; return r; }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in payload.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                map[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }

            if (map.TryGetValue("present", out var p)) r.Present = p == "1";
            if (map.TryGetValue("openable", out var o)) r.Openable = o == "1";
            if (map.TryGetValue("responsive", out var re)) r.Responsive = re == "1";
            if (map.TryGetValue("mode", out var m) && int.TryParse(m, out int mode)) r.Mode = mode;
            if (map.TryGetValue("verdict", out var v) && !string.IsNullOrEmpty(v)) r.Verdict = v;
            if (map.TryGetValue("detail", out var d)) r.Detail = d;
            return r;
        }
    }
}
