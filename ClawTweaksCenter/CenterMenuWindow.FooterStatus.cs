using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Shared.Enums;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The two things in the footer that are NOT buttons: the battery on the left, the clock on the
    /// right.
    ///
    /// -- Why they are not part of the action bar ------------------------------------------------
    /// The chips say what the buttons do right now; these two say what the machine is doing. That
    /// difference is the whole reason they survive immersive mode: when the hints are hidden the
    /// screen is a shelf of covers with nothing else on it, and the time and the charge are exactly
    /// the two facts somebody still wants from across the room (user, 2026-09-09).
    ///
    /// -- Where the battery comes from -----------------------------------------------------------
    /// From the HELPER, not from Windows here. The helper already reads it, already resolves the
    /// runtime Windows-first (the same source MSI's own OSD uses, which is what makes it work on a
    /// Claw 8 EX where the battery exposes no rate sensor at all), and already publishes it as the
    /// QuickMetrics bundle the widget draws. A second reader in Center would be a second answer to
    /// the same question, and the one that is wrong is always the one nobody is looking at.
    ///
    /// It is a REQUEST, not a subscription: the helper's own 1 Hz push only runs while the widget's
    /// Quick Metrics row is switched on, so a footer that rode along would go blank because of a
    /// setting in a different program. Program.PipeHandlers answers "GetPowerStatus" with the same
    /// JSON on demand.
    /// </summary>
    public partial class CenterMenuWindow
    {
        /// <summary>
        /// How often the battery is asked for. TEN SECONDS, set by the user, and it is a ceiling
        /// rather than a target: a charge percentage moves a few times an hour, and the round trip
        /// costs the helper a sensor read on a handheld whose battery is the thing being measured.
        ///
        /// The clock rides the same timer. It shows hours and minutes, so it can be up to ten
        /// seconds late crossing a minute - which is invisible on a clock without a second hand, and
        /// cheaper than a second timer that exists to be exactly on time.
        /// </summary>
        private static readonly TimeSpan FooterStatusInterval = TimeSpan.FromSeconds(10);

        /// <summary>How long the helper gets to answer. Short: a footer is not worth a stall, and the
        /// next attempt is ten seconds away.</summary>
        private static readonly TimeSpan PowerStatusTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// How often a DISCONNECTED footer tries to reach the helper.
        ///
        /// Longer than the poll on purpose. Center's pipe client is not connected by default - every
        /// other caller (the power actions, the tray column, onboarding, leave, maintenance) connects
        /// for itself when it needs the helper, and this one has to as well. A connect attempt costs
        /// up to four seconds of liveness verification, so on a machine with no helper at all a
        /// 10-second retry would spend most of its life in a connect that cannot succeed.
        /// </summary>
        private static readonly TimeSpan PipeRetryInterval = TimeSpan.FromSeconds(30);

        /// <summary>One attempt, then wait. Long enough for the client's own liveness check (it needs
        /// a status push back within 4 s before it calls a bind "live").</summary>
        private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(5);

        private DispatcherTimer _footerStatusTimer;
        private bool _powerStatusInFlight;
        private DateTime _lastPipeAttemptUtc = DateTime.MinValue;

        private void StartFooterStatus()
        {
            UpdateFooterClock();

            if (_footerStatusTimer == null)
            {
                _footerStatusTimer = new DispatcherTimer { Interval = FooterStatusInterval };
                _footerStatusTimer.Tick += (_, __) => { UpdateFooterClock(); RequestPowerStatus(); };
            }
            _footerStatusTimer.Start();

            // The first reading right away rather than ten seconds in - an empty slot on startup
            // reads as "Center cannot see the battery", which is the one thing it must not say while
            // it simply has not asked yet.
            RequestPowerStatus();
        }

        private void UpdateFooterClock()
        {
            if (FooterClock == null) return;
            // ShortTimePattern, so it follows the user's own 24h/12h setting rather than ours.
            FooterClock.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Asks the helper for the metrics bundle and draws the battery from it.
        ///
        /// A missed answer LEAVES THE LAST READING UP. The helper restarts on every ClawTweaks
        /// update, and blanking the line for those few seconds would make a working footer flicker
        /// between a value and nothing. It only clears when the pipe is actually down (below), which
        /// is a state that lasts.
        /// </summary>
        private void RequestPowerStatus()
        {
            if (FooterBattery == null || _helperPipe == null)
            {
                if (FooterBattery != null) FooterBattery.Visibility = Visibility.Collapsed;
                return;
            }

            // One in flight at a time. The timeout is shorter than the interval, so this can only
            // ever catch a genuinely slow answer - but a queue of overlapping requests against a
            // helper that is busy is how a diagnostic turns into load. A connect attempt counts as
            // in flight too: it can take seconds, and two of them at once is two pipes.
            if (_powerStatusInFlight) return;
            _powerStatusInFlight = true;

            _ = RequestPowerStatusAsync();
        }

        /// <summary>
        /// Connects if needed, then asks.
        ///
        /// ⚠️ THE CONNECT IS THE PART THAT WAS MISSING (measured 2026-09-09). Center's shared
        /// HelperPipeClient starts DISCONNECTED and stays that way: every other user of it - the
        /// power actions, the tray column, onboarding, leave, maintenance - calls ConnectAsync for
        /// itself first. This one only tested IsConnected, so it drew a battery exactly when some
        /// other screen had happened to open the pipe, and nothing the rest of the time. The helper
        /// was answering correctly the whole time (probed over the Quick Settings pipe: batteryLevel
        /// 87, timeRemaining 19502) - nobody was asking.
        ///
        /// One connect, not one per tick: the client re-establishes itself after a drop
        /// (_keepConnected), so the only case that needs a retry here is a helper that was not there
        /// at all - and that one gets the slow interval.
        /// </summary>
        private async System.Threading.Tasks.Task RequestPowerStatusAsync()
        {
            try
            {
                if (!_helperPipe.IsConnected)
                {
                    if (DateTime.UtcNow - _lastPipeAttemptUtc < PipeRetryInterval)
                    {
                        FooterBattery.Visibility = Visibility.Collapsed;
                        return;
                    }
                    _lastPipeAttemptUtc = DateTime.UtcNow;

                    bool connected = await _helperPipe.ConnectAsync(PipeConnectTimeout).ConfigureAwait(true);
                    if (!connected)
                    {
                        // No helper is a lasting state, and an empty slot is the honest answer to it.
                        FooterBattery.Visibility = Visibility.Collapsed;
                        return;
                    }
                }

                string json = await _helperPipe.RequestWithResultAsync("GetPowerStatus", true, Function.QuickMetrics, PowerStatusTimeout)
                                               .ConfigureAwait(true);
                if (json != null) ApplyPowerStatus(json);
            }
            catch (Exception ex) { Core.InstallLog.Write("[Footer] power status request failed: " + ex.Message); }
            finally { _powerStatusInFlight = false; }
        }

        /// <summary>
        /// Reads the four fields the footer needs out of the helper's bundle.
        ///
        /// Hand-rolled, like every other pipe payload on this side (HelperPipeClient's own parser is
        /// the precedent): the bundle is a flat object of numbers written by one printf-style line in
        /// PerformanceManager, and pulling a JSON dependency in for it would be the larger change.
        /// </summary>
        private void ApplyPowerStatus(string json)
        {
            if (FooterBattery == null) return;

            double level = ReadNumber(json, "batteryLevel");
            double remaining = ReadNumber(json, "timeRemaining");
            double toFull = ReadNumber(json, "timeToFull");
            bool charging = Regex.IsMatch(json, "\"isCharging\"\\s*:\\s*true", RegexOptions.IgnoreCase);

            // A level of -1 is the helper's "no reading", and 0 on a running machine is the same
            // thing wearing a plausible number. Neither is worth a line in the footer.
            if (level <= 0)
            {
                FooterBattery.Visibility = Visibility.Collapsed;
                return;
            }

            string percent = ((int)Math.Round(level)).ToString(CultureInfo.CurrentCulture) + "%";
            double seconds = charging ? toFull : remaining;

            string text;
            if (seconds > 0)
            {
                // Same shape as the widget's own tile: h:mm, seconds in, so the two surfaces agree
                // to the minute rather than looking like two different measurements.
                string clock = ((int)(seconds / 3600)).ToString(CultureInfo.CurrentCulture)
                               + ":" + ((int)((seconds % 3600) / 60)).ToString("D2", CultureInfo.CurrentCulture);
                text = charging
                    ? Core.Loc.F("{0} · {1} to full", percent, clock)
                    : Core.Loc.F("{0} · {1} left", percent, clock);
            }
            else
            {
                // The percentage on its own, and the charging state as a word rather than a time
                // nobody has. "--:--" next to a real percentage reads as a broken readout; the fact
                // that it is charging is the useful half and it is known.
                text = charging ? Core.Loc.F("{0} · charging", percent) : percent;
            }

            FooterBattery.Text = text;
            FooterBattery.Visibility = Visibility.Visible;
        }

        private static double ReadNumber(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[\\d.]+)");
            if (!m.Success) return -1;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1;
        }

        /// <summary>
        /// The footer's own surface: opaque bar, translucent bar over a picture, or nothing at all.
        ///
        /// ONE WRITER for all three, and it reads BOTH facts every time rather than being told which
        /// one changed. The two inputs arrive from opposite directions - a background is chosen in
        /// the settings, the chips are hidden by immersive mode - and a chrome that each of them
        /// half-owns is how a footer ends up transparent with a hairline under it, or opaque over a
        /// picture, depending on which happened last.
        /// </summary>
        private void ApplyFooterChrome()
        {
            if (FooterBar == null) return;

            bool overPicture = BackgroundImage != null && BackgroundImage.Visibility == Visibility.Visible;
            bool chipsHidden = ActionBar != null && ActionBar.Visibility != Visibility.Visible;

            if (chipsHidden)
            {
                // Immersive, hints down. What is left is a line of text over the shelf, so it gets
                // no bar and no rule - drawing either would put a band back on the screen that
                // immersive mode exists to remove.
                FooterBar.Background = System.Windows.Media.Brushes.Transparent;
                FooterBar.BorderThickness = new Thickness(0);
                return;
            }

            FooterBar.BorderThickness = new Thickness(0, 1, 0, 0);

            if (!overPicture)
            {
                FooterBar.Background = (System.Windows.Media.Brush)TryFindResource("FooterBrush");
                return;
            }

            // The footer's own colour, thinned so the blurred picture behind it comes through.
            // Derived from the resource rather than a second literal colour: a theme change moves
            // both, and a hand-picked hex here would be the one that stayed behind.
            if (TryFindResource("FooterBrush") is System.Windows.Media.SolidColorBrush footer)
            {
                var colour = footer.Color;
                colour.A = 0xA6;
                var brush = new System.Windows.Media.SolidColorBrush(colour);
                brush.Freeze();
                FooterBar.Background = brush;
            }
        }

        /// <summary>
        /// Puts the blurred background copy exactly behind the footer, whatever height it currently
        /// has.
        ///
        /// The mask is in RELATIVE coordinates (0..1 of the element), and the element spans the whole
        /// window - so the offset is the footer's share of the window height, recomputed whenever
        /// either changes. Both stops sit on the same offset: a hard edge, matching the hairline the
        /// footer already draws, rather than a fade that would look like a rendering artefact.
        /// </summary>
        private void RefreshFooterBlurMask()
        {
            if (BlurMaskTop == null || BlurMaskBottom == null) return;

            double windowHeight = ActualHeight;
            double footerHeight = FooterBar?.ActualHeight ?? 0;
            if (windowHeight <= 0 || footerHeight <= 0) return;

            double offset = 1.0 - (footerHeight / windowHeight);
            if (offset < 0) offset = 0;
            if (offset > 1) offset = 1;

            BlurMaskTop.Offset = offset;
            BlurMaskBottom.Offset = offset;
        }
    }
}
