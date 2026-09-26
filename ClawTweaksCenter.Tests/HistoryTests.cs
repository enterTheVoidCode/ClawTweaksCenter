using ClawTweaksCenter.Library;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    [RegressionTest]
    private static void HarvestManifestRecognizesUnchangedLogsAcrossProcesses()
    {
        WithHistoryFile(path =>
        {
            string logs = WriteHelperLog(path);
            RunHarvestProbe(path, logs, @"C:\SyntheticGames\First");
            string manifest = Path.Combine(Path.GetDirectoryName(path)!, "playharvest.json");
            string before = File.ReadAllText(manifest);

            // A valid manifest skips the log before opening it. Locking just the synthetic log
            // distinguishes that skip from silently re-reading it and obtaining the same history.
            using (File.Open(Path.Combine(logs, "helper_synthetic.log"), FileMode.Open, FileAccess.Read, FileShare.None))
                RunHarvestProbe(path, logs, @"C:\SyntheticGames\First");

            AssertEx.Equal(before, File.ReadAllText(manifest), "A fresh process must retain the unchanged log's harvest stamp.");
        });
    }

    [RegressionTest]
    private static void HarvestDirectorySetIgnoresCaseOrderAndDuplicates()
    {
        WithHistoryFile(path =>
        {
            string logs = WriteHelperLog(path);
            RunHarvestProbe(path, logs, @"C:\SyntheticGames\First", @"C:\SyntheticGames\Second");
            string manifest = Path.Combine(Path.GetDirectoryName(path)!, "playharvest.json");
            string before = File.ReadAllText(manifest);

            using (File.Open(Path.Combine(logs, "helper_synthetic.log"), FileMode.Open, FileAccess.Read, FileShare.None))
                RunHarvestProbe(path, logs, @"c:\syntheticgames\second\", @"c:\SYNTHETICGAMES\FIRST", @"C:\SyntheticGames\Second");

            AssertEx.Equal(before, File.ReadAllText(manifest), "Equivalent directory sets must reuse the manifest.");
        });
    }

    [RegressionTest]
    private static void ChangedHarvestDirectorySetReadsOldLogsForNewGames()
    {
        WithHistoryFile(path =>
        {
            string logs = WriteHelperLog(path);
            RunHarvestProbe(path, logs, @"C:\SyntheticGames\First");
            string manifest = Path.Combine(Path.GetDirectoryName(path)!, "playharvest.json");
            string initialKey = ReadDirectoryKey(manifest);
            AssertEx.Equal<DateTime?>(null, PlayHistory.Load(path).LastPlayedFor(@"C:\SyntheticGames\Second"));

            RunHarvestProbe(path, logs, @"C:\SyntheticGames\First", @"C:\SyntheticGames\Second");
            string addedKey = ReadDirectoryKey(manifest);
            AssertEx.False(initialKey == addedKey, "Adding a directory must invalidate the manifest.");
            AssertEx.Equal<DateTime?>(Earlier, PlayHistory.Load(path).LastPlayedFor(@"C:\SyntheticGames\Second"));

            RunHarvestProbe(path, logs, @"C:\SyntheticGames\First");
            AssertEx.Equal(initialKey, ReadDirectoryKey(manifest), "Removing the directory restores the original set's fingerprint.");
        });
    }

    [RegressionProbe("history-harvest")]
    private static string HarvestProbe(string[] args)
    {
        var history = PlayHistory.Load(args[0]);
        var games = args.Skip(2).Select(dir => new GameEntry { Store = GameStore.Steam, InstallDir = dir }).ToArray();
        history.HarvestHelperLogs(games, CancellationToken.None, args[1]);
        history.SaveIfChanged();
        return "harvested";
    }

    private static void RunHarvestProbe(string path, string logs, params string[] directories)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--probe", "history-harvest", path, logs }.Concat(directories))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Synthetic history probe timed out.");
        }
        AssertEx.Equal(0, process.ExitCode, error.GetAwaiter().GetResult());
        AssertEx.Equal("harvested", output.GetAwaiter().GetResult().Trim());
    }

    private static string WriteHelperLog(string historyPath)
    {
        string logs = Path.Combine(Path.GetDirectoryName(historyPath)!, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "helper_synthetic.log"), new[]
        {
            @"2025-01-02 10:00:00.001 [GameDetection] Latched 'First' to PID=1 (key='C:\SyntheticGames\First\game.exe'",
            @"2025-01-02 10:00:00.002 [GameDetection] Latched 'Second' to PID=2 (key='C:\SyntheticGames\Second\game.exe'",
        });
        return logs;
    }

    private static string ReadDirectoryKey(string manifest)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        return json.RootElement.GetProperty("DirsKey").GetString()!;
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
