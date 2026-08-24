using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Which games the user pinned to the Favorites tab, on disk.
    ///
    /// A flat set of <see cref="GameEntry.FavoriteKey"/> strings, not a flag saved on the game itself
    /// - GameEntry is rebuilt from scratch every scan (it is what the stores report, not something we
    /// own), so nothing on it can be the source of truth. This file is.
    /// </summary>
    public static class FavoritesStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "favorites.json");

        public static HashSet<string> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new HashSet<string>(StringComparer.Ordinal);
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                return new HashSet<string>(list ?? new List<string>(), StringComparer.Ordinal);
            }
            catch { return new HashSet<string>(StringComparer.Ordinal); }
        }

        private static void Save(HashSet<string> keys)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(keys.ToList()));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>Stamps IsFavorite onto every entry from the current file. Called on every scan
        /// round (see GameLibrary.ScanAsync), which is what makes a toggle safe against a scan still
        /// in flight - the flag is re-derived from disk each time rather than carried across rounds,
        /// so there is nothing to lose the way an in-memory-only change could.</summary>
        public static void ApplyTo(IEnumerable<GameEntry> games)
        {
            var keys = Load();
            if (keys.Count == 0)
            {
                foreach (var g in games) g.IsFavorite = false;
                return;
            }
            foreach (var g in games) g.IsFavorite = keys.Contains(g.FavoriteKey);
        }

        /// <summary>Flips one game's favourite state, on disk and on the object handed in - the
        /// caller's own reference is what the current render already points at, so the tile updates
        /// without waiting for a rescan.</summary>
        public static void Toggle(GameEntry game)
        {
            if (game == null) return;
            var keys = Load();
            if (!keys.Remove(game.FavoriteKey)) keys.Add(game.FavoriteKey);
            Save(keys);
            game.IsFavorite = keys.Contains(game.FavoriteKey);
        }

        public static bool Any() => Load().Count > 0;
    }
}
