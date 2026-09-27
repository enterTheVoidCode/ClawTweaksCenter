using ClawTweaksCenter.Library;
using System.IO;
using System.Reflection;

namespace ClawTweaksCenter.Tests;

internal static class SteamDownloadTests
{
    [RegressionTest]
    private static void PausedManifestStopsShowingAnActiveDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "claw-steam-download-" + Guid.NewGuid().ToString("N"));
        string apps = Path.Combine(root, "steamapps");
        Directory.CreateDirectory(apps);
        string manifest = Path.Combine(apps, "appmanifest_12345.acf");
        try
        {
            WriteManifest(manifest, 1282); // Required + Running + Started.
            var running = ReadManifest(manifest, apps);
            AssertEx.True(running.Downloading);

            WriteManifest(manifest, 1034); // Required + Queued + Started; no Running.
            var paused = ReadManifest(manifest, apps);
            AssertEx.False(paused.Downloading, "A suspended Steam transfer is not actively downloading.");
            AssertEx.False(paused.Installed);
            AssertEx.Equal(SteamDownloadStatus.Paused, paused.DownloadStatus);

            WriteManifest(manifest, 2); // Required but not started yet.
            AssertEx.Equal(SteamDownloadStatus.Queued, ReadManifest(manifest, apps).DownloadStatus);

            WriteManifest(manifest, 1538); // Required + Paused + Started.
            AssertEx.Equal(SteamDownloadStatus.Paused, ReadManifest(manifest, apps).DownloadStatus);

            WriteManifest(manifest, 1282);
            AssertEx.Equal(SteamDownloadStatus.Downloading, ReadManifest(manifest, apps).DownloadStatus);

            WriteManifest(manifest, 4);
            Directory.CreateDirectory(Path.Combine(apps, "common", "Test game"));
            AssertEx.Equal(SteamDownloadStatus.None, ReadManifest(manifest, apps).DownloadStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [RegressionTest]
    private static void IncompleteSteamGameAppearsOnSteamShelfInsteadOfRecent()
    {
        var library = new GameLibrary();
        var download = new GameEntry
        {
            Id = "12345", Title = "Downloading game", Store = GameStore.Steam,
            Installed = false, DownloadStatus = SteamDownloadStatus.Downloading,
        };
        var paused = new GameEntry
        {
            Id = "23456", Title = "Paused game", Store = GameStore.Steam,
            Installed = false, DownloadStatus = SteamDownloadStatus.Paused,
        };
        var ownedOnly = new GameEntry
        {
            Id = "34567", Title = "Not installed", Store = GameStore.Steam,
            Installed = false,
        };
        typeof(GameLibrary).GetProperty(nameof(GameLibrary.Games))!
            .SetValue(library, new[] { download, paused, ownedOnly });

        AssertEx.True(library.ForGroup(LibraryGroup.Steam).Contains(download));
        AssertEx.True(library.ForGroup(LibraryGroup.Steam).Contains(paused));
        AssertEx.False(library.ForGroup(LibraryGroup.Steam).Contains(ownedOnly));
        AssertEx.False(library.ForGroup(LibraryGroup.Recent).Contains(download));
        AssertEx.False(library.ForGroup(LibraryGroup.Recent).Contains(paused));
    }

    private static GameEntry ReadManifest(string path, string apps)
        => (GameEntry)typeof(SteamSource)
            .GetMethod("ReadManifest", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { path, apps })!;

    private static void WriteManifest(string path, int flags)
        => File.WriteAllText(path, "\"AppState\"\n{\n"
            + "\"appid\" \"12345\"\n\"name\" \"Test game\"\n"
            + "\"installdir\" \"Test game\"\n\"StateFlags\" \"" + flags + "\"\n}");
}
