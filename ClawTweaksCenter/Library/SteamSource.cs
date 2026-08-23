using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using ValveKeyValue;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Steam, read entirely from disk. Steam records every installed game in an
    /// <c>appmanifest_&lt;appid&gt;.acf</c> next to the files, and every library folder in
    /// <c>libraryfolders.vdf</c> - both are KeyValues text, both are always there, and neither needs
    /// an account.
    /// </summary>
    public sealed class SteamSource : IGameSource
    {
        public GameStore Store => GameStore.Steam;

        /// <summary>
        /// AppIDs that are installed like games and are not games. This is DATA, not logic - extend
        /// the list, do not add conditions. Steamworks Common Redistributables in particular sits on
        /// more or less every machine that has ever installed a game, this one included.
        /// </summary>
        private static readonly HashSet<string> NonGameAppIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "228980",  // Steamworks Common Redistributables
            "1070560", // Steam Linux Runtime 1.0 (scout)
            "1391110", // Steam Linux Runtime 2.0 (soldier)
            "1628350", // Steam Linux Runtime 3.0 (sniper)
            "1493710", // Proton Experimental
            "2180100", // Proton Hotfix
            "1887720", // Proton 7.0
            "2230260", // Proton 8.0
            "2805730", // Proton 9.0
        };

        /// <summary>StateFlags bit 2 (value 4) is "fully installed". Anything else is a download in
        /// progress, an update, or a stub - listing those means offering to start a game that is not
        /// there yet.</summary>
        private const int StateFlagFullyInstalled = 4;

        /// <summary>Where Steam itself is, per the registry. NOT a hardcoded Program Files (x86) path:
        /// Steam installs anywhere, and on a handheld it very often is not on C:.</summary>
        public static string SteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return Path.GetFullPath(p);
                }
            }
            catch { }
            return null;
        }

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();
            string steam = SteamPath();
            if (steam == null) return games;

            foreach (string lib in LibraryFolders(steam))
            {
                ct.ThrowIfCancellationRequested();
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;

                string[] manifests;
                try { manifests = Directory.GetFiles(apps, "appmanifest_*.acf"); }
                catch { continue; }

                foreach (string manifest in manifests)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = ReadManifest(manifest, apps);
                    if (entry != null) games.Add(entry);
                }
            }
            return games;
        }

        /// <summary>Every Steam library root, the install itself included. A handheld regularly has
        /// two (internal plus microSD); reading only the main one silently loses half the library.</summary>
        public static IReadOnlyList<string> LibraryFolders(string steamPath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            if (string.IsNullOrEmpty(steamPath)) return result;
            seen.Add(steamPath);
            result.Add(steamPath);

            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return result;

            try
            {
                var root = Deserialize(vdf);
                if (root == null) return result;
                foreach (var child in root)
                {
                    // Two historical shapes: "0" { "path" "D:\SteamLibrary" ... } and the older
                    // "0" "D:\SteamLibrary". Both still occur in the wild.
                    string path = ValueOf(child.Value, "path") ?? Text(child.Value);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!Directory.Exists(path)) continue;
                    string full = Path.GetFullPath(path);
                    if (seen.Add(full)) result.Add(full);
                }
            }
            catch { }
            return result;
        }

        private static GameEntry ReadManifest(string manifestPath, string steamappsDir)
        {
            try
            {
                var app = Deserialize(manifestPath);
                if (app == null) return null;

                string appId = ValueOf(app, "appid");
                string name = ValueOf(app, "name");
                string installDir = ValueOf(app, "installdir");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir)) return null;
                if (NonGameAppIds.Contains(appId)) return null;

                if (!int.TryParse(ValueOf(app, "StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags))
                    flags = 0;
                if ((flags & StateFlagFullyInstalled) == 0) return null;

                string dir = Path.Combine(steamappsDir, "common", installDir);
                if (!Directory.Exists(dir)) return null;

                return new GameEntry
                {
                    Id = appId,
                    Store = GameStore.Steam,
                    Title = string.IsNullOrWhiteSpace(name) ? installDir : name,
                    InstallDir = dir,
                    LaunchUri = "steam://rungameid/" + appId,
                    LastPlayed = UnixToLocal(ValueOf(app, "LastPlayed")),
                };
            }
            catch { return null; }
        }

        /// <summary>Steam's own play timestamp. It is reliable where it is set - measured on this
        /// machine, 24 of 44 manifests carry a real value and the rest are games never started here.
        /// A 0 therefore means "never played", not "Steam does not track this".</summary>
        private static DateTime? UnixToLocal(string unixSeconds)
        {
            if (!long.TryParse(unixSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long secs)) return null;
            if (secs <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime; }
            catch { return null; }
        }

        /// <summary>The document's ROOT node. Both file kinds wrap everything in one named block
        /// ("libraryfolders", "AppState"), so the interesting keys are one level in.</summary>
        private static KVObject Deserialize(string path)
        {
            // Shared read: Steam keeps these files open while it runs, and a library that only works
            // with Steam closed is a library that never works.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var kv = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
                return kv.Deserialize(fs)?.Root;
            }
        }

        /// <summary>A child value by name, or null. Case-insensitive on purpose: Steam is not
        /// consistent about capitalisation across manifest generations (LastPlayed / lastplayed).</summary>
        private static string ValueOf(KVObject parent, string key)
        {
            if (parent == null) return null;
            if (parent.TryGetValue(key, out var direct)) return Text(direct);
            foreach (var child in parent)
                if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))
                    return Text(child.Value);
            return null;
        }

        /// <summary>The scalar text of a node, or null for a node that holds children rather than a
        /// value - ToString on a collection yields something that is not a path and not a number.</summary>
        private static string Text(KVObject node)
        {
            if (node == null || node.IsNull || node.IsCollection || node.IsArray) return null;
            try { return node.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
