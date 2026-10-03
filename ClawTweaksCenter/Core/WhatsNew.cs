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
    /// stored, and a running version above it with notes of its own is an update to announce. A first
    /// install stores the current version without showing anything - nothing was updated.
    /// </summary>
    public static class WhatsNew
    {
        /// <summary>How many versions the history keeps.</summary>
        public const int Max = 10;

        private static List<WhatsNewEntry> _cached;
        private static UiLanguage _cachedFor;

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
            var running = Running;
            string seenText = CenterSettings.WhatsNewSeenVersion;
            CenterSettings.WhatsNewSeenVersion = running.ToString();
            if (string.IsNullOrEmpty(seenText) || !Version.TryParse(seenText, out var seen)) return false;   // first install
            if (running <= seen) return false;
            entry = All().FirstOrDefault(e => e.Version == running);
            InstallLog.Write("[WhatsNew] updated " + seen + " -> " + running + (entry != null ? ", notes shown" : ", no notes for it"));
            return entry != null;
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
