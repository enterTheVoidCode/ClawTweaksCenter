using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace ClawTweaksCenter.Library
{
    /// <summary>Which ClawTweaks profiles a game has.</summary>
    [Flags]
    public enum ClawProfileKinds
    {
        None = 0,
        /// <summary>A per-game performance profile that is switched ON (TDP, fan curve, FPS cap, …).</summary>
        Performance = 1,
        /// <summary>A per-game controller profile (buttons, gyro, vibration).</summary>
        Controller = 2,
    }

    /// <summary>
    /// Reads which games ClawTweaks has a per-game profile for. READ ONLY - see the warning below.
    ///
    /// The two kinds are stored completely differently, and that is not tidiness we can fix from here:
    ///
    ///   PERFORMANCE  LocalState\profiles\&lt;exeName&gt;.xml, one plain XML per game. Each carries the
    ///                full exe path in &lt;GameId&gt;&lt;Path&gt;, and a &lt;Use&gt; flag saying whether it is
    ///                switched on at all.
    ///   CONTROLLER   the widget's UWP LocalSettings, which another process cannot read. The widget
    ///                therefore mirrors them to LocalState\controller-profiles.tsv - see
    ///                GamingWidget.MirrorControllerProfileGamesToFile in the app repo.
    ///
    /// ⚠️ DO NOT WRITE TO THESE FILES. The helper keeps every performance profile in memory and
    /// rewrites the file on its own next save, so an edit made here disappears without a word. Giving
    /// Center the ability to edit means adding pipe commands on both sides first - the shared Function
    /// enum is ordinal and mirrored, so that is a contract change, not a patch.
    /// </summary>
    public static class ClawProfiles
    {
        /// <summary>Full exe paths that have a performance profile with Use=true.</summary>
        private static List<string> _perfPaths = new List<string>();

        /// <summary>Full exe paths that have a per-game controller profile.</summary>
        private static List<string> _ctrlPaths = new List<string>();

        private static string LocalState => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "MSIClaw.ClawTweaks_7eszav2039cvc", "LocalState");

        public static void Refresh()
        {
            _perfPaths = ReadPerformanceProfiles();
            _ctrlPaths = ReadControllerMirror();
        }

        /// <summary>
        /// Which profiles this game has.
        ///
        /// MATCHED ON THE INSTALL FOLDER, not on the exe. Center usually does not know which exe a
        /// Steam game is - Steam does not say, and guessing among launcher, crash handler and shipping
        /// binary is wrong more often than it is right (see GameEntry.ExePath). The profile knows its
        /// own full path, so asking "does that path live inside this game's folder" needs no guess and
        /// works for a game that has never been started while ClawTweaks was running.
        ///
        /// ExePath is still preferred when we do have it: an install folder that contains a second
        /// game's files would otherwise claim its profile too.
        /// </summary>
        public static ClawProfileKinds For(GameEntry game)
        {
            if (game == null) return ClawProfileKinds.None;

            var kinds = ClawProfileKinds.None;
            if (Matches(_perfPaths, game)) kinds |= ClawProfileKinds.Performance;
            if (Matches(_ctrlPaths, game)) kinds |= ClawProfileKinds.Controller;
            return kinds;
        }

        private static bool Matches(List<string> paths, GameEntry game)
        {
            if (paths.Count == 0) return false;

            if (!string.IsNullOrEmpty(game.ExePath))
            {
                foreach (string p in paths)
                    if (string.Equals(p, game.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (string.IsNullOrEmpty(game.InstallDir)) return false;

            string dir = Normalise(game.InstallDir);
            if (dir.Length == 0) return false;

            foreach (string p in paths)
                if (Normalise(p).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>Trailing separator included, so "…\Game" cannot match "…\Game2".</summary>
        private static string Normalise(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    full += Path.DirectorySeparatorChar;
                return full;
            }
            catch { return path ?? string.Empty; }
        }

        /// <summary>
        /// The performance profiles. Only ones with &lt;Use&gt;true&lt;/Use&gt; count: switching a per-game
        /// profile off leaves the file behind, so a badge driven by the file's existence alone would
        /// mark a game whose profile is not being applied to anything.
        /// </summary>
        private static List<string> ReadPerformanceProfiles()
        {
            var paths = new List<string>();
            try
            {
                string dir = Path.Combine(LocalState, "profiles");
                if (!Directory.Exists(dir)) return paths;

                foreach (string file in Directory.GetFiles(dir, "*.xml"))
                {
                    try
                    {
                        var doc = XDocument.Load(file);
                        var root = doc.Root;
                        if (root == null) continue;

                        string use = (string)root.Element("Use");
                        if (!string.Equals(use, "true", StringComparison.OrdinalIgnoreCase)) continue;

                        string path = (string)root.Element("GameId")?.Element("Path");
                        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                    }
                    catch { /* one unreadable profile must not lose the rest */ }
                }
            }
            catch { }
            return paths;
        }

        /// <summary>
        /// The controller mirror: one "exePath\tgameName" per line, written by the widget.
        ///
        /// A missing file is the NORMAL state on an installation whose widget predates the mirror,
        /// and on one where the Game Bar has not been opened since. It means "we do not know", which
        /// here is the same as showing one badge fewer.
        /// </summary>
        private static List<string> ReadControllerMirror()
        {
            var paths = new List<string>();
            try
            {
                string file = Path.Combine(LocalState, "controller-profiles.tsv");
                if (!File.Exists(file)) return paths;

                foreach (string line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int tab = line.IndexOf('\t');
                    string exe = tab > 0 ? line.Substring(0, tab) : line;
                    if (!string.IsNullOrWhiteSpace(exe)) paths.Add(exe.Trim());
                }
            }
            catch { }
            return paths;
        }
    }
}
