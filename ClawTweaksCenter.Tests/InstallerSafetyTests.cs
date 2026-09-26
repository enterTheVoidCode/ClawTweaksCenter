using System.IO;
using ClawTweaksCenter.Core;

namespace ClawTweaksCenter.Tests;

internal static class InstallerSafetyTests
{
    [RegressionTest]
    private static void LockedSettingsStopPreflightBeforeRemovalOrDeployment()
    {
        using var fixture = new InstallationFixture(bundle: true);
        using var locked = File.Open(fixture.Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var installed = PackageInstaller.Install("update.msix", [], fixture.Operations, fixture.Log.Add);
        AssertEx.Equal(0, fixture.Removals, "Preflight removed a registration after an incomplete backup.");
        AssertEx.Equal(0, fixture.Additions, "Preflight backup failure must stop deployment.");
        AssertEx.False(installed);
        AssertEx.True(fixture.Log.Any(s => s.Contains("Install stopped", StringComparison.Ordinal)));
    }

    [RegressionTest]
    private static void LockedSettingsStopConflictRetryBeforeRemoval()
    {
        using var fixture = new InstallationFixture(bundle: false, conflict: true);
        using var locked = File.Open(fixture.Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var installed = PackageInstaller.Install("update.msix", [], fixture.Operations, fixture.Log.Add);
        AssertEx.Equal(0, fixture.Removals, "Conflict retry removed a registration after an incomplete backup.");
        AssertEx.Equal(1, fixture.Additions, "Backup failure must not retry deployment.");
        AssertEx.False(installed);
        AssertEx.True(fixture.Log.Any(s => s.Contains("Install stopped", StringComparison.Ordinal)));
    }

    [RegressionTest]
    private static void CompleteBackupRestoresSettingsAfterPreflightRemoval()
    {
        using var fixture = new InstallationFixture(bundle: true);
        AssertEx.True(PackageInstaller.Install("update.msix", [], fixture.Operations));
        AssertEx.Equal(1, fixture.Removals);
        AssertEx.Equal(1, fixture.Additions);
        AssertEx.Equal("synthetic settings", File.ReadAllText(fixture.Settings));
        AssertEx.Equal("synthetic settings", File.ReadAllText(Path.Combine(fixture.Operations.BackupFolder, "Settings", "settings.dat")));
    }

    [RegressionTest]
    private static void CompleteBackupRestoresSettingsAfterConflictRetry()
    {
        using var fixture = new InstallationFixture(bundle: false, conflict: true);
        AssertEx.True(PackageInstaller.Install("update.msix", [], fixture.Operations));
        AssertEx.Equal(1, fixture.Removals);
        AssertEx.Equal(2, fixture.Additions);
        AssertEx.Equal("synthetic settings", File.ReadAllText(fixture.Settings));
    }

    [RegressionTest]
    private static void ConflictAfterPreflightKeepsTheOriginalVerifiedBackup()
    {
        using var fixture = new InstallationFixture(bundle: true, conflict: true, recreateOnFailure: true);
        AssertEx.True(PackageInstaller.Install("update.msix", [], fixture.Operations));
        AssertEx.Equal(2, fixture.Removals);
        AssertEx.Equal(2, fixture.Additions);
        AssertEx.Equal("synthetic settings", File.ReadAllText(fixture.Settings));
    }

    [RegressionTest]
    private static void MatchingHealthyFamilyDoesNotRequireBackup()
    {
        using var fixture = new InstallationFixture(bundle: false);
        using var locked = File.Open(fixture.Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertEx.True(PackageInstaller.Install("update.msix", [], fixture.Operations));
        AssertEx.Equal(0, fixture.Removals);
        AssertEx.Equal(1, fixture.Additions);
        AssertEx.False(Directory.Exists(fixture.Operations.BackupFolder));
    }

    private sealed class InstallationFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ClawTweaksInstallerTests", Guid.NewGuid().ToString("N"));
        internal readonly PackageInstaller.InstallationOperations Operations;
        internal readonly List<string> Log = [];
        internal int Removals;
        internal int Additions;
        internal string Settings => Path.Combine(Operations.DataFolder, "Settings", "settings.dat");

        internal InstallationFixture(bool bundle, bool conflict = false, bool recreateOnFailure = false)
        {
            var entries = new List<PackageInstaller.FamilyEntry>
            {
                new() { FullName = "Synthetic.Package_1.0", IsBundle = bundle, Status = "Ok" }
            };
            Operations = new PackageInstaller.InstallationOperations
            {
                DataFolder = Path.Combine(root, "appdata"),
                BackupFolder = Path.Combine(root, "backup"),
                Inspect = () => entries.ToList(),
                Remove = (name, log) =>
                {
                    Removals++;
                    entries.RemoveAll(e => e.FullName == name);
                    // Simulate Windows removing app data only when it can; the locked-file tests
                    // still observe that an unsafe removal was attempted.
                    try { Directory.Delete(Path.Combine(root, "appdata"), true); } catch (IOException) { }
                    return true;
                },
                Add = (string path, IEnumerable<string> dependencies, Action<string> log, out string error) =>
                {
                    Additions++;
                    error = conflict && Additions == 1 ? "0x80073CF3" : null!;
                    if (error != null && recreateOnFailure)
                    {
                        entries.Add(new() { FullName = "Synthetic.Partial_2.0", Status = "Ok" });
                        var settings = Path.Combine(root, "appdata", "Settings", "settings.dat");
                        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
                        File.WriteAllText(settings, "partial deployment defaults");
                    }
                    return error == null;
                }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            File.WriteAllText(Settings, "synthetic settings");
        }

        public void Dispose() => Directory.Delete(root, true);
    }
}
