using System.IO;
using System.IO.Compression;
using ClawTweaksCenter.Core;
using Microsoft.Win32;

namespace ClawTweaksCenter.Tests;

internal static class BackupRestoreTests
{
    private const string ValidSettings = """[{"Name":"Language","Kind":"string","Value":"German"},{"Name":"Volume","Kind":"dword","Value":"25"}]""";

    [RegressionTest]
    private static void MalformedSettingsLeaveOriginalStoresIntact()
    {
        using var fixture = new Fixture();
        fixture.Zip("{broken", ("favorites.json", "new favorites"));
        AssertEx.Equal(-1, fixture.Restore(out var error));
        AssertEx.True(!string.IsNullOrEmpty(error));
        fixture.AssertOriginal();
    }

    [RegressionTest]
    private static void InvalidSettingValueLeavesOriginalStoresIntact()
    {
        using var fixture = new Fixture();
        fixture.Zip("""[{"Name":"Volume","Kind":"dword","Value":"not a number"}]""", ("favorites.json", "new favorites"));
        AssertEx.Equal(-1, fixture.Restore(out _));
        fixture.AssertOriginal();
    }

    [RegressionTest]
    private static void InvalidSettingsShapesLeaveOriginalStoresIntact()
    {
        foreach (string settings in new[]
        {
            "null", "[null]", "[{}]",
            """[{"Name":"Volume","Kind":"qword","Value":"9223372036854775808"}]""",
            """[{"Name":"Language","Kind":"unknown","Value":"German"}]""",
            """[{"Name":"Language","Kind":"string","Value":"German"},{"Name":"language","Kind":"string","Value":"French"}]"""
        })
        {
            using var fixture = new Fixture();
            fixture.Zip(settings, ("favorites.json", "new favorites"));
            AssertEx.Equal(-1, fixture.Restore(out _), "Invalid settings were accepted: " + settings);
            fixture.AssertOriginal();
        }
    }

    [RegressionTest]
    private static void UnsafeOrDuplicatePathsLeaveOriginalStoresIntact()
    {
        foreach (string path in new[] { "../outside", "nested/../../outside", "C:/outside", "favorites.json:stream", "favorites.json.", "FAVORITES.JSON" })
        {
            using var fixture = new Fixture();
            fixture.Zip(ValidSettings, ("favorites.json", "new favorites"), (path, "invalid"));
            AssertEx.Equal(-1, fixture.Restore(out _), "Invalid path was accepted: " + path);
            fixture.AssertOriginal();
        }
    }

    [RegressionTest]
    private static void PartialExtractionLeavesOriginalStoresIntact()
    {
        using var fixture = new Fixture();
        // Both entries are individually valid paths, but extraction of the second fails because
        // the first made its parent a file. This exercises real ZIP/file I/O after one extraction.
        fixture.Zip(ValidSettings, ("conflict", "file"), ("conflict/child", "child"));
        AssertEx.Equal(-1, fixture.Restore(out _));
        fixture.AssertOriginal();
    }

    [RegressionTest]
    private static void PartialFileCommitRollsBackOriginalStores()
    {
        using var fixture = new Fixture();
        // The log's directory cannot become a file. The preceding entry can be committed first.
        fixture.Write("logs/session.log", "keep this nested log");
        fixture.Zip(ValidSettings, ("favorites.json", "new favorites"), ("logs", "conflict"));
        AssertEx.Equal(-1, fixture.Restore(out _));
        fixture.AssertOriginal();
        AssertEx.Equal("keep this nested log", File.ReadAllText(Path.Combine(fixture.Data, "logs/session.log")));
    }

