using System;

namespace ClawTweaksCenter.Library
{
    /// <summary>Which store an entry came from. The order is the order the group strip shows.</summary>
    public enum GameStore
    {
        Steam,
        Epic,
        Xbox,
        /// <summary>A ROM, described and launched by Playnite. Not a store - the name is what the
        /// user sees, and "Playnite" is what they installed.</summary>
        Playnite,
        /// <summary>A tool the user added by hand. The only kind of entry nothing discovered - see
        /// MiscSource.</summary>
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
                    case GameStore.Misc: return "Misc";
                    default: return SystemName ?? "Playnite";
                }
            }
        }
    }
}
