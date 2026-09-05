using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Whether the Other Stores shelf has anything on it. Read by the tab strip AND by
        /// the trigger cycle, which is why it is one property rather than the same LINQ twice.</summary>
        private bool HasOtherStoreGames =>
            _libraryScanned && _library.ForGroup(LibraryGroup.OtherStores).Count > 0;

        private bool HasNotInstalledGames =>
            _libraryScanned && _library.ForGroup(LibraryGroup.NotInstalled).Count > 0;
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

        /// <summary>Whether a scan has EVER completed this session. The first one paints once at
        /// the end; every later one keeps painting as each store lands, because by then there is
        /// already a full grid on screen and filling it in beats blanking it.</summary>
        private bool _libraryEverScanned;

        /// <summary>How long the first scan may hold the spinner before it gives up and paints
        /// per store after all. A source that hangs must not turn into a frozen screen - the same
        /// rule every other wait in this app follows.</summary>
        private const int FirstScanPatienceMs = 6000;
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
            /// <summary>"Install X?" - the Not Installed tab's version of Confirm. Nothing has been
            /// asked of Steam yet.</summary>
            ConfirmInstall,
            /// <summary>Handed to Steam. Center has no further part in it - see ConfirmInstallNow.
            /// </summary>
            InstallHandedOver,
        }

        private LaunchPrompt _launchPrompt;
        private GameEntry _launchTarget;

        /// <summary>
        /// How long after a launch the running screen says "starting" rather than "running".
        ///
        /// A CLOCK, NOT A SIGNAL, and deliberately so. There is no reliable "the game is up" here -
        /// every store launch hands back the launcher rather than the game (GameLibrary.Launch says
        /// why), and the one component that answers this properly is the helper's game detection,
        /// which Center does not hear from. A minute is long enough to cover a cold Steam plus a
        /// shader build, and being wrong costs a line of text that is a minute stale.
        /// </summary>
        private static readonly TimeSpan LaunchStartingWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether the running screen is still in its first minute. A LATCH, not a second reading of
        /// the clock, and that is the whole fix for "the countdown often does not switch".
        ///
        /// It used to be computed as `UtcNow - _launchStartedAt &lt; LaunchStartingWindow`, while the
        /// timer that triggers the redraw runs for exactly LaunchStartingWindow. Two clocks, one
        /// boundary: a DispatcherTimer fires at OR AFTER its interval as measured by its own timer,
        /// and the ~15.6 ms system tick means the elapsed time on the wall clock can still read a
        /// hair UNDER a minute when it does. Then the redraw painted "is starting" a second time -
        /// and the timer is one-shot, so nothing ever came back to correct it. The screen sat on
        /// "starting" for as long as the game ran, intermittently, with no way to tell the two
        /// outcomes apart from the outside.
        ///
        /// With a latch there is only one authority: the timer flips this, the screen reads it.
        /// </summary>
        private bool _launchStarting;

        /// <summary>Whether the launch just gone had to start Steam - see
        /// GameLibrary.LastLaunchStartedSteam. Copied at launch time rather than read at render
        /// time, because a second launch would otherwise rewrite this screen's own history.</summary>
        private bool _launchSteamColdStart;

        /// <summary>Redraws the running screen once, when the starting window is up. Without it the
        /// line only changes the next time something else happens to redraw the screen, which on a
        /// game that is left running is never.</summary>
        private DispatcherTimer _launchStartingTimer;

        private bool LaunchStartingPhase => _launchStarting;

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

        // What each row DOES, filled in the same call that builds the row (AddExitPromptRow). Kept
        // beside the rows rather than as a switch over indices: four of these rows end the session,
        // and a switch drifts silently the first time somebody inserts a row above them.
        private readonly List<Action> _exitPromptActions = new List<Action>();

        /// <summary>Why the last press did nothing, shown under the heading. Only ever set when an
        /// action could not be delivered - the prompt stays up, because dismissing it looks exactly
        /// like a press that worked.</summary>
        private string _exitPromptNote;

        // ── Three columns, 2026-09-03 ──────────────────────────────────────────────────────────────
        // Left = tray apps (Systems/TrayApps.cs on the helper), middle = the rows above (unchanged,
        // which is why they keep their original field names), right = curated Windows tools. Full
        // build in CenterMenuWindow.QuickMenu.cs.
        //
        // Left/Right MOVE BETWEEN COLUMNS, Up/Down move WITHIN one - so each column needs its own
        // index and its own row list, rather than the one pair the middle column already had. Each
        // column also remembers its own index across a Left/Right switch, so leaving and returning to
        // a column does not reset where the user was in it.
        // The NUMBER is the position on screen, left to right, and that is all Left/Right knows -
        // so swapping the two side columns is these two constants plus the Grid.SetColumn calls in
        // RenderExitPrompt, and nothing else. Tools moved left and the tray right on 2026-09-05.
        private const int ExitPromptColumnTools = 0;
        private const int ExitPromptColumnCenter = 1;
        private const int ExitPromptColumnTray = 2;

        private const int ExitPromptColumnFirst = 0;
        private const int ExitPromptColumnLast = 2;
        private int _exitPromptColumn = ExitPromptColumnCenter;

        private int _exitPromptTrayIndex;
        private readonly List<Border> _exitPromptTrayRows = new List<Border>();
        private readonly List<Action> _exitPromptTrayActions = new List<Action>();

        // Index-aligned with the two lists above: what X does for the same row A would Open. Every
        // navigable tray row currently has both (CanOpen and CanClose come from the helper coupled -
        // see TrayApps.ResolveWindow), so this is never sparser than _exitPromptTrayActions, but it
        // stays a separate list rather than a tuple so the day they DO diverge is a one-line change
        // here instead of a new field threaded through every caller.
        private readonly List<Action> _exitPromptTrayCloseActions = new List<Action>();

        private int _exitPromptToolsIndex;
        private readonly List<Border> _exitPromptToolsRows = new List<Border>();
        private readonly List<Action> _exitPromptToolsActions = new List<Action>();

        /// <summary>True while one of the launch screens is up. Everything that navigates the grid
        /// checks this - the launch screens own the whole library area while they are on it.</summary>
        private bool LaunchOverlayOpen => _launchPrompt != LaunchPrompt.None || _exitPromptOpen || _infoOpen;

        // The background watcher for "restore Center once this game ends" (see GameRunTracker /
        // StartTrackingForRestore). Held so a second launch can cancel a stale watch instead of
        // leaving two of them racing to restore the same window.
        private CancellationTokenSource _gameTrackCts;

        // THE LIBRARY DOES NOT NEED CLAWTWEAKS, and the gate that used to say otherwise is gone.
        //
        // It was `_installedVersionChecked && _installedVersion != null`, which hid the tab, both
        // Home tiles and the whole tab strip until a PowerShell version check had answered. The
        // premise was that the library is a ClawTweaks feature. It is not: it scans Steam, Epic,
        // Xbox, the four other launchers, Playnite and your own apps, and launches them - none of
        // which involves ClawTweaks at all. The only parts that do are the profile badge and the
        // play history, and both read files that simply are not there, which they already handle.
        //
        // Removed rather than pinned to `true`: a property that is always true is an invitation to
        // put the condition back.

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

            bool inLibrary = _view == View.Library;

            // The brand-and-device header is hidden inside the library. It belongs to a setup screen,
            // and covers want the height more than the device name does - the same device is still
            // named on every other screen.
            if (ShellHeader != null)
                ShellHeader.Visibility = inLibrary ? Visibility.Collapsed : Visibility.Visible;

            // 🔴 A LAUNCH PROMPT OWNS THE WHOLE SCREEN. The tab strip is navigation for a grid the
            // user is no longer looking at: while "Start X?" is up, LB/RB do nothing and the tabs are
            // just a row of names behind the question. Reported on device 2026-09-04 as exactly that -
            // tabs above and the right-stick hint below, both still there behind the prompt.
            //
            // Collapsed, not dimmed. The immersive path dims because its chrome comes BACK on the next
            // press; this one does not, and a greyed row that never lights up reads as broken.
            //
            // ⚠️ Deliberately NOT LibraryOverlayOwnsScreen, which also covers settings and the game
            // menu. Those two are lists the user navigates INSIDE the library, and the tab they came
            // from is context worth keeping on screen. Only the launch prompt replaces the screen.
            if (inLibrary && LaunchPromptOwnsScreen)
            {
                TabStrip.Visibility = Visibility.Collapsed;
                return;
            }

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
                foreach (LibraryGroup g in Library.LibraryTabs.Visible())
                {
                    // EVERY tab is drawn, including the ones with nothing behind them - dimmed
                    // rather than absent (see GroupHasContent). Four of them used to disappear
                    // silently, and the reasoning was that "ROMs 0" invites a hunt for a bug that is
                    // really "you have no Playnite". That is true of a bare zero and stops being
                    // true once the tab is dimmed and its empty state names the reason: the user
                    // gets to see the category exists, and gets told why it is empty, which is more
                    // than an absent tab could ever say.
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

                // ONLY the info button docks here now. The right-stick readout used to sit beside
                // it and has moved down to the selected-title row - see BuildSelectedTitle. Two
                // things pushed it: the tabs are the row that grows (Favorites and Other Stores both
                // appear when earned), and the readout is about the GAMES, which is what the row
                // below it is about.
                var rightEnd = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightEnd.Children.Add(BuildInfoButton());

                _tabScroller = BuildEdgeFadedStrip(chips);
                FillDock(TabStripPanel, BuildLibraryBrandAndLb(), rightEnd, _tabScroller);
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

        /// <summary>
        /// The library mark, then the LB cap - the far-left end of the tab strip.
        ///
        /// This is the ONLY thing on this screen that says which app the covers belong to: the
        /// brand-and-device header is deliberately collapsed inside the library (see RefreshTabStrip),
        /// because covers want the height. A full-screen grid of box art with no mark on it reads as
        /// somebody else's launcher.
        ///
        /// ⚠️ It is NOT the header wordmark. Each area of ClawTweaks wears its own object and colour,
        /// so the library has a mark of its own; Home keeps the app's own icon. Swapping one for the
        /// other undoes the distinction rather than tidying it up.
        ///
        /// LB stays, and stays to the RIGHT of the mark: it says where the scrolling row starts, and
        /// the row starts after the brand, not at the window edge. The mark is decoration - it takes
        /// no focus and answers no button.
        /// </summary>
        private UIElement BuildLibraryBrandAndLb()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var mark = new Image
            {
                Source = new BitmapImage(new Uri(
                    "pack://application:,,,/Assets/branding/ctw-library-icon.png", UriKind.Absolute)),
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            // The source is 256 px for a 26 px box, so the downscale is where this either looks
            // clean or looks like a thumbnail. Same setting the header wordmark uses.
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            row.Children.Add(mark);

            row.Children.Add(BuildKeyCap("LB"));
            return row;
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

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            row.Children.Add(BuildCountBadge(count.Value, fontSize));
            return row;
        }

        /// <summary>The grey disc holding a count. Split out of BuildChipContent so the icon-only tab
        /// chips (BuildGroupChipContent) show the SAME badge - two hand-written copies of a rounded
        /// pill drift apart the first time one of them is adjusted.</summary>
        private static Border BuildCountBadge(int count, double fontSize)
        {
            var number = new TextBlock
            {
                Text = count.ToString(),
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
            return new Border
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
        }

        private static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        /// <summary>
        /// Whether a tab has anything behind it on THIS machine. Not a permission - every tab can
        /// still be opened - only whether it is drawn at full strength.
        ///
        /// One list, and that is the point: the chip row and the shoulder cycle each used to carry
        /// their own copy of these four conditions, which is two places to forget when a tab is
        /// added.
        /// </summary>
        private bool GroupHasContent(LibraryGroup g)
        {
            switch (g)
            {
                case LibraryGroup.Roms: return Library.PlayniteSource.IsPresent;
                case LibraryGroup.Favorites: return Library.FavoritesStore.Any();
                case LibraryGroup.OtherStores: return HasOtherStoreGames;
                case LibraryGroup.NotInstalled: return HasNotInstalledGames;
                default: return true;
            }
        }

        /// <summary>
        /// A tab chip: the group's icon always, its NAME only while it is the active tab.
        ///
        /// The strip did not fit. With ten labels spelled out, "Not Installed" sat off the right-hand
        /// edge and was only reachable by tabbing all the way across - reported on device 2026-09-04,
        /// and the tab a user needs least often is not the one that should be hardest to reach. An
        /// icon is about a quarter of the width of its name, so the whole strip fits at once and the
        /// name is still there for the one tab whose name is in question.
        ///
        /// The icon is the store's REAL logo when the launcher is installed (see Library/StoreIcons.cs
        /// for why it is extracted rather than shipped), a Segoe glyph otherwise.
        ///
        /// ⚠️ The count badge stays on every tab, active or not. It is the one thing that cannot be
        /// inferred from an icon, and it is why the strip is glanced at in the first place.
        /// </summary>
        // 20, while the fallback glyphs stay at font size 16. Not an inconsistency: a glyph is drawn
        // inside a font's em box with its own padding, so it covers noticeably less of its nominal
        // size than a bitmap does. Matching the NUMBERS made the picture icons look smaller than the
        // font ones, which is how it was reported on 2026-09-04.
        private const double TabIconSize = 20;

        private static UIElement BuildGroupChipContent(LibraryGroup g, int? count, bool active)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Three sources, in order of how well they identify the tab: the store's own logo, a
            // drawn vector where no logo exists but a font glyph would collide with another tab
            // (ROMs), and the shared glyph set last.
            var vector = Library.StoreIcons.VectorFor(g, active ? UiHelpers.Text : UiHelpers.Subtle, TabIconSize);
            var logo = vector == null ? Library.StoreIcons.For(g) : null;

            if (vector != null)
            {
                row.Children.Add(vector);
            }
            else if (logo != null)
            {
                row.Children.Add(new Image
                {
                    Source = logo,
                    Width = TabIconSize,
                    Height = TabIconSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    // The extracted icon is 32x32 drawn at 16; without this it is visibly mushy on a
                    // strip where it is the only thing identifying the tab.
                    SnapsToDevicePixels = true,
                    Opacity = active ? 1.0 : 0.75,
                });
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = Library.StoreIcons.GlyphFor(g),
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16,
                    Foreground = active ? UiHelpers.Text : UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (active)
            {
                row.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(GameLibrary.GroupLabel(g)),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiHelpers.Text,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (count != null)
            {
                var badge = BuildCountBadge(count.Value, 14);
                badge.Margin = new Thickness(active ? 6 : 5, 0, 0, 0);
                row.Children.Add(badge);
            }

            return row;
        }

        private UIElement BuildGroupChip(LibraryGroup g)
        {
            bool active = g == _libraryGroup;
            int count = _libraryScanned ? _library.ForGroup(g).Count : 0;
            bool hasContent = GroupHasContent(g);

            var chip = new Border
            {
                Child = BuildGroupChipContent(g, _libraryScanned && !ImmersiveCountsHidden ? count : (int?)null, active),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(13),
                Background = active ? UiHelpers.Card : Brushes.Transparent,
                BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(active ? 1 : 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                // Dimmed, not disabled. It still opens, and what it opens is the empty state that
                // says why - which is the only place that explanation can reach the user, because a
                // chip nobody can land on is a chip nobody can be told anything by.
                Opacity = hasContent || active ? 1.0 : 0.4,
            };
            var captured = g;
            chip.MouseLeftButtonUp += (_, __) => SetLibraryGroup(captured);
            return chip;
        }
        #endregion

        #region Enter / leave
        private void OpenLibrary()
        {
            _view = View.Library;
            ContentScroller.Visibility = Visibility.Collapsed;
            LibraryRoot.Visibility = Visibility.Visible;
            RefreshTabStrip();
            RenderLibrary();
            RefreshActionBar();
            RefreshLibrarySilently();
            if (Core.CenterSettings.StartSteamWithLibrary) PrewarmSteamInBackground();

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

        /// <summary>
        /// Starts Steam in the tray, off the UI thread, when the setting asks for it.
        ///
        /// OFF THE UI THREAD IS NOT OPTIONAL: the prewarm waits up to five seconds for Steam's
        /// process, and this runs while the library is drawing itself.
        ///
        /// Nothing is reported back, and nothing should be. It is a courtesy that either happened or
        /// did not - Steam already running is the normal outcome, and it is indistinguishable from
        /// success from anywhere the user can see.
        /// </summary>
        private static void PrewarmSteamInBackground()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Library.GameLibrary.PrewarmSteam(); } catch { }
            });
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

        /// <summary>
        /// A refresh that leaves the screen alone while it runs.
        ///
        /// The difference to RescanFromInstall is one line - _libraryScanned STAYS true - and that
        /// line is the whole point. With it false the grid empties and "Reading your stores..." takes
        /// the screen: the right answer the first time, and a flicker every time after. Here the list
        /// that is already up stays up, and each store repaints it as it lands.
        ///
        /// Called on EVERY library entry and again once a game ends, because both are moments when
        /// the answer has genuinely changed. History.Note writes the play at LAUNCH time, but the
        /// entries the Recent reel sorts were built during the previous scan - so without this,
        /// Recent keeps the order it had when the library was first opened, and the game you just
        /// played is missing from the one tab that exists to show it. That was the "press Y to see
        /// it" report.
        ///
        /// The first call still scans the ordinary way: with nothing scanned yet the spinner is the
        /// honest screen, and the guard below is the only thing this method adds to it.
        /// </summary>
        private void RefreshLibrarySilently()
        {
            if (_libraryScanning) return;
            _ = ScanLibraryAsync();
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
                // THE FIRST SCAN OF A SESSION PAINTS ONCE, AT THE END.
                //
                // Nine sources land one after another and each landing repainted the whole reel,
                // so the library visibly rebuilt itself up to nine times while the user watched -
                // reported as "only the first two games, then the rest pull in". Filling in beats
                // waiting on the slowest source when there is already something on screen; at
                // startup there is not, so it is only jitter, and the user is waiting for the
                // controller to mount anyway.
                //
                // _libraryScanned stays false while we hold, which is what keeps the
                // "Reading your stores" spinner up instead of showing a half-built grid.
                //
                // WITH A CEILING. Past FirstScanPatienceMs it falls back to painting per store,
                // because a hung source must degrade to the old behaviour rather than to a frozen
                // screen.
                var firstScanClock = System.Diagnostics.Stopwatch.StartNew();
                bool holdFirstPaint = !_libraryEverScanned;

                await _library.ScanAsync(ct, () => Dispatcher.Invoke(() =>
                {
                    if (holdFirstPaint && firstScanClock.ElapsedMilliseconds < FirstScanPatienceMs)
                        return;
                    holdFirstPaint = false;
                    _libraryScanned = true;
                    RenderLibraryIfNoOverlay();
                    RefreshTabStrip();
                }));
                _libraryEverScanned = true;
                _libraryScanned = true;
                _libraryScanning = false;
                RenderLibraryIfNoOverlay();
                RefreshTabStrip();

                StartArtFetch();
                WarmCoverCacheInBackground(ct);

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

            // The hint depends on WHICH tab is showing, so it is refreshed by the thing that
            // already runs on every tab change instead of from a hand-kept list of call sites.
            // Cheap - three property writes - and it cannot be forgotten by a screen added later.
            ApplyFooterVisibility();

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

            if (_libraryGroup == LibraryGroup.NotInstalled && _libraryScanned)
            {
                var note = BuildNotInstalledNote();
                Grid.SetRow(note, 0);
                LibraryRoot.Children.Add(note);
            }
            else if (_libraryGroup == LibraryGroup.Roms && _libraryScanned)
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

            // ── PUT THE CURSOR BACK ON SCREEN ───────────────────────────────────────────────────
            //
            // BuildReel and BuildGrid hand back a BRAND-NEW ListBox every time, so the scroll offset
            // is 0 again while _libSelectedIndex - a field - survives untouched. Cursor and covers
            // therefore disagree: the selection visuals and A/Start still act on the right game, and
            // that game is off screen, with the view sitting on the first tile. Reported for Recent
            // after leaving the game menu or the launch screen; it is not a Recent problem, it is
            // every path that re-renders - tab switch, art fetch, scan round, overlay close.
            //
            // Queued at Loaded priority, not called straight away: the list has not been measured
            // yet at this point, and ScrollIntoView on an unmeasured, unrealised virtualising panel
            // does nothing at all. Same reasoning, same fix as BringChipIntoView above.
            //
            // The identity check is what makes it safe to queue: a second render can land before
            // this runs, and scrolling a ListBox that is no longer on screen would move the cursor
            // of a view the user has already left.
            var builtList = _libList;
            if (builtList != null && ReferenceEquals(body, builtList))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_libList, builtList)) ScrollSelectionIntoView();
                }), DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// The line above the covers in the Not Installed tab.
        ///
        /// TWO THINGS, and the first one is not optional: the tab is Steam-only, and a user with an
        /// Epic library would otherwise read an incomplete list as a broken one. Saying which stores
        /// are covered is the difference between a limit and a bug.
        ///
        /// The second is any download in flight, with its percentage. It sits here rather than on the
        /// tile because it is the answer to "did my press do anything", and that answer has to be in
        /// a fixed place - hunting for one cover among eight hundred is not an answer.
        /// </summary>
        private UIElement BuildNotInstalledNote()
        {
            var stack = new StackPanel { Margin = new Thickness(LibOuterMargin, 8, LibOuterMargin, 0) };

            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Only Steam is supported for now."),
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });

            foreach (var g in _library.ForGroup(LibraryGroup.NotInstalled))
            {
                if (g.DownloadTotalBytes <= 0) continue;
                stack.Children.Add(new TextBlock
                {
                    Text = g.Title + "  ·  " + Core.Loc.T("Downloading") + " " + g.DownloadPercent + "%",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Accent,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            return stack;
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
                case LibraryGroup.Misc: return "No apps added yet.";
                // Reachable while a scan is still landing: the tab is drawn from the previous
                // round's count and the source it belongs to has not answered yet.
                case LibraryGroup.OtherStores: return "No games from these stores installed.";
                // Reachable for one frame: unfavoriting the last game while its own tab is on screen
                // still redraws it before the tab strip drops the now-empty chip.
                case LibraryGroup.Favorites: return "No favorites yet.";
                case LibraryGroup.NotInstalled: return "No Steam library found on this device.";
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
            var stack = new StackPanel();

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

            // The right-stick readout rides along on this row, hard right. It is two short chips
            // against a title that is already trimmed with an ellipsis, so it gets its width first
            // and the title takes what is left - the other way round, a long title would push the
            // only on-screen explanation of the gesture off the edge.
            var row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(LibOuterMargin, 10, LibOuterMargin, 10),
            };
            var readout = BuildSortStrip();
            DockPanel.SetDock(readout, Dock.Right);
            ((FrameworkElement)readout).VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(readout);
            row.Children.Add(stack);
            return row;
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

            // AN ENTRY THAT IS NOT INSTALLED SAYS SO FIRST. The rest of the line still applies - the
            // hours come from the account and are real even for a game that lives on another machine
            // - but "12 h" with no explanation on a game that is not there reads as a fault.
            if (!g.Installed)
                parts.Add(g.DownloadTotalBytes > 0
                    ? Core.Loc.T("Downloading") + " " + g.DownloadPercent + "%"
                    : Core.Loc.T("Not installed"));

            string played = Library.SteamPlaytime.Format(g.PlaytimeMinutes);
            if (played != null) parts.Add(played);

            // How much room it takes. On a handheld with one small drive this is the number that
            // decides what gets uninstalled next, and Steam hands it over for free in the manifest -
            // every other store would mean walking the folder tree on every scan round, which is why
            // this is blank for them rather than computed.
            string size = Library.SteamPlaytime.FormatSize(g.InstallBytes);
            if (size != null) parts.Add(size);

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

        // The LISTBOX item index of each tile row, which is NOT derivable from the tile index. With
        // grouping on, Items also carries the heading rows, and a group's last row stops early - so
        // "tile index / columns" drifts one item further off with every group above the cursor. That
        // drift is why the grid stopped following the cursor downwards whenever grouping was on: the
        // selection kept moving, while ScrollIntoView was handed a row well above the one to show.
        private readonly List<int> _libRowItemIndex = new List<int>();

        private UIElement BuildGrid()
        {
            MeasureGridMetrics();

            var rows = new List<LibraryRow>();
            _libRowStarts.Clear();
            _libRowCounts.Clear();
            _libRowItemIndex.Clear();

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
                // Recorded BEFORE the row goes in, so it is the position this row is about to take.
                // Any heading for this group is already in the list at this point and counted.
                _libRowItemIndex.Add(rows.Count);
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

            // COLUMNS ADDED, not a second target tile width. The width below is derived from the
            // column count, so adding a column here scales the covers down on its own - narrower AND
            // shorter, because the height comes from LibCoverAspect. A second width constant would be
            // a number that has to be kept in step with LibGridTileWidth by hand, and it would only be
            // right on the window size it was picked for.
            //
            // ONE IS NOW THE DEFAULT (user, 2026-09-04, after seeing it on device): what LibGridTileWidth
            // alone produces was too sparse, so the +1 that used to be the option is now the baseline
            // and the option goes one further. On this panel that reads as 5 -> 6 -> 7, but the
            // arithmetic stays relative, so a different window still gets a sensible pair.
            //
            // ⚠️ Deliberately NOT done by dropping LibGridTileWidth to ~158. That constant is also the
            // seed for _libTileWidth and _libDecodeWidth before the first measurement, and retuning a
            // shared constant to move one derived number is how two things that looked unrelated end
            // up drifting.
            _libColumns += Core.CenterSettings.DenseLibraryGrid ? 2 : 1;

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
            if (_tabEditorOpen) { MoveTabEditorSelection(dir); return; }
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

            // NOTHING WRAPS. The reel used to: left from the first tile landed on the last, as a
            // shortcut to the far end.
            //
            // The right stick now owns that shortcut (JumpLibrarySelection), and having both
            // was worse than having either. Two ways to reach the end is not twice as convenient
            // when one of them fires on the key you step sideways with: you arrive at the start,
            // press left once more out of habit, and are suddenly at the other end of the shelf
            // with nothing having said so. Reported exactly that way - "man versteht manchmal
            // nicht, wenn man am Anfang ankommt".
            //
            // A hard stop is also what makes the ends legible: the cursor refusing to move IS the
            // signal that there is nothing further, and it costs nothing because the stick flick
            // is the fast route and it is now named in the footer hint.

            if (next < 0 || next >= _libraryGames.Count) return;
            if (next == _libSelectedIndex) return;

            _libSelectedIndex = next;
            ApplySelectionVisuals();
            UpdateSelectedTitle();
            ScrollSelectionIntoView();
        }

        /// <summary>
        /// Jumps the cursor to the first or last tile. The right stick's left/right flick in Recent.
        ///
        /// It sits on the stick because in Recent BOTH stick axes are otherwise idle: the tab has no
        /// sort (it IS a sort) and no grouping, so the two things a flick normally does are both
        /// unavailable there. Nothing is being taken away from another tab.
        /// </summary>
        private void JumpLibrarySelection(bool toEnd)
        {
            if (_libraryGames.Count == 0) return;
            int next = toEnd ? _libraryGames.Count - 1 : 0;
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

            int row = RowForTile(_libSelectedIndex);
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

        /// <summary>The tile row holding a given tile, or -1 before the first BuildGrid.</summary>
        private int RowForTile(int tileIndex)
        {
            int row = -1;
            for (int i = 0; i < _libRowStarts.Count; i++)
            {
                if (tileIndex < _libRowStarts[i]) break;
                row = i;
            }
            return row;
        }

        private void ScrollSelectionIntoView()
        {
            if (_libList?.Items == null) return;

            int itemIndex;
            if (_libReelMode)
            {
                // One item per tile in the reel, so the tile index IS the item index.
                itemIndex = _libSelectedIndex;
            }
            else
            {
                // The row map is the authority wherever it exists. The column arithmetic below is
                // only for the moment before the first BuildGrid, and it is correct ONLY when every
                // row is full and nothing else shares the list - which is exactly why it must not be
                // the normal path.
                int row = RowForTile(_libSelectedIndex);
                itemIndex = row >= 0 && row < _libRowItemIndex.Count
                    ? _libRowItemIndex[row]
                    : (_libColumns > 0 ? _libSelectedIndex / _libColumns : 0);
            }

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
            // Every tab is in the cycle, including the dimmed ones. The shoulders used to skip
            // them, which made a visible tab unreachable from the pad - the exact trap this project
            // has paid for before with controls that could be seen and not focused. A dimmed tab
            // costs one shoulder press and answers a question; an unreachable one answers nothing.
            // The SAME list the strip draws (Library/LibraryTabs.cs). Walking the enum here while the
            // strip walks the user's order is how a hidden tab stays reachable from the shoulders and
            // a visible one stops being - two lists over one set, disagreeing in silence.
            var values = Library.LibraryTabs.Visible();
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
                // Everything else answers for itself - Ubisoft, EA, Battle.net, GOG and My Apps all
                // carry the name the subline under a cover already uses, so the heading above a
                // group and the line under the cursor cannot drift apart.
                default: return g.StoreName;
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

        /// <summary>
        /// Immersive mode belongs to ONE screen: the Recent shelf, in the library.
        ///
        /// The tab gate is not cosmetic. Recent is a single horizontal row of covers that speaks for
        /// itself; every other tab is a grid with counts, filters and a scrollbar, where hiding the
        /// chrome removes the only labels naming what the buttons do. And the setting is reachable
        /// from a screen the user may be driving with a mouse on an external display, where "click
        /// the right stick" is not an instruction they can follow.
        /// </summary>
        private bool ImmersiveActive =>
            _view == View.Library
            && _libraryGroup == LibraryGroup.Recent
            && Core.CenterSettings.ImmersiveMode;

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
            // Leaving Recent leaves immersive mode, so this has to RESTORE rather than return. A
            // bare return left the strip dimmed and the footer hidden on a grid tab, which is the
            // same stuck state the way out of the library used to produce.
            if (!ImmersiveActive) { StopImmersive(); return; }
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

        /// <summary>Puts everything back and stops both timers. Called on the way out of the library,
        /// on the way off the Recent tab, and whenever the setting is turned off - a dimmed tab strip
        /// left behind on the start screen would look like a rendering fault.</summary>
        private void StopImmersive()
        {
            _immersiveIdleTimer?.Stop();
            _footerRevealTimer?.Stop();
            _immersiveDim = false;
            _footerRevealed = false;
            ApplyImmersiveChrome();

            // ⚠️ And then put the chrome back WITHOUT consulting ImmersiveActive again.
            //
            // Both callers that leave the library run in this order:
            //
            //     LeaveLibrary();      // -> lands here
            //     _view = View.Home;   // only afterwards
            //
            // so _view still reads Library while this executes, ApplyFooterVisibility recomputes
            // footerHidden as true, and the footer is collapsed on the way OUT. Nothing outside the
            // library ever writes FooterBar.Visibility again - the single assignment lives in
            // ApplyFooterVisibility and both of its callers are library paths - so it stayed hidden
            // on Home, on Maintenance and on the ClawTweaks update screen, next to a hint telling a
            // mouse user on an external display to click the right stick.
            //
            // Restoring here rather than reordering those two callers is deliberate: the ordering is
            // not this method's to enforce, and the next screen that leaves the library would have
            // to remember it.
            if (FooterBar != null) FooterBar.Visibility = Visibility.Visible;
            if (ImmersiveHint != null) ImmersiveHint.Visibility = Visibility.Collapsed;
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

        /// <summary>True while a full-screen prompt has replaced the library: a launch/install prompt,
        /// or the quick menu.
        ///
        /// Still narrower than <see cref="LibraryOverlayOwnsScreen"/>: settings and the game menu are
        /// navigated INSIDE the library and keep their tab as useful context. These two replace the
        /// screen outright.
        ///
        /// ⚠️ The quick menu was excluded when this was written and that was wrong - reported the same
        /// day it shipped. Its three columns fill the screen exactly the way a launch prompt does, so
        /// the tabs and the stick hint behind it are the same distraction; "it has its own columns to
        /// look at" was an argument for hiding the rest, not for keeping it.</summary>
        private bool LaunchPromptOwnsScreen => _launchPrompt != LaunchPrompt.None || _exitPromptOpen;

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
                // WHAT THE RIGHT STICK DOES HERE, and it is two different things that must not
                // read as one sentence.
                //
                // In Recent the flick jumps to the end or back to the start. That line is shown
                // ALWAYS, immersive or not, because it replaces the wrap-around that used to be on
                // the D-pad: taking a shortcut away without naming its replacement would just make
                // the shelf feel longer.
                //
                // The click-for-button-hints line stays tied to the hidden footer - it is the way
                // back to a footer that is not there, and it says nothing while the footer is up.
                //
                // Together they go under one heading with a separator rather than as two
                // sentences: they are both "what the right stick does", and a hint row that reads
                // as a list of unrelated tips is a row people stop reading.
                string jump = _libReelMode
                    ? Core.Loc.T("Right stick: right jumps to the end, left to the start")
                    : null;
                string click = footerHidden
                    ? Core.Loc.T("Click the right stick to show the button hints")
                    : null;

                if (jump != null && click != null)
                    ImmersiveHint.Text = Core.Loc.T("Right stick actions") + ": " + jump + "  \u2022  " + click;
                else
                    ImmersiveHint.Text = jump ?? click ?? string.Empty;

                // Gone entirely while ANY overlay owns the screen, whatever it would otherwise say.
                // Both of its lines describe the right stick's effect ON THE SHELF, and the shelf is
                // not what is on screen - "right jumps to the end" under a "Start X?" question, or
                // over the library settings, is an instruction for a list the user cannot see.
                //
                // LibraryOverlayOwnsScreen rather than LaunchPromptOwnsScreen, and the condition is
                // taken from OnRightStickFlick on purpose: that method refuses the flick in exactly
                // these four states, so this is the set where the hint describes a gesture that does
                // nothing. It was still up over the settings screen and the tab editor until
                // 2026-09-05.
                ImmersiveHint.Visibility = ImmersiveHint.Text.Length > 0 && !LibraryOverlayOwnsScreen
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
        private const int SettingsStartSteamRow = 6;
        private const int SettingsDenseGridRow = 7;

        /// <summary>Opens the tab editor rather than toggling anything - the only row up here that
        /// leads somewhere instead of changing a value in place.</summary>
        private const int SettingsTabsRow = 8;

        /// <summary>The key row, and it is ALWAYS the last one: it holds a text box, so it spans the
        /// full width and sits on its own line below the pairs. The navigation maths below derives the
        /// pair count from this, so adding a switch above it needs no other change.</summary>
        private const int SettingsKeyRow = 9;

        // THREE, not two (user, 2026-09-05). Nine switches in two columns ran past the bottom of an
        // eight-inch panel again - the same reason this went from one column to two - and the rows are
        // a short label plus a switch, so the width was never carrying anything.
        private const int SettingsColumns = 3;

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
                MaxWidth = 1320,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Library settings"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            // Columns, because stacked rows ran off the bottom of an eight-inch panel and the key row
            // - the one people are sent here for - was the one below the fold.
            var pairs = new UniformGrid { Columns = SettingsColumns };
            pairs.Children.Add(BuildSettingRow(SettingsStartInLibraryRow, "Start in the library",
                Core.CenterSettings.OpenLibraryAtStartup, null));
            pairs.Children.Add(BuildSettingRow(SettingsSquareRomArtRow, "Square ROM art",
                _squareRomArt, null));
            pairs.Children.Add(BuildSettingRow(SettingsImmersiveRow, "Recent immersive",
                Core.CenterSettings.ImmersiveMode, null));
            pairs.Children.Add(BuildSettingRow(SettingsLaunchBehaviorRow, "After starting a game",
                null, LaunchBehaviorLabel(Core.CenterSettings.LaunchBehavior)));
            pairs.Children.Add(BuildSettingRow(SettingsStartWithClawTweaksRow, "Start Center with ClawTweaks",
                Core.CenterSettings.StartCenterWithClawTweaks, null));
            pairs.Children.Add(BuildSettingRow(SettingsRunInBackgroundRow, "Run in background",
                Core.CenterSettings.RunInBackground, null));
            pairs.Children.Add(BuildSettingRow(SettingsStartSteamRow, "Start Steam with the library",
                Core.CenterSettings.StartSteamWithLibrary, null));
            pairs.Children.Add(BuildSettingRow(SettingsDenseGridRow, "Denser grid",
                Core.CenterSettings.DenseLibraryGrid, null));
            pairs.Children.Add(BuildSettingRow(SettingsTabsRow, "Library tabs", null, TabsSummary()));
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
                    // From the key row, onto the LAST switch that exists rather than a fixed index.
                    // With an odd number of switches the bottom row is half empty, so "one row up"
                    // is a cell that is not there - and "pairs - columns" lands a whole row too high
                    // in exactly that case, skipping the switch the cursor just came down past.
                    if (_settingsIndex == last) { next = Math.Max(0, pairs - 1); break; }
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
                case SettingsDenseGridRow:
                    Core.CenterSettings.DenseLibraryGrid = !Core.CenterSettings.DenseLibraryGrid;
                    // No repaint here, deliberately - same as Square ROM art, which changes the tiles
                    // in the same way. CloseSettings already calls RenderLibrary, and that is when
                    // MeasureGridMetrics re-runs. Rendering from HERE would paint the library over the
                    // settings screen the user is still standing on.
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
                case SettingsStartSteamRow:
                    Core.CenterSettings.StartSteamWithLibrary = !Core.CenterSettings.StartSteamWithLibrary;
                    // Turning it ON acts now rather than at the next library entry: the user is one
                    // B press away from the library they just came out of, and a switch that only
                    // works from the second visit onwards looks like one that did not work.
                    if (Core.CenterSettings.StartSteamWithLibrary) PrewarmSteamInBackground();
                    break;
                case SettingsTabsRow:
                    OpenTabEditor();
                    return;
                case SettingsKeyRow:
                    _artKeyBox?.Focus();
                    _artKeyBox?.SelectAll();
                    return;
            }
            RenderLibrarySettings();
            RefreshActionBar();
        }

        /// <summary>What the settings row says without opening the editor: how much of the strip is
        /// left. "All tabs shown" rather than "10 of 10" - the count only means something once one is
        /// missing.</summary>
        private static string TabsSummary()
        {
            int all = Library.LibraryTabs.Ordered().Count;
            int shown = Library.LibraryTabs.Visible().Count;
            // "3 / 10" rather than "3 of 10": a sentence fragment split around a number translates
            // badly in four languages, and the strip's own count badges already read this way.
            return shown >= all ? Core.Loc.T("All tabs shown") : shown + " / " + all;
        }

        #region Tab editor
        // The tab strip, arranged by hand: order and visibility. It lives INSIDE the settings screen
        // rather than beside it - _tabEditorOpen implies _settingsOpen, so every guard that already
        // keeps the library still while settings are up covers this too, without a second flag having
        // to be added to nine call sites.
        //
        // Changes are written the moment they are made, not on the way out: this screen is left with
        // the Back button and there is no "cancel" anywhere else in the library, so an unsaved buffer
        // would be the one place where backing out loses work.
        private bool _tabEditorOpen;
        private int _tabEditorIndex;
        private List<LibraryGroup> _tabEditorOrder = new List<LibraryGroup>();
        private readonly HashSet<LibraryGroup> _tabEditorHidden = new HashSet<LibraryGroup>();
        private readonly List<Border> _tabEditorRows = new List<Border>();

        private void OpenTabEditor()
        {
            _tabEditorOpen = true;
            _tabEditorIndex = 0;
            _tabEditorOrder = Library.LibraryTabs.Ordered();
            _tabEditorHidden.Clear();
            foreach (var g in _tabEditorOrder)
                if (Library.LibraryTabs.IsHidden(g)) _tabEditorHidden.Add(g);

            RenderTabEditor();
            RefreshActionBar();
        }

        /// <summary>
        /// Back to the settings screen the editor was opened from.
        ///
        /// The current tab is moved if it was the one just hidden: leaving _libraryGroup pointing at a
        /// tab that is no longer drawn would show its games under a strip with nothing highlighted,
        /// which reads as a library that lost track of itself.
        /// </summary>
        private void CloseTabEditor()
        {
            _tabEditorOpen = false;
            _tabEditorRows.Clear();

            var visible = Library.LibraryTabs.Visible();
            if (!visible.Contains(_libraryGroup))
            {
                _libraryGroup = visible[0];
                _romSystem = null;
                _libSelectedIndex = 0;
            }

            RenderLibrarySettings();
            RefreshActionBar();
        }

        /// <summary>Two, because ten rows in one column pushed the heading off the top of an
        /// eight-inch panel - reported the day the editor shipped. The navigation below derives from
        /// this the same way the settings grid derives from SettingsColumns.</summary>
        private const int TabEditorColumns = 2;

        private void RenderTabEditor()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _tabEditorRows.Clear();

            // HEADING BESIDE THE LIST, NOT ABOVE IT. Stacked, it was the first thing to be pushed off
            // the screen by a list that is as long as the enum - so the one element that says what
            // this screen is was the one that disappeared. Beside it, the list can grow without ever
            // reaching it. The hints ride along with it for the same reason.
            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Tab visibility & order"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            // The shoulders are the only gesture on this screen with no footer chip of its own, so
            // they are named here. Two short lines rather than one long one - each says one thing.
            heading.Children.Add(TabEditorHint("Move a tab with LB and RB."));
            heading.Children.Add(TabEditorHint("Hide a tab with A."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new UniformGrid { Columns = TabEditorColumns };
            for (int i = 0; i < _tabEditorOrder.Count; i++)
            {
                var g = _tabEditorOrder[i];
                bool hidden = _tabEditorHidden.Contains(g);
                // compact: the side-column form - smaller text, smaller glyph, no card fill. Ten of
                // these fit where ten full-size rows did not, which is the whole point of the change.
                var row = BuildRowVisual(
                    Library.StoreIcons.GlyphFor(g),
                    GameLibrary.GroupLabel(g),
                    hidden ? "Hidden" : "Shown",
                    inCard: false,
                    dim: hidden,
                    compact: true);
                row.Margin = new Thickness(0, 0, 8, 6);
                row.MinWidth = 180;
                row.Tag = i;
                int captured = i;
                row.MouseLeftButtonUp += (_, __) => { _tabEditorIndex = captured; ToggleTabVisibility(); };
                _tabEditorRows.Add(row);
                list.Children.Add(row);
            }

            // Still capped and scrolled: the enum grows, and a list that runs off the bottom edge is
            // what this layout exists to avoid. Two columns just move the ceiling a long way up.
            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetColumn(scroller, 1);
            columns.Children.Add(scroller);

            LibraryRoot.Children.Add(columns);
            ApplyTabEditorSelection();
        }

        private static UIElement TabEditorHint(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 14,
            Foreground = UiHelpers.Subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        };

        private void ApplyTabEditorSelection()
        {
            Border selected = null;
            foreach (var row in _tabEditorRows)
            {
                bool on = row.Tag is int i && i == _tabEditorIndex;
                row.BorderBrush = on ? UiHelpers.Accent : Brushes.Transparent;
                if (on) selected = row;
            }
            // Same reason as the quick menu's side columns: these rows are never keyboard-focused, so
            // the scroller has nothing to follow on its own and the highlight would walk out of sight.
            selected?.BringIntoView();
        }

        /// <summary>Up and down step a whole row, left and right one cell - the grid the rows are
        /// laid out in, rather than the flat order underneath it. LB/RB keep moving the tab itself,
        /// so the two gestures cannot be confused for one another.</summary>
        private void MoveTabEditorSelection(PadButton dir)
        {
            int count = _tabEditorRows.Count;
            if (count == 0) return;

            int next = _tabEditorIndex;
            switch (dir)
            {
                case PadButton.Left:
                    if (_tabEditorIndex % TabEditorColumns == 0) return;
                    next = _tabEditorIndex - 1;
                    break;
                case PadButton.Right:
                    if (_tabEditorIndex % TabEditorColumns == TabEditorColumns - 1) return;
                    next = _tabEditorIndex + 1;
                    break;
                case PadButton.Up:
                    if (_tabEditorIndex < TabEditorColumns) return;
                    next = _tabEditorIndex - TabEditorColumns;
                    break;
                case PadButton.Down:
                    next = _tabEditorIndex + TabEditorColumns;
                    // An odd count leaves the bottom row half empty; stepping onto the last row that
                    // EXISTS beats refusing the press over a cell that is not drawn.
                    if (next >= count) next = count - 1;
                    break;
                default: return;
            }

            if (next < 0 || next >= count || next == _tabEditorIndex) return;
            _tabEditorIndex = next;
            ApplyTabEditorSelection();
        }

        /// <summary>
        /// Hides or shows the selected tab.
        ///
        /// THE LAST VISIBLE TAB CANNOT BE HIDDEN. A strip with nothing in it is a library with no way
        /// back to its own games, and the only way out of it would be the registry.
        /// </summary>
        private void ToggleTabVisibility()
        {
            if (_tabEditorIndex < 0 || _tabEditorIndex >= _tabEditorOrder.Count) return;
            var g = _tabEditorOrder[_tabEditorIndex];

            if (_tabEditorHidden.Contains(g)) _tabEditorHidden.Remove(g);
            else if (_tabEditorHidden.Count + 1 < _tabEditorOrder.Count) _tabEditorHidden.Add(g);
            else return;

            Library.LibraryTabs.Save(_tabEditorOrder, _tabEditorHidden);
            RenderTabEditor();
            RefreshActionBar();
        }

        /// <summary>Moves the selected tab one place, and moves the CURSOR with it - the alternative
        /// is a highlight that stays put while the row under it changes, which reads as the press
        /// having moved the wrong tab.</summary>
        private void MoveTabInOrder(int delta)
        {
            int from = _tabEditorIndex;
            int to = from + delta;
            if (from < 0 || from >= _tabEditorOrder.Count) return;
            if (to < 0 || to >= _tabEditorOrder.Count) return;

            var g = _tabEditorOrder[from];
            _tabEditorOrder.RemoveAt(from);
            _tabEditorOrder.Insert(to, g);
            _tabEditorIndex = to;

            Library.LibraryTabs.Save(_tabEditorOrder, _tabEditorHidden);
            RenderTabEditor();
        }
        #endregion

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
        /// <summary>
        /// Decodes every cover into GameArt's cache as soon as the scan is done, instead of waiting
        /// for a tile to come into view and ask for it.
        ///
        /// WHY IT IS WORTH DOING UP FRONT. The reel virtualises, so moving one place to the right
        /// realises a host that has never decoded its picture - and the user meets that on the FIRST
        /// press after the library appears, which is where it was reported (2026-09-05). There is
        /// nothing to save by being lazy here: the library is up long before the virtual controller
        /// has finished mounting, so this window is time the user is spending waiting anyway.
        ///
        /// At the decode width the tiles ACTUALLY USE, read on the UI thread before leaving it. A
        /// warm-up at the wrong width fills the cache with entries nothing will ever ask for - the
        /// key is "width|path" - so it would cost the memory and save nothing.
        ///
        /// Recent first, then everything else. Recent is the tab the library opens on, so its covers
        /// are the ones somebody is about to walk through; the rest ride along behind them.
        ///
        /// GameArt.LoadAsync caps itself at four concurrent decodes and hands back the SAME task for
        /// a path already in flight, so this cannot fight the tiles that are decoding at the same
        /// time - it joins them.
        ///
        /// ⚠️ This warms DECODED BITMAPS, nothing else. If a stall survives it, the remaining
        /// suspect is WPF realising containers (tile plus its VisualBrush reflection), which no cache
        /// can answer - that would be a change to how the reel is built, not to what it has ready.
        /// </summary>
        private void WarmCoverCacheInBackground(CancellationToken ct)
        {
            int decodeWidth = _libDecodeWidth;
            if (decodeWidth <= 0) return;

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _library.ForGroup(LibraryGroup.Recent))
                if (!string.IsNullOrEmpty(g.ArtPath) && seen.Add(g.ArtPath)) paths.Add(g.ArtPath);
            foreach (var g in _library.Games)
                if (!string.IsNullOrEmpty(g.ArtPath) && seen.Add(g.ArtPath)) paths.Add(g.ArtPath);

            if (paths.Count == 0) return;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int done = 0;
                try
                {
                    foreach (string path in paths)
                    {
                        if (ct.IsCancellationRequested) return;
                        if (await Library.GameArt.LoadAsync(path, decodeWidth).ConfigureAwait(false) != null) done++;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Core.InstallLog.Write("Cover warm-up failed: " + ex.Message); return; }

                // One line, at the end. It is the only way to answer "was it still warming when it
                // stuttered?" from a report, and the answer decides whether the next look belongs
                // here or in how the reel builds its containers.
                Core.InstallLog.Write(
                    $"Cover warm-up: {done}/{paths.Count} decoded at {decodeWidth}px in {clock.ElapsedMilliseconds} ms");
            }, ct);
        }

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
            _launchPrompt = game.Installed ? LaunchPrompt.Confirm : LaunchPrompt.ConfirmInstall;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        /// <summary>
        /// A on the install confirmation. This is where Steam is asked to fetch the game.
        ///
        /// STEAM DOES THE WORK AND STEAM ASKS AGAIN. <c>steam://install/&lt;appid&gt;</c> opens the
        /// client's own install dialog - the one with the library folder and the disk space - and
        /// there is deliberately no way to skip it. Center starting a fifty-gigabyte download off one
        /// button press, with no say in where it lands, is not a thing a launcher should be able to
        /// do. Our own prompt in front of it exists because A is also the button that scrolls past
        /// eight hundred covers to get here.
        ///
        /// The protocol handler starts Steam by itself when it is not running, but it is prewarmed
        /// first for the same reason a launch is: a cold Steam comes up with its full window in front
        /// of everything.
        /// </summary>
        private void ConfirmInstallNow()
        {
            var game = _launchTarget;
            if (game == null || _launchPrompt != LaunchPrompt.ConfirmInstall) return;

            // Already downloading: there is nothing to ask for, so this opens the queue instead of
            // asking Steam to install something it is already installing.
            string uri = game.DownloadTotalBytes > 0
                ? "steam://open/downloads"
                : "steam://install/" + game.Id;

            _launchPrompt = GameLibrary.OpenSteamUri(uri) ? LaunchPrompt.InstallHandedOver : LaunchPrompt.Failed;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        /// <summary>Closes the hand-over screen and rescans, so a finished install moves out of the
        /// Not Installed tab without the user having to find the Rescan chip on another screen. It is
        /// NOT automatic: an install takes minutes to hours, and a library that rescans itself on a
        /// timer would be doing it for nothing almost every time.</summary>
        private void RescanFromInstall()
        {
            ClearLaunchOverlay();
            if (_libraryScanning) return;
            _libraryScanned = false;
            _ = ScanLibraryAsync();
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

            if (started)
            {
                _launchStarting = true;
                _launchSteamColdStart = GameLibrary.LastLaunchStartedSteam;
                StartLaunchStartingTimer();
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

        /// <summary>
        /// Arms the one redraw that moves the screen from "starting" to "running".
        ///
        /// One-shot: it stops itself on the first tick. A repeating timer would keep rebuilding a
        /// screen that has nothing left to change on it, for as long as the game runs.
        /// </summary>
        private void StartLaunchStartingTimer()
        {
            _launchStartingTimer?.Stop();
            _launchStartingTimer = new DispatcherTimer { Interval = LaunchStartingWindow };
            _launchStartingTimer.Tick += (_, __) =>
            {
                _launchStartingTimer?.Stop();
                _launchStartingTimer = null;

                // The latch drops FIRST, before any guard can return. It is the state; the redraw
                // below is only how the state reaches the screen.
                _launchStarting = false;

                // Only if this screen is still the thing on it. The user can have pressed B, hidden
                // Center or started something else in the meantime.
                if (_launchPrompt != LaunchPrompt.Running || _optiWikiOpen) return;
                RenderLaunchOverlay();
                RefreshActionBar();
            };
            _launchStartingTimer.Start();
        }

        private void ClearLaunchOverlay()
        {
            _launchStartingTimer?.Stop();
            _launchStartingTimer = null;
            _launchStarting = false;
            _launchSteamColdStart = false;
            _launchPrompt = LaunchPrompt.None;
            _launchTarget = null;
            _optiWikiOpen = false;
            _optiWikiScroller = null;
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

                // The game that just ended is the newest thing Recent has, and nothing else on this
                // path picks it up: the play was noted at LAUNCH time into the history file, while
                // the entries the reel sorts came out of the previous scan. Guarded on the view
                // because a game can end while the user is somewhere else entirely, and a scan for a
                // screen nobody is looking at is work for nothing - the next entry refreshes anyway.
                if (_view == View.Library) RefreshLibrarySilently();

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

        /// <summary>
        /// The launch screen: the game's own key art behind it, its cover in front, the question on
        /// top of both.
        ///
        /// FOUR LAYERS, and the order is what makes text on a photograph readable at all:
        ///   1. a flat colour derived from the title, so the screen is never empty while the picture
        ///      is still decoding and never blank when there is no picture at all,
        ///   2. the backdrop itself, filled in asynchronously (see ApplyLaunchBackdropAsync),
        ///   3. a scrim - flat dim plus a vertical gradient - because a key image is designed to be
        ///      busy and light in places, and white text over it is a coin toss without one,
        ///   4. the cover, the headline and the line under it.
        ///
        /// The cover is sized from the height actually available rather than fixed: this same screen
        /// is drawn on an eight-inch handheld and in a desktop window, and a 420 px cover that fits
        /// one of them pushes the question off the other.
        /// </summary>
        /// <summary>
        /// X and Y on the launch screen, when the game has anything behind them.
        ///
        /// ON X AND Y RATHER THAN AS BUTTONS ON THE SCREEN. This screen asks one question with two
        /// answers, and A/B are those two answers everywhere in Center. Putting focusable buttons
        /// between them would turn a two-press decision into a navigation problem on a device that
        /// navigates with a thumbstick.
        ///
        /// OptiClick needs BOTH halves to be true: this game has an OptiClick route, and this machine
        /// has OptiClick. Offering it without the second is a button that can only fail.
        /// </summary>
        private void AddLaunchOptiActions()
        {
            var info = _launchTarget == null ? null : Library.GamePresets.For(_launchTarget);
            if (info == null) return;

            if (info.HasWiki) AddAction(PadButton.X, "OptiScaler wiki", true, OpenOptiWiki);
            if (info.IsOptiClick && Library.OptiClick.IsInstalled)
                AddAction(PadButton.Y, "Open OptiClick", true, LaunchOptiClick);
        }

        private void RenderLaunchOverlay()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();

            var game = _launchTarget;
            string title = game?.Title ?? string.Empty;
            string head, sub;
            switch (_launchPrompt)
            {
                case LaunchPrompt.Confirm:
                    head = Core.Loc.F("Start {0}?", title);
                    sub = null;
                    break;
                case LaunchPrompt.Running:
                    // Two lines for one state, split on the clock - see LaunchStartingWindow. The
                    // first minute is where a cold Steam, a launcher update and a shader build all
                    // sit, and "X is running" over a black screen for that minute reads as a lie.
                    if (LaunchStartingPhase)
                    {
                        head = Core.Loc.F("{0} is starting", title);
                        sub = "This can take a moment.";
                    }
                    else
                    {
                        head = Core.Loc.F("{0} is running", title);
                        sub = "The library comes back when the game ends.";
                    }
                    break;
                case LaunchPrompt.ConfirmInstall:
                    head = (game != null && game.DownloadTotalBytes > 0)
                        ? Core.Loc.F("{0} is downloading", title)
                        : Core.Loc.F("Install {0}?", title);
                    sub = "Steam asks you where to put it.";
                    break;
                case LaunchPrompt.InstallHandedOver:
                    head = title;
                    sub = "Steam has taken over. Rescan the library when it is done.";
                    break;
                default:
                    head = Core.Loc.F("Could not start {0}.", title);
                    sub = null;
                    break;
            }

            var host = new Grid { ClipToBounds = true };

            // Layer 1 - the plate. The same colour the coverless tiles use, so a game without art
            // looks like itself here too rather than like a different kind of screen.
            host.Children.Add(new Rectangle { Fill = Library.GameArt.ColorForTitle(title) });

            // Layer 2 - the backdrop. Added empty and filled in afterwards: it is a file read and
            // sometimes a download, and the question has to be on screen before either finishes.
            var backdrop = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            host.Children.Add(backdrop);

            // Layer 3 - the scrim.
            host.Children.Add(new Rectangle { Fill = LaunchScrimFlat });
            host.Children.Add(new Rectangle { Fill = LaunchScrimGradient });

            // Layer 4 - the content.
            double available = LibraryRoot.ActualHeight > 0 ? LibraryRoot.ActualHeight : 700;
            double coverHeight = Math.Max(200, Math.Min(420, available * 0.46));

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };

            var cover = new Image
            {
                Height = coverHeight,
                Width = coverHeight / LibCoverAspect,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(new Border
            {
                Child = cover,
                CornerRadius = new CornerRadius(10),
                // Black behind the picture, and clipped: the corners would otherwise be painted over
                // by the image and the rounding would only show while the cover is still decoding.
                Background = Brushes.Black,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 22),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 28,
                    ShadowDepth = 6,
                    Direction = 270,
                    Opacity = 0.65,
                    Color = Colors.Black,
                },
            });

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
                    Text = Core.Loc.T(sub),
                    FontSize = 15,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                });

            var opti = game == null ? null : Library.GamePresets.For(game);
            if (opti != null && opti.HasAnything)
            {
                var badges = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 16, 0, 0),
                };

                // THREE BADGES, THREE DIFFERENT CLAIMS - they only look alike:
                //   OptiClick  - the tool officially supports this game,
                //   OptiScaler - somebody tested a manual route and it worked,
                //   Wiki       - OptiScaler's wiki has a page for it, which is NOT a verdict. A page
                //                can exist for a game with no working route, which is why this one is
                //                grey while the other two are lit.
                if (opti.IsOptiClick) badges.Children.Add(LaunchBadge("OptiClick", true));
                if (opti.IsOptiScaler) badges.Children.Add(LaunchBadge("OptiScaler", true));
                // NAMED, not just "Wiki page". Next to two badges that carry their tool's name, a
                // bare "Wiki" reads as OptiClick's wiki - it is OptiScaler's, and it is there for
                // games OptiClick has never heard of.
                if (opti.HasWiki) badges.Children.Add(LaunchBadge(Core.Loc.T("OptiScaler wiki"), false));

                stack.Children.Add(badges);
            }

            // Under the badges: the one thing that explains a longer than usual wait.
            //
            // NOT A BADGE, deliberately, even though it sits under a row of them. The badges above
            // are STANDING FACTS about the game - a lit OptiClick badge says the same thing tomorrow.
            // This says something is happening right now, and giving it the same shape would file it
            // with the facts. The spinner is the whole difference: it is the one element on this
            // screen that moves, and movement is what separates "still working" from "a label".
            //
            // ONLY WHILE STARTING, and only when we actually started Steam ourselves. Once the game
            // is up it is no longer about anything, and on a machine where Steam was already running
            // it never was - a permanent notice about a wait nobody is having teaches people to stop
            // reading this area.
            if (_launchPrompt == LaunchPrompt.Running && _launchSteamColdStart && LaunchStartingPhase)
            {
                var steamNote = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 16, 0, 0),
                };

                var spinner = Ui.GifSpinner.Create(22);
                spinner.VerticalAlignment = VerticalAlignment.Center;
                spinner.Margin = new Thickness(0, 0, 10, 0);
                steamNote.Children.Add(spinner);

                steamNote.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Steam starts first. This takes longer."),
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiHelpers.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });

                stack.Children.Add(steamNote);
            }

            // Layer 5 - the two profile panels, one either side of the cover.
            //
            // THREE COLUMNS, star / auto / star. The centre column takes exactly the width the cover
            // and the headline need; the two star columns split what is left, and each panel is
            // centred inside its own column - so a panel sits midway between the cover and the edge
            // of the screen rather than being pinned to either. That also means the panels cannot
            // push the cover off centre, however long a line in them gets.
            var sides = new Grid();
            sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sides.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(stack, 1);
            sides.Children.Add(stack);

            var details = game == null ? null : Library.ClawProfileDetails.For(game);
            if (details != null)
            {
                // Performance on the left, controller on the right - the side each one is on is the
                // whole navigation here, so it is fixed rather than "whichever exists".
                var left = BuildProfilePanel(Core.Loc.T("Performance"), details.Performance, 0);
                if (left != null) sides.Children.Add(left);
                var right = BuildProfilePanel(Core.Loc.T("Controller"), details.Controller, 2);
                if (right != null) sides.Children.Add(right);
            }

            host.Children.Add(sides);
            LibraryRoot.Children.Add(host);

            if (game != null)
            {
                ApplyLaunchCover(game, cover, (int)Math.Round(coverHeight / LibCoverAspect));
                _ = ApplyLaunchBackdropAsync(game, backdrop);
                EnsureCatalogForLaunch();
            }
        }

        /// <summary>
        /// Pulls the catalog in the first time a launch screen is opened, and redraws once it lands.
        ///
        /// NOT DURING THE LIBRARY SCAN, deliberately: the file is 3 MB and the answer is only ever
        /// needed on this one screen. The cost of doing it here is that the very first launch screen
        /// of a session may show its badges a moment late; the cost of doing it there would be a 3 MB
        /// download every time somebody opens the library to start a game they already know.
        /// </summary>
        private void EnsureCatalogForLaunch()
        {
            if (Library.GamePresets.Loaded) return;

            Library.GamePresets.EnsureLoadedAsync(CancellationToken.None).ContinueWith(_ =>
                Dispatcher.Invoke(() =>
                {
                    // Only if a launch screen is still the thing on screen. Redrawing it from under a
                    // user who has already pressed B would put it back up.
                    if (_launchPrompt == LaunchPrompt.None || _optiWikiOpen) return;
                    RenderLaunchOverlay();
                    RefreshActionBar();
                }), TaskScheduler.Default);
        }

        /// <summary>
        /// One of the two profile panels beside the cover, or null when the profile sets nothing on
        /// that side.
        ///
        /// NULL RATHER THAN AN EMPTY BOX. A panel headed "Controller" with nothing under it says the
        /// profile is empty, when what it usually means is that this game has no controller profile
        /// at all - and the reader only ever emits a line for a value that IS set, so an empty list
        /// is the normal case rather than a fault.
        ///
        /// Not focusable and not in any navigation order: this screen asks one question with two
        /// answers (A and B), and anything the stick could land on turns that into navigation. The
        /// panels are here to be READ while deciding, which is why they carry no controls.
        /// </summary>
        private UIElement BuildProfilePanel(string heading, List<Library.ProfileLine> lines, int column)
        {
            if (lines == null || lines.Count == 0) return null;

            var stack = new StackPanel { MaxWidth = 260 };
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 0, 0, 10),
            });

            foreach (var line in lines)
            {
                // Label above value, not side by side: the labels differ in length by a factor of
                // three ("HDR" against "Efficient Aggressive At Guaranteed"), and a two-column layout
                // either wraps the value or leaves most of the panel empty.
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(line.Label),
                    FontSize = 11,
                    Foreground = UiHelpers.Subtle,
                });
                row.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(line.Value),
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Text,
                    TextWrapping = TextWrapping.Wrap,
                });
                stack.Children.Add(row);
            }

            var border = new Border
            {
                Child = stack,
                Background = LaunchPanelFill,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 0, 24, 0),
            };
            Grid.SetColumn(border, column);
            return border;
        }

        /// <summary>Darker than the card colour and semi-transparent: these boxes sit on the game's
        /// own artwork, and an opaque card would read as a dialog over the picture rather than as
        /// something written on it.</summary>
        private static readonly Brush LaunchPanelFill =
            Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x14)));

        private static UIElement LaunchBadge(string text, bool lit)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = lit ? UiHelpers.Text : UiHelpers.Subtle,
                },
                Background = lit ? UiHelpers.Card : Brushes.Transparent,
                BorderBrush = lit ? UiHelpers.Accent : UiHelpers.Subtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0),
            };
        }

        /// <summary>Flat dim, under the gradient. A gradient on its own leaves the middle of the
        /// screen - where the cover and the headline are - at whatever brightness the artwork
        /// happens to have there.</summary>
        private static readonly Brush LaunchScrimFlat =
            Freeze(new SolidColorBrush(Color.FromArgb(0x8A, 0x0B, 0x0B, 0x0E)));

        /// <summary>Darker at the two edges than in the middle, so the picture stays visible where
        /// nothing sits on top of it and the text has ground under it where something does.</summary>
        private static readonly Brush LaunchScrimGradient = Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xCC, 0x0B, 0x0B, 0x0E), 0.0),
                new GradientStop(Color.FromArgb(0x33, 0x0B, 0x0B, 0x0E), 0.45),
                new GradientStop(Color.FromArgb(0xE6, 0x0B, 0x0B, 0x0E), 1.0),
            },
            new Point(0.5, 0), new Point(0.5, 1)));

        /// <summary>The SteamGridDB backdrop fetch currently in flight, and the game it belongs to.
        /// Not a cache - SteamGridDb keeps that - only a way for the two renders of one launch to
        /// share one request.</summary>
        private Task<string> _launchHeroTask;
        private GameEntry _launchHeroFor;

        private void ApplyLaunchCover(GameEntry game, Image target, int decodeWidth)
        {
            if (game.ArtPath == null) return;

            var wanted = game;
            Library.GameArt.LoadAsync(game.ArtPath, Math.Max(120, decodeWidth)).ContinueWith(t =>
            {
                var bmp = t.Result;
                if (bmp == null) return;
                Dispatcher.Invoke(() =>
                {
                    // The screen may have been dismissed, or moved on to another game, while a JPEG
                    // was decoding. Painting into a discarded Image would be harmless; painting the
                    // wrong game's cover would not.
                    if (_launchTarget != wanted || _launchPrompt == LaunchPrompt.None) return;
                    target.Source = bmp;
                });
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// Fills in the backdrop, in three descending qualities.
        ///
        ///   1. Steam's own cached hero image. Free, local, no key - and present for all 44 installed
        ///      games on this machine, which is better coverage than the covers themselves have.
        ///   2. SteamGridDB, ONCE per game and only with a key configured. This is the case Steam
        ///      cannot answer: Epic, Xbox, Ubisoft, EA, Battle.net and GOG cache no key art at all.
        ///   3. The cover, filling the screen and blurred hard. It is not a backdrop and does not
        ///      pretend to be one - it is there so the screen carries the GAME'S colours in the second
        ///      before it starts, instead of a flat plate.
        ///
        /// What (2) finds is written back onto the entry, so a second launch in the same session does
        /// not even hit the cache index.
        /// </summary>
        private async Task ApplyLaunchBackdropAsync(GameEntry game, Image target)
        {
            try
            {
                string path = game.HeroPath;

                if (path == null && Library.SteamGridDb.HasKey)
                {
                    // ⚠ THIS SCREEN IS RENDERED TWICE PER LAUNCH - once to ask, once to say the game
                    // is running - and a download that is still in flight when the user presses A
                    // would otherwise be started a second time against the same personal API quota.
                    // The task is kept, not the result, so the second render awaits the first fetch
                    // instead of racing it.
                    if (_launchHeroTask == null || _launchHeroFor != game)
                    {
                        _launchHeroFor = game;
                        _launchHeroTask = Library.SteamGridDb.EnsureHeroAsync(game, CancellationToken.None);
                    }

                    path = await _launchHeroTask.ConfigureAwait(true);
                    if (path != null) game.HeroPath = path;
                }

                bool blur = false;
                if (path == null) { path = game.ArtPath; blur = true; }
                if (path == null) return;

                // A picture about to be blurred to a smear does not need decoding at full width - the
                // blur throws that detail away, and this is the path that runs on the machines with
                // the least to spare.
                var bmp = await Library.GameArt.LoadAsync(path, blur ? 360 : 1280).ConfigureAwait(true);
                if (bmp == null) return;
                if (_launchTarget != game || _launchPrompt == LaunchPrompt.None) return;

                target.Source = bmp;
                target.Effect = blur
                    ? new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 64,
                        KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                    }
                    : null;

                // Faded in rather than snapped in: the picture lands a moment after the question, and
                // a hard cut reads as the screen having been redrawn underneath the user.
                //
                // The value is ASSIGNED FIRST and animated afterwards. The Image starts at zero
                // opacity so nothing flashes before its picture is ready, and an animation is the one
                // way of getting it back that can silently not happen - a backdrop invisible because
                // an animation was dropped is indistinguishable from a backdrop that was never found.
                double shown = blur ? 0.85 : 1.0;
                target.Opacity = shown;
                target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = shown,
                    Duration = TimeSpan.FromMilliseconds(220),
                });
            }
            catch { }
        }

        #region OptiScaler wiki panel

        private bool _optiWikiOpen;
        private ScrollViewer _optiWikiScroller;

        /// <summary>
        /// The game's page from OptiScaler's wiki, over the launch screen.
        ///
        /// A READING SCREEN, not a menu: there is nothing to select, so the D-pad has nothing to do
        /// and the RIGHT STICK scrolls it - the same gesture that scrolls every other long screen in
        /// Center. B goes back to the launch screen, which is still underneath with its question
        /// unanswered.
        /// </summary>
        private async void OpenOptiWiki()
        {
            var game = _launchTarget;
            var info = game == null ? null : Library.GamePresets.For(game);
            if (info == null || !info.HasWiki) return;

            _optiWikiOpen = true;
            RenderOptiWiki(info, null, loading: true);
            RefreshActionBar();

            var rows = await Library.OptiWiki.GetAsync(info.WikiPage, CancellationToken.None);

            // The user may have gone back, or on to another game, while the page was in flight.
            if (!_optiWikiOpen || _launchTarget != game) return;
            RenderOptiWiki(info, rows, loading: false);
        }

        private void CloseOptiWiki()
        {
            _optiWikiOpen = false;
            _optiWikiScroller = null;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        private void RenderOptiWiki(Library.GamePresets.Info info,
                                    System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> rows,
                                    bool loading)
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();

            var stack = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center };

            stack.Children.Add(new TextBlock
            {
                Text = info.Title ?? _launchTarget?.Title ?? string.Empty,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = "OptiScaler wiki · " + (info.WikiPage ?? string.Empty).Replace('-', ' '),
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 2, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            // WHAT THE CATALOG SAYS COMES FIRST, and it is printed whether or not the wiki answered:
            // it is the part we can be sure of, and it carries the anti-cheat rule. A route shown
            // without its caveat is the dangerous half arriving alone.
            AddWikiRow(stack, Core.Loc.T("Route"), info.SupportPath ?? info.Tool);
            AddWikiRow(stack, Core.Loc.T("Output"), info.Output);
            AddWikiRow(stack, Core.Loc.T("Preset"), info.Preset);
            AddWikiRow(stack, Core.Loc.T("Frame generation"), info.FgOutput);
            AddWikiRow(stack, Core.Loc.T("Requirements"), info.Requirements);
            AddWikiRow(stack, Core.Loc.T("Anti-cheat"), info.AntiCheat);
            AddWikiRow(stack, Core.Loc.T("Recommendation"), info.Policy);

            if (loading)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Loading the page…"),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 16, 0, 0),
                });
            }
            else if (rows == null || rows.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("The wiki page could not be read. Open it in a browser."),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 16, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = UiHelpers.Subtle,
                    Opacity = 0.3,
                    Margin = new Thickness(0, 14, 0, 10),
                });
                foreach (var kv in rows) AddWikiRow(stack, kv.Key, kv.Value);
            }

            _optiWikiScroller = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(LibOuterMargin, 18, LibOuterMargin, 18),
            };
            LibraryRoot.Children.Add(_optiWikiScroller);
        }

        private static void AddWikiRow(Panel into, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var text = new TextBlock
            {
                Text = value,
                FontSize = 13,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            into.Children.Add(grid);
        }

        /// <summary>The right stick on the wiki panel. Returns false when the panel is not up, so the
        /// caller can fall through to whatever else owns the stick.</summary>
        private bool ScrollOptiWiki(double delta)
        {
            if (!_optiWikiOpen || _optiWikiScroller == null) return false;
            try { _optiWikiScroller.ScrollToVerticalOffset(_optiWikiScroller.VerticalOffset + delta); }
            catch { }
            return true;
        }

        private void OpenOptiWikiInBrowser()
        {
            var info = _launchTarget == null ? null : Library.GamePresets.For(_launchTarget);
            if (info == null || !info.HasWiki) return;
            Core.PrerequisiteGuide.OpenPage(Library.OptiWiki.BrowserUrl(info.WikiPage),
                                            m => Core.InstallLog.Write(m));
        }

        /// <summary>Starts OptiClick. Both halves have to be true before this is offered - the game
        /// has an OptiClick route AND this machine has OptiClick - so a failure here means it went
        /// missing between the two, and it says so rather than doing nothing visible.</summary>
        private void LaunchOptiClick()
        {
            if (!Library.OptiClick.Launch())
                Core.InstallLog.Write("OptiClick could not be started - check that it is still installed.");
        }

        #endregion

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
            _optiWikiOpen = false;
            _optiWikiScroller = null;
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
                Text = Core.Loc.T("ClawTweaks Library Overview"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 18),
            });

            // Top line, above every heading: it is the one setting that changes what the user sees
            // on the NEXT start, so it belongs where it is read before anything else.
            stack.Children.Add(InfoLead("Go into Library Settings and switch on Start in the library."));

            stack.Children.Add(InfoHeading("Your games"));
            stack.Children.Add(InfoLine("Shows your installed Steam, Xbox and Epic games."));
            stack.Children.Add(InfoLine("Steam cover art is found automatically."));
            // One line, not three: this screen has no scroll viewer and was already clipping at the
            // bottom. Playnite locks its database while it runs, so closing it is not a nicety - an
            // open Playnite makes the ROMs unreadable. The instruction is on screen, the reason is
            // not.
            stack.Children.Add(InfoLine("Add ROMs in Playnite, then close it \u2014 they are found automatically."));

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

            // The immersive-mode block used to sit here. It is gone because this screen clips, and
            // it was the least load-bearing section on it: the setting explains itself where it
            // lives, in Library Settings. The "Immersive mode" translation key stays - the settings
            // row still uses it.
            stack.Children.Add(InfoHeading("Use CTW Library with Windows Fullscreen Experience (FSE) via AnyFSE"));
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
                Text = AnyFsePath,
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

        /// <summary>The FOLDER Center is installed in, not the exe inside it - AnyFSE's own path
        /// field does not accept a path down to the exe itself (reported 2026-09-03: it silently
        /// refused "...\ClawTweaksCenter\CTW_Center.exe"). Derived from <see
        /// cref="Core.SelfInstaller.InstalledExe"/>, which already resolves classic vs. Velopack -
        /// stripping the filename here keeps that one source of truth instead of duplicating it.</summary>
        private static string AnyFsePath => System.IO.Path.GetDirectoryName(Core.SelfInstaller.InstalledExe);

        /// <summary>Puts the installed Center folder on the clipboard. Wrapped because the clipboard
        /// is a shared OS resource - another process holding it open makes Clipboard.SetText throw,
        /// and a failed copy must not take the library down with it.</summary>
        private void CopyAnyFsePath()
        {
            try { Clipboard.SetText(AnyFsePath); }
            catch (Exception ex) { Core.InstallLog.Write("Copying the Center path failed: " + ex.Message); }
        }
        #endregion

        #region Leaving the library
        private void OpenExitPrompt()
        {
            if (LaunchOverlayOpen || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            _exitPromptOpen = true;
            _exitPromptColumn = ExitPromptColumnCenter;
            _exitPromptIndex = 0;
            _exitPromptNote = null;
            RenderExitPrompt();
            RefreshActionBar();

            // Fire-and-forget: the tray column starts empty ("Loading...") and fills in when the
            // helper answers, a beat or two later - fine for a menu the user just opened rather than
            // something on a hot path. See CenterMenuWindow.QuickMenu.cs.
            _ = RequestTrayAppsAsync();
        }

        private void CloseExitPrompt()
        {
            _exitPromptOpen = false;
            _exitPromptRows.Clear();
            _exitPromptActions.Clear();
            _exitPromptTrayRows.Clear();
            _exitPromptTrayActions.Clear(); _exitPromptTrayCloseActions.Clear();
            _exitPromptToolsRows.Clear();
            _exitPromptToolsActions.Clear();
            RenderLibrary();
            RefreshActionBar();
        }

        private void RenderExitPrompt()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _exitPromptRows.Clear();
            _exitPromptActions.Clear();
            _exitPromptTrayRows.Clear();
            _exitPromptTrayActions.Clear(); _exitPromptTrayCloseActions.Clear();
            _exitPromptToolsRows.Clear();
            _exitPromptToolsActions.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Library quick menu"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            if (!string.IsNullOrEmpty(_exitPromptNote))
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(_exitPromptNote),
                    FontSize = 14,
                    Foreground = UiHelpers.Warn,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12),
                });

            // \u26A0\uFE0F LEAVING CENTER IS ONE ROW, NOT TWO. "Minimize to tray" and "Close Center" both stood
            // here, and with Run in background OFF they did the SAME THING: the minimize row calls
            // Close(), and the Closing handler exits when there is no tray to go to. Two rows, one
            // outcome, and nothing on screen said which. The setting decides which row exists at all.
            //
            // Ordered by how much each throws away, least first - so the row order differs between
            // the two cases rather than the label just swapping in place.
            if (Core.CenterSettings.RunInBackground)
                AddExitPromptRow(stack, "\uE921", "Minimize", "Center keeps running.",
                    () => { _exitPromptOpen = false; Close(); });

            AddExitPromptRow(stack, "\uE80F", "Center start screen", "Leave the library open.",
                () => { _exitPromptOpen = false; _exitPromptRows.Clear(); _exitPromptActions.Clear();
                        _exitPromptTrayRows.Clear(); _exitPromptTrayActions.Clear(); _exitPromptTrayCloseActions.Clear();
                        _exitPromptToolsRows.Clear(); _exitPromptToolsActions.Clear(); GoHome(); });

            if (!Core.CenterSettings.RunInBackground)
                AddExitPromptRow(stack, "\uE711", "Close Center", "Ends Center completely.",
                    () => Application.Current.Shutdown());

            // THE DEVICE, not Center - which is why the four sit inside ONE card. They are the same
            // kind of decision as each other and a different kind from the rows above, and four
            // free-standing rows made the screen read as seven unrelated choices.
            //
            // Every one is the helper's own PowerAction verb, the same the Game Bar power tile sends,
            // so Center carries no power code at all. Reboot to firmware is deliberately left out: it
            // is the only verb whose mis-press cannot be undone by waiting.
            //
            // RESTART LAST. Sleep and Hibernate come back to where you were, Shut down does not, and
            // Restart is the longest way round of the four - so the list runs from the cheapest to
            // the most expensive, the same rule as the rows above it.
            //
            // Sleep and Hibernate keep their English names in every language on purpose; they are
            // what Windows itself calls these states here. Restart and Shut down are translated.
            var power = new StackPanel();
            AddExitPromptRow(power, "\uE708", "Sleep", "Keeps your session in memory.",
                () => SendPowerAction("sleep"), inCard: true);
            AddExitPromptRow(power, "\uE74E", "Hibernate", "Saves your session to disk.",
                () => SendPowerAction("hibernate"), inCard: true);
            AddExitPromptRow(power, "\uE7E8", "Shut down", "Closes everything and powers off.",
                () => SendPowerAction("poweroff"), inCard: true);
            AddExitPromptRow(power, "\uE777", "Restart", "Closes everything and starts again.",
                () => SendPowerAction("reboot"), inCard: true);

            stack.Children.Add(new Border
            {
                Child = power,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                // Padding 6 -> 12 and the width down from 520: with a sidebar on each side the centre
                // column ran off the right-hand edge, and the card sat tighter than the rows in it
                // (user, 2026-09-04). Narrower is free here - the rows are icon + two short lines, so
                // the width was never carrying anything.
                Padding = new Thickness(6, 12, 6, 12),
                Margin = new Thickness(0, 6, 0, 0),
                MinWidth = ExitPromptCentreWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            // Three columns: curated Windows tools (left) and tray apps (right) flank the buttons
            // above, unchanged in the middle. Fixed side widths rather than equal thirds - these are
            // sidebars for a list of icon+text rows, not a peer of the button stack's own width.
            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

            var toolsColumn = BuildToolsColumn();
            Grid.SetColumn(toolsColumn, ExitPromptColumnTools);
            columns.Children.Add(toolsColumn);

            Grid.SetColumn(stack, ExitPromptColumnCenter);
            columns.Children.Add(stack);

            var trayColumn = BuildTrayColumn();
            Grid.SetColumn(trayColumn, ExitPromptColumnTray);
            columns.Children.Add(trayColumn);

            LibraryRoot.Children.Add(columns);
            ApplyExitPromptSelection();
        }

        /// <summary>Adds one row and the thing it does, in one call. The index is taken from the list
        /// rather than passed in: two hand-kept sequences over the same positions is how the wrong row
        /// gets triggered the moment somebody inserts one, and four of these rows now end the
        /// session.</summary>
        private void AddExitPromptRow(StackPanel stack, string glyph, string title, string subtitle,
                                      Action activate, bool inCard = false)
        {
            int index = _exitPromptRows.Count;
            _exitPromptActions.Add(activate);
            stack.Children.Add(ExitPromptRow(index, glyph, title, subtitle, inCard));
        }

        /// <summary>
        /// Hands a power action to the helper, and says so when there is nobody to hand it to.
        ///
        /// Center owns no power code by design. The helper already runs elevated and holds the
        /// shutdown privilege, and it is the single place that knows what these words mean on this
        /// device - its "sleep" turns the display off and lets the machine drift into Modern Standby,
        /// because there is no API to force S0. A second implementation here would be a second answer
        /// to that question, and the two would drift.
        /// </summary>
        /// <summary>Guards against a second press while the first is still connecting - the connect
        /// can take a couple of seconds, and two shutdown requests in flight is not something to find
        /// out about afterwards.</summary>
        private bool _powerActionBusy;

        private async void SendPowerAction(string action)
        {
            if (_powerActionBusy) return;
            _powerActionBusy = true;
            try
            {
                // ⚠️ THE PIPE IS NOT CONNECTED HERE, and assuming it was is what made the first
                // version of this report "the helper is not running" on a machine where it plainly
                // was. Center's shared HelperPipeClient is only ever connected from inside the
                // onboarding, maintenance and uninstall flows; nothing on the library path had ever
                // needed it, so IsConnected was simply false and SendRequest returned false without
                // going near the helper. The message was true about the PIPE and wrong about the
                // thing the user was being told about.
                if (_helperPipe == null)
                {
                    ShowPowerActionFailed(action, "no pipe client");
                    return;
                }

                if (!_helperPipe.IsConnected)
                {
                    _exitPromptNote = null;
                    // Short, because this is a keypress and not a setup step: a healthy helper binds
                    // in well under a second, and the flows that wait 45 s are doing it while the
                    // user watches a progress screen.
                    bool ok = await _helperPipe.ConnectAsync(
                        TimeSpan.FromSeconds(6), m => Core.InstallLog.Write(m));
                    if (!ok)
                    {
                        ShowPowerActionFailed(action, "could not reach the helper");
                        return;
                    }
                }

                if (!_helperPipe.SendRequest("PowerAction", action))
                {
                    ShowPowerActionFailed(action, "the pipe rejected the write");
                    return;
                }

                Core.InstallLog.Write($"Power action '{action}' sent to the helper.");

                // Sleep and Hibernate come back to a live Center, so the prompt has to be gone by
                // then. For restart and shut down nobody sees this, and it costs nothing.
                CloseExitPrompt();
            }
            catch (Exception ex)
            {
                // async void: an escaping exception here takes the PROCESS, not just the press.
                ShowPowerActionFailed(action, ex.Message);
            }
            finally { _powerActionBusy = false; }
        }

        /// <summary>The prompt STAYS UP with a line saying why. Closing it would look exactly like a
        /// press that worked, while the device is still on.</summary>
        private void ShowPowerActionFailed(string action, string why)
        {
            Core.InstallLog.Write($"Power action '{action}' not sent - {why}.");
            _exitPromptNote = "ClawTweaks is not running. Start it and try again.";
            if (_exitPromptOpen) RenderExitPrompt();
        }

        /// <summary>
        /// The visual a selectable row is built from - icon, title, subtitle, card styling. Split out
        /// of <see cref="ExitPromptRow"/> so the tray-apps and quick-tools columns
        /// (CenterMenuWindow.QuickMenu.cs) can share the exact same look without sharing that method's
        /// click-wiring, which is hardwired to the middle column's own index and action list.
        ///
        /// <paramref name="inCard"/> drops its own background and most of its margin, because inside
        /// the power card the CARD is the surface - a second card colour on every row would draw four
        /// boxes inside a box and undo the grouping. <paramref name="dim"/> is the "present but not
        /// reachable right now" row - greyed rather than absent, because a control that silently is
        /// not there reads as a bug and one this project has already paid for once.
        ///
        /// <paramref name="compact"/> is the SIDE-column form (tray apps, quick tools). It is not just
        /// "the same row, smaller": it drops the card fill entirely, so the two side lists read as
        /// lists while the middle column keeps reading as the buttons it is. The middle column holds
        /// the consequential actions - shut down, hibernate, leave the library - and it earns the
        /// visual weight; a shortcut to Task Manager does not. Requested on device, 2026-09-04, after
        /// three identical-looking columns made the middle one stop standing out at all.
        /// </summary>
        // What the middle column of the quick menu is allowed to be wide. It used to be 520 and was
        // reported cut off on the right once the two sidebars flanked it - and the rows in it are an
        // icon plus two short lines, so the width was never doing any work.
        private const double ExitPromptCentreWidth = 430;

        private static Border BuildRowVisual(
            string glyph, string title, string subtitle, bool inCard, bool dim = false, bool compact = false)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(title),
                FontSize = compact ? 13 : 18,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            // NO EMPTY SECOND LINE. A TextBlock with no text still measures one line high, so a row
            // built without a subtitle - the whole Windows tools column - was two lines tall with only
            // the first one drawn. Both halves are centred in the row, so the title sat in the upper
            // half while the icon sat in the middle, and the gear looked a couple of pixels above its
            // own label (user, 2026-09-05). Leaving the element out is what makes the two line up.
            if (!string.IsNullOrEmpty(subtitle))
                text.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(subtitle),
                    FontSize = compact ? 10 : 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, compact ? 1 : 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            Grid.SetColumn(text, 1);

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = compact ? 14 : 20,
                Foreground = UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!compact) icon.Foreground = UiHelpers.Text;

            var grid = new Grid();
            // 48, not 34: the glyph is drawn at 20pt and centred, so a 34-wide column left barely
            // three pixels between it and the text. The gap belongs to the COLUMN rather than to a
            // margin on the text, so the two lines of the label still start at the same x whatever
            // width the glyph happens to have.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 26 : 48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(icon);
            grid.Children.Add(text);

            return new Border
            {
                Child = grid,
                Background = (inCard || compact) ? Brushes.Transparent : UiHelpers.Card,
                CornerRadius = new CornerRadius(inCard || compact ? 7 : 10),
                Padding = compact
                    ? new Thickness(8, 5, 8, 5)
                    : new Thickness(16, inCard ? 9 : 12, 16, inCard ? 9 : 12),
                Margin = new Thickness(0, 0, 0, compact ? 2 : (inCard ? 0 : 10)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = dim ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                Opacity = dim ? 0.45 : 1.0,
                // The card carries the width for its own rows; setting it here too would fight the
                // card's padding and push the rows past its rounded edge. A side column is sized by
                // its own grid column, so it wants no minimum either.
                MinWidth = (inCard || compact) ? 0 : ExitPromptCentreWidth,
            };
        }

        /// <summary>One selectable row in the MIDDLE column's own list/index/click wiring.</summary>
        private Border ExitPromptRow(int index, string glyph, string title, string subtitle, bool inCard = false)
        {
            var row = BuildRowVisual(glyph, title, subtitle, inCard);
            row.Tag = index;
            int captured = index;
            row.MouseLeftButtonUp += (_, __) =>
            {
                _exitPromptColumn = ExitPromptColumnCenter;
                _exitPromptIndex = captured;
                ActivateExitPromptSelection();
            };
            _exitPromptRows.Add(row);
            return row;
        }

        /// <summary>The rows and index for whichever column currently has focus. One switch instead
        /// of three near-identical copies of every method below - see the field block above for why
        /// each column needs its own list and index in the first place.</summary>
        private (List<Border> rows, List<Action> actions, int index) ActiveExitPromptColumn() => _exitPromptColumn switch
        {
            ExitPromptColumnTray => (_exitPromptTrayRows, _exitPromptTrayActions, _exitPromptTrayIndex),
            ExitPromptColumnTools => (_exitPromptToolsRows, _exitPromptToolsActions, _exitPromptToolsIndex),
            _ => (_exitPromptRows, _exitPromptActions, _exitPromptIndex),
        };

        /// <summary>Highlights the selected row in the ACTIVE column only - the other two keep no
        /// highlighted row, so there is never a moment with two accent borders on screen at once.</summary>
        private void ApplyExitPromptSelection()
        {
            var (activeRows, _, activeIndex) = ActiveExitPromptColumn();
            Border selected = null;
            foreach (var rows in new[] { _exitPromptRows, _exitPromptTrayRows, _exitPromptToolsRows })
            {
                bool isActive = rows == activeRows;
                foreach (var row in rows)
                {
                    bool on = isActive && row.Tag is int i && i == activeIndex;
                    row.BorderBrush = on ? UiHelpers.Accent : Brushes.Transparent;
                    if (on) selected = row;
                }
            }

            // 🔴 THE SCROLLER DOES NOT FOLLOW THE PAD ON ITS OWN. Reported 2026-09-04: the side lists
            // could only be scrolled with mouse or touch. Nothing was broken about the navigation -
            // the highlight really did move onto row seven - it just moved BELOW the visible area of a
            // height-capped ScrollViewer, and a highlight nobody can see is indistinguishable from a
            // press that did nothing.
            //
            // A ScrollViewer scrolls itself for KEYBOARD FOCUS. These rows are Borders that are never
            // focused - selection here is our own index plus a border brush - so that mechanism never
            // had anything to react to. BringIntoView is the same request made explicitly.
            //
            // Called from the one place that changes the selection, so every route in (pad, mouse,
            // column switch, a rebuild after the list refreshes) gets it without its own line.
            selected?.BringIntoView();
        }

        /// <summary>Up/Down move within the active column; Left/Right switch column, one at a time, and
        /// refuse to land on a column with nothing in it yet (the tray column while it is still
        /// loading) rather than moving the ring onto emptiness.</summary>
        private void MoveExitPromptSelection(PadButton dir)
        {
            if (dir == PadButton.Left || dir == PadButton.Right)
            {
                int nextColumn = _exitPromptColumn + (dir == PadButton.Right ? 1 : -1);
                if (nextColumn < ExitPromptColumnFirst || nextColumn > ExitPromptColumnLast) return;

                var candidateRows = nextColumn switch
                {
                    ExitPromptColumnTray => _exitPromptTrayRows,
                    ExitPromptColumnTools => _exitPromptToolsRows,
                    _ => _exitPromptRows,
                };
                if (candidateRows.Count == 0) return;

                _exitPromptColumn = nextColumn;
                ApplyExitPromptSelection();
                return;
            }

            var (rows, _, index) = ActiveExitPromptColumn();
            if (rows.Count == 0) return;
            int next = index + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= rows.Count || next == index) return;
            SetActiveColumnIndex(next);
            ApplyExitPromptSelection();
        }

        private void SetActiveColumnIndex(int index)
        {
            switch (_exitPromptColumn)
            {
                case ExitPromptColumnTray: _exitPromptTrayIndex = index; break;
                case ExitPromptColumnTools: _exitPromptToolsIndex = index; break;
                default: _exitPromptIndex = index; break;
            }
        }

        /// <summary>Runs whatever the selected row in the ACTIVE column was built with. The bounds
        /// check is the whole safety net: an index that no longer has a row behind it now does
        /// NOTHING, where the old switch fell through to its default and shut Center down.
        ///
        /// Row 0 of the middle column uses Close(), not Hide(): the Closing handler already knows
        /// what closing means on this machine, and with Run in background off it exits instead - the
        /// honest answer when there is no tray to minimize into.</summary>
        private void ActivateExitPromptSelection()
        {
            var (_, actions, index) = ActiveExitPromptColumn();
            if (index < 0 || index >= actions.Count) return;
            actions[index]();
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

            // The tab strip rides the SAME funnel, deliberately. Its visibility now depends on the
            // launch prompt, and every one of the five places that opens or closes that prompt already
            // calls the action bar - so hanging it here is the difference between one rule and five
            // hand-maintained call sites that a sixth transition would silently miss. That mistake has
            // its own entry in CLAUDE.md (UpdateCpuStateLocks, which had two callers and was wrong
            // everywhere else). RefreshTabStrip calls nothing back, so there is no cycle.
            RefreshTabStrip();

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
                string confirmLabel = _exitPromptColumn == ExitPromptColumnTray ? "Open"
                    : _exitPromptColumn == ExitPromptColumnTools ? "Launch"
                    : "Select";
                AddAction(PadButton.A, confirmLabel, true, ActivateExitPromptSelection);

                // X only makes sense on the tray column, and only while a real row is selected there -
                // the close action list is index-aligned with the open one, built together in
                // CenterMenuWindow.QuickMenu.cs.
                if (_exitPromptColumn == ExitPromptColumnTray
                    && _exitPromptTrayIndex >= 0 && _exitPromptTrayIndex < _exitPromptTrayCloseActions.Count
                    && _exitPromptTrayCloseActions[_exitPromptTrayIndex] != null)
                {
                    int capturedIndex = _exitPromptTrayIndex;
                    AddAction(PadButton.X, "Close", true, () => _exitPromptTrayCloseActions[capturedIndex]());
                }

                AddAction(PadButton.B, "Back to library", true, CloseExitPrompt);
                return;
            }

            if (_optiWikiOpen)
            {
                AddAction(PadButton.A, "Open in browser", true, OpenOptiWikiInBrowser);
                AddAction(PadButton.B, "Back", true, CloseOptiWiki);
                return;
            }

            if (LaunchOverlayOpen)
            {
                switch (_launchPrompt)
                {
                    case LaunchPrompt.Confirm:
                        AddAction(PadButton.A, "Play", true, ConfirmLaunch);
                        AddAction(PadButton.B, "Cancel", true, ClearLaunchOverlay);
                        AddLaunchOptiActions();
                        break;
                    case LaunchPrompt.ConfirmInstall:
                        AddAction(PadButton.A,
                                  _launchTarget != null && _launchTarget.DownloadTotalBytes > 0 ? "Open Steam" : "Install",
                                  true, ConfirmInstallNow);
                        AddAction(PadButton.B, "Cancel", true, ClearLaunchOverlay);
                        // The wiki and OptiClick apply to a game you own, installed or not - deciding
                        // whether a game is worth fetching is exactly when that page is useful.
                        AddLaunchOptiActions();
                        break;
                    case LaunchPrompt.InstallHandedOver:
                        AddAction(PadButton.A, "Rescan", true, RescanFromInstall);
                        AddAction(PadButton.B, "Back", true, ClearLaunchOverlay);
                        break;
                    case LaunchPrompt.Running:
                        // The label says what happens to CENTER, because that is all this does. The
                        // three modes read differently enough that one word would be a lie in two of
                        // them: "Hide" over an app that exits is not hiding.
                        AddAction(PadButton.A, HideLabel(), true, HideAfterLaunch);

                        // B ALWAYS LEAVES THE SCREEN, and this was the one state without it (user,
                        // 2026-09-04). The running screen is not a dead end - the game keeps running
                        // either way - so the only thing its absence achieved was that B, the button
                        // that means "back" on every other screen in Center, did nothing here while
                        // the bar advertised closing or minimizing Center instead.
                        //
                        // With LaunchBehavior.StayOpen A does the same thing, and that is fine: two
                        // buttons agreeing is not a bug, and hiding B in that one mode would make the
                        // way out of this screen depend on a setting.
                        AddAction(PadButton.B, "Back to library", true, ClearLaunchOverlay);
                        AddLaunchOptiActions();
                        break;
                    default:
                        AddAction(PadButton.B, "Back", true, ClearLaunchOverlay);
                        break;
                }
                return;
            }

            if (_tabEditorOpen)
            {
                bool hidden = _tabEditorIndex >= 0 && _tabEditorIndex < _tabEditorOrder.Count
                              && _tabEditorHidden.Contains(_tabEditorOrder[_tabEditorIndex]);
                AddAction(PadButton.A, hidden ? "Show" : "Hide", true, ToggleTabVisibility);
                AddAction(PadButton.B, "Back", true, CloseTabEditor);
                return;
            }

            if (_settingsOpen)
            {
                string label = _settingsIndex == SettingsKeyRow ? "Edit"
                    : _settingsIndex == SettingsTabsRow ? "Open"
                    : _settingsIndex == SettingsLaunchBehaviorRow ? "Cycle"
                    : "Toggle";
                AddAction(PadButton.A, label, true, ActivateSetting);
                AddAction(PadButton.B, "Back", true, SaveArtKeyAndClose);
                return;
            }

            if (RefreshMiscActionBar()) return;
            if (RefreshGameMenuActionBar()) return;

            // "Play" would be a lie in the one tab where nothing can be played.
            bool notInstalled = _libraryGroup == LibraryGroup.NotInstalled;
            AddAction(PadButton.A, notInstalled ? "Install" : "Play", SelectedGame != null, LaunchSelectedGame);
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
