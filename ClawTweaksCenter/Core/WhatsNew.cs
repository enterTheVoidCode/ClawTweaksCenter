using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ClawTweaksCenter.Core
{
    /// <summary>One version's notes: "0.4.48", its heading line, and the Markdown under it.</summary>
    public sealed class WhatsNewEntry
    {
        public Version Version;
        /// <summary>The heading as written, e.g. "0.4.48 · 3. Oktober 2026".</summary>
        public string Heading;
        public string Markdown;
    }

    /// <summary>
    /// What is new in Center, version by version (user, 2026-10-03): shown once after an update has
    /// gone through, and on demand from the start screen's "What's new" tile.
    ///
    /// The notes ship INSIDE Center (Assets\WhatsNew\whatsnew.&lt;lang&gt;.md, embedded): the screen
    /// works offline, and the notes are always the ones for the build that is running - a release page
    /// fetched over the network could describe a different one. German and English; every other
    /// language reads the English file.
    ///
    /// "After an update" is decided by version, not by a Velopack hook: the version last shown is
    /// stored, and a running version above it with notes of its own is an update to announce. So it
    /// works the same whichever way the new version arrived - Velopack, the Inno setup run over an
    /// older install, or a portable exe (user, 2026-10-03: "bei jedem Versionswechsel").
    ///
    /// A first install stores the current version without showing anything - nothing was updated.
    /// BUT 0.4.47 and older never stored a version at all, so "no stored version" alone cannot tell
    /// a first install from the update off one of those. <see cref="CaptureStartState"/> looks at
    /// Center's settings key at the very start of the process: anything already in it means Center
    /// ran here before, and the notes of the running version are shown.
    ///
    /// DEBUG BUILDS show the newest notes at every start and store nothing, so the screen can be
    /// looked at before a release (Deploy-CenterDev.ps1 -Configuration Debug).
    /// </summary>
    public static class WhatsNew
    {
        /// <summary>How many versions the history keeps.</summary>
        public const int Max = 10;

        private static List<WhatsNewEntry> _cached;
        private static UiLanguage _cachedFor;

        /// <summary>Whether Center had run on this machine before this process - read once, first
        /// thing in Main, before any setting of this start is written.</summary>
        private static bool _usedBefore;

        public static void CaptureStartState()
        {
            _usedBefore = CenterSettings.HasSettingsBesidesWhatsNew();
        }

        public static Version Running
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>The newest <see cref="Max"/> entries, newest first.</summary>
        public static List<WhatsNewEntry> All()
        {
            if (_cached != null && _cachedFor == Loc.Current) return _cached;
            string text = ReadResource(Loc.Current == UiLanguage.German ? "de" : "en") ?? ReadResource("en") ?? string.Empty;
            _cached = Parse(text).OrderByDescending(e => e.Version).Take(Max).ToList();
            _cachedFor = Loc.Current;
            return _cached;
        }

        /// <summary>
        /// True once after an update: the running version is newer than the one last shown, and has
        /// notes. Marks it shown as it answers, so a crash on the way to the screen cannot turn it
        /// into a screen that comes up on every start.
        /// </summary>
        public static bool TakeUpdateToAnnounce(out WhatsNewEntry entry)
        {
            entry = null;
#if DEBUG
            entry = All().FirstOrDefault();
            InstallLog.Write("[WhatsNew] debug build: showing " + (entry?.Version.ToString() ?? "nothing") + " at every start");
            return entry != null;
#else
            var running = Running;
            string seenText = CenterSettings.WhatsNewSeenVersion;
            CenterSettings.WhatsNewSeenVersion = running.ToString();
            string from;
            if (string.IsNullOrEmpty(seenText) || !Version.TryParse(seenText, out var seen))
            {
                // Nothing stored: a first install - or an update from 0.4.47 or older, which never
                // stored anything. Told apart by whether Center had run here before this start.
                if (!_usedBefore) return false;
                from = "a version without What's new";
            }
            else
            {
                if (running <= seen) return false;
                from = seen.ToString();
            }
            entry = All().FirstOrDefault(e => e.Version == running);
            InstallLog.Write("[WhatsNew] updated " + from + " -> " + running + (entry != null ? ", notes shown" : ", no notes for it"));
            return entry != null;
#endif
        }

        private static List<WhatsNewEntry> Parse(string text)
        {
            var list = new List<WhatsNewEntry>();
            // Comments are for whoever edits the file.
            text = Regex.Replace(text.Replace("\r\n", "\n"), "<!--.*?-->", "", RegexOptions.Singleline);
            WhatsNewEntry current = null;
            var body = new List<string>();
            void Flush()
            {
                if (current == null) return;
                current.Markdown = string.Join("\n", body).Trim();
                list.Add(current);
                body.Clear();
            }
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("## "))
                {
                    Flush();
                    string heading = line.Substring(3).Trim();
                    var m = Regex.Match(heading, "^(\\d+\\.\\d+(?:\\.\\d+)?)");
                    current = m.Success && Version.TryParse(m.Groups[1].Value, out var v)
                        ? new WhatsNewEntry { Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build)), Heading = heading }
                        : null;
                    continue;
                }
                if (current != null) body.Add(line);
            }
            Flush();
            return list;
        }

        private static string ReadResource(string lang)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("whatsnew." + lang + ".md", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch { return null; }
        }
    }
}
