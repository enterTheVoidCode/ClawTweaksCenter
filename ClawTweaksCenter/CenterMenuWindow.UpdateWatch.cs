using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClawTweaksCenter.Core.Sources;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The background half of the drivers and updates area: once the library is up and nobody has
    /// started a game, Center quietly asks whether anything is waiting and turns findings into
    /// notifications.
    ///
    /// ── THE FOUR CONDITIONS, AND WHY EACH ONE IS THERE ──────────────────────────────────────────
    /// 1. The library has finished scanning. Before that the machine is busy with covers and stores,
    ///    and this is the least urgent thing on it.
    /// 2. No game has been started in this Center session (user, 2026-09-13). Someone who has
    ///    launched something is playing, not maintaining.
    /// 3. Once per Center session, whatever the intervals say. The intervals decide whether a source
    ///    is DUE; this makes sure a long session cannot check the same thing twice.
    /// 4. Each source only when its own interval has elapsed - three separate settings, because the
    ///    three cost wildly different amounts.
    ///
    /// ── ORDER IS BY COST, AND IT IS DELIBERATE ──────────────────────────────────────────────────
    /// Widget first: the list is already in memory, fetched at start, so this is a comparison and
    /// nothing else. Drivers second: the helper serves its cache. Windows Update LAST and awaited on
    /// its own, because it is a network round trip to Microsoft measured at 13.6 s and 29.1 s - and
    /// the moment the library finishes loading is exactly the moment the user starts scrolling.
    ///
    /// ⚠️ ONLY FINDINGS BECOME NOTIFICATIONS. A weekly "checked, nothing new" is the fastest way to
    /// train someone to ignore the counter (user, 2026-09-13). The last-checked timestamp is written
    /// either way, so the screen can still account for itself.
    /// </summary>
    public partial class CenterMenuWindow
    {
        /// <summary>Set on every successful launch and never cleared. "Since Center started" is the
        /// scope the user asked for, so a game that has already ended still counts.</summary>
        private bool _gameLaunchedThisSession;

        private bool _updateWatchRan;

        /// <summary>Entry point, called once the library scan reports done. Fire and forget: nothing
        /// on screen waits for this, and an exception in here must not reach the scan that called
        /// it.</summary>
        private void StartBackgroundUpdateChecks()
        {
            if (_updateWatchRan) return;
            if (_gameLaunchedThisSession) return;
            _updateWatchRan = true;
            _ = RunBackgroundUpdateChecksAsync();
        }

        /// <summary>
        /// Announcements from the manifest. NOT part of the interval machinery and deliberately so:
        /// the two cases this channel exists for are "something is badly wrong" and "this release
        /// needs a special setup", and neither of those should wait up to four weeks for a slot.
        /// The duplicate key does the throttling instead - an announcement arrives once, whenever
        /// Center happens to read the manifest.
        /// </summary>
        private void PostManifestAnnouncements()
        {
            var list = _setupVersionCheck?.Announcements;
            if (list == null || list.Count == 0) return;

            foreach (var a in list)
            {
                // A ceiling that is set and already passed means the message has done its job for
                // this machine - the user installed what it was asking for.
                if (a.MaxAppVersion != null && _installedVersion != null && _installedVersion > a.MaxAppVersion)
                    continue;

                Core.Notifications.Add(
                    key: "announce:" + a.Id,
                    kind: "announcement",
                    title: a.Title,
                    detail: a.Detail);
            }
        }

        /// <summary>
        /// One notification, once per machine, outside FSE: Center can be the Windows full-screen
        /// start app - the Xbox-style mode - and that saves the RAM and start-up time of the desktop
        /// underneath (user, 2026-09-16). The setup from the GitHub releases page registers it, so
        /// opening the card goes there.
        ///
        /// NOT POSTED when Windows has no such picker (older than 26100.8039 - the same floor the
        /// installer uses), when Center is the start app already, or when its package is registered
        /// and the user simply chose not to boot into it: all three are questions already answered.
        /// The flag is written whether or not a card was posted, so the check runs once and not on
        /// every start.
        /// </summary>
        private void PostFseHintOnce()
        {
            if (Core.CenterSettings.FseHintPosted) return;
            if (Core.CenterSettings.FseMode) return;
            try
            {
                Core.CenterSettings.FseHintPosted = true;
                if (!Core.FseHelperStart.WindowsHasFsePicker()) return;
                if (Core.FseHelperStart.IsFsePackageChosen()) return;

                Core.Notifications.Add(
                    key: "fse:hint",
                    kind: "fse",
                    title: Core.Loc.T("Make the library your Xbox full-screen start app"),
                    detail: Core.Loc.T("Saves memory and start-up time. Run the setup from the GitHub releases page to register it."));
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("FSE hint: " + ex.Message);
            }
        }

        private async Task RunBackgroundUpdateChecksAsync()
        {
            try
            {
                CheckWidgetUpdateForNotification();
                await CheckDriversForNotificationAsync().ConfigureAwait(false);
                await CheckWindowsUpdatesForNotificationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Background update checks failed: " + ex.Message);
            }
        }

        // ── Widget builds ───────────────────────────────────────────────────────────────────────
        //
        // No fetch here. Center already pulls the release list on every start (FetchGitHubAsync), so
        // the interval throttles the MESSAGE, not the search - which is also what the setting says.
        private void CheckWidgetUpdateForNotification()
        {
            if (!Core.CenterSettings.IsCheckDue(Core.CenterSettings.WidgetNotifyLastUtc,
                                                Core.CenterSettings.WidgetUpdateNotifyIntervalWeeks))
                return;

            Core.CenterSettings.WidgetNotifyLastUtc = DateTime.UtcNow;

            if (_installedVersion == null) return;

            BuildSource best = null;
            Version bestVer = null;
            var candidates = (_releases ?? Enumerable.Empty<BuildSource>()).ToList();
            if (Core.CenterSettings.WidgetNotifyTestBuilds)
                candidates.AddRange(_testBuilds ?? Enumerable.Empty<BuildSource>());

            foreach (var b in candidates)
            {
                if (!TryParseVersion(b.Version, out var v) || v <= _installedVersion) continue;
                // Never announce a build the picker would refuse - a notification that leads to a
                // greyed-out tile is worse than none.
                if (IsBlocked(b, out _)) continue;
                if (bestVer == null || v > bestVer) { bestVer = v; best = b; }
            }
            if (best == null) return;

            Core.Notifications.Add(
                key: "widget:" + best.Version,
                kind: "widget",
                title: Core.Loc.F("ClawTweaks {0} is available", best.Version),
                detail: Core.Loc.T("Install it under Update & Release."));
        }

        // ── Device drivers ──────────────────────────────────────────────────────────────────────
        private async Task CheckDriversForNotificationAsync()
        {
            if (!Core.CenterSettings.IsCheckDue(Core.CenterSettings.DriverCheckLastUtc,
                                                Core.CenterSettings.DriverCheckIntervalWeeks))
                return;

            if (!await EnsureHelperAsync().ConfigureAwait(false)) return;

            string json = await _helperPipe.RequestWithResultAsync(
                new[]
                {
                    new KeyValuePair<string, object>("CheckDriverUpdates", true),
                    new KeyValuePair<string, object>("ForceRefresh", false),
                },
                Shared.Enums.Function.DriverUpdateResult, DriverRequestTimeout).ConfigureAwait(false);

            // No answer is not an answer. Leaving the timestamp alone means the next entry into the
            // library tries again instead of waiting out a whole week on a helper that was starting.
            if (string.IsNullOrEmpty(json)) return;

            var result = ParseJson<DriversResult>(json);
            if (result == null) return;

            Core.CenterSettings.DriverCheckLastUtc = DateTime.UtcNow;
            PostDriverNotifications(result);
        }

        /// <summary>One card per driver with an update, muted ones excluded. Shared by the background
        /// pass and the manual refresh on the drivers screen - a finding is a finding whichever way
        /// it was found (user, 2026-09-16), and the key makes a second posting a no-op.</summary>
        private static void PostDriverNotifications(DriversResult result)
        {
            foreach (var d in result?.Drivers ?? new List<DriverEntryDto>())
            {
                if (d.Ignored) continue;                                    // muted is muted
                // A test-mode row is really current. Posting it would also burn the dedupe key, so
                // the real update at that version could never announce itself later.
                if (d.TestForced) continue;
                if (d.UpdateStatus != DriverUpdateStatusDto.UpdateAvailable) continue;

                // Name AND version: the same driver at a NEWER version has to be able to speak up
                // again, and at the same version it must not.
                Core.Notifications.Add(
                    key: "driver:" + d.Name + ":" + d.Version,
                    kind: "driver",
                    title: Core.Loc.F("Driver update: {0}", d.Name),
                    detail: Core.Loc.F("Version {0} is available.", d.Version));
            }
        }

        // ── Windows Update ──────────────────────────────────────────────────────────────────────
        private async Task CheckWindowsUpdatesForNotificationAsync()
        {
            if (!Core.CenterSettings.IsCheckDue(Core.CenterSettings.WindowsUpdateCheckLastUtc,
                                                Core.CenterSettings.WindowsUpdateCheckIntervalWeeks))
                return;

            if (!await EnsureHelperAsync().ConfigureAwait(false)) return;

            string json = await _helperPipe.RequestWithResultAsync(
                new[]
                {
                    new KeyValuePair<string, object>("CheckWindowsUpdates", true),
                    new KeyValuePair<string, object>("ForceRefresh", true),
                },
                Shared.Enums.Function.WindowsUpdateResult, WindowsUpdateRequestTimeout).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json)) return;
            var result = ParseJson<WindowsUpdateResultDto>(json);

            // resultCode 2 is the only value that means the search actually ran. Anything else is not
            // "nothing pending", and writing the timestamp for it would silence the check for a week
            // on the strength of an answer nobody got.
            if (result == null || result.ResultCode != 2) return;

            Core.CenterSettings.WindowsUpdateCheckLastUtc = DateTime.UtcNow;
            PostWindowsUpdateNotifications(result);
        }

        /// <summary>Shared with the manual check on the drivers screen, like PostDriverNotifications
        /// above. Only a search that really ran (resultCode 2) and found something posts.</summary>
        private static void PostWindowsUpdateNotifications(WindowsUpdateResultDto result)
        {
            if (result == null || result.ResultCode != 2) return;

            var updates = result.Updates ?? new List<WindowsUpdateEntryDto>();
            if (updates.Count == 0) return;

            // ONE notification for the lot, not one per update. The action is the same for all of
            // them - open Windows Update - and a Patch Tuesday would otherwise post six cards.
            //
            // The key carries the DATE, so next month's batch is a new message and today's cannot
            // come back tomorrow.
            Core.Notifications.Add(
                key: "windows:" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ":" + updates.Count,
                kind: "windows",
                title: Core.Loc.F("{0} Windows update(s) waiting", updates.Count),
                detail: updates[0].Title ?? "");
        }
    }
}
