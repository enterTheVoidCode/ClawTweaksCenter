using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The achievement history: the user's own last unlocks across Steam, Xbox and Epic, newest
    /// first, at most <see cref="AchievementHistory.Max"/>. Opened from the pill at the right end of
    /// Recent's title row (X).
    ///
    /// It owns the library area the way the friends screen does - the tab strip goes, LB/RB do
    /// nothing, B comes back to the shelf - and it is built the same way: a flag the library's
    /// render, navigation and action bar ask before anything else.
    ///
    /// The rows are the achievement list's own rows (BuildAchievementLine) with the game's name
    /// above the achievement's, so a history entry and the same achievement in its game's list
    /// cannot look different.
    ///
    /// ROMS APART (user, 2026-10-03). With RetroAchievements connected the screen has two columns:
    /// the PC stores on the left, RetroAchievements on the right, each newest first with its own
    /// cursor; Left and Right move between them. Not one mixed list - ROMs are kept separate
    /// everywhere in Center, and the width is there for it.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _achHistoryOpen;
        private bool _achHistoryLoading;
        private List<AchievementHistoryEntry> _achHistory = new List<AchievementHistoryEntry>();
        private readonly List<Border> _achHistoryRows = new List<Border>();
        private int _achHistoryIndex;
        private CancellationTokenSource _achHistoryCts;

        /// <summary>The RetroAchievements column. Only while that account is connected.</summary>
        private bool _raHistoryShown;
        private bool _raHistoryLoading;
        private List<Library.Accounts.RetroAchievementsAchievements.RecentUnlock> _raHistory = new List<Library.Accounts.RetroAchievementsAchievements.RecentUnlock>();
        private readonly List<Border> _raHistoryRows = new List<Border>();
        private int _raHistoryIndex;
        /// <summary>0 = the PC stores, 1 = RetroAchievements.</summary>
        private int _achHistoryColumn;

        /// <summary>The pill sits in Recent only: it is the shelf about what was played lately.</summary>
        private bool LibraryTabOffersHistory => _libraryGroup == LibraryGroup.Recent;

        /// <summary>The pill at the right end of Recent's title row.</summary>
        private UIElement BuildHistoryPill()
        {
            // The key and the trophy only, no words (user, 2026-10-03): the title row already names
            // the game, and a label beside it competed with it. The name stays as a tooltip.
            var pill = BuildCornerChip("X", "\uEB95", "", OpenAchievementHistory);
            if (pill is Border b)
            {
                // A real pill here, not the corner's bare chip: it sits on the title row beside a
                // 26 px headline and has to read as a control rather than as part of the subline.
                b.Background = UiHelpers.Card;
                b.CornerRadius = new CornerRadius(16);
                b.Padding = new Thickness(6, 5, 4, 5);
                b.ToolTip = Core.Loc.T("Achievement history");
            }
            return pill;
        }

        private void OpenAchievementHistory()
        {
            if (_launchPrompt != LaunchPrompt.None || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            if (_achHistoryOpen || _friendsOpen || _infoOpen || _exitPromptOpen || _letterBarOpen) return;

            _achHistoryOpen = true;
            _achHistoryLoading = true;
            _achHistory = new List<AchievementHistoryEntry>();
            _achHistoryIndex = 0;
            _raHistoryShown = Library.Accounts.RetroAchievementsAccount.IsSignedIn;
            _raHistoryLoading = _raHistoryShown;
            _raHistory = new List<Library.Accounts.RetroAchievementsAchievements.RecentUnlock>();
            _raHistoryIndex = 0;
            _achHistoryColumn = 0;
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
            LoadAchievementHistory();
        }

        private void LoadAchievementHistory()
        {
            _achHistoryCts?.Cancel();
            var cts = new CancellationTokenSource();
            _achHistoryCts = cts;

            // Installed and not installed: a Steam game played last year and uninstalled since still
            // has its unlocks on this disk.
            var games = new List<GameEntry>(_library.Games);
            if (_library.NotInstalledLoaded) games.AddRange(_library.ForGroup(LibraryGroup.NotInstalled));

            _ = AchievementHistory.BuildAsync(games, cts.Token).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_achHistoryCts != cts || !_achHistoryOpen) return;
                    _achHistoryLoading = false;
                    _achHistory = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<AchievementHistoryEntry>();
                    if (t.IsFaulted) Core.InstallLog.Write("[Achievements] history failed: " + t.Exception?.GetBaseException().Message);
                    _achHistoryIndex = Math.Min(_achHistoryIndex, Math.Max(0, _achHistory.Count - 1));
                    RenderLibrary();
                    RefreshActionBar();
                }));
            }, System.Threading.Tasks.TaskScheduler.Default);

            if (!_raHistoryShown) return;
            _ = Library.Accounts.RetroAchievementsAchievements.RecentUnlocksAsync(AchievementHistory.Max, cts.Token).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_achHistoryCts != cts || !_achHistoryOpen) return;
                    _raHistoryLoading = false;
                    _raHistory = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<Library.Accounts.RetroAchievementsAchievements.RecentUnlock>();
                    if (t.IsFaulted) Core.InstallLog.Write("[Achievements] RetroAchievements history failed: " + t.Exception?.GetBaseException().GetType().Name);
                    else if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) Core.InstallLog.Write("[Achievements] RetroAchievements history: " + _raHistory.Count + " shown");
                    _raHistoryIndex = Math.Min(_raHistoryIndex, Math.Max(0, _raHistory.Count - 1));
                    RenderLibrary();
                    RefreshActionBar();
                }));
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void CloseAchievementHistory()
        {
            ResetAchievementHistoryState();
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>Everything that takes the library down at once calls this.</summary>
        private void ResetAchievementHistoryState()
        {
            _achHistoryCts?.Cancel();
            _achHistoryCts = null;
            _achHistoryOpen = false;
            _achHistoryLoading = false;
            _achHistory = new List<AchievementHistoryEntry>();
            _achHistoryRows.Clear();
            _achHistoryIndex = 0;
            _raHistoryShown = false;
            _raHistoryLoading = false;
            _raHistory = new List<Library.Accounts.RetroAchievementsAchievements.RecentUnlock>();
            _raHistoryRows.Clear();
            _raHistoryIndex = 0;
            _achHistoryColumn = 0;
        }

        private void RenderAchievementHistory()
        {
            _liveRows.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _achHistoryRows.Clear();
            _raHistoryRows.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 10), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Achievement history"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 4),
            });
            head.Children.Add(new TextBlock
            {
                Text = _raHistoryShown
                    ? Core.Loc.T("Your last unlocks, newest first. Left and right switch between the PC stores and RetroAchievements.")
                    : _achHistoryLoading
                        ? Core.Loc.T("Collecting your achievements...")
                        : Core.Loc.F("Your last {0} unlocks from Steam, Xbox and Epic, newest first.", _achHistory.Count),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            var corner = BuildAccountsCorner(withRetro: true);
            ((FrameworkElement)corner).Margin = new Thickness(0, 14, LibOuterMargin, 0);
            Grid.SetRow(corner, 0);
            LibraryRoot.Children.Add(corner);

            UIElement body;
            if (!_raHistoryShown)
            {
                // One column, as before RetroAchievements: the PC stores at reading width.
                var pc = BuildPcHistoryColumn();
                body = new Border
                {
                    Child = pc,
                    Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12),
                    MaxWidth = 900,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                if (_achHistoryLoading || _achHistory.Count == 0)
                {
                    // The spinner and the empty text sit in the middle of the screen, not of the column.
                    ((Border)body).MaxWidth = double.PositiveInfinity;
                    ((Border)body).HorizontalAlignment = HorizontalAlignment.Stretch;
                }
            }
            else
            {
                // Two equal columns with a gap; each one scrolls on its own.
                var grid = new Grid { Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var pcHead = BuildHistoryColumnHead(Core.Loc.T("PC stores"), _achHistoryLoading ? null : (int?)_achHistory.Count, _achHistoryColumn == 0, null);
                Grid.SetColumn(pcHead, 0);
                grid.Children.Add(pcHead);
                var raHead = BuildHistoryColumnHead("RetroAchievements", _raHistoryLoading ? null : (int?)_raHistory.Count, _achHistoryColumn == 1,
                    Library.StoreIcons.VectorFor(LibraryGroup.Roms, _achHistoryColumn == 1 ? UiHelpers.Text : UiHelpers.Subtle, 16));
                Grid.SetColumn(raHead, 2);
                grid.Children.Add(raHead);

                var pc = BuildPcHistoryColumn();
                Grid.SetRow(pc, 1);
                Grid.SetColumn(pc, 0);
                grid.Children.Add(pc);
                var ra = BuildRetroHistoryColumn();
                Grid.SetRow(ra, 1);
                Grid.SetColumn(ra, 2);
                grid.Children.Add(ra);
                body = grid;
            }
            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyAchievementHistorySelection();
        }

        /// <summary>"PC stores  100" over a column, underlined in the accent while the cursor is in it.</summary>
        private static UIElement BuildHistoryColumnHead(string name, int? count, bool active, UIElement icon)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 6) };
            if (icon is FrameworkElement fe)
            {
                fe.Width = 16;
                fe.Height = 16;
                fe.Margin = new Thickness(0, 0, 8, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(icon);
            }
            row.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = active ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (count.HasValue)
                row.Children.Add(new TextBlock
                {
                    Text = count.Value.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                });
            return new Border
            {
                Child = row,
                BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
            };
        }

        private static UIElement HistoryWait()
        {
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(GifSpinner.Create(40));
            return wait;
        }

        private static UIElement HistoryEmpty(string text) => new TextBlock
        {
            Text = text,
            FontSize = 15,
            Foreground = UiHelpers.Subtle,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static ScrollViewer HistoryScroller(UIElement content) => new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        private UIElement BuildPcHistoryColumn()
        {
            if (_achHistoryLoading) return HistoryWait();
            // Says where dates come from, because "nothing" with three accounts signed in otherwise
            // reads as a fault.
            if (_achHistory.Count == 0)
                return HistoryEmpty(Core.Loc.T("No dated achievements yet. Steam dates only what this device has seen; Xbox and Epic date everything once you are signed in."));

            var list = new StackPanel();
            for (int i = 0; i < _achHistory.Count; i++)
            {
                var e = _achHistory[i];
                // The store's logo where the launcher is installed to take it from; its name
                // where not, so the store is never left unsaid.
                var logo = Library.StoreIcons.For(StoreGroup(e.Store));
                string line = logo != null ? e.GameTitle : e.GameTitle + "  ·  " + StoreLabel(e.Store);
                var row = BuildAchievementLine(e.Achievement, AchievementIconSize, true, line, logo);
                row.Tag = i;
                int captured = i;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _achHistoryIndex = captured;
                    if (_achHistoryColumn != 0) { _achHistoryColumn = 0; RenderAchievementHistory(); }
                    else ApplyAchievementHistorySelection();
                };
                _achHistoryRows.Add(row);
                list.Children.Add(row);
            }
            return HistoryScroller(list);
        }

        private UIElement BuildRetroHistoryColumn()
        {
            if (_raHistoryLoading) return HistoryWait();
            if (_raHistory.Count == 0) return HistoryEmpty(Core.Loc.T("No RetroAchievements unlocks yet."));

            // The library's title for a ROM it has, RetroAchievements' own otherwise; the console
            // after it, because the same game exists on several.
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _library.Games)
                if (g.Store == GameStore.Playnite && !string.IsNullOrEmpty(g.Id) && !titles.ContainsKey(g.Id)) titles[g.Id] = g.Title;

            var list = new StackPanel();
            for (int i = 0; i < _raHistory.Count; i++)
            {
                var e = _raHistory[i];
                string title = e.PlayniteId != null && titles.TryGetValue(e.PlayniteId, out var t) ? t : e.GameTitle;
                string line = string.IsNullOrEmpty(e.ConsoleName) ? title : title + "  ·  " + e.ConsoleName;
                var row = BuildAchievementLine(e.Entry, AchievementIconSize, true, line);
                row.Tag = i;
                int captured = i;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _raHistoryIndex = captured;
                    if (_achHistoryColumn != 1) { _achHistoryColumn = 1; RenderAchievementHistory(); }
                    else ApplyAchievementHistorySelection();
                };
                _raHistoryRows.Add(row);
                list.Children.Add(row);
            }
            return HistoryScroller(list);
        }

        private static LibraryGroup StoreGroup(GameStore store) =>
            store == GameStore.Epic ? LibraryGroup.Epic : store == GameStore.Xbox ? LibraryGroup.Xbox : LibraryGroup.Steam;

        private static string StoreLabel(GameStore store) =>
            store == GameStore.Epic ? "Epic Games" : store == GameStore.Xbox ? "Xbox" : "Steam";

        private void ApplyAchievementHistorySelection()
        {
            foreach (var row in _achHistoryRows)
                row.BorderBrush = _achHistoryColumn == 0 && row.Tag is int i && i == _achHistoryIndex ? UiHelpers.Accent : Brushes.Transparent;
            foreach (var row in _raHistoryRows)
                row.BorderBrush = _achHistoryColumn == 1 && row.Tag is int i && i == _raHistoryIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveAchievementHistorySelection(PadButton dir)
        {
            if (dir == PadButton.Left || dir == PadButton.Right)
            {
                int column = dir == PadButton.Right ? 1 : 0;
                if (!_raHistoryShown || column == _achHistoryColumn) return;
                _achHistoryColumn = column;
                // Redrawn for the column heads; each column keeps its own cursor. The scroll
                // position is lost with the redraw, so the cursor's row is brought back into view.
                RenderAchievementHistory();
                var target = column == 0 ? _achHistoryRows : _raHistoryRows;
                int at = column == 0 ? _achHistoryIndex : _raHistoryIndex;
                if (at < target.Count)
                {
                    var row = target[at];
                    Dispatcher.BeginInvoke(new Action(() => { try { row.BringIntoView(); } catch { } }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                return;
            }

            var rows = _achHistoryColumn == 0 ? _achHistoryRows : _raHistoryRows;
            int index = _achHistoryColumn == 0 ? _achHistoryIndex : _raHistoryIndex;
            if (rows.Count == 0) return;
            int next = index + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= rows.Count || next == index) return;
            if (_achHistoryColumn == 0) _achHistoryIndex = next; else _raHistoryIndex = next;
            ApplyAchievementHistorySelection();
            try { rows[next].BringIntoView(); } catch { }
        }

        private void AddAchievementHistoryActions()
        {
            AddAction(PadButton.B, "Back", true, CloseAchievementHistory);
            _liveActions[PadButton.X] = OpenAccountsFromLibrary;
        }
    }
}
