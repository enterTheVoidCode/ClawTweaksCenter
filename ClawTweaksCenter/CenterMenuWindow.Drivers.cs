using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Drivers and Windows Updates. Two columns: device and graphics drivers on the left, the state of
    /// Windows Update on the right. See Doku/PLAN_Drivers_And_Windows_Updates.md in the app repo.
    ///
    /// ── CENTER RENDERS. CENTER DETECTS NOTHING. ──────────────────────────────────────────────────
    /// Both halves come from the helper, which already owns them: MsiClawDriverCheckService plus the
    /// Intel DSA catalogue for the left column, WindowsUpdateCheckService for the right. Mutes, the
    /// beta opt-in and the installer cache are HELPER state - this screen may show them and change
    /// them through the pipe, never hold its own copy. A second opinion about the same question is
    /// the failure this project has already paid for at the TDP, the boost modes and the fan curve.
    ///
    /// ── The two halves cost very different amounts ───────────────────────────────────────────────
    /// The driver check is cheap because the helper caches it, so it runs when the screen opens. The
    /// Windows Update search is a network round trip to Microsoft measured at 29.1 s and 13.6 s, so it
    /// runs ONLY on the button, and the screen says when the answer was taken. A screen that says
    /// nothing for half a minute is indistinguishable from a hung one, hence the explicit busy line.
    ///
    /// ── What the right column deliberately does NOT show ─────────────────────────────────────────
    /// Drivers offered by Windows Update, and Defender's signature updates. The helper filters both
    /// out before they get here (Function.WindowsUpdateResult): Defender's entry is pending
    /// practically always, so a count including it can never reach zero, and Windows' own optional
    /// driver updates are the coarser copy of what the LEFT column already knows about this exact
    /// device. Decision of the user, 2026-09-13.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private DriversResult _driverResult;
        private WindowsUpdateResultDto _windowsUpdates;
        private bool _driversBusy;
        private bool _windowsUpdatesBusy;
        private string _driversError;
        private DateTime? _windowsUpdatesCheckedLocal;

        /// <summary>
        /// The rows on this screen a cursor can sit on, rebuilt on every render: the two interval
        /// settings and the shortcut into Windows' own page. The driver and update CARDS are
        /// deliberately not among them - there is nothing to do to a row that only reports.
        ///
        /// ⚠️ EACH ROW REMEMBERS WHICH COLUMN IT IS IN. The first version kept one flat list, so
        /// Down walked out of the left column and into the right one - and on a machine whose left
        /// column had nothing selectable (a non-Claw, where the helper answers "Claw only") the
        /// cursor could never reach the left side at all. That was the report. Left/Right crosses
        /// between columns now, Up/Down stays inside one.
        /// </summary>
        private sealed class DriverRow
        {
            public int Column;          // 0 = drivers, 1 = Windows Update
            public Action Activate;     // null for a row that only reports
            public FrameworkElement Element;
            public DriverEntryDto Driver;   // set on a driver card, so RT can mute it
        }

        private readonly List<DriverRow> _driverRows = new List<DriverRow>();
        private int _driverRowIndex;

        /// <summary>
        /// Drivers whose download the helper was asked to start in this Center session, by mute key.
        /// The helper answers the install request on the WIDGET's pipe (Program.PipeHandlers,
        /// InstallDriverUpdate), so Center never hears the outcome; the card says the download was
        /// started and the next refresh - which re-reads PnP, the helper drops its cache after an
        /// install - shows what came of it. Cleared on every forced refresh for that reason.
        /// </summary>
        private readonly HashSet<string> _driverInstallStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Deliberately generous. The driver check can go out to MSI and Intel on a cold cache, and the
        // Windows Update search is a network round trip that measured 29.1 s on this very machine - a
        // timeout below that would report "no answer" on a search that was still working.
        private static readonly TimeSpan DriverRequestTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan WindowsUpdateRequestTimeout = TimeSpan.FromSeconds(90);

        // ── Entry ──────────────────────────────────────────────────────────────────────────────
        private void OpenDrivers()
        {
            LeaveLibrary();
            _view = View.Drivers;
            _driverRowIndex = 0;
            RenderDrivers();
            RefreshTabStrip();   // the library's tabs are not navigation for this screen
            RefreshActionBar();

            // Cheap side: the helper serves its cached result unless something asks it not to.
            if (_driverResult == null && !_driversBusy) _ = RequestDriversAsync(force: false);
        }

        // ── Requests ───────────────────────────────────────────────────────────────────────────
        private async Task RequestDriversAsync(bool force)
        {
            _driversBusy = true;
            _driversError = null;
            if (force) _driverInstallStarted.Clear();
            RenderDriversIfStillOpen();
            try
            {
                if (!await EnsureHelperAsync())
                {
                    _driversError = "ClawTweaks is not running.";
                    return;
                }

                string json = await _helperPipe.RequestWithResultAsync(
                    new[]
                    {
                        new KeyValuePair<string, object>("CheckDriverUpdates", true),
                        new KeyValuePair<string, object>("ForceRefresh", force),
                    },
                    Shared.Enums.Function.DriverUpdateResult, DriverRequestTimeout);

                if (string.IsNullOrEmpty(json))
                {
                    // No answer is NOT "no drivers" - saying so would be the same mistake the right
                    // column avoids with resultCode.
                    _driversError = "No answer from ClawTweaks.";
                    return;
                }
                _driverResult = ParseJson<DriversResult>(json);
                if (_driverResult == null) _driversError = "The driver list could not be read.";
                // A manual check is still a check: what it finds goes to the notification list
                // exactly as the background pass would post it (user, 2026-09-16 - a driver found by
                // hand never showed up under LT).
                else if (force) PostDriverNotifications(_driverResult);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("RequestDriversAsync failed: " + ex.Message);
                _driversError = "The driver check failed.";
            }
            finally
            {
                _driversBusy = false;
                RenderDriversIfStillOpen();
            }
        }

        private async Task RequestWindowsUpdatesAsync(bool force)
        {
            _windowsUpdatesBusy = true;
            RenderDriversIfStillOpen();
            try
            {
                if (!await EnsureHelperAsync())
                {
                    _windowsUpdates = new WindowsUpdateResultDto { ErrorMessage = "ClawTweaks is not running." };
                    return;
                }

                string json = await _helperPipe.RequestWithResultAsync(
                    new[]
                    {
                        new KeyValuePair<string, object>("CheckWindowsUpdates", true),
                        new KeyValuePair<string, object>("ForceRefresh", force),
                    },
                    Shared.Enums.Function.WindowsUpdateResult, WindowsUpdateRequestTimeout);

                _windowsUpdates = string.IsNullOrEmpty(json)
                    ? new WindowsUpdateResultDto { ErrorMessage = "No answer from ClawTweaks." }
                    : (ParseJson<WindowsUpdateResultDto>(json)
                       ?? new WindowsUpdateResultDto { ErrorMessage = "The answer could not be read." });

                _windowsUpdatesCheckedLocal = ParseUtc(_windowsUpdates.CheckedUtc);
                // Same as the driver side: a manual search that finds something posts it. The search
                // takes half a minute, so the user has usually moved on by the time it lands - which
                // is exactly what the list is for.
                if (force) PostWindowsUpdateNotifications(_windowsUpdates);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("RequestWindowsUpdatesAsync failed: " + ex.Message);
                _windowsUpdates = new WindowsUpdateResultDto { ErrorMessage = "The check failed." };
            }
            finally
            {
                _windowsUpdatesBusy = false;
                RenderDriversIfStillOpen();
            }
        }

        private async Task<bool> EnsureHelperAsync()
        {
            if (_helperPipe == null) return false;
            if (_helperPipe.IsConnected) return true;
            return await _helperPipe.ConnectAsync(TimeSpan.FromSeconds(6), m => Core.InstallLog.Write(m));
        }

        /// <summary>Redraws only while this screen is still the one on display - a request that comes
        /// back after the user has moved on must not repaint someone else's screen.</summary>
        private void RenderDriversIfStillOpen()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RenderDriversIfStillOpen); return; }
            // Center settings shows the helper's two driver opt-ins out of the same result, so an
            // answer that lands while THAT screen is up redraws it as well.
            if (_view == View.CenterSettings) { RenderCenterSettings(); return; }
            if (_view != View.Drivers) return;
            RenderDrivers();
            RefreshActionBar();
        }

        /// <summary>
        /// Flips one of the helper's two driver opt-ins from Center settings. The helper owns the
        /// value (its LocalSettings, the same ones the widget's checkboxes write), so this sends the
        /// widget's verb and then asks for a fresh list - forced, because the modded Wi-Fi choice
        /// changes which ROW the Wi-Fi driver is, and a cached serve would still show the old one.
        /// </summary>
        private async void SetDriverOptIn(string verb, bool value)
        {
            try
            {
                if (!await EnsureHelperAsync()) return;
                _helperPipe.SendRequest(verb, value);
                Core.InstallLog.Write($"{verb} = {value} sent from Center settings.");
                await RequestDriversAsync(force: true);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("SetDriverOptIn failed: " + ex.Message);
            }
        }

        // ── Render ─────────────────────────────────────────────────────────────────────────────
        private void RenderDrivers()
        {
            BeginContent(centred: false);
            _driverRows.Clear();

            ContentHost.Children.Add(UiHelpers.Title("Drivers & Windows Updates"));

            var columns = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = BuildDriverColumn();
            Grid.SetColumn(left, 0);
            columns.Children.Add(left);

            var right = BuildWindowsUpdateColumn();
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);

            ContentHost.Children.Add(columns);
        }

        private StackPanel BuildDriverColumn()
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionHeading("Device drivers"));
            stack.Children.Add(IntervalHint(Core.CenterSettings.DriverCheckIntervalWeeks));

            if (_driversBusy && _driverResult == null)
            {
                stack.Children.Add(CheckingLine("Checking…"));
                return stack;
            }
            if (_driversError != null)
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Drivers could not be checked", _driversError));
                return stack;
            }
            if (_driverResult == null)
            {
                stack.Children.Add(UiHelpers.Body("Not checked yet."));
                return stack;
            }
            if (!string.IsNullOrEmpty(_driverResult.Message))
            {
                // "Driver updates are only available on MSI Claw hardware" lands here. The setting
                // still belongs on screen: it is about future checks, not about this one.
                stack.Children.Add(UiHelpers.Body(_driverResult.Message));
                return stack;
            }

            var all = _driverResult.Drivers ?? new List<DriverEntryDto>();

            // Graphics on top and set apart, per the user's layout. The split already exists in the
            // model, so this reads it rather than guessing from the name.
            var graphics = all.Where(IsGraphics).ToList();
            var devices = all.Where(d => !IsGraphics(d)).ToList();

            if (graphics.Count > 0)
            {
                // What the catalogue was asked for, next to the rows it produced: WHQL only, or
                // betas too. The switch is in Center settings; the answer belongs here (user,
                // 2026-09-16).
                stack.Children.Add(SubHeading(_driverResult.UseIntelBeta ? "Graphics (WHQL and non-WHQL)" : "Graphics (WHQL only)"));
                foreach (var d in graphics) stack.Children.Add(BuildDriverCard(d, showHighlights: true));
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = UiHelpers.Card,
                    Margin = new Thickness(0, 6, 0, 12),
                });
            }

            stack.Children.Add(SubHeading("Devices"));
            if (devices.Count == 0)
                stack.Children.Add(UiHelpers.Body("Nothing to show for this device."));
            else
                foreach (var d in devices) stack.Children.Add(BuildDriverCard(d, showHighlights: false));

            if (_driverResult.LiveFetchSucceeded == false)
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Offline",
                    "The list could not be refreshed; showing what was known last."));

            return stack;
        }

        private static bool IsGraphics(DriverEntryDto d) =>
            string.Equals(d.Category, "Graphics", StringComparison.OrdinalIgnoreCase);

        private Border BuildDriverCard(DriverEntryDto d, bool showHighlights)
        {
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = d.Name ?? "",
                FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            string installed = string.IsNullOrEmpty(d.InstalledVersion) ? Core.Loc.T("not installed") : d.InstalledVersion;
            stack.Children.Add(new TextBlock
            {
                Text = $"{Core.Loc.T("installed")} {installed}   ·   {Core.Loc.T("available")} {d.Version}",
                FontSize = 13, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });

            bool started = _driverInstallStarted.Contains(DriverMuteKey(d));

            // The state pill, and beside it WHAT A DOES on this card (user, 2026-09-21): Install for
            // an Intel .exe the helper starts, Download for everything it only saves (MSI's ZIPs, the
            // modded Wi-Fi archive), Open page for a web page. The footer names the same verb.
            var pills = new StackPanel { Orientation = Orientation.Horizontal };
            pills.Children.Add(BuildDriverChip(d));
            if (CanInstall(d) && !started) pills.Children.Add(BuildVerbPill(VerbOf(d)));
            stack.Children.Add(pills);

            if (started)
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(StartedHint(d)),
                    FontSize = 12, Foreground = UiHelpers.Accent, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });

            if (showHighlights && !string.IsNullOrWhiteSpace(d.Highlights))
                stack.Children.Add(new TextBlock
                {
                    Text = d.Highlights,
                    FontSize = 12, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });

            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack,
            };

            // A cursor stop whether or not pressing it does anything: without it the left column
            // cannot be reached or scrolled with the pad at all, and reading the list is what most
            // people come to this column for (user, 2026-09-13).
            //
            // A INSTALLS when there is something to install (user, 2026-09-16) - the same helper verb
            // the Game Bar widget's Install button sends. Muted rows do not offer it: the user said
            // they do not want to hear about this version, and RT unmutes it first.
            Action install = CanInstall(d) && !started ? () => StartDriverInstall(d) : (Action)null;
            RegisterDriverRow(0, card, install, d);
            return card;
        }

        private enum DriverVerb { Install, Download, Open }

        /// <summary>
        /// What A does on a driver card, read from what the helper will do with it - never from the
        /// vendor. An .exe/.msi is started (the Intel graphics, Wi-Fi and Bluetooth installers); the
        /// modded Wi-Fi archive and MSI's ZIPs are only saved to Downloads; a "deeplink" row is a
        /// web page. Taking it from the URL the way the helper does is what keeps the label honest.
        /// </summary>
        private static DriverVerb VerbOf(DriverEntryDto d)
        {
            if (IsDeepLink(d)) return DriverVerb.Open;
            if (IsModdedWifi(d)) return DriverVerb.Download;
            return IsRunnableDownload(d) ? DriverVerb.Install : DriverVerb.Download;
        }

        private static string VerbLabel(DriverVerb v) =>
            v == DriverVerb.Install ? "Install" : v == DriverVerb.Download ? "Download" : "Open page";

        /// <summary>
        /// The line under a card once A was pressed. It is on screen for three seconds before the
        /// download is even asked for (see StartDriverInstall), because a small file finishes at once
        /// and the installer or the Downloads folder then covers Center.
        ///
        /// Every Intel installer is large enough that "nothing is happening" needs an answer; the
        /// graphics one is over 800 MB (user, 2026-09-16 and 2026-09-21).
        /// </summary>
        private static string StartedHint(DriverEntryDto d)
        {
            if (IsModdedWifi(d))
                return "Download starts in the background. Your Downloads folder opens when it is done. Unpack it and run Setup.bat.";
            if (VerbOf(d) == DriverVerb.Download)
                return "Download starts in the background. Your Downloads folder opens when it is done.";
            return IsGraphics(d)
                ? "Download starts in the background - over 800 MB, this takes a while. The installer opens when it is done."
                : "Download starts in the background and can take a few minutes. The installer opens when it is done.";
        }

        private static Border BuildVerbPill(DriverVerb v)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(8, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(1),
                BorderBrush = UiHelpers.Accent,
                Child = new TextBlock
                {
                    Text = "\u24B6 " + Core.Loc.T(VerbLabel(v)),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Accent,
                },
            };
        }

        private static bool IsModdedWifi(DriverEntryDto d) =>
            string.Equals(d.Action, "moddedwifi", StringComparison.OrdinalIgnoreCase);

        /// <summary>A row whose link is a web page, not a file (the manifest's "deeplink" action).
        /// The helper only downloads from MSI's and Intel's download hosts and refuses a page, so
        /// these open in the browser - which is what the widget has always done with them.</summary>
        private static bool IsDeepLink(DriverEntryDto d) =>
            string.Equals(d.Action, "deeplink", StringComparison.OrdinalIgnoreCase);

        /// <summary>The helper starts only an .exe or .msi; everything else is saved to Downloads.
        /// Read from the URL the way the helper reads it, so the two cannot disagree.</summary>
        private static bool IsRunnableDownload(DriverEntryDto d)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(new Uri(d.DownloadUrl).LocalPath).ToLowerInvariant();
                return ext == ".exe" || ext == ".msi";
            }
            catch { return false; }
        }

        private static bool CanInstall(DriverEntryDto d) =>
            !d.Ignored
            && !string.IsNullOrWhiteSpace(d.DownloadUrl)
            && (d.UpdateStatus == DriverUpdateStatusDto.UpdateAvailable
                || d.UpdateStatus == DriverUpdateStatusDto.NotInstalled);

        /// <summary>The helper's mute key, composed the way MsiClawDriverCheckService.IgnoreKey
        /// composes it (name|version, lower case). Three writers now - helper, widget, Center - and
        /// a key that drifts in one of them is a mute that silently does not apply.</summary>
        private static string DriverMuteKey(DriverEntryDto d) =>
            ((d.Name ?? "").Trim() + "|" + (d.Version ?? "").Trim()).ToLowerInvariant();

        /// <summary>
        /// Hands the download to the helper and marks the card. Fire-and-forget by necessity: the
        /// helper acknowledges on the widget's pipe and pushes DriverInstallComplete there too, so
        /// there is nothing on this pipe to wait for. The helper downloads, launches the installer
        /// (an .exe/.msi; a BIOS zip is only downloaded) and invalidates its own cache, so the next
        /// refresh here tells the truth about what happened.
        /// </summary>
        private async void StartDriverInstall(DriverEntryDto d)
        {
            try
            {
                if (!await EnsureHelperAsync()) { _driversError = "ClawTweaks is not running."; RenderDriversIfStillOpen(); return; }
                if (IsDeepLink(d))
                {
                    // A page, not a file: Center runs unelevated, so the browser opens as the user.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = d.DownloadUrl,
                        UseShellExecute = true,
                    });
                    Core.InstallLog.Write($"Driver page opened from Center: {d.Name} -> {d.DownloadUrl}");
                    return;
                }

                // THE HINT FIRST, THE DOWNLOAD THREE SECONDS LATER (user, 2026-09-21). A ZIP or the
                // modded archive is done in a moment, and Explorer or the installer then lands on top
                // of Center before the line under the card was readable. The card is marked now, so
                // a second A in those three seconds does nothing.
                string key = DriverMuteKey(d);
                if (!_driverInstallStarted.Add(key)) return;
                RenderDriversIfStillOpen();
                RefreshActionBar();
                await Task.Delay(TimeSpan.FromSeconds(3));

                // The modded Wi-Fi driver has its own verb: the helper saves the archive to
                // Downloads and opens Explorer on it - it never unpacks or runs Setup.bat (see the
                // helper's InstallModdedWifiAsync). InstallDriverUpdate would refuse its host.
                bool sent = IsModdedWifi(d)
                    ? _helperPipe.SendRequest("InstallModdedWifi", true)
                    : _helperPipe.SendRequest("InstallDriverUpdate", d.DownloadUrl);
                if (sent)
                    Core.InstallLog.Write($"Driver {VerbLabel(VerbOf(d)).ToLowerInvariant()} requested from Center: {d.Name} {d.Version}");
                else
                {
                    // Nothing went out: take the mark back, or the card claims a download forever.
                    _driverInstallStarted.Remove(key);
                    Core.InstallLog.Write($"Driver request could not be sent: {d.Name} {d.Version}");
                }
                RenderDriversIfStillOpen();
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("StartDriverInstall failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Mute or unmute the driver under the cursor - the same SetDriverIgnore verb and the same key
        /// the widget sends, so a mute made here shows there and vice versa. The helper acks on the
        /// widget's pipe, so the list is simply re-requested (cached; the helper re-applies mutes on
        /// every serve) and the pill changes on the redraw.
        /// </summary>
        private async void ToggleDriverMute(DriverEntryDto d)
        {
            try
            {
                if (!await EnsureHelperAsync()) return;
                bool mute = !d.Ignored;
                _helperPipe.SendRequest(new[]
                {
                    new KeyValuePair<string, object>("SetDriverIgnore", DriverMuteKey(d)),
                    new KeyValuePair<string, object>("IgnoreState", mute),
                });
                Core.InstallLog.Write($"Driver {(mute ? "muted" : "unmuted")} from Center: {d.Name} {d.Version}");
                await RequestDriversAsync(force: false);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("ToggleDriverMute failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The one-glance answer per row: a coloured pill instead of a line of prose that was only
        /// there when something was wrong. Before this, "up to date" was rendered as the ABSENCE of a
        /// line - which reads the same as a row that was never checked.
        ///
        /// Four states, and the quiet one is deliberate. Unknown is what a BIOS row from a foreign
        /// board comes back as (DriverMatchUtil.CompareMsiBiosCodes refuses to compare across board
        /// prefixes and returns null), and the helper does not offer those - so it stays grey and
        /// says "cannot tell" rather than colouring a row nobody should act on.
        /// </summary>
        private Border BuildDriverChip(DriverEntryDto d)
        {
            string text;
            Brush colour;

            if (d.Ignored)
            {
                // A muted row keeps its real state out of the chip on purpose: the user has said they
                // do not want to hear about it, and a green pill on a muted update would argue.
                text = Core.Loc.T("Muted");
                colour = UiHelpers.Subtle;
            }
            else
            {
                switch (d.UpdateStatus)
                {
                    case DriverUpdateStatusDto.UpdateAvailable:
                        // TestForced: offered only because the driver test mode is on.
                        text = d.TestForced ? Core.Loc.T("Update available (test)")
                            : d.IsBeta ? Core.Loc.T("Update available (beta)") : Core.Loc.T("Update available");
                        colour = UiHelpers.Warn;
                        break;
                    case DriverUpdateStatusDto.UpToDate:
                        text = Core.Loc.T("Up to date");
                        colour = UiHelpers.Ok;
                        break;
                    case DriverUpdateStatusDto.NotInstalled:
                        text = Core.Loc.T("Not installed");
                        colour = UiHelpers.Accent;
                        break;
                    default:
                        text = Core.Loc.T("Cannot tell");
                        colour = UiHelpers.Subtle;
                        break;
                }
            }

            // Tinted from the SAME brush as the text, so the pill cannot drift out of the theme the
            // way a hard-coded pair of colours would. 0.16 keeps it readable on the card behind it.
            var fill = colour.Clone();
            fill.Opacity = 0.16;

            return new Border
            {
                Background = fill,
                // A rectangle with soft corners, not a capsule: the pill shape read as a tag, and
                // these are states (user, 2026-09-13).
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = colour,
                },
            };
        }

        private StackPanel BuildWindowsUpdateColumn()
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionHeading("Windows Update"));
            stack.Children.Add(IntervalHint(Core.CenterSettings.WindowsUpdateCheckIntervalWeeks));

            if (_windowsUpdatesBusy)
            {
                // The search really does take tens of seconds. Saying so is the difference between a
                // slow screen and one the user reads as frozen.
                stack.Children.Add(CheckingLine("Checking with Windows… this can take up to a minute."));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            if (_windowsUpdates == null)
            {
                stack.Children.Add(UiHelpers.Body("Not checked yet."));
                stack.Children.Add(UiHelpers.Body("The check asks Microsoft directly and takes a moment."));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            if (!string.IsNullOrEmpty(_windowsUpdates.ErrorMessage))
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Could not check", _windowsUpdates.ErrorMessage));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            // resultCode 2 is the only "the search really ran" answer. Anything else must not be
            // rendered as "up to date" - that is the whole reason it travels over the pipe.
            if (_windowsUpdates.ResultCode != 2)
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "No result from Windows Update",
                    Core.Loc.F("The search ended with code {0}.", _windowsUpdates.ResultCode)));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            // ONE banner, not two (user, 2026-09-16): "restart pending" used to be its own row under
            // the list, and with the count banner above it the column spent two cards on one fact.
            // It folds into the banner instead - the title says both, the detail says what to do.
            var updates = _windowsUpdates.Updates ?? new List<WindowsUpdateEntryDto>();
            bool reboot = _windowsUpdates.RebootRequired;
            if (updates.Count == 0)
                stack.Children.Add(reboot
                    ? UiHelpers.StatusRow(StatusKind.Warning, "Restart pending",
                        "Windows needs a restart to finish an update.")
                    : UiHelpers.StatusRow(StatusKind.Ok, "Up to date",
                        "Windows has nothing pending for this machine."));
            else
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning,
                    reboot ? Core.Loc.F("{0} update(s) ready, restart pending", updates.Count)
                           : Core.Loc.F("{0} update(s) ready", updates.Count),
                    reboot ? "Install them in Windows Update, then restart." : "Install them in Windows Update."));

            if (_windowsUpdatesCheckedLocal.HasValue)
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.F("Checked at {0}", _windowsUpdatesCheckedLocal.Value.ToString("t")),
                    FontSize = 12, Foreground = UiHelpers.Subtle, Margin = new Thickness(2, 2, 0, 10),
                });

            foreach (var u in updates) stack.Children.Add(BuildWindowsUpdateCard(u));

            AppendWindowsUpdateFooterRows(stack);
            return stack;
        }

        private Border BuildWindowsUpdateCard(WindowsUpdateEntryDto u)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = u.Title ?? "",
                FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            var facts = new List<string>();
            if (!string.IsNullOrEmpty(u.Kb)) facts.Add("KB" + u.Kb);
            if (!string.IsNullOrEmpty(u.Severity)) facts.Add(u.Severity);
            if (u.SizeBytes > 0) facts.Add($"{u.SizeBytes / 1024d / 1024d:N0} MB");
            // 1 = always, 2 = can require. 0 says nothing worth a line.
            if (u.RebootBehavior == 1) facts.Add(Core.Loc.T("restart required"));
            else if (u.RebootBehavior == 2) facts.Add(Core.Loc.T("restart possible"));

            if (facts.Count > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", facts),
                    FontSize = 12, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack,
            };
            RegisterDriverRow(1, card, null);
            return card;
        }

        /// <summary>The one thing this column does besides report: a real BUTTON into Windows' own
        /// page. It was briefly a settings-style row and that was wrong - it is an action, not a
        /// value (user, 2026-09-13). The interval that sat under it moved to Center settings.</summary>
        private void AppendWindowsUpdateFooterRows(StackPanel stack)
        {
            var button = new Button
            {
                Content = Core.Loc.T("Open Windows Update"),
                Style = (Style)Application.Current.Resources["SetupButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 0),
            };
            button.Click += (_, __) => OpenWindowsUpdateSettings();

            RegisterDriverRow(1, button, OpenWindowsUpdateSettings);
            stack.Children.Add(button);
        }

        /// <summary>
        /// Makes an element a cursor stop. <paramref name="activate"/> may be null: a row that only
        /// reports still has to be somewhere the cursor can go, or a column of pure information
        /// cannot be reached or scrolled with a pad.
        ///
        /// The selection is drawn as an outline on whatever the element already is, so a card still
        /// looks like a card and a button still looks like a button.
        /// </summary>
        private void RegisterDriverRow(int column, FrameworkElement element, Action activate, DriverEntryDto driver = null)
        {
            int index = _driverRows.Count;
            bool selected = index == _driverRowIndex;

            if (element is Control control)
            {
                control.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;
                control.BorderThickness = new Thickness(selected ? 2 : 0);
            }
            else if (element is Border border)
            {
                border.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;
                border.BorderThickness = new Thickness(selected ? 2 : 0);
            }

            if (activate != null)
            {
                element.Cursor = Cursors.Hand;
                element.MouseLeftButtonUp += (_, __) => { _driverRowIndex = index; activate(); };
            }

            _driverRows.Add(new DriverRow { Column = column, Activate = activate, Element = element, Driver = driver });
        }

        /// <summary>1 \u2192 2 \u2192 3 \u2192 4 \u2192 off \u2192 1. Off is reachable on purpose: a background check that
        /// reaches the network and cannot be switched off is a behaviour, not a setting.</summary>
        private static int NextInterval(int weeks) =>
            weeks >= Core.CenterSettings.IntervalMaxWeeks ? Core.CenterSettings.IntervalOff
            : weeks <= Core.CenterSettings.IntervalOff ? Core.CenterSettings.IntervalMinWeeks
            : weeks + 1;

        private static string IntervalLabel(int weeks)
        {
            if (weeks <= Core.CenterSettings.IntervalOff) return Core.Loc.T("Never");
            if (weeks == 1) return Core.Loc.T("Every week");
            return Core.Loc.F("Every {0} weeks", weeks);
        }

        // \u2500\u2500 Navigation \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        private void MoveDriversSelection(PadButton dir)
        {
            if (_driverRows.Count == 0) return;
            if (_driverRowIndex < 0 || _driverRowIndex >= _driverRows.Count) _driverRowIndex = 0;

            int current = _driverRowIndex;
            int column = _driverRows[current].Column;
            int next = current;

            if (dir == PadButton.Up || dir == PadButton.Down)
            {
                int step = dir == PadButton.Up ? -1 : 1;
                for (int i = current + step; i >= 0 && i < _driverRows.Count; i += step)
                    if (_driverRows[i].Column == column) { next = i; break; }
            }
            else if (dir == PadButton.Left || dir == PadButton.Right)
            {
                // Cross to the other column and land on ITS first row - not on the nearest index,
                // which would depend on how many driver rows happen to be listed above.
                int target = dir == PadButton.Left ? 0 : 1;
                for (int i = 0; i < _driverRows.Count; i++)
                    if (_driverRows[i].Column == target) { next = i; break; }
            }
            else return;

            if (next == current) return;
            _driverRowIndex = next;
            RenderDrivers();

            // Without this the cursor walks off the bottom of the viewport and the screen looks
            // frozen - the rows below the fold are exactly the ones this navigation exists for.
            //
            // ⚠️ THE FIRST ROW OF A COLUMN SCROLLS TO THE VERY TOP, not merely into view. Above it
            // sit the title, the heading and the interval chips, and BringIntoView stops the moment
            // the card is visible - so once the screen had been scrolled down, nothing the pad
            // could do brought the top back (user, 2026-09-16). The first row is the only stop
            // there is up there, so it has to carry the whole trip.
            if (IsFirstRowOfItsColumn(_driverRowIndex)) ContentScroller?.ScrollToTop();
            else _driverRows[_driverRowIndex].Element?.BringIntoView();

            // ⚠️ THE CHIPS FOLLOW THE CURSOR, and they did not until 2026-09-16. Ⓐ is enabled per
            // row (a card reads, a button acts), but the bar was only rebuilt when the screen was
            // opened - with the cursor on the first driver card. So "Open Windows Update" stayed
            // greyed out however far the cursor went, and the press did nothing (user report).
            RefreshActionBar();
        }

        private bool IsFirstRowOfItsColumn(int index)
        {
            int column = _driverRows[index].Column;
            for (int i = 0; i < index; i++)
                if (_driverRows[i].Column == column) return false;
            return true;
        }

        /// <summary>
        /// What this column does on its own, and where to change it.
        ///
        /// It sits at the TOP because it is a property of the whole list underneath, not a footnote
        /// to it - and because the setting itself no longer lives on this screen (it moved to Center
        /// settings on 2026-09-13). Without the pointer, a column that quietly checks itself once a
        /// week would be a behaviour with no visible switch anywhere near it.
        ///
        /// TWO CHIPS, not a sentence (user, 2026-09-16): the interval, and where it is changed. An
        /// outlined chip with rounded corners, so it reads as a fact about the column and not as a
        /// second line of the heading.
        /// </summary>
        private static StackPanel IntervalHint(int weeks)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, -4, 10, 12),
            };
            row.Children.Add(InfoChip(weeks <= Core.CenterSettings.IntervalOff
                ? Core.Loc.T("Automatic check: off")
                : Core.Loc.F("Automatic check: {0}", IntervalLabel(weeks).ToLowerInvariant())));
            row.Children.Add(InfoChip(Core.Loc.T("Change in Center settings")));
            return row;
        }

        /// <summary>A small outlined chip. Distinct from the driver state pill on purpose - that one
        /// is filled and coloured by state, this one only informs.</summary>
        private static Border InfoChip(string text) => new Border
        {
            BorderBrush = UiHelpers.Subtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Opacity = 0.85,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
            },
        };

        /// <summary>The red spinner beside a "checking" line, while a check runs.</summary>
        private static UIElement CheckingLine(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var spinner = GifSpinner.Create(28);
            if (spinner is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(0, 0, 10, 0);
            }
            row.Children.Add(spinner);
            var body = UiHelpers.Body(text);
            body.VerticalAlignment = VerticalAlignment.Center;
            body.Margin = new Thickness(0);
            row.Children.Add(body);
            return row;
        }

        private static TextBlock SectionHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 17, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
            Margin = new Thickness(0, 0, 0, 10),
        };

        private static TextBlock SubHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Subtle,
            Margin = new Thickness(2, 0, 0, 6),
        };

        // ── Action bar ─────────────────────────────────────────────────────────────────────────
        private void RefreshDriversActionBar()
        {
            // \u24B6 acts on the ROW the cursor is on - the two interval settings and the shortcut into
            // Windows. The two check buttons keep their own chips: they are what someone came here to
            // press, and burying them one cursor move deep would be the wrong trade.
            // Enabled only when the row under the cursor actually does something - most of them are
            // driver and update cards, which exist to be read.
            var row = _driverRowIndex >= 0 && _driverRowIndex < _driverRows.Count ? _driverRows[_driverRowIndex] : null;
            bool canAct = row?.Activate != null;
            // "Install" on a driver card, "Open" on the Windows Update button - the chip names what
            // the press does, and the two are not the same thing.
            AddAction(PadButton.A, row?.Driver != null ? VerbLabel(VerbOf(row.Driver)) : "Open", canAct, () =>
            {
                if (canAct) row.Activate();
            });

            // THREE CHIPS AT MOST, and only the ones for the column the cursor is in (user,
            // 2026-09-21). Left, device drivers: Y checks again, RT mutes the card under the cursor.
            // Right, Windows Update: X checks. Both check buttons stay BOUND in either column - only
            // the chip follows the cursor.
            bool windowsColumn = row?.Column == 1;
            Action checkWindows = () => _ = RequestWindowsUpdatesAsync(force: true);
            Action checkDrivers = () => _ = RequestDriversAsync(force: true);

            if (windowsColumn)
            {
                AddAction(PadButton.X, "Check Windows Update", !_windowsUpdatesBusy, checkWindows);
                if (!_driversBusy) _liveActions[PadButton.Y] = checkDrivers;
            }
            else
            {
                AddAction(PadButton.Y, "Check drivers again", !_driversBusy, checkDrivers);
                if (!_windowsUpdatesBusy) _liveActions[PadButton.X] = checkWindows;
            }

            // Mute lives on RT, only while the cursor is on a driver card - there is nothing to mute
            // anywhere else. That is the third chip there, so Back keeps its binding without one.
            if (row?.Driver != null)
            {
                AddAction(PadButton.RT, row.Driver.Ignored ? "Unmute" : "Mute", true, () => ToggleDriverMute(row.Driver));
                _liveActions[PadButton.B] = GoHome;
            }
            else
            {
                AddAction(PadButton.B, "Back", true, GoHome);
            }
        }

        /// <summary>
        /// ⚠️ An UNKNOWN ms-settings id opens the Settings HOME PAGE - no error, no return value, and
        /// ShellExec reports success either way. That is exactly how the FSE button broke on
        /// 2026-09-10. This id is read out of C:\Windows\ImmersiveControlPanel, not guessed, and so
        /// must any that is added next to it.
        /// </summary>
        private void OpenWindowsUpdateSettings()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:windowsupdate-action",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Opening Windows Update failed: " + ex.Message);
            }
        }

        // ── Parsing ────────────────────────────────────────────────────────────────────────────
        private static T ParseJson<T>(string json) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write($"Drivers area: could not parse {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static DateTime? ParseUtc(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : (DateTime?)null;
        }

        // ── Wire shapes ────────────────────────────────────────────────────────────────────────
        // Mirrors of what the helper sends, kept deliberately SMALL: only the fields this screen
        // draws. The helper's payload carries more (download URLs, cached installers, match scores)
        // and every one of those belongs to an action this screen does not have.

        private enum DriverUpdateStatusDto { Unknown = 0, UpToDate = 1, UpdateAvailable = 2, NotInstalled = 3 }

        private sealed class DriversResult
        {
            public bool LiveFetchSucceeded { get; set; }
            /// <summary>The helper's two opt-ins, as it holds them. Shown and toggled from Center
            /// settings, never stored here - the helper is the one copy.</summary>
            public bool UseIntelBeta { get; set; }
            public bool UseModdedWifi { get; set; }
            /// <summary>The helper's debug switch: every driver with a download is offered.</summary>
            public bool DriverTestMode { get; set; }
            public string Message { get; set; }
            public List<DriverEntryDto> Drivers { get; set; }
        }

        private sealed class DriverEntryDto
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Version { get; set; }
            public string InstalledVersion { get; set; }
            public DriverUpdateStatusDto UpdateStatus { get; set; }
            public string Highlights { get; set; }
            public bool IsBeta { get; set; }
            public bool Ignored { get; set; }
            /// <summary>Offered only because of the test mode - the row is really current.</summary>
            public bool TestForced { get; set; }
            public string ProviderScope { get; set; }
            public string DownloadUrl { get; set; }   // what A hands to the helper
            public string Action { get; set; }        // "install" | "moddedwifi" | ... - picks the verb
        }

        private sealed class WindowsUpdateResultDto
        {
            public int ResultCode { get; set; }
            public string CheckedUtc { get; set; }
            public bool RebootRequired { get; set; }
            public List<WindowsUpdateEntryDto> Updates { get; set; }
            public string ErrorMessage { get; set; }
        }

        private sealed class WindowsUpdateEntryDto
        {
            public string Title { get; set; }
            public string Kb { get; set; }
            public string Severity { get; set; }
            public long SizeBytes { get; set; }
            public int RebootBehavior { get; set; }
        }
    }
}
