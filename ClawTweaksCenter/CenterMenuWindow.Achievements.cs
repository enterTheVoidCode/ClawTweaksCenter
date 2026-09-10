using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Steam achievements on the game menu: the last few without asking, and the full list behind one
    /// row.
    ///
    /// The numbers come from Steam's own cache on this disk - see
    /// <see cref="Library.SteamAchievements"/>. Only the icons need the network, and the screen is
    /// built so that losing them costs a picture and never a line of text.
    /// </summary>
    public partial class CenterMenuWindow
    {
        /// <summary>How many fit on the menu screen above the rows without pushing them off a 1200p
        /// panel. Three, and the list row underneath is what the fourth is for.</summary>
        private const int RecentAchievementCount = 3;

        private const double AchievementIconSize = 52;
        private const double RecentIconSize = 40;

        private List<AchievementEntry> _achievements = new List<AchievementEntry>();
        private readonly List<Border> _achievementRows = new List<Border>();
        private int _achievementIndex;
        private ScrollViewer _achievementScroller;

        private void ResetAchievementState()
        {
            _achievements = new List<AchievementEntry>();
            _achievementRows.Clear();
            _achievementIndex = 0;
            _achievementScroller = null;
        }

        /// <summary>
        /// A, on the "all achievements" row. The list is read here rather than at menu-open time: the
        /// parse is the expensive half, and the menu itself only ever needs the count.
        /// </summary>
        private void OpenAchievements()
        {
            var game = _gameMenuTarget;
            if (game == null) return;

            var list = SteamAchievements.UnlockedFor(game);
            if (list.Count == 0) return;      // the row is greyed in this case; belt and braces

            _achievements = list;
            _achievementIndex = 0;
            _gameMenuOverlay = GameMenuOverlay.Achievements;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        #region The panel on the menu screen
        /// <summary>
        /// The block above the menu rows: how far along, and the last few unlocked.
        ///
        /// Returns null when this game has no achievements at all, and the caller adds nothing - an
        /// empty panel with a heading is a section that looks broken rather than absent. A game that
        /// HAS achievements and none unlocked does get the block, because there the zero is the
        /// answer to a question the user asked by opening this menu.
        /// </summary>
        private UIElement BuildRecentAchievementsPanel(GameEntry game)
        {
            var summary = SteamAchievements.SummaryFor(game);
            if (summary == null || summary.Total <= 0) return null;

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = Core.Loc.T("Achievements"),
                FontSize = 15,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 0);
            head.Children.Add(title);

            // "11 / 57  ·  19 %" - the fraction and the percentage together, because on a 700-entry
            // game the percentage alone hides how much work a single point is, and on a 12-entry game
            // the fraction alone hides how close the end is.
            var figures = new TextBlock
            {
                // Not translated and not spaced before the sign: the Downloading line in the library
                // already writes a percentage this way, and one screen spelling it differently from
                // the next is worse than either spelling.
                Text = summary.Unlocked.ToString(CultureInfo.CurrentCulture) + " / " +
                       summary.Total.ToString(CultureInfo.CurrentCulture) + "   ·   " +
                       summary.Percent.ToString(CultureInfo.CurrentCulture) + "%",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = summary.Unlocked >= summary.Total ? UiHelpers.Ok : UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(figures, 1);
            head.Children.Add(figures);
            panel.Children.Add(head);

            panel.Children.Add(new Border
            {
                Height = 1,
                Background = UiHelpers.Card,
                Margin = new Thickness(0, 6, 0, 8),
            });

            var recent = SteamAchievements.RecentFor(game, RecentAchievementCount);
            if (recent.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Nothing unlocked yet."),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                });
                return panel;
            }

            foreach (var a in recent) panel.Children.Add(BuildAchievementLine(a, RecentIconSize, false));
            return panel;
        }
        #endregion

        #region The full list
        private void RenderAchievements()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var game = _gameMenuTarget;
            var summary = SteamAchievements.SummaryFor(game);

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 10), MaxWidth = 900 };
            head.Children.Add(new TextBlock
            {
                Text = game?.Title ?? string.Empty,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4),
            });
            head.Children.Add(new TextBlock
            {
                Text = summary == null
                    ? Core.Loc.T("Achievements")
                    : Core.Loc.F("{0} of {1} unlocked", summary.Unlocked, summary.Total) +
                      "   ·   " + Core.Loc.F("{0}%", summary.Percent),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            var list = new StackPanel { Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12), MaxWidth = 900 };
            for (int i = 0; i < _achievements.Count; i++)
            {
                var row = BuildAchievementLine(_achievements[i], AchievementIconSize, true);
                row.Tag = i;
                int captured = i;
                row.MouseLeftButtonUp += (_, __) => { _achievementIndex = captured; ApplyAchievementSelection(); };
                _achievementRows.Add(row);
                list.Children.Add(row);
            }

            _achievementScroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            Grid.SetRow(_achievementScroller, 1);
            LibraryRoot.Children.Add(_achievementScroller);

            ApplyAchievementSelection();
        }

        /// <summary>
        /// One achievement: icon, name, description, and when it was unlocked.
        ///
        /// <paramref name="selectable"/> only decides whether it draws a selection border - the shape
        /// is shared so the three on the menu screen and the hundred in the list cannot drift apart.
        /// </summary>
        private Border BuildAchievementLine(AchievementEntry a, double iconSize, bool selectable)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconSize + 12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // A CARD BEHIND THE ICON, always. The picture arrives over the network and may never
            // arrive at all; without something behind it the row silently changes width when it
            // lands, and offline the column reads as a layout fault rather than a missing image.
            var iconHost = new Border
            {
                Width = iconSize,
                Height = iconSize,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var image = new Image { Stretch = Stretch.UniformToFill };
            iconHost.Child = image;
            Grid.SetColumn(iconHost, 0);
            grid.Children.Add(iconHost);

            if (!string.IsNullOrEmpty(a.IconUrl))
            {
                // LoadRemoteAsync, NOT LoadAsync - an http source handed to BitmapImage.UriSource
                // downloads asynchronously and then makes Freeze throw, which is how the art picker
                // once ended up with a grid of grey tiles. Same trap, same answer.
                string source = a.IconUrl;
                image.Tag = source;
                GameArt.LoadRemoteAsync(source, (int)(iconSize * 2)).ContinueWith(t =>
                {
                    var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (bmp == null) return;
                    image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(image.Tag, source)) return;
                        image.Source = bmp;
                    }));
                }, TaskScheduler.Default);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = a.Name ?? string.Empty,
                FontSize = selectable ? 16 : 14,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(a.Description))
                text.Children.Add(new TextBlock
                {
                    Text = a.Description,
                    FontSize = selectable ? 13 : 12,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = selectable ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // NO PLACEHOLDER FOR A MISSING DATE. An achievement can be unlocked with no timestamp -
            // Steam's two records are not the same record - and a dash there would read as "unlocked
            // on no date", which is not a thing. The row simply ends after the description.
            if (a.UnlockedAt.HasValue)
            {
                var ci = CultureInfo.CurrentCulture;
                var when = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                when.Children.Add(new TextBlock
                {
                    Text = a.UnlockedAt.Value.ToString("d MMM yyyy", ci),
                    FontSize = selectable ? 13 : 12,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                if (selectable)
                    when.Children.Add(new TextBlock
                    {
                        Text = a.UnlockedAt.Value.ToString("t", ci),
                        FontSize = 11,
                        Foreground = UiHelpers.Subtle,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    });
                Grid.SetColumn(when, 2);
                grid.Children.Add(when);
            }

            return new Border
            {
                Child = grid,
                Background = selectable ? UiHelpers.Card : Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = selectable ? new Thickness(12, 10, 12, 10) : new Thickness(0, 4, 0, 4),
                Margin = selectable ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = selectable ? System.Windows.Input.Cursors.Hand : null,
            };
        }

        private void ApplyAchievementSelection()
        {
            foreach (var row in _achievementRows)
                row.BorderBrush = row.Tag is int i && i == _achievementIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveAchievementSelection(PadButton dir)
        {
            if (_achievementRows.Count == 0) return;

            int next = _achievementIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _achievementRows.Count || next == _achievementIndex) return;

            _achievementIndex = next;
            ApplyAchievementSelection();
            try { _achievementRows[_achievementIndex].BringIntoView(); } catch { }
            RefreshActionBar();
        }
        #endregion
    }
}
