using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shared.Enums;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Client for the Helper's second Named Pipe ("ClawTweaksCenter", see
    /// XboxGamingBarHelper/Program.cs InitializeConnection). Speaks the exact same line-delimited-JSON
    /// wire protocol the widget uses on its own pipe ({"RequestId":0,"Command":1,"Function":N,
    /// "Content":"..."}), so property writes go through the Helper's existing generic property
    /// dispatch (properties.HandlePipeMessage) — no Helper logic is duplicated here.
    ///
    /// Deliberately does NOT reference Shared.IPC.PipeMessage: that type's ToValueSet()/FromValueSet()
    /// pull in Windows.Foundation.Collections.ValueSet (WinRT), which Shared.csproj's own comment warns
    /// must never flow into a modern SDK-style consumer like this one (CoreClrInitFailure at startup).
    /// Only the plain Function/Command enums are used here — those are safe, same as DeviceInfo/
    /// DeviceType already are elsewhere in this project.
    /// </summary>
    public sealed class HelperPipeClient : IDisposable
    {
        private const string PipeName = "ClawTweaksCenter";

        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly object _writeLock = new object();
        private CancellationTokenSource _cts;
        private Task _readLoop;

        private readonly object _valuesLock = new object();
        private readonly Dictionary<Function, string> _lastKnownValues = new Dictionary<Function, string>();

        // Reconnect/verification state. Right after an in-app update the pipe-serving helper is being
        // SWAPPED (the old keeper still holds the Global single-instance mutex, so the freshly
        // task-launched keeper exits immediately until the old one is killed and the new one reaches
        // its early InitializeConnection). A one-shot connect can therefore bind to a helper that is
        // about to exit — or miss the gap entirely — and then sit "connected" to nothing. So: verify
        // every bind with a real status push (proof the server is alive), and auto-reconnect on drop.
        private volatile bool _disposed;
        private volatile bool _keepConnected;
        private Action<string> _log;
        private TaskCompletionSource<bool> _firstPushTcs;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr Pipe, out uint ServerProcessId);

        // Diagnostics land in a file the user can retrieve after a test run — the onboarding UI's log
        // callback discards the message text, so this is the only durable trace of the connect path.
        private static readonly string DiagLogPath = BuildDiagLogPath();
        private static string BuildDiagLogPath()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "center_onboarding.log");
            }
            catch { return null; }
        }
        private void Diag(string msg, Action<string> extern_ = null)
        {
            extern_?.Invoke(msg);
            _log?.Invoke(msg);
            try { if (DiagLogPath != null) File.AppendAllText(DiagLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); } catch { }
        }

        public bool IsConnected => _pipe?.IsConnected == true;

        /// <summary>Raised whenever the helper pushes a property value (our own writes echo back too).</summary>
        public event Action<Function, string> PropertyUpdated;

        /// <summary>Connects with retries and VERIFIES the server is live (answers with a status push)
        /// before returning true — a bare pipe bind to a dying/mutex-losing instance no longer counts.
        /// Enables auto-reconnect for the life of this client so an update-time helper swap self-heals.
        /// Returns false only if no live helper answered within <paramref name="timeout"/>.</summary>
        public async Task<bool> ConnectAsync(TimeSpan timeout, Action<string> log = null)
        {
            _log = log;
            _keepConnected = true;
            var deadline = DateTime.UtcNow + timeout;
            int attempt = 0;
            Diag($"Connect requested (timeout {timeout.TotalSeconds:0}s).");
            while (!_disposed && DateTime.UtcNow < deadline)
            {
                attempt++;
                if (await TryConnectVerifiedAsync(attempt).ConfigureAwait(false)) return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            Diag("Timed out waiting for a LIVE helper on the ClawTweaksCenter pipe.");
            return false;
        }

        /// <summary>One bind + liveness check: bind the pipe, log which server PID actually owns it
        /// (directly reveals an update-time PID swap), then require at least one status push within a
        /// few seconds as proof of a live keeper. A silent bind is torn down so the caller retries.</summary>
        private async Task<bool> TryConnectVerifiedAsync(int attempt)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1500).ConfigureAwait(false);

                uint serverPid = 0;
                try { GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out serverPid); } catch { }

                _pipe = pipe;
                _reader = new StreamReader(_pipe, Encoding.UTF8);
                _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = false };
                _cts = new CancellationTokenSource();
                var firstPush = new TaskCompletionSource<bool>();
                _firstPushTcs = firstPush;
                _readLoop = Task.Run(() => ReadLoop(_cts.Token));

                Diag($"Pipe bound (attempt {attempt}, server PID {serverPid}). Verifying live helper…");

                RequestStatusRefresh(); // live keeper answers via PushCenterStatusSnapshot
                var done = await Task.WhenAny(firstPush.Task, Task.Delay(4000)).ConfigureAwait(false);
                if (done == firstPush.Task)
                {
                    Diag($"Connected to LIVE helper (server PID {serverPid}).");
                    return true;
                }

                Diag($"Bound but server PID {serverPid} sent no status in 4s — treating as not-live (dying/swapped instance), retrying.");
                TeardownPipe();
                return false;
            }
            catch (Exception ex)
            {
                Diag($"Connect attempt {attempt} failed: {ex.GetType().Name} ({ex.Message}).");
                TeardownPipe();
                return false;
            }
        }

        private void TeardownPipe()
        {
            try { _cts?.Cancel(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private void ReadLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _pipe.IsConnected)
                {
                    string line = _reader.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (TryParseFunctionContent(line, out var function, out var content))
                    {
                        lock (_valuesLock) { _lastKnownValues[function] = content; }
                        _firstPushTcs?.TrySetResult(true); // liveness proof for TryConnectVerifiedAsync
                        PropertyUpdated?.Invoke(function, content);
                    }
                }
            }
            catch (IOException) { /* normal on disconnect */ }
            catch (ObjectDisposedException) { /* normal on dispose */ }
            finally
            {
                // A genuine drop (helper exited/was swapped) — NOT our own intentional teardown
                // (which cancels ct) — kicks off a background reconnect so an update-time swap heals.
                if (_keepConnected && !_disposed && !ct.IsCancellationRequested)
                {
                    Diag("Helper pipe dropped — reconnecting (helper may have been swapped by an update).");
                    _ = Task.Run(ReconnectLoopAsync);
                }
            }
        }

        private async Task ReconnectLoopAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            int attempt = 0;
            while (_keepConnected && !_disposed && DateTime.UtcNow < deadline)
            {
                attempt++;
                if (await TryConnectVerifiedAsync(attempt).ConfigureAwait(false))
                {
                    Diag("Reconnected to helper after drop.");
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            Diag("Reconnect window elapsed without a live helper.");
        }

        /// <summary>Same hand-rolled extraction the widget's own PipeClient.cs uses — no JSON library.</summary>
        private static bool TryParseFunctionContent(string json, out Function function, out string content)
        {
            function = Function.None;
            content = null;

            var funcMatch = Regex.Match(json, "\"Function\"\\s*:\\s*(-?\\d+)");
            if (!funcMatch.Success) return false;
            if (!int.TryParse(funcMatch.Groups[1].Value, out int funcInt)) return false;
            function = (Function)funcInt;

            var contentMatch = Regex.Match(json, "\"Content\"\\s*:\\s*\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"");
            if (contentMatch.Success) content = UnescapeJson(contentMatch.Groups[1].Value);
            return true;
        }

        private static string UnescapeJson(string s) =>
            s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");

        /// <summary>Fire-and-forget property write — RequestId stays 0 (async push, no correlated ack
        /// expected back). The helper's own value-change broadcast is what confirms it landed.</summary>
        public bool SetProperty(Function function, object value)
        {
            if (!IsConnected) return false;
            string content = value is bool b ? (b ? "true" : "false") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            string json = $"{{\"RequestId\":0,\"Command\":{(int)Command.Set},\"Function\":{(int)function},\"Content\":\"{content}\"}}";

            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine(json);
                    _writer.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryGetLastKnownValue(Function function, out string content)
        {
            lock (_valuesLock) return _lastKnownValues.TryGetValue(function, out content);
        }

        /// <summary>
        /// Sends a request carrying a single Extra key (the helper's PipeServer_MessageReceived dispatches
        /// on Extra keys — FactoryReset / BackupCreate / BackupRestore) and waits for the helper's
        /// correlated Response push on <paramref name="resultFunction"/>. Returns the compact "key=value;…"
        /// result content, or null on timeout / not-connected. This is the Reset/Backup/Restore transport:
        /// the reply comes back on THIS (Center) pipe only, as a Function+Content push the read loop
        /// already parses — no RequestId/ack plumbing needed on the Center side.
        /// </summary>
        public async Task<string> RequestWithResultAsync(string extraKey, object extraValue, Function resultFunction, TimeSpan timeout)
        {
            if (!IsConnected) return null;

            var tcs = new TaskCompletionSource<string>();
            void Handler(Function f, string c) { if (f == resultFunction) tcs.TrySetResult(c); }
            PropertyUpdated += Handler;
            try
            {
                // Extra value: bool → true/false literal; anything else → a JSON string (paths carry
                // backslashes/quotes, so they MUST be JSON-escaped or the helper mis-parses the message).
                string extraJson = extraValue is bool bVal
                    ? (bVal ? "true" : "false")
                    : "\"" + EscapeJson(Convert.ToString(extraValue, System.Globalization.CultureInfo.InvariantCulture)) + "\"";
                string json = $"{{\"RequestId\":0,\"Command\":0,\"Function\":0,\"{extraKey}\":{extraJson}}}";

                lock (_writeLock)
                {
                    _writer.WriteLine(json);
                    _writer.Flush();
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == tcs.Task ? tcs.Task.Result : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                PropertyUpdated -= Handler;
            }
        }

        /// <summary>
        /// Sends a single Extra-key request and does NOT wait for anything.
        ///
        /// Some of the helper's verbs answer with a correlated Function push (that is
        /// <see cref="RequestWithResultAsync"/>); others answer with a plain pipe ack addressed to a
        /// RequestId, which this client does not track. MsiChargeLimit and MsiFanControl are the
        /// second kind. Returning "it went out on the wire" is therefore the strongest true statement
        /// available here, and the caller is expected to report it as sent rather than as verified.
        /// </summary>
        public bool SendRequest(string extraKey, object extraValue)
        {
            if (!IsConnected) return false;
            try
            {
                string extraJson = extraValue is bool bVal
                    ? (bVal ? "true" : "false")
                    : (extraValue is int || extraValue is long
                        ? Convert.ToString(extraValue, System.Globalization.CultureInfo.InvariantCulture)
                        : "\"" + EscapeJson(Convert.ToString(extraValue, System.Globalization.CultureInfo.InvariantCulture)) + "\"");

                lock (_writeLock)
                {
                    _writer.WriteLine($"{{\"RequestId\":0,\"Command\":0,\"Function\":0,\"{extraKey}\":{extraJson}}}");
                    _writer.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string EscapeJson(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        /// <summary>
        /// Asks the helper to push its current state for the properties onboarding cares about (see
        /// Program.PipeHandlers.cs PushCenterStatusSnapshot — replies only on this pipe, never the
        /// widget's). Fire-and-forget; caller awaits the resulting PropertyUpdated pushes separately.
        /// </summary>
        public bool RequestStatusRefresh()
        {
            if (!IsConnected) return false;
            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine("{\"RequestId\":0,\"Command\":0,\"Function\":0,\"CenterRequestStatus\":true}");
                    _writer.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the helper to run the physical-controller health probe (open the Claw command HID +
        /// read-only round-trip) and waits for the resulting ControllerHwHealth push. Returns the raw
        /// "key=value;…" payload, or null on timeout. Onboarding step 0 parses this to gate the chain.
        /// </summary>
        public async Task<string> RequestControllerHealthAsync(TimeSpan timeout)
        {
            if (!IsConnected) return null;
            var tcs = new TaskCompletionSource<string>();
            void Handler(Function f, string c)
            {
                if (f == Function.ControllerHwHealth) tcs.TrySetResult(c);
            }

            PropertyUpdated += Handler;
            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine("{\"RequestId\":0,\"Command\":0,\"Function\":0,\"CenterRequestHealth\":true}");
                    _writer.Flush();
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == tcs.Task ? tcs.Task.Result : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                PropertyUpdated -= Handler;
            }
        }

        /// <summary>
        /// Asks the helper whether a VIRTUAL pad is coming up and whether it is usable yet. Returns
        /// the raw "virtual=..;ready=..;failed=.." payload, or null when the helper does not answer.
        ///
        /// A plain Get, not one of the Extra-key requests next door: ControllerReadyState is a real
        /// Function the helper answers in HandlePipePropertyRequest, and it is cheap - three values
        /// already in memory. The library asks once when it opens; the helper pushes the same payload
        /// again on its own when the answer changes, so nothing here polls.
        /// </summary>
        public async Task<string> RequestControllerReadyStateAsync(TimeSpan timeout)
        {
            if (!IsConnected) return null;

            var tcs = new TaskCompletionSource<string>();
            void Handler(Function f, string c) { if (f == Function.ControllerReadyState) tcs.TrySetResult(c); }
            PropertyUpdated += Handler;
            try
            {
                string json = $"{{\"RequestId\":0,\"Command\":{(int)Command.Get},\"Function\":{(int)Function.ControllerReadyState}}}";
                lock (_writeLock)
                {
                    _writer.WriteLine(json);
                    _writer.Flush();
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == tcs.Task ? tcs.Task.Result : null;
            }
            catch (Exception) { return null; }
            finally { PropertyUpdated -= Handler; }
        }

        /// <summary>
        /// Sets a property, then waits (via the PropertyUpdated push, not polling a request/response)
        /// until the helper confirms the expected value — or times out. Used for onboarding steps that
        /// must not proceed until the previous one is actually done (e.g. Center M fully disabled).
        /// </summary>
        public async Task<bool> SetAndWaitForConfirmationAsync(Function function, object value, string expectedContent, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>();
            void Handler(Function f, string c)
            {
                if (f == function && string.Equals(c, expectedContent, StringComparison.OrdinalIgnoreCase))
                    tcs.TrySetResult(true);
            }

            PropertyUpdated += Handler;
            try
            {
                // Already at the target value? Nothing to wait for.
                if (TryGetLastKnownValue(function, out var current) &&
                    string.Equals(current, expectedContent, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!SetProperty(function, value)) return false;

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == tcs.Task && tcs.Task.Result;
            }
            finally
            {
                PropertyUpdated -= Handler;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _keepConnected = false;
            try { _cts?.Cancel(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
