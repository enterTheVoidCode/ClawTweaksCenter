using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Xbox / Microsoft Store games.
    ///
    /// MEASURED, and the obvious routes were all dead ends. In order:
    ///
    ///   1. Read each game's MicrosoftGame.config out of its folder. NO: the per-game
    ///      <c>Content\</c> folder is ACL-protected, and listing it as the logged-on user returns
    ///      exactly one entry (<c>media</c>). Center never elevates, so that file is out of reach.
    ///   2. Read the AUMID from <c>HKCU\...\AppModel\Repository\Packages</c>. NO: that key does not
    ///      exist on this machine at all.
    ///   3. Take registered packages whose InstallLocation sits under a GamingRoot. NO, and this one
    ///      looked right for a while: Stardew Valley is installed and playable, and its package lives
    ///      in <c>Program Files\WindowsApps</c> while only its data sits in <c>C:\XboxGames</c>. The
    ///      filter found zero of two installed games. It also cost 3.2 s.
    ///
    /// What works is the Start menu. <c>Get-StartApps</c> returns a name and the full AUMID for every
    /// launchable app, needs no elevation, and takes 1.6 s. Intersecting it with the folders under a
    /// GamingRoot is what separates games from the other 233 entries.
    ///
    /// Verified against both installed games:
    ///     Forza Horizon 6  -> Microsoft.ForteBaseGame_8wekyb3d8bbwe!Forzahorizon6
    ///     Stardew Valley   -> ConcernedApe.StardewValleyPC_0c8vynj4cqe4e!Game
    /// and against the 14 folders on this machine that are leftovers - none of them matched, which is
    /// the correct answer: nothing about them is launchable.
    /// </summary>
    public sealed class XboxSource : IGameSource
    {
        public GameStore Store => GameStore.Xbox;

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        /// <summary>Game folders that exist on disk but match no launchable app. Reported so the UI
        /// can tell "no Xbox games" apart from "sixteen folders and none of them usable" - those are
        /// very different answers to give a user.</summary>
        public static int OrphanFolderCount { get; private set; }

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();

            var roots = GamingRoots();
            if (roots.Count == 0) { OrphanFolderCount = 0; return games; }

            var folders = GameFolders(roots);
            if (folders.Count == 0) { OrphanFolderCount = 0; return games; }

            var startApps = StartMenuApps(ct);
            if (startApps.Count == 0) { OrphanFolderCount = folders.Count; return games; }

            var byAumid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int orphans = 0;

            foreach (string folder in folders)
            {
                ct.ThrowIfCancellationRequested();

                string key = NormalizeName(Path.GetFileName(folder));
                if (key.Length == 0 || !startApps.TryGetValue(key, out var app)) { orphans++; continue; }

                // An update leftover and the live folder normalise to the same app, so the first one
                // wins and the rest are not counted as orphans - they are the same game.
                if (!byAumid.Add(app.Aumid)) continue;

                games.Add(new GameEntry
                {
                    // The AUMID is the identity: it is stable across the sibling folders Windows
                    // leaves behind after an update (Call of Duty, Call of Duty_1).
                    Id = app.Aumid,
                    Store = GameStore.Xbox,
                    // The Start menu name, never the folder name: a folder cannot hold a colon, so
                    // "Clair Obscur: Expedition 33" is stored as "Clair Obscur- Expedition 33". The
                    // folder name is a corrupted copy of a string we can read properly elsewhere.
                    Title = app.Name,
                    InstallDir = folder,
                    // shell:appsFolder is the documented way to activate a packaged app without
                    // WinRT. explorer.exe resolves the AUMID and hands off to the app model.
                    LaunchUri = "shell:appsFolder\\" + app.Aumid,
                });
            }

            OrphanFolderCount = orphans;
            return games;
        }

        /// <summary>
        /// The Xbox install roots, one per fixed drive. Format verified byte by byte on this machine:
        /// magic <c>RGBX</c>, then <c>01 00 00 00</c>, then the relative path as UTF-16LE, null
        /// terminated. <c>C:\.GamingRoot</c> yields <c>XboxGames</c>, so <c>C:\XboxGames</c>.
        /// </summary>
        public static List<string> GamingRoots()
        {
            var roots = new List<string>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return roots; }

            foreach (var d in drives)
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string marker = Path.Combine(d.RootDirectory.FullName, ".GamingRoot");
                    if (!File.Exists(marker)) continue;

                    byte[] bytes = File.ReadAllBytes(marker);
                    if (bytes.Length <= 8) continue;
                    if (bytes[0] != (byte)'R' || bytes[1] != (byte)'G' || bytes[2] != (byte)'B' || bytes[3] != (byte)'X') continue;

                    string rel = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0').Trim();
                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    string full = Path.Combine(d.RootDirectory.FullName, rel);
                    if (Directory.Exists(full)) roots.Add(Path.GetFullPath(full));
                }
                catch { }
            }
            return roots;
        }

        private static List<string> GameFolders(List<string> roots)
        {
            var list = new List<string>();
            foreach (string root in roots)
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        // Xbox keeps its cloud-save staging alongside the games under the same root.
                        if (string.Equals(Path.GetFileName(dir), "GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(dir);
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// Folder name to comparable key. Two things have to go: the <c>_1</c> / <c>_2</c> suffix
        /// Windows appends to an update leftover, and every character a path cannot hold - a colon
        /// becomes a hyphen on disk, so "Clair Obscur: Expedition 33" and "Clair Obscur- Expedition
        /// 33" only match once punctuation is out of the way entirely.
        /// </summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int cut = name.LastIndexOf('_');
            if (cut > 0 && cut < name.Length - 1)
            {
                bool allDigits = true;
                for (int i = cut + 1; i < name.Length; i++)
                    if (!char.IsDigit(name[i])) { allDigits = false; break; }
                if (allDigits) name = name.Substring(0, cut);
            }

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private struct StartApp
        {
            public string Name;
            public string Aumid;
        }

        /// <summary>
        /// Every launchable app, by normalised name. <c>Get-StartApps</c> is a plain Windows
        /// PowerShell cmdlet - the same instrument Center already uses to read its own installed
        /// version - and needs no elevation.
        /// </summary>
        private static Dictionary<string, StartApp> StartMenuApps(CancellationToken ct)
        {
            var map = new Dictionary<string, StartApp>(StringComparer.Ordinal);

            // Single quotes only - the command is embedded in a double-quoted -Command argument.
            // The separator is a pipe because an app name can hold almost anything else.
            string output = RunPowerShell(
                "Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }", 30000);
            if (string.IsNullOrWhiteSpace(output)) return map;

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                int sep = line.LastIndexOf('|');
                if (sep <= 0 || sep >= line.Length - 1) continue;

                string name = line.Substring(0, sep).Trim();
                string aumid = line.Substring(sep + 1).Trim();
                if (name.Length == 0 || aumid.Length == 0) continue;

                string key = NormalizeName(name);
                if (key.Length == 0) continue;
                if (!map.ContainsKey(key)) map[key] = new StartApp { Name = name, Aumid = aumid };
            }
            return map;
        }

        private static string RunPowerShell(string command, int timeoutMs)
        {
            try
            {
                string winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = File.Exists(winPs) ? winPs : "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                                command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                string outp = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } return null; }
                return outp;
            }
            catch { return null; }
        }
    }
}
