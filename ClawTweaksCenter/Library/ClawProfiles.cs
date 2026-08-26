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

        /// <summary>Performance profile path -> the XML it was read from, so the launch screen can
        /// go back to the file for the VALUES without scanning the folder a second time.</summary>
        private static Dictionary<string, string> _perfFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Full exe paths that have a per-game controller profile.</summary>
        private static List<string> _ctrlPaths = new List<string>();

        /// <summary>
        /// THE TWO KINDS LIVE UNDER DIFFERENT PACKAGES, and that is not a mistake in this file - it is
        /// the state of the app repo. The helper resolves its profile folder through
        /// ProfileManager.GetLocalFolderPath, which still names the ORIGINAL GoTweaks package; the
        /// widget's own LocalState, and with it controller-profiles.tsv, is under the ClawTweaks one.
        /// Measured on this device 2026-08-26: profiles\Animal Well.xml sat under PlayandBuildCustom
        /// while Center was reading MSIClaw.ClawTweaks - so the performance badge could not appear for
        /// ANY game, on any machine, and read as "that game has no profile".
        ///
        /// BOTH are searched for BOTH kinds, deliberately. Naming one package per kind would be a
        /// second place that has to be edited on the day the app repo finally unifies them, and the
        /// failure it causes is silent - a missing badge, which is exactly what this comment exists
        /// because of. Searching both costs one Directory.Exists on a folder that is not there.
        /// </summary>
        private static readonly string[] LocalStates = BuildLocalStates();

        private static string[] BuildLocalStates()
        {
            string packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            return new[]
            {
                Path.Combine(packages, "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalState"),
                Path.Combine(packages, "MSIClaw.ClawTweaks_7eszav2039cvc", "LocalState"),
            };
        }

        public static void Refresh()
        {
            _perfFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        private static bool Matches(List<string> paths, GameEntry game) =>
            MatchedPath(paths, game) != null;

        /// <summary>The path from <paramref name="paths"/> that belongs to this game, or null.
        /// ONE rule, two callers: the badge asks whether there is one, the launch screen asks which
        /// one. A second copy of the matching would drift from this one.</summary>
        private static string MatchedPath(List<string> paths, GameEntry game)
        {
            if (game == null || paths.Count == 0) return null;

            if (!string.IsNullOrEmpty(game.ExePath))
            {
                foreach (string p in paths)
                    if (string.Equals(p, game.ExePath, StringComparison.OrdinalIgnoreCase)) return p;
            }

            if (string.IsNullOrEmpty(game.InstallDir)) return null;

            string dir = Normalise(game.InstallDir);
            if (dir.Length == 0) return null;

            foreach (string p in paths)
                if (Normalise(p).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return p;

            return null;
        }

        /// <summary>The performance profile XML for this game, or null. READ ONLY - see the class
        /// warning: the helper rewrites this file from memory on its next save.</summary>
        public static string PerformanceFileFor(GameEntry game)
        {
            string path = MatchedPath(_perfPaths, game);
            if (path == null) return null;
            return _perfFiles.TryGetValue(path, out string file) ? file : null;
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
            foreach (string root in LocalStates) ReadPerformanceProfilesFrom(root, paths);
            return paths;
        }

        private static void ReadPerformanceProfilesFrom(string root, List<string> paths)
        {
            try
            {
                string dir = Path.Combine(root, "profiles");
                if (!Directory.Exists(dir)) return;

                foreach (string file in Directory.GetFiles(dir, "*.xml"))
                {
                    try
                    {
                        var xml = XDocument.Load(file).Root;
                        if (xml == null) continue;

                        string use = (string)xml.Element("Use");
                        if (!string.Equals(use, "true", StringComparison.OrdinalIgnoreCase)) continue;

                        string path = (string)xml.Element("GameId")?.Element("Path");
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        paths.Add(path);
                        _perfFiles[path] = file;
                    }
                    catch { /* one unreadable profile must not lose the rest */ }
                }
            }
            catch { }
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
            foreach (string root in LocalStates)
            {
                try
                {
                    string file = Path.Combine(root, "controller-profiles.tsv");
                    if (!File.Exists(file)) continue;

                    foreach (string line in File.ReadAllLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        int tab = line.IndexOf('\t');
                        string exe = tab > 0 ? line.Substring(0, tab) : line;
                        if (!string.IsNullOrWhiteSpace(exe)) paths.Add(exe.Trim());
                    }
                }
                catch { }
            }
            return paths;
        }
    }
}
