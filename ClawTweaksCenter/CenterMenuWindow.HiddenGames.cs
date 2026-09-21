using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The games the user hid from the game menu, with a way to bring each one back. Reached from
    /// Library settings, the same way the tab editor and the sound settings are.
    ///
    /// It lives INSIDE the settings screen: _hiddenGamesOpen implies _settingsOpen, so every guard
    /// that keeps the library still while settings are up covers this too.
    ///
    /// Listed from HiddenGamesStore, not from the library: a hidden game the scan no longer finds
    /// (uninstalled, store signed out) still has to be here, or it is on the list forever with no
    /// way to take it off.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _hiddenGamesOpen;
        private int _hiddenGamesIndex;
        private List<HiddenGamesStore.Entry> _hiddenGames = new List<HiddenGamesStore.Entry>();
        private readonly List<Border> _hiddenGamesRows = new List<Border>();
        private ScrollViewer _hiddenGamesScroller;

        private void OpenHiddenGames()
        {
            _hiddenGamesOpen = true;
            _hiddenGamesIndex = 0;
            RenderHiddenGames();
            RefreshActionBar();
        }

        private void CloseHiddenGames()
        {
            _hiddenGamesOpen = false;
            _hiddenGamesRows.Clear();
            _hiddenGamesScroller = null;
            RenderLibrarySettings();
            RefreshActionBar();
        }

        private void RenderHiddenGames()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _hiddenGamesRows.Clear();

            _hiddenGames = HiddenGamesStore.Load()
                .OrderBy(e => e.Title ?? e.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (_hiddenGamesIndex >= _hiddenGames.Count) _hiddenGamesIndex = _hiddenGames.Count - 1;
            if (_hiddenGamesIndex < 0) _hiddenGamesIndex = 0;

            // Heading beside the rows, like the tab editor and the sound settings: one layout for
            // the settings sub-screens, and a long list cannot push the heading off the screen.
            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Hidden games"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            heading.Children.Add(TabEditorHint("Press A to show a game in the library again."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new StackPanel { Width = 460 };
            if (_hiddenGames.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("No hidden games."),
                    FontSize = 17,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 12, 0, 12),
                });
            }
            for (int i = 0; i < _hiddenGames.Count; i++)
                list.Children.Add(BuildHiddenGameRow(i, _hiddenGames[i]));

            _hiddenGamesScroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                MaxHeight = 560,
            };
            Grid.SetColumn(_hiddenGamesScroller, 1);
            columns.Children.Add(_hiddenGamesScroller);

            LibraryRoot.Children.Add(columns);
            ApplyHiddenGamesSelection();
        }

        private Border BuildHiddenGameRow(int index, HiddenGamesStore.Entry entry)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(entry.Title) ? entry.Key : entry.Title,
                FontSize = 17,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);

            var store = new TextBlock
            {
                Text = entry.Store ?? string.Empty,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(store, 1);
            grid.Children.Add(store);

            var row = new Border
            {
                Child = grid,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
            };
            row.MouseLeftButtonUp += (_, __) => { _hiddenGamesIndex = index; UnhideSelectedGame(); };
            _hiddenGamesRows.Add(row);
            return row;
        }

        private void ApplyHiddenGamesSelection()
        {
            foreach (var row in _hiddenGamesRows)
            {
                bool selected = row.Tag is int i && i == _hiddenGamesIndex;
                row.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;
                if (selected) row.BringIntoView();
            }
        }

        private void MoveHiddenGamesSelection(PadButton dir)
        {
            if (_hiddenGamesRows.Count == 0) return;
            int next = _hiddenGamesIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _hiddenGamesRows.Count || next == _hiddenGamesIndex) return;
            _hiddenGamesIndex = next;
            ApplyHiddenGamesSelection();
            RefreshActionBar();
        }

        /// <summary>
        /// Takes the selected game off the hidden list and re-stamps the live library, so it is back
        /// on its shelf the moment settings close - no rescan. The cursor stays where it was, which
        /// is now the next game down: bringing back several in a row is A, A, A.
        /// </summary>
        private void UnhideSelectedGame()
        {
            if (_hiddenGamesIndex < 0 || _hiddenGamesIndex >= _hiddenGames.Count) return;
            var entry = _hiddenGames[_hiddenGamesIndex];
            HiddenGamesStore.Unhide(entry.Key);
            HiddenGamesStore.ApplyTo(_library.Games);
            Core.InstallLog.Write("[Library] shown again: " + entry.Title + " (" + entry.Key + ")");
            RenderHiddenGames();
            RefreshActionBar();
        }

        private void AddHiddenGamesActions()
        {
            AddAction(PadButton.A, "Show again", _hiddenGames.Count > 0, UnhideSelectedGame);
            AddAction(PadButton.B, "Back", true, CloseHiddenGames);
        }

        /// <summary>What the settings row shows without opening the list.</summary>
        private static string HiddenGamesSummary()
        {
            int n = HiddenGamesStore.Count();
            return n == 0 ? Core.Loc.T("None") : n.ToString();
        }
    }
}
