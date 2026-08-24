using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Lets a second launch of Center find the one already running and wake it, instead of opening a
    /// second window on top of it.
    ///
    /// TWO PRIMITIVES, each doing the one thing it is good at. A named Mutex answers "is anyone else
    /// already running" - instantly, no race, no listener required on the other end. A named pipe
    /// carries the one thing a Mutex cannot: WHAT the second launch actually wanted (just come to the
    /// front, or come to the front already on the library) - see App.xaml.cs for the client side and
    /// CenterMenuWindow.Tray.cs for the server side that listens on this for as long as Center is
    /// running in the background.
    ///
    /// A SEPARATE pipe from Core.HelperPipeClient's connection to the ClawTweaks helper - that one is
    /// Center-to-Helper, this one is Center-to-Center, and mixing the two would mean the helper's
    /// single-server-instance pipe (see the CLAUDE.md note on ClawTweaksCenter's pipe) has to somehow
    /// also speak this protocol.
    /// </summary>
    internal static class CenterInstanceSignal
    {
        private const string MutexName = "Local\\ClawTweaksCenter.SingleInstance";
        private const string PipeName = "ClawTweaksCenter.Wake";

        // Show the window as it is.
        public const string CommandShow = "show";
        // Show the window AND land on the library, same as launching with --library.
        public const string CommandShowLibrary = "library";

        /// <summary>
        /// Claims the single-instance mutex. Returns the Mutex the caller must keep referenced for as
        /// long as Center runs (a Mutex with no live reference can be finalized and silently release
        /// its claim - see the standard .NET single-instance-app pitfall) when this IS the only
        /// instance; returns null when another instance already holds it.
        /// </summary>
        public static Mutex TryClaim()
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew) return mutex;
            mutex.Dispose();
            return null;
        }

        /// <summary>
        /// Tells whichever instance holds the mutex what this launch wanted, then returns whether that
        /// reached it. A short timeout, not a retry loop: if the resident instance is not answering its
        /// own pipe, something is already wrong with it, and hanging this brand-new process waiting for
        /// a reply is worse than just giving up - the mutex proved a process exists; the user can still
        /// find its window or tray icon by hand.
        /// </summary>
        public static bool SignalRunningInstance(string command)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(command);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Runs for as long as Center does, accepting one connection at a time and dispatching the
        /// single line it wrote. <paramref name="onShow"/> and <paramref name="onShowLibrary"/> are
        /// invoked on a background thread - the caller marshals to the UI thread itself, the same way
        /// every other background-to-UI hop in this app already does.
        /// </summary>
        public static void StartListening(CancellationToken ct, Action onShow, Action onShowLibrary)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                        using var reader = new StreamReader(server);
                        string command = await reader.ReadLineAsync().ConfigureAwait(false);

                        if (string.Equals(command, CommandShowLibrary, StringComparison.OrdinalIgnoreCase))
                            onShowLibrary?.Invoke();
                        else
                            onShow?.Invoke();
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        InstallLog.Write("Instance-wake pipe iteration failed: " + ex.Message);
                        // A malformed connection attempt must not kill the listener - the whole point
                        // is that this outlives every individual wake request.
                        try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
                    }
                }
            }, ct);
        }
    }
}
