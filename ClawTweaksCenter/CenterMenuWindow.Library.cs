using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
        private DispatcherTimer _closeTimer;

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

                _tabScroller = BuildEdgeFadedStrip(chips);
                FillDock(TabStripPanel, BuildKeyCap("LB"), BuildKeyCap("RB"), _tabScroller);
                BringChipIntoView(_tabScroller, _activeGroupChip);
            }
            else
            {
                var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                tabs.Children.Add(BuildTab("Start", true, null));
                tabs.Children.Add(BuildTab("Library", false, OpenLibrary));
                // RT, not RB. The shoulders belong to the library's own tabs once you are inside it,
                // so the way IN is a trigger and stays out of their way.
                tabs.Children.Add(BuildKeyCap("RT"));
                FillDock(TabStripPanel, null, null, tabs);
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

        private UIElement BuildTab(string label, bool active, Action onClick)
        {
            var border = new Border
            {
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 17,
                    FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = active ? UiHelpers.Text : UiHelpers.Subtle,
                },
                Padding = new Thickness(4, 8, 4, 8),
                Margin = new Thickness(0, 0, 22, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                // The active tab is marked by an underline rather than a filled pill: the header
                // above already carries the brand block, and two filled shapes in a row read as two
                // competing headers.
                BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 3),
            };
            if (onClick != null) border.MouseLeftButtonUp += (_, __) => onClick();
            return border;
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
                Child = BuildChipContent(GameLibrary.GroupLabel(g), _libraryScanned ? count : (int?)null, active, 14),
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
        }

        /// <summary>Puts the build-list host back in front and restores the header. Called from
        /// GoHome - the library is a tab, not a window, so leaving it is a visibility change.</summary>
        private void LeaveLibrary()
        {
            CancelPendingClose();
            if (LibraryRoot != null) LibraryRoot.Visibility = Visibility.Collapsed;
            if (ContentScroller != null) ContentScroller.Visibility = Visibility.Visible;
            if (ShellHeader != null) ShellHeader.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task ScanLibraryAsync()
        {
            _libraryScanning = true;
            RenderLibrary();
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
                    if (_view == View.Library) { RenderLibrary(); RefreshTabStrip(); }
                }));
                _libraryScanned = true;
                _libraryScanning = false;
                if (_view == View.Library) { RenderLibrary(); RefreshTabStrip(); }

                StartArtFetch();

                // The log harvest only refines the ordering of a library that is already usable, so
                // it runs after the view is up rather than in front of it. It is also what fills the
                // Recent reel for anything Steam does not track, hence the re-render.
                _library.HarvestHistoryInBackground(ct, () => Dispatcher.Invoke(() =>
                {
                    if (_view == View.Library && _libraryGroup == LibraryGroup.Recent) RenderLibrary();
                }));
            }
            catch (OperationCanceledException) { _libraryScanning = false; }
            catch (Exception ex)
            {
                _libraryScanning = false;
                Core.InstallLog.Write("Library scan failed: " + ex);
                if (_view == View.Library) RenderLibrary();
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
            _libraryGames = _libraryScanned ? _library.ForGroup(_libraryGroup, _romSystem) : Array.Empty<GameEntry>();

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

                var chip = new Border
                {
                    Child = BuildChipContent(SystemLabel(system), count, active, 13),
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    CornerRadius = new CornerRadius(11),
                    Background = active ? UiHelpers.Card : Brushes.Transparent,
                    BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                    BorderThickness = new Thickness(active ? 1 : 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
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

        private static string SystemLabel(string system)
        {
            if (system == GameLibrary.RomRecentSystem) return "Recent";
            return system ?? "All systems";
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
            _libSubline.Text = g.LastPlayed.HasValue
                ? g.StoreName + "  ·  Last played " + g.LastPlayed.Value.ToString("d MMM yyyy")
                : g.StoreName;
        }

        private GameEntry SelectedGame =>
            _libSelectedIndex >= 0 && _libSelectedIndex < _libraryGames.Count ? _libraryGames[_libSelectedIndex] : null;

        private UIElement BuildLibraryMessage(string text, bool working)
        {
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
        private UIElement BuildGrid()
        {
            MeasureGridMetrics();

            var rows = new List<LibraryRow>();
            for (int i = 0; i < _libraryGames.Count; i += _libColumns)
            {
                var slice = new List<GameEntry>();
                for (int c = 0; c < _libColumns && i + c < _libraryGames.Count; c++) slice.Add(_libraryGames[i + c]);
                rows.Add(new LibraryRow { Owner = this, FirstIndex = i, Items = slice });
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
        private void MeasureGridMetrics()
        {
            double avail = LibraryRoot.ActualWidth > 0 ? LibraryRoot.ActualWidth : ActualWidth;
            if (avail <= 0) avail = 1120;
            double usable = Math.Max(200, avail - 2 * LibOuterMargin);

            _libColumns = (int)Math.Round((usable + LibTileGap) / (LibGridTileWidth + LibTileGap));
            if (_libColumns < 2) _libColumns = 2;
            if (_libColumns > 12) _libColumns = 12;

            _libTileWidth = (usable - (_libColumns - 1) * LibTileGap) / _libColumns;
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
            if (_libraryGames.Count == 0) return;
            if (_closeTimer != null) return;  // a launch countdown owns the screen

            int next = _libSelectedIndex;
            switch (dir)
            {
                case PadButton.Left: next -= 1; break;
                case PadButton.Right: next += 1; break;
                // In the reel there is nothing above or below - the whole grouping is one line.
                case PadButton.Up: if (_libReelMode) return; next -= _libColumns; break;
                case PadButton.Down: if (_libReelMode) return; next += _libColumns; break;
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
            if (_closeTimer != null || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
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
            if (_view != View.Library || _closeTimer != null) return;
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
        private const int SettingsLaunchBehaviorRow = 2;
        private const int SettingsStartWithClawTweaksRow = 3;
        private const int SettingsRunInBackgroundRow = 4;
        private const int SettingsKeyRow = 5;

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
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = "Library settings",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            stack.Children.Add(BuildSettingRow(SettingsStartInLibraryRow, "Start in the library",
                Core.CenterSettings.OpenLibraryAtStartup ? "On" : "Off"));
            stack.Children.Add(BuildSettingRow(SettingsSquareRomArtRow, "Square ROM art",
                _squareRomArt ? "On" : "Off"));
            stack.Children.Add(BuildSettingRow(SettingsLaunchBehaviorRow, "After starting a game",
                LaunchBehaviorLabel(Core.CenterSettings.LaunchBehavior)));
            stack.Children.Add(BuildSettingRow(SettingsStartWithClawTweaksRow, "Start Center with ClawTweaks",
                Core.CenterSettings.StartCenterWithClawTweaks ? "On" : "Off"));
            stack.Children.Add(BuildSettingRow(SettingsRunInBackgroundRow, "Run in background",
                Core.CenterSettings.RunInBackground ? "On" : "Off"));

            var keyRow = BuildSettingRow(SettingsKeyRow, "SteamGridDB key", null);
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
                Text = Library.SteamGridDb.HasKey ? "Set. Covers are downloaded for games with none." : "Not set.",
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

        /// <summary>One settings row: title on the left, current state on the right. The state is the
        /// VALUE, not a checkbox glyph - on a handheld the row is read at a glance from across the
        /// room, and "On" survives that better than a tick.</summary>
        private Border BuildSettingRow(int index, string title, string state)
        {
            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                Foreground = UiHelpers.Text,
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);

            if (state != null)
            {
                var value = new TextBlock
                {
                    Text = state,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = state == "On" ? UiHelpers.Ok : UiHelpers.Subtle,  // On is green, everything else (Off, or a mode name) reads as neutral
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                };
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }

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
                MinWidth = 560,
            };
            row.MouseLeftButtonUp += (_, __) => { _settingsIndex = index; ActivateSetting(); };
            _settingsRows.Add(row);
            return row;
        }

        private void ApplySettingsSelection()
        {
            foreach (var row in _settingsRows)
                row.BorderBrush = row.Tag is int i && i == _settingsIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveSettingsSelection(PadButton dir)
        {
            int next = _settingsIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _settingsRows.Count || next == _settingsIndex) return;
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
                case Core.LaunchBehavior.Minimize: return "Minimize";
                case Core.LaunchBehavior.StayOpen: return "Stay open";
                default: return "Close Center";
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
                        if (_view == View.Library && !_settingsOpen && _closeTimer == null) RenderLibrary();
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
        private void LaunchSelectedGame()
        {
            var game = SelectedGame;
            if (game == null || _closeTimer != null) return;

            bool started = GameLibrary.Launch(game, out var startedProcess);
            if (started)
            {
                _library.History.Note(game.InstallDir, DateTime.Now);
                _library.History.SaveIfChanged();
            }

            ShowLaunchOverlay(game, started);
            if (!started) return;

            // Read ONCE, here. The countdown runs for two and a half seconds and the settings
            // screen is unreachable during it, but binding the decision to the moment of the launch
            // is what makes the footer label above and the action below agree by construction.
            var behavior = Core.CenterSettings.LaunchBehavior;

            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _closeTimer.Tick += (_, __) =>
            {
                CancelPendingClose();
                switch (behavior)
                {
                    case Core.LaunchBehavior.Minimize:
                        WindowState = WindowState.Minimized;
                        // Only Minimize needs to come back on its own - StayOpen never left, and
                        // Close has already exited by this point in a way there is nothing left to
                        // restore. Mirrors Playnite's own pairing (AfterLaunch=Minimize with
                        // AfterGameClose=Restore) rather than tracking every launch unconditionally.
                        StartTrackingForRestore(game, startedProcess);
                        break;
                    case Core.LaunchBehavior.StayOpen:
                        break;
                    default:
                        Application.Current.Shutdown();
                        return;
                }
                // Back to the grid for both surviving modes: leaving "Starting X…" on screen would
                // greet the user with a stale line whenever they came back.
                RenderLibrary();
                RefreshActionBar();
            };
            _closeTimer.Start();
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

        private void ShowLaunchOverlay(GameEntry game, bool started)
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = started ? "Starting " + game.Title + "…" : "Could not start " + game.Title + ".",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            });
            LibraryRoot.Children.Add(stack);
        }

        /// <summary>
        /// B during the countdown.
        ///
        /// It cancels the CLOSING, and nothing else. ShellExecute has long since fired by then, the
        /// game is on its way, and there is no process of ours to stop - the handler returns Steam or
        /// the shell, never the game. Which is why the footer says "Keep Center open" and not
        /// "Cancel": a label promising to stop the launch, followed by the game appearing anyway,
        /// reads as a bug.
        /// </summary>
        private void CancelPendingClose()
        {
            if (_closeTimer == null) return;
            _closeTimer.Stop();
            _closeTimer = null;
        }

        private void KeepCenterOpen()
        {
            CancelPendingClose();
            RenderLibrary();
            RefreshActionBar();
        }
        #endregion

        #region Footer
        private void RefreshLibraryActionBar()
        {
            if (_closeTimer != null)
            {
                // "Keep Center open" is only true when the countdown would otherwise exit. Promising
                // to keep something open that was never going to close reads as a broken label.
                AddAction(PadButton.B,
                    Core.CenterSettings.LaunchBehavior == Core.LaunchBehavior.Close ? "Keep Center open" : "Back",
                    true, KeepCenterOpen);
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
            if (_libraryGroup == LibraryGroup.Misc)
            {
                AddAction(PadButton.X, "Add app", true, OpenMiscAdd);
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
            AddAction(PadButton.B, "Back", true, GoHome);

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

        private void Build(LibraryRow row)
        {
            _tiles.Clear();
            Child = null;
            if (row?.Items == null) return;

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
