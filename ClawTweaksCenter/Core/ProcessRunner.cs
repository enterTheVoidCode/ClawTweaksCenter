using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Core
{
    internal sealed record ProcessResult(string StandardOutput, string StandardError, int ExitCode, bool TimedOut);

    internal static class ProcessRunner
    {
        internal static ProcessResult Run(ProcessStartInfo startInfo, int timeoutMs)
            => RunAsync(startInfo, timeoutMs).GetAwaiter().GetResult();

        private static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            // StartInfo retains each caller's executable, arguments and stream encodings.
            using var deadline = new CancellationTokenSource(timeoutMs);
            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            var finished = Task.WhenAll(output, error, exited);
            bool completed = false;
            try
            {
                // Exit alone is insufficient: a descendant can inherit the pipes and keep them
                // open after the process exits. The same deadline covers both drains and exit.
                await finished.WaitAsync(deadline.Token).ConfigureAwait(false);
                completed = true;
                return new ProcessResult(output.Result, error.Result, process.ExitCode, false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return new ProcessResult(output.IsCompletedSuccessfully ? output.Result : "",
                                         error.IsCompletedSuccessfully ? error.Result : "", -1, true);
            }
            finally
            {
                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    deadline.Cancel();
                    // No unbounded wait in cleanup. A root that has already exited cannot identify
                    // its orphaned descendants for Kill; release our pipe handles in that case too.
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                    _ = finished.ContinueWith(task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }
}
