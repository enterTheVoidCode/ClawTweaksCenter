using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawTweaksCenter.Library;

namespace ClawTweaksCenter.Tests;

internal static class SteamGridDbTests
{
    private const string SearchResult = """{"success":true,"data":[{"id":1,"name":"Portal"}]}""";
    private const string ArtResult = """{"success":true,"data":[{"url":"https://art.invalid/portal.png"}]}""";
    private const string EmptyResult = """{"success":true,"data":[]}""";

    [RegressionTest]
    private static async Task HttpFailuresAtEveryStageRemainRetryableForCoversAndHeroes()
    {
        foreach (bool hero in new[] { false, true })
        foreach (int stage in new[] { 0, 1, 2 })
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            using var fixture = new Fixture(hero);
            fixture.BeforeFailure(stage);
            fixture.Handler.Reply(new HttpResponseMessage(status) { Content = new StringContent("temporary error") });
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null, $"{(hero ? "Hero" : "Cover")} cached {status} at stage {stage} as absence.");
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task MalformedAndFailedApiResponsesRemainRetryable()
    {
        foreach (bool hero in new[] { false, true })
        foreach (int stage in new[] { 0, 1 })
        foreach (string payload in new[] { "{bad json", "{}", "[]", """{"success":false,"data":[]}""", """{"success":true,"data":[{}]}""" })
        {
            using var fixture = new Fixture(hero);
            fixture.BeforeFailure(stage);
            fixture.Handler.Json(payload);
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null, $"Malformed payload became a persistent miss at stage {stage}: {payload}");
        }
    }

    [RegressionTest]
    private static async Task NetworkFailuresRemainRetryable()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            fixture.Handler.Fail((_, _) => throw new HttpRequestException("Synthetic network failure."));
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null);
        }
    }

    [RegressionTest]
    private static async Task EmptyOrInvalidImageBytesRemainRetryable()
    {
        foreach (bool hero in new[] { false, true })
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("an HTML error page") })
        {
            using var fixture = new Fixture(hero);
            fixture.BeforeFailure(2);
            fixture.Handler.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null);
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task ConfirmedAbsenceIsCachedForTheSessionThenRetried()
    {
        foreach (bool hero in new[] { false, true })
        foreach (int stage in new[] { 0, 1 })
        {
            using var fixture = new Fixture(hero);
            fixture.BeforeFailure(stage);
            fixture.Handler.Json(EmptyResult);
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            int calls = fixture.Handler.Calls;
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            AssertEx.Equal(calls, fixture.Handler.Calls, "Confirmed absence spent quota again in the same session.");
            fixture.NewSession();
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null, "Confirmed absence survived into a new session forever.");
        }
    }

    [RegressionTest]
    private static async Task LegacyEmptyDiskEntriesAreRetriedWithoutLosingPositiveEntries()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            File.WriteAllText(fixture.IndexPath, """{"portal":"","othergame":"other.png"}""");
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null, "A legacy error entry still blocks artwork forever.");
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(fixture.IndexPath))!;
            AssertEx.Equal("other.png", saved["othergame"]);
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task SuccessfulImagesAreReusedAcrossSessions()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            fixture.Success();
            string? path = await fixture.Fetch();
            AssertEx.True(path != null);
            AssertEx.Equal(path, await fixture.Fetch());
            fixture.NewSession();
            AssertEx.Equal(path, await fixture.Fetch());
            AssertEx.Equal(3, fixture.Handler.Calls);
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task MissingPreviouslyCachedImagesAreDownloadedAgain()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            fixture.Success();
            string? path = await fixture.Fetch();
            AssertEx.True(path != null);
            File.Delete(path!);
            fixture.Success();
            AssertEx.Equal(path, await fixture.Fetch());
            AssertEx.Equal(6, fixture.Handler.Calls);
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task CoverSweepSkipsExistingArtAndReusesNormalizedTitleResults()
    {
        using var fixture = new Fixture(hero: false);
        fixture.Success();
        var games = new[]
        {
            new GameEntry { Title = "An existing cover", ArtPath = "existing-art.png" },
            new GameEntry { Title = "Portal" },
            new GameEntry { Title = "PORTAL" },
            new GameEntry { Title = "" }
        };
        int progress = 0;
        await fixture.FetchCovers(games, () => progress++);
        AssertEx.Equal("existing-art.png", games[0].ArtPath);
        AssertEx.True(games[1].ArtPath != null);
        AssertEx.Equal(games[1].ArtPath, games[2].ArtPath);
        AssertEx.Equal(3, fixture.Handler.Calls);
        AssertEx.True(progress > 0);
    }

    [RegressionTest]
    private static async Task CancellationDoesNotPoisonTheCache()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            using var cancellation = new CancellationTokenSource();
            fixture.Handler.Fail((_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation did not reach the request.");
            });
            if (hero) AssertEx.Equal<string?>(null, await fixture.Fetch(cancellation.Token));
            else await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await fixture.Fetch(cancellation.Token));
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null);
            fixture.AssertPersistedImage();
        }
    }

    [RegressionTest]
    private static async Task DiskWriteFailureDoesNotPoisonTheCache()
    {
        foreach (bool hero in new[] { false, true })
        {
            using var fixture = new Fixture(hero);
            string conflict = Path.Combine(fixture.CacheDir, hero ? "hero_portal.png" : "portal.png");
            Directory.CreateDirectory(conflict);
            fixture.Success();
            AssertEx.Equal<string?>(null, await fixture.Fetch());
            Directory.Delete(conflict);
            fixture.Success();
            AssertEx.True(await fixture.Fetch() != null);
            fixture.AssertPersistedImage();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> _responses = new();
        public int Calls { get; private set; }
        public void Reply(HttpResponseMessage response) => _responses.Enqueue((_, _) => response);
        public void Json(string value) => Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) });
        public void Fail(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) => _responses.Enqueue(response);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request: " + request.RequestUri);
            return Task.FromResult(_responses.Dequeue()(request, cancellationToken));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _hero;
        private readonly HttpClient _client;
        private SteamGridDbCache _cache;
        public string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "SteamGridDbTests-" + Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(CacheDir, _hero ? "heroindex.json" : "index.json");
        public Handler Handler { get; } = new();
        private readonly byte[] _image;

        public Fixture(bool hero)
        {
            _hero = hero;
            Directory.CreateDirectory(CacheDir);
            _client = new HttpClient(Handler);
            _cache = new SteamGridDbCache(_client, CacheDir);
            using var image = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4)));
            encoder.Save(image);
            _image = image.ToArray();
        }

        public void NewSession() => _cache = new SteamGridDbCache(_client, CacheDir);
        public void BeforeFailure(int stage)
        {
            if (stage >= 1) Handler.Json(SearchResult);
            if (stage >= 2) Handler.Json(ArtResult);
        }
        public void Success()
        {
            Handler.Json(SearchResult);
            Handler.Json(ArtResult);
            Handler.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_image) });
        }
        public async Task<string?> Fetch(CancellationToken ct = default)
        {
            var game = new GameEntry { Title = "Portal" };
            if (_hero) return await _cache.EnsureHeroAsync(game, "synthetic-test-key", ct);
            await _cache.FetchMissingAsync(new[] { game }, "synthetic-test-key", ct, null);
            return game.ArtPath;
        }
        public Task FetchCovers(IReadOnlyList<GameEntry> games, Action progress)
            => _cache.FetchMissingAsync(games, "synthetic-test-key", CancellationToken.None, progress);
        public void AssertPersistedImage()
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath))!;
            AssertEx.True(saved["portal"].Length > 0);
            AssertEx.SequenceEqual(_image, File.ReadAllBytes(Path.Combine(CacheDir, saved["portal"])));
        }
        public void Dispose()
        {
            _client.Dispose();
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true);
        }
    }
}
