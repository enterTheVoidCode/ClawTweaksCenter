using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// One user-picked cover per game, on disk.
    ///
    /// SEPARATE from SteamGridDb's own index, and deliberately so: that one is keyed by NORMALIZED
    /// TITLE, because the silent background fill wants two same-named games to share one lookup and
    /// one cache file. A manual pick from the Start-button game menu is the opposite - it is a
    /// decision about ONE tile - so it is keyed by GameEntry.FavoriteKey (Store + Id) instead, and a
    /// Steam game and a same-named ROM can carry two completely different covers without either
    /// ever touching the other's file.
    /// </summary>
    public static class ArtOverrideStore
    {
        private static string FilePath => Path.Combine(SteamGridDb.CacheDir, "overrides.json");

        // FavoriteKey -> absolute path of the downloaded file.
        private static Dictionary<string, string> _index;
        private static readonly object IndexLock = new object();

        private static Dictionary<string, string> Index
        {
            get
            {
                lock (IndexLock)
                {
                    if (_index != null) return _index;
                    _index = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                            if (loaded != null) _index = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                        }
                    }
                    catch { }
                    return _index;
                }
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(SteamGridDb.CacheDir);
                string tmp = FilePath + ".tmp";
                lock (IndexLock) File.WriteAllText(tmp, JsonSerializer.Serialize(_index));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>Records a pick and stamps it onto the object handed in, so the tile the user is
        /// looking at updates without waiting for the next scan round.</summary>
        public static void Set(GameEntry game, string downloadedPath)
        {
            if (game == null || string.IsNullOrEmpty(downloadedPath)) return;
            lock (IndexLock) Index[game.FavoriteKey] = downloadedPath;
            Save();
            game.ArtPath = downloadedPath;
        }

        /// <summary>
        /// Stamps every override onto the current game list. Always the LAST art step in a scan round
        /// (after GameArt.ResolveLocalArt and after SteamGridDb's own auto-fill result) - a manual
        /// pick is a decision the user made about this exact tile and outranks whatever a rescan or
        /// the silent auto-fill would otherwise have put there.
        /// </summary>
        public static void ApplyTo(IEnumerable<GameEntry> games)
        {
            var index = Index;
            if (index.Count == 0) return;
            foreach (var g in games)
            {
                if (!index.TryGetValue(g.FavoriteKey, out string path)) continue;
                // A file that vanished (cache cleared by hand) must fall back to whatever local/
                // auto-fetched art already resolved, not draw a broken image.
                if (File.Exists(path)) g.ArtPath = path;
            }
        }
    }
}
