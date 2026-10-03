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
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _achHistoryOpen;
        private bool _achHistoryLoading;
        private List<AchievementHistoryEntry> _achHistory = new List<AchievementHistoryEntry>();
        private readonly List<Border> _achHistoryRows = new List<Border>();
        private int _achHistoryIndex;
        private CancellationTokenSource _achHistoryCts;

        /// <summary>The pill sits in Recent only: it is the shelf about what was played lately.</summary>
        private bool LibraryTabOffersHistory => _libraryGroup == LibraryGroup.Recent;

        /// <summary>The pill at the right end of Recent's title row.</summary>
        private UIElement BuildHistoryPill()
        {
            var pill = BuildCornerChip("X", "", Core.Loc.T("Achievement history"), OpenAchievementHistory);
            if (pill is Border b)
            {
                // A real pill here, not the corner's bare chip: it sits on the title row beside a
                // 26 px headline and has to read as a control rather than as part of the subline.
                b.Background = UiHelpers.Card;
                b.CornerRadius = new CornerRadius(16);
                b.Padding = new Thickness(6, 5, 14, 5);
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
        }

        private void RenderAchievementHistory()
        {
            _liveRows.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _achHistoryRows.Clear();
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
                Text = _achHistoryLoading
                    ? Core.Loc.T("Collecting your achievements...")
                    : Core.Loc.F("Your last {0} unlocks from Steam, Xbox and Epic, newest first.", _achHistory.Count),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            UIElement body;
            if (_achHistoryLoading)
            {
                var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                wait.Children.Add(GifSpinner.Create(40));
                body = wait;
            }
            else if (_achHistory.Count == 0)
            {
                body = new TextBlock
                {
                    // Says where dates come from, because "nothing" with three accounts signed in
                    // otherwise reads as a fault.
                    Text = Core.Loc.T("No dated achievements yet. Steam dates only what this device has seen; Xbox and Epic date everything once you are signed in."),
                    FontSize = 15,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                var list = new StackPanel { Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
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
                    row.MouseLeftButtonUp += (_, __) => { _achHistoryIndex = captured; ApplyAchievementHistorySelection(); };
                    _achHistoryRows.Add(row);
                    list.Children.Add(row);
                }
                body = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                };
            }
            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyAchievementHistorySelection();
        }

        private static LibraryGroup StoreGroup(GameStore store) =>
            store == GameStore.Epic ? LibraryGroup.Epic : store == GameStore.Xbox ? LibraryGroup.Xbox : LibraryGroup.Steam;

        private static string StoreLabel(GameStore store) =>
            store == GameStore.Epic ? "Epic Games" : store == GameStore.Xbox ? "Xbox" : "Steam";

        private void ApplyAchievementHistorySelection()
        {
            foreach (var row in _achHistoryRows)
                row.BorderBrush = row.Tag is int i && i == _achHistoryIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveAchievementHistorySelection(PadButton dir)
        {
            if (_achHistoryRows.Count == 0) return;
            int next = _achHistoryIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _achHistoryRows.Count || next == _achHistoryIndex) return;
            _achHistoryIndex = next;
            ApplyAchievementHistorySelection();
            try { _achHistoryRows[_achHistoryIndex].BringIntoView(); } catch { }
        }

        private void AddAchievementHistoryActions()
        {
            AddAction(PadButton.B, "Back", true, CloseAchievementHistory);
        }
    }
}
