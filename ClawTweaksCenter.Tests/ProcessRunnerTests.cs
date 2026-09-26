using System.Diagnostics;
using System.IO;
using System.Text;
using ClawTweaksCenter.Core;

namespace ClawTweaksCenter.Tests;

internal static class ProcessRunnerTests
{
    [RegressionTest]
    private static void TimeoutIncludesReadingFromASilentLiveProcess()
    {
        var watch = Stopwatch.StartNew();
        var result = ProcessRunner.Run(Probe("process-sleep"), 400);
        AssertEx.True(result.TimedOut, "Pipe reads bypassed the process timeout.");
        AssertEx.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Timeout cleanup exceeded the deadline allowance.");
    }

    [RegressionTest]
    private static void LargeStdoutAndStderrDrainConcurrently()
    {
        var result = ProcessRunner.Run(Probe("process-fill-pipes"), 5000);
        AssertEx.False(result.TimedOut);
        AssertEx.Equal(0, result.ExitCode, "Child could not drain both pipes before its watchdog killed it.");
        AssertEx.Equal(512 * 1024, result.StandardOutput.Count(c => c == 'o'));
        AssertEx.Equal(512 * 1024, result.StandardError.Count(c => c == 'e'));
    }

    [RegressionTest]
    private static void TimeoutAlsoCoversDescendantHoldingPipesAfterParentExit()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "ClawTweaksProcessTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var watch = Stopwatch.StartNew();
            var result = ProcessRunner.Run(Probe("process-spawn-pipe-holder", pidFile), 700);
            AssertEx.True(File.Exists(pidFile), "Parent did not start the descendant fixture.");
            AssertEx.True(result.TimedOut, "Parent exit must not bypass the stream-read deadline.");
            AssertEx.True(watch.Elapsed < TimeSpan.FromSeconds(2), "An inherited pipe blocked cleanup.");
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(2000);
                }
                catch (ArgumentException) { }
                File.Delete(pidFile);
            }
        }
    }

    [RegressionTest]
    private static void TimeoutTerminatesTheLiveProcessTree()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "ClawTweaksProcessTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = ProcessRunner.Run(Probe("process-live-tree", pidFile), 700);
            AssertEx.True(result.TimedOut);
            AssertEx.True(File.Exists(pidFile), "Parent did not start the descendant fixture.");
            foreach (var pid in File.ReadAllLines(pidFile).Select(int.Parse))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    AssertEx.True(process.WaitForExit(1000), "Timeout left a process in the live tree running.");
                }
                catch (ArgumentException) { } // The terminated process has already been reaped.
            }
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                foreach (var pid in File.ReadAllLines(pidFile).Select(int.Parse))
                {
                    try { using var process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                    catch (ArgumentException) { }
                }
                File.Delete(pidFile);
            }
        }
    }

    [RegressionTest]
    private static void EncodingsTextAndNonzeroExitArePreserved()
    {
        var startInfo = Probe("process-unicode");
        startInfo.StandardOutputEncoding = Encoding.Unicode;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        var result = ProcessRunner.Run(startInfo, 5000);
        AssertEx.False(result.TimedOut);
        AssertEx.Equal(23, result.ExitCode);
        AssertEx.Equal("caffè 日本語", result.StandardOutput);
        AssertEx.Equal("échec Ελληνικά", result.StandardError);
    }

    private static ProcessStartInfo Probe(string name, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Path.ChangeExtension(typeof(ProcessRunnerTests).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--probe");
        startInfo.ArgumentList.Add(name);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    [RegressionProbe("process-sleep")]
    private static void Sleep() => Thread.Sleep(4000);

    [RegressionProbe("process-fill-pipes")]
    private static void FillPipes()
    {
        // Prevent a broken runner from hanging the regression suite itself.
        using var watchdog = new Timer(_ => Process.GetCurrentProcess().Kill(), null, 3000, Timeout.Infinite);
        var stderr = Task.Run(() => Console.Error.Write(new string('e', 512 * 1024)));
        Console.Out.Write(new string('o', 512 * 1024));
        stderr.GetAwaiter().GetResult();
    }

    [RegressionProbe("process-spawn-pipe-holder")]
    private static void SpawnPipeHolder(string[] args)
    {
        var startInfo = Probe("process-sleep");
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        using var child = Process.Start(startInfo)!;
        File.WriteAllText(args[0], child.Id.ToString());
    }

    [RegressionProbe("process-live-tree")]
    private static void SpawnLiveTree(string[] args)
    {
        var startInfo = Probe("process-sleep");
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        using var child = Process.Start(startInfo)!;
        File.WriteAllLines(args[0], [Environment.ProcessId.ToString(), child.Id.ToString()]);
        Thread.Sleep(4000);
    }

    [RegressionProbe("process-unicode")]
    private static void Unicode()
    {
        using (var output = Console.OpenStandardOutput())
            output.Write(Encoding.Unicode.GetBytes("caffè 日本語"));
        using (var error = Console.OpenStandardError())
            error.Write(Encoding.UTF8.GetBytes("échec Ελληνικά"));
        Environment.Exit(23);
    }
}
