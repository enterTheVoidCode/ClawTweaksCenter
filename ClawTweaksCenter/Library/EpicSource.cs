using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Epic Games. Its launcher writes one plain-JSON <c>.item</c> manifest per installed game into
    /// ProgramData - readable without the launcher running and without an account.
    ///
    /// UNTESTED AGAINST A REAL INSTALL: there is no Epic game on the development machine, so this
    /// path has only ever run against a hand-written manifest. Treat a bug report here as likely
    /// real rather than as user error.
    /// </summary>
    public sealed class EpicSource : IGameSource
    {
        public GameStore Store => GameStore.Epic;

        public static string ManifestDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<GameEntry>>(() => Scan(ct), ct);

        private static IReadOnlyList<GameEntry> Scan(CancellationToken ct)
        {
            var games = new List<GameEntry>();
            string dir = ManifestDir;
            if (!Directory.Exists(dir)) return games;

            string[] items;
            try { items = Directory.GetFiles(dir, "*.item"); }
            catch { return games; }

            foreach (string file in items)
            {
                ct.ThrowIfCancellationRequested();
                var entry = ReadManifest(file);
                if (entry != null) games.Add(entry);
            }
            return games;
        }

        private static GameEntry ReadManifest(string path)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var root = doc.RootElement;

                    string appName = Str(root, "AppName");
                    string mainGame = Str(root, "MainGameAppName");
                    string displayName = Str(root, "DisplayName");
                    string installLocation = Str(root, "InstallLocation");
                    string ns = Str(root, "CatalogNamespace");
                    string catalogItemId = Str(root, "CatalogItemId");
                    string launchExe = Str(root, "LaunchExecutable");

                    if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation)) return null;

                    // A DLC carries its parent's AppName in MainGameAppName. Listing those puts
                    // add-ons in the grid next to the games they belong to, and starting one does
                    // nothing useful.
                    if (!string.IsNullOrWhiteSpace(mainGame) &&
                        !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase)) return null;

                    // A half-finished download is not something to offer a launch button for.
                    if (Bool(root, "bIsIncompleteInstall")) return null;

                    if (!Directory.Exists(installLocation)) return null;
                    if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(catalogItemId)) return null;

                    string exe = null;
                    if (!string.IsNullOrWhiteSpace(launchExe))
                    {
                        try
                        {
                            string candidate = Path.Combine(installLocation, launchExe);
                            if (File.Exists(candidate)) exe = candidate;
                        }
                        catch { }
                    }

                    return new GameEntry
                    {
                        Id = appName,
                        Store = GameStore.Epic,
                        Title = string.IsNullOrWhiteSpace(displayName) ? appName : displayName,
                        InstallDir = installLocation,
                        // The three ids are colon-separated INSIDE one path segment, so the colons
                        // have to arrive as %3A - an unescaped colon there makes the launcher treat
                        // the rest as a different route and silently open the store instead.
                        LaunchUri = "com.epicgames.launcher://apps/"
                                    + Uri.EscapeDataString(ns) + "%3A"
                                    + Uri.EscapeDataString(catalogItemId) + "%3A"
                                    + Uri.EscapeDataString(appName)
                                    + "?action=launch&silent=true",
                        ExePath = exe,
                    };
                }
            }
            catch { return null; }
        }

        private static string Str(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
