using ClawTweaksCenter.Navigation;

namespace ClawTweaksCenter.Tests;

internal static class ControllerInputTests
{
    [RegressionTest]
    private static void LosingForegroundDuringDriverPollDiscardsTheSample()
    {
        var input = new InputFixture();
        input.Tick(default);
        input.AfterRead = () => input.Foreground = false;
        input.Tick(AllInputs);
        AssertEx.Equal(0, input.Events.Count, "Input read after switching to the game reached Center.");
    }

    [RegressionTest]
    private static void LosingForegroundBeforeDispatchDropsEveryEventStream()
    {
        var input = new InputFixture { QueueDelivery = true };
        input.Tick(default);
        input.Tick(AllInputs);
        input.Now += TimeSpan.FromMilliseconds(500);
        input.Tick(AllInputs); // Adds held-direction repeats to the other four streams.
        AssertEx.True(input.Pending.Count > 0, "The fixture must defer real decoded input.");
        input.Foreground = false;
        input.Deliver();
        AssertEx.Equal(0, input.Events.Count, "Queued input acted after another window took foreground.");
    }

    [RegressionTest]
    private static void BackgroundInputDoesNotPollTheController()
    {
        var input = new InputFixture { Foreground = false };
        input.Tick(AllInputs);
        AssertEx.Equal(0, input.Reads);
        AssertEx.Equal(0, input.Events.Count);
    }

    [RegressionTest]
    private static void OnlyTheExactNonzeroForegroundWindowAcceptsInput()
    {
        var input = new InputFixture();
        foreach (var foreground in new[] { IntPtr.Zero, new IntPtr(2), new IntPtr(3) })
        {
            input.ForegroundHandle = foreground; // A game, another Center window, or no owner.
            input.Tick(AllInputs);
        }
        AssertEx.Equal(0, input.Reads);
        input.WindowHandle = IntPtr.Zero;
        input.ForegroundHandle = IntPtr.Zero;
        input.Tick(AllInputs);
        AssertEx.Equal(0, input.Reads, "An uninitialized Center HWND must fail closed.");

        input.WindowHandle = new IntPtr(1);
        input.ForegroundHandle = input.WindowHandle;
        input.Tick(default);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Contains("press:A"));
    }

    [RegressionTest]
    private static void ObservedFocusLossInvalidatesQueuedInputEvenAfterFocusReturns()
    {
        var input = new InputFixture { QueueDelivery = true };
        input.Tick(default);
        input.Tick(AllInputs);
        AssertEx.True(input.Pending.Count > 0);
        input.Foreground = false;
        input.Tick(AllInputs);
        input.Foreground = true;
        input.Tick(default);
        input.Deliver();
        AssertEx.Equal(0, input.Events.Count, "Input from a previous foreground session was replayed.");

        input.Tick(AllInputs);
        input.Deliver();
        AssertEx.True(input.Events.Contains("press:A"));
    }

    [RegressionTest]
    private static void HeldGameInputMustBeReleasedAfterReturningToCenter()
    {
        var input = new InputFixture();
        input.Tick(default);
        input.Tick(AllInputs);
        input.Events.Clear();
        input.Foreground = false;
        input.Tick(AllInputs);
        input.Foreground = true;
        input.Tick(AllInputs);
        input.Now += TimeSpan.FromSeconds(3);
        input.Tick(AllInputs);
        AssertEx.Equal(0, input.Events.Count, "Held game controls became a fresh Center action on return.");

        input.Tick(default);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Contains("press:A"));
        AssertEx.True(input.Events.Contains("flick:Up"), "Right-stick edges must also rearm after release.");
        AssertEx.False(input.Events.Any(e => e.StartsWith("repeat:")), "A new press must wait for the repeat delay.");
        input.Now += TimeSpan.FromMilliseconds(400);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Any(e => e.StartsWith("repeat:")));
    }

    [RegressionTest]
    private static void ForegroundInputKeepsEdgesScrollAndRepeatTiming()
    {
        var input = new InputFixture();
        input.Tick(default);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Contains("press:A"));
        AssertEx.True(input.Events.Contains("press:LT"));
        AssertEx.True(input.Events.Contains("flick:Up"));
        AssertEx.True(input.Events.Contains("scroll"));
        AssertEx.True(input.Events.Contains("right-scroll"));
        input.Events.Clear();

        input.Now += TimeSpan.FromMilliseconds(399);
        input.Tick(AllInputs);
        AssertEx.False(input.Events.Any(e => e.StartsWith("press:") || e.StartsWith("flick:") || e.StartsWith("repeat:")));
        input.Events.Clear();
        input.Now += TimeSpan.FromMilliseconds(1);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Contains("repeat:Up"));
        input.Events.Clear();
        input.Now += TimeSpan.FromMilliseconds(159);
        input.Tick(AllInputs);
        AssertEx.False(input.Events.Any(e => e.StartsWith("repeat:")));
        input.Events.Clear();
        input.Now += TimeSpan.FromMilliseconds(1);
        input.Tick(AllInputs);
        AssertEx.True(input.Events.Contains("repeat:Up"));
    }

    private static readonly ControllerState AllInputs = new(0x1001, 16000, 16000, 16000, 16000, 255, 255);

    private sealed class InputFixture
    {
        private readonly ControllerInput input;
        private ControllerState state;
        internal IntPtr WindowHandle = new(1);
        internal IntPtr ForegroundHandle = new(1);
        internal bool Foreground
        {
            get => ControllerInput.IsForegroundWindow(WindowHandle, ForegroundHandle);
            set => ForegroundHandle = value ? WindowHandle : new IntPtr(2);
        }
        internal bool QueueDelivery;
        internal int Reads;
        internal DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal Action? AfterRead;
        internal readonly List<string> Events = [];
        internal readonly List<Action> Pending = [];

        internal InputFixture()
        {
            input = new ControllerInput(() => Foreground, () =>
            {
                Reads++;
                AfterRead?.Invoke();
                return state;
            }, action =>
            {
                if (QueueDelivery) Pending.Add(action);
                else action();
            }, () => Now);
            input.ButtonPressed += button => Events.Add("press:" + button);
            input.ScrollRequested += _ => Events.Add("scroll");
            input.RightStickScrollRequested += _ => Events.Add("right-scroll");
            input.RightStickFlicked += button => Events.Add("flick:" + button);
            input.ButtonRepeated += button => Events.Add("repeat:" + button);
        }

        internal void Tick(ControllerState sample)
        {
            state = sample;
            input.PollOnce();
        }

        internal void Deliver()
        {
            foreach (var action in Pending) action();
            Pending.Clear();
        }
    }
}
