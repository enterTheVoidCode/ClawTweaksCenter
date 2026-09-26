using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawTweaksCenter.Library;

namespace ClawTweaksCenter.Tests;

internal static class GameArtTests
{
    [RegressionTest]
    private static async Task MissingLocalCoverCanBeLoadedAfterCreation()
    {
        using var fixture = new ImageFixture();
        AssertEx.Equal<BitmapSource?>(null, await GameArt.LoadAsync(fixture.Path, 32));
        fixture.WriteValidImage();
        var repaired = await GameArt.LoadAsync(fixture.Path, 32);
        AssertEx.True(repaired != null, "The missing image poisoned the cache after the file appeared.");
        AssertEx.True(repaired!.IsFrozen);
    }

    [RegressionTest]
    private static async Task CorruptLocalCoverCanBeLoadedAfterRepair()
    {
        using var fixture = new ImageFixture();
        File.WriteAllText(fixture.Path, "not a PNG");
        var failures = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => GameArt.LoadAsync(fixture.Path, 48)));
        AssertEx.True(failures.All(x => x == null));
        fixture.WriteValidImage();
        var repaired = await GameArt.LoadAsync(fixture.Path, 48);
        AssertEx.True(repaired != null, "A corrupt image stayed cached after a valid replacement.");
        AssertEx.True(ReferenceEquals(repaired, await GameArt.LoadAsync(fixture.Path, 48)), "Successful images should stay cached.");
    }

    private sealed class ImageFixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctw-art-test-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "cover.png");
        internal ImageFixture() => Directory.CreateDirectory(root);
        internal void WriteValidImage()
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path);
            encoder.Save(file);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