    [RegressionTest]
    private static void LockedOriginalFileRollsBackOtherOriginalFiles()
    {
        using var fixture = new Fixture();
        fixture.Zip(ValidSettings, ("favorites.json", "new favorites"));
        // A real Windows sharing violation when moving an existing store, after staging succeeds.
        using (var locked = new FileStream(Path.Combine(fixture.Data, "old.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            AssertEx.Equal(-1, fixture.Restore(out _));
        fixture.AssertOriginal();
    }

    [RegressionTest]
    private static void PartialSettingsWriteRollsBackFilesAndAllRegistryKinds()
    {
        using var fixture = new Fixture();
        fixture.Settings.Values["RawPath"] = new("RawPath", RegistryValueKind.ExpandString, "%TEMP%\\covers");
        fixture.Settings.Values["Binary"] = new("Binary", RegistryValueKind.Binary, new byte[] { 1, 2, 3 });
        fixture.Settings.Values["Multi"] = new("Multi", RegistryValueKind.MultiString, new[] { "one", "two" });
        fixture.Settings.Values["Counter"] = new("Counter", RegistryValueKind.QWord, long.MaxValue);
        fixture.Settings.FailOnceOnSet = "Volume";
        fixture.Zip(ValidSettings, ("favorites.json", "new favorites"), ("art/new.png", "new art"));
        AssertEx.Equal(-1, fixture.Restore(out _));
        fixture.AssertOriginal();
        AssertEx.Equal(RegistryValueKind.ExpandString, fixture.Settings.Values["RawPath"].Kind);
        AssertEx.Equal("%TEMP%\\covers", fixture.Settings.Values["RawPath"].Value);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3 }, (byte[])fixture.Settings.Values["Binary"].Value);
        AssertEx.SequenceEqual(new[] { "one", "two" }, (string[])fixture.Settings.Values["Multi"].Value);
        AssertEx.Equal(long.MaxValue, fixture.Settings.Values["Counter"].Value);
        AssertEx.False(File.Exists(Path.Combine(fixture.Data, "art/new.png")));
    }

    [RegressionTest]
    private static void FailedSettingsRollbackRetainsRecoveryAndStillRestoresFiles()
    {
        using var fixture = new Fixture();
        fixture.Settings.FailEverySet = true;
        fixture.Zip(ValidSettings, ("favorites.json", "new favorites"));
        AssertEx.Equal(-1, fixture.Restore(out var error));
        AssertEx.True(error.Contains("Rollback incomplete", StringComparison.Ordinal));
        AssertEx.Equal("original favorites", File.ReadAllText(Path.Combine(fixture.Data, "favorites.json")));
        AssertEx.Equal("original old choice", File.ReadAllText(Path.Combine(fixture.Data, "old.json")));
        string recovery = Directory.GetDirectories(Path.GetDirectoryName(fixture.Data)!, ".center-restore-*").Single();
        AssertEx.True(error.Contains(recovery, StringComparison.Ordinal));
        string settings = File.ReadAllText(Path.Combine(recovery, "settings-before.json"));
        AssertEx.True(settings.Contains("English", StringComparison.Ordinal));
        AssertEx.True(settings.Contains("OldChoice", StringComparison.Ordinal));
        AssertEx.Equal(1, fixture.Settings.Values["OnboardingPending"].Value);
    }

    [RegressionTest]
    private static void SuccessfulRestoreReplacesChoicesAndPreservesLogsAndInstallSettings()
    {
        using var fixture = new Fixture();
        fixture.Zip("""[{"Name":"Language","Kind":"string","Value":"German"},{"Name":"Volume","Kind":"dword","Value":"25"},{"Name":"OnboardingPending","Kind":"dword","Value":"0"}]""",
            ("favorites.json", "new favorites"), ("art/new.png", "new art"), ("install.log", "archive log"));
        AssertEx.Equal(3, fixture.Restore(out var error));
        AssertEx.Equal<string?>(null, error);
        AssertEx.Equal("new favorites", File.ReadAllText(Path.Combine(fixture.Data, "favorites.json")));
        AssertEx.Equal("new art", File.ReadAllText(Path.Combine(fixture.Data, "art/new.png")));
        AssertEx.False(File.Exists(Path.Combine(fixture.Data, "old.json")));
        AssertEx.Equal("original log", File.ReadAllText(Path.Combine(fixture.Data, "install.log")));
        AssertEx.Equal("German", fixture.Settings.Values["Language"].Value);
        AssertEx.Equal(25, fixture.Settings.Values["Volume"].Value);
        AssertEx.False(fixture.Settings.Values.ContainsKey("OldChoice"));
        AssertEx.Equal(1, fixture.Settings.Values["OnboardingPending"].Value);
        AssertEx.Equal(1, fixture.Settings.Values["VelopackUpdates"].Value);
    }

    [RegressionTest]
    private static void RestoreHandlesFilesChangingIntoDirectoriesAndBack()
    {
        using var fixture = new Fixture();
        fixture.Write("old-folder/child", "old child");
        fixture.Write("old-file", "old file");
        fixture.Zip("[]", ("old-folder", "now a file"), ("old-file/child", "now a directory"));
        AssertEx.Equal(3, fixture.Restore(out _));
        AssertEx.Equal("now a file", File.ReadAllText(Path.Combine(fixture.Data, "old-folder")));
        AssertEx.Equal("now a directory", File.ReadAllText(Path.Combine(fixture.Data, "old-file/child")));
        AssertEx.Equal(2, fixture.Settings.Values.Count);
        AssertEx.Equal("original log", File.ReadAllText(Path.Combine(fixture.Data, "install.log")));
    }

    private sealed class MemorySettings : ICenterBackupSettings
    {
        public Dictionary<string, CenterBackupSetting> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FailOnceOnSet { get; set; }
        public bool FailEverySet { get; set; }
        public IReadOnlyList<CenterBackupSetting> ReadAll() => Values.Values.ToArray();
        public void Delete(string name) => Values.Remove(name);
        public void Set(CenterBackupSetting value)
        {
            if (FailEverySet || value.Name == FailOnceOnSet)
            {
                FailOnceOnSet = null;
                throw new IOException("Simulated registry write failure.");
            }
            Values[value.Name] = value;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CenterRestoreTests-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(_root, "Center");
        private string ZipPath => Path.Combine(_root, "backup.zip");
        public MemorySettings Settings { get; } = new();

        public Fixture()
        {
            Write("favorites.json", "original favorites");
            Write("old.json", "original old choice");
            Write("install.log", "original log");
            Settings.Values["Language"] = new("Language", RegistryValueKind.String, "English");
            Settings.Values["OldChoice"] = new("OldChoice", RegistryValueKind.DWord, 42);
            Settings.Values["OnboardingPending"] = new("OnboardingPending", RegistryValueKind.DWord, 1);
            Settings.Values["VelopackUpdates"] = new("VelopackUpdates", RegistryValueKind.DWord, 1);
        }

        public void Write(string relative, string contents)
        {
            string path = Path.Combine(Data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Zip(string settings, params (string Path, string Contents)[] entries)
        {
            using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("CENTER/settings.json").Open())) writer.Write(settings);
            foreach (var entry in entries)
                using (var writer = new StreamWriter(zip.CreateEntry("CENTER/data/" + entry.Path).Open())) writer.Write(entry.Contents);
        }

        public int Restore(out string error) => CenterDataBackup.RestoreFromZip(ZipPath, Data, Settings, out error);

        public void AssertOriginal()
        {
            AssertEx.True(File.Exists(Path.Combine(Data, "favorites.json")), "Restore destroyed the original favorites store.");
            AssertEx.Equal("original favorites", File.ReadAllText(Path.Combine(Data, "favorites.json")));
            AssertEx.True(File.Exists(Path.Combine(Data, "old.json")), "Restore destroyed an unrelated original store.");
            AssertEx.Equal("original old choice", File.ReadAllText(Path.Combine(Data, "old.json")));
            AssertEx.Equal("original log", File.ReadAllText(Path.Combine(Data, "install.log")));
            AssertEx.Equal("English", Settings.Values["Language"].Value);
            AssertEx.Equal(42, Settings.Values["OldChoice"].Value);
            AssertEx.Equal(1, Settings.Values["OnboardingPending"].Value);
            AssertEx.Equal(1, Settings.Values["VelopackUpdates"].Value);
        }

        public void Dispose()
        {
            // Every fixture owns this generated child of TEMP; never clean an app-data directory.
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
