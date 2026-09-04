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
        /// <summary>Ubisoft Connect, EA, Battle.net and GOG on ONE shelf.
        ///
        /// One tab for four stores because of how many games are behind them: someone with three
        /// Ubisoft games and one Battle.net game would otherwise get four tabs holding one row
        /// between them, and would still have to visit all four to see it. Hidden entirely when it
        /// is empty, the same rule Favorites and Roms already follow.
        ///
        /// The entries keep their own store identity underneath - the subline under a cover says
        /// "Ubisoft", and grouping the All tab by platform still separates them.</summary>
        OtherStores,
        /// <summary>Apps the user added by hand. Sits after the stores because that is what it is -
        /// another shelf next to them, not a store of its own.</summary>
        Misc,
        /// <summary>ROMs, from Playnite. The only grouping with a second level under it - the
        /// system, cycled with the triggers.</summary>
        Roms,
        /// <summary>
        /// Owned but not playable yet: games with no install on this machine, and downloads that
        /// have not finished.
        ///
        /// LAST IN THE STRIP ON PURPOSE. Every other tab is a shelf of things to start; this one is
        /// a shelf of things to fetch, which is a different errand and not the one anybody opens the
        /// library for. It is also the only tab whose A button does not launch anything.
        ///
        /// STEAM ONLY for now, and the tab says so above the covers rather than leaving the user to
        /// wonder where their Epic library went. Nothing about the grouping is Steam-specific - any
        /// store that can be asked what it owns fits here - but Steam is the one that answers
        /// without an account, a key or a network call.
        /// </summary>
        NotInstalled,
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
            var sources = new IGameSource[]
            {
                new PlayniteSource(), new SteamSource(), new EpicSource(), new XboxSource(),
                // Four separate sources, one shelf. Each is a handful of registry reads and each
                // fails on its own - see OtherStores.cs.
                new UbisoftSource(), new EaSource(), new BattleNetSource(), new GogSource(),
                new MiscSource(),
            };
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
                // Both are file reads off the local disk and both are re-done per round rather than
                // once: a profile can appear while Center is open (the user sets one in the widget),
                // and Steam rewrites localconfig.vdf when a game exits.
                SteamPlaytime.Refresh();
                ClawProfiles.Refresh();
                foreach (var g in all)
                {
                    if (g.Store == GameStore.Steam)
                    {
                        g.PlaytimeMinutes = SteamPlaytime.MinutesFor(g.Id);

                        // TWO Steam timestamps, and the second one is not a refinement - it is
                        // most of the answer. SteamSource reads LastPlayed out of the app manifest,
                        // which belongs to the INSTALLATION; localconfig.vdf belongs to the ACCOUNT.
                        //
                        // MEASURED on this machine: of 44 installed games, 15 carry NO manifest
                        // timestamp at all while the account file knows exactly when they were last
                        // played, and not one game is the other way round. Those 15 could only ever
                        // reach Recent if the helper's own log happened to still hold a play event
                        // for them - so a third of the library was invisible on the tab the library
                        // opens on.
                        //
                        // Merged on MAXIMUM rather than by picking a winner, the same rule
                        // PlayHistory.Note applies to every other source: both files are written by
                        // Steam, at different moments, and neither is reliably the fresher one.
                        var fromAccount = SteamPlaytime.LastPlayedFor(g.Id);
                        if (fromAccount.HasValue && (!g.LastPlayed.HasValue || fromAccount.Value > g.LastPlayed.Value))
                            g.LastPlayed = fromAccount;
                    }
                    g.Profiles = ClawProfiles.For(g);
                }
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

            // 🔴 THE SAME FOUR STEPS THE SCAN RUNS, IN THE SAME ORDER. Two of them were missing here
            // (2026-09-04): ArtOverrideStore and FavoritesStore. That is a real divergence between the
            // two ways this list gets built - a hand-picked cover and a pinned favourite were applied
            // by a full scan and silently not by an add/rename/remove.
            //
            // ⚠️ ORDER IS LOAD-BEARING, and it is why the override comes second: ResolveLocalArt only
            // fills an ArtPath that is still null, so a manual pick has to be written AFTER it to win.
            // Reversed, the local art would take the slot and the override would find nothing to do.
            GameArt.ResolveLocalArt(rebuilt);
            ArtOverrideStore.ApplyTo(rebuilt);
            FavoritesStore.ApplyTo(rebuilt);
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

        /// <summary>
        /// How many games Recent shows.
        ///
        /// A shelf, not an archive. Recent is one reel with no sorting and no second level, and the
        /// point of it is the games someone is actually playing through - past this the tab stops
        /// answering "what was I playing" and starts being a worse copy of All, which is right there
        /// and sortable. It is also what makes the jump-to-the-end flick worth having: a bounded row
        /// has an end that means something.
        /// </summary>
        public const int RecentLimit = 50;

        /// <summary>The four stores that share the Other Stores shelf. In one place because the tab,
        /// the tab-strip visibility check and the trigger cycle all have to agree about it.</summary>
        public static bool IsOtherStore(GameStore store) =>
            store == GameStore.Ubisoft || store == GameStore.EA ||
            store == GameStore.BattleNet || store == GameStore.Gog;

        public IReadOnlyList<GameEntry> ForGroup(LibraryGroup group) => ForGroup(group, null);

        /// <summary>
        /// The entries of one grouping. <paramref name="system"/> is the second level and applies
        /// only to ROMs - null means every system, which is the state the tab opens in.
        /// </summary>
        public IReadOnlyList<GameEntry> ForGroup(LibraryGroup group, string system)
        {
            // NOT INSTALLED IS THE ONE TAB THAT WANTS THE OTHERS. Everything below it works on
            // `playable`, so an entry that cannot be started cannot leak onto a shelf that offers to
            // start it - one filter in one place rather than a condition in nine branches.
            if (group == LibraryGroup.NotInstalled)
                return Games.Where(g => !g.Installed)
                            // Downloads first: they are the ones with something happening, and the
                            // ones the user just pressed a button to cause.
                            .OrderByDescending(g => g.DownloadTotalBytes > 0)
                            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();

            var playable = Games.Where(g => g.Installed).ToList();

            switch (group)
            {
                case LibraryGroup.Steam: return playable.Where(g => g.Store == GameStore.Steam).ToList();
                case LibraryGroup.Epic: return playable.Where(g => g.Store == GameStore.Epic).ToList();
                case LibraryGroup.Xbox: return playable.Where(g => g.Store == GameStore.Xbox).ToList();
                case LibraryGroup.Misc: return playable.Where(g => g.Store == GameStore.Misc).ToList();
                case LibraryGroup.OtherStores: return playable.Where(g => IsOtherStore(g.Store)).ToList();
                case LibraryGroup.Favorites: return playable.Where(g => g.IsFavorite).ToList();
                case LibraryGroup.Roms:
                    var roms = playable.Where(g => g.Store == GameStore.Playnite);
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
                    return playable.Where(g => g.LastPlayed.HasValue
                                            && g.Store != GameStore.Playnite
                                            && g.Store != GameStore.Misc)
                                .OrderByDescending(g => g.LastPlayed.Value)
                                .Take(RecentLimit)
                                .ToList();
                // "All" means the PC library - installed GAMES. ROMs are hundreds of entries with
                // their own tab and would bury the installed games they sit next to; Misc is not
                // games at all.
                default: return playable.Where(g => g.Store != GameStore.Playnite && g.Store != GameStore.Misc).ToList();
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
                case LibraryGroup.OtherStores: return "Other Stores";
                case LibraryGroup.Misc: return "My Apps";
                case LibraryGroup.Roms: return "ROMs";
                case LibraryGroup.NotInstalled: return "Not Installed";
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
        /// <summary>
        /// Whether the last launch had to start Steam itself, rather than finding it already
        /// running. Read by the running screen, which says the wait will be longer when it is true.
        ///
        /// A STATIC rather than a second out parameter: it is a fact about how long the user is
        /// about to wait, not about whether the launch worked, and every caller that does not draw a
        /// screen would otherwise have to declare a variable for it. Reset on every launch, so it is
        /// never the answer for the game before this one.
        /// </summary>
        public static bool LastLaunchStartedSteam { get; private set; }

        public static bool Launch(GameEntry game, out Process startedProcess)
        {
            startedProcess = null;
            LastLaunchStartedSteam = false;
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
            LastLaunchStartedSteam = PrewarmSteamIfNeeded(game.LaunchUri);

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
        /// Sends one steam:// URI to the client, starting it first when it is not running.
        ///
        /// The protocol handler would start Steam by itself - it is registered as
        /// <c>steam.exe -- "%1"</c> - but a cold start that way comes up with the full client window
        /// in front of everything. Prewarming is the same courtesy a game launch already gets.
        /// </summary>
        public static bool OpenSteamUri(string uri)
        {
            LastLaunchStartedSteam = false;
            if (string.IsNullOrWhiteSpace(uri)) return false;
            try
            {
                LastLaunchStartedSteam = PrewarmSteamIfNeeded(uri);
                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Steam URI '" + uri + "' failed: " + ex.Message);
                return false;
            }
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
        /// <returns>True when Steam was NOT running and this call started it - which is the one
        /// case where the wait before the game appears is noticeably longer, and the only reason the
        /// running screen has anything to say about Steam.</returns>
        private static bool PrewarmSteamIfNeeded(string launchUri)
        {
            try
            {
                if (launchUri == null || !launchUri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return false;
                if (Process.GetProcessesByName("steam").Length > 0) return false;

                string root = SteamSource.SteamPath();
                if (root == null) return false;
                string exe = System.IO.Path.Combine(root, "steam.exe");
                if (!System.IO.File.Exists(exe)) return false;

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
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Starts Steam in the tray if it is not running, outside any launch.
        ///
        /// Behind CenterSettings.StartSteamWithLibrary and called when the library opens. It blocks
        /// for as long as five seconds waiting for the process, so the caller has to be off the UI
        /// thread - which is also why it is a separate entry point rather than the launch path's own
        /// prewarm with a different argument: at launch that wait is the launch, here it is not.
        /// </summary>
        public static bool PrewarmSteam()
        {
            return PrewarmSteamIfNeeded("steam://");
        }
    }
}
