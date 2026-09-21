using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Store games the user took off the shelves, on disk.
    ///
    /// Same shape as <see cref="FavoritesStore"/> and for the same reason: GameEntry is rebuilt from
    /// the stores on every scan, so a flag on it cannot be the source of truth. A game from a scan
    /// cannot be deleted - the next scan brings it back - so hiding is the only way to get it off the
    /// shelf. Entries the user added by hand (My Apps) are deleted instead, never hidden.
    ///
    /// THE TITLE IS STORED WITH THE KEY. The list in Library settings has to name a hidden game even
    /// when the scan no longer finds it (uninstalled, store logged out); a key alone would show up
    /// there as "Steam|1245620", and that entry could never be brought back by anyone who cannot
    /// read app ids.
    /// </summary>
    public static class HiddenGamesStore
    {
        public sealed class Entry
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public string Store { get; set; }
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "hidden-games.json");

        public static List<Entry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Entry>();
                var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
                return (list ?? new List<Entry>()).Where(e => !string.IsNullOrEmpty(e?.Key)).ToList();
            }
            catch { return new List<Entry>(); }
        }

        private static void Save(List<Entry> entries)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>Stamps IsHidden onto every entry from the file. Called wherever FavoritesStore
        /// is, so every way the library list gets built agrees about it.</summary>
        public static void ApplyTo(IEnumerable<GameEntry> games)
        {
            var keys = new HashSet<string>(Load().Select(e => e.Key), StringComparer.Ordinal);
            foreach (var g in games) g.IsHidden = keys.Count > 0 && keys.Contains(g.FavoriteKey);
        }

        /// <summary>Hides one game, on disk and on the object handed in - the render the caller is
        /// about to do reads that object, not the file.</summary>
        public static void Hide(GameEntry game)
        {
            if (game == null || game.Store == GameStore.Misc) return;
            var entries = Load();
            if (!entries.Any(e => e.Key == game.FavoriteKey))
            {
                entries.Add(new Entry { Key = game.FavoriteKey, Title = game.Title, Store = game.StoreName });
                Save(entries);
            }
            game.IsHidden = true;
        }

        /// <summary>Takes one key off the list. The caller re-applies to the live games.</summary>
        public static void Unhide(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var entries = Load();
            if (entries.RemoveAll(e => e.Key == key) > 0) Save(entries);
        }

        public static int Count() => Load().Count;
    }
}
