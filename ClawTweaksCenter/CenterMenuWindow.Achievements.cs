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
        /// <summary>How many go on the launch screen under the cover. Two, on the user's call - the
        /// screen already carries the art, the headline and up to three badges, and the row beneath
        /// is what a third would have been.</summary>
        private const int RecentAchievementCount = 2;

        private const double AchievementIconSize = 52;
        private const double RecentIconSize = 40;

        private List<AchievementEntry> _achievements = new List<AchievementEntry>();
        private readonly List<Border> _achievementRows = new List<Border>();
        private int _achievementIndex;
        private ScrollViewer _achievementScroller;

        /// <summary>Whose achievements are on screen. NOT _gameMenuTarget: this screen is reachable
        /// from the launch screen too, and there the game is _launchTarget.</summary>
        private GameEntry _achievementsGame;

        /// <summary>Where B goes. The launch screen is still standing behind this one - its prompt is
        /// never cleared - so going back is a redraw rather than a re-open.</summary>
        private bool _achievementsFromLaunch;

        private void ResetAchievementState()
        {
            _achievements = new List<AchievementEntry>();
            _achievementRows.Clear();
            _achievementIndex = 0;
            _achievementScroller = null;
            _achievementsGame = null;
            _achievementsFromLaunch = false;
        }

        /// <summary>
        /// A, on an "all achievements" row - the one in the game menu, or the one under the cover on
        /// the launch screen. The list is read here rather than when either screen is drawn: the
        /// parse is the expensive half, and neither of them needs more than the count.
        /// </summary>
        private void OpenAchievements(GameEntry game, bool fromLaunch)
        {
            if (game == null) return;

            var list = SteamAchievements.AllFor(game);
            if (list.Count == 0) return;      // both rows are greyed in this case; belt and braces

            _achievements = list;
            _achievementsGame = game;
            _achievementsFromLaunch = fromLaunch;
            _achievementIndex = 0;

            // The launch prompt is deliberately LEFT STANDING when we come from there. Clearing it
            // would drop the target, the cover and the cold-start timer, and B would have to build
            // the whole screen again from the library. Both overlays being open at once is already
            // the rule everywhere else that matters: RenderLibrary and MoveLibrarySelection both ask
            // about the game-menu overlay before the launch one, and the action bar now does too.
            _gameMenuOverlay = GameMenuOverlay.Achievements;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        /// <summary>B, on the achievement list, when it was opened from the launch screen.</summary>
        private void CloseAchievementsToLaunch()
        {
            ResetAchievementState();
            _gameMenuOverlay = GameMenuOverlay.None;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        #region The block on the launch screen
        /// <summary>The "All achievements" row under the two recent ones. Held so the focus border
        /// can be moved without redrawing the launch screen.</summary>
        private Border _launchAchRow;

        /// <summary>True while the launch screen has a live achievements row to move onto.</summary>
        private bool LaunchAchievementsRowLive => _launchAchRow != null && _launchAchRow.Tag is bool live && live;

        /// <summary>
        /// What sits under the cover on the launch screen: how far along, the last two unlocked, and
        /// a row into the full list.
        ///
        /// Returns null when this game has no achievements at all, and the caller adds nothing - an
        /// empty block with a heading is a section that looks broken rather than absent. A game that
        /// HAS achievements and none unlocked still gets it, because there the zero is the answer to
        /// the question somebody is about to act on.
        /// </summary>
        private UIElement BuildLaunchAchievementsBlock(GameEntry game)
        {
            _launchAchRow = null;

            var summary = SteamAchievements.SummaryFor(game);
            if (summary == null || summary.Total <= 0) return null;

            var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0), MaxWidth = 560 };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = Core.Loc.T("Achievements"),
                FontSize = 14,
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
                FontSize = 14,
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
                panel.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Nothing unlocked yet."),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            else
                foreach (var a in recent) panel.Children.Add(BuildAchievementLine(a, RecentIconSize, false));

            panel.Children.Add(BuildLaunchAchievementsRow(game));
            return panel;
        }

        /// <summary>
        /// The row that leads into the full list.
        ///
        /// A REAL ROW, not a footer chip. A and B on this screen already mean Play and Cancel, and
        /// the way in had to be something the stick can land on - which is what the user asked for,
        /// and what makes the launch screen's one exception to "nothing here takes focus" worth it.
        ///
        /// Live whenever there is a schema on disk, NOT when something is unlocked: a game where
        /// nothing has been earned yet is exactly where the list of what there is to go after is
        /// worth reading. Greyed rather than hidden on the other answer, so the screen keeps its
        /// height as the cursor moves from a Steam game to an Xbox one.
        /// </summary>
        private Border BuildLaunchAchievementsRow(GameEntry game)
        {
            bool live = SteamAchievements.HasDetail(game);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // The award glyph, as an escape rather than the literal character: a private-use
            // character is invisible in every diff and survives no encoding change. The same one the
            // game menu's row carries - see CLAUDE.md for why Segoe Fluent Icons has no trophy.
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = live ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(glyph, 0);
            grid.Children.Add(glyph);

            var label = new TextBlock
            {
                Text = Core.Loc.T("All achievements…"),
                FontSize = 15,
                Foreground = live ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var row = new Border
            {
                Child = grid,
                Tag = live,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 8, 0, 0),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = live ? System.Windows.Input.Cursors.Hand : null,
            };
            if (live)
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _launchFocus = LaunchFocusAchievements;
                    ApplyLaunchFocusVisuals();
                    OpenAchievements(game, true);
                };

            _launchAchRow = row;
            return row;
        }
        #endregion

        #region The full list
        private void RenderAchievements()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var game = _achievementsGame;
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
        /// One achievement: icon, name, description, how rare it is, and when it was unlocked.
        ///
        /// <paramref name="selectable"/> only decides whether it draws a selection border - the shape
        /// is shared so the two on the launch screen and the hundred in the list cannot drift apart.
        ///
        /// A LOCKED ONE IS THE SAME ROW, DIMMED. Steam's schema carries a second, grey icon for
        /// exactly this state, so the difference is a colour rather than a layout - which is what
        /// keeps a list of eighty scannable when a third of it is still to go.
        /// </summary>
        private Border BuildAchievementLine(AchievementEntry a, double iconSize, bool selectable)
        {
            bool locked = !a.Unlocked;
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
            // Dimmed rather than left at full strength: Steam's grey icon is already desaturated,
            // but next to a colour one at the same brightness the two still read as equals.
            var image = new Image { Stretch = Stretch.UniformToFill, Opacity = locked ? 0.55 : 1.0 };
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
                Foreground = locked ? UiHelpers.Subtle : UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // A SPOILER STAYS A SPOILER WHILE IT IS LOCKED. Steam flags these itself and hides their
            // text on its own pages; showing it here would turn a list somebody opened to see what is
            // left into the one thing they were being kept from. The NAME is kept - Steam keeps it
            // too, and a row with nothing in it reads as a fault rather than as a secret.
            string description = locked && a.Hidden ? Core.Loc.T("Hidden until you unlock it.") : a.Description;
            if (!string.IsNullOrEmpty(description))
                text.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = selectable ? 13 : 12,
                    Foreground = UiHelpers.Subtle,
                    FontStyle = locked && a.Hidden ? FontStyles.Italic : FontStyles.Normal,
                    TextWrapping = selectable ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                });

            // How many players have it, and - for a counted one - how far this player is.
            //
            // BOTH ARE OPTIONAL AND BOTH ARE OFTEN ABSENT. They come from a different Steam file than
            // everything above, one written when the Steam UI last drew this game's page, so most
            // games have it for a handful of achievements and many have it for none. A row simply
            // ends after its description in that case; there is no placeholder, because a dash here
            // would read as "nobody has this" rather than as "Steam never told us".
            var extra = new List<string>();
            if (a.GlobalPercent.HasValue)
                extra.Add(Core.Loc.F("{0}% of players", FormatRarity(a.GlobalPercent.Value)));
            if (a.ProgressMax > 0 && locked)
                extra.Add(FormatProgress(a.Progress) + " / " + FormatProgress(a.ProgressMax));

            if (extra.Count > 0)
                text.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", extra),
                    FontSize = selectable ? 12 : 11,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 3, 0, 0),
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

        /// <summary>
        /// A rarity percentage, at one decimal and without a trailing zero.
        ///
        /// THE DECIMAL IS THE POINT AT THE RARE END. Whole numbers turn every hard achievement in the
        /// game into "0 %", which is the one figure somebody scanning for the rare ones is looking
        /// for - 0.4 and 0.04 are a very different afternoon. Common ones lose nothing: 90 stays 90
        /// rather than becoming 90.0.
        /// </summary>
        private static string FormatRarity(double percent)
            => percent.ToString("0.#", CultureInfo.CurrentCulture);

        /// <summary>Steam counts progress in floats even where it is plainly a count of things, so
        /// "2" is written rather than "2.0" and a genuine half still survives.</summary>
        private static string FormatProgress(float value)
            => value.ToString("0.#", CultureInfo.CurrentCulture);

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
