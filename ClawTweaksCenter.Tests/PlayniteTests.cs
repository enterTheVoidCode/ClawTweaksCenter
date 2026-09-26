using System.IO;
using System.Text.Json;
using ClawTweaksCenter.Library;
using LiteDB;

namespace ClawTweaksCenter.Tests;

internal static class PlayniteTests
{
    [RegressionTest]
    private static void SuccessfulEmptyDatabaseRemovesDeletedRomsFromCache()
    {
        using var fixture = new PlayniteFixture();
        fixture.SeedCachedRom();
        fixture.SetGames();

        var games = fixture.Scan();

        AssertEx.Equal(0, games.Count, "A successful empty scan must not resurrect a deleted ROM.");
        AssertEx.False(PlayniteSource.UsedCache);
        AssertEx.Equal(0, PlayniteSource.LastSystems.Count);
        AssertEx.True(PlayniteSource.LastArtIndex.IsEmpty);
        using (var cache = JsonDocument.Parse(File.ReadAllText(fixture.CachePath)))
            AssertEx.Equal(0, cache.RootElement.GetProperty("Roms").GetArrayLength());

        fixture.Corrupt("games.db");
        AssertEx.Equal(0, fixture.Scan().Count, "A later failure must fall back to the new empty cache.");
        AssertEx.True(PlayniteSource.UsedCache, "An empty saved snapshot is still a valid cache fallback.");
    }

