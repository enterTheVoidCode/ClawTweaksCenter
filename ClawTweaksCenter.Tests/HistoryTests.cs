using ClawTweaksCenter.Library;
using System.IO;
using System.Text.Json;

namespace ClawTweaksCenter.Tests;

internal static class HistoryTests
{
    private static readonly DateTime Earlier = new(2025, 1, 2, 10, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Later = Earlier.AddDays(1);
    private const string SharedDirectory = @"C:\SyntheticGames\Roms";

    [RegressionTest]
    private static void LaunchingOneRomDoesNotMarkItsNeighbourPlayed()
    {
        var history = new PlayHistory();
        var first = Rom("11111111-1111-1111-1111-111111111111");
        var second = Rom("22222222-2222-2222-2222-222222222222");

        history.NoteLaunch(first, Later);
        history.ApplyTo(new[] { first, second });

        AssertEx.Equal<DateTime?>(Later, first.LastPlayed);
        AssertEx.Equal<DateTime?>(null, second.LastPlayed, "A different ROM in the same folder was never played.");
    }

    [RegressionTest]
    private static void SharedRomFolderRetainsEachPlayniteLastActivity()
    {
        var history = new PlayHistory();
        var first = Rom("first", Later);
        var second = Rom("second", Earlier);

        history.ApplyTo(new[] { first, second });
        history.ApplyTo(new[] { second, first });

        AssertEx.Equal<DateTime?>(Later, first.LastPlayed);
        AssertEx.Equal<DateTime?>(Earlier, second.LastPlayed, "Playnite's older activity belongs to this ROM only.");
    }

    [RegressionTest]
    private static void LegacyDirectoryHistoryCannotIdentifyAnIndividualRom()
    {
        var history = new PlayHistory();
        history.Note(SharedDirectory, Later);
        history.NoteExe(SharedDirectory, SharedDirectory + @"\pc-game.exe");
        var knownRom = Rom("known", Earlier);
        var unknownRom = Rom("unknown");
        var pcGame = new GameEntry { Store = GameStore.Steam, Id = "pc", InstallDir = SharedDirectory };

        history.ApplyTo(new[] { knownRom, unknownRom, pcGame });

        AssertEx.Equal<DateTime?>(Earlier, knownRom.LastPlayed);
        AssertEx.Equal<DateTime?>(null, unknownRom.LastPlayed);
        AssertEx.Equal<string?>(null, unknownRom.ExePath);
        AssertEx.Equal<DateTime?>(Later, pcGame.LastPlayed);
        AssertEx.Equal(SharedDirectory + @"\pc-game.exe", pcGame.ExePath);
    }

    [RegressionTest]
    private static void StableIdsRemainSeparateAcrossStores()
    {
        var history = new PlayHistory();
        var rom = Rom("same-id");
        var app = new GameEntry { Store = GameStore.Misc, Id = "same-id" };
        var pcGame = new GameEntry { Store = GameStore.Epic, Id = "same-id", InstallDir = SharedDirectory };
        history.NoteLaunch(rom, Later);
        history.NoteLaunch(app, Earlier);

        history.ApplyTo(new[] { rom, app, pcGame });

        AssertEx.Equal<DateTime?>(Later, rom.LastPlayed);
        AssertEx.Equal<DateTime?>(Earlier, app.LastPlayed);
        AssertEx.Equal<DateTime?>(null, pcGame.LastPlayed);
    }

    [RegressionTest]
    private static void RomHistorySurvivesReloadAndFolderChanges()
    {
        WithHistoryFile(path =>
        {
            var history = PlayHistory.Load(path);
            var first = Rom("first", Earlier);
            var second = Rom("second", Earlier.AddDays(-1));
            var app = new GameEntry { Store = GameStore.Misc, Id = "first" };
            history.ApplyTo(new[] { first, second });
            history.NoteLaunch(first, Later);
            history.NoteLaunch(app, Earlier);
            history.SaveIfChanged();
            AssertEx.True(File.Exists(path), "History must be persisted to the supplied file.");

            var reloaded = PlayHistory.Load(path);
            var movedFirst = Rom("first", Earlier);
            movedFirst.InstallDir = @"D:\MovedRoms";
            var movedSecond = Rom("second");
            movedSecond.InstallDir = null;
            reloaded.ApplyTo(new[] { movedFirst, movedSecond, app });
            AssertEx.Equal<DateTime?>(Later, movedFirst.LastPlayed);
            AssertEx.Equal<DateTime?>(Earlier.AddDays(-1), movedSecond.LastPlayed);
            AssertEx.Equal<DateTime?>(Earlier, app.LastPlayed);

            movedSecond.LastPlayed = Later.AddDays(1);
            reloaded.ApplyTo(new[] { movedSecond });
            reloaded.SaveIfChanged();
            var freshSecond = Rom("second");
            PlayHistory.Load(path).ApplyTo(new[] { freshSecond });
            AssertEx.Equal<DateTime?>(Later.AddDays(1), freshSecond.LastPlayed);
        });
    }

    [RegressionTest]
    private static void LegacyFileRetainsPcHistoryWithoutInventingRomHistory()
    {
        WithHistoryFile(path =>
        {
            string exe = SharedDirectory + @"\game.exe";
            File.WriteAllText(path, JsonSerializer.Serialize(new[]
            {
                new { Dir = SharedDirectory, LastPlayedUtcTicks = Later.ToUniversalTime().Ticks, Exe = exe },
            }));
            var history = PlayHistory.Load(path);
            var first = Rom("first", Earlier);
            var neverPlayed = Rom("never-played");
            history.ApplyTo(new[] { first, neverPlayed });
            history.SaveIfChanged();

            var reloaded = PlayHistory.Load(path);
            first = Rom("first");
            reloaded.ApplyTo(new[] { first, neverPlayed });
            AssertEx.Equal<DateTime?>(Earlier, first.LastPlayed);
            AssertEx.Equal<DateTime?>(null, neverPlayed.LastPlayed);
            AssertEx.Equal<DateTime?>(Later, reloaded.LastPlayedFor(SharedDirectory));
            AssertEx.Equal(exe, reloaded.ExeFor(SharedDirectory));
        });
    }

    [RegressionTest]
    private static void RomWithoutStableIdDoesNotFallBackToSharedFolder()
    {
        var history = new PlayHistory();
        var unidentified = Rom("", Earlier);
        history.NoteLaunch(unidentified, Later);
        history.ApplyTo(new[] { unidentified });
        AssertEx.Equal<DateTime?>(Earlier, unidentified.LastPlayed);
        AssertEx.Equal<DateTime?>(null, history.LastPlayedFor(SharedDirectory));
    }

    private static void WithHistoryFile(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClawTweaksCenter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "playhistory.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static GameEntry Rom(string id, DateTime? activity = null) => new()
    {
        Store = GameStore.Playnite,
        Id = id,
        InstallDir = SharedDirectory,
        LastPlayed = activity,
    };
}
