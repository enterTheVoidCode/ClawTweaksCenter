using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// How the library is grouped. LT/RT cycle through these IN THIS ORDER, and the order is a
    /// statement: Recent comes first because it is what the library opens on and what someone
    /// picking up the device is usually after. Moving it would change the default view.
    /// </summary>
    public enum LibraryGroup
    {
        Recent,
        /// <summary>Games pinned from the Start-button game menu. Right after Recent by declaration
        /// order - LT/RT and the tab strip both walk the enum in this order, so that placement is
        /// what actually puts it there, not a separate rule to keep in sync. Excluded from both
        /// wherever there are zero favourites (see CenterMenuWindow.Library.cs), the same way Roms is
        /// excluded without Playnite - a tab that can only ever be empty is a dead end.</summary>
        Favorites,
        All,
        Steam,
        Epic,
        Xbox,
        /// <summary>Tools the user added by hand. Sits after the stores because that is what it is -
        /// another shelf next to them, not a store of its own.</summary>
        Misc,
        /// <summary>ROMs, from Playnite. The only grouping with a second level under it - the
        /// system, cycled with the triggers.</summary>
        Roms,
    }

    /// <summary>
    /// Runs every store source and merges the result.
    ///
    /// Each source runs on its own and inside its own try/catch: a machine without Epic is the normal
    /// case, and one source throwing must never cost the user the other two. What a source could not
    /// deliver is recorded in <see cref="SourceErrors"/> rather than thrown, so the UI can say which
    /// store went missing instead of showing an empty grid with no explanation.
    /// </summary>
    public sealed class GameLibrary
    {
        public IReadOnlyList<GameEntry> Games { get; private set; } = Array.Empty<GameEntry>();

        /// <summary>Store to error text, for sources that failed outright. An empty result is NOT an
        /// error and never lands here.</summary>
        public IReadOnlyDictionary<GameStore, string> SourceErrors { get; private set; }
            = new Dictionary<GameStore, string>();

        public PlayHistory History { get; private set; } = new PlayHistory();

        /// <summary>
        /// Misc entries added or edited WHILE a scan is still landing.
        ///
        /// MiscSource takes its own snapshot of the file once, at the moment ScanAsync builds the
        /// source list. Every other source is fast enough that this never mattered, but Xbox alone
        /// measures 1.6 s - long enough for a user to add a tool through the UI before the scan has
        /// finished, and the NEXT source landing would otherwise rebuild `Games` from that first,
        /// now-stale Misc snapshot and make the freshly added tool disappear again.
        ///
        /// Null means "no override, trust whatever MiscSource returned this scan" - cleared at the
        /// start of every ScanAsync so a later Rescan reads the file fresh rather than replaying a
        /// stale override from three scans ago.
        /// </summary>
        private List<GameEntry> _miscOverride;

        /// <summary>
        /// Scans every store and publishes results AS EACH SOURCE LANDS, not at the end.
        ///
        /// The sources are wildly different in cost - measured here: Steam 97 ms, Epic 11 ms, Xbox
        /// 1.6 s (it has to ask Windows for the Start menu through PowerShell). Waiting for all three
        /// would leave the user looking at a spinner while 44 of the 46 games were ready in a tenth
        /// of a second.
        /// </summary>
        public async Task ScanAsync(CancellationToken ct, Action onPartial = null)
        {
            // Playnite is listed FIRST because it carries the art index the other three borrow from
            // for their coverless entries. The order does not decide who paints first (they all run
            // at once and land as they finish), and it does not have to: art is re-resolved on every
            // round, so a tile that had no cover in round one gets one in round three.
            var sources = new IGameSource[] { new PlayniteSource(), new SteamSource(), new EpicSource(), new XboxSource(), new MiscSource() };
            var errors = new Dictionary<GameStore, string>();
            var all = new List<GameEntry>();
            var history = PlayHistory.Load();
            _miscOverride = null;

            var pending = sources.Select(async s =>
            {
                try { return (s.Store, list: await s.ScanAsync(ct).ConfigureAwait(false), error: (string)null); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (s.Store, list: (IReadOnlyList<GameEntry>)Array.Empty<GameEntry>(), error: ex.Message); }
            }).ToList();

            History = history;
            SourceErrors = errors;

            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);

                var result = await done.ConfigureAwait(false);
                if (result.error != null) errors[result.Store] = result.error;
                all.AddRange(result.list);

                ApplyMiscOverride(all);
                Dedupe(all);
                GameArt.ResolveLocalArt(all);
                ArtOverrideStore.ApplyTo(all);  // a manual pick always outranks local/auto-fetched art
                FavoritesStore.ApplyTo(all);
                history.ApplyTo(all);
                all.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));

                // A fresh list each round rather than mutating the published one: the UI thread reads
                // Games while this loop runs, and handing it a list being sorted underneath it is the
                // kind of race that shows up once a month and never in a test.
                Games = new List<GameEntry>(all);
                if (onPartial != null && !ct.IsCancellationRequested) onPartial();
            }
        }

        /// <summary>
        /// The expensive half of the play history, run AFTER the grid is already on screen: harvesting
        /// the helper logs is the one step that can take real time, and it only ever adds ordering
        /// information to a library the user can already browse.
        /// </summary>
        public void HarvestHistoryInBackground(CancellationToken ct, Action onUpdated)
        {
            var games = Games;
            var history = History;
            if (games.Count == 0) return;

            Task.Run(() =>
            {
                try
                {
                    history.HarvestHelperLogs(games, ct);
                    history.ApplyTo(games);
                    history.SaveIfChanged();
                    if (!ct.IsCancellationRequested) onUpdated?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch { }
            }, ct);
        }

        /// <summary>Swaps in whatever the UI has told us is current for Misc, so a round that lands
        /// after an add/rename/remove does not resurrect the snapshot MiscSource took at scan
        /// start.</summary>
        private void ApplyMiscOverride(List<GameEntry> all)
        {
            if (_miscOverride == null) return;
            all.RemoveAll(g => g.Store == GameStore.Misc);
            all.AddRange(_miscOverride);
        }

        /// <summary>
        /// Drops repeats of the same game.
        ///
        /// TWO RULES, and the second one used to be one rule too many. Identity within a source is
        /// the store id. ACROSS sources it is the install folder - the same game described by two
        /// scanners is one game.
        ///
        /// What it must NOT do is treat a shared folder inside ONE source as a duplicate. Measured on
        /// this machine: 23 Switch ROMs live in <c>D:\Emulation\roms\switch\</c>, 16 Game Boy Color
        /// ROMs in <c>gbc\</c>, and a folder-wide rule threw all but one of each away - 160 ROMs
        /// became 85, and three Steam games sharing a folder went with them. The library simply had
        /// half of itself missing, with nothing to show that anything had been dropped.
        /// </summary>
        private static void Dedupe(List<GameEntry> games)
        {
            var seenId = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirOwner = new Dictionary<string, GameStore>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < games.Count; i++)
            {
                var g = games[i];
                bool drop = !seenId.Add(g.Store + "|" + g.Id);

                if (!drop && !string.IsNullOrEmpty(g.InstallDir))
                {
                    if (dirOwner.TryGetValue(g.InstallDir, out var owner)) drop = owner != g.Store;
                    else dirOwner[g.InstallDir] = g.Store;
                }

                if (drop) { games.RemoveAt(i); i--; }
            }
        }

        /// <summary>
        /// Swaps the Misc entries for a freshly saved list, without rescanning the stores.
        ///
        /// Publishes a NEW list rather than editing the published one, exactly as the scan loop does:
        /// the UI thread reads Games while this runs. A scan that happens to be in flight is not a
        /// problem either - MiscSource reads the same file, which the caller has already written, so
        /// the next round arrives at the same answer.
        /// </summary>
        public void ReplaceMisc(IReadOnlyList<GameEntry> misc)
        {
            _miscOverride = misc != null ? new List<GameEntry>(misc) : new List<GameEntry>();

            var rebuilt = new List<GameEntry>();
            foreach (var g in Games) if (g.Store != GameStore.Misc) rebuilt.Add(g);
            if (misc != null) rebuilt.AddRange(misc);

            GameArt.ResolveLocalArt(rebuilt);
            History.ApplyTo(rebuilt);
            rebuilt.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            Games = rebuilt;
        }

        /// <summary>Every ROM system that actually has games, most-populated first.</summary>
        public IReadOnlyList<string> RomSystems => PlayniteSource.LastSystems;

        /// <summary>The pseudo-system that means "recently played ROMs". A sentinel rather than an
        /// extra enum value, because it sits in the same strip as the real systems and cycles with
        /// them.</summary>
        public const string RomRecentSystem = "\u0001recent";

        public IReadOnlyList<GameEntry> ForGroup(LibraryGroup group) => ForGroup(group, null);

        /// <summary>
        /// The entries of one grouping. <paramref name="system"/> is the second level and applies
        /// only to ROMs - null means every system, which is the state the tab opens in.
        /// </summary>
        public IReadOnlyList<GameEntry> ForGroup(LibraryGroup group, string system)
        {
            switch (group)
            {
                case LibraryGroup.Steam: return Games.Where(g => g.Store == GameStore.Steam).ToList();
                case LibraryGroup.Epic: return Games.Where(g => g.Store == GameStore.Epic).ToList();
                case LibraryGroup.Xbox: return Games.Where(g => g.Store == GameStore.Xbox).ToList();
                case LibraryGroup.Misc: return Games.Where(g => g.Store == GameStore.Misc).ToList();
                case LibraryGroup.Favorites: return Games.Where(g => g.IsFavorite).ToList();
                case LibraryGroup.Roms:
                    var roms = Games.Where(g => g.Store == GameStore.Playnite);
                    if (system == RomRecentSystem)
                        return roms.Where(g => g.LastPlayed.HasValue)
                                   .OrderByDescending(g => g.LastPlayed.Value)
                                   .ToList();
                    return roms.Where(g => system == null || string.Equals(g.SystemName, system, StringComparison.OrdinalIgnoreCase))
                               .ToList();
                case LibraryGroup.Recent:
                    // ROMs are deliberately NOT here. They get tried out a handful at a time - one
                    // evening of browsing a Game Boy collection would push every PC game off the
                    // shelf, and Recent is meant to be the short list you reach for. ROMs have their
                    // own recent, inside their own tab (RomRecentSystem).
                    // Misc is out for a second reason on top of the ROM one: these are tools, and
                    // a shelf meant to hold "what you were playing" should not fill up with the fan
                    // curve editor you open more often than any game.
                    return Games.Where(g => g.LastPlayed.HasValue
                                            && g.Store != GameStore.Playnite
                                            && g.Store != GameStore.Misc)
                                .OrderByDescending(g => g.LastPlayed.Value)
                                .ToList();
                // "All" means the PC library - installed GAMES. ROMs are hundreds of entries with
                // their own tab and would bury the installed games they sit next to; Misc is not
                // games at all.
                default: return Games.Where(g => g.Store != GameStore.Playnite && g.Store != GameStore.Misc).ToList();
            }
        }

        public static string GroupLabel(LibraryGroup g)
        {
            switch (g)
            {
                case LibraryGroup.Steam: return "Steam";
                case LibraryGroup.Epic: return "Epic";
                case LibraryGroup.Xbox: return "Xbox";
                case LibraryGroup.Recent: return "Recent";
                case LibraryGroup.Favorites: return "Favorites";
                case LibraryGroup.Misc: return "Misc";
                case LibraryGroup.Roms: return "ROMs";
                default: return "All";
            }
        }

        /// <summary>
        /// Starts a game through its store.
        ///
        /// There is NO reliable signal back. All three launch paths are a protocol handler or the
        /// shell, so the process this returns is Steam, the Epic launcher or explorer - never the
        /// game. Waiting for "the game is up" cannot be built here, and building it would mean a
        /// second game detector living next to the helper's, which is the one component that already
        /// does this properly.
        /// </summary>
        public static bool Launch(GameEntry game) => Launch(game, out _);

        /// <summary>
        /// Starts a game through its store, and hands back the actual Process object when there is
        /// one to hand back - only true for the direct-exe path (ROMs, Misc). A store launch (Steam,
        /// Epic, Xbox, the shell URI fallback) returns Steam/the launcher/explorer, never the game
        /// itself, so GameRunTracker falls back to watching the install directory for those instead
        /// of pretending this Process handle means anything.
        /// </summary>
        public static bool Launch(GameEntry game, out Process startedProcess)
        {
            startedProcess = null;
            if (game == null) return false;

            // Misc entries were resolved once, when the user added them, and know exactly which of
            // the two activation routes applies. Guessing again here would mean re-deciding it on
            // every launch from data that has not changed.
            if (game.Store == GameStore.Misc) return MiscSource.Launch(game, out startedProcess);

            // A resolved emulator command line goes straight to the emulator. The URI route works
            // too, but it starts Playnite first and leaves it running afterwards.
            if (!string.IsNullOrEmpty(game.LaunchExe))
            {
                try
                {
                    var direct = new ProcessStartInfo
                    {
                        FileName = game.LaunchExe,
                        Arguments = game.LaunchArgs ?? string.Empty,
                        // Emulators routinely load cores, BIOS files and configuration relative to
                        // their own folder, and inherit OUR working directory otherwise.
                        WorkingDirectory = System.IO.Path.GetDirectoryName(game.LaunchExe),
                        UseShellExecute = false,
                    };
                    startedProcess = Process.Start(direct);
                    return true;
                }
                catch
                {
                    // Fall through to the URI: a moved emulator or a missing dependency should cost
                    // the fast path, not the launch.
                }
            }

            if (game.LaunchUri == null) return false;

            // The steam:// handler starts Steam when it is not running, and Steam then comes up with
            // its full window in front of the game - which is why it only ever happens on the FIRST
            // launch after a boot. Starting it ourselves, silently, first is the whole fix.
            PrewarmSteamIfNeeded(game.LaunchUri);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = game.LaunchUri,
                    UseShellExecute = true,
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Brings Steam up in the tray before handing it a steam:// URI, but only when it is not
        /// running yet.
        ///
        /// WHY NOT -applaunch INSTEAD OF THE URI: the URI route is the one that already works, launch
        /// options and all, and rungameid covers shortcuts and mod ids that -applaunch does not. This
        /// changes only the STATE Steam is in when the URI arrives, so the launch path itself is
        /// untouched.
        ///
        /// The wait is bounded and it is a wait for the PROCESS, not for readiness - Steam is not
        /// finished starting when its process appears, but the URI is queued by the handler either
        /// way, and the alternative is holding a button press for as long as a cold Steam takes.
        /// If anything here fails, the URI is fired anyway: a visible Steam window is a nuisance, a
        /// game that does not start is a defect.
        /// </summary>
        private static void PrewarmSteamIfNeeded(string launchUri)
        {
            try
            {
                if (launchUri == null || !launchUri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return;
                if (Process.GetProcessesByName("steam").Length > 0) return;

                string root = SteamSource.SteamPath();
                if (root == null) return;
                string exe = System.IO.Path.Combine(root, "steam.exe");
                if (!System.IO.File.Exists(exe)) return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-silent",
                    WorkingDirectory = root,
                    UseShellExecute = true,
                });

                // Up to five seconds, checked ten times a second. A cold Steam usually shows its
                // process within one.
                for (int i = 0; i < 50; i++)
                {
                    if (Process.GetProcessesByName("steam").Length > 0) break;
                    System.Threading.Thread.Sleep(100);
                }
            }
            catch { }
        }
    }
}