    [RegressionTest]
    private static void PcOnlyScanKeepsLiveArtworkAndReplacesStaleRoms()
    {
        using var fixture = new PlayniteFixture();
        fixture.SeedCachedRom();
        string liveCover = fixture.WriteCover("live.png");
        var storeGame = fixture.Game(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Store Game", "live.png");
        storeGame["SourceId"] = fixture.SourceId;
        var windowsGame = fixture.Game(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Windows Game", "live.png");
        windowsGame["PlatformIds"] = new BsonArray { fixture.WindowsId };
        fixture.SetGames(storeGame, windowsGame);

        var games = fixture.Scan();

        AssertEx.Equal(0, games.Count);
        AssertEx.False(PlayniteSource.UsedCache);
        AssertEx.Equal(0, PlayniteSource.LastSystems.Count);
        AssertEx.Equal(liveCover, PlayniteSource.LastArtIndex.TryFindArt(new GameEntry { InstallDir = fixture.InstallDir }));
        AssertEx.Equal(liveCover, PlayniteSource.LastArtIndex.TryFindArt(new GameEntry { Title = "Windows Game" }));
        AssertEx.Equal<string?>(null, PlayniteSource.LastArtIndex.TryFindArt(new GameEntry { Title = "Cached ROM" }));

        fixture.Corrupt("games.db");
        AssertEx.Equal(0, fixture.Scan().Count);
        AssertEx.Equal(liveCover, PlayniteSource.LastArtIndex.TryFindArt(new GameEntry { Title = "Store Game" }));
    }

    [RegressionTest]
    private static void CorruptGameDatabaseRetainsLastSuccessfulSnapshot()
    {
        using var fixture = new PlayniteFixture();
        fixture.SeedCachedRom();
        string before = File.ReadAllText(fixture.CachePath);
        fixture.Corrupt("games.db");

        AssertCachedRom(fixture.Scan());
        AssertEx.True(PlayniteSource.UsedCache);
        AssertEx.Equal(before, File.ReadAllText(fixture.CachePath));
        AssertEx.Equal(Path.Combine(fixture.LibraryDir, "files", "old.png"),
            PlayniteSource.LastArtIndex.TryFindArt(new GameEntry { Title = "Cached ROM" }));
    }

    [RegressionTest]
    private static void ExclusiveDatabaseLockRetainsLastSuccessfulSnapshot()
    {
        using var fixture = new PlayniteFixture();
        fixture.SeedCachedRom();
        string before = File.ReadAllText(fixture.CachePath);
        using (File.Open(Path.Combine(fixture.LibraryDir, "games.db"), System.IO.FileMode.Open, FileAccess.Read, FileShare.None))
            AssertCachedRom(fixture.Scan());
        AssertEx.True(PlayniteSource.UsedCache);
        AssertEx.Equal(before, File.ReadAllText(fixture.CachePath));
    }

    [RegressionTest]
    private static void CorruptClassificationDatabaseRetainsLastSuccessfulSnapshot()
    {
        foreach (string database in new[] { "platforms.db", "sources.db" })
        {
            using var fixture = new PlayniteFixture();
            fixture.SeedCachedRom();
            string before = File.ReadAllText(fixture.CachePath);
            fixture.SetGames(fixture.Game(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Changed ROM", "old.png"));
            fixture.Corrupt(database);

            AssertCachedRom(fixture.Scan());
            AssertEx.True(PlayniteSource.UsedCache, "Unreadable classification data is not a successful scan.");
            AssertEx.Equal(before, File.ReadAllText(fixture.CachePath));
        }
    }

    [RegressionTest]
    private static void ReadFailureWithNoCacheReturnsAnEmptyLibrary()
    {
        using var fixture = new PlayniteFixture();
        fixture.Corrupt("games.db");
        AssertEx.Equal(0, fixture.Scan().Count);
        AssertEx.False(PlayniteSource.UsedCache);
        AssertEx.True(PlayniteSource.LastArtIndex.IsEmpty);
        AssertEx.Equal(0, PlayniteSource.LastSystems.Count);
    }

    private static void AssertCachedRom(IReadOnlyList<GameEntry> games)
    {
        AssertEx.Equal(1, games.Count);
        AssertEx.Equal("11111111-1111-1111-1111-111111111111", games[0].Id);
        AssertEx.Equal("Cached ROM", games[0].Title);
        AssertEx.Equal<DateTime?>(new DateTime(2025, 1, 2, 10, 0, 0, DateTimeKind.Utc).ToLocalTime(), games[0].LastPlayed);
    }

    private sealed class PlayniteFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClawTweaksCenter.Tests", Guid.NewGuid().ToString("N"));
        public string LibraryDir => Path.Combine(_root, "library");
        public string CachePath => Path.Combine(_root, "cache", "romcache.json");
        public string InstallDir => Path.Combine(_root, "roms");
        public Guid SourceId { get; } = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public Guid WindowsId { get; } = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly Guid _consoleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        public PlayniteFixture()
        {
            Directory.CreateDirectory(LibraryDir);
            using (var platforms = Open("platforms.db"))
            {
                platforms.GetCollection("Platform").Insert(new BsonDocument { ["_id"] = _consoleId, ["Name"] = "Synthetic Console" });
                platforms.GetCollection("Platform").Insert(new BsonDocument { ["_id"] = WindowsId, ["Name"] = "PC (Windows)" });
            }
            using (var sources = Open("sources.db"))
                sources.GetCollection("Source").Insert(new BsonDocument { ["_id"] = SourceId, ["Name"] = "Steam" });
            SetGames();
        }

        public void SeedCachedRom()
        {
            WriteCover("old.png");
            SetGames(Game(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Cached ROM", "old.png"));
            AssertCachedRom(Scan());
            AssertEx.False(PlayniteSource.UsedCache);
        }

        public BsonDocument Game(Guid id, string title, string cover) => new()
        {
            ["_id"] = id,
            ["Name"] = title,
            ["IsInstalled"] = true,
            ["InstallDirectory"] = InstallDir,
            ["PlatformIds"] = new BsonArray { _consoleId },
            ["CoverImage"] = cover,
            ["LastActivity"] = new DateTime(2025, 1, 2, 10, 0, 0, DateTimeKind.Utc),
        };

        public void SetGames(params BsonDocument[] games)
        {
            using var db = Open("games.db");
            var collection = db.GetCollection("Game");
            collection.Delete(Query.All());
            foreach (var game in games) collection.Insert(game);
        }

        public string WriteCover(string name)
        {
            string files = Path.Combine(LibraryDir, "files");
            Directory.CreateDirectory(files);
            string path = Path.Combine(files, name);
            File.WriteAllText(path, "Synthetic cover file; no image decoder runs in source scans.");
            return path;
        }

        public void Corrupt(string name) => File.WriteAllText(Path.Combine(LibraryDir, name), "Invalid LiteDB fixture");
        public IReadOnlyList<GameEntry> Scan() => PlayniteSource.Scan(CancellationToken.None, LibraryDir, CachePath);
        private LiteDatabase Open(string name) => new("Filename=" + Path.Combine(LibraryDir, name) + ";journal=false");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
