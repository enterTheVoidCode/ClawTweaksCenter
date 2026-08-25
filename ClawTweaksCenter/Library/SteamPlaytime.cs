using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using ValveKeyValue;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Total playtime per Steam game, read from Steam's own file. No account, no API key, no network.
    ///
    /// Steam keeps it in the per-user local config:
    ///
    ///   &lt;Steam&gt;\userdata\&lt;accountId&gt;\config\localconfig.vdf
    ///     UserLocalConfigStore ▸ Software ▸ Valve ▸ Steam ▸ apps ▸ &lt;appid&gt;
    ///         Playtime      171          total MINUTES
    ///         Playtime2wks   49          last two weeks, minutes
    ///         LastPlayed   1783357947    unix seconds
    ///
    /// STEAM ONLY, and that is not a gap we can close. Epic records no playtime on this machine at
    /// all, Xbox keeps it in the cloud behind a sign-in, and a ROM's time is Playnite's business. So
    /// the library shows the figure where it exists and says nothing where it does not - see
    /// UpdateSelectedTitle. An empty "0 h" would be a claim, not a blank.
    /// </summary>
    public static class SteamPlaytime
    {
        /// <summary>appid -&gt; total minutes.</summary>
        private static Dictionary<string, int> _minutes;

        /// <summary>appid -&gt; when Steam says it was last played.</summary>
        private static Dictionary<string, DateTime> _lastPlayed;

        public static void Refresh()
        {
            var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
            var last = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            try
            {
                string file = LocalConfigPath();
                if (file != null)
                {
                    // Shared read, same reason as SteamSource: Steam holds this file open while it
                    // runs, and figures that only appear with Steam closed are figures nobody sees.
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        // ⚠️ HasEscapeSequences, and WITHOUT IT NOTHING HERE WORKS. localconfig.vdf
                        // stores LaunchOptions with escaped quotes:
                        //     "LaunchOptions"  "\"C:\...\start_game_in_offline_mode.exe\" %command%"
                        // The default reader treats the backslash literally, walks into the value as
                        // if it were structure, and throws "Attempted to finalize object while in
                        // state InObjectBetweenKeyAndValue". Measured on this machine: the whole file
                        // failed at line 1228 and every game reported zero minutes.
                        //
                        // SteamSource does NOT pass this and is right not to: appmanifest and
                        // libraryfolders carry no escapes, and enabling them there would change how
                        // install paths with a backslash are read.
                        var options = new KVSerializerOptions { HasEscapeSequences = true };
                        var root = KVSerializer.Create(KVSerializationFormat.KeyValues1Text)
                                               .Deserialize(fs, options)?.Root;
                        var apps = Child(Child(Child(Child(root, "Software"), "Valve"), "Steam"), "apps");
                        if (apps != null)
                        {
                            // Enumerating a KVObject yields Key/Value pairs, not named nodes - the
                            // same shape SteamSource.ValueOf walks. The KEY is the appid here.
                            foreach (var app in apps)
                            {
                                string id = app.Key;
                                if (string.IsNullOrEmpty(id)) continue;

                                int m = ReadInt(app.Value, "Playtime");
                                if (m > 0) minutes[id] = m;

                                long unix = ReadInt(app.Value, "LastPlayed");
                                if (unix > 0) last[id] = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // LOGGED, not swallowed. The first version of this method had a bare catch, and the
                // parser threw on every single machine - so the feature was silently dead and the
                // only symptom was "no playtime", which looks exactly like "Steam has no figures".
                // A guard that hides its own failure makes the next field report unanswerable.
                Core.InstallLog.Write("Steam playtime unavailable: " + ex.Message);
            }

            _minutes = minutes;
            _lastPlayed = last;
        }

        /// <summary>A child block by name, case-insensitively. Steam is not consistent about
        /// capitalisation across generations of these files.</summary>
        private static KVObject Child(KVObject parent, string name)
        {
            if (parent == null) return null;
            try
            {
                foreach (var c in parent)
                    if (string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase)) return c.Value;
            }
            catch { }
            return null;
        }

        /// <summary>Total minutes, or 0 when Steam has no figure for this app.</summary>
        public static int MinutesFor(string appId)
        {
            if (_minutes == null || string.IsNullOrEmpty(appId)) return 0;
            return _minutes.TryGetValue(appId, out int m) ? m : 0;
        }

        public static DateTime? LastPlayedFor(string appId)
        {
            if (_lastPlayed == null || string.IsNullOrEmpty(appId)) return null;
            return _lastPlayed.TryGetValue(appId, out DateTime d) ? d : (DateTime?)null;
        }

        /// <summary>
        /// "45 min" under an hour, "12 h" above it, "1.5 h" in between.
        ///
        /// No minutes next to hours: at 340 hours the minutes are noise, and this line has to stay
        /// short enough to sit beside the store name and a date.
        /// </summary>
        public static string Format(int minutes)
        {
            if (minutes <= 0) return null;
            if (minutes < 60) return minutes + " min";

            double hours = minutes / 60.0;
            return hours < 10
                ? hours.ToString("0.#", CultureInfo.CurrentCulture) + " h"
                : ((int)Math.Round(hours)).ToString(CultureInfo.CurrentCulture) + " h";
        }

        /// <summary>
        /// The active Steam account's localconfig.vdf.
        ///
        /// ActiveUser is the right answer and is NOT always available: it is 0 whenever Steam is not
        /// running, which on a handheld is most of the time Center is open. So it is used when it is
        /// there, and otherwise the newest userdata folder wins - on a machine with one account those
        /// are the same folder anyway, and on a shared machine the newest is the one being used.
        /// </summary>
        private static string LocalConfigPath()
        {
            string steam = SteamSource.SteamPath();
            if (steam == null) return null;

            string userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) return null;

            string active = ActiveUserFolder(userdata);
            if (active != null) return active;

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Directory.GetDirectories(userdata))
            {
                string cfg = Path.Combine(dir, "config", "localconfig.vdf");
                if (!File.Exists(cfg)) continue;

                DateTime t = File.GetLastWriteTimeUtc(cfg);
                if (t > bestTime) { bestTime = t; best = cfg; }
            }
            return best;
        }

        private static string ActiveUserFolder(string userdata)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object raw = key?.GetValue("ActiveUser");
                    if (raw == null) return null;

                    int id = Convert.ToInt32(raw);
                    if (id == 0) return null;          // Steam is not running - not an error

                    string cfg = Path.Combine(userdata, id.ToString(CultureInfo.InvariantCulture),
                                              "config", "localconfig.vdf");
                    return File.Exists(cfg) ? cfg : null;
                }
            }
            catch { return null; }
        }

        private static int ReadInt(KVObject node, string name)
        {
            var v = Child(node, name);
            if (v == null || v.IsCollection || v.IsArray) return 0;
            try
            {
                return int.TryParse(v.ToString(CultureInfo.InvariantCulture),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
            }
            catch { return 0; }
        }
    }
}
