using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// ONE settings screen with a sidebar of topics (user, 2026-10-03: "a huge mess"). It replaced
    /// two screens - the library's eighteen cells in a three-wide grid with a special bottom band,
    /// and Center's own settings drawn into ContentHost - with a single place:
    ///
    ///   left    the topics, one per line
    ///   right   the rows of the selected topic, one column, each with what it does UNDER its title
    ///
    /// Every row keeps its id from CenterMenuWindow.Library.cs (SettingsXxxRow); the topics are
    /// only an ORDER over those ids, so ActivateSetting, the action bar and every sub-screen that
    /// returns "onto the row it was opened from" did not have to change. Center's rows joined that id
    /// space at <see cref="SettingsCenterBase"/> and keep their old activation code in
    /// CenterMenuWindow.CenterSettings.cs.
    ///
    /// ── WHERE IT LIVES ──────────────────────────────────────────────────────────────────────────
    /// In the library host, as the library settings did. That is reachable with or without
    /// ClawTweaks installed - Home's tile already opened the library first and the settings after -
    /// so Language is still there for somebody reading install instructions.
    ///
    /// ── THE PAD ────────────────────────────────────────────────────────────────────────────────
    /// Two columns of focus. In the sidebar Up/Down choose a topic and its rows show at once;
    /// Right or A goes into them. In the rows Up/Down walk them, Left or B goes back to the
    /// sidebar. B in the sidebar closes the screen. LB/RB change topic from anywhere.
    /// </summary>
    public partial class CenterMenuWindow
    {
        // ── Center's rows, in the same id space as the library's ────────────────────────────────
        // id - SettingsCenterBase is the old CenterSettingsXxxRow number, which is what
        // ActivateCenterSetting still switches on.
        private const int SettingsCenterBase = 100;
        private const int SettingsLanguageRow = SettingsCenterBase + CenterSettingsLanguageRow;
        private const int SettingsFullscreenRow = SettingsCenterBase + CenterSettingsFullscreenRow;
        private const int SettingsDriverCheckRow = SettingsCenterBase + CenterSettingsDriverCheckRow;
        private const int SettingsWindowsCheckRow = SettingsCenterBase + CenterSettingsWindowsCheckRow;
        private const int SettingsDriverBetaRow = SettingsCenterBase + CenterSettingsDriverBetaRow;
        private const int SettingsDriverWifiRow = SettingsCenterBase + CenterSettingsDriverWifiRow;
        private const int SettingsDriverTestRow = SettingsCenterBase + CenterSettingsDriverTestRow;
        private const int SettingsWidgetCheckRow = SettingsCenterBase + CenterSettingsWidgetCheckRow;
        private const int SettingsWidgetTestRow = SettingsCenterBase + CenterSettingsWidgetTestRow;

        private static bool IsCenterSettingRow(int id) => id >= SettingsCenterBase;

        private sealed class SettingsTopic
        {
            public string Title;
            public int[] Rows;
        }

        /// <summary>
        /// The topics and what is in them, in order (agreed with the user 2026-10-03). General and
        /// Updates are Center's former screen; the five between are the former library screen.
        /// </summary>
        private static readonly SettingsTopic[] SettingsTopics =
        {
            new SettingsTopic { Title = "General", Rows = new[] { SettingsLanguageRow, SettingsFullscreenRow } },
            new SettingsTopic { Title = "Start and behaviour", Rows = new[] {
                SettingsStartInLibraryRow, SettingsStartWithClawTweaksRow, SettingsStartSteamRow,
                SettingsRunInBackgroundRow, SettingsLaunchBehaviorRow } },
            new SettingsTopic { Title = "Library", Rows = new[] {
                SettingsTabsRow, SettingsHiddenGamesRow, SettingsOwnAppsInRecentRow, SettingsInfoRow } },
            new SettingsTopic { Title = "Appearance", Rows = new[] {
                SettingsDenseGridRow, SettingsImmersiveRow, SettingsReflectionsRow, SettingsSquareRomArtRow,
                SettingsBackgroundRow, SettingsUserImagesRow } },
            new SettingsTopic { Title = "Sound", Rows = new[] { SettingsSoundRow } },
            new SettingsTopic { Title = "Accounts and services", Rows = new[] { SettingsAccountsRow, SettingsKeyRow } },
            new SettingsTopic { Title = "Updates and notifications", Rows = new[] {
                SettingsDriverCheckRow, SettingsDriverBetaRow, SettingsDriverWifiRow, SettingsDriverTestRow,
                SettingsWindowsCheckRow, SettingsWidgetCheckRow, SettingsWidgetTestRow } },
        };

        private const int SettingsTopicGeneral = 0;

        private int _settingsTopic;
        /// <summary>The cursor is in the sidebar, not in the rows.</summary>
        private bool _settingsInSidebar = true;
        private readonly List<Border> _settingsTopicRows = new List<Border>();
        private ScrollViewer _settingsScroller;

        /// <summary>Opened from Home rather than from the library: B leaves to Home again.</summary>
        private bool _settingsFromHome;

        private static int TopicOf(int rowId)
        {
            for (int t = 0; t < SettingsTopics.Length; t++)
                if (Array.IndexOf(SettingsTopics[t].Rows, rowId) >= 0) return t;
            return 0;
        }

        /// <summary>Puts the cursor onto one row - for the sub-screens that come back "onto the row
        /// they were opened from", and for Home's tiles that open a topic.</summary>
        private void SelectSettingsRow(int rowId)
        {
            _settingsIndex = rowId;
            _settingsTopic = TopicOf(rowId);
            _settingsInSidebar = false;
        }

        private void RenderLibrarySettings()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _settingsRows.Clear();
            _settingsTopicRows.Clear();
            _artKeyStatusPinned = false;
            _artKeyBox = null;
            _artKeyStatus = null;

            if (_settingsTopic < 0 || _settingsTopic >= SettingsTopics.Length) _settingsTopic = 0;
            var topic = SettingsTopics[_settingsTopic];
            if (Array.IndexOf(topic.Rows, _settingsIndex) < 0) _settingsIndex = topic.Rows[0];

            // PINNED LEFT, full width (user, 2026-10-03). Centred with a MaxWidth, the whole block -
            // sidebar included - moved sideways whenever a topic's rows were wider or narrower than
            // the last one's, so the topics jumped under the cursor. The sidebar now has a fixed
            // place and only the right column takes what the rows need.
            var outer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(24, 18, 24, 10),
            };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = Loc.T("Settings"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 14),
            };
            Grid.SetColumnSpan(title, 2);
            outer.Children.Add(title);

            // ── the sidebar ──
            var side = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
            for (int t = 0; t < SettingsTopics.Length; t++)
                side.Children.Add(BuildSettingsTopicRow(t));
            Grid.SetRow(side, 1);
            outer.Children.Add(side);

            // ── the rows of this topic ──
            // Left-aligned in its column with a fixed width, so a short topic does not centre itself
            // somewhere else than a long one.
            var rows = new StackPanel { Width = 760, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (int id in topic.Rows)
            {
                rows.Children.Add(BuildSettingsRowFor(id));
                // The language list unfolds UNDER its row, and nowhere else - see the note on
                // BuildLanguageList's caller history in CenterMenuWindow.CenterSettings.cs.
                if (id == SettingsLanguageRow && _languageListOpen) rows.Children.Add(BuildLanguageList());
            }
            _settingsScroller = new ScrollViewer
            {
                Content = rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetRow(_settingsScroller, 1);
            Grid.SetColumn(_settingsScroller, 1);
            outer.Children.Add(_settingsScroller);

            // ── the status line: why a row is fixed, and the key check ──
            _settingsHint = new TextBlock { FontSize = 14, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap };
            var hintLine = new Grid { MinHeight = 28, Margin = new Thickness(0, 6, 0, 0) };
            hintLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hintLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hintLine.Children.Add(_settingsHint);
            if (_artKeyStatus != null)
            {
                Grid.SetColumn(_artKeyStatus, 1);
                hintLine.Children.Add(_artKeyStatus);
            }
            Grid.SetRow(hintLine, 2);
            Grid.SetColumn(hintLine, 1);
            outer.Children.Add(hintLine);

            LibraryRoot.Children.Add(outer);
            ApplySettingsSelection();
        }

        private Border BuildSettingsTopicRow(int t)
        {
            bool current = t == _settingsTopic;
            var row = new Border
            {
                Child = new TextBlock
                {
                    Text = Loc.T(SettingsTopics[t].Title),
                    FontSize = 17,
                    Foreground = current ? UiHelpers.Text : UiHelpers.Subtle,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                // The current topic keeps a card behind it while the cursor is in the rows, so it
                // is clear whose rows these are.
                Background = current ? UiHelpers.Card : Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = t,
            };
            row.MouseLeftButtonUp += (_, __) =>
            {
                _settingsTopic = t;
                _settingsInSidebar = true;
                _languageListOpen = false;
                RenderLibrarySettings();
                RefreshActionBar();
            };
            _settingsTopicRows.Add(row);
            return row;
        }

        /// <summary>One row by id: its title, its state, and what it does under the title.</summary>
        private Border BuildSettingsRowFor(int id)
        {
            Border row;
            switch (id)
            {
                case SettingsStartInLibraryRow: row = BuildSettingRow(id, "Start in the library", CenterSettings.OpenLibraryAtStartup, null); break;
                case SettingsStartWithClawTweaksRow: row = BuildSettingRow(id, "Start Center with ClawTweaks", CenterSettings.StartCenterWithClawTweaks, null); break;
                case SettingsStartSteamRow: row = BuildSettingRow(id, "Start Steam with the library", CenterSettings.StartSteamWithLibrary, null); break;
                case SettingsRunInBackgroundRow: row = BuildSettingRow(id, "Run in background", CenterSettings.RunInBackground, null); break;
                case SettingsLaunchBehaviorRow: row = BuildSettingRow(id, "After starting a game", null, LaunchBehaviorLabel(CenterSettings.LaunchBehavior)); break;
                case SettingsTabsRow: row = BuildSettingRow(id, "Tabs order and visibility", null, TabsSummary()); break;
                case SettingsDenseGridRow: row = BuildSettingRow(id, "Denser grid", CenterSettings.DenseLibraryGrid, null); break;
                case SettingsImmersiveRow: row = BuildSettingRow(id, "Recent immersive", CenterSettings.ImmersiveMode, null); break;
                case SettingsReflectionsRow: row = BuildSettingRow(id, "Recent reflections", CenterSettings.RecentReflections, null); break;
                case SettingsSquareRomArtRow: row = BuildSettingRow(id, "Square ROM art", _squareRomArt, null); break;
                case SettingsUserImagesRow: row = BuildSettingRow(id, "Your images", null, UserImagesSummary()); break;
                case SettingsBackgroundRow: row = BuildSettingRow(id, "Center background", null, BackgroundSummary()); break;
                case SettingsSoundRow: row = BuildSettingRow(id, "Sound settings", null, null); break;
                case SettingsOwnAppsInRecentRow: row = BuildSettingRow(id, "Show own apps in Recent", CenterSettings.ShowOwnAppsInRecent, null); break;
                case SettingsHiddenGamesRow: row = BuildSettingRow(id, "Hidden games", null, HiddenGamesSummary()); break;
                case SettingsAccountsRow: row = BuildSettingRow(id, "Accounts", null, AccountsSummary()); break;
                case SettingsInfoRow: row = BuildSettingRow(id, "Library help", null, null); break;
                case SettingsKeyRow: row = BuildKeySettingRow(); break;

                case SettingsLanguageRow: row = BuildSettingRow(id, "Language", null, Loc.NameOf(Loc.Preference)); break;
                case SettingsFullscreenRow: row = BuildSettingRow(id, "Fullscreen", WindowMode.IsFullscreen(this), null); break;
                case SettingsDriverCheckRow: row = BuildSettingRow(id, "Device drivers", null, IntervalLabel(CenterSettings.DriverCheckIntervalWeeks)); break;
                case SettingsWindowsCheckRow: row = BuildSettingRow(id, "Windows Update", null, IntervalLabel(CenterSettings.WindowsUpdateCheckIntervalWeeks)); break;
                case SettingsWidgetCheckRow: row = BuildSettingRow(id, "Gamebar Widget Releases", null, IntervalLabel(CenterSettings.WidgetUpdateNotifyIntervalWeeks)); break;
                case SettingsWidgetTestRow: row = BuildSettingRow(id, "Include test versions", CenterSettings.WidgetNotifyTestBuilds, null); break;
                case SettingsDriverBetaRow:
                case SettingsDriverWifiRow:
                case SettingsDriverTestRow:
                {
                    // Helper state, read from the driver result (see CenterMenuWindow.CenterSettings.cs).
                    bool haveHelper = _driverResult != null && string.IsNullOrEmpty(_driverResult.Message);
                    if (_driverResult == null && !_driversBusy) _ = RequestDriversAsync(force: false);
                    string t = id == SettingsDriverBetaRow ? "Also non-WHQL graphics drivers"
                             : id == SettingsDriverWifiRow ? "Modded Wi-Fi driver instead of stock (download only)"
                             : "Offer every driver as an update";
                    bool? on = !haveHelper ? (bool?)null
                             : id == SettingsDriverBetaRow ? _driverResult.UseIntelBeta
                             : id == SettingsDriverWifiRow ? _driverResult.UseModdedWifi
                             : _driverResult.DriverTestMode;
                    row = BuildSettingRow(id, t, on, haveHelper ? null : Loc.T("ClawTweaks is not running."));
                    break;
                }
                default: row = BuildSettingRow(id, "?", null, null); break;
            }

            // What it does, UNDER the title - one column has the width a grid cell did not.
            string what = SettingDescription(id);
            if (!string.IsNullOrEmpty(what) && row.Child is Grid g && g.Children.Count > 0 && g.Children[0] is StackPanel left)
                left.Children.Add(new TextBlock
                {
                    Text = Loc.T(what),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });

            // "System" says nothing about which language that turned out to be - see the old
            // screen's note: on a machine we do not translate the setting otherwise looks broken.
            if (id == SettingsLanguageRow && Loc.Preference == UiLanguage.System
                && row.Child is Grid lg && lg.Children.Count > 0 && lg.Children[0] is StackPanel ll)
                ll.Children.Add(new TextBlock { Text = "→ " + Loc.NameOf(Loc.Current), FontSize = 13, Foreground = UiHelpers.Subtle });

            row.Margin = new Thickness(0, 0, 0, 8);
            return row;
        }

        /// <summary>The SteamGridDB key: title on the left, the box on the right, the check result on
        /// the status line under the rows.</summary>
        private Border BuildKeySettingRow()
        {
            var keyRow = BuildSettingRow(SettingsKeyRow, "SteamGridDB key", null, null);
            var keyGrid = (Grid)keyRow.Child;
            _artKeyBox = new TextBox
            {
                Text = CenterSettings.SteamGridDbApiKey,
                FontSize = 15,
                Width = 280,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_artKeyBox, 1);
            keyGrid.Children.Add(_artKeyBox);
            _artKeyStatus = new TextBlock
            {
                Text = Loc.T(Library.SteamGridDb.HasKey ? "Set. Covers are downloaded for games with none." : "Not set."),
                FontSize = 14,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(16, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Visibility = Visibility.Collapsed,
            };
            return keyRow;
        }

        private void ApplySettingsSelection()
        {
            Border selected = null;
            foreach (var row in _settingsRows)
            {
                bool sel = !_settingsInSidebar && row.Tag is int i && i == _settingsIndex;
                row.BorderBrush = sel ? UiHelpers.Accent : Brushes.Transparent;
                if (sel) selected = row;
            }
            foreach (var row in _settingsTopicRows)
                row.BorderBrush = _settingsInSidebar && row.Tag is int t && t == _settingsTopic ? UiHelpers.Accent : Brushes.Transparent;

            if (_settingsHint != null)
                _settingsHint.Text = !_settingsInSidebar && IsSettingLockedInFse(_settingsIndex)
                    ? Loc.T("Fixed while Center is the Windows full-screen start app.") : string.Empty;

            if (_artKeyStatus != null && !_artKeyStatusPinned)
                _artKeyStatus.Visibility = !_settingsInSidebar && _settingsIndex == SettingsKeyRow ? Visibility.Visible : Visibility.Collapsed;

            if (selected != null)
            {
                var target = selected;
                Dispatcher.BeginInvoke(new Action(() => { try { target.BringIntoView(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void MoveSettingsSelection(PadButton dir)
        {
            // The unfolded language list owns the D-pad, as it did on the old screen.
            if (_languageListOpen)
            {
                int count = LanguageOrder().Length;
                int move = dir == PadButton.Up ? -1 : dir == PadButton.Down ? 1 : 0;
                if (move == 0) return;
                int target = _languageIndex + move;
                if (target < 0 || target >= count) return;
                _languageIndex = target;
                RenderLibrarySettings();
                ScrollLanguageRowIntoView();
                return;
            }

            if (_settingsInSidebar)
            {
                switch (dir)
                {
                    case PadButton.Up:
                    case PadButton.Down:
                        int next = _settingsTopic + (dir == PadButton.Down ? 1 : -1);
                        if (next < 0 || next >= SettingsTopics.Length) return;
                        _settingsTopic = next;
                        _settingsIndex = SettingsTopics[next].Rows[0];
                        RenderLibrarySettings();
                        RefreshActionBar();
                        return;
                    case PadButton.Right:
                        EnterSettingsRows();
                        return;
                    default:
                        return;
                }
            }

            var rows = SettingsTopics[_settingsTopic].Rows;
            int at = Array.IndexOf(rows, _settingsIndex);
            switch (dir)
            {
                case PadButton.Left:
                    LeaveSettingsRows();
                    return;
                case PadButton.Up:
                    if (at <= 0) return;
                    _settingsIndex = rows[at - 1];
                    break;
                case PadButton.Down:
                    if (at < 0 || at >= rows.Length - 1) return;
                    _settingsIndex = rows[at + 1];
                    break;
                default:
                    return;
            }
            ApplySettingsSelection();
            RefreshActionBar();
        }

        private void EnterSettingsRows()
        {
            _settingsInSidebar = false;
            var rows = SettingsTopics[_settingsTopic].Rows;
            if (Array.IndexOf(rows, _settingsIndex) < 0) _settingsIndex = rows[0];
            ApplySettingsSelection();
            RefreshActionBar();
        }

        private void LeaveSettingsRows()
        {
            _settingsInSidebar = true;
            ApplySettingsSelection();
            RefreshActionBar();
        }

        /// <summary>LB/RB: the previous or next topic, wherever the cursor is.</summary>
        private void CycleSettingsTopic(int step)
        {
            int next = _settingsTopic + step;
            if (next < 0 || next >= SettingsTopics.Length) return;
            _languageListOpen = false;
            _settingsTopic = next;
            _settingsIndex = SettingsTopics[next].Rows[0];
            RenderLibrarySettings();
            RefreshActionBar();
        }

        /// <summary>True while the settings screen itself is up, not one of the screens behind its
        /// rows - those have their own pad handling.</summary>
        private bool SettingsTopLevelOpen =>
            _settingsOpen && !_soundSettingsOpen && !_tabEditorOpen && !_hiddenGamesOpen && !_accountsOpen;

        /// <summary>A on the selected thing: a topic opens its rows; a row does what it does.</summary>
        private void ActivateSettingsSelection()
        {
            if (_settingsInSidebar) { EnterSettingsRows(); return; }
            ActivateSetting();
        }

        /// <summary>B: closes the language list, else back to the sidebar, else leaves.</summary>
        private void SettingsBack()
        {
            if (_languageListOpen)
            {
                _languageListOpen = false;
                RenderLibrarySettings();
                RefreshActionBar();
                return;
            }
            if (!_settingsInSidebar) { LeaveSettingsRows(); return; }
            SaveArtKeyAndClose();
        }

        /// <summary>Center's rows: the old activation, which re-renders through
        /// RenderSettingsAfterCenterChange.</summary>
        private void ActivateCenterSettingRow(int id)
        {
            _centerSettingsIndex = id - SettingsCenterBase;
            ActivateCenterSetting();
        }

        /// <summary>Whatever shows Center's rows redraws - since 2026-10-03 that is this screen.</summary>
        private void RenderSettingsAfterCenterChange()
        {
            if (_settingsOpen && _view == View.Library) RenderLibrarySettings();
        }

        /// <summary>Home's tiles: straight into one topic of this screen.</summary>
        private void OpenSettingsFromHome(int topic)
        {
            OpenLibrary();
            OpenLibrarySettings();
            _settingsFromHome = true;
            _settingsTopic = topic;
            _settingsIndex = SettingsTopics[topic].Rows[0];
            _settingsInSidebar = true;
            RenderLibrarySettings();
            RefreshActionBar();
        }
    }
}
