using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Playnite, read-only.
    ///
    /// Playnite already models what we cannot: ROM collections, the system each ROM belongs to, and
    /// which emulator and command line starts it. None of that is re-derived here. We read its
    /// database, show what it knows, and launch through Playnite itself - the emulator configuration
    /// stays its job, and a second half-implementation of it would only ever disagree with the first.
    ///
    /// MEASURED on this machine, 2026-08-23:
    ///   247 installed entries, 245 with cover art, across 20 platforms
    ///   163 have no store source (ROMs), 69 Steam, 10 Steam Family Sharing, 4 Xbox, 1 Epic
    ///   417 MB of art in 247 folders under library\files
    ///
    /// The database is LiteDB **v4**, not v5 - measured from the file header and database.json
    /// ({"Version":4}). Opening it with the v5 package fails outright with "not a valid LiteDB
    /// database format", which reads like a corrupt file rather than a wrong package version.
    /// </summary>
    public sealed class PlayniteSource : IGameSource
    {
        public GameStore Store => GameStore.Playnite;

        public static string LibraryDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "library");

        public static bool IsPresent => File.Exists(Path.Combine(LibraryDir, "games.db"));

        /// <summary>
        /// Cover art for games we found ourselves but have no picture for. Only Steam caches capsules
        /// locally; Epic and Xbox ship none at all, and Playnite has already downloaded one for
        /// nearly everything it knows. Keyed by install folder first (exact) and normalised title
        /// second - see <see cref="TryFindArt"/> for why that order matters.
        /// </summary>
        public sealed class ArtIndex
        {
            internal readonly Dictionary<string, string> ByInstallDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> ByTitle = new Dictionary<string, string>(StringComparer.Ordinal);

            public bool IsEmpty => ByInstallDir.Count == 0 && ByTitle.Count == 0;

            /// <summary>
            /// Install folder wins over title. A folder is an identity; a title is a label two
            /// different games can share (every "Doom" ever released), and a cover on the wrong game
            /// is worse than no cover - it is wrong in a way the user cannot correct.
            /// </summary>
            public string TryFindArt(GameEntry game)
            {
                if (game == null) return null;
                if (!string.IsNullOrEmpty(game.InstallDir))
                {
                    string dir = Normalize(game.InstallDir);
                    if (ByInstallDir.TryGetValue(dir, out string byDir)) return byDir;
                }
                string key = NormalizeTitle(game.Title);
                return key.Length > 0 && ByTitle.TryGetValue(key, out string byTitle) ? byTitle : null;
            }
        }

        public static ArtIndex LastArtIndex { get; private set; } = new ArtIndex();

        /// <summary>The systems present among the ROM entries, in the order the group strip shows
        /// them: most games first, because that is the order someone scrolls looking for one.</summary>
        public static IReadOnlyList<string> LastSystems { get; private set; } = Array.Empty<string>();

        /// <summary>True when this scan could not read the database and fell back to the cache. The
        /// normal cause is Playnite running - see <see cref="CopyAside"/>.</summary>
        public static bool UsedCache { get; private set; }

        /// <summary>
        /// Playnite's own "what to do after launching a game" setting, straight out of its config:
        /// 0 = leave it open, 1 = minimise, 2 = close. Null when it could not be read.
        ///
        /// Read so the ROM tab can say why Playnite appears when a ROM starts. NEVER WRITTEN - this
        /// integration reads Playnite and does not reconfigure it, and silently changing another
        /// application's settings is not something a launcher gets to do.
        /// </summary>
        public static int? AfterLaunchSetting()
        {
            try
            {
                string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                          "Playnite", "config.json");
                if (!File.Exists(cfg)) return null;
                using (var fs = new FileStream(cfg, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var doc = System.Text.Json.JsonDocument.Parse(fs))
                    if (doc.RootElement.TryGetProperty("AfterLaunch", out var v) && v.TryGetInt32(out int n)) return n;
            }
            catch { }
            return null;
        }

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();
            var art = new ArtIndex();
            var systemCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            UsedCache = false;
            if (!IsPresent) { LastArtIndex = art; LastSystems = Array.Empty<string>(); return games; }

            string work = null;
            try
            {
                work = CopyAside(ct);
                // Playnite holds its database with an EXCLUSIVE lock - measured: even opening it with
                // FileShare.ReadWrite from our side fails while Playnite runs, because the sharing
                // mode is decided by the FIRST opener. And Playnite is running exactly when it
                // matters, since starting a ROM starts Playnite. Without the cache the ROM tab would
                // simply be empty from then on, with nothing saying why.
                if (work == null) return LoadCache(ref art);

                var platforms = LoadNames(Path.Combine(work, "platforms.db"));
                var sources = LoadNames(Path.Combine(work, "sources.db"));
                var emulators = PlayniteEmulators.LoadEmulators(work);
                string filesRoot = Path.Combine(LibraryDir, "files");

                using (var db = OpenRead(Path.Combine(work, "games.db")))
                {
                    var collection = db.GetCollection("Game");
                    foreach (var doc in collection.FindAll())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Bool(doc, "IsInstalled")) continue;

                        string title = Text(doc, "Name");
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        string cover = Text(doc, "CoverImage");
                        string coverPath = null;
                        if (!string.IsNullOrEmpty(cover))
                        {
                            string p = Path.Combine(filesRoot, cover.Replace('/', '\\'));
                            if (File.Exists(p)) coverPath = p;
                        }

                        string installDir = Text(doc, "InstallDirectory");
                        if (coverPath != null)
                        {
                            if (!string.IsNullOrEmpty(installDir))
                                art.ByInstallDir[Normalize(installDir)] = coverPath;
                            string tk = NormalizeTitle(title);
                            if (tk.Length > 0 && !art.ByTitle.ContainsKey(tk)) art.ByTitle[tk] = coverPath;
                        }

                        // Anything with a store behind it is already found by our own sources, and
                        // showing it twice under two names is worse than not showing Playnite's copy.
                        // Its ART is still taken (above) - that part is additive.
                        if (IdName(doc, "SourceId", sources) != null) continue;

                        string system = FirstIdName(doc, "PlatformIds", platforms);
                        // "PC (Windows)" here means a manually added Windows game, not a ROM. It has
                        // no emulator and no system, so it does not belong in a ROM tab.
                        if (string.IsNullOrEmpty(system) ||
                            system.IndexOf("PC ", StringComparison.OrdinalIgnoreCase) == 0) continue;

                        Guid id = doc["_id"].AsGuid;
                        systemCounts[system] = systemCounts.TryGetValue(system, out int n) ? n + 1 : 1;

                        var direct = ResolveDirectLaunch(doc, installDir, emulators);

                        games.Add(new GameEntry
                        {
                            LaunchExe = direct?.Executable,
                            LaunchArgs = direct?.Arguments,
                            Id = id.ToString(),
                            Store = GameStore.Playnite,
                            Title = title,
                            SystemName = system,
                            InstallDir = installDir,
                            ArtPath = coverPath,
                            // The parameterless form on purpose: Playnite resolves the emulator, its
                            // profile and the command line itself. Rebuilding that here from
                            // EmulatorId / EmulatorProfileId would be a second copy of a
                            // configuration the user maintains in one place.
                            LaunchUri = "playnite://playnite/start/" + id.ToString("D"),
                            LastPlayed = Date(doc, "LastActivity"),
                        });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* a Playnite that cannot be read is a missing source, not a failure */ }
            finally
            {
                if (work != null) { try { Directory.Delete(work, true); } catch { } }
            }

            // A read that produced nothing is a read that failed in a way we did not catch - keep the
            // cache rather than replacing a working library with an empty one.
            if (games.Count == 0) return LoadCache(ref art);

            var systems = new List<string>(systemCounts.Keys);
            systems.Sort((a, b) =>
            {
                int byCount = systemCounts[b].CompareTo(systemCounts[a]);
                return byCount != 0 ? byCount : string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            });

            LastArtIndex = art;
            LastSystems = systems;
            SaveCache(games, systems, art);
            return games;
        }

        /// <summary>
        /// Finds the play action, resolves the ROM path behind it, and asks
        /// <see cref="PlayniteEmulators"/> for the command line.
        ///
        /// The ROM path is stored with a <c>{InstallDir}</c> token rather than in full, and an entry
        /// can list several candidates (Playnite records both the file and a same-named subfolder).
        /// The first one that exists on disk wins; if none does, there is nothing to launch directly
        /// and the caller falls back to the URI.
        /// </summary>
        private static PlayniteEmulators.Resolved ResolveDirectLaunch(
            BsonDocument doc, string installDir, Dictionary<string, PlayniteEmulators.EmulatorEntry> emulators)
        {
            try
            {
                if (!doc.ContainsKey("GameActions") || !doc["GameActions"].IsArray) return null;

                BsonDocument action = null;
                foreach (var a in doc["GameActions"].AsArray)
                {
                    var ad = a.AsDocument;
                    if (ad == null) continue;
                    if (!ad.ContainsKey("IsPlayAction") || !ad["IsPlayAction"].AsBoolean) continue;
                    if (Text(ad, "Type") != "Emulator") continue;
                    action = ad;
                    break;
                }
                if (action == null) return null;

                // An action that overrides the defaults carries its own arguments, and reading the
                // definition would then contradict what the user configured. Left to Playnite.
                if (action.ContainsKey("OverrideDefaultArgs") && action["OverrideDefaultArgs"].AsBoolean) return null;

                string emulatorId = action.ContainsKey("EmulatorId") && !action["EmulatorId"].IsNull
                    ? action["EmulatorId"].AsGuid.ToString() : null;
                string profileId = Text(action, "EmulatorProfileId");
                string rom = FirstExistingRom(doc, installDir);
                if (rom == null) return null;

                return PlayniteEmulators.Resolve(emulators, emulatorId, profileId, rom);
            }
            catch { return null; }
        }

        private static string FirstExistingRom(BsonDocument doc, string installDir)
        {
            if (!doc.ContainsKey("Roms") || !doc["Roms"].IsArray) return null;
            foreach (var r in doc["Roms"].AsArray)
            {
                var rd = r.AsDocument;
                string path = Text(rd, "Path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!string.IsNullOrEmpty(installDir))
                    path = path.Replace("{InstallDir}", installDir.TrimEnd('\\', '/'));
                try { if (File.Exists(path)) return Path.GetFullPath(path); }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Copies the database files to a scratch folder before opening them.
        ///
        /// Playnite holds them open while it runs, and a library that only works with Playnite closed
        /// is a library that does not work. Copying is also the guarantee that this stays READ-ONLY:
        /// LiteDB writes to a file it opens, even for a query, and the file it writes to here is a
        /// throwaway.
        /// </summary>
        private static string CopyAside(CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ClawTweaksCenter", "playnite-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                foreach (string name in new[] { "games.db", "platforms.db", "sources.db", "emulators.db" })
                {
                    ct.ThrowIfCancellationRequested();
                    string src = Path.Combine(LibraryDir, name);
                    if (!File.Exists(src)) continue;
                    // Fully qualified: LiteDB ships its own FileMode enum, and an unqualified name
                    // here binds to that one instead of System.IO.
                    using (var input = new FileStream(src, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var output = new FileStream(Path.Combine(dir, name), System.IO.FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
                return dir;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                try { Directory.Delete(dir, true); } catch { }
                return null;
            }
        }

        private static LiteDatabase OpenRead(string path) => new LiteDatabase("Filename=" + path + ";journal=false");

        #region Cache
        // Our own copy of the last successful read. Not a mirror of Playnite's database - just the
        // handful of fields the ROM tab draws, so it stays small and has nothing to go stale in a way
        // that matters: paths that no longer exist simply show no cover.
        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "romcache.json");

        private sealed class CacheFile
        {
            public List<CacheRom> Roms { get; set; }
            public List<string> Systems { get; set; }
            public List<CacheArt> Art { get; set; }
        }

        private sealed class CacheRom
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string System { get; set; }
            public string InstallDir { get; set; }
            public string ArtPath { get; set; }
            public string LaunchUri { get; set; }
            public string LaunchExe { get; set; }
            public string LaunchArgs { get; set; }
            public long LastPlayedUtcTicks { get; set; }
        }

        private sealed class CacheArt
        {
            public string Key { get; set; }
            public string Path { get; set; }
            public bool IsDir { get; set; }
        }

        private static void SaveCache(List<GameEntry> games, List<string> systems, ArtIndex art)
        {
            try
            {
                var file = new CacheFile
                {
                    Roms = new List<CacheRom>(),
                    Systems = systems,
                    Art = new List<CacheArt>(),
                };
                foreach (var g in games)
                    file.Roms.Add(new CacheRom
                    {
                        Id = g.Id,
                        Title = g.Title,
                        System = g.SystemName,
                        InstallDir = g.InstallDir,
                        ArtPath = g.ArtPath,
                        LaunchUri = g.LaunchUri,
                        LaunchExe = g.LaunchExe,
                        LaunchArgs = g.LaunchArgs,
                        LastPlayedUtcTicks = g.LastPlayed?.ToUniversalTime().Ticks ?? 0,
                    });
                foreach (var kv in art.ByInstallDir) file.Art.Add(new CacheArt { Key = kv.Key, Path = kv.Value, IsDir = true });
                foreach (var kv in art.ByTitle) file.Art.Add(new CacheArt { Key = kv.Key, Path = kv.Value, IsDir = false });

                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                // Beside, then move: an interrupted write of the file itself leaves JSON that will not
                // parse, and the next start would look like Playnite had gone missing.
                string tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(file));
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch { }
        }

        private static IReadOnlyList<GameEntry> LoadCache(ref ArtIndex art)
        {
            var games = new List<GameEntry>();
            try
            {
                if (!File.Exists(CachePath)) { LastArtIndex = art; LastSystems = Array.Empty<string>(); return games; }

                var file = System.Text.Json.JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
                if (file?.Roms == null) { LastArtIndex = art; LastSystems = Array.Empty<string>(); return games; }

                foreach (var r in file.Roms)
                    games.Add(new GameEntry
                    {
                        Id = r.Id,
                        Store = GameStore.Playnite,
                        Title = r.Title,
                        SystemName = r.System,
                        InstallDir = r.InstallDir,
                        ArtPath = r.ArtPath != null && File.Exists(r.ArtPath) ? r.ArtPath : null,
                        LaunchUri = r.LaunchUri,
                        // Re-checked, not trusted: an emulator that has been moved or uninstalled
                        // since the cache was written would otherwise fail with a shell error the
                        // user cannot place. Without it the URI route still works.
                        LaunchExe = r.LaunchExe != null && File.Exists(r.LaunchExe) ? r.LaunchExe : null,
                        LaunchArgs = r.LaunchArgs,
                        LastPlayed = r.LastPlayedUtcTicks > 0
                            ? new DateTime(r.LastPlayedUtcTicks, DateTimeKind.Utc).ToLocalTime()
                            : (DateTime?)null,
                    });

                foreach (var a in file.Art ?? new List<CacheArt>())
                {
                    if (a?.Key == null || a.Path == null || !File.Exists(a.Path)) continue;
                    if (a.IsDir) art.ByInstallDir[a.Key] = a.Path;
                    else art.ByTitle[a.Key] = a.Path;
                }

                LastArtIndex = art;
                LastSystems = file.Systems ?? new List<string>();
                UsedCache = games.Count > 0;
            }
            catch
            {
                LastArtIndex = art;
                LastSystems = Array.Empty<string>();
            }
            return games;
        }
        #endregion

        private static Dictionary<string, string> LoadNames(string file)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(file)) return map;
            try
            {
                using (var db = OpenRead(file))
                    foreach (string collection in db.GetCollectionNames())
                        foreach (var doc in db.GetCollection(collection).FindAll())
                        {
                            string name = Text(doc, "Name");
                            if (!string.IsNullOrWhiteSpace(name)) map[doc["_id"].ToString()] = name;
                        }
            }
            catch { }
            return map;
        }

        private static string IdName(BsonDocument doc, string key, Dictionary<string, string> names)
        {
            if (doc == null || !doc.ContainsKey(key) || doc[key].IsNull) return null;
            return names.TryGetValue(doc[key].ToString(), out string n) ? n : null;
        }

        private static string FirstIdName(BsonDocument doc, string key, Dictionary<string, string> names)
        {
            if (doc == null || !doc.ContainsKey(key) || !doc[key].IsArray) return null;
            foreach (var v in doc[key].AsArray)
                if (names.TryGetValue(v.ToString(), out string n)) return n;
            return null;
        }

        private static string Text(BsonDocument doc, string key)
        {
            if (doc == null || !doc.ContainsKey(key) || doc[key].IsNull) return null;
            try { return doc[key].AsString; } catch { return null; }
        }

        private static bool Bool(BsonDocument doc, string key)
        {
            if (doc == null || !doc.ContainsKey(key) || doc[key].IsNull) return false;
            try { return doc[key].AsBoolean; } catch { return false; }
        }

        private static DateTime? Date(BsonDocument doc, string key)
        {
            if (doc == null || !doc.ContainsKey(key) || doc[key].IsNull) return null;
            try { return doc[key].AsDateTime.ToLocalTime(); } catch { return null; }
        }

        internal static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return (path ?? string.Empty).TrimEnd('\\', '/'); }
        }

        /// <summary>
        /// Title to comparison key: letters and digits only, lower case. Enough to bridge "DARK
        /// SOULS™: REMASTERED" and "Dark Souls Remastered", and deliberately nothing cleverer - a
        /// fuzzy match here would pair up sequels and re-releases, and put the wrong cover on a game.
        /// </summary>
        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
