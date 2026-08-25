using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The game library: a second main tab next to the existing start screen, driven from the pad.
    /// Everything it shows comes from files the stores already wrote to this machine - no account,
    /// no API key, and (in this state) no network at all.
    ///
    /// Two presentations, on purpose:
    ///   - RECENT is a single horizontal reel with the covers mirrored below, the way a console
    ///     front-end shows them. It is what the library opens on, because "the game I played
    ///     yesterday" is what someone reaching for a handheld wants nine times out of ten.
    ///   - Every other grouping is a grid, where the job is finding one title among many.
    /// </summary>
    public partial class CenterMenuWindow
    {
        // Tile sizes. Smaller than a desktop launcher would use: at arm's length on an 8" panel a
        // cover only has to be recognisable, not readable, and a bigger tile just means more
        // scrolling for the same number of games.
        private const double LibGridTileWidth = 190;

        /// <summary>Tiles are drawn at this fraction of the column width. See MeasureGridMetrics -
        /// it is the scrollbar's width, given back.</summary>
        private const double LibGridTileScale = 0.9;
        private const double LibTileGap = 16;
        private const double LibOuterMargin = 32;
        private const double LibCoverAspect = 1.5;      // 600x900 - the capsule Steam already caches

        // The reel sizes itself from the height it is given, within these bounds, so the mirrored
        // cover always fits without a scrollbar appearing underneath it.
        private const double LibReelMaxTileHeight = 300;
        private const double LibReelMinTileHeight = 150;
        private const double LibReflectionFraction = 0.38;

        private readonly GameLibrary _library = new GameLibrary();
        private LibraryGroup _libraryGroup = LibraryGroup.Recent;
        // Second-level grouping, ROMs only. Null = every system, which is how the tab opens.
        private string _romSystem;
        // Square ROM tiles. Remembered across launches - it describes the user's collection, not a
        // momentary preference.
        private bool _squareRomArt = Core.CenterSettings.SquareRomArt;
        private bool _libSquareTiles;
        private bool _settingsOpen;
        private ScrollViewer _tabScroller;
        private FrameworkElement _activeGroupChip;
        private ScrollViewer _systemScroller;
        private FrameworkElement _activeSystemChip;
        private CancellationTokenSource _artFetchCts;
        private IReadOnlyList<GameEntry> _libraryGames = Array.Empty<GameEntry>();
        private int _libSelectedIndex;
        private int _libColumns = 1;
        private double _libTileWidth = LibGridTileWidth;
        private int _libDecodeWidth = (int)LibGridTileWidth;
        private bool _libReelMode;
        private bool _libraryScanned;
        private bool _libraryScanning;
        private CancellationTokenSource _libraryCts;
        private ListBox _libList;
        private TextBlock _libHeadline;
        private TextBlock _libSubline;
        private readonly HashSet<ILibrarySelectionHost> _liveRows = new HashSet<ILibrarySelectionHost>();

        // Pending close after a launch (see LaunchSelectedGame). Non-null only while the countdown
        // is up, which is also what tells the footer to offer "Keep Center open".
        /// <summary>
        /// Which of the launch screens owns the library, if any.
        ///
        /// This replaced a 2.5-second timer that fired once and then acted on its own. The timer was
        /// the wrong shape twice over: pressing A started a game with no way back, and the window
        /// then decided by itself when the launch was "over" - two and a half seconds, whatever the
        /// game was actually doing.
        /// </summary>
        private enum LaunchPrompt
        {
            None,
            /// <summary>"Start X?" - nothing has happened yet.</summary>
            Confirm,
            /// <summary>Launched and still running. Stays until the game ends or the user hides it.</summary>
            Running,
            /// <summary>The launch itself failed.</summary>
            Failed,
        }

        private LaunchPrompt _launchPrompt;
        private GameEntry _launchTarget;

        /// <summary>
        /// B in the library asks instead of acting.
        ///
        /// It used to drop straight back to the start screen, and B is the button a thumb finds by
        /// accident - one stray press threw away the tab, the scroll position and the covers already
        /// decoded. The three answers here are the three things B could reasonably have meant.
        /// </summary>
        /// <summary>The library's own info screen. Opened only by the user, from the info chip in the
        /// tab strip - which pulses until they have done so once (CenterSettings.LibraryInfoSeen).</summary>
        private bool _infoOpen;

        private const string SteamGridDbUrl = "https://www.steamgriddb.com/";
        private const string AnyFseUrl = "https://github.com/ashpynov/AnyFSE";

        private bool _exitPromptOpen;
        private int _exitPromptIndex;
        private readonly List<Border> _exitPromptRows = new List<Border>();

        /// <summary>True while one of the launch screens is up. Everything that navigates the grid
        /// checks this - the launch screens own the whole library area while they are on it.</summary>
        private bool LaunchOverlayOpen => _launchPrompt != LaunchPrompt.None || _exitPromptOpen || _infoOpen;

        // The background watcher for "restore Center once this game ends" (see GameRunTracker /
        // StartTrackingForRestore). Held so a second launch can cancel a stale watch instead of
        // leaving two of them racing to restore the same window.
        private CancellationTokenSource _gameTrackCts;

        /// <summary>
        /// The tab exists only once we KNOW ClawTweaks is installed. The check runs through
        /// PowerShell and takes about a second, during which the answer is genuinely unknown - and a
        /// tab that shows up and then disappears reads as a crash, while one that appears a second
        /// late is not noticed at all. So: absent until certain, then permanent.
        /// </summary>
        private bool LibraryAvailable => _installedVersionChecked && _installedVersion != null;

        #region Tab strip
        /// <summary>
        /// One strip carries both levels of navigation: the main tabs on the left, the library's own
        /// groupings immediately to their right behind a divider. They are not the same kind of thing,
        /// hence the divider - but giving each its own full-width row cost two lines of screen on a
        /// device that has 1200 of them, for two words each.
        /// </summary>
        private void RefreshTabStrip()
        {
            if (TabStrip == null || TabStripPanel == null) return;

            if (!LibraryAvailable)
            {
                TabStrip.Visibility = Visibility.Collapsed;
                if (ShellHeader != null) ShellHeader.Visibility = Visibility.Visible;
                return;
            }

            bool inLibrary = _view == View.Library;

            // The brand-and-device header is hidden inside the library. It belongs to a setup screen,
            // and covers want the height more than the device name does - the same device is still
            // named on every other screen.
            if (ShellHeader != null)
                ShellHeader.Visibility = inLibrary ? Visibility.Collapsed : Visibility.Visible;

            TabStrip.Visibility = Visibility.Visible;
            TabStrip.Margin = new Thickness(inLibrary ? LibOuterMargin : 24, inLibrary ? 10 : 0, 24, 0);
            TabStripPanel.Children.Clear();

            if (inLibrary)
            {
                // No "B Start" here any more: B is already named in the footer, and repeating it stole
                // the width the tabs need. The shoulders sit at the two ends of the row they scroll,
                // which is where they are on the pad.
                var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                _activeGroupChip = null;
                foreach (LibraryGroup g in Enum.GetValues(typeof(LibraryGroup)))
                {
                    // No ROMs chip without Playnite: a tab that can only ever be empty is a dead end,
                    // and "ROMs 0" invites a hunt for a bug that is really "you have no Playnite".
                    if (g == LibraryGroup.Roms && !Library.PlayniteSource.IsPresent) continue;
                    // Same reasoning for Favorites: it does not exist until there is at least one -
                    // an empty Favorites tab next to Recent on a fresh install would look like the
                    // feature is broken rather than simply unused yet.
                    if (g == LibraryGroup.Favorites && !Library.FavoritesStore.Any()) continue;
                    var chip = BuildGroupChip(g);
                    if (g == _libraryGroup) _activeGroupChip = chip as FrameworkElement;
                    chips.Children.Add(chip);
                }

                // RB sits AFTER THE LAST TAB, inside the scrolling strip, not pinned to the far
                // right of the window. Docked to the edge it floated in whatever empty space was
                // left over - on a wide window that put it a hand's width away from the thing it
                // scrolls, reading as a control for something else entirely.
                //
                // The cost is real and accepted: on a row long enough to scroll, RB scrolls with it.
                // LB stays pinned, so the pair is no longer symmetrical - but LB marks the START of
                // the row, which is a fixed place, while RB marks its end, which is not.
                chips.Children.Add(BuildKeyCap("RB"));

                // Sort/grouping and the info button share the docked right end, in that order: the
                // readout changes with the tab, the info button never does, so the fixed thing sits
                // at the edge and the changing one next to the tabs it belongs to.
                var rightEnd = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightEnd.Children.Add(BuildSortStrip());
                rightEnd.Children.Add(BuildInfoButton());

                _tabScroller = BuildEdgeFadedStrip(chips);
                FillDock(TabStripPanel, BuildKeyCap("LB"), rightEnd, _tabScroller);
                BringChipIntoView(_tabScroller, _activeGroupChip);
            }
            else
            {
                // NO TAB STRIP OUTSIDE THE LIBRARY. There used to be a "Start | Library" pair with an
                // RT hint here, and it was a second way to reach something Home already has a tile
                // for - two controls for one destination, one of them invisible on the screens where
                // it made no sense at all (it rode along through the whole update run).
                //
                // Inside the library the strip stays: there it is the game TABS, which is a different
                // thing wearing the same row.
                TabStrip.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Lays out "key cap - scrolling middle - key cap". The caps are pinned to the two ends and
        /// never scroll away: on a device with twenty systems the right-hand hint is exactly the one
        /// that would otherwise be pushed off screen, and it is the one that says there is more.
        /// </summary>
        private static void FillDock(DockPanel dock, UIElement left, UIElement right, UIElement middle)
        {
            dock.Children.Clear();
            if (left != null)
            {
                DockPanel.SetDock(left, Dock.Left);
                ((FrameworkElement)left).Margin = new Thickness(0, 0, 10, 0);
                dock.Children.Add(left);
            }
            if (right != null)
            {
                DockPanel.SetDock(right, Dock.Right);
                ((FrameworkElement)right).Margin = new Thickness(10, 0, 0, 0);
                dock.Children.Add(right);
            }
            dock.Children.Add(middle);   // last child fills what is left
        }

        /// <summary>
        /// A horizontal strip that scrolls without a scrollbar and fades at whichever end still has
        /// content behind it.
        ///
        /// The fade is the whole point: with more systems than fit, a hard cut at the edge looks like
        /// the list simply ends there. A softened edge says the opposite, and it disappears once the
        /// end really is reached.
        /// </summary>
        private ScrollViewer BuildEdgeFadedStrip(FrameworkElement content)
        {
            var scroller = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            scroller.ScrollChanged += (_, __) => UpdateEdgeFade(scroller);
            scroller.SizeChanged += (_, __) => UpdateEdgeFade(scroller);
            return scroller;
        }

        private static void UpdateEdgeFade(ScrollViewer scroller)
        {
            try
            {
                double width = scroller.ActualWidth;
                if (width <= 0) return;

                bool more = scroller.HorizontalOffset < scroller.ScrollableWidth - 0.5;
                bool before = scroller.HorizontalOffset > 0.5;
                if (!more && !before) { scroller.OpacityMask = null; return; }

                // 56 px of fade, expressed as a fraction of the current width so the gradient stays
                // the same size on screen whatever the strip is measured at.
                double fade = Math.Min(0.35, 56 / width);
                var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, before ? fade : 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, more ? 1 - fade : 1));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                mask.Freeze();
                scroller.OpacityMask = mask;
            }
            catch { }
        }

        /// <summary>
        /// Scrolls the selected chip into view.
        ///
        /// Queued at Loaded priority because the strip has not been measured yet when the render
        /// builds it - asking an unmeasured element to bring itself into view does nothing, which is
        /// how the active system ended up off screen once there were more systems than fit.
        /// </summary>
        private void BringChipIntoView(ScrollViewer scroller, FrameworkElement chip)
        {
            if (scroller == null || chip == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { chip.BringIntoView(); UpdateEdgeFade(scroller); } catch { }
            }), DispatcherPriority.Loaded);
        }


        /// <summary>A shoulder button drawn as its key cap plus what it does.</summary>
        private UIElement BuildPadHint(string cap, string label, Action onClick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(BuildKeyCap(cap));
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 15,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
            });

            var border = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(0, 4, 0, 4),
            };
            if (onClick != null) border.MouseLeftButtonUp += (_, __) => onClick();
            return border;
        }

        /// <summary>
        /// The info button: the X key cap and the info glyph inside ONE frame.
        ///
        /// Two separate outlines side by side read as two controls, and only one of them was
        /// clickable - so the pair had to become a single object with a single border. It is also
        /// what makes the promise legible: the button and the key that presses it are one thing.
        ///
        /// The divider between them is a hairline rather than a gap, because a gap inside a frame is
        /// how two things end up looking like two things again.
        /// </summary>
        private UIElement BuildInfoButton()
        {
            // Never opened: the chip is accented and breathes, so it reads as "there is
            // something here" instead of sitting in the same grey as every other hint in the
            // strip. Once opened it drops back to grey for good - an attention cue that never
            // stops is one the user learns to ignore.
            bool unseen = !Core.CenterSettings.LibraryInfoSeen;
            Brush ink = unseen ? UiHelpers.Accent : UiHelpers.Subtle;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(new TextBlock
            {
                Text = "X",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new Border
            {
                Width = 1,
                Height = 12,
                Background = ink,
                Opacity = 0.4,
                Margin = new Thickness(7, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = "\uE946",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Border
            {
                Child = row,
                Background = UiHelpers.Card,
                BorderBrush = ink,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "About this library",
            };
            button.MouseLeftButtonUp += (_, __) => OpenLibraryInfo();

            if (unseen) StartInfoPulse(button);
            return button;
        }

        /// <summary>
        /// The slow opacity pulse on the unseen info chip.
        ///
        /// Started on the element itself rather than through a Storyboard resource: the chip is
        /// built fresh on every tab-strip refresh, and an animation attached to a throwaway element
        /// dies with it. Nothing has to stop it - opening the info flips the flag, and the next
        /// refresh builds a grey chip with no animation on it at all.
        /// </summary>
        private static void StartInfoPulse(UIElement target)
        {
            var pulse = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.45,
                Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            target.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        private UIElement BuildKeyCap(string text) => new Border
        {
            Background = UiHelpers.Card,
            BorderBrush = UiHelpers.Subtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = UiHelpers.Subtle,
            },
        };

        private UIElement BuildDivider() => new Border
        {
            Width = 1,
            Height = 18,
            Background = UiHelpers.Subtle,
            Opacity = 0.35,
            Margin = new Thickness(16, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>
        /// A tab chip's insides: the label, and the number of games behind it in a small grey disc.
        ///
        /// The count used to be two spaces and a number in the same run of text, which read as part
        /// of the name - "Steam 84" looks like a title until you notice it changes. Set apart, it is
        /// a quantity, and the same treatment on the tab row and the ROM system row keeps it one idea
        /// rather than two similar-looking ones.
        ///
        /// The disc stays SUBTLE even on the active chip. It is there to be glanced at, not read: the
        /// chip already carries the selection through its own border and weight, and a second thing
        /// lighting up inside it competes with that.
        /// </summary>
        private static UIElement BuildChipContent(string label, int? count, bool active, double fontSize)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = fontSize,
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                Foreground = active ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (count == null) return text;

            var number = new TextBlock
            {
                Text = count.Value.ToString(),
                FontSize = fontSize - 3,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // MinWidth = MinHeight and a radius of half that: a single digit sits in a circle, and
            // three digits stretch it into a pill instead of overflowing a fixed-size circle. A round
            // badge that clips its own contents at 100+ items is the version of this that only breaks
            // on the tabs that matter.
            double size = fontSize + 6;
            var badge = new Border
            {
                Child = number,
                MinWidth = size,
                MinHeight = size,
                CornerRadius = new CornerRadius(size / 2),
                // A translucent white wash rather than the Card brush: the ACTIVE chip is already
                // filled with Card, and a badge painted in the same colour would vanish on exactly
                // the tab the user is looking at. This one sits on both backgrounds.
                Background = BadgeFill,
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            row.Children.Add(badge);
            return row;
        }

        private static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private UIElement BuildGroupChip(LibraryGroup g)
        {
            bool active = g == _libraryGroup;
            int count = _libraryScanned ? _library.ForGroup(g).Count : 0;

            var chip = new Border
            {
                Child = BuildChipContent(Core.Loc.T(GameLibrary.GroupLabel(g)),
                                         _libraryScanned && !ImmersiveCountsHidden ? count : (int?)null, active, 14),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(13),
                Background = active ? UiHelpers.Card : Brushes.Transparent,
                BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(active ? 1 : 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = g;
            chip.MouseLeftButtonUp += (_, __) => SetLibraryGroup(captured);
            return chip;
        }
        #endregion

        #region Enter / leave
        private void OpenLibrary()
        {
            if (!LibraryAvailable) return;
            _view = View.Library;
            ContentScroller.Visibility = Visibility.Collapsed;
            LibraryRoot.Visibility = Visibility.Visible;
            RefreshTabStrip();
            RenderLibrary();
            RefreshActionBar();
            if (!_libraryScanned && !_libraryScanning) _ = ScanLibraryAsync();

            // Straight into the immersive look, no two-second grace: the footer is meant to be gone
            // in this mode, and showing it for two seconds on every entry is the flicker the mode is
            // for. The tabs get their countdown, because their first job is to say where you are.
            if (ImmersiveActive)
            {
                _footerRevealed = false;
                ApplyImmersiveChrome();
                RestartIdleTimer();
            }

        }

        /// <summary>Puts the build-list host back in front and restores the header. Called from
        /// GoHome - the library is a tab, not a window, so leaving it is a visibility change.</summary>
        private void LeaveLibrary()
        {
            StopImmersive();
            CancelPendingClose();
            if (LibraryRoot != null) LibraryRoot.Visibility = Visibility.Collapsed;
            if (ContentScroller != null) ContentScroller.Visibility = Visibility.Visible;
            if (ShellHeader != null) ShellHeader.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task ScanLibraryAsync()
        {
            _libraryScanning = true;
            // ⚠️ EVERY RenderLibrary IN THIS METHOD IS GUARDED. The scan starts the moment the
            // library opens and paints again on each store landing - straight over anything an
            // overlay had put on the screen a millisecond earlier. That is what made the info screen
            // flash and vanish for anyone starting directly into the library: it was drawn, and then
            // the scan's first repaint replaced it, while the overlay still owned every button.
            RenderLibraryIfNoOverlay();
            try
            {
                _libraryCts?.Cancel();
                _libraryCts = new CancellationTokenSource();
                var ct = _libraryCts.Token;

                // Each store paints as it lands (Steam is ready in a tenth of a second, Xbox takes
                // three), so the view fills in instead of waiting on the slowest source. Marked as
                // scanned on the FIRST result: from that point the screen has content, and leaving
                // the "reading your stores" spinner up over a full grid would be a lie.
                await _library.ScanAsync(ct, () => Dispatcher.Invoke(() =>
                {
                    _libraryScanned = true;
                    RenderLibraryIfNoOverlay();
                    RefreshTabStrip();
                }));
                _libraryScanned = true;
                _libraryScanning = false;
                RenderLibraryIfNoOverlay();
                RefreshTabStrip();

                StartArtFetch();

                // The log harvest only refines the ordering of a library that is already usable, so
                // it runs after the view is up rather than in front of it. It is also what fills the
                // Recent reel for anything Steam does not track, hence the re-render.
                _library.HarvestHistoryInBackground(ct, () => Dispatcher.Invoke(() =>
                {
                    if (_libraryGroup == LibraryGroup.Recent) RenderLibraryIfNoOverlay();
                }));
            }
            catch (OperationCanceledException) { _libraryScanning = false; }
            catch (Exception ex)
            {
                _libraryScanning = false;
                Core.InstallLog.Write("Library scan failed: " + ex);
                RenderLibraryIfNoOverlay();
            }
            RefreshActionBar();
        }
        #endregion

        #region Rendering
        private void RenderLibrary()
        {
            if (LibraryRoot == null) return;

            // An overlay owns the whole area while it is up. Checked HERE rather than at every call
            // site: RenderLibrary is called from the art fetch, from the resize handler and from the
            // scan loop, and any one of them arriving mid-overlay would wipe the screen underneath
            // the user.
            if (MiscOverlayOpen) { RenderMiscOverlay(); return; }
            if (GameMenuOverlayOpen) { RenderGameMenuOverlay(); return; }

            _liveRows.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // ROM systems
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // selected title
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _libReelMode = _libraryGroup == LibraryGroup.Recent;
            // Square only in the ROM tab. Recent mixes ROMs and store games on one shelf, and two
            // different tile shapes side by side looks like a rendering fault rather than a setting.
            _libSquareTiles = _squareRomArt && _libraryGroup == LibraryGroup.Roms;
            _libGroupBreaks.Clear();
            _libraryGames = _libraryScanned
                ? ArrangeForDisplay(_library.ForGroup(_libraryGroup, _romSystem))
                : (IReadOnlyList<GameEntry>)Array.Empty<GameEntry>();

            if (_libraryGroup == LibraryGroup.Roms && _libraryScanned)
            {
                // No advisory line under the strip any more. It said two true things - that the list
                // came from our cache while Playnite held its database, and that Playnite reappears
                // after a launch unless its own setting says so - and neither earns a permanent
                // yellow line above the covers now that ROMs start without Playnite at all.
                var systemStrip = BuildSystemStrip();
                Grid.SetRow(systemStrip, 0);
                LibraryRoot.Children.Add(systemStrip);
            }
            if (_libSelectedIndex >= _libraryGames.Count) _libSelectedIndex = _libraryGames.Count - 1;
            if (_libSelectedIndex < 0) _libSelectedIndex = 0;

            var titleBlock = BuildSelectedTitle();
            Grid.SetRow(titleBlock, 1);
            LibraryRoot.Children.Add(titleBlock);

            UIElement body;
            if (_libraryScanning && !_libraryScanned) body = BuildLibraryMessage("Reading your stores…", working: true);
            else if (_libraryGames.Count == 0) body = BuildLibraryMessage(EmptyMessage(), working: false);
            else body = _libReelMode ? BuildReel() : BuildGrid();

            Grid.SetRow((FrameworkElement)body, 2);
            LibraryRoot.Children.Add(body);
        }

        /// <summary>
        /// The second level under ROMs: one chip per console, plus an "All systems" entry in front.
        /// Only ever drawn for the ROM tab - every other grouping is one flat list, and a second row
        /// of chips there would be an empty promise.
        /// </summary>
        private UIElement BuildSystemStrip()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _activeSystemChip = null;
            foreach (string system in RomSystemCycle())
            {
                bool active = string.Equals(system, _romSystem, StringComparison.OrdinalIgnoreCase);
                int count = _library.ForGroup(LibraryGroup.Roms, system).Count;

                var chip = BuildSystemChip(SystemLabel(system), ImmersiveCountsHidden ? (int?)null : count, active);
                var captured = system;
                chip.MouseLeftButtonUp += (_, __) => SetRomSystem(captured);
                if (active) _activeSystemChip = chip;
                panel.Children.Add(chip);
            }

            // Twenty systems on an eight-inch panel: the strip scrolls sideways rather than wrapping
            // into a second line that would push the covers down, and the triggers stay pinned to the
            // two ends so the "there is more that way" hint cannot scroll away with the content.
            _systemScroller = BuildEdgeFadedStrip(panel);

            var dock = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(LibOuterMargin, 8, LibOuterMargin, 0),
            };
            FillDock(dock, BuildKeyCap("LT"), BuildKeyCap("RT"), _systemScroller);
            BringChipIntoView(_systemScroller, _activeSystemChip);
            return dock;
        }

        /// <summary>
        /// A ROM system chip, and DELIBERATELY not the same object as a tab chip.
        ///
        /// The two rows sit one above the other and are two different levels: the tabs are where you
        /// are in the library, the systems are a filter inside one of them. Drawn as pills both, the
        /// second row read as a continuation of the first - two rows of the same thing, with no clue
        /// which one the shoulders move and which one the triggers do.
        ///
        /// So this one drops the pill entirely: text, and an accent RULE under the active system.
        /// The rule is the same shape a tab underline has everywhere else, at a level below a filled
        /// pill - subordinate by construction rather than by being a bit smaller.
        /// </summary>
        private Border BuildSystemChip(string label, int? count, bool active)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? UiHelpers.Accent : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);

            if (count != null)
            {
                // Quieter than the tab badge above it, and no disc: the pill treatment is what the
                // level above uses, and repeating it here is exactly the sameness being removed.
                row.Children.Add(new TextBlock
                {
                    Text = count.Value.ToString(),
                    FontSize = 11,
                    Foreground = UiHelpers.Subtle,
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 1, 0, 0),
                });
            }

            var content = new StackPanel();
            content.Children.Add(row);
            content.Children.Add(new Border
            {
                Height = 2,
                // Always present, painted only when active: a rule that appears and disappears would
                // move every label by two pixels as the cursor passes along the row.
                Background = active ? UiHelpers.Accent : Brushes.Transparent,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 3, 0, 0),
            });

            return new Border
            {
                Child = content,
                Padding = new Thickness(7, 2, 7, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static string SystemLabel(string system)
        {
            // Only the two OWN labels are translated. A console name is a product name and reads the
            // same everywhere, so passing it through Loc would be a lookup that can only ever miss.
            if (system == GameLibrary.RomRecentSystem) return Core.Loc.T("Recent");
            return system ?? Core.Loc.T("All systems");
        }

        /// <summary>
        /// Recent first, then every system, then the individual consoles. All three are one sequence
        /// because LT/RT walk through them and have to wrap.
        ///
        /// ROMs get their OWN recent because they are excluded from the library-wide one: they are
        /// tried out a handful at a time, and an evening of browsing a Game Boy collection would push
        /// every PC game off the main shelf.
        /// </summary>
        private List<string> RomSystemCycle()
        {
            var list = new List<string> { GameLibrary.RomRecentSystem, null };
            list.AddRange(_library.RomSystems);
            return list;
        }

        private void SetRomSystem(string system)
        {
            if (string.Equals(system, _romSystem, StringComparison.OrdinalIgnoreCase)) return;
            _romSystem = system;
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshActionBar();
        }

        private void CycleRomSystem(int delta)
        {
            var cycle = RomSystemCycle();
            if (cycle.Count <= 1) return;
            int i = cycle.FindIndex(s => string.Equals(s, _romSystem, StringComparison.OrdinalIgnoreCase)) + delta;
            if (i < 0) i = cycle.Count - 1;
            if (i >= cycle.Count) i = 0;
            SetRomSystem(cycle[i]);
        }

        /// <summary>What the empty view says. Deliberately different per case: "nothing installed" and
        /// "sixteen folders and none of them usable" are different answers, and giving the second one
        /// as the first sends the user looking for a bug in the wrong place.</summary>
        private string EmptyMessage()
        {
            if (!_libraryScanned) return "Nothing found yet.";
            switch (_libraryGroup)
            {
                case LibraryGroup.Recent: return "No game has been played yet.";
                case LibraryGroup.Epic: return "No Epic games installed.";
                case LibraryGroup.Xbox:
                    return XboxSource.OrphanFolderCount > 0
                        ? "No Xbox games are registered for this account."
                        : "No Xbox games installed.";
                case LibraryGroup.Steam: return "No Steam games installed.";
                case LibraryGroup.Misc: return "No tools added yet.";
                // Reachable for one frame: unfavoriting the last game while its own tab is on screen
                // still redraws it before the tab strip drops the now-empty chip.
                case LibraryGroup.Favorites: return "No favorites yet.";
                case LibraryGroup.Roms:
                    if (!Library.PlayniteSource.IsPresent) return "Playnite is not installed.";
                    if (_romSystem == GameLibrary.RomRecentSystem) return "No ROM has been played yet.";
                    return _romSystem == null ? "No ROMs in your Playnite library." : "No ROMs for " + _romSystem + ".";
                default: return "No games found.";
            }
        }

        /// <summary>
        /// The selected game's name above the covers, Big-Picture style. The tiles themselves stay
        /// pure artwork - a caption under every tile turns a wall of covers into a wall of text, and
        /// the only title anyone needs is the one under the cursor.
        /// </summary>
        private UIElement BuildSelectedTitle()
        {
            var stack = new StackPanel { Margin = new Thickness(LibOuterMargin, 10, LibOuterMargin, 10) };

            _libHeadline = new TextBlock
            {
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _libSubline = new TextBlock
            {
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 2, 0, 0),
            };
            stack.Children.Add(_libHeadline);
            stack.Children.Add(_libSubline);
            UpdateSelectedTitle();
            return stack;
        }

        private void UpdateSelectedTitle()
        {
            if (_libHeadline == null || _libSubline == null) return;
            var g = SelectedGame;
            if (g == null)
            {
                _libHeadline.Text = string.Empty;
                _libSubline.Text = string.Empty;
                return;
            }
            _libHeadline.Text = g.Title;
            // The labels are translated, the store name and the date are not: one is a brand, and
            // the other is formatted by the CURRENT CULTURE so it already reads correctly for the
            // user. Splitting it this way is why the key is the label alone rather than the whole
            // line - a key with a date in it would never match twice.
            //
            // EVERY PART IS OPTIONAL EXCEPT THE STORE. Playtime exists for Steam and nowhere else,
            // and a date exists only once something has been played. A missing part is left out
            // rather than shown empty: "0 h" is a claim, and "—" is a gap the user has to interpret.
            var parts = new List<string> { g.StoreName };

            string played = Library.SteamPlaytime.Format(g.PlaytimeMinutes);
            if (played != null) parts.Add(played);

            if (g.LastPlayed.HasValue)
                parts.Add(Core.Loc.T("Last played") + " " +
                          g.LastPlayed.Value.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture));

            _libSubline.Text = string.Join("  ·  ", parts);
        }

        private GameEntry SelectedGame =>
            _libSelectedIndex >= 0 && _libSelectedIndex < _libraryGames.Count ? _libraryGames[_libSelectedIndex] : null;

        private UIElement BuildLibraryMessage(string text, bool working)
        {
            text = Core.Loc.T(text);

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (working) stack.Children.Add(GifSpinner.Create(40));
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 17,
                Foreground = UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, working ? 12 : 0, 0, 0),
            });
            return stack;
        }

        /// <summary>
        /// The Recent reel: one horizontal row, covers mirrored below as if standing on glass.
        ///
        /// A ListBox with a HORIZONTAL virtualising panel - same reason as the grid below, and it
        /// matters more here because a reel has no second dimension to run out of: every game ever
        /// played sits on one line.
        /// </summary>
        private UIElement BuildReel()
        {
            MeasureReelMetrics();

            var items = new List<LibraryReelItem>();
            for (int i = 0; i < _libraryGames.Count; i++)
                items.Add(new LibraryReelItem { Owner = this, Index = i, Game = _libraryGames[i] });

            var factory = new FrameworkElementFactory(typeof(LibraryReelHost));
            factory.SetBinding(LibraryReelHost.ItemProperty, new Binding("."));

            _libList = BuildStripList(items, factory, horizontal: true);
            _libList.Padding = new Thickness(LibOuterMargin, 0, LibOuterMargin, 0);
            // Stretch, not Top. Two things depend on it: the covers sit in the MIDDLE of the space
            // instead of clinging to the title above them, and the horizontal scrollbar - which lives
            // at the bottom edge of the list - ends up directly above the footer bar rather than
            // floating in the middle of the screen under the reel.
            _libList.VerticalAlignment = VerticalAlignment.Stretch;
            return _libList;
        }

        /// <summary>
        /// The cover grid.
        ///
        /// A ListBox, not a WrapPanel: a WrapPanel does not virtualise, so two hundred games would
        /// mean two hundred live containers. The items are ROWS, each row builds its own tiles, and
        /// the virtualising stack panel only keeps the visible ones alive. ListBox rather than
        /// ItemsControl for one specific reason - ScrollIntoView works with virtualisation, and
        /// BringIntoView on a container that was never realised does not.
        /// </summary>
        // Where each TILE row starts and how wide it is. Built by BuildGrid, read by the D-pad.
        //
        // Needed because a group boundary breaks a row early: with headings on, "index plus one
        // column" is no longer the tile below, and moving down from a short last row would land in
        // a different column of a different group.
        private readonly List<int> _libRowStarts = new List<int>();
        private readonly List<int> _libRowCounts = new List<int>();

        private UIElement BuildGrid()
        {
            MeasureGridMetrics();

            var rows = new List<LibraryRow>();
            _libRowStarts.Clear();
            _libRowCounts.Clear();

            int index = 0;
            while (index < _libraryGames.Count)
            {
                if (_libGroupBreaks.TryGetValue(index, out string heading))
                    rows.Add(new LibraryRow { Owner = this, Heading = heading });

                // A row never spans two groups: it stops at the next break even when it is half
                // empty. Without that, the last row of one group would carry the first covers of the
                // next, under the wrong heading - which is the one thing a heading must not do.
                var slice = new List<GameEntry>();
                for (int c = 0; c < _libColumns && index + c < _libraryGames.Count; c++)
                {
                    if (c > 0 && _libGroupBreaks.ContainsKey(index + c)) break;
                    slice.Add(_libraryGames[index + c]);
                }

                _libRowStarts.Add(index);
                _libRowCounts.Add(slice.Count);
                rows.Add(new LibraryRow { Owner = this, FirstIndex = index, Items = slice });
                index += slice.Count;
            }

            var factory = new FrameworkElementFactory(typeof(LibraryRowHost));
            factory.SetBinding(LibraryRowHost.RowProperty, new Binding("."));

            _libList = BuildStripList(rows, factory, horizontal: false);
            _libList.Padding = new Thickness(0, 0, 0, 24);
            return _libList;
        }

        private ListBox BuildStripList(System.Collections.IEnumerable items, FrameworkElementFactory itemFactory, bool horizontal)
        {
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
            }));
            itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

            var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            panel.SetValue(VirtualizingStackPanel.OrientationProperty, horizontal ? Orientation.Horizontal : Orientation.Vertical);

            var list = new ListBox
            {
                ItemsSource = items,
                ItemTemplate = new DataTemplate { VisualTree = itemFactory },
                ItemsPanel = new ItemsPanelTemplate(panel),
                ItemContainerStyle = itemStyle,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            return list;
        }

        /// <summary>
        /// Columns, tile width, and the decode width in PHYSICAL pixels.
        ///
        /// The DPI factor is not optional. At 150% scaling - the Claw's default - a 190 DIP tile is
        /// 285 real pixels wide, and decoding to 190 makes every cover visibly soft on exactly the
        /// devices this is built for. Rounding generously the other way is just as wrong: the memory
        /// cost grows with the square of the edge, which is the whole reason DecodePixelWidth is set
        /// at all.
        /// </summary>
        /// <summary>
        /// Repaints the grid, unless something is on top of it.
        ///
        /// The single place every BACKGROUND repaint goes through. Foreground code paths call
        /// RenderLibrary directly - they know what is on screen because they just put it there;
        /// a scan landing three seconds later does not.
        /// </summary>
        private void RenderLibraryIfNoOverlay()
        {
            if (_view != View.Library) return;
            if (_settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen || LaunchOverlayOpen) return;
            RenderLibrary();
        }

        private void MeasureGridMetrics()
        {
            double avail = LibraryRoot.ActualWidth > 0 ? LibraryRoot.ActualWidth : ActualWidth;
            if (avail <= 0) avail = 1120;
            double usable = Math.Max(200, avail - 2 * LibOuterMargin);

            _libColumns = (int)Math.Round((usable + LibTileGap) / (LibGridTileWidth + LibTileGap));
            if (_libColumns < 2) _libColumns = 2;
            if (_libColumns > 12) _libColumns = 12;

            // The column count fills the width exactly, and that turned out to be a hair too exact:
            // the vertical scrollbar appears AFTER this measurement and takes its width out of the
            // content, which clipped the right-hand edge of the last column. Ten per cent off the
            // tile leaves the gaps and the margins untouched and gives that width back.
            _libTileWidth = (usable - (_libColumns - 1) * LibTileGap) / _libColumns * LibGridTileScale;
            _libDecodeWidth = DecodeWidthFor(_libTileWidth);
        }

        /// <summary>
        /// The reel takes its size from the HEIGHT available, not the width: the cover plus its
        /// mirror has to fit the row, otherwise a horizontal strip grows a vertical scrollbar, which
        /// is the one thing a reel must never do.
        /// </summary>
        private void MeasureReelMetrics()
        {
            double availH = LibraryRoot.ActualHeight > 0 ? LibraryRoot.ActualHeight : ActualHeight - 220;
            // Row 0 (the title block) sits above, and the horizontal scrollbar takes the bottom edge;
            // leave both their space plus a little breathing room.
            availH -= 112;
            if (availH < 120) availH = 120;

            double tileH = (availH - 12) / (1 + LibReflectionFraction);
            if (tileH > LibReelMaxTileHeight) tileH = LibReelMaxTileHeight;
            if (tileH < LibReelMinTileHeight) tileH = LibReelMinTileHeight;

            _libTileWidth = tileH / LibCoverAspect;
            _libColumns = 1; // a reel is one row; Up/Down have nothing to move to
            _libDecodeWidth = DecodeWidthFor(_libTileWidth);
        }

        private int DecodeWidthFor(double tileWidth)
        {
            double dpiScale = 1.0;
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) dpiScale = src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            if (dpiScale <= 0) dpiScale = 1.0;
            return Math.Max(64, (int)Math.Round(tileWidth * dpiScale));
        }

        internal double LibTileWidth => _libTileWidth;
        internal double LibTileHeight => _libSquareTiles ? _libTileWidth : _libTileWidth * LibCoverAspect;
        internal double LibTileGapValue => LibTileGap;
        internal double LibOuterMarginValue => LibOuterMargin;
        internal double LibReflectionFractionValue => LibReflectionFraction;
        internal int LibDecodeWidth => _libDecodeWidth;
        internal int LibSelectedIndex => _libSelectedIndex;

        internal void RegisterRow(ILibrarySelectionHost host) => _liveRows.Add(host);
        internal void UnregisterRow(ILibrarySelectionHost host) => _liveRows.Remove(host);

        internal void OnTileClicked(int index)
        {
            _libSelectedIndex = index;
            ApplySelectionVisuals();
            UpdateSelectedTitle();
            LaunchSelectedGame();
        }
        #endregion

        #region Navigation
        private void MoveLibrarySelection(PadButton dir)
        {
            if (_settingsOpen) { MoveSettingsSelection(dir); return; }
            if (MiscOverlayOpen) { MoveMiscSelection(dir); return; }
            if (GameMenuOverlayOpen) { MoveGameMenuSelection(dir); return; }
            // Before the empty-grid check below, not after: the exit prompt is reachable from an
            // empty tab too, and a menu whose cursor cannot move is a menu with one usable answer.
            if (_exitPromptOpen) { MoveExitPromptSelection(dir); return; }
            if (_libraryGames.Count == 0) return;
            if (LaunchOverlayOpen) return;  // a launch screen owns the library

            int next = _libSelectedIndex;
            switch (dir)
            {
                case PadButton.Left: next -= 1; break;
                case PadButton.Right: next += 1; break;
                // In the reel there is nothing above or below - the whole grouping is one line.
                case PadButton.Up: if (_libReelMode) return; next = NeighbourRowIndex(-1); break;
                case PadButton.Down: if (_libReelMode) return; next = NeighbourRowIndex(+1); break;
                default: return;
            }
            if (next < 0 || next >= _libraryGames.Count) return;
            if (next == _libSelectedIndex) return;

            _libSelectedIndex = next;
            ApplySelectionVisuals();
            UpdateSelectedTitle();
            ScrollSelectionIntoView();
        }

        /// <summary>Repaints the cursor on the tiles that are actually alive. Rebuilding the whole
        /// view for a cursor move would throw away every cover already decoded on screen.</summary>
        /// <summary>
        /// The index one tile row up or down, keeping the column where it can.
        ///
        /// Falls back to plain column arithmetic when the row map is empty - that is the state before
        /// the first BuildGrid, and a navigation call that arrives then should still do something
        /// sensible rather than refuse.
        /// </summary>
        private int NeighbourRowIndex(int delta)
        {
            if (_libRowStarts.Count == 0) return _libSelectedIndex + delta * _libColumns;

            int row = -1;
            for (int i = 0; i < _libRowStarts.Count; i++)
            {
                if (_libSelectedIndex < _libRowStarts[i]) break;
                row = i;
            }
            if (row < 0) return _libSelectedIndex;

            int target = row + delta;
            if (target < 0 || target >= _libRowStarts.Count) return -1;

            int column = _libSelectedIndex - _libRowStarts[row];
            if (column >= _libRowCounts[target]) column = _libRowCounts[target] - 1;
            return _libRowStarts[target] + column;
        }

        private void ApplySelectionVisuals()
        {
            foreach (var row in _liveRows) row.ApplySelection(_libSelectedIndex);
        }

        private void ScrollSelectionIntoView()
        {
            if (_libList?.Items == null) return;
            int itemIndex = _libReelMode ? _libSelectedIndex : (_libColumns > 0 ? _libSelectedIndex / _libColumns : 0);
            if (itemIndex < 0 || itemIndex >= _libList.Items.Count) return;
            try { _libList.ScrollIntoView(_libList.Items[itemIndex]); } catch { }
        }

        private void SetLibraryGroup(LibraryGroup group)
        {
            // A launch countdown owns the screen: switching tabs under it would leave Center closing
            // itself from a view the user has already moved on from. The key box owns it for the same
            // reason - a shoulder press mid-typing would throw the entry away.
            if (LaunchOverlayOpen || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            if (_libraryGroup == group) return;
            _libraryGroup = group;
            // Leaving ROMs drops the system filter: coming back to a tab still narrowed to "Atari
            // 2600" from three tabs ago looks like a library that lost most of its games.
            _romSystem = null;
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void CycleLibraryGroup(int delta)
        {
            var values = new List<LibraryGroup>((LibraryGroup[])Enum.GetValues(typeof(LibraryGroup)));
            // Same rule as the chip row: without Playnite the ROM tab is not in the cycle either,
            // otherwise the shoulders stop on a tab that has nothing to show.
            if (!Library.PlayniteSource.IsPresent) values.Remove(LibraryGroup.Roms);
            if (!Library.FavoritesStore.Any()) values.Remove(LibraryGroup.Favorites);
            if (values.Count == 0) return;

            int i = values.IndexOf(_libraryGroup) + delta;
            if (i < 0) i = values.Count - 1;
            if (i >= values.Count) i = 0;
            SetLibraryGroup(values[i]);
        }

        /// <summary>
        /// Re-lays the view when the window size changes what fits. Guarded on the derived metric
        /// rather than on the raw size: a resize fires continuously while a window is dragged, and
        /// rebuilding on every pixel would throw away the decoded covers over and over.
        /// </summary>
        private void OnLibrarySizeChanged()
        {
            if (_view != View.Library || LaunchOverlayOpen) return;
            if (LibraryRoot == null || _libList == null) return;

            if (_libReelMode)
            {
                double before = _libTileWidth;
                MeasureReelMetrics();
                if (Math.Abs(_libTileWidth - before) < 8) return;
            }
            else
            {
                int before = _libColumns;
                MeasureGridMetrics();
                if (_libColumns == before) return;
            }
            RenderLibrary();
        }
        #endregion

        #region Sorting and grouping
        /// <summary>What a tab can be grouped BY, or nothing.</summary>
        private enum GroupingKind
        {
            None,
            /// <summary>The All tab: Steam, Epic, Xbox, ...</summary>
            Platform,
            /// <summary>The ROM tab with every system shown at once.</summary>
            System,
        }

        /// <summary>
        /// Recent has no sort of its own: it IS a sort, by when you last played. Offering A-Z there
        /// would be offering to destroy the only thing the tab is for. The same goes for the ROM
        /// tab's own Recent system.
        /// </summary>
        private bool SortingAvailable =>
            _libraryGroup != LibraryGroup.Recent
            && !(_libraryGroup == LibraryGroup.Roms && _romSystem == GameLibrary.RomRecentSystem);

        private GroupingKind Grouping
        {
            get
            {
                if (_libraryGroup == LibraryGroup.All) return GroupingKind.Platform;
                // Only with every system on screen. Inside one system, grouping by system would
                // produce exactly one group with a heading nobody needs.
                if (_libraryGroup == LibraryGroup.Roms && _romSystem == null) return GroupingKind.System;
                return GroupingKind.None;
            }
        }

        private bool GroupingOn => Grouping != GroupingKind.None && Core.CenterSettings.LibraryGrouped;

        /// <summary>
        /// Puts the tab's entries in the order they will be drawn in.
        ///
        /// Grouping happens FIRST and sorting inside it, which is what makes the two independent: the
        /// user's A-Z choice keeps meaning the same thing whether or not there are headings. The
        /// group order itself is alphabetical too and does NOT follow the sort direction - Z-A is a
        /// statement about game titles, and flipping the platform headings with them would be an
        /// answer to a question nobody asked.
        /// </summary>
        private List<GameEntry> ArrangeForDisplay(IReadOnlyList<GameEntry> games)
        {
            var list = new List<GameEntry>(games);
            if (!SortingAvailable) return list;

            bool desc = Core.CenterSettings.LibrarySortDescending;
            Comparison<GameEntry> byTitle = (a, b) =>
            {
                int c = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                return desc ? -c : c;
            };

            if (!GroupingOn) { list.Sort(byTitle); return list; }

            var kind = Grouping;
            var buckets = new SortedDictionary<string, List<GameEntry>>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var g in list)
            {
                string key = kind == GroupingKind.Platform ? PlatformLabel(g) : (g.SystemName ?? "Other");
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<GameEntry>();
                bucket.Add(g);
            }

            _libGroupBreaks.Clear();
            var arranged = new List<GameEntry>(list.Count);
            foreach (var pair in buckets)
            {
                pair.Value.Sort(byTitle);
                _libGroupBreaks[arranged.Count] = pair.Key;
                arranged.AddRange(pair.Value);
            }
            return arranged;
        }

        /// <summary>Flat index of the first entry of a group -> its heading. Rebuilt by
        /// ArrangeForDisplay and read by BuildGrid, so the two cannot disagree about where a group
        /// starts.</summary>
        private readonly Dictionary<int, string> _libGroupBreaks = new Dictionary<int, string>();

        private static string PlatformLabel(GameEntry g)
        {
            switch (g.Store)
            {
                case GameStore.Steam: return "Steam";
                case GameStore.Epic: return "Epic";
                case GameStore.Xbox: return "Xbox";
                case GameStore.Playnite: return "ROMs";
                case GameStore.Misc: return "Apps";
                default: return "Other";
            }
        }

        private void ToggleSortDirection()
        {
            if (!SortingAvailable) return;
            Core.CenterSettings.LibrarySortDescending = !Core.CenterSettings.LibrarySortDescending;
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
        }

        private void SetSortDirection(bool descending)
        {
            if (!SortingAvailable || Core.CenterSettings.LibrarySortDescending == descending) return;
            Core.CenterSettings.LibrarySortDescending = descending;
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
        }

        private void SetGrouping(bool on)
        {
            if (Grouping == GroupingKind.None || Core.CenterSettings.LibraryGrouped == on) return;
            Core.CenterSettings.LibraryGrouped = on;
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
        }

        /// <summary>
        /// The sort and grouping readout, top right of the tab row.
        ///
        /// A READOUT, not a menu: there is nothing to open and nothing to focus. The right stick
        /// drives it directly - up/down for the order, left/right for the grouping - so what is on
        /// screen is a statement of the current state plus the gesture that changes it.
        /// </summary>
        private UIElement BuildSortStrip()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28, 0, 0, 0),
            };

            if (!SortingAvailable && Grouping == GroupingKind.None) return row;

            // Named ONCE, in front of both chips, and each chip then carries only its own axis.
            // Writing "right stick" into both would say the same thing twice in a row where the
            // two chips already sit side by side - the caption covers the pair, the arrows say
            // which way each of them is driven.
            row.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Right stick"),
                FontSize = 11,
                Foreground = UiHelpers.Subtle,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });

            if (SortingAvailable)
            {
                bool desc = Core.CenterSettings.LibrarySortDescending;
                // The arrow is the AXIS, not the direction. The direction is the label beside it,
                // which says it in words an arrow cannot - "A-Z" beats "up".
                row.Children.Add(SortChip("↕", desc ? "Z-A" : "A-Z", true));
            }

            if (Grouping != GroupingKind.None)
            {
                bool on = GroupingOn;
                row.Children.Add(SortChip("↔", Core.Loc.T(Grouping == GroupingKind.Platform ? "Platform" : "System"), on));
            }

            return row;
        }

        private UIElement SortChip(string glyph, string label, bool lit)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // Plain UI font, not the icon font: these are two ordinary arrow characters and
            // Segoe UI has both. Sending them through Segoe Fluent Icons, whose private range
            // is what it is actually for, is one missing glyph away from saying nothing.
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = lit ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = lit ? UiHelpers.Text : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
            });

            return new Border
            {
                Child = content,
                Background = lit ? UiHelpers.Card : Brushes.Transparent,
                BorderBrush = UiHelpers.Subtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                // Dimmed rather than hidden when off: the chip is also the only place that says the
                // gesture exists, so an unused grouping must still be visible to be discovered.
                Opacity = lit ? 1.0 : 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        #endregion

        #region Immersive mode
        // Two timers, two different questions. The idle timer asks "has the user stopped?"; the
        // reveal timer asks "has the peek at the footer run its course?". One timer doing both would
        // have to remember which of the two it is counting, which is a bug waiting for a fast thumb.
        private DispatcherTimer _immersiveIdleTimer;
        private DispatcherTimer _footerRevealTimer;
        private bool _immersiveDim;
        private bool _footerRevealed;

        private const double ImmersiveIdleSeconds = 2;
        private const double FooterRevealSeconds = 4;

        /// <summary>True while the tab strip should be showing plain labels without their counts.</summary>
        internal bool ImmersiveCountsHidden => _immersiveDim;

        private bool ImmersiveActive => _view == View.Library && Core.CenterSettings.ImmersiveMode;

        /// <summary>
        /// Called on every pad press while the library is up. It does NOT undim anything.
        ///
        /// Its whole job is to keep the idle countdown honest and to switch immersive mode off the
        /// moment the setting is off or the library is left. Bringing the chrome back belongs to the
        /// two gestures that ask for it: LB/RB for the tabs, a right-stick flick for the footer.
        /// </summary>
        private void NoteLibraryActivity()
        {
            if (!ImmersiveActive) { StopImmersive(); return; }
            if (!_immersiveDim) RestartIdleTimer();
        }

        /// <summary>
        /// Only LB/RB bring the tab strip back.
        ///
        /// The counts and the bright strip answer ONE question - which tab am I on, and how much is
        /// in each - so they belong to the buttons that change the tab. Every other press moves a
        /// cursor inside the tab the user is already looking at, and lighting the strip up for that
        /// is exactly the flicker immersive mode exists to remove.
        /// </summary>
        private void NoteTabChange()
        {
            if (!ImmersiveActive) return;
            _immersiveDim = false;
            ApplyImmersiveChrome();
            RestartIdleTimer();
        }

        private void RestartIdleTimer()
        {
            if (_immersiveIdleTimer == null)
            {
                _immersiveIdleTimer = new DispatcherTimer();
                _immersiveIdleTimer.Tick += (_, __) =>
                {
                    _immersiveIdleTimer.Stop();
                    if (!ImmersiveActive) return;
                    _immersiveDim = true;
                    _footerRevealed = false;
                    ApplyImmersiveChrome();
                };
            }
            _immersiveIdleTimer.Interval = TimeSpan.FromSeconds(ImmersiveIdleSeconds);
            _immersiveIdleTimer.Stop();
            _immersiveIdleTimer.Start();
        }

        /// <summary>A flick UP on the right stick: show the footer for a few seconds.</summary>
        internal void RevealFooterBriefly()
        {
            if (!ImmersiveActive || !_immersiveDim) return;

            _footerRevealed = true;
            ApplyImmersiveChrome();

            if (_footerRevealTimer == null)
            {
                _footerRevealTimer = new DispatcherTimer();
                _footerRevealTimer.Tick += (_, __) =>
                {
                    _footerRevealTimer.Stop();
                    _footerRevealed = false;
                    ApplyImmersiveChrome();
                };
            }
            _footerRevealTimer.Interval = TimeSpan.FromSeconds(FooterRevealSeconds);
            // Restarted, not left running: a second flick during the peek should buy another four
            // seconds, not let the first timer close it early.
            _footerRevealTimer.Stop();
            _footerRevealTimer.Start();
        }

        /// <summary>Puts everything back and stops both timers. Called on the way out of the library
        /// and whenever the setting is turned off - a dimmed tab strip left behind on the start
        /// screen would look like a rendering fault.</summary>
        private void StopImmersive()
        {
            _immersiveIdleTimer?.Stop();
            _footerRevealTimer?.Stop();
            if (!_immersiveDim && !_footerRevealed) { ApplyImmersiveChrome(); return; }
            _immersiveDim = false;
            _footerRevealed = false;
            ApplyImmersiveChrome();
        }

        /// <summary>
        /// True while a prompt, a menu or the settings own the library area.
        ///
        /// Immersive mode hides the footer on the SHELF - the one screen whose content speaks for
        /// itself. On any of these the footer is the only place the available buttons are named, and
        /// a confirmation whose Yes and No are invisible is not immersive, it is a dead end.
        /// </summary>
        private bool LibraryOverlayOwnsScreen =>
            _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen || LaunchOverlayOpen;

        /// <summary>
        /// The footer and its stand-in hint only. Cheap, and called from every library action-bar
        /// refresh - which is what makes an overlay opening or closing bring the footer with it,
        /// without each of the six of them having to remember to.
        ///
        /// Kept apart from ApplyImmersiveChrome deliberately: that one rebuilds the tab strip, and
        /// doing that on every action-bar refresh would repaint the chips several times per press.
        /// </summary>
        private void ApplyFooterVisibility()
        {
            // ⚠️ THE FOOTER FOLLOWS THE STICK CLICK ALONE, not the idle state the tabs follow. It is
            // hidden for as long as immersive mode is on and shown only in the seconds after R3.
            //
            // Tying it to idle as well meant it came back on the first press of anything and went
            // away two seconds later, over and over, on a row that spans the bottom of the screen -
            // which is more distracting than either of the two states it was moving between.
            bool footerHidden = ImmersiveActive && !_footerRevealed && !LibraryOverlayOwnsScreen;

            if (FooterBar != null) FooterBar.Visibility = footerHidden ? Visibility.Collapsed : Visibility.Visible;
            if (ImmersiveHint != null)
            {
                // Set here rather than left to the XAML: it is the one piece of text in the shell
                // that is authored as a literal attribute, so it is the one Loc never sees. Assigned
                // on every pass because the language can change while the window is open.
                ImmersiveHint.Text = Core.Loc.T("Click the right stick to show the button hints");
                ImmersiveHint.Visibility = footerHidden ? Visibility.Visible : Visibility.Collapsed;
                // As low as it can go in a GRID, higher in the reel.
                //
                // The two tabs put different things at the bottom edge. Recent's covers lie in one
                // horizontal line with the scrollbar underneath them, so there is room there and the
                // hint needs to clear the bar. A grid runs its last row all the way down, and every
                // pixel the hint sits up from the edge is a pixel of cover art it stands on.
                ImmersiveHint.Margin = new Thickness(0, 0, 0, _libReelMode ? 26 : 2);
            }
        }

        private void ApplyImmersiveChrome()
        {
            bool dim = _immersiveDim && ImmersiveActive;
            if (TabStrip != null) TabStrip.Opacity = dim ? 0.35 : 1.0;

            ApplyFooterVisibility();

            // The counts live inside the chips, so they only change on a rebuild.
            RefreshTabStrip();
        }
        #endregion

        #region Settings
        // The settings screen lives behind Select, and it holds everything about the library that is
        // remembered rather than chosen per session. One screen, because three switches scattered
        // across three places is how a setting ends up being looked for in the fourth.
        private int _settingsIndex;
        private TextBox _artKeyBox;
        private TextBlock _artKeyStatus;
        private readonly List<Border> _settingsRows = new List<Border>();

        // Row indices as names, not magic numbers - the key row is the only one whose activation
        // (focus a text box, not toggle a value) and A-button label differ from the rest, and a
        // bare "3" scattered across three places is what breaks silently when a row is added above it.
        private const int SettingsStartInLibraryRow = 0;
        private const int SettingsSquareRomArtRow = 1;
        private const int SettingsImmersiveRow = 2;
        private const int SettingsLaunchBehaviorRow = 3;
        private const int SettingsStartWithClawTweaksRow = 4;
        private const int SettingsRunInBackgroundRow = 5;

        /// <summary>The key row, and it is ALWAYS the last one: it holds a text box, so it spans both
        /// columns and sits on its own line below the pairs. The navigation maths below derives the
        /// pair count from this, so adding a switch above it needs no other change.</summary>
        private const int SettingsKeyRow = 6;

        private const int SettingsColumns = 2;

        private void OpenLibrarySettings()
        {
            _settingsOpen = true;
            _settingsIndex = 0;
            RenderLibrarySettings();
            RefreshActionBar();
        }

        private void CloseLibrarySettings()
        {
            _settingsOpen = false;
            _artKeyBox = null;
            _artKeyStatus = null;
            _settingsRows.Clear();
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void RenderLibrarySettings()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _settingsRows.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 940,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Library settings"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            // Two columns, because six stacked rows ran off the bottom of an eight-inch panel and the
            // key row - the one people are sent here for - was the one below the fold.
            var pairs = new UniformGrid { Columns = SettingsColumns };
            pairs.Children.Add(BuildSettingRow(SettingsStartInLibraryRow, "Start in the library",
                Core.CenterSettings.OpenLibraryAtStartup, null));
            pairs.Children.Add(BuildSettingRow(SettingsSquareRomArtRow, "Square ROM art",
                _squareRomArt, null));
            pairs.Children.Add(BuildSettingRow(SettingsImmersiveRow, "Immersive mode",
                Core.CenterSettings.ImmersiveMode, null));
            pairs.Children.Add(BuildSettingRow(SettingsLaunchBehaviorRow, "After starting a game",
                null, LaunchBehaviorLabel(Core.CenterSettings.LaunchBehavior)));
            pairs.Children.Add(BuildSettingRow(SettingsStartWithClawTweaksRow, "Start Center with ClawTweaks",
                Core.CenterSettings.StartCenterWithClawTweaks, null));
            pairs.Children.Add(BuildSettingRow(SettingsRunInBackgroundRow, "Run in background",
                Core.CenterSettings.RunInBackground, null));
            stack.Children.Add(pairs);

            var keyRow = BuildSettingRow(SettingsKeyRow, "SteamGridDB key", null, null);
            var keyStack = (StackPanel)((Grid)keyRow.Child).Children[0];
            _artKeyBox = new TextBox
            {
                Text = Core.CenterSettings.SteamGridDbApiKey,
                FontSize = 15,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 8, 0, 0),
            };
            keyStack.Children.Add(_artKeyBox);
            _artKeyStatus = new TextBlock
            {
                Text = Core.Loc.T(Library.SteamGridDb.HasKey
                    ? "Set. Covers are downloaded for games with none." : "Not set."),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            keyStack.Children.Add(_artKeyStatus);
            stack.Children.Add(keyRow);

            LibraryRoot.Children.Add(stack);
            ApplySettingsSelection();
        }

        /// <summary>
        /// One settings row: title on the left, its state on the right.
        ///
        /// A boolean gets a SWITCH; anything else keeps its value as text. The switch is the shape
        /// people expect from a setting that has two states, and it reads from across the room in a
        /// way a word does not - which is what a handheld screen actually asks of it.
        /// </summary>
        private Border BuildSettingRow(int index, string title, bool? on, string valueText)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(title),
                FontSize = 17,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);

            UIElement state = on.HasValue ? BuildToggle(on.Value)
                : valueText != null ? new TextBlock
                {
                    Text = valueText,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                } : null;

            if (state != null)
            {
                Grid.SetColumn(state, 1);
                grid.Children.Add(state);
            }

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
            row.MouseLeftButtonUp += (_, __) => { _settingsIndex = index; ActivateSetting(); };
            _settingsRows.Add(row);
            return row;
        }

        /// <summary>
        /// A switch. The knob is placed by ALIGNMENT rather than by a margin, so it sits against the
        /// correct end whatever the track ends up measuring - a hard-coded offset only looks right at
        /// one size, and this control is built at exactly one size today.
        /// </summary>
        private static UIElement BuildToggle(bool on)
        {
            var knob = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = on ? Brushes.White : UiHelpers.Subtle,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };

            return new Border
            {
                Width = 44,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = on ? UiHelpers.Ok : Brushes.Transparent,
                BorderBrush = on ? UiHelpers.Ok : UiHelpers.Subtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = knob,
            };
        }

        private void ApplySettingsSelection()
        {
            foreach (var row in _settingsRows)
                row.BorderBrush = row.Tag is int i && i == _settingsIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        /// <summary>
        /// Two columns of switches with one full-width row underneath.
        ///
        /// The pair count comes from the row list rather than from a constant: the key row is always
        /// the last one, so everything above it is a pair. Adding a switch shifts the maths by itself.
        /// </summary>
        private void MoveSettingsSelection(PadButton dir)
        {
            if (_settingsRows.Count == 0) return;
            int last = _settingsRows.Count - 1;   // the full-width key row
            int pairs = last;                     // everything above it
            int next = _settingsIndex;

            switch (dir)
            {
                case PadButton.Left:
                    if (_settingsIndex >= pairs || _settingsIndex % SettingsColumns == 0) return;
                    next = _settingsIndex - 1;
                    break;
                case PadButton.Right:
                    if (_settingsIndex >= pairs || _settingsIndex % SettingsColumns == SettingsColumns - 1) return;
                    next = _settingsIndex + 1;
                    if (next >= pairs) return;
                    break;
                case PadButton.Up:
                    // From the key row, back into the LAST pair row rather than to a fixed index -
                    // with an odd number of switches the last row is half empty, and landing on a
                    // cell that is not there would leave the cursor invisible.
                    if (_settingsIndex == last) { next = Math.Max(0, pairs - SettingsColumns); break; }
                    if (_settingsIndex < SettingsColumns) return;
                    next = _settingsIndex - SettingsColumns;
                    break;
                case PadButton.Down:
                    if (_settingsIndex >= pairs) return;
                    next = _settingsIndex + SettingsColumns;
                    if (next >= pairs) next = last;
                    break;
                default: return;
            }

            if (next == _settingsIndex || next < 0 || next > last) return;
            _settingsIndex = next;
            ApplySettingsSelection();
            RefreshActionBar();
        }

        private void ActivateSetting()
        {
            switch (_settingsIndex)
            {
                case SettingsStartInLibraryRow:
                    Core.CenterSettings.OpenLibraryAtStartup = !Core.CenterSettings.OpenLibraryAtStartup;
                    break;
                case SettingsSquareRomArtRow:
                    _squareRomArt = !_squareRomArt;
                    Core.CenterSettings.SquareRomArt = _squareRomArt;
                    break;
                case SettingsImmersiveRow:
                    Core.CenterSettings.ImmersiveMode = !Core.CenterSettings.ImmersiveMode;
                    // Turning it OFF has to undo it on the spot. The settings screen is reached from
                    // the library, so a dimmed strip and a hidden footer would otherwise still be
                    // there when the user goes back - looking like the switch did nothing.
                    StopImmersive();
                    break;
                case SettingsLaunchBehaviorRow:
                    Core.CenterSettings.LaunchBehavior = NextLaunchBehavior(Core.CenterSettings.LaunchBehavior);
                    break;
                case SettingsStartWithClawTweaksRow:
                    Core.CenterSettings.StartCenterWithClawTweaks = !Core.CenterSettings.StartCenterWithClawTweaks;
                    break;
                case SettingsRunInBackgroundRow:
                    Core.CenterSettings.RunInBackground = !Core.CenterSettings.RunInBackground;
                    // Takes effect immediately, not just on the next launch - a tray icon that only
                    // appears after a restart would look like the toggle silently failed.
                    SyncTrayIcon();
                    break;
                case SettingsKeyRow:
                    _artKeyBox?.Focus();
                    _artKeyBox?.SelectAll();
                    return;
            }
            RenderLibrarySettings();
            RefreshActionBar();
        }

        private static string LaunchBehaviorLabel(Core.LaunchBehavior behavior)
        {
            switch (behavior)
            {
                case Core.LaunchBehavior.Minimize: return Core.Loc.T("Minimize");
                case Core.LaunchBehavior.StayOpen: return Core.Loc.T("Stay open");
                default: return Core.Loc.T("Close Center");
            }
        }

        /// <summary>A three-way cycle rather than a toggle - there is no natural "off" state among
        /// three genuinely different behaviours, so A steps through all of them in a fixed order.</summary>
        private static Core.LaunchBehavior NextLaunchBehavior(Core.LaunchBehavior current)
        {
            switch (current)
            {
                case Core.LaunchBehavior.Close: return Core.LaunchBehavior.Minimize;
                case Core.LaunchBehavior.Minimize: return Core.LaunchBehavior.StayOpen;
                default: return Core.LaunchBehavior.Close;
            }
        }

        /// <summary>
        /// Stores the key on the way out, after checking it against the API.
        ///
        /// Checked BEFORE it is stored: a typo that is only discovered by covers never appearing is a
        /// bug report about the wrong thing entirely.
        /// </summary>
        private async void SaveArtKeyAndClose()
        {
            string key = (_artKeyBox?.Text ?? string.Empty).Trim();
            string stored = Core.CenterSettings.SteamGridDbApiKey ?? string.Empty;

            if (key == stored) { CloseLibrarySettings(); return; }

            if (key.Length == 0)
            {
                Core.CenterSettings.SteamGridDbApiKey = string.Empty;
                CloseLibrarySettings();
                return;
            }

            if (_artKeyStatus != null) _artKeyStatus.Text = "Checking…";
            bool ok = await Library.SteamGridDb.VerifyKeyAsync(key, CancellationToken.None);
            if (!ok)
            {
                if (_artKeyStatus != null) _artKeyStatus.Text = "That key was rejected.";
                return;
            }

            Core.CenterSettings.SteamGridDbApiKey = key;
            CloseLibrarySettings();
            StartArtFetch();
        }

        /// <summary>
        /// Downloads the covers nothing local could supply. Runs behind the finished library, one
        /// request at a time, and redraws as pictures arrive.
        /// </summary>
        private void StartArtFetch()
        {
            if (!Library.SteamGridDb.HasKey || !_libraryScanned) return;

            _artFetchCts?.Cancel();
            _artFetchCts = new CancellationTokenSource();
            var ct = _artFetchCts.Token;
            var games = _library.Games;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await Library.SteamGridDb.FetchMissingAsync(games, ct, () => Dispatcher.Invoke(() =>
                    {
                        if (_view == View.Library && !_settingsOpen && !LaunchOverlayOpen) RenderLibrary();
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Core.InstallLog.Write("Cover art fetch failed: " + ex.Message); }
            }, ct);
        }
        #endregion

        #region Launch
        /// <summary>
        /// Starts the selected game, then does whatever the launch-behaviour setting says.
        ///
        /// Not "launch, then act". Steam can take several seconds to put a window up, and a Center
        /// that vanishes the instant A is pressed looks like a crash - so the user gets a line saying
        /// what is starting, and the window reacts a couple of seconds later.
        ///
        /// Closing means EXITING, not hiding. Staying open and minimising are both safe, though, and
        /// the reason is worth keeping written down because this comment used to claim the opposite:
        /// XInputNavigator.OnTick returns immediately unless the window is active, so a background
        /// Center never sees the sticks the game is using, and the window is not topmost, so it
        /// cannot end up over one either.
        /// </summary>
        /// <summary>
        /// A on a game: ASK FIRST. Nothing is started here.
        ///
        /// A handheld grid is navigated with a thumbstick and confirmed with the same button that
        /// scrolls past a dozen covers to get there, so the cost of a stray press was launching a
        /// game - which on Steam can mean a client coming up, a window taking the foreground, and a
        /// download resuming. One extra press is cheap; an accidental launch is not.
        /// </summary>
        private void LaunchSelectedGame()
        {
            var game = SelectedGame;
            if (game == null || LaunchOverlayOpen) return;

            _launchTarget = game;
            _launchPrompt = LaunchPrompt.Confirm;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        /// <summary>A on the confirmation: this is where the game actually starts.</summary>
        private void ConfirmLaunch()
        {
            var game = _launchTarget;
            if (game == null || _launchPrompt != LaunchPrompt.Confirm) return;

            bool started = GameLibrary.Launch(game, out var startedProcess);
            if (started)
            {
                _library.History.Note(game.InstallDir, DateTime.Now);
                _library.History.SaveIfChanged();
            }

            _launchPrompt = started ? LaunchPrompt.Running : LaunchPrompt.Failed;
            RenderLaunchOverlay();
            RefreshActionBar();

            // Tracked on EVERY successful launch now, not only when the window minimises. The running
            // screen has to know when the game ends, and that is the same question the restore was
            // already answering - one tracker, two readers.
            if (started) StartTrackingForRestore(game, startedProcess);
        }

        /// <summary>
        /// "Hide" on the running screen. The game keeps running; only Center goes away, in whatever
        /// way the user picked under After starting a game.
        ///
        /// The setting is read HERE rather than at launch time, because the screen it is acting on
        /// stays up for as long as the game does - reading it minutes earlier would act on a choice
        /// the user could have changed since.
        /// </summary>
        private void HideAfterLaunch()
        {
            if (_launchPrompt != LaunchPrompt.Running) return;

            switch (Core.CenterSettings.LaunchBehavior)
            {
                case Core.LaunchBehavior.Minimize:
                    WindowState = WindowState.Minimized;
                    break;
                case Core.LaunchBehavior.StayOpen:
                    // Nothing to hide. Leaving the running screen up would be a dead end, so the
                    // library comes back - the tracker keeps watching either way.
                    ClearLaunchOverlay();
                    break;
                default:
                    // Close means exit. The tracker dies with the process, which is correct: there is
                    // no window left to bring back.
                    Application.Current.Shutdown();
                    return;
            }

            RefreshActionBar();
        }

        private void ClearLaunchOverlay()
        {
            _launchPrompt = LaunchPrompt.None;
            _launchTarget = null;
            RenderLibrary();
            RefreshActionBar();
        }

        /// <summary>
        /// Watches the just-launched game (see GameRunTracker) and brings Center back once it ends -
        /// the half of Playnite's Minimize/Restore pairing that used to be missing: Center could
        /// minimize itself, but nothing ever brought it back.
        ///
        /// Cancels any tracker already running first. Only one game is ever "the one Center is
        /// waiting on" - launching a second game while the first's tracker is still watching an old,
        /// already-closed process would otherwise leave two background loops racing to restore the
        /// same window.
        /// </summary>
        private void StartTrackingForRestore(GameEntry game, System.Diagnostics.Process startedProcess)
        {
            _gameTrackCts?.Cancel();
            _gameTrackCts = new CancellationTokenSource();
            var ct = _gameTrackCts.Token;

            Library.GameRunTracker.Track(game, startedProcess, ct, () => Dispatcher.Invoke(() =>
            {
                if (ct.IsCancellationRequested) return;
                RestoreAfterGameEnded();
            }));
        }

        /// <summary>
        /// Brings the window back after the tracked game ends.
        ///
        /// The 1-second delay before restoring is Playnite's own fix, not a guess - their comment on
        /// it (GamesEditor.Controllers_Stopped) says some emulators (RPCS3 named specifically) hand
        /// focus back to Windows in a way that leaves the restoring app visually back but not actually
        /// active until something else nudges it. Cheap to keep even where it turns out unnecessary.
        /// </summary>
        private void RestoreAfterGameEnded()
        {
            var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            restoreTimer.Tick += (_, __) =>
            {
                restoreTimer.Stop();
                // The running screen has served its purpose the moment the game ends - leaving it up
                // would greet the user with a line claiming a game is running that just stopped.
                if (_launchPrompt == LaunchPrompt.Running) ClearLaunchOverlay();

                // Covers both ways the window could be out of sight: minimized (the normal case for
                // this path) and hidden-to-tray (RunInBackground On) if the user closed it by hand
                // while this same tracker was still watching the game it started before that.
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                try { Ui.WindowMode.ForceForeground(this); } catch { }
            };
            restoreTimer.Start();
        }

        private void RenderLaunchOverlay()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();

            string title = _launchTarget?.Title ?? string.Empty;
            string head, sub;
            switch (_launchPrompt)
            {
                case LaunchPrompt.Confirm:
                    head = "Start " + title + "?";
                    sub = null;
                    break;
                case LaunchPrompt.Running:
                    head = title + " is running";
                    sub = "Center comes back when the game ends.";
                    break;
                default:
                    head = "Could not start " + title + ".";
                    sub = null;
                    break;
            }

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = head,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = _launchPrompt == LaunchPrompt.Failed ? UiHelpers.Error : UiHelpers.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            });
            if (sub != null)
                stack.Children.Add(new TextBlock
                {
                    Text = sub,
                    FontSize = 15,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                });

            LibraryRoot.Children.Add(stack);
        }

        /// <summary>
        /// Drops whatever launch screen is up. Called on the way out of the library, so a running
        /// screen cannot survive into a tab it says nothing about.
        ///
        /// It does NOT stop the game. On the confirmation screen there is nothing to stop; after it,
        /// ShellExecute has long since fired and the handler we got back is Steam or the shell, never
        /// the game itself.
        /// </summary>
        private void CancelPendingClose()
        {
            _launchPrompt = LaunchPrompt.None;
            _launchTarget = null;
        }
        #endregion

        #region Footer
        #region Info
        private void OpenLibraryInfo()
        {
            if (_launchPrompt != LaunchPrompt.None || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            if (_infoOpen) return;

            _exitPromptOpen = false;
            _infoOpen = true;
            Core.CenterSettings.LibraryInfoSeen = true;
            RenderLibraryInfo();
            RefreshActionBar();
        }

        private void CloseLibraryInfo()
        {
            _infoOpen = false;
            RenderLibrary();
            // Rebuilds the info chip, which is how the pulse and the accent go away: opening the info
            // set LibraryInfoSeen, but nothing on screen has re-read it yet.
            RefreshTabStrip();
            RefreshActionBar();
        }

        // The info page used to open ITSELF on the first library visit. It does not any more: the
        // first thing someone wants from a library is to see their games, not a page of text in front
        // of them. What replaced it is the info chip in the tab strip - accented and pulsing until it
        // has been opened once (see BuildInfoButton). Do not put the auto-open back without also
        // removing the pulse; the flag they both hang off is the same one, so together they cancel
        // each other out - the auto-open marks the page seen before the pulse is ever visible.

        private void RenderLibraryInfo()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 640,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Your ClawTweaks library"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 18),
            });

            // Top line, above every heading: it is the one setting that changes what the user sees
            // on the NEXT start, so it belongs where it is read before anything else.
            stack.Children.Add(InfoLead("Turn on Settings \u2192 Start in the library to open here every time."));

            stack.Children.Add(InfoHeading("Your games"));
            stack.Children.Add(InfoLine("Shows your installed Steam, Xbox and Epic games."));
            stack.Children.Add(InfoLine("Steam cover art is found automatically."));
            stack.Children.Add(InfoLine("Add ROMs in Playnite \u2014 they are imported from there."));

            // These four were one flat run of bullets, which read as four unrelated facts. They are
            // one task in order, so they get a heading and an indent under it.
            stack.Children.Add(InfoHeading("Covers for Xbox, Epic and your own games"));
            stack.Children.Add(InfoLine("Create a free SteamGridDB account.", indent: true));
            stack.Children.Add(InfoLine("Copy your key from Preferences \u2192 API Key.", indent: true));
            stack.Children.Add(InfoLine("Add it under Settings \u2192 SteamGridDB key.", indent: true));
            stack.Children.Add(new TextBlock
            {
                Text = SteamGridDbUrl,
                FontSize = 13,
                Foreground = UiHelpers.Accent,
                // Lines up with the TEXT of the bullets above, not with their dots.
                Margin = new Thickness(InfoIndent + InfoBulletColumn, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            stack.Children.Add(InfoHeading("Immersive mode"));
            stack.Children.Add(InfoLine("Turn it on under Settings to show covers only."));

            stack.Children.Add(InfoHeading("Full screen with AnyFSE"));
            stack.Children.Add(InfoLine("Add ClawTweaks Center in AnyFSE as your full screen app.", indent: true));
            stack.Children.Add(InfoLine("Enter the path below, then turn on Start in the library.", indent: true));
            stack.Children.Add(BuildAnyFsePathRow());
            stack.Children.Add(new TextBlock
            {
                Text = AnyFseUrl,
                FontSize = 13,
                Foreground = UiHelpers.Accent,
                Margin = new Thickness(InfoIndent + InfoBulletColumn, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            LibraryRoot.Children.Add(stack);
        }

        /// <summary>Width of the bullet column, and how far a sub-step sits in from the margin. Both
        /// are named because the SteamGridDB link under the last sub-step has to line up with the
        /// TEXT of the bullets above it, not with their dots.</summary>
        private const double InfoBulletColumn = 18;
        private const double InfoIndent = 18;

        /// <summary>Section heading. What follows it is one topic - nine bullets in a row read as
        /// nine unrelated facts.</summary>
        private static UIElement InfoHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiHelpers.Subtle,
            Margin = new Thickness(0, 14, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>The line above the first heading. No bullet: it belongs to no section.</summary>
        private static UIElement InfoLead(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 16,
            Foreground = UiHelpers.Text,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>One statement per line, and the bullet is a real element rather than a character
        /// in the text: a wrapped line then indents under the words instead of under the dot.</summary>
        private static UIElement InfoLine(string text, bool indent = false)
        {
            var grid = new Grid { Margin = new Thickness(indent ? InfoIndent : 0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InfoBulletColumn) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new TextBlock
            {
                Text = "\u2022",
                FontSize = 16,
                Foreground = UiHelpers.Subtle,
            });

            var body = new TextBlock
            {
                Text = Core.Loc.T(text),
                FontSize = 16,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            return grid;
        }

        private static UIElement InfoGap() => new Border { Height = 10 };

        /// <summary>The Center path AnyFSE has to be pointed at, with a Copy button next to it.
        /// Typing it out on a handheld means an on-screen keyboard and a path with two capitalised
        /// folder names in it, so the path is offered rather than described.</summary>
        private UIElement BuildAnyFsePathRow()
        {
            var row = new Grid { Margin = new Thickness(InfoIndent + InfoBulletColumn, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var path = new TextBox
            {
                Text = Core.SelfInstaller.InstalledExe,
                IsReadOnly = true,
                FontSize = 13,
                Padding = new Thickness(8, 5, 8, 5),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(path);

            var copy = new Button
            {
                Content = "Copy",
                Style = (Style)Application.Current.Resources["SetupButton"],
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
            };
            copy.Click += (_, __) => CopyAnyFsePath();
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            return row;
        }

        /// <summary>Puts the installed Center path on the clipboard. Wrapped because the clipboard is
        /// a shared OS resource - another process holding it open makes Clipboard.SetText throw, and
        /// a failed copy must not take the library down with it.</summary>
        private void CopyAnyFsePath()
        {
            try { Clipboard.SetText(Core.SelfInstaller.InstalledExe); }
            catch (Exception ex) { Core.InstallLog.Write("Copying the Center path failed: " + ex.Message); }
        }
        #endregion

        #region Leaving the library
        private void OpenExitPrompt()
        {
            if (LaunchOverlayOpen || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            _exitPromptOpen = true;
            _exitPromptIndex = 0;
            RenderExitPrompt();
            RefreshActionBar();
        }

        private void CloseExitPrompt()
        {
            _exitPromptOpen = false;
            _exitPromptRows.Clear();
            RenderLibrary();
            RefreshActionBar();
        }

        private void RenderExitPrompt()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _exitPromptRows.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Leave the library"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            // Ordered by how much they throw away: least first. Tray keeps everything, Start screen
            // keeps the process, Close keeps nothing.
            stack.Children.Add(ExitPromptRow(0, "\uE7C4", "Minimize to tray", "Center keeps running."));
            stack.Children.Add(ExitPromptRow(1, "\uE80F", "Center start screen", "Leave the library open."));
            stack.Children.Add(ExitPromptRow(2, "\uE711", "Close Center", "Ends Center completely."));

            LibraryRoot.Children.Add(stack);
            ApplyExitPromptSelection();
        }

        private Border ExitPromptRow(int index, string glyph, string title, string subtitle)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = Core.Loc.T(title), FontSize = 18, Foreground = UiHelpers.Text });
            text.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(subtitle),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(text, 1);

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = UiHelpers.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid();
            // 48, not 34: the glyph is drawn at 20pt and centred, so a 34-wide column left barely
            // three pixels between it and the text. The gap belongs to the COLUMN rather than to a
            // margin on the text, so the two lines of the label still start at the same x whatever
            // width the glyph happens to have.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(icon);
            grid.Children.Add(text);

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
                MinWidth = 520,
            };
            int captured = index;
            row.MouseLeftButtonUp += (_, __) => { _exitPromptIndex = captured; ActivateExitPromptRow(); };
            _exitPromptRows.Add(row);
            return row;
        }

        private void ApplyExitPromptSelection()
        {
            foreach (var row in _exitPromptRows)
                row.BorderBrush = row.Tag is int i && i == _exitPromptIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveExitPromptSelection(PadButton dir)
        {
            if (_exitPromptRows.Count == 0) return;
            int next = _exitPromptIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _exitPromptRows.Count || next == _exitPromptIndex) return;
            _exitPromptIndex = next;
            ApplyExitPromptSelection();
        }

        private void ActivateExitPromptRow()
        {
            switch (_exitPromptIndex)
            {
                case 0:
                    // Close(), not Hide(): the Closing handler already knows what closing means on
                    // this machine. With Run in background off it exits instead, which is the honest
                    // answer - there is no tray to minimize into.
                    _exitPromptOpen = false;
                    Close();
                    return;
                case 1:
                    _exitPromptOpen = false;
                    _exitPromptRows.Clear();
                    GoHome();
                    return;
                default:
                    Application.Current.Shutdown();
                    return;
            }
        }
        #endregion

        /// <summary>What A does on the running screen, named after the behaviour the user picked.
        /// The game is untouched in all three.</summary>
        private static string HideLabel()
        {
            switch (Core.CenterSettings.LaunchBehavior)
            {
                case Core.LaunchBehavior.Minimize: return "Minimize Center";
                case Core.LaunchBehavior.StayOpen: return "Back to library";
                default: return "Close Center";
            }
        }

        private void RefreshLibraryActionBar()
        {
            // Before the chips are built, not after: this decides whether the row they go into is on
            // screen at all, and doing it afterwards would show one frame of the old state.
            ApplyFooterVisibility();

            if (_infoOpen)
            {
                AddAction(PadButton.A, "Open SteamGridDB", true,
                    () => Core.PrerequisiteGuide.OpenPage(SteamGridDbUrl, m => Core.InstallLog.Write(m)));
                AddAction(PadButton.Y, "Open AnyFSE", true,
                    () => Core.PrerequisiteGuide.OpenPage(AnyFseUrl, m => Core.InstallLog.Write(m)));
                AddAction(PadButton.X, "Copy Center path", true, CopyAnyFsePath);
                AddAction(PadButton.B, "Close", true, CloseLibraryInfo);
                return;
            }

            if (_exitPromptOpen)
            {
                AddAction(PadButton.A, "Select", true, ActivateExitPromptRow);
                AddAction(PadButton.B, "Back to library", true, CloseExitPrompt);
                return;
            }

            if (LaunchOverlayOpen)
            {
                switch (_launchPrompt)
                {
                    case LaunchPrompt.Confirm:
                        AddAction(PadButton.A, "Play", true, ConfirmLaunch);
                        AddAction(PadButton.B, "Cancel", true, ClearLaunchOverlay);
                        break;
                    case LaunchPrompt.Running:
                        // The label says what happens to CENTER, because that is all this does. The
                        // three modes read differently enough that one word would be a lie in two of
                        // them: "Hide" over an app that exits is not hiding.
                        AddAction(PadButton.A, HideLabel(), true, HideAfterLaunch);
                        break;
                    default:
                        AddAction(PadButton.B, "Back", true, ClearLaunchOverlay);
                        break;
                }
                return;
            }

            if (_settingsOpen)
            {
                string label = _settingsIndex == SettingsKeyRow ? "Edit"
                    : _settingsIndex == SettingsLaunchBehaviorRow ? "Cycle"
                    : "Toggle";
                AddAction(PadButton.A, label, true, ActivateSetting);
                AddAction(PadButton.B, "Back", true, SaveArtKeyAndClose);
                return;
            }

            if (RefreshMiscActionBar()) return;
            if (RefreshGameMenuActionBar()) return;

            AddAction(PadButton.A, "Play", SelectedGame != null, LaunchSelectedGame);
            // The per-game menu (favorite, cover art) - only makes sense with something selected, and
            // Start is free everywhere in the library: nothing else has claimed it since the
            // key-entry screen it used to open moved behind View (Select) instead.
            AddAction(PadButton.Menu, "Menu", SelectedGame != null, OpenGameMenu);

            // The Misc tab is the one place with entries the user OWNS, so it is the one place with
            // add and edit. X is free everywhere; Y takes over from Rescan here because rescanning
            // the stores does nothing for a list that is not scanned, and Rescan stays on Y in every
            // other tab. Braced deliberately - AddAction just overwrites a dictionary slot, so an
            // unguarded Rescan call below this block would silently win over "Edit" on every redraw.
            // X is the info screen in EVERY tab, so the key cap next to the icon in the tab strip is
            // true wherever the user is standing. Misc's "Add app" moved to Y, which is free there -
            // Y is the tab-specific slot (Rescan elsewhere), X is the one that means the same thing
            // everywhere.
            AddAction(PadButton.X, "Info", true, OpenLibraryInfo);

            if (_libraryGroup == LibraryGroup.Misc)
            {
                AddAction(PadButton.Y, "Add app", true, OpenMiscAdd);
            }
            else
            {
                // The square-art switch used to sit here as an X chip. It moved into the settings
                // screen: it is remembered across launches, and a footer is for what you do now, not
                // for what you configure once.
                AddAction(PadButton.Y, "Rescan", !_libraryScanning, () =>
                {
                    _libraryScanned = false;
                    _ = ScanLibraryAsync();
                });
            }

            AddAction(PadButton.View, "Settings", true, OpenLibrarySettings);
            AddAction(PadButton.B, "Back", true, OpenExitPrompt);

            // The triggers move between ROM SYSTEMS - the shoulders own the tabs now. Bound WITHOUT
            // a footer chip: both are labelled in the strip they belong to, which is where someone
            // looking for them looks, and a chip as well would push the three real actions along.
            if (_libraryScanned && _libraryGroup == LibraryGroup.Roms)
            {
                _liveActions[PadButton.LT] = () => CycleRomSystem(-1);
                _liveActions[PadButton.RT] = () => CycleRomSystem(1);
            }
        }
        #endregion
    }

    /// <summary>Anything that draws tiles and has to repaint the cursor when it moves.</summary>
    internal interface ILibrarySelectionHost
    {
        void ApplySelection(int selectedIndex);
    }

    /// <summary>One grid row. The list virtualises over these, so a row is the unit that gets built
    /// and thrown away as the user scrolls.</summary>
    internal sealed class LibraryRow
    {
        public CenterMenuWindow Owner;
        public int FirstIndex;
        public List<GameEntry> Items;

        /// <summary>Set on a HEADING row, which carries no tiles. One list holds both kinds so the
        /// virtualising panel keeps working - a second ItemsControl per group would defeat the
        /// recycling the grid depends on for a library of a few hundred covers.</summary>
        public string Heading;
    }

    /// <summary>One reel entry - a single game, since the reel is one long line.</summary>
    internal sealed class LibraryReelItem
    {
        public CenterMenuWindow Owner;
        public int Index;
        public GameEntry Game;
    }

    /// <summary>Shared tile drawing for both presentations.</summary>
    internal static class LibraryTile
    {
        /// <summary>
        /// One cover. The no-cover state is not an error state: a coloured plate with the title on it
        /// is a deliberate-looking tile, and it can never fail the way a missing image can.
        /// </summary>
        /// <summary>Corner radius of the tile's outer Border.</summary>
        private const double TileRadius = 8;
        private const double TileBorder = 3;

        /// <summary>The sweep overlay of a tile, so ApplySelected can find it again without walking
        /// the visual tree by index - the child order of the content grid is an implementation detail
        /// and should not become load-bearing.</summary>
        private static readonly DependencyProperty SweepProperty = DependencyProperty.RegisterAttached(
            "Sweep", typeof(Rectangle), typeof(CenterMenuWindow));

        public static Border Build(CenterMenuWindow owner, GameEntry game, int index, Action<int> onClick,
            bool glass = false)
        {
            var fallback = new Border
            {
                Background = GameArt.ColorForTitle(game.Title),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = game.Title,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(10),
                },
            };

            var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var content = new Grid();
            content.Children.Add(fallback);
            content.Children.Add(image);

            var badge = BuildProfileBadge(game.Profiles);
            if (badge != null) content.Children.Add(badge);

            if (glass)
            {
                content.Children.Add(BuildGlass());
                var sweep = BuildSweep();
                content.Children.Add(sweep);
                content.SetValue(SweepProperty, sweep);
            }

            var tile = new Border
            {
                Width = owner.LibTileWidth,
                Height = owner.LibTileHeight,
                CornerRadius = new CornerRadius(TileRadius),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(TileBorder),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                Child = content,
            };
            tile.MouseLeftButtonUp += (_, __) => onClick(index);

            // ⚠ NOT ClipToBounds. That clips to the LAYOUT RECTANGLE, never to CornerRadius - so a
            // cover (Stretch=UniformToFill, filling the whole content area) painted square corners
            // exactly where the rounded accent border should be, and the focus frame looked like its
            // corners had been cut off. A tile with NO cover looked right, because its coloured
            // fallback plate is itself a Border with the same CornerRadius - which is what made this
            // read as "only some tiles are wrong".
            //
            // A real rounded clip on the content is the fix. Driven off SizeChanged rather than
            // computed from LibTileWidth/Height so it cannot drift from whatever WPF actually
            // arranged, and the inner radius is the outer one less the border thickness so the two
            // curves sit concentrically instead of the clip cutting a second, tighter arc.
            content.SizeChanged += (_, e) => content.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                TileRadius - TileBorder, TileRadius - TileBorder);

            if (game.ArtPath != null) LoadCover(owner, game.ArtPath, image);
            return tile;
        }

        /// <summary>
        /// The mark that says "ClawTweaks has a profile for this game" - a small dark chip in the
        /// bottom-left corner of the cover, one glyph per kind.
        ///
        /// DELIBERATELY QUIET. The cover is the content of this screen; a badge loud enough to read
        /// across the room is one that has started competing with the artwork it sits on. It is there
        /// to be noticed when looked for, not to announce itself - hence a dim plate, a small glyph,
        /// and the corner furthest from where the eye lands.
        ///
        /// BOTTOM-LEFT, not top-right: the selection sweep travels across the upper area of a
        /// selected tile, and a badge there flickers under it on every cursor move.
        ///
        /// Returns null when there is nothing to say. An "empty" badge - a plate with no glyph - would
        /// still be a mark on the cover, and every game would carry one.
        /// </summary>
        private static UIElement BuildProfileBadge(ClawProfileKinds kinds)
        {
            if (kinds == ClawProfileKinds.None) return null;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            // Performance first, because it is the one that changes how the game runs. Lightning for
            // power, gamepad for the controller - the same two pictures the widget uses for the same
            // two things, so nothing new has to be learned here.
            if ((kinds & ClawProfileKinds.Performance) != 0) row.Children.Add(BadgeGlyph(""));
            if ((kinds & ClawProfileKinds.Controller) != 0) row.Children.Add(BadgeGlyph(""));

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 2, 5, 3),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                // The tile itself handles the click. A badge that swallowed it would make the corner
                // of every marked cover dead.
                IsHitTestVisible = false,
                Child = row,
            };
        }

        private static TextBlock BadgeGlyph(string glyph) => new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>
        /// The glass sheen over a reel cover: one soft highlight down the top third, nothing else.
        ///
        /// Kept deliberately weak (12% white at the very top, gone by 45% down). The cover art IS the
        /// content here - a glass effect strong enough to notice on its own is one that has started
        /// washing out the picture it sits on.
        /// </summary>
        private static Rectangle BuildGlass()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.22));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
            brush.Freeze();

            return new Rectangle { Fill = brush, IsHitTestVisible = false };
        }

        /// <summary>
        /// The focus sweep: a skewed band of light that travels across the selected cover.
        ///
        /// Same construction as the one in the ClawTweaks widget and the user's Handheld Companion
        /// fork (TemplatesDictionary.xaml, "Focus shimmer"): a transparent-to-white-to-transparent
        /// horizontal gradient, skewed -18 degrees, translated once across the tile. Starts hidden
        /// and motionless - ApplySelected is what animates it,
        /// so only ever ONE tile in the view is running an animation.
        /// </summary>
        private static Rectangle BuildSweep()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
            brush.Freeze();

            return new Rectangle
            {
                Width = SweepWidth,
                Fill = brush,
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        new SkewTransform(-18, 0),
                        new TranslateTransform(-SweepWidth, 0),
                    },
                },
            };
        }

        private const double SweepWidth = 64;

        /// <summary>How long one pass takes. Fast enough to be over before the cursor moves on -
        /// this is a highlight, not something to watch.</summary>
        private const double SweepSeconds = 0.55;

        /// <summary>
        /// Paints the selection frame and starts or stops the sweep.
        ///
        /// Both hosts route through here so "selected" means one thing. A tile built without glass
        /// simply has no sweep attached and gets the frame alone.
        /// </summary>
        public static void ApplySelected(Border tile, bool selected)
        {
            if (tile == null) return;
            tile.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;

            var sweep = tile.Child?.GetValue(SweepProperty) as Rectangle;
            if (sweep == null) return;

            var translate = (sweep.RenderTransform as TransformGroup)?.Children[1] as TranslateTransform;
            if (translate == null) return;

            if (!selected)
            {
                // Handing null to BeginAnimation removes the animation's hold on the property - just
                // setting the value would be overridden again on the animation's next frame, and the
                // clock would keep running on a tile nobody is looking at.
                sweep.BeginAnimation(UIElement.OpacityProperty, null);
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                sweep.Opacity = 0;
                translate.X = -SweepWidth;
                return;
            }

            sweep.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));

            // ONCE per selection, not on a loop. A stripe that keeps coming back reads as something
            // still loading; a single pass reads as the tile lighting up when the cursor lands on it.
            // Landing on a tile again re-runs it, because ApplySelected is called on every move.
            double travel = tile.Width + SweepWidth;
            var slide = new DoubleAnimation(-SweepWidth, travel, TimeSpan.FromSeconds(SweepSeconds))
            {
                // Holds at the far edge instead of snapping back to the left, which would show the
                // stripe crossing the tile a second time in one frame.
                FillBehavior = FillBehavior.HoldEnd,
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        private static void LoadCover(CenterMenuWindow owner, string path, Image image)
        {
            // Stamp the tile with the cover it is waiting for BEFORE starting the decode: recycling
            // can hand the host a different row while the decode runs, and without the stamp a fast
            // scroll paints covers onto the wrong games.
            image.Tag = path;
            GameArt.LoadAsync(path, owner.LibDecodeWidth).ContinueWith(t =>
            {
                var bitmap = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
                if (bitmap == null) return;
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(image.Tag, path)) return;
                    image.Source = bitmap;
                    image.Visibility = Visibility.Visible;
                }));
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>
        /// The mirrored cover under a reel tile.
        ///
        /// A VisualBrush of the live tile, flipped and faded out downwards - so it follows the cover
        /// as it loads, with no second decode and no second bitmap in memory. Cached, because a
        /// VisualBrush re-renders its source by default and a reel has several on screen at once.
        /// </summary>
        public static UIElement BuildReflection(Border tile, double width, double height)
        {
            var brush = new VisualBrush(tile)
            {
                Stretch = Stretch.None,
                AlignmentY = AlignmentY.Bottom,
                AlignmentX = AlignmentX.Center,
            };
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);

            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            // Defined BEFORE the flip, so the opaque end at offset 1 is the edge that ends up touching
            // the cover. 0.35 rather than 1: glass returns a fraction of the light, and a mirror at
            // full strength reads as a second, upside-down tile rather than as a reflection.
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 0, 0, 0), 1));
            mask.Freeze();

            return new Rectangle
            {
                Width = width,
                Height = height,
                Fill = brush,
                OpacityMask = mask,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, -1),
                IsHitTestVisible = false,
                Margin = new Thickness(0, 2, 0, 0),
            };
        }
    }

    /// <summary>
    /// Builds one grid row of cover tiles. A Border with a dependency property rather than a XAML
    /// data template, because every other screen in Center is code-built too and a lone template file
    /// would be the odd one out.
    ///
    /// Recycling means the same host is handed a different row as the user scrolls, so everything
    /// happens in <see cref="OnRowChanged"/> - there is no "build once" moment.
    /// </summary>
    internal sealed class LibraryRowHost : Border, ILibrarySelectionHost
    {
        public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
            nameof(Row), typeof(LibraryRow), typeof(LibraryRowHost),
            new PropertyMetadata(null, OnRowChanged));

        public LibraryRow Row
        {
            get => (LibraryRow)GetValue(RowProperty);
            set => SetValue(RowProperty, value);
        }

        private readonly List<Border> _tiles = new List<Border>();
        private CenterMenuWindow _owner;

        public LibraryRowHost()
        {
            Loaded += (_, __) => { _owner?.RegisterRow(this); ApplySelection(_owner?.LibSelectedIndex ?? -1); };
            Unloaded += (_, __) => _owner?.UnregisterRow(this);
        }

        private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LibraryRowHost)d).Build(e.NewValue as LibraryRow);

        /// <summary>
        /// A group heading row. It registers with nothing and holds no tiles - the selection never
        /// lands on it, which is why it needs no entry in the row map either.
        /// </summary>
        private void BuildHeading(LibraryRow row)
        {
            _owner?.UnregisterRow(this);
            _owner = row.Owner;

            var text = new TextBlock
            {
                Text = row.Heading,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var rule = new Border
            {
                Height = 1,
                Background = Ui.UiHelpers.Subtle,
                Opacity = 0.25,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(rule, 1);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(text);
            grid.Children.Add(rule);

            Child = new Border
            {
                Child = grid,
                Margin = new Thickness(_owner.LibOuterMarginValue, 6, _owner.LibOuterMarginValue, 8),
            };
        }

        private void Build(LibraryRow row)
        {
            _tiles.Clear();
            Child = null;
            if (row == null) return;

            if (row.Heading != null) { BuildHeading(row); return; }
            if (row.Items == null) return;

            _owner?.UnregisterRow(this);
            _owner = row.Owner;
            _owner?.RegisterRow(this);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(_owner.LibOuterMarginValue, 0, _owner.LibOuterMarginValue, _owner.LibTileGapValue),
            };

            for (int i = 0; i < row.Items.Count; i++)
            {
                var tile = LibraryTile.Build(_owner, row.Items[i], row.FirstIndex + i, idx => _owner.OnTileClicked(idx));
                tile.Margin = new Thickness(0, 0, i == row.Items.Count - 1 ? 0 : _owner.LibTileGapValue, 0);
                _tiles.Add(tile);
                panel.Children.Add(tile);
            }

            Child = panel;
            ApplySelection(_owner?.LibSelectedIndex ?? -1);
        }

        public void ApplySelection(int selectedIndex)
        {
            foreach (var tile in _tiles)
                LibraryTile.ApplySelected(tile, tile.Tag is int i && i == selectedIndex);
        }
    }

    /// <summary>One reel entry: the cover plus its mirror.</summary>
    internal sealed class LibraryReelHost : Border, ILibrarySelectionHost
    {
        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
            nameof(Item), typeof(LibraryReelItem), typeof(LibraryReelHost),
            new PropertyMetadata(null, OnItemChanged));

        public LibraryReelItem Item
        {
            get => (LibraryReelItem)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        private Border _tile;
        private CenterMenuWindow _owner;

        public LibraryReelHost()
        {
            Loaded += (_, __) => { _owner?.RegisterRow(this); ApplySelection(_owner?.LibSelectedIndex ?? -1); };
            Unloaded += (_, __) => _owner?.UnregisterRow(this);
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LibraryReelHost)d).Build(e.NewValue as LibraryReelItem);

        private void Build(LibraryReelItem item)
        {
            _tile = null;
            Child = null;
            if (item?.Game == null) return;

            _owner?.UnregisterRow(this);
            _owner = item.Owner;
            _owner?.RegisterRow(this);

            double w = _owner.LibTileWidth;
            double h = _owner.LibTileHeight;

            _tile = LibraryTile.Build(_owner, item.Game, item.Index, idx => _owner.OnTileClicked(idx), glass: true);

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(_tile);
            stack.Children.Add(LibraryTile.BuildReflection(_tile, w, h * _owner.LibReflectionFractionValue));

            Child = stack;
            // Centred within the stretched row: the cover and its mirror are one object standing on
            // glass, so the pair is centred rather than the cover alone.
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(0, 0, _owner.LibTileGapValue, 0);
            ApplySelection(_owner?.LibSelectedIndex ?? -1);
        }

        public void ApplySelection(int selectedIndex)
        {
            if (_tile == null) return;
            LibraryTile.ApplySelected(_tile, _tile.Tag is int i && i == selectedIndex);
        }
    }
}
