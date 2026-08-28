using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// When each game was last played, merged from every source that knows something.
    ///
    /// THE STORE IS OURS AND IT OUTLIVES ITS SOURCES. The helper's log is the only place a non-Steam
    /// play event is recorded, and those logs are rotated away after a few days. Reading them fresh on
    /// every start would therefore produce a history that quietly resets. They are harvested INTO this
    /// file instead, and this file is only ever added to.
    ///
    /// The key is the normalised install folder, not the title: a title changes (editions, re-brands,
    /// localisation), an install folder does not.
    /// </summary>
    public sealed class PlayHistory
    {
        private readonly Dictionary<string, DateTime> _lastPlayed =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Exe path last seen running out of an install folder. Comes from the helper log and
        /// is the answer to a question Steam cannot answer: WHICH executable is the game. Only used
        /// for matching a ClawTweaks per-game profile.</summary>
        private readonly Dictionary<string, string> _exeByDir =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;

        public static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "playhistory.json");

        /// <summary>
        /// Which helper logs have already been harvested, as name -> "length:lastWriteUtcTicks".
        /// Beside the history rather than inside it: the history file is a published shape and
        /// this is bookkeeping.
        /// </summary>
        private static string HarvestManifestPath => Path.Combine(
            Path.GetDirectoryName(StorePath), "playharvest.json");

        private static string HelperLogDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "MSIClaw.ClawTweaks_7eszav2039cvc", "LocalCache", "Local");

        private sealed class Record
        {
            public string Dir { get; set; }
            public long LastPlayedUtcTicks { get; set; }
            public string Exe { get; set; }
        }

        public static PlayHistory Load()
        {
            var h = new PlayHistory();
            try
            {
                if (!File.Exists(StorePath)) return h;
                var records = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(StorePath));
                if (records == null) return h;
                foreach (var r in records)
                {
                    if (string.IsNullOrWhiteSpace(r?.Dir)) continue;
                    string key = Normalize(r.Dir);
                    if (r.LastPlayedUtcTicks > 0)
                        h._lastPlayed[key] = new DateTime(r.LastPlayedUtcTicks, DateTimeKind.Utc);
                    if (!string.IsNullOrWhiteSpace(r.Exe)) h._exeByDir[key] = r.Exe;
                }
            }
            catch { }
            return h;
        }

        public void SaveIfChanged()
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                var records = new List<Record>();
                foreach (var kv in _lastPlayed)
                {
                    _exeByDir.TryGetValue(kv.Key, out string exe);
                    records.Add(new Record { Dir = kv.Key, LastPlayedUtcTicks = kv.Value.Ticks, Exe = exe });
                }
                foreach (var kv in _exeByDir)
                    if (!_lastPlayed.ContainsKey(kv.Key))
                        records.Add(new Record { Dir = kv.Key, LastPlayedUtcTicks = 0, Exe = kv.Value });

                // Write beside the target and move into place: an interrupted write of the file
                // itself would leave a truncated JSON that fails to parse on the next start, and the
                // whole history would be gone for a crash that had nothing to do with it.
                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = false }));
                File.Move(tmp, StorePath, overwrite: true);
                _dirty = false;
            }
            catch { }
        }

        /// <summary>
        /// Records a play time. MERGED ON MAXIMUM, never by rank: the newest plausible timestamp
        /// wins whatever it came from. Overwriting by source rank would let an older Steam field
        /// replace a log entry from an hour ago.
        /// </summary>
        public void Note(string installDir, DateTime whenLocal)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return;
            DateTime utc = whenLocal.ToUniversalTime();
            if (utc > DateTime.UtcNow.AddDays(1)) return; // a clock skewed into the future is not data
            string key = Normalize(installDir);
            if (_lastPlayed.TryGetValue(key, out var existing) && existing >= utc) return;
            _lastPlayed[key] = utc;
            _dirty = true;
        }

        public void NoteExe(string installDir, string exePath)
        {
            if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(exePath)) return;
            string key = Normalize(installDir);
            if (_exeByDir.TryGetValue(key, out var existing) &&
                string.Equals(existing, exePath, StringComparison.OrdinalIgnoreCase)) return;
            _exeByDir[key] = exePath;
            _dirty = true;
        }

        public DateTime? LastPlayedFor(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return null;
            return _lastPlayed.TryGetValue(Normalize(installDir), out var utc) ? utc.ToLocalTime() : (DateTime?)null;
        }

        public string ExeFor(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return null;
            return _exeByDir.TryGetValue(Normalize(installDir), out var exe) ? exe : null;
        }

        /// <summary>Writes what the store knows onto the entries, without ever lowering a value the
        /// entry already carries (Steam's own LastPlayed is exact and may be newer than anything we
        /// harvested).</summary>
        public void ApplyTo(IEnumerable<GameEntry> games)
        {
            foreach (var g in games)
            {
                if (g?.InstallDir == null) continue;
                if (g.LastPlayed.HasValue) Note(g.InstallDir, g.LastPlayed.Value);

                var known = LastPlayedFor(g.InstallDir);
                if (known.HasValue && (!g.LastPlayed.HasValue || known.Value > g.LastPlayed.Value))
                    g.LastPlayed = known;

                if (g.ExePath == null) g.ExePath = ExeFor(g.InstallDir);
                else NoteExe(g.InstallDir, g.ExePath);
            }
        }

        // Matches the helper line that records a game being latched onto a process. It carries a
        // timestamp, the title and the full executable path, and the path is the point: it is the
        // hard key back from a play event to an install folder.
        private static readonly Regex LatchedLine = new Regex(
            @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+.*?\[GameDetection\] Latched '(?<title>[^']*)' to PID=\d+ \(key='(?<key>[^']*)'",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Harvests play events out of the helper's own logs.
        ///
        /// The Latched line carries a timestamp, a title AND the full exe path, which is what makes
        /// this worth doing at all: the exe path is the hard key back to an install folder.
        ///
        /// FILTERED BY INSTALL FOLDER, deliberately. The logs also latch onto things that are not
        /// games - measured here: "Steam Big Picture Mode is launching", "One Game Launcher",
        /// "StreamLight", msedge.exe. Keeping only exe paths that sit inside a folder we already know
        /// from a store throws all of that away with the very same test that assigns the entry.
        ///
        /// Streams line by line and matches before doing anything else: one helper log has been
        /// measured at 8.8 MB after an hour of play, and reading those into memory to find a handful
        /// of lines would be the expensive part of starting the library.
        /// </summary>
        public void HarvestHelperLogs(IReadOnlyList<GameEntry> games, CancellationToken ct)
        {
            if (games == null || games.Count == 0) return;

            var dirs = new List<string>();
            foreach (var g in games)
                if (!string.IsNullOrWhiteSpace(g?.InstallDir)) dirs.Add(Normalize(g.InstallDir));
            if (dirs.Count == 0) return;

            string logDir = HelperLogDir;
            if (!Directory.Exists(logDir)) return;

            string[] files;
            try { files = Directory.GetFiles(logDir, "helper_*.log"); }
            catch { return; }

            // A LOG THAT HAS NOT CHANGED HOLDS NOTHING NEW.
            //
            // Measured on this machine: 36 files, 11 MB, 78,387 lines read on EVERY library open
            // to find 67 matching lines - and the result was already saved from last time. Only
            // the newest file can still grow, and it fails the size/timestamp test by itself, so
            // it is always read.
            //
            // The manifest is thrown away when the SET OF INSTALL FOLDERS changes, and that is a
            // correctness requirement rather than a nicety: the filter below keeps only paths
            // inside a folder some store already knows, so a game installed later has its old
            // play events sitting in files we would otherwise never open again.
            var manifest = LoadHarvestManifest(dirs);

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    string stamp = info.Length.ToString(CultureInfo.InvariantCulture) + ":" +
                                   info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    if (manifest.Seen.TryGetValue(info.Name, out string had) && had == stamp) continue;
                    // Shared read: the helper holds these open and writes to them continuously.
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.IndexOf("Latched", StringComparison.Ordinal) < 0) continue;
                            var m = LatchedLine.Match(line);
                            if (!m.Success) continue;

                            string exe = m.Groups["key"].Value;
                            if (string.IsNullOrWhiteSpace(exe)) continue;
                            string normExe = Normalize(exe);

                            string dir = null;
                            foreach (string d in dirs)
                                if (normExe.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) { dir = d; break; }
                            if (dir == null) continue;

                            if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;

                            Note(dir, when);
                            NoteExe(dir, exe);
                        }
                    }

                    // Stamped only after the whole file was read: a cancellation or a read error
                    // partway through must not mark it done.
                    manifest.Seen[info.Name] = stamp;
                }
                catch { }
            }

            SaveHarvestManifest(manifest);
        }

        private sealed class HarvestManifest
        {
            public string DirsKey { get; set; }
            public Dictionary<string, string> Seen { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string DirsKeyFor(List<string> dirs)
        {
            var copy = new List<string>(dirs);
            copy.Sort(StringComparer.OrdinalIgnoreCase);
            return copy.Count + "|" + string.Join("|", copy).GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        private static HarvestManifest LoadHarvestManifest(List<string> dirs)
        {
            string key = DirsKeyFor(dirs);
            try
            {
                if (File.Exists(HarvestManifestPath))
                {
                    var m = JsonSerializer.Deserialize<HarvestManifest>(File.ReadAllText(HarvestManifestPath));
                    if (m != null && m.Seen != null && m.DirsKey == key) return m;
                }
            }
            catch { }
            return new HarvestManifest { DirsKey = key };
        }

        private static void SaveHarvestManifest(HarvestManifest m)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HarvestManifestPath));
                string tmp = HarvestManifestPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m));
                File.Move(tmp, HarvestManifestPath, overwrite: true);
            }
            catch { }
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return (path ?? string.Empty).TrimEnd('\\', '/'); }
        }
    }
}
