using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Center's own settings - the ones that are about the application rather than about the games
    /// library, which has its own screen (CenterMenuWindow.Library.cs).
    ///
    /// WHY A SEPARATE SCREEN rather than two more rows on the library's. The library settings live
    /// inside the library: they are reached from it, they draw into its host, and their Back returns
    /// to the grid. Language is not a library setting - it is the language of the installer screens
    /// too, and on a machine where ClawTweaks is not installed yet the library does not exist at all,
    /// so a language buried inside it would be unreachable exactly when somebody is trying to read
    /// the install instructions.
    ///
    /// It therefore draws into ContentHost like Home, Browse and Onboarding, and is available with
    /// or without ClawTweaks installed.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private readonly List<Border> _centerSettingsRows = new List<Border>();
        private int _centerSettingsIndex;

        /// <summary>The language list is unfolded. While it is, the D-pad and A belong to IT and not
        /// to the settings rows behind it - see MoveCenterSettingsSelection.</summary>
        private bool _languageListOpen;
        private int _languageIndex;
        private readonly List<Border> _languageRows = new List<Border>();

        private const int CenterSettingsLanguageRow = 0;
        private const int CenterSettingsFullscreenRow = 1;

        // ── Nach Updates suchen und benachrichtigen ─────────────────────────────────────────────
        //
        // All three intervals live HERE and not next to the thing they are about (user, 2026-09-13).
        // They were under their own columns on the drivers screen and inside Update & Release, which
        // put settings in three places and made two screens part settings screen. One place, one
        // shape, and each screen keeps only what it is for.
        private const int CenterSettingsDriverCheckRow = 2;
        private const int CenterSettingsWindowsCheckRow = 3;

        // The helper's two driver opt-ins, under the driver row (user, 2026-09-16). HELPER STATE,
        // read out of the driver result and written back over the pipe - the same verbs the widget's
        // checkboxes send - so Center holds no copy of either. Without a helper the rows say so.
        private const int CenterSettingsDriverBetaRow = 4;
        private const int CenterSettingsDriverWifiRow = 5;
        // Debug switch, also helper state (DriverTestMode): every driver with a download is offered
        // as an update, so each download path can be tried on a machine that is current (user,
        // 2026-09-19). The widget's debug section writes the same value.
        private const int CenterSettingsDriverTestRow = 6;

        private const int CenterSettingsWidgetCheckRow = 7;
        private const int CenterSettingsWidgetTestRow = 8;

        /// <summary>From here down the grid is ONE column - see MoveCenterSettingsSelection.</summary>
        private const int CenterSettingsTailStart = CenterSettingsDriverBetaRow;

        // ── Experimentell - ENTFERNT 2026-09-15 ────────────────────────────────────────────────
        //
        // There was one experimental row here, "Center starts the helper" (CenterSettingsFseStartRow
        // = 6). It asked the helper's scheduled task to run early when Center is the full screen
        // home app. It is gone, together with the machinery behind it, for three reasons:
        //
        //   * THE MEASUREMENT CAME BACK NEGATIVE. Across four boots on 2026-09-14 the logon trigger
        //     fired at about +16.7s and Center ran at about +21.7s, so the scheduler refused every
        //     request as a duplicate (event 322). It never once made anything faster.
        //   * The problem it aimed at - the virtual pad arriving in the middle of the user's first
        //     navigation - was solved somewhere else entirely, in the scheduled task itself. See
        //     Doku/TODO_Scheduled_Task_Fast_Controller.md in the helper repo.
        //   * An experimental switch that does nothing is worse than no switch: it invites people to
        //     turn it on, and then it owns the blame for the next unrelated startup oddity.
        //
        // DISCONNECTED, NOT MERELY HIDDEN. Four places carry this same note: the call site in
        // App.xaml.cs is commented out, the stored preference CenterSettings.FseStartsHelper is
        // commented out, Core/FseHelperStart.TryStartHelper returns before it does anything, and the
        // activation case at the bottom of this file is commented out.
        //
        // Row numbers 0..5 above are unchanged, so nothing that remembers an index moves.

        private void OpenCenterSettings()
        {
            LeaveLibrary();
            _view = View.CenterSettings;
            _centerSettingsIndex = 0;
            _languageListOpen = false;
            RenderCenterSettings();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void RenderCenterSettings()
        {
            BeginContent(centred: false);
            _centerSettingsRows.Clear();

            var stack = new StackPanel { MaxWidth = 940 };
            stack.Children.Add(new TextBlock
            {
                Text = Loc.T("Center settings"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 16, 16),
            });

            // Two columns, the same shape as the library's settings - two screens that do the same
            // job and look different are two screens to learn.
            var pairs = new UniformGrid { Columns = 2 };
            pairs.Children.Add(BuildCenterSettingRow(CenterSettingsLanguageRow, Loc.T("Language"),
                Loc.NameOf(Loc.Preference)));
            pairs.Children.Add(BuildCenterSettingRow(CenterSettingsFullscreenRow, Loc.T("Fullscreen"),
                null, WindowMode.IsFullscreen(this)));
            stack.Children.Add(pairs);

            // RIGHT HERE, under the row it belongs to, and that is the whole fix (user, 2026-09-15).
            //
            // This block used to be appended at the END of the screen, which was correct while
            // Language and Fullscreen were the only rows: the end of the screen WAS under the
            // language row. Then the update intervals and the experimental band arrived between
            // them, and the list kept unfolding at the bottom - far below the row that opened it,
            // past the bottom of the viewport, and therefore not operable with a pad at all: the
            // selection moved through something the user could not see.
            //
            // The lesson generalises: a control that belongs UNDER another one has to be added next
            // to it, not at the end of the builder. "Last" is only "below" until somebody appends
            // the next section.
            if (_languageListOpen) stack.Children.Add(BuildLanguageList());

            // Under the language row, and ONLY while the preference is "System": it is the one entry
            // whose result is not written on it. "System language" does not say which language that
            // turned out to be, and on a machine where the answer is English - which is every machine
            // we do not translate - the setting otherwise looks like it is not working.
            else if (Loc.Preference == UiLanguage.System)
                stack.Children.Add(new TextBlock
                {
                    Text = "→ " + Loc.NameOf(Loc.Current),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(2, 2, 0, 10),
                });

            stack.Children.Add(new TextBlock
            {
                Text = Loc.T("Check for updates and notify"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(2, 18, 0, 8),
            });

            var checks = new UniformGrid { Columns = 2 };
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsDriverCheckRow, Loc.T("Device drivers"),
                IntervalLabel(CenterSettings.DriverCheckIntervalWeeks)));
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsWindowsCheckRow, Loc.T("Windows Update"),
                IntervalLabel(CenterSettings.WindowsUpdateCheckIntervalWeeks)));

            // The two opt-ins sit UNDER the driver row, one column, with the empty cell doing the
            // pushing - the same shape as "Include test versions" under the widget row below. Their
            // value comes from the helper's last answer; until one has arrived the row says so.
            bool haveHelper = _driverResult != null && string.IsNullOrEmpty(_driverResult.Message);
            if (_driverResult == null && !_driversBusy) _ = RequestDriversAsync(force: false);
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsDriverBetaRow, Loc.T("Also non-WHQL graphics drivers"),
                haveHelper ? null : Loc.T("ClawTweaks is not running."), haveHelper ? _driverResult.UseIntelBeta : (bool?)null));
            checks.Children.Add(new Border());
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsDriverWifiRow, Loc.T("Modded Wi-Fi driver instead of stock (download only)"),
                haveHelper ? null : Loc.T("ClawTweaks is not running."), haveHelper ? _driverResult.UseModdedWifi : (bool?)null));
            checks.Children.Add(new Border());
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsDriverTestRow, Loc.T("Offer every driver as an update"),
                haveHelper ? null : Loc.T("ClawTweaks is not running."), haveHelper ? _driverResult.DriverTestMode : (bool?)null));
            checks.Children.Add(new Border());

            checks.Children.Add(BuildCenterSettingRow(CenterSettingsWidgetCheckRow, Loc.T("Gamebar Widget Releases"),
                IntervalLabel(CenterSettings.WidgetUpdateNotifyIntervalWeeks)));
            // "Include test versions" belongs UNDER the widget row, not beside it (user, 2026-09-15):
            // it is a sub-setting of that one check, and in the cell to the right it read as a
            // fourth, unrelated check. The empty cell is what pushes it down a row.
            checks.Children.Add(new Border());
            checks.Children.Add(BuildCenterSettingRow(CenterSettingsWidgetTestRow, Loc.T("Include test versions"),
                null, CenterSettings.WidgetNotifyTestBuilds));
            stack.Children.Add(checks);

            // The "read at every start" hint that stood here is gone (user, 2026-09-15).

            // "Experimental" was a band of its own here. REMOVED 2026-09-15 - see the note where
            // CenterSettingsFseStartRow used to be declared. It held one switch, it measured as
            // doing nothing, and the speed-up it was aiming at came from the scheduled task instead.

            ContentHost.Children.Add(stack);
            ApplyCenterSettingsSelection();
        }

        /// <summary>
        /// The unfolded language list.
        ///
        /// A LIST, NOT A WPF ComboBox. Center has no ComboBox anywhere - it is a gamepad interface
        /// built from panels, and a single drop-down would be the one control in the app that needs
        /// its own focus model. Unfolding in place gives what a drop-down is wanted FOR here: seeing
        /// at a glance which languages exist, instead of pressing a button five times to find out.
        ///
        /// TWO COLUMNS, and the left one decides the order: the English name, sorted, so the list
        /// reads the same whatever language is active - somebody who has landed in Korean by accident
        /// finds "German" where it was before. The right column is the language in its OWN script,
        /// which is the half that lets them recognise their own.
        /// </summary>
        private UIElement BuildLanguageList()
        {
            _languageRows.Clear();
            var list = new StackPanel { Margin = new Thickness(0, 2, 10, 0) };

            var order = LanguageOrder();
            for (int i = 0; i < order.Length; i++)
            {
                var lang = order[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new TextBlock
                {
                    Text = EnglishNameOf(lang),
                    FontSize = 16,
                    Foreground = UiHelpers.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var native = new TextBlock
                {
                    Text = Loc.NameOf(lang),
                    FontSize = 16,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(native, 1);
                grid.Children.Add(native);

                // The tick marks what is STORED, so "System" stays ticked rather than the language it
                // happens to resolve to - otherwise choosing System looks like it chose German.
                if (lang == Loc.Preference)
                {
                    var check = new TextBlock
                    {
                        Text = "\uE73E",
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 14,
                        Foreground = UiHelpers.Ok,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0),
                    };
                    Grid.SetColumn(check, 2);
                    grid.Children.Add(check);
                }

                var row = new Border
                {
                    Child = grid,
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    BorderThickness = new Thickness(2),
                    BorderBrush = i == _languageIndex ? UiHelpers.Accent : Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                int captured = i;
                row.MouseLeftButtonUp += (_, __) => { _languageIndex = captured; PickLanguage(); };
                _languageRows.Add(row);
                list.Children.Add(row);
            }

            return list;
        }

        /// <summary>System first because it is the default and the way back; the rest alphabetical by
        /// their ENGLISH name, which is the column the list is read down.</summary>
        private static UiLanguage[] LanguageOrder()
        {
            var rest = new List<UiLanguage>();
            foreach (var l in (UiLanguage[])Enum.GetValues(typeof(UiLanguage)))
                if (l != UiLanguage.System) rest.Add(l);

            rest.Sort((a, b) => string.Compare(EnglishNameOf(a), EnglishNameOf(b), StringComparison.Ordinal));

            var all = new List<UiLanguage> { UiLanguage.System };
            all.AddRange(rest);
            return all.ToArray();
        }

        /// <summary>The English name, never translated - it is the sort key and the stable half of
        /// each row. Loc.NameOf gives the other half, in that language own script.</summary>
        private static string EnglishNameOf(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return "German";
                case UiLanguage.French: return "French";
                case UiLanguage.Korean: return "Korean";
                case UiLanguage.Spanish: return "Spanish";
                case UiLanguage.Russian: return "Russian";
                case UiLanguage.Greek: return "Greek";
                // The parenthesis is what makes these sort next to each other in the list, which is
                // where a reader looking for one of them expects to find the other.
                case UiLanguage.ChineseSimplified: return "Chinese (Simplified)";
                case UiLanguage.ChineseTraditional: return "Chinese (Traditional)";
                case UiLanguage.Italian: return "Italian";
                case UiLanguage.Portuguese: return "Portuguese (Brazil)";
                case UiLanguage.Japanese: return "Japanese";
                case UiLanguage.Polish: return "Polish";
                case UiLanguage.English: return "English";
                default: return "System";
            }
        }

        private void PickLanguage()
        {
            var order = LanguageOrder();
            if (_languageIndex >= 0 && _languageIndex < order.Length)
                Loc.Set(order[_languageIndex]);

            _languageListOpen = false;

            // The whole window, not just this screen: the footer chips, the tab strip and the header
            // chip are all drawn in the old language and none of them redraw on their own. Half a
            // translated window reads as a broken translation.
            //
            // The device banner belongs to that list and was missing from it (reported 2026-09-15):
            // its second line is translated when the banner is built, and it is built once at
            // startup, so it kept the old language until Center was restarted. Drawn from the cached
            // detection result — this must not re-probe the hardware to re-translate a line, and it
            // goes BEFORE RenderCenterSettings so this screen is the last thing rendered.
            if (_lastDeviceDetect != null) RenderDeviceBanner(_lastDeviceDetect);

            RenderCenterSettings();
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>One row: title on the left, a switch or the current value on the right. Same
        /// shape as the library's BuildSettingRow, kept separate because that one owns the library
        /// screen's row list and index.</summary>
        private Border BuildCenterSettingRow(int index, string title, string valueText, bool? on = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            UIElement state = on.HasValue
                ? BuildToggle(on.Value)
                : new TextBlock
                {
                    Text = valueText ?? string.Empty,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                };
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

            var row = new Border
            {
                Child = grid,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 10, 10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
            };
            row.MouseLeftButtonUp += (_, __) => { _centerSettingsIndex = index; ActivateCenterSetting(); };
            _centerSettingsRows.Add(row);
            return row;
        }

        /// <summary>
        /// Keeps the highlighted language on screen while the pad walks the list.
        ///
        /// Thirteen languages plus System is taller than the viewport once the update bands are
        /// above it, so without this the selection walks off the bottom edge and the list is again
        /// something a pad cannot operate - the same complaint that moved the list up in the first
        /// place, just further down the list instead of at the start of it.
        ///
        /// At Loaded priority because RenderCenterSettings has just rebuilt every row: the element
        /// exists but has no layout yet, and BringIntoView on a thing with no position scrolls
        /// nowhere. Same shape as the friends list (CenterMenuWindow.Friends.cs).
        /// </summary>
        private void ScrollLanguageRowIntoView()
        {
            if (!_languageListOpen) return;
            if (_languageIndex < 0 || _languageIndex >= _languageRows.Count) return;

            var target = _languageRows[_languageIndex];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { target.BringIntoView(); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyCenterSettingsSelection()
        {
            foreach (var row in _centerSettingsRows)
                row.BorderBrush = row.Tag is int i && i == _centerSettingsIndex
                    ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveCenterSettingsSelection(PadButton dir)
        {
            // While the list is unfolded it owns the D-pad. Up/Down walk it and nothing reaches the
            // two settings rows behind it, which is what an open drop-down does everywhere else.
            if (_languageListOpen)
            {
                int count = LanguageOrder().Length;
                int move = dir == PadButton.Up ? -1 : dir == PadButton.Down ? 1 : 0;
                if (move == 0) return;

                int target = _languageIndex + move;
                if (target < 0 || target >= count) return;

                _languageIndex = target;
                RenderCenterSettings();
                ScrollLanguageRowIntoView();
                return;
            }

            if (_centerSettingsRows.Count == 0) return;

            int last = _centerSettingsRows.Count - 1;
            int next = _centerSettingsIndex;

            // A two-column GRID since the update intervals moved in: Left/Right step one cell,
            // Up/Down a whole row. Both UniformGrids on this screen are two wide, so one stride
            // covers them - if a third grid ever arrives with a different width, this is the line
            // that has to know.
            const int stride = 2;
            // The tail from the widget row down is ONE column (the test toggle sits under the widget
            // row, its right-hand cell is empty), so there Up/Down step one and Left/Right nothing.
            bool inTail = _centerSettingsIndex >= CenterSettingsTailStart;
            if (dir == PadButton.Left) { if (inTail) return; next--; }
            else if (dir == PadButton.Right) { if (inTail) return; next++; }
            else if (dir == PadButton.Up) next -= (_centerSettingsIndex > CenterSettingsTailStart) ? 1 : stride;
            else if (dir == PadButton.Down) next += inTail ? 1 : stride;
            else return;

            // Down from the right-hand cell above the tail lands on the tail's first row, not past it.
            if (dir == PadButton.Down && !inTail && next > CenterSettingsTailStart) next = CenterSettingsTailStart;

            // Down from the last row lands on the last cell rather than nowhere: with an odd number
            // of rows the cell below is missing, and refusing the press reads as a dead d-pad.
            if (next > last && dir == PadButton.Down) next = last;

            if (next < 0 || next > last || next == _centerSettingsIndex) return;

            _centerSettingsIndex = next;
            ApplyCenterSettingsSelection();
            RefreshActionBar();
        }

        private void ActivateCenterSetting()
        {
            switch (_centerSettingsIndex)
            {
                case CenterSettingsLanguageRow:
                    if (_languageListOpen) { PickLanguage(); return; }

                    // Opens ON the stored choice, not at the top: the list is opened to change
                    // something, and starting anywhere else means the first thing the user has to do
                    // is find where they already are.
                    _languageIndex = Math.Max(0, Array.IndexOf(LanguageOrder(), Loc.Preference));
                    _languageListOpen = true;
                    RenderCenterSettings();
                    ScrollLanguageRowIntoView();
                    RefreshActionBar();
                    return;

                case CenterSettingsFullscreenRow:
                    WindowMode.Toggle(this);
                    break;

                case CenterSettingsDriverCheckRow:
                    CenterSettings.DriverCheckIntervalWeeks =
                        NextInterval(CenterSettings.DriverCheckIntervalWeeks);
                    break;

                case CenterSettingsWindowsCheckRow:
                    CenterSettings.WindowsUpdateCheckIntervalWeeks =
                        NextInterval(CenterSettings.WindowsUpdateCheckIntervalWeeks);
                    break;

                case CenterSettingsWidgetCheckRow:
                    CenterSettings.WidgetUpdateNotifyIntervalWeeks =
                        NextInterval(CenterSettings.WidgetUpdateNotifyIntervalWeeks);
                    break;

                case CenterSettingsWidgetTestRow:
                    CenterSettings.WidgetNotifyTestBuilds = !CenterSettings.WidgetNotifyTestBuilds;
                    break;

                case CenterSettingsDriverBetaRow:
                    if (_driverResult != null) SetDriverOptIn("SetUseIntelBeta", !_driverResult.UseIntelBeta);
                    return;   // the pipe answer redraws this screen

                case CenterSettingsDriverWifiRow:
                    if (_driverResult != null) SetDriverOptIn("SetUseModdedWifi", !_driverResult.UseModdedWifi);
                    return;

                case CenterSettingsDriverTestRow:
                    if (_driverResult != null) SetDriverOptIn("SetDriverTestMode", !_driverResult.DriverTestMode);
                    return;

                // Removed 2026-09-15 with the experimental band - see the note where the row
                // constants are declared, at the top of this file.
                //
                // case CenterSettingsFseStartRow:
                //     CenterSettings.FseStartsHelper = !CenterSettings.FseStartsHelper;
                //     break;

            }

            RenderCenterSettings();
            RefreshActionBar();
        }
    }
}
