using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// The smaller PC stores: Ubisoft Connect, EA (Origin), Battle.net and GOG Galaxy.
    ///
    /// FOUR SEPARATE SOURCES IN ONE FILE, not one source with four branches. Each keeps its own
    /// <see cref="IGameSource.Store"/>, so a store that throws is named in SourceErrors on its own
    /// and the other three are unaffected — the same contract Steam, Epic and Xbox already have.
    /// They share a file because each of them is thirty lines of registry reading and splitting
    /// them into four files would hide how much they have in common.
    ///
    /// ⚠️ WHAT HAS AND HAS NOT BEEN SEEN WORKING. Battle.net is verified end to end against a real
    /// Diablo IV install, download and all. UBISOFT, EA AND GOG ARE NOT: the development machine has
    /// their launchers and not one game from any of them — measured while writing this, 13 leftover
    /// "Origin Games" registry keys, an empty Origin LocalContent tree and one Ubisoft install key
    /// with no values in it. Treat a bug report about those three as likely real rather than as user
    /// error, exactly as EpicSource says of itself.
    ///
    /// THAT MEASUREMENT IS ALSO WHY EVERY SOURCE HERE DEMANDS A DIRECTORY THAT EXISTS. All four
    /// stores leave their registry keys behind after an uninstall, and two of them (EA above all)
    /// register entitlements the account owns rather than what is on the disk. Listing those would
    /// have put eight uninstalled EA games on this very machine's shelf, each with a launch button
    /// that starts a download. A folder on disk is the only claim any of them makes that we can
    /// check.
    ///
    /// WHAT IS DELIBERATELY NOT HERE: Rockstar, Riot and Amazon. Rockstar and Riot are reachable
    /// the same way, but neither has a launch route we could state with any confidence, and Amazon
    /// keeps its inventory in a SQLite database. An entry that lists correctly and then fails to
    /// start is worse than an entry that is not there.
    /// </summary>
    internal static class StoreRegistry
    {
        /// <summary>
        /// One subkey path, read out of BOTH registry views.
        ///
        /// Every one of these stores is a 32-bit application, so its keys live under WOW6432Node on
        /// a 64-bit Windows — but reading only the 32-bit view would be a guess in the other
        /// direction. Both are read and the results merged; a key present in both is the same key.
        /// </summary>
        /// <remarks>Collected up front rather than yielded: an iterator that owns registry handles
        /// leaks the parent key the moment a caller stops early, and every caller here is a loop that
        /// can be cancelled.</remarks>
        public static List<(string Name, RegistryKey Key)> Subkeys(RegistryHive hive, string path)
        {
            var found = new List<(string, RegistryKey)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                RegistryKey root = null;
                try
                {
                    root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                    if (root == null) continue;

                    foreach (string name in root.GetSubKeyNames())
                    {
                        if (!seen.Add(name)) continue;
                        RegistryKey sub = null;
                        try { sub = root.OpenSubKey(name); } catch { }
                        if (sub != null) found.Add((name, sub));
                    }
                }
                catch { }
                finally { root?.Dispose(); }
            }
            return found;
        }

        public static string Value(RegistryKey key, string name)
        {
            try { return key?.GetValue(name) as string; }
            catch { return null; }
        }

        /// <summary>The directory a store claims, or null when it does not exist. Stores write these
        /// with forward slashes and trailing separators often enough that normalising here is worth
        /// more than four copies of the same TrimEnd.</summary>
        public static string Directory(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                string full = Path.GetFullPath(raw.Replace('/', '\\').TrimEnd('\\'));
                return System.IO.Directory.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Every uninstall entry on the machine, from all three hives and both views.
        ///
        /// Two of the four stores describe their games ONLY here: Ubisoft puts the title in an
        /// "Uplay Install &lt;id&gt;" entry, and Battle.net has no inventory of its own that can be
        /// read without parsing its agent database. Enumerating this hive is a few hundred keys and
        /// is done once per scan, not once per game.
        /// </summary>
        public static List<UninstallEntry> Uninstalls()
        {
            var list = new List<UninstallEntry>();
            var hives = new[]
            {
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (hive, path) in hives)
            {
                foreach (var (name, key) in Subkeys(hive, path))
                {
                    using (key)
                    {
                        list.Add(new UninstallEntry
                        {
                            KeyName = name,
                            DisplayName = Value(key, "DisplayName"),
                            InstallLocation = Value(key, "InstallLocation"),
                            UninstallString = Value(key, "UninstallString"),
                        });
                    }
                }
            }
            return list;
        }

        public sealed class UninstallEntry
        {
            public string KeyName;
            public string DisplayName;
            public string InstallLocation;
            public string UninstallString;
        }
    }

    /// <summary>
    /// Ubisoft Connect. Its launcher keeps one key per installed game, and the key holds nothing but
    /// the folder — the title has to come from the matching uninstall entry.
    /// </summary>
    public sealed class UbisoftSource : IGameSource
    {
        public GameStore Store => GameStore.Ubisoft;

        private const string InstallsPath = @"SOFTWARE\Ubisoft\Launcher\Installs";

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var u in StoreRegistry.Uninstalls())
            {
                // "Uplay Install 1234" — the number is the same id the Installs key is named after.
                if (u.KeyName == null || !u.KeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)) continue;
                string id = u.KeyName.Substring("Uplay Install ".Length).Trim();
                if (id.Length > 0 && !string.IsNullOrWhiteSpace(u.DisplayName)) titles[id] = u.DisplayName;
            }

            foreach (var (id, key) in StoreRegistry.Subkeys(RegistryHive.LocalMachine, InstallsPath))
            {
                ct.ThrowIfCancellationRequested();
                using (key)
                {
                    // An install key with no InstallDir at all is what an uninstalled game leaves
                    // behind — there is one of those on the development machine right now.
                    string dir = StoreRegistry.Directory(StoreRegistry.Value(key, "InstallDir"));
                    if (dir == null) continue;

                    titles.TryGetValue(id, out string title);

                    games.Add(new GameEntry
                    {
                        Id = id,
                        Store = GameStore.Ubisoft,
                        // The folder name is the fallback, not the first choice: Ubisoft names those
                        // after the internal product, so "Anno 1800" can sit in "Anno 1800" or in
                        // "AC Valhalla" depending on the release year.
                        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(dir) : title,
                        InstallDir = dir,
                        // The trailing 0 is the launch-parameter index. Every entry the launcher
                        // writes has one, and 0 is "the game" as opposed to a bonus executable.
                        LaunchUri = "uplay://launch/" + id + "/0",
                    });
                }
            }
            return games;
        }
    }

    /// <summary>
    /// EA, covering both the Origin era and the EA app that replaced it — they share the registry
    /// key, which is why this is one source and not two.
    ///
    /// THE REGISTRY ALONE IS NOT ENOUGH HERE and this is the one store where that really bites.
    /// "Origin Games\&lt;id&gt;" carries a DisplayName and a locale, and no path whatsoever. It is
    /// also written for things that are not games: measured on this machine, 5 of 13 entries are
    /// texture packs and multiplayer data blobs for one title. So the folder has to be found
    /// elsewhere, and an entry whose folder cannot be found is dropped.
    /// </summary>
    public sealed class EaSource : IGameSource
    {
        public GameStore Store => GameStore.EA;

        private const string GamesPath = @"SOFTWARE\Origin Games";

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();
            var byId = InstallDirsFromUninstalls();

            foreach (var (id, key) in StoreRegistry.Subkeys(RegistryHive.LocalMachine, GamesPath))
            {
                ct.ThrowIfCancellationRequested();
                using (key)
                {
                    string title = StoreRegistry.Value(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // Newer installs do put the folder in the key. Older ones do not, and then the
                    // uninstall entry is the only place it exists.
                    string dir = StoreRegistry.Directory(StoreRegistry.Value(key, "InstallDir"))
                              ?? StoreRegistry.Directory(StoreRegistry.Value(key, "Install Dir"));
                    if (dir == null && byId.TryGetValue(id, out string fromUninstall)) dir = fromUninstall;
                    if (dir == null) continue;

                    games.Add(new GameEntry
                    {
                        Id = id,
                        Store = GameStore.EA,
                        Title = title,
                        InstallDir = dir,
                        // origin2:// is what the EA app registers today and what the Origin client
                        // registered before it, so one URI covers both generations.
                        LaunchUri = "origin2://game/launch?offerIds=" + Uri.EscapeDataString(id),
                    });
                }
            }
            return games;
        }

        /// <summary>Offer id to folder, for the entries EA registers with an uninstall command that
        /// names the id. The key name is the offer id itself on the EA app; on Origin the id is
        /// inside the uninstall command line instead.</summary>
        private static Dictionary<string, string> InstallDirsFromUninstalls()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in StoreRegistry.Uninstalls())
            {
                string dir = StoreRegistry.Directory(u.InstallLocation);
                if (dir == null) continue;

                if (!string.IsNullOrWhiteSpace(u.KeyName)) map[u.KeyName] = dir;

                string cmd = u.UninstallString;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                int at = cmd.IndexOf("offerIds=", StringComparison.OrdinalIgnoreCase);
                if (at < 0) at = cmd.IndexOf("offerId=", StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                int start = cmd.IndexOf('=', at) + 1;
                int end = start;
                while (end < cmd.Length && (char.IsLetterOrDigit(cmd[end]) || cmd[end] == '_' || cmd[end] == '-')) end++;
                if (end > start) map[cmd.Substring(start, end - start)] = dir;
            }
            return map;
        }
    }

    /// <summary>
    /// Battle.net.
    ///
    /// TWO SOURCES, in this order:
    ///   1. <c>%ProgramData%\Battle.net\Agent\aggregate.json</c> - the client's own list of what it
    ///      considers installed. Plain JSON, and it carries the official launch URI and a last-played
    ///      timestamp, neither of which can be derived from anywhere else.
    ///   2. The uninstall entries, whose command line carries the product uid. The fallback for a
    ///      client generation that does not write the file.
    ///
    /// ⚠ NEITHER OF THEM ANSWERS "IS IT FINISHED DOWNLOADING" - see <see cref="PatchFinished"/>.
    /// Measured on a live Diablo IV install: at 73% downloaded, the uninstall entry was already
    /// there, the folder was already there with 64 GB and a runnable-looking Diablo IV.exe in it, and
    /// aggregate.json already listed it under "installed".
    /// </summary>
    public sealed class BattleNetSource : IGameSource
    {
        public GameStore Store => GameStore.BattleNet;

        private static string AgentDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent");

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = FromAggregate(ct);
            if (games.Count > 0) return games;
            return FromUninstallEntries(ct);
        }

        /// <summary>
        /// Whether the last install or patch of this folder RAN TO COMPLETION.
        ///
        /// The signal is a hidden one-byte <c>.patch.result</c> in the game's own folder. Measured on
        /// a Diablo IV download, and this is the whole of the evidence:
        ///
        ///   during the download   game folder has .build.info and .product.db, NO .patch.result
        ///   the second it ended   .patch.result appears, containing "0", timestamped to the exact
        ///                         second the agent logged "OP_UPDATE for 'fenris' completed"
        ///
        /// Nothing else moved: the .battle.net working folders stayed, aggregate.json was
        /// byte-identical before and after, .build.info was unchanged.
        ///
        /// ⚠ ONE OBSERVATION OF ONE PRODUCT. The rule is therefore deliberately asymmetric: ABSENT
        /// means "not ready" and hides the entry, and any value that is PRESENT counts as ready. A
        /// non-zero result presumably means a failed patch, but that has not been seen here, and
        /// inventing a meaning for a value nobody has measured is how a working game disappears from
        /// somebody's library.
        ///
        /// The alternatives were worse, and both were tried: the agent's local HTTP API (its port is
        /// in Agent.dat) has exactly this data as "state"/"progress"/"paused" and answers 401 without
        /// a session token, and the same JSON is in an 8 MB rolling agent log.
        /// </summary>
        private static bool PatchFinished(string installDir)
        {
            try { return File.Exists(Path.Combine(installDir, ".patch.result")); }
            catch { return false; }
        }

        private static List<GameEntry> FromAggregate(CancellationToken ct)
        {
            var games = new List<GameEntry>();

            string file = Path.Combine(AgentDir, "aggregate.json");
            string json;
            try
            {
                if (!File.Exists(file)) return games;
                json = File.ReadAllText(file);
            }
            catch { return games; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("installed", out var installed) ||
                    installed.ValueKind != JsonValueKind.Array) return games;

                foreach (var item in installed.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    string id = Str(item, "product_id");
                    string name = Str(item, "name");
                    string icon = Str(item, "icon_path");
                    if (id == null || name == null || icon == null) continue;

                    // The install folder is the launcher's folder. Blizzard writes these paths with
                    // forward slashes.
                    string dir;
                    try { dir = StoreRegistry.Directory(Path.GetDirectoryName(icon.Replace('/', '\\'))); }
                    catch { continue; }
                    if (dir == null || !PatchFinished(dir)) continue;

                    games.Add(new GameEntry
                    {
                        Id = id,
                        Store = GameStore.BattleNet,
                        Title = name,
                        InstallDir = dir,
                        // The client's OWN uri, not one assembled from the uid. It is "game/<id>",
                        // which a uid-only guess would have got wrong.
                        LaunchUri = Str(item, "launch_uri") ?? "battlenet://game/" + id,
                        // The only store besides Steam that records this locally, so it is the only
                        // other one whose games can reach Recent without having been played while
                        // ClawTweaks was watching. 0 means never played.
                        LastPlayed = UnixToLocal(item, "last_played_timestamp"),
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Core.InstallLog.Write("Battle.net aggregate.json unreadable: " + ex.Message); }

            return games;
        }

        private static List<GameEntry> FromUninstallEntries(CancellationToken ct)
        {
            var games = new List<GameEntry>();

            foreach (var u in StoreRegistry.Uninstalls())
            {
                ct.ThrowIfCancellationRequested();

                string cmd = u.UninstallString;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                if (cmd.IndexOf("Battle.net", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string uid = ArgumentValue(cmd, "--uid=");
                if (uid == null) continue;

                // Battle.net itself uninstalls the same way. It is a launcher, not a game.
                if (uid.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) continue;

                string dir = StoreRegistry.Directory(u.InstallLocation);
                if (dir == null || !PatchFinished(dir)) continue;
                if (string.IsNullOrWhiteSpace(u.DisplayName)) continue;

                games.Add(new GameEntry
                {
                    Id = uid,
                    Store = GameStore.BattleNet,
                    Title = u.DisplayName,
                    InstallDir = dir,
                    // Through the protocol handler rather than through Battle.net.exe with a command
                    // line: the exe route would hand GameRunTracker the LAUNCHER's process, and it
                    // would then report the game as finished the moment Battle.net closes.
                    LaunchUri = "battlenet://game/" + uid,
                });
            }
            return games;
        }

        /// <summary>The value of a --name=value argument, unquoted, or null.</summary>
        private static string ArgumentValue(string commandLine, string prefix)
        {
            int at = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            int start = at + prefix.Length;
            if (start < commandLine.Length && commandLine[start] == '"')
            {
                int close = commandLine.IndexOf('"', start + 1);
                return close > start + 1 ? commandLine.Substring(start + 1, close - start - 1) : null;
            }

            int end = start;
            while (end < commandLine.Length && !char.IsWhiteSpace(commandLine[end]) && commandLine[end] != '"') end++;
            return end > start ? commandLine.Substring(start, end - start) : null;
        }

        private static string Str(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            string s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static DateTime? UnixToLocal(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v) || !v.TryGetInt64(out long secs) || secs <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime; }
            catch { return null; }
        }
    }

    /// <summary>
    /// GOG Galaxy. The friendliest of the four: its registry key holds the title, the folder AND
    /// the executable, because GOG games are DRM-free and have to be startable without the client.
    ///
    /// That is also why this one launches the game DIRECTLY instead of through a protocol handler.
    /// It is the better experience — no client window in front of the game — and it is the better
    /// tracking, because the process handed back is the game itself rather than a launcher.
    /// </summary>
    public sealed class GogSource : IGameSource
    {
        public GameStore Store => GameStore.Gog;

        private const string GamesPath = @"SOFTWARE\GOG.com\Games";

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();

            foreach (var (id, key) in StoreRegistry.Subkeys(RegistryHive.LocalMachine, GamesPath))
            {
                ct.ThrowIfCancellationRequested();
                using (key)
                {
                    string dir = StoreRegistry.Directory(StoreRegistry.Value(key, "path"))
                              ?? StoreRegistry.Directory(StoreRegistry.Value(key, "workingDir"));
                    if (dir == null) continue;

                    string title = StoreRegistry.Value(key, "gameName");
                    string exe = StoreRegistry.Value(key, "exe");
                    string args = StoreRegistry.Value(key, "launchParam");

                    // An "exe" that is a bare file name is relative to the install folder.
                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        try
                        {
                            string full = Path.IsPathRooted(exe) ? exe : Path.Combine(dir, exe);
                            exe = File.Exists(full) ? full : null;
                        }
                        catch { exe = null; }
                    }
                    else exe = null;

                    games.Add(new GameEntry
                    {
                        Id = id,
                        Store = GameStore.Gog,
                        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(dir) : title,
                        InstallDir = dir,
                        LaunchExe = exe,
                        LaunchArgs = args,
                        ExePath = exe,
                        // Kept as the fallback for the rare game whose registered exe is missing —
                        // this opens Galaxy on the game rather than starting it, which is a poor
                        // answer but a better one than a button that does nothing.
                        LaunchUri = "goggalaxy://openGameView/" + Uri.EscapeDataString(id),
                    });
                }
            }
            return games;
        }
    }
}
