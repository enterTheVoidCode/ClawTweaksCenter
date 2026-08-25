using System;

namespace ClawTweaksCenter.Library
{
    /// <summary>Which store an entry came from. The order is the order the group strip shows.</summary>
    public enum GameStore
    {
        Steam,
        Epic,
        Xbox,
        /// <summary>Ubisoft Connect, EA (Origin), Battle.net and GOG Galaxy. They are separate
        /// stores and keep separate identities here - the library only ever puts them on ONE shelf
        /// (see <see cref="LibraryGroup.OtherStores"/>), which is a statement about how many games
        /// people have in them, not about them being the same thing. See OtherStores.cs.</summary>
        Ubisoft,
        EA,
        BattleNet,
        Gog,
        /// <summary>A ROM, described and launched by Playnite. Not a store - the name is what the
        /// user sees, and "Playnite" is what they installed.</summary>
        Playnite,
        /// <summary>An app the user added by hand. The only kind of entry nothing discovered - see
        /// MiscSource. The enum member keeps its old name on purpose: FavoritesStore and
        /// ArtOverrideStore key on it as text, so renaming it would silently orphan every favourite
        /// and every hand-picked cover already on disk. Only the LABEL changed, to "My Apps".</summary>
        Misc,
    }

    /// <summary>
    /// One installed game, as far as we can know it WITHOUT logging into anything: everything here
    /// comes from files the store already wrote to this machine.
    ///
    /// <see cref="LaunchUri"/> is deliberately a protocol URI (or a shell command), not an exe path.
    /// Launching a game through its store is the only way that works for all three: Steam needs its
    /// own DRM bootstrap, Epic needs its launcher, and an Xbox package cannot be started by path at
    /// all. It also means we never have to guess WHICH exe in an install folder is the game.
    /// </summary>
    public sealed class GameEntry
    {
        /// <summary>Stable per-store id — Steam AppID, Epic AppName, Xbox PackageFamilyName. Used for
        /// art lookup and de-duplication, never shown.</summary>
        public string Id { get; set; }

        public GameStore Store { get; set; }

        /// <summary>The title as the store spells it. Never taken from a folder name: Xbox mangles
        /// characters it cannot put in a path (a colon becomes a hyphen), so a folder name is a
        /// corrupted copy of a string we can read properly one file further in.</summary>
        public string Title { get; set; }

        public string InstallDir { get; set; }

        /// <summary>The console a ROM belongs to ("Nintendo 64", "Sony PlayStation"). Null for
        /// everything that is not a ROM - a PC game has no system to group by.</summary>
        public string SystemName { get; set; }

        public string LaunchUri { get; set; }

        /// <summary>
        /// The emulator and its arguments, when a ROM could be resolved to a direct command line.
        /// Preferred over <see cref="LaunchUri"/> because the URI route starts Playnite first - a
        /// launcher launching a launcher, several seconds, and it stays on screen afterwards.
        /// Null whenever the resolution failed; the URI is then still there and still works.
        /// </summary>
        public string LaunchExe { get; set; }

        public string LaunchArgs { get; set; }

        /// <summary>The game's executable where the store states it outright (Epic, Xbox). Steam does
        /// not say, and guessing among launcher/crash-handler/shipping binaries would be wrong more
        /// often than it is worth — for Steam this is filled in later from the helper's own log, or
        /// stays null. Only used for matching a ClawTweaks per-game profile.</summary>
        public string ExePath { get; set; }

        /// <summary>Local cover art, already on disk. Null means none was found — the tile then draws
        /// itself from the title (see GameArt), it never shows a broken image.</summary>
        public string ArtPath { get; set; }

        /// <summary>
        /// Total minutes played, where the store records it locally. 0 = unknown, which is the normal
        /// answer for everything except Steam - Epic writes no playtime to disk, Xbox keeps it in the
        /// cloud, and a ROM's time belongs to Playnite. See SteamPlaytime.
        /// </summary>
        public int PlaytimeMinutes { get; set; }

        /// <summary>
        /// How much room the install takes, in bytes. 0 = unknown, which is the normal answer
        /// everywhere except Steam: Steam writes the figure into the manifest, and everyone else
        /// would mean walking the folder tree on every scan round.
        /// </summary>
        public long InstallBytes { get; set; }

        /// <summary>
        /// The wide 1920x620 key image, for the launch screen's backdrop. Null = none found, and the
        /// launch screen then falls back to a blurred cover. See GameArt.FindSteamHero.
        /// </summary>
        public string HeroPath { get; set; }

        /// <summary>
        /// Whether the game is on the disk and ready to start.
        ///
        /// FALSE HAS TWO MEANINGS and the tab shows both: owned but never installed (from
        /// SteamOwned), or installed but not finished (a Steam manifest that has not reached
        /// fully-installed - see DownloadTotalBytes). They belong on the same shelf because they
        /// answer the same question: this is a game you have that you cannot start yet.
        ///
        /// EVERY OTHER GROUPING FILTERS THESE OUT. A shelf of covers is a shelf of things to play,
        /// and an entry that cannot be played does not belong in Recent, in All, or under its store.
        /// </summary>
        public bool Installed { get; set; } = true;

        /// <summary>Download progress, where the store reports it. Both 0 means "not downloading" or
        /// "no figures" - only Steam fills these in, out of the same manifest the entry came from.
        /// </summary>
        public long DownloadedBytes { get; set; }

        public long DownloadTotalBytes { get; set; }

        /// <summary>0..100, or -1 when there is nothing to report. A download Steam has queued but
        /// not started reads as 0%, which is true and different from "not downloading".</summary>
        public int DownloadPercent => DownloadTotalBytes <= 0
            ? -1
            : (int)Math.Min(100, Math.Max(0, DownloadedBytes * 100 / DownloadTotalBytes));

        /// <summary>Which ClawTweaks per-game profiles exist for this entry. Filled on every scan
        /// round from the files on disk - see ClawProfiles.</summary>
        public ClawProfileKinds Profiles { get; set; }

        /// <summary>Last time this was played, from whichever source knew it best (see PlayHistory).
        /// Null = never seen played, which is a normal answer, not a gap.</summary>
        public DateTime? LastPlayed { get; set; }

        /// <summary>User-picked, via the Start-button game menu. Backed by FavoritesStore and
        /// re-applied on every scan round from that file - the flag on this object is a read cache,
        /// never the source of truth, so toggling it never needs to survive a rebuild by itself.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>Stable cross-source identity for FavoritesStore and ArtOverrideStore: Store alone
        /// is not unique, Id alone collides between stores (a Steam AppID and an Epic AppName can be
        /// the same string by coincidence), so both together are what those stores key on.</summary>
        public string FavoriteKey => Store + "|" + Id;

        public string StoreName
        {
            get
            {
                switch (Store)
                {
                    case GameStore.Steam: return "Steam";
                    case GameStore.Epic: return "Epic";
                    case GameStore.Xbox: return "Xbox";
                    case GameStore.Ubisoft: return "Ubisoft";
                    case GameStore.EA: return "EA";
                    case GameStore.BattleNet: return "Battle.net";
                    case GameStore.Gog: return "GOG";
                    case GameStore.Misc: return "My Apps";
                    default: return SystemName ?? "Playnite";
                }
            }
        }
    }
}
