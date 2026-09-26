using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClawTweaksCenter.Core
{
    /// <summary>Keeps the latest helper metrics and formats them against the current AC line.</summary>
    internal sealed class FooterPowerStatus
    {
        private string _lastMetrics;

        // A null reply means no new sample, including a normal tick while the helper is slow.
        internal string Update(string json, bool onMains)
        {
            if (json != null) _lastMetrics = json;
            json = _lastMetrics;
            if (string.IsNullOrEmpty(json)) return null;
            double level = ReadNumber(json, "batteryLevel");
            double remaining = ReadNumber(json, "timeRemaining");
            double toFull = ReadNumber(json, "timeToFull");
            // The helper sample can outlive an unplug. The current AC line wins over its charging
            // flag, before selecting either the label or time-to-full versus remaining runtime.
            bool charging = onMains && Regex.IsMatch(json, "\"isCharging\"\\s*:\\s*true", RegexOptions.IgnoreCase);

            // A level of -1 is the helper's "no reading", and 0 is not a useful footer reading.
            if (level <= 0) return null;
            string percent = ((int)Math.Round(level)).ToString(CultureInfo.CurrentCulture) + "%";
            double seconds = charging ? toFull : remaining;
            string clock = seconds > 0
                ? ((int)(seconds / 3600)).ToString(CultureInfo.CurrentCulture)
                  + ":" + ((int)((seconds % 3600) / 60)).ToString("D2", CultureInfo.CurrentCulture)
                : null;

            if (charging)
                return clock != null
                    ? Loc.F("{0} · charging · {1} h", percent, clock)
                    : Loc.F("{0} · charging", percent);
            if (onMains)
            {
                // A charge limit may hold a plugged-in battery below full.
                return level >= 99
                    ? Loc.F("{0} · AC power · fully charged", percent)
                    : Loc.F("{0} · AC power · not charging", percent);
            }
            return clock != null
                ? Loc.F("{0} · discharging · {1} h", percent, clock)
                : Loc.F("{0} · discharging", percent);
        }

        private static double ReadNumber(string json, string key)
        {
            // Other metrics in the same helper bundle can contain decimal commas, making the
            // whole bundle invalid JSON. Keep reading these integer fields independently.
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[\\d.]+)");
            if (!m.Success) return -1;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1;
        }
    }
}
