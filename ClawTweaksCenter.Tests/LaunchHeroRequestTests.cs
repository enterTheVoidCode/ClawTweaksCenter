using ClawTweaksCenter.Library;

namespace ClawTweaksCenter.Tests;

internal static class LaunchHeroRequestTests
{
    [RegressionTest]
    private static async Task NewPromptRetriesAfterThePreviousRequestReturnedNoArt()
    {
        var game = new GameEntry { Title = "Synthetic game" };
        int calls = 0;
        var request = new LaunchHeroRequest(_ => Task.FromResult(++calls == 1 ? null! : "hero.png"));
        request.BeginPrompt(game);
        AssertEx.Equal<string?>(null, await request.GetAsync(game));
        AssertEx.Equal<string?>(null, await request.GetAsync(game));
        AssertEx.Equal(1, calls, "Repeated renders in one prompt must reuse its result.");

        request.BeginPrompt(game);

        AssertEx.Equal("hero.png", await request.GetAsync(game), "Reopening the prompt must retry a completed empty result.");
        AssertEx.Equal(2, calls);
    }

    [RegressionTest]
    private static async Task NewPromptRetriesAfterThePreviousRequestFaulted()
    {
        var game = new GameEntry { Title = "Synthetic game" };
        int calls = 0;
        var request = new LaunchHeroRequest(_ => ++calls == 1
            ? Task.FromException<string>(new InvalidOperationException("Synthetic fetch failure"))
            : Task.FromResult("hero.png"));
        request.BeginPrompt(game);
        await AssertEx.ThrowsAsync<InvalidOperationException>(async () => await request.GetAsync(game));

        request.BeginPrompt(game);

        AssertEx.Equal("hero.png", await request.GetAsync(game));
        AssertEx.Equal(2, calls);
    }

    [RegressionTest]
    private static async Task NewPromptRetriesAfterThePreviousRequestWasCanceled()
    {
        var game = new GameEntry { Title = "Synthetic game" };
        int calls = 0;
        var request = new LaunchHeroRequest(_ => ++calls == 1
            ? Task.FromCanceled<string>(new CancellationToken(canceled: true))
            : Task.FromResult("hero.png"));
        request.BeginPrompt(game);
        await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await request.GetAsync(game));

        request.BeginPrompt(game);

        AssertEx.Equal("hero.png", await request.GetAsync(game));
        AssertEx.Equal(2, calls);
    }

    [RegressionTest]
    private static async Task ActiveRequestIsSharedByRendersAndReopenedPrompts()
    {
        var game = new GameEntry { Title = "Synthetic game" };
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var request = new LaunchHeroRequest(_ => { calls++; return pending.Task; });
        request.BeginPrompt(game);
        var confirmation = request.GetAsync(game);
        var running = request.GetAsync(game);
        request.BeginPrompt(game);
        var reopened = request.GetAsync(game);

        AssertEx.True(ReferenceEquals(confirmation, running));
        AssertEx.True(ReferenceEquals(confirmation, reopened));
        AssertEx.Equal(1, calls, "Reopening while the download is active must not start another one.");
        pending.SetResult("hero.png");
        AssertEx.Equal("hero.png", await confirmation);
        AssertEx.Equal("hero.png", await reopened);
    }

    [RegressionTest]
    private static async Task SuccessfulRequestIsReusedByLaterPrompts()
    {
        var game = new GameEntry { Title = "Synthetic game" };
        int calls = 0;
        var request = new LaunchHeroRequest(_ => { calls++; return Task.FromResult("hero.png"); });
        request.BeginPrompt(game);
        AssertEx.Equal("hero.png", await request.GetAsync(game));
        request.BeginPrompt(game);
        AssertEx.Equal("hero.png", await request.GetAsync(game));
        AssertEx.Equal(1, calls);
    }

    [RegressionTest]
    private static async Task SelectingAnotherGameStartsItsOwnRequest()
    {
        var first = new GameEntry { Id = "first" };
        var second = new GameEntry { Id = "second" };
        var request = new LaunchHeroRequest(game => Task.FromResult(game.Id + ".png"));
        request.BeginPrompt(first);
        AssertEx.Equal("first.png", await request.GetAsync(first));
        request.BeginPrompt(second);
        AssertEx.Equal("second.png", await request.GetAsync(second));
    }
}
