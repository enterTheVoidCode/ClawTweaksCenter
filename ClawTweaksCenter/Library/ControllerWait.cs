using System;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ClawTweaksCenter.Core;
using Shared.Enums;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Holds the library behind a blur while the VIRTUAL controller is still mounting.
    ///
    /// ── The problem, measured ───────────────────────────────────────────────────────────────────
    /// Booting straight into the library puts a fully drawn, fully unresponsive screen in front of
    /// the user. From the helper's own [ClawReady] lines the mount takes 7–10 s, and the whole helper
    /// start is 14–17 s. Nothing on screen says why the sticks do nothing, so it reads as a hang.
    ///
    /// ── Three rules, and each one is a requirement rather than a preference ─────────────────────
    ///
    ///   1. OPT-IN. <see cref="CenterSettings.WaitForVirtualController"/> is off by default. The wait
    ///      is only right for a machine that boots into the library with the virtual pad as its
    ///      standard; anywhere else an overlay between the user and their games is a regression.
    ///
    ///   2. NEVER IN HARDWARE MODE. The helper reports `virtual=0` and there is simply nothing to
    ///      wait for - the physical pad is there. This is why the payload is three flags and not a
    ///      bool: "not ready yet" and "nothing to wait for" are opposite instructions, and a bool
    ///      would have made them the same value.
    ///
    ///   3. ALREADY READY MEANS NO DELAY AT ALL. Not a short overlay, not one frame - a Center opened
    ///      by hand long after boot must behave exactly as it does today. That is why the state is
    ///      READ once as well as subscribed to: the push is the edge, and a consumer that starts
    ///      after the edge would otherwise wait for one that never comes.
    ///
    /// ── And it always ends ─────────────────────────────────────────────────────────────────────
    /// A helper that never answers, a mount that fails, a helper that is not running yet at all -
    /// all of them end the wait. A wait without a ceiling is a hang with an explanation on it, which
    /// is worse than the silence it replaced, so <see cref="MaxWaitMs"/> is a hard stop and the
    /// library is fully usable when it expires. Touch works throughout regardless.
    /// </summary>
    public partial class CenterMenuWindow
    {
        /// <summary>
        /// The ceiling. Generous against the measured 7–10 s mount plus a slow helper start, and
        /// still short enough that a machine where this never resolves is only briefly annoying.
        /// </summary>
        private const int MaxWaitMs = 25000;

        private const double WaitBlurRadius = 18;

        private DispatcherTimer _controllerWaitTimeout;
        private bool _controllerWaitActive;

        /// <summary>Parsed form of the helper's "virtual=..;ready=..;failed=.." payload.</summary>
        private struct ControllerReady
        {
            public bool Virtual;
            public bool Ready;
            public bool Failed;

            /// <summary>
            /// Missing fields read as FALSE, and for `virtual` that is the safe direction: an
            /// unparsable payload then means "no virtual pad", which shows no overlay. Guessing the
            /// other way would blur the library on the strength of a string we did not understand.
            /// </summary>
            public static ControllerReady Parse(string payload)
            {
                var r = new ControllerReady();
                if (string.IsNullOrWhiteSpace(payload)) return r;

                foreach (string part in payload.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = part.Substring(0, eq).Trim();
                    bool on = part.Substring(eq + 1).Trim() == "1";

                    if (key == "virtual") r.Virtual = on;
                    else if (key == "ready") r.Ready = on;
                    else if (key == "failed") r.Failed = on;
                }
                return r;
            }

            /// <summary>The only question the library asks: should it hold?</summary>
            public bool ShouldWait => Virtual && !Ready && !Failed;
        }

        /// <summary>
        /// Called from OpenLibrary. Returns immediately in every case where there is nothing to wait
        /// for, and the ask itself runs off the UI thread - the library draws as it always has, and
        /// the overlay only ever appears ON TOP of a finished screen. Blocking here would trade a
        /// visible problem for an invisible one.
        /// </summary>
        private void BeginControllerWaitIfEnabled()
        {
            if (!CenterSettings.WaitForVirtualController) return;
            if (_controllerWaitActive) return;
            if (_helperPipe == null || !_helperPipe.IsConnected) return;

            // The last value the pipe already saw beats a round trip: if the helper has pushed a
            // ready state at any point this session, the answer is known and the library opens now.
            if (_helperPipe.TryGetLastKnownValue(Function.ControllerReadyState, out string cached)
                && !ControllerReady.Parse(cached).ShouldWait)
                return;

            _helperPipe.PropertyUpdated += OnControllerReadyPushed;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                string payload = null;
                try
                {
                    payload = await _helperPipe.RequestControllerReadyStateAsync(TimeSpan.FromSeconds(3))
                                               .ConfigureAwait(false);
                }
                catch { }

                var state = ControllerReady.Parse(payload);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // No answer at all is NOT "still mounting". The helper may not be up yet, and
                    // holding the library on a question nobody answered is the failure mode this
                    // whole feature is supposed to remove.
                    if (payload == null || !state.ShouldWait) { EndControllerWait(); return; }
                    ShowControllerWait();
                }));
            });
        }

        private void OnControllerReadyPushed(Function function, string content)
        {
            if (function != Function.ControllerReadyState) return;
            var state = ControllerReady.Parse(content);
            if (state.ShouldWait) return;

            Dispatcher.BeginInvoke(new Action(EndControllerWait));
        }

        private void ShowControllerWait()
        {
            if (_controllerWaitActive) return;
            _controllerWaitActive = true;

            try
            {
                if (ControllerWaitText != null) ControllerWaitText.Text = Loc.T("Waiting for the virtual controller to settle\u2026");
                if (ControllerWaitSkipText != null) ControllerWaitSkipText.Text = Loc.T("Skip the wait");
                if (LibraryRoot != null) LibraryRoot.Effect = new BlurEffect { Radius = WaitBlurRadius };
                if (ControllerWaitOverlay != null) ControllerWaitOverlay.Visibility = Visibility.Visible;

                _controllerWaitTimeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MaxWaitMs) };
                _controllerWaitTimeout.Tick += (_, __) => EndControllerWait();
                _controllerWaitTimeout.Start();
            }
            catch
            {
                // A window that will not blur is a window that shows its library. Never the other way.
                EndControllerWait();
            }
        }

        /// <summary>
        /// Takes the overlay down and unhooks everything. Safe to call when no wait is running, and
        /// safe to call twice - every exit from the wait goes through here, so there is one place
        /// that can leave the library blurred and it always finishes.
        /// </summary>
        private void EndControllerWait()
        {
            _controllerWaitActive = false;

            try
            {
                if (_controllerWaitTimeout != null)
                {
                    _controllerWaitTimeout.Stop();
                    _controllerWaitTimeout = null;
                }
                if (_helperPipe != null) _helperPipe.PropertyUpdated -= OnControllerReadyPushed;
                if (ControllerWaitOverlay != null) ControllerWaitOverlay.Visibility = Visibility.Collapsed;

                // The effect is cleared unconditionally, not only when we set it: a blurred library
                // that nobody can un-blur is the one outcome worse than no feature.
                if (LibraryRoot != null) LibraryRoot.Effect = null;
            }
            catch { }
        }
    }
}
