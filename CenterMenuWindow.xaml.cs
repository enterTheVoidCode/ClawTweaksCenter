using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawTweaksSetup.Core;
using ClawTweaksSetup.Core.Sources;
using ClawTweaksSetup.Navigation;
using ClawTweaksSetup.Ui;

namespace ClawTweaksSetup
{
    /// <summary>
    /// Standalone entry menu shown when the exe is run with nothing next to it (no sibling
    /// .msix/.cer — see App.xaml.cs). Lets the user pick a build from GitHub releases, GitHub test
    /// builds, or Google Drive nightlies, downloads/stages it, then triggers and monitors the actual
    /// install (cert trust → Add-AppxPackage → Game Bar → helper) by repointing
    /// <see cref="SetupContext.AssetRoot"/> — this is the fast-iteration path for an already-onboarded
    /// dev device, not the full first-time wizard (that's still <see cref="MainWindow"/>, reached via
    /// a real release folder, unchanged).
    /// </summary>
    public partial class CenterMenuWindow : Window
    {
        private readonly List<BuildSource> _flat = new List<BuildSource>();
        private readonly Dictionary<BuildSource, Border> _rowElements = new Dictionary<BuildSource, Border>();
        private readonly Dictionary<PadButton, Action> _liveActions = new Dictionary<PadButton, Action>();

        private List<BuildSource> _releases;
        private List<BuildSource> _testBuilds;
        private List<BuildSource> _nightlies;
        private string _releasesError;
        private string _testBuildsError;
        private string _nightliesError;

        /// <summary>Which idle screen ContentHost shows — Confirm/Install are transient overlays
        /// triggered from Browse and don't need their own value here.</summary>
        private enum View { Home, Browse, Onboarding, Maintenance }
        private View _view = View.Home;

        private DeviceDetect.Model _deviceModel = DeviceDetect.Model.Unknown;
        private Version _installedVersion;
        private bool _installedVersionChecked;
        private SetupVersionCheck.Result _setupVersionCheck;
        private WindowsChannelDetect.Result _windowsChannel;
        private int _selectedIndex = -1;
        // Controller cursor over the Home tiles (0=Browse, 1=Onboarding, 2=Maintenance). -1 is the
        // Center self-update card ABOVE that row, and only exists while an update is actually offered
        // (see CenterUpdateOffered) — Up/Down move between the two, Left/Right within the tile row.
        private int _homeSelectedIndex = 0;

        // Migration from the pre-0.1.9 machine-wide install (see BuildLegacyInstallCard). Session-only
        // by design — the condition is still true next launch, so the notice should come back.
        private bool _legacyNoticeDismissed;
        private string _legacyRemovalStatus;

        // Set while a hand-off screen is up (missing prerequisites / untrusted certificate): the thing
        // Ⓨ does. Every other screen drives its actions from the controller footer, and these two were
        // the odd ones out with a button buried at the bottom of a scrolling page.
        private Action _recheckAction;
        private string _recheckLabel;

        // Missing-prerequisites hand-off screen: which tools it lists, which build it will resume, and
        // where the controller cursor sits. Non-null only while that screen is up — that is also what
        // tells MoveSelection to navigate the tool cards instead of the build list.
        private List<ToolStatus> _prereqTools;
        private BuildSource _prereqBuild;
        private int _prereqSelectedIndex;
        private FrameworkElement _prereqSelectedCard;

        // The staged build folder from the last download, so a re-check does NOT download the ZIP
        // again. Re-running the whole of InstallSelectedAsync was the simple thing to write and the
        // wrong thing to do: the user fixes one prerequisite, presses re-check, and sits through
        // another download of a file already on disk.
        private string _stagedRoot;
        private BuildSource _stagedBuild;
        private int _onbSelectedIndex = 0;      // controller cursor over the onboarding step cards
        private FrameworkElement _onbSelectedCard; // for BringIntoView after a selection move
        private bool _busy;
        private bool _confirming;
        private bool _buildBlocked;     // confirm screen is showing "can't install this" — no A action
        private bool _installFinished;
        private BuildSource _pendingBuild;
        private XInputNavigator _nav;

        // ONE shared helper connection for the whole Center. The helper's ClawTweaksCenter pipe accepts a
        // single server instance (NamedPipeServer maxNumberOfServerInstances=1), so onboarding and
        // maintenance MUST reuse the same client — two keep-alive clients starved each other (one grabbed
        // the slot and its auto-reconnect held it, timing the other out on every action).
        private readonly Core.HelperPipeClient _helperPipe;
        private readonly OnboardingRunner _onboarding;
        private readonly bool _startOnboardingOnLoad;

        // Gone with the elevation gate: a "--resume-install=<temp json>" argument used to carry the
        // build the user had picked across the elevated relaunch, so the install could pick up where
        // the UAC prompt interrupted it. Nothing in the install relaunches any more, so there is
        // nothing to resume — the user simply stays in the same process the whole way through.

        public CenterMenuWindow(bool startOnboarding = false)
        {
            _startOnboardingOnLoad = startOnboarding;

            // Build the shared pipe client and both runners BEFORE anything uses them (field initializers
            // across partial files have no guaranteed cross-file order, so wire them here explicitly).
            _helperPipe = new Core.HelperPipeClient();
            _onboarding = new OnboardingRunner(_helperPipe);
            _maintenance = new MaintenanceRunner(_helperPipe);

            InitializeComponent();

            _onboarding.StepsChanged += () => Dispatcher.Invoke(() =>
            {
                if (_view == View.Onboarding && !_confirming && !_busy) RenderOnboarding();
            });

            // Fill the screen without covering the taskbar. WindowStyle="None" + WindowState="Maximized"
            // is the common trap here — without window chrome, WPF maximizes to the full monitor bounds
            // instead of the work area, which hides the taskbar entirely. Sizing manually to the work
            // area gets the borderless look while leaving the taskbar visible. Read the work area in
            // SourceInitialized, not the constructor — SystemParameters.WorkArea can still report the
            // full monitor bounds before the window has an actual display/HWND association, only
            // settling to the real (taskbar-excluded) value once one exists.
            SourceInitialized += (_, __) =>
            {
                Left = SystemParameters.WorkArea.Left;
                Top = SystemParameters.WorkArea.Top;
                Width = SystemParameters.WorkArea.Width;
                Height = SystemParameters.WorkArea.Height;
            };

            SetupVersionLabel.Text = "CTW Center v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?");
            RenderDeviceBanner(null);
            RenderHome();
            RefreshActionBar();

            Loaded += async (_, __) =>
            {
                _nav = new XInputNavigator(this);
                _nav.ButtonPressed += b => Dispatcher.Invoke(() => Invoke(b));
                _nav.RightStickScrollRequested += d => Dispatcher.Invoke(() =>
                {
                    // Defensive: this fires at up to ~25Hz straight off a live gamepad reading, so any
                    // transient WPF layout hiccup here must never take the whole app down with it.
                    try { ContentScroller.ScrollToVerticalOffset(ContentScroller.VerticalOffset + d); }
                    catch { }
                });
                _nav.Start();

                var deviceTask = Task.Run(() => DeviceDetect.Detect());
                var sourcesTask = RefreshSourcesAsync();
                var setupVersionTask = SetupVersionCheck.CheckAsync();
                var windowsChannelTask = Task.Run(() => WindowsChannelDetect.Detect());
                RenderDeviceBanner(await deviceTask);
                await sourcesTask;

                _setupVersionCheck = await setupVersionTask;
                _windowsChannel = await windowsChannelTask;
                RenderCurrentView(); // picks up the outdated-Setup / Insider-channel warnings once known

                // Reached via MainWindow after a successful install/update (release-folder wizard
                // path) — the helper is already confirmed running there, so open onboarding right
                // away instead of waiting for the user to find the tile.
                if (_startOnboardingOnLoad) OpenOnboarding();
            };
            Closed += (_, __) =>
            {
                _nav?.Dispose();
                try { _helperPipe?.Dispose(); } catch { } // single shared client for both runners
            };

            // Keyboard fallbacks for desk testing.
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Invoke(PadButton.B); e.Handled = true; }
                else if (e.Key == Key.Enter) { Invoke(PadButton.A); e.Handled = true; }
                else if (e.Key == Key.Tab) { Invoke(PadButton.X); e.Handled = true; }
                else if (e.Key == Key.F5) { Invoke(PadButton.Y); e.Handled = true; }
                else if (e.Key == Key.Up) { Invoke(PadButton.Up); e.Handled = true; }
                else if (e.Key == Key.Down) { Invoke(PadButton.Down); e.Handled = true; }
                else if (e.Key == Key.Left) { Invoke(PadButton.Left); e.Handled = true; }
                else if (e.Key == Key.Right) { Invoke(PadButton.Right); e.Handled = true; }
            };
        }

        private void Invoke(PadButton b)
        {
            if (_liveActions.TryGetValue(b, out var action)) { action(); return; }
            if (b == PadButton.Up || b == PadButton.Down || b == PadButton.Left || b == PadButton.Right)
                MoveSelection(b);
        }

        #region Device banner
        private void RenderDeviceBanner(DeviceDetect.Result? device)
        {
            if (device == null)
            {
                DeviceBanner.Content = UiHelpers.StatusRow(StatusKind.Working, "Detecting device…", "");
                return;
            }

            var d = device.Value;
            _deviceModel = d.Model;
            RenderCurrentView(); // the build list's per-device gating tags depend on this

            var kind = d.Supported ? StatusKind.Ok : StatusKind.Warning;
            string detail = d.Supported ? "Supported." : "Not a recognized MSI Claw — installing here is untested.";

            var icon = DeviceIcons.For(d.Model);
            if (icon == null)
            {
                DeviceBanner.Content = UiHelpers.StatusRow(kind, d.DisplayName, detail);
                return;
            }

            var image = new Image
            {
                Source = icon, Height = 72, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = d.DisplayName, FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });
            textStack.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 15, Foreground = UiHelpers.BrushFor(kind), Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            // Grid, not a horizontal StackPanel: same wrap-defeating pitfall as the log/status rows —
            // the "not a recognized MSI Claw" detail line is long enough to need real wrapping.
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(image, 0);
            Grid.SetColumn(textStack, 1);
            content.Children.Add(image);
            content.Children.Add(textStack);

            DeviceBanner.Content = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 18, 10),
                Child = content,
            };
        }
        #endregion

        #region Source fetching
        private async Task RefreshSourcesAsync()
        {
            if (_busy) return;
            _busy = true;
            RefreshActionBar();

            _releasesError = _testBuildsError = _nightliesError = null;
            _releases = _testBuilds = _nightlies = null;
            RebuildFlat();
            RenderCurrentView();

            var versionTask = Task.Run(() => PackageInstaller.GetInstalledVersion());
            var ghTask = FetchGitHubAsync();
            var driveTask = FetchDriveAsync();
            await Task.WhenAll(versionTask, ghTask, driveTask);

            _installedVersion = versionTask.Result;
            _installedVersionChecked = true;
            RenderCurrentView(); // installed version is now known — Home's update banner + Browse's tags show up

            _busy = false;
            RefreshActionBar();
        }

        private async Task FetchGitHubAsync()
        {
            try
            {
                var (releases, testBuilds) = await GitHubReleaseSource.FetchAsync();
                _releases = releases;
                _testBuilds = testBuilds;
            }
            catch (Exception ex)
            {
                _releasesError = _testBuildsError = ex.Message;
            }
            RebuildFlat();
            RenderCurrentView();
        }

        private async Task FetchDriveAsync()
        {
            try { _nightlies = await GoogleDriveSource.FetchAsync(); }
            catch (Exception ex) { _nightliesError = ex.Message; }
            RebuildFlat();
            RenderCurrentView();
        }

        /// <summary>Re-renders whichever idle screen is currently showing — used by the background
        /// fetches so Home's update banner and Browse's list both stay live as data arrives. Skipped
        /// while the Confirm screen is up so a background refresh can't clobber it (Install has its
        /// own separate ContentHost takeover and never runs concurrently with a source refresh).</summary>
        private void RenderCurrentView()
        {
            if (_confirming) return;
            switch (_view)
            {
                case View.Home: RenderHome(); break;
                case View.Onboarding: RenderOnboarding(); break;
                case View.Maintenance: RenderMaintenance(); break;
                default: RenderBrowse(); break;
            }
        }

        private void RebuildFlat()
        {
            _flat.Clear();
            if (_releases != null) _flat.AddRange(_releases);
            if (_testBuilds != null) _flat.AddRange(_testBuilds);
            if (_nightlies != null) _flat.AddRange(_nightlies);

            if (_selectedIndex >= _flat.Count) _selectedIndex = _flat.Count - 1;
            if (_selectedIndex < 0 && _flat.Count > 0) _selectedIndex = 0;
        }
        #endregion

        #region Home
        private void GoHome()
        {
            _view = View.Home;
            RenderHome();
            RefreshActionBar();
        }

        private void OpenBrowse()
        {
            _view = View.Browse;
            RenderBrowse();
            RefreshActionBar();
        }

        /// <summary>A on the Home screen: opens whichever of the 3 tiles the controller cursor is on,
        /// or the Center download page when the cursor is on the update notice above them.</summary>
        private void ActivateHomeTile()
        {
            switch (_homeSelectedIndex)
            {
                case -1: OpenCenterDownloadPage(); break;
                case 0: OpenBrowse(); break;
                case 1: OpenOnboarding(); break;
                case 2: OpenMaintenance(); break;
            }
        }

        /// <summary>True when the manifest advertises a newer Center than the one running. See
        /// SetupVersionCheck.IsUpdateOffered.</summary>
        private bool CenterUpdateOffered => _setupVersionCheck?.IsUpdateOffered == true;

        private void RenderHome()
        {
            ContentHost.Children.Clear();

            // Center's OWN update, above everything else — it's the one thing on this screen that
            // changes the app the user is looking at. Distinct from the "Update available on GitHub"
            // line further down, which is about the installed ClawTweaks app.
            if (CenterUpdateOffered)
                ContentHost.Children.Add(BuildCenterUpdateCard());

            if (_setupVersionCheck?.Outdated == true)
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "This Setup build is outdated",
                    $"{_setupVersionCheck.Message} (running {_setupVersionCheck.RunningVersion}, needs {_setupVersionCheck.MinimumVersion}+)"));

            if (_windowsChannel?.IsInsider == true)
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Windows Insider Preview detected",
                    $"You're on the \"{_windowsChannel.ChannelName}\" channel — the install routine is currently known not to work correctly on Insider builds."));

            if (SelfInstaller.LegacyInstallPresent() && !_legacyNoticeDismissed)
                ContentHost.Children.Add(BuildLegacyInstallCard());

            var versionStack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            if (!_installedVersionChecked)
            {
                // Checking PackageInstaller.GetInstalledVersion() takes a moment (PowerShell
                // Get-AppxPackage) — showing the "not installed" text as a placeholder during that
                // window was actively misleading on machines that DO have ClawTweaks installed. Show
                // a spinner instead of any default text until the real state is known.
                var checkingRow = new StackPanel { Orientation = Orientation.Horizontal };
                checkingRow.Children.Add(new ContentControl
                {
                    Width = 22, Height = 22, Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = UiHelpers.Badge(StatusKind.Working, 22),
                });
                checkingRow.Children.Add(new TextBlock
                {
                    Text = "Checking installed ClawTweaks version…", FontSize = 18,
                    FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                });
                versionStack.Children.Add(checkingRow);
            }
            else
            {
                versionStack.Children.Add(new Border
                {
                    BorderBrush = UiHelpers.Ok,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 14, 10),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        // Called out as "ClawTweaks" specifically — this is the main app's version, not
                        // the Setup/Center tool's own (shown separately under the header logo).
                        Text = _installedVersion != null
                            ? $"Currently installed: ClawTweaks {_installedVersion}"
                            : "ClawTweaks is not installed yet.",
                        FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Ok,
                    },
                });
            }
            var update = FindNewestGithubUpdate();
            if (update != null)
                versionStack.Children.Add(new TextBlock
                {
                    Text = $"▲ Update available on GitHub: {update.Version} ({update.Origin})",
                    FontSize = 15, Foreground = UiHelpers.Ok, Margin = new Thickness(0, 4, 0, 0),
                });
            ContentHost.Children.Add(versionStack);

            var mainRow = new Grid();
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // -1 is only a valid cursor position while the update card is actually on screen; if the
            // offer disappears (manifest re-fetch, update installed) the cursor falls back to the row.
            int minIndex = CenterUpdateOffered ? -1 : 0;
            if (_homeSelectedIndex < minIndex) _homeSelectedIndex = minIndex;
            if (_homeSelectedIndex > 2) _homeSelectedIndex = 2;

            var releaseTile = BuildHomeTile(
                "Update & Release", "Browse GitHub releases, test builds, and Drive nightlies to install.",
                clickable: true, onClick: () => { _homeSelectedIndex = 0; OpenBrowse(); }, selected: _homeSelectedIndex == 0);
            Grid.SetColumn(releaseTile, 0);
            releaseTile.Margin = new Thickness(0, 0, 7, 10);
            mainRow.Children.Add(releaseTile);

            var onboardingTile = BuildHomeTile(
                "Onboarding", "Center M, virtual controller, Game Bar auto-jump.",
                clickable: true, onClick: () => { _homeSelectedIndex = 1; OpenOnboarding(); }, selected: _homeSelectedIndex == 1);
            Grid.SetColumn(onboardingTile, 1);
            onboardingTile.Margin = new Thickness(7, 0, 7, 10);
            mainRow.Children.Add(onboardingTile);

            // Third column (right of Onboarding): the Reset / Backup / Restore maintenance tools.
            var maintenanceTile = BuildHomeTile(
                "Reset · Backup · Restore", "Full factory reset, or back up and restore all your profiles.",
                clickable: true, onClick: () => { _homeSelectedIndex = 2; OpenMaintenance(); }, selected: _homeSelectedIndex == 2);
            Grid.SetColumn(maintenanceTile, 2);
            maintenanceTile.Margin = new Thickness(7, 0, 0, 10);
            mainRow.Children.Add(maintenanceTile);

            ContentHost.Children.Add(mainRow);

            var placeholders = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
            placeholders.Children.Add(BuildHomeTile("FAQ", "Common questions and troubleshooting.", clickable: false));
            placeholders.Children.Add(BuildHomeTile("Controller Diagnostics", "Run the controller/helper health checks on demand.", clickable: false));
            placeholders.Children.Add(BuildHomeTile("ClawTweaks News", "Announcements from the project.", clickable: false));
            ContentHost.Children.Add(placeholders);
        }

        /// <summary>Highest GitHub release/test-build version above what's currently installed, or
        /// null. Drive nightlies aren't considered — the ask was specifically "available on GitHub".</summary>
        private BuildSource FindNewestGithubUpdate()
        {
            if (_installedVersion == null) return null;

            BuildSource best = null; Version bestVer = null;
            foreach (var b in (_releases ?? Enumerable.Empty<BuildSource>()).Concat(_testBuilds ?? Enumerable.Empty<BuildSource>()))
            {
                if (!TryParseVersion(b.Version, out var v) || v <= _installedVersion) continue;
                // Never advertise a build the picker will refuse: Home's banner is a call to action,
                // and pointing it at a greyed-out tile is a dead end the user can't do anything about.
                if (IsBlocked(b, out _)) continue;
                if (bestVer == null || v > bestVer) { bestVer = v; best = b; }
            }
            return best;
        }

        /// <summary>Switches to the dedicated Onboarding view and asks the helper for a fresh status
        /// snapshot (queries — never just assumes a step still needs doing). Safe to call repeatedly.</summary>
        private void OpenOnboarding()
        {
            _view = View.Onboarding;
            _onbSelectedIndex = 0;
            RenderOnboarding();
            RefreshActionBar();
            _ = _onboarding.RefreshStatusAsync(msg => Dispatcher.Invoke(RenderOnboarding));
        }

        /// <summary>The dedicated Onboarding view — each step is queried from the helper and triggered
        /// individually by the user. A step whose target the helper reports as already satisfied (e.g.
        /// Center M already off, from some other change entirely) shows done with its run button
        /// greyed out, instead of blindly offering to redo something that's already correct.</summary>
        private void RenderOnboarding()
        {
            ContentHost.Children.Clear();
            ContentHost.Children.Add(UiHelpers.Title("Onboarding"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Helps set the most important ClawTweaks settings."));

            // Shown unconditionally: HidHide and usbip install kernel drivers that Windows only loads on
            // the next boot (the two tools flagged NeedsReboot in PrerequisiteGuide). Without the restart
            // the steps below can fail for a reason that looks nothing like "reboot pending", so the note
            // is cheaper than the support round-trip.
            ContentHost.Children.Add(new Border
            {
                Background = Tint(UiHelpers.Warn, 0x22),
                BorderBrush = Tint(UiHelpers.Warn, 0x99),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = "⚠  If HidHide or usbip was just installed, restart the device before running these steps.",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Warn,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

            if (_onboarding.IsConnecting)
            {
                var connectingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
                connectingRow.Children.Add(new ContentControl
                {
                    Width = 22, Height = 22, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
                    Content = UiHelpers.Badge(StatusKind.Working, 22),
                });
                connectingRow.Children.Add(new TextBlock
                {
                    Text = "Connecting to the helper…", FontSize = 15, Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                });
                ContentHost.Children.Add(connectingRow);
            }

            if (_onbSelectedIndex < 0) _onbSelectedIndex = 0;
            if (_onbSelectedIndex >= _onboarding.Steps.Count) _onbSelectedIndex = _onboarding.Steps.Count - 1;
            _onbSelectedCard = null;

            // Two-column grid of numbered step cards; the controller cursor highlights one with an accent
            // outline (D-pad moves it, A runs it — see MoveSelection / RefreshActionBar).
            var onbGrid = new UniformGrid { Columns = 2 };
            ContentHost.Children.Add(onbGrid);

            for (int i = 0; i < _onboarding.Steps.Count; i++)
            {
                int index = i; // capture
                var step = _onboarding.Steps[i];
                bool selectedCard = index == _onbSelectedIndex;
                bool working = step.State == OnboardingStepState.Working;

                // A step reads as "done" (green check) ONLY when its state is Ok. A non-actionable step is
                // NOT necessarily done — in the dependency chain it is usually just GATED, waiting for an
                // earlier step (e.g. "Disable MSI Center M first."), and must show the neutral circle, not
                // a check. The runner sets Ok explicitly for genuinely-satisfied steps, so state is the
                // single source of truth. Gated on being connected & not mid-connect.
                bool doneNoAction = !working && !_onboarding.IsConnecting
                    && step.State == OnboardingStepState.Ok;
                string glyph = step.State == OnboardingStepState.Error ? "✕" : (doneNoAction ? "✓" : "○");
                Brush glyphBrush = step.State == OnboardingStepState.Error ? UiHelpers.Error
                    : (doneNoAction ? UiHelpers.Ok : UiHelpers.Subtle);

                var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                FrameworkElement statusEl = working
                    ? UiHelpers.Badge(StatusKind.Working, 20)
                    : new TextBlock
                    {
                        Text = glyph, FontSize = 18, FontWeight = FontWeights.Bold,
                        Foreground = glyphBrush,
                    };
                statusEl.Width = 26;
                statusEl.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(statusEl, 0);
                row.Children.Add(statusEl);

                var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
                textStack.Children.Add(new TextBlock { Text = $"{index + 1}. {step.Title}", FontSize = 16, Foreground = UiHelpers.Text, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrEmpty(step.Detail))
                    textStack.Children.Add(new TextBlock { Text = step.Detail, FontSize = 13, Foreground = UiHelpers.Subtle });
                Grid.SetColumn(textStack, 1);
                row.Children.Add(textStack);

                bool enabled = step.Actionable && !working && !_onboarding.IsConnecting;
                var runBtn = new Button
                {
                    Content = working ? "Working…" : (index == OnboardingRunner.StepAddToBar ? "Check" : "Run"),
                    Style = (Style)Application.Current.Resources["SetupButton"],
                    IsEnabled = enabled,
                    Opacity = enabled ? 1.0 : 0.4,
                    MinWidth = 90,
                };
                runBtn.Click += (_, __) => _ = _onboarding.RunStepAsync(index, msg => Dispatcher.Invoke(RenderOnboarding));

                // The auto-jump step lets the user enter the slot ClawTweaks sits at (it can't be read).
                // A small −/N/+ stepper keeps it controller-navigable; Run then sends the number.
                var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (index == OnboardingRunner.StepAutoJump)
                {
                    bool stepEnabled = step.Actionable && !working && !_onboarding.IsConnecting;
                    Button StepBtn(string glyphTxt) => new Button
                    {
                        Content = glyphTxt,
                        Style = (Style)Application.Current.Resources["SetupButton"],
                        IsEnabled = stepEnabled, Opacity = stepEnabled ? 1.0 : 0.4,
                        MinWidth = 40, Margin = new Thickness(0, 0, 4, 0),
                    };
                    var minus = StepBtn("−");
                    var plus = StepBtn("+");
                    var posText = new TextBlock
                    {
                        Text = _onboarding.AutoJumpPositionValue.ToString(),
                        FontSize = 16, Foreground = UiHelpers.Text, MinWidth = 24,
                        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    minus.Click += (_, __) => { if (_onboarding.AutoJumpPositionValue > 1) { _onboarding.AutoJumpPositionValue--; RenderOnboarding(); } };
                    plus.Click += (_, __) => { if (_onboarding.AutoJumpPositionValue < 10) { _onboarding.AutoJumpPositionValue++; RenderOnboarding(); } };
                    rightPanel.Children.Add(minus);
                    rightPanel.Children.Add(posText);
                    rightPanel.Children.Add(plus);
                }
                rightPanel.Children.Add(runBtn);
                Grid.SetColumn(rightPanel, 2);
                row.Children.Add(rightPanel);

                var onbPad = new Thickness(16, 12, 16, 12);
                var card = new Border
                {
                    Background = UiHelpers.Card, CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 10, 10),
                    BorderBrush = selectedCard ? UiHelpers.Accent : Brushes.Transparent,
                    BorderThickness = new Thickness(selectedCard ? 2 : 0),
                    Padding = selectedCard ? Deflate(onbPad, 2) : onbPad, // see Deflate: keeps the card from resizing
                    Child = row,
                };
                if (selectedCard) _onbSelectedCard = card;
                onbGrid.Children.Add(card);
            }

            _onbSelectedCard?.BringIntoView();
            RefreshActionBar(); // keep the A="Run" footer action in sync with the selected step
        }

        private Border BuildHomeTile(string title, string detail, bool clickable, Action onClick = null, bool selected = false)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
            });
            stack.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 14, Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            if (!clickable)
                stack.Children.Add(new TextBlock
                {
                    Text = "Coming soon", FontSize = 12, FontWeight = FontWeights.Bold,
                    Foreground = UiHelpers.Accent, Margin = new Thickness(0, 10, 0, 0),
                });

            // Focus model: the controller cursor highlights ONE clickable tile with a thick accent
            // outline; the other clickable tiles show only a thin subtle border so the selected one is
            // unmistakable. Non-clickable ("coming soon") tiles never get the accent.
            Brush borderBrush = selected ? UiHelpers.Accent : (clickable ? UiHelpers.Subtle : Brushes.Transparent);
            double borderThickness = selected ? 3 : (clickable ? 1 : 0);

            // Three different ring thicknesses here, so the padding is derived from the thickness rather
            // than written out per state: border + padding stays constant, and the tile therefore
            // measures the same in all three. Offset by 1 because the ordinary clickable tile (1px) is
            // the state the padding below was authored against. See Deflate.
            var tilePad = new Thickness(20, 18, 20, 18);

            var border = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(borderThickness),
                Padding = Deflate(tilePad, borderThickness - 1),
                Opacity = clickable ? 1.0 : 0.55,
                Child = stack,
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
            };
            if (clickable) border.MouseLeftButtonUp += (_, __) => onClick?.Invoke();
            return border;
        }

        /// <summary>
        /// The Center update NOTICE on Home. It tells the user a newer build exists and opens the page
        /// — it does not download anything and it does not install anything.
        ///
        /// This used to be a full self-updater (download → SHA-256 → launch the installer). It is gone
        /// on purpose: an unsigned app fetching an executable and running it is the dropper shape, and
        /// verifying the bytes afterwards does not change how that behaviour scores. Updating Center is
        /// now the same two steps as installing it the first time — download it, run it — which is also
        /// the one flow that actually gets tested on every release. See SetupVersionCheck.
        /// </summary>
        private Border BuildCenterUpdateCard()
        {
            var check = _setupVersionCheck;
            bool selected = _homeSelectedIndex == -1;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"ClawTweaks Center update available: {check.LatestVersion}",
                FontSize = 19, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"You're running {check.RunningVersion}. Download the new Setup file and run it — " +
                       "it installs over this one, and no administrator rights are needed.",
                FontSize = 14, Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

            var button = new Button
            {
                Content = "Open download page",
                Style = (Style)Application.Current.Resources["SetupButton"],
                IsEnabled = !_busy,
                Opacity = !_busy ? 1.0 : 0.4,
                MinWidth = 190,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
            };
            button.Click += (_, __) => OpenCenterDownloadPage();
            stack.Children.Add(button);

            var updatePad = new Thickness(20, 18, 20, 18);
            return new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 0, 14),
                BorderBrush = selected ? UiHelpers.Accent : UiHelpers.Ok,
                BorderThickness = new Thickness(selected ? 3 : 1),
                Padding = selected ? Deflate(updatePad, 2) : updatePad, // see Deflate: keeps the card from resizing
                Child = stack,
            };
        }

        /// <summary>
        /// The migration card for users coming from a machine-wide install. Center up to 0.1.8.x lived
        /// in Program Files with an HKLM entry and a machine-wide Start Menu shortcut; installing this
        /// version does NOT replace it, because per-user and machine-wide are separate installs. Left
        /// alone the user ends up with two "ClawTweaks Center" entries in Settings → Apps and two
        /// identically-named Start Menu shortcuts, one of which launches a stale copy that will keep
        /// offering to update itself.
        ///
        /// The button hands the job to the OLD install's own uninstaller, which elevates itself — so
        /// the UAC prompt says "ClawTweaks Center" and is about removing it, and Center still asks for
        /// nothing. See SelfInstaller.RemoveLegacyInstall.
        /// </summary>
        private Border BuildLegacyInstallCard()
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "An older ClawTweaks Center is still installed",
                FontSize = 19, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
            });

            var legacyVersion = SelfInstaller.GetLegacyInstalledVersion();
            stack.Children.Add(new TextBlock
            {
                Text = (legacyVersion != null ? $"Version {legacyVersion} is " : "A previous version is ") +
                       $"still installed for all users, in {SelfInstaller.LegacyInstallDir}. This version " +
                       "installs into your own user folder instead, so the old one is no longer used — but " +
                       "it stays in Settings → Apps and in the Start Menu until it's removed, where it's " +
                       "easy to launch by mistake.",
                FontSize = 14, Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Removing it needs administrator rights. ClawTweaks Center never asks for those — " +
                       "the button below starts the old version's own uninstaller, so the prompt you see " +
                       "comes from it, about removing itself.",
                FontSize = 14, Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

            if (!string.IsNullOrEmpty(_legacyRemovalStatus))
                stack.Children.Add(new TextBlock
                {
                    Text = _legacyRemovalStatus,
                    FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Warn,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var remove = new Button
            {
                Content = "Remove the old version",
                Style = (Style)Application.Current.Resources["SetupButton"],
                MinWidth = 210,
            };
            remove.Click += (_, __) =>
            {
                if (SelfInstaller.RemoveLegacyInstall(m => _legacyRemovalStatus = m))
                    _legacyRemovalStatus = "The old version's uninstaller is running — confirm its prompt. " +
                                           "This notice disappears once it's gone (press Ⓨ to refresh).";
                RenderHome();
            };
            buttons.Children.Add(remove);

            // Not everyone can produce an administrator password on the machine they game on, and this
            // is not urgent enough to nag through the whole session. Dismissal is deliberately NOT
            // persisted: the old install is still there next launch, and silently forgetting about it
            // forever is how a user ends up puzzled by two identical Start Menu entries months later.
            var later = new Button
            {
                Content = "Not now",
                Style = (Style)Application.Current.Resources["SetupButton"],
                MinWidth = 110,
                Margin = new Thickness(10, 0, 0, 0),
            };
            later.Click += (_, __) => { _legacyNoticeDismissed = true; RenderHome(); };
            buttons.Children.Add(later);
            stack.Children.Add(buttons);

            return new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20, 18, 20, 18),
                Margin = new Thickness(0, 0, 0, 14),
                BorderBrush = UiHelpers.Warn,
                BorderThickness = new Thickness(1),
                Child = stack,
            };
        }

        /// <summary>Opens the release page in the user's browser. Center stays open — the user is going
        /// to come back to it after downloading, and closing out from under them would lose whatever
        /// they were in the middle of.</summary>
        private void OpenCenterDownloadPage()
        {
            string url = _setupVersionCheck?.LatestPageUrl;
            if (!string.IsNullOrEmpty(url)) PrerequisiteGuide.OpenPage(url);
        }
        #endregion

        #region Build list rendering + grid navigation
        private void RenderBrowse()
        {
            ContentHost.Children.Clear();
            _rowElements.Clear();
            AddSection("Stable Releases", "✅", UiHelpers.Ok, _releases, _releasesError);
            AddSection("Test releases", "⚠️", UiHelpers.Warn, _testBuilds, _testBuildsError);
            AddSection("Nightly Releases (Experimental Builds)", "🥼", UiHelpers.Error, _nightlies, _nightliesError);
        }

        /// <summary>
        /// One channel (stable / test / nightly) as its own framed card, so the three read as separate
        /// groups instead of one long column where only a heading tells them apart. The frame is a
        /// washed-out version of the channel's own colour rather than CardBrush: the release tiles
        /// inside already use CardBrush, and a container in the same tone would make them disappear
        /// into it. Tinted frame outside, solid tiles inside.
        /// </summary>
        private void AddSection(string header, string iconEmoji, Brush titleColor, List<BuildSource> items, string error)
        {
            var sectionStack = new StackPanel();

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            headerRow.Children.Add(new TextBlock
            {
                // WPF's text renderer doesn't support color-emoji font layers (unlike UWP/WinUI or a
                // browser) — these render as plain monochrome outlines. Foreground defaults to black
                // when unset, which is invisible against the dark theme; white reads fine instead.
                Text = iconEmoji,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 20,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = titleColor,
                VerticalAlignment = VerticalAlignment.Center,
            });
            sectionStack.Children.Add(headerRow);

            if (error != null)
            {
                sectionStack.Children.Add(UiHelpers.StatusRow(StatusKind.Error, "Couldn't load", error));
            }
            else if (items == null)
            {
                sectionStack.Children.Add(UiHelpers.StatusRow(StatusKind.Working, "Loading…", ""));
            }
            else if (items.Count == 0)
            {
                sectionStack.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "Nothing found", ""));
            }
            else
            {
                bool haveSelection = _selectedIndex >= 0 && _selectedIndex < _flat.Count;
                var selected = haveSelection ? _flat[_selectedIndex] : null;

                var grid = new UniformGrid { Columns = 2 };
                foreach (var b in items)
                {
                    // items are already sorted newest-first (GitHubReleaseSource/GoogleDriveSource), so
                    // only the first card per section gets full contrast — the rest are dimmed.
                    bool isNewest = ReferenceEquals(b, items[0]);
                    var row = BuildRow(b, ReferenceEquals(b, selected), isNewest);
                    _rowElements[b] = row;
                    grid.Children.Add(row);
                }
                sectionStack.Children.Add(grid);
            }

            ContentHost.Children.Add(new Border
            {
                Background = Tint(titleColor, 0x10),
                BorderBrush = Tint(titleColor, 0x66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14, 8, 6),
                Margin = new Thickness(0, 0, 0, 14),
                Child = sectionStack,
            });
        }

        /// <summary>
        /// Semi-transparent variant of a theme brush, for section frames and badge fills. Alpha rather
        /// than a second set of hard-coded colours, so these follow the theme brushes automatically and
        /// there is nothing to keep in sync. Opacity on the Border itself is not an option — it would
        /// fade the tiles and text inside with it.
        /// </summary>
        private static Brush Tint(Brush source, byte alpha)
        {
            if (source is SolidColorBrush scb)
            {
                var c = scb.Color;
                return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            }
            return Brushes.Transparent;
        }

        /// <summary>
        /// Shrinks a card's padding by exactly the extra border a focus ring adds, so the card measures
        /// the SAME whether it is selected or not.
        ///
        /// BorderThickness is part of an element's measured size, so a selection ring that is 2–3px
        /// thicker than the unselected state made every card grow the moment the cursor landed on it —
        /// which re-flowed the panel around it and read as the whole layout flickering on each D-pad
        /// press. Compensating inwards keeps border + padding (and therefore the outer box) constant, so
        /// nothing moves, while the ring itself stays exactly as thick as it was designed to be.
        /// </summary>
        private static Thickness Deflate(Thickness padding, double by) => new Thickness(
            Math.Max(0, padding.Left - by), Math.Max(0, padding.Top - by),
            Math.Max(0, padding.Right - by), Math.Max(0, padding.Bottom - by));

        /// <summary>
        /// Outlined pill for a short status fact on a release tile ("Currently installed", "Older than
        /// installed", a device block). Carries the same colour the plain text used to, just with a
        /// border and fill so it reads as a label at a glance instead of another line of prose.
        /// </summary>
        private static Border BuildTagBadge(string text, Brush brush) => new Border
        {
            Background = Tint(brush, 0x22),
            BorderBrush = Tint(brush, 0x99),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(0, 6, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        private Border BuildRow(BuildSource b, bool selected, bool isNewest)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{b.Version}  —  {b.Title}",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            string detail = b.When != default ? b.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            if (!string.IsNullOrEmpty(b.SizeLabel)) detail += (detail.Length > 0 ? "  ·  " : "") + b.SizeLabel;
            if (!string.IsNullOrEmpty(detail))
                stack.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 14, Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 3, 0, 0),
                });

            // Both facts are short labels, so they go in a WrapPanel of outlined badges — a build that
            // is both older AND device-blocked shows two side by side and wraps if the tile is narrow.
            var badges = new WrapPanel { Orientation = Orientation.Horizontal };

            string tag = VersionTag(b, out var tagBrush);
            if (tag != null) badges.Children.Add(BuildTagBadge(tag, tagBrush));

            bool blocked = IsBlocked(b, out string blockReason);
            if (blocked) badges.Children.Add(BuildTagBadge("⛔ " + blockReason, UiHelpers.Error));

            if (badges.Children.Count > 0) stack.Children.Add(badges);

            // Only the newest card per section reads at full contrast; older ones are dimmed so the
            // latest is the obvious pick at a glance. A selected (controller-highlighted) card always
            // shows at full strength regardless, so the highlight itself is never hard to see.
            double baseOpacity = (isNewest || selected) ? 1.0 : 0.55;

            // A blocked build is greyed out rather than hidden. Hiding it looks tidier right up until
            // the blocked one IS the current stable — then the Stable Releases section reads "Nothing
            // found" and Center looks broken, with nothing on screen to say why. Dimmed-with-a-reason
            // answers the question before it gets asked. The ⛔ badge above carries the reason at full
            // contrast, so the dimming never costs legibility of the important part.
            if (blocked && !selected) baseOpacity = Math.Min(baseOpacity, 0.4);

            var rowPad = new Thickness(16, 12, 16, 12);
            var border = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0), // full outline, matching the Home tile's focus style
                Padding = selected ? Deflate(rowPad, 2) : rowPad,  // see Deflate: keeps the tile from resizing
                Child = stack,
                Cursor = Cursors.Hand,
                Opacity = _busy ? baseOpacity * 0.5 : baseOpacity,
            };
            border.MouseLeftButtonUp += (_, __) =>
            {
                if (_busy) return;
                _selectedIndex = _flat.IndexOf(b);
                ShowConfirm(b);
            };
            return border;
        }

        /// <summary>Compares a listed build against the currently installed version. Null (no tag)
        /// if nothing's installed yet or the version string doesn't parse.</summary>
        private string VersionTag(BuildSource b, out Brush tagBrush)
        {
            tagBrush = UiHelpers.Subtle;
            if (_installedVersion == null || !TryParseVersion(b.Version, out var v)) return null;

            if (v > _installedVersion) { tagBrush = UiHelpers.Ok; return "▲ Newer than installed"; }
            if (v < _installedVersion) { tagBrush = UiHelpers.Subtle; return "▼ Older than installed"; }
            tagBrush = UiHelpers.Accent;
            return "● Currently installed";
        }

        /// <summary>
        /// The single gate on "may this build be installed at all". Two independent floors feed it,
        /// and they are checked in that order so the more specific reason wins:
        ///
        ///   1. The DEVICE floor — this machine's model needs at least version X (the Claw 8 EX only
        ///      landed proper support in 0.1.7.63). Baked into DeviceDetect, since it is a fact about
        ///      hardware and doesn't change after the fact.
        ///   2. The MANIFEST floor — nobody may install below version Y any more, whatever their
        ///      hardware. Curated globally (see SetupVersionCheck), because the reason is usually the
        ///      build's INSTALL ROUTINE rather than the build itself, and that judgement is made long
        ///      after the build shipped.
        ///
        /// Both fail OPEN on anything unexpected: no floor known, an unparseable version string, or a
        /// manifest we couldn't fetch all leave the build installable. A user who is offline, or whom
        /// a typo'd manifest would otherwise lock out of installing anything at all, is worse off than
        /// one who slips an old build past us.
        /// </summary>
        private bool IsBlocked(BuildSource b, out string reason)
        {
            reason = null;
            if (!TryParseVersion(b.Version, out var v)) return false;

            var deviceMin = DeviceDetect.MinimumSupportedVersion(_deviceModel);
            if (deviceMin != null && v < deviceMin)
            {
                reason = $"Not supported on this device — needs {deviceMin}+";
                return true;
            }

            var appMin = _setupVersionCheck?.MinimumAppVersion;
            if (appMin != null && v < appMin)
            {
                reason = _setupVersionCheck.AppVersionMessage;
                return true;
            }

            return false;
        }

        /// <summary>
        /// D-Pad grid navigation: Left/Right move by one card, Up/Down by a row (stride 2, matching
        /// the 2-column layout above). Treated as one global 2-col grid across all three sections —
        /// a small simplification at section boundaries, but predictable and simple.
        /// </summary>
        private void MoveSelection(PadButton dir)
        {
            if (_view == View.Home) { MoveHomeSelection(dir); return; }
            if (_view == View.Onboarding) { MoveOnboardingSelection(dir); return; }
            if (_view == View.Maintenance) { MoveMaintenanceSelection(dir); return; }

            // A hand-off screen (missing prerequisites / untrusted certificate) is up. _view is still
            // Browse — these screens replace the CONTENT without being their own view — so without this
            // the Browse branch below would run RenderBrowse() and wipe the screen the user is following,
            // dropping them back into the build picker. On the tools screen the keys move the cursor
            // instead; the certificate screen has nothing to move, so they simply do nothing.
            if (_recheckAction != null)
            {
                if (_prereqTools != null) MovePrereqSelection(dir);
                return;
            }

            if (_view != View.Browse || _busy || _confirming || _flat.Count == 0) return;

            int delta = dir switch
            {
                PadButton.Left => -1,
                PadButton.Right => 1,
                PadButton.Up => -2,
                PadButton.Down => 2,
                _ => 0,
            };
            if (delta == 0) return;

            int next = _selectedIndex < 0 ? 0 : _selectedIndex + delta;
            if (next < 0) next = 0;
            if (next >= _flat.Count) next = _flat.Count - 1;
            if (next == _selectedIndex) return;

            _selectedIndex = next;
            RenderBrowse();
            if (_rowElements.TryGetValue(_flat[_selectedIndex], out var el)) el.BringIntoView();
        }

        /// <summary>D-pad navigation over the 3 Home tiles (a single row: Left/Right move the cursor,
        /// A opens the selected one — wired in RefreshActionBar). The "coming soon" placeholder tiles
        /// below are non-actionable, so the cursor stays on the top row.</summary>
        private void MoveHomeSelection(PadButton dir)
        {
            int next = _homeSelectedIndex;
            // Up/Down only do something when the update notice is on screen: it is the one row above
            // the tiles. Without an update offered they stay inert (the "coming soon" placeholders
            // below are non-actionable, so there is still nothing under the tile row).
            if (dir == PadButton.Up) next = CenterUpdateOffered ? -1 : next;
            else if (dir == PadButton.Down) next = next < 0 ? 0 : next;
            else if (dir == PadButton.Left) next = next < 0 ? next : next - 1;
            else if (dir == PadButton.Right) next = next < 0 ? next : next + 1;
            else return;

            int minIndex = CenterUpdateOffered ? -1 : 0;
            if (next < minIndex) next = minIndex;
            if (next > 2) next = 2;
            if (next == _homeSelectedIndex) return;

            _homeSelectedIndex = next;
            RenderHome();
            RefreshActionBar();
        }

        /// <summary>D-pad navigation over the onboarding step cards (2-column grid: Left/Right by one,
        /// Up/Down by a row). The auto-jump card sits alone in the last row, so Left/Right there adjust
        /// the slot number instead of moving. A (footer) runs the selected step.</summary>
        private void MoveOnboardingSelection(PadButton dir)
        {
            if (_busy || _confirming || _onboarding.IsConnecting) return;
            var steps = _onboarding.Steps;
            if (steps.Count == 0) return;

            if (_onbSelectedIndex == OnboardingRunner.StepAutoJump
                && (dir == PadButton.Left || dir == PadButton.Right)
                && steps[OnboardingRunner.StepAutoJump].Actionable)
            {
                if (dir == PadButton.Left && _onboarding.AutoJumpPositionValue > 1) _onboarding.AutoJumpPositionValue--;
                else if (dir == PadButton.Right && _onboarding.AutoJumpPositionValue < 10) _onboarding.AutoJumpPositionValue++;
                RenderOnboarding();
                return;
            }

            int delta = dir switch
            {
                PadButton.Left => -1,
                PadButton.Right => 1,
                PadButton.Up => -2,
                PadButton.Down => 2,
                _ => 0,
            };
            if (delta == 0) return;
            int next = _onbSelectedIndex + delta;
            if (next < 0) next = 0;
            if (next >= steps.Count) next = steps.Count - 1;
            if (next == _onbSelectedIndex) return;

            _onbSelectedIndex = next;
            RenderOnboarding();
        }
        #endregion

        #region Footer action bar
        private void RefreshActionBar()
        {
            _liveActions.Clear();
            ActionBar.Children.Clear();

            if (_confirming)
            {
                if (_buildBlocked) { AddAction(PadButton.B, "Back", true, CancelConfirm); AddScrollHint(); return; }
                AddAction(PadButton.A, "Yes, install", true, ConfirmInstall);
                AddAction(PadButton.B, "Cancel", true, CancelConfirm);
                AddScrollHint(); // the "What's new" section can run long
                return;
            }

            // View-specific footers come BEFORE the _busy/_installFinished gates below: those gates are
            // about the build browse/download/install flow (Browse view) and must NOT blank the footer
            // of Home/Onboarding/Maintenance. A background source refresh (RefreshSourcesAsync sets
            // _busy) otherwise left onboarding with an empty footer — D-pad navigation still worked (it's
            // handled in Invoke's fallback), but A/B/Y never bound, so pressing A did nothing.
            if (_view == View.Home)
            {
                AddAction(PadButton.A, "Open", true, ActivateHomeTile);
                AddAction(PadButton.B, "Exit", true, () => Application.Current.Shutdown());
                return;
            }

            if (_view == View.Onboarding)
            {
                var sel = (_onbSelectedIndex >= 0 && _onbSelectedIndex < _onboarding.Steps.Count)
                    ? _onboarding.Steps[_onbSelectedIndex] : null;
                bool canRun = sel != null && sel.Actionable && !_onboarding.IsConnecting;
                AddAction(PadButton.A, "Run", canRun, () => _ = _onboarding.RunStepAsync(_onbSelectedIndex, msg => Dispatcher.Invoke(RenderOnboarding)));
                AddAction(PadButton.Y, "Refresh status", !_onboarding.IsConnecting, () => _ = _onboarding.RefreshStatusAsync(msg => Dispatcher.Invoke(RenderOnboarding)));
                AddAction(PadButton.B, "Back", true, GoHome);
                return;
            }

            if (_view == View.Maintenance)
            {
                RefreshMaintenanceActionBar();
                return;
            }

            // Browse-view flow states below (these never apply to the views handled above).
            // Nothing is actionable mid-download/install — an empty bar beats four dead-looking chips.
            if (_busy) return;

            // A hand-off screen is up: the user has gone off to install a driver or import the
            // certificate. Ⓨ re-checks, matching Onboarding's "Refresh status" — checked BEFORE the
            // _installFinished branch below, which would otherwise offer nothing but Exit.
            if (_recheckAction != null)
            {
                // On the tools screen Ⓐ opens the download page of the card the cursor is on, so the
                // whole screen is usable from the controller without reaching for the mouse.
                string prereqUrl = SelectedPrereqUrl();
                if (prereqUrl != null)
                    AddAction(PadButton.A, "Open download page", true, () => PrerequisiteGuide.OpenPage(prereqUrl));

                AddAction(PadButton.Y, _recheckLabel ?? "Re-check", true, _recheckAction);
                AddAction(PadButton.B, "Exit", true, () => Application.Current.Shutdown());
                AddScrollHint();
                return;
            }

            // Once an install has run to completion (success or failure), the only thing left to do
            // is close — re-launch the Center for another round rather than silently falling back
            // into the same picker.
            if (_installFinished)
            {
                AddAction(PadButton.B, "Exit", true, () => Application.Current.Shutdown());
                AddScrollHint();
                return;
            }

            AddAction(PadButton.A, "Install this build", _flat.Count > 0, () =>
            {
                if (_selectedIndex >= 0 && _selectedIndex < _flat.Count) ShowConfirm(_flat[_selectedIndex]);
            });
            AddAction(PadButton.Y, "Refresh", true, () => _ = RefreshSourcesAsync());
            AddAction(PadButton.B, "Back", true, GoHome);
            AddScrollHint();
        }

        /// <summary>Non-interactive footer hint: right stick scrolls the content — added wherever the
        /// current screen can realistically overflow the viewport (the "What's new" section on Confirm
        /// in particular, but Browse's list and the install history can run long too).</summary>
        private void AddScrollHint()
        {
            var glyph = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/xbox/xbox_stick_r_vertical.png", UriKind.Absolute)),
                Width = 44, Height = 44,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);

            var label = new TextBlock
            {
                Text = "Scroll", FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiHelpers.Subtle,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(glyph);
            content.Children.Add(label);
            ActionBar.Children.Add(new Border { Padding = new Thickness(10, 0, 10, 0), Child = content });
        }

        private void AddAction(PadButton b, string label, bool enabled, Action action)
        {
            if (enabled) _liveActions[b] = action;
            ActionBar.Children.Add(ActionBarBuilder.BuildChip(b, label, enabled, action));
        }

        #endregion

        #region Confirm
        private void ShowConfirm(BuildSource build)
        {
            if (_busy || build == null) return;
            _pendingBuild = build;
            _confirming = true;

            ContentHost.Children.Clear();

            if (IsBlocked(build, out string blockReason))
            {
                _buildBlocked = true;
                ContentHost.Children.Add(UiHelpers.Title("This build can't be installed"));
                ContentHost.Children.Add(UiHelpers.Body($"{build.Version} — {build.Origin} — {build.Title}"));
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Error, "Blocked", blockReason));
                RefreshActionBar();
                return;
            }
            _buildBlocked = false;

            ContentHost.Children.Add(UiHelpers.Title($"Install {build.Version}?"));
            ContentHost.Children.Add(UiHelpers.Body($"{build.Origin} — {build.Title}"));

            if (_installedVersion != null && TryParseVersion(build.Version, out var selVer) && selVer < _installedVersion)
            {
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Downgrade",
                    $"Currently installed: {_installedVersion} — this installs an OLDER version ({selVer})."));
            }

            // "What's new" — only Releases/Test builds carry a GitHub release body; nightlies don't.
            if (!string.IsNullOrWhiteSpace(build.Body))
            {
                ContentHost.Children.Add(new TextBlock
                {
                    Text = "What's new", FontSize = 18, FontWeight = FontWeights.Bold,
                    Foreground = UiHelpers.Text, Margin = new Thickness(0, 16, 0, 6),
                });
                var notes = new StackPanel();
                ReleaseNotes.RenderInto(notes, build.Body);
                ContentHost.Children.Add(notes);
            }

            RefreshActionBar();
        }

        private void CancelConfirm()
        {
            _confirming = false;
            _buildBlocked = false;
            _pendingBuild = null;
            RenderBrowse();
            RefreshActionBar();
        }

        private void ConfirmInstall()
        {
            var build = _pendingBuild;
            _confirming = false;
            _pendingBuild = null;
            if (build == null) { RenderBrowse(); RefreshActionBar(); return; }
            _ = InstallSelectedAsync(build);
        }

        private static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            return Version.TryParse(s.TrimStart('v', 'V'), out v);
        }
        #endregion

        #region Install
        /// <summary>
        /// Ends the install run and hands the screen over to <paramref name="screen"/>. Both hand-off
        /// screens (missing prerequisites, untrusted certificate) need the exact same bookkeeping, and
        /// getting it wrong leaves the action bar stuck on "installing" with no way out.
        /// </summary>
        private void StopInstallAndShow(UIElement screen, BuildSource build, string recheckLabel)
        {
            _busy = false;
            _installFinished = true;
            // Ⓨ resumes the SAME build without downloading it again (reuseStaged).
            _recheckLabel = recheckLabel;
            _recheckAction = () => _ = InstallSelectedAsync(build, reuseStaged: true);
            ContentHost.Children.Clear();
            ContentHost.Children.Add(screen);
            RefreshActionBar();
        }

        /// <summary>The heading of a "do this, then come back" screen. The caller fills in the cards and
        /// ends with <see cref="BuildRecheckButton"/>.</summary>
        private static StackPanel BuildHandoffScreen(string title, string intro)
        {
            var root = new StackPanel();
            root.Children.Add(UiHelpers.Title(title));
            root.Children.Add(UiHelpers.Body(intro));
            return root;
        }

        /// <summary>The footer hint both hand-off screens end with. The action itself lives on Ⓨ in the
        /// action bar (see StopInstallAndShow) like every other screen's — this is only the on-page
        /// reminder, since the user has just come back from another window and needs to be told what to
        /// press. Mouse users can click it too.</summary>
        private Button BuildRecheckButton(BuildSource build, string label)
        {
            var button = new Button
            {
                Content = "Ⓨ  " + label,
                Style = (Style)Application.Current.Resources["SetupButton"],
                MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 20, 0, 0),
            };
            button.Click += (_, __) => _ = InstallSelectedAsync(build, reuseStaged: true);
            return button;
        }

        // BuildCommandBox lived here: a read-only TextBox showing a "winget install …" line per tool.
        // Removed with the winget hints themselves (see PrerequisiteGuide). Worth knowing that it was
        // also the only TextBox in Center, and therefore the only thing that ever reached WPF's full
        // text-layout path — which is where the InvariantGlobalization crash fired (see the csproj note).
        // If a TextBox is ever needed again, that setting must stay false.

        /// <summary>
        /// Shown when a prerequisite tool is missing. Center detects and explains; the USER downloads
        /// and installs, from the vendor, with the vendor's own signature and elevation prompt.
        ///
        /// Center used to do this itself — winget for HidHide/RTSS/PawnIO, download-then-runas for usbip
        /// and the HidHide MSI. See PrerequisiteGuide for why none of that is coming back: it made an
        /// unsigned app fetch executables and run them elevated, and it was the last thing forcing UAC
        /// prompts out of Center.
        /// </summary>
        private void ShowMissingPrerequisites(BuildSource build, List<ToolStatus> missing)
        {
            // Remember what this screen is about. D-pad navigation re-renders it (same pattern as
            // Browse/Onboarding), and without this state a re-render would have nothing to draw.
            _prereqBuild = build;
            _prereqTools = missing;
            if (_prereqSelectedIndex >= missing.Count) _prereqSelectedIndex = 0;
            RenderMissingPrerequisites();
        }

        /// <summary>
        /// Draws the missing-prerequisites screen from <see cref="_prereqTools"/>, with the controller
        /// cursor on <see cref="_prereqSelectedIndex"/>. Split out from
        /// <see cref="ShowMissingPrerequisites"/> so moving the cursor can redraw it.
        /// </summary>
        private void RenderMissingPrerequisites()
        {
            var build = _prereqBuild;
            var missing = _prereqTools;
            if (missing == null) return;

            var root = BuildHandoffScreen(
                "Install the missing prerequisites",
                "Download each one from the vendor and run their installer, then re-check.");

            // HidHide and usbip install KERNEL DRIVERS, and a kernel driver does not exist until the
            // machine restarts. Skipping this leaves the user in the worst possible state: everything
            // reports installed, the install completes, and the virtual controller silently does
            // nothing — which is exactly what happened on 2026-07-30. This has to be impossible to
            // miss, so it sits at the TOP of the screen rather than as a line on one of the cards.
            if (missing.Any(t => PrerequisiteGuide.For(t.Name)?.NeedsReboot == true))
            {
                var names = missing
                    .Where(t => PrerequisiteGuide.For(t.Name)?.NeedsReboot == true)
                    .Select(t => t.Name)
                    .ToList();

                var rebootStack = new StackPanel();
                rebootStack.Children.Add(new TextBlock
                {
                    Text = "⚠  Restart the device after installing " + string.Join(" and ", names),
                    FontSize = 20, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Warn,
                    TextWrapping = TextWrapping.Wrap,
                });
                rebootStack.Children.Add(new TextBlock
                {
                    Text = "These install kernel drivers. Without the restart everything looks installed " +
                           "and the virtual controller still won't start.",
                    FontSize = 15, Foreground = UiHelpers.Text,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                });

                root.Children.Add(new Border
                {
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(20, 16, 20, 16),
                    Margin = new Thickness(0, 4, 0, 16),
                    BorderBrush = UiHelpers.Warn,
                    BorderThickness = new Thickness(3),
                    Child = rebootStack,
                });
            }

            for (int i = 0; i < missing.Count; i++)
            {
                var tool = missing[i];
                bool selectedTool = i == _prereqSelectedIndex;
                var info = PrerequisiteGuide.For(tool.Name);
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = tool.Name, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
                });

                // A tool can be "not installed" for two very different reasons, and the difference
                // matters: a clean miss just needs installing, whereas ToolDetect's BROKEN verdict means
                // leftover registry entries with no driver binary — which reads as installed to a lot of
                // other software and needs a reinstall + reboot to actually clear.
                bool broken = tool.Detail != null && tool.Detail.StartsWith("BROKEN", StringComparison.Ordinal);
                if (broken)
                    stack.Children.Add(new TextBlock
                    {
                        Text = tool.Detail, FontSize = 14, FontWeight = FontWeights.SemiBold,
                        Foreground = UiHelpers.Warn, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                    });

                if (info != null)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = info.Why + "  " + info.WhatToGet,
                        FontSize = 14, Foreground = UiHelpers.Subtle,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                    });

                    // The one trap that breaks this tool, in its own outlined strip. Inline in the
                    // paragraph it was demonstrably missed — twice, for usbip's arm64 asset.
                    if (info.Warning != null)
                        stack.Children.Add(new Border
                        {
                            Background = Tint(UiHelpers.Error, 0x22),
                            BorderBrush = Tint(UiHelpers.Error, 0x99),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(10, 6, 10, 6),
                            Margin = new Thickness(0, 10, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = new TextBlock
                            {
                                Text = info.Warning,
                                FontSize = 16, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Error,
                                TextWrapping = TextWrapping.Wrap,
                            },
                        });

                    var open = new Button
                    {
                        Content = "Open download page",
                        Style = (Style)Application.Current.Resources["SetupButton"],
                        MinWidth = 190,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 10, 0, 0),
                    };
                    string url = info.PageUrl;
                    open.Click += (_, __) => PrerequisiteGuide.OpenPage(url);
                    stack.Children.Add(open);
                }

                // Accent outline on the card the controller cursor is on, same convention as the build
                // tiles and the onboarding steps — Ⓐ opens that one's download page.
                var toolPad = new Thickness(20, 16, 20, 16);
                var card = new Border
                {
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(0, 0, 0, 12),
                    BorderBrush = selectedTool ? UiHelpers.Accent : (broken ? UiHelpers.Warn : UiHelpers.Subtle),
                    BorderThickness = new Thickness(selectedTool ? 3 : 1),
                    Padding = selectedTool ? Deflate(toolPad, 2) : toolPad, // see Deflate: keeps the card from resizing
                    Child = stack,
                };
                if (selectedTool) _prereqSelectedCard = card;
                root.Children.Add(card);
            }

            root.Children.Add(BuildRecheckButton(build, "Re-check and continue"));
            StopInstallAndShow(root, build, "Re-check tools");
            _prereqSelectedCard?.BringIntoView();
        }

        /// <summary>
        /// D-pad over the missing-tool cards. Any direction moves by one — it is a single column, and on
        /// a handheld the user should not have to work out which axis this particular list uses.
        ///
        /// This exists because without it the keys fell through to <see cref="MoveSelection"/>'s Browse
        /// branch, which called RenderBrowse() and replaced this screen with the build list — the user
        /// was thrown back into the picker they had already started an install from.
        /// </summary>
        private void MovePrereqSelection(PadButton dir)
        {
            if (_prereqTools == null || _prereqTools.Count == 0) return;

            int delta = dir switch
            {
                PadButton.Up or PadButton.Left => -1,
                PadButton.Down or PadButton.Right => 1,
                _ => 0,
            };
            if (delta == 0) return;

            int next = _prereqSelectedIndex + delta;
            if (next < 0 || next >= _prereqTools.Count) return;

            _prereqSelectedIndex = next;
            RenderMissingPrerequisites();
            RefreshActionBar();
        }

        /// <summary>Download page of the tool the cursor is on, or null.</summary>
        private string SelectedPrereqUrl()
        {
            if (_prereqTools == null || _prereqSelectedIndex < 0 || _prereqSelectedIndex >= _prereqTools.Count)
                return null;
            return PrerequisiteGuide.For(_prereqTools[_prereqSelectedIndex].Name)?.PageUrl;
        }

        /// <summary>
        /// Shown when the ClawTweaks signing certificate isn't trusted yet. The user imports it through
        /// Windows' own Certificate Import Wizard; Center points at the file and says which store, and
        /// on a re-check names the specific mistake if it went somewhere else.
        ///
        /// The certificate has to go into the MACHINE store for the MSIX to be sideloadable, so writing
        /// it needs admin — it was the last privileged action left in Center. Handing it to the wizard
        /// means Windows asks for those rights, for a store the user picked, instead of an unsigned app
        /// asking for admin and then writing to a certificate store silently. See CertInstaller.
        ///
        /// This normally happens exactly once per machine: the same key signs every build, so once it is
        /// trusted, every future ClawTweaks install and update skips this screen entirely.
        /// </summary>
        private void ShowCertificateHandoff(BuildSource build, string cerPath)
        {
            var root = BuildHandoffScreen(
                "Trust the ClawTweaks signing certificate",
                "ClawTweaks is signed with its own certificate, and Windows only installs a package whose " +
                "certificate it trusts. You only have to do this once — every future build uses the same one.");

            // Say what is actually wrong, not just "not trusted". Both of these mistakes leave the user
            // certain they did the step, and a bare "still not trusted" would send them round the same
            // loop making the same choice.
            var placement = CertInstaller.Diagnose(CertInstaller.ThumbprintOf(cerPath));
            if (placement != CertInstaller.CertPlacement.Missing)
            {
                string what = placement switch
                {
                    CertInstaller.CertPlacement.WrongScopeCurrentUser =>
                        "The certificate was imported for your user account instead of the local machine. " +
                        "Windows only consults the machine store when installing a package, so this has no " +
                        "effect. Run the import again and pick \"Local Machine\" on the first page.",
                    CertInstaller.CertPlacement.WrongStoreRoot =>
                        "The certificate landed in \"Trusted Root Certification Authorities\". That is both " +
                        "more than ClawTweaks needs — it would make this certificate a trust anchor for " +
                        "anything, not just this package — and still not where Windows looks. Remove it " +
                        "from there and import it into \"Trusted People\" instead.",
                    _ => null,
                };
                if (what != null)
                    root.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Almost — but in the wrong place", what));
            }

            var stack = new StackPanel();
            int step = 1;
            foreach (var line in CertInstaller.ImportSteps)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"{step++}.  {line}",
                    FontSize = 15, Foreground = UiHelpers.Text,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                });
            }
            stack.Children.Add(new TextBlock
            {
                Text = cerPath, FontSize = 13, Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

            // Opens the FOLDER; the user double-clicks the .cer themselves. Center used to launch the
            // file, and dropped that after a behavioural Defender detection whose timing pointed here —
            // suspected, not proven, see the note on CertInstaller.ShowInExplorer before treating the
            // link as established.
            var showFolder = new Button
            {
                Content = "Show the certificate in Explorer",
                Style = (Style)Application.Current.Resources["SetupButton"],
                MinWidth = 250,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 14, 0, 0),
            };
            showFolder.Click += (_, __) => CertInstaller.ShowInExplorer(cerPath);
            stack.Children.Add(showFolder);

            root.Children.Add(new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20, 18, 20, 18),
                BorderBrush = UiHelpers.Subtle,
                BorderThickness = new Thickness(1),
                Child = stack,
            });

            root.Children.Add(BuildRecheckButton(build, "Check the certificate and continue"));
            StopInstallAndShow(root, build, "Check certificate");
        }

        /// <param name="reuseStaged">Set when this is a re-check from a hand-off screen: the build has
        /// already been downloaded and unpacked, so skip straight to re-testing the real state. Without
        /// it the user pays for a fresh download of a file already on disk every time they come back
        /// from installing one driver.</param>
        private async Task InstallSelectedAsync(BuildSource build, bool reuseStaged = false)
        {
            if (_busy) return;

            // Last line of defence for the version floors. ShowConfirm already refuses a blocked build,
            // so reaching here means something got past it — most plausibly a timing one: the picker
            // renders as soon as the GitHub/Drive listings land, while the manifest carrying the floor
            // arrives on its own request, and until it does IsBlocked answers "not blocked" by design.
            // A user quick enough to select and confirm inside that window would otherwise install
            // exactly the build the floor exists to stop. Cheap to re-ask; the check is a comparison.
            if (IsBlocked(build, out _)) { ShowConfirm(build); return; }

            // Leaving whatever hand-off screen we came from — the footer is rebuilt below and must not
            // keep offering a stale Ⓨ. The prerequisites state goes with it, so D-pad input belongs to
            // the build list again (and a later re-check rebuilds the list from a fresh detect anyway).
            _recheckAction = null;
            _recheckLabel = null;
            _prereqTools = null;
            _prereqBuild = null;
            _prereqSelectedCard = null;
            _prereqSelectedIndex = 0;

            // NO elevation anywhere in this method, up front or later. Center never asks for
            // administrator rights: the two steps that need them are handed to the user (missing driver
            // tools → the vendor's own installer; an untrusted certificate → Windows' own import
            // wizard), and everything Center does itself — detect, download, Add-AppxPackage — runs
            // fine as a plain unelevated app.
            //
            //   * DETECTION never needs it. This used to gate here because ToolDetect.PawnIO() reported
            //     PawnIO as missing unelevated — a detection bug, not a privilege problem: access-denied
            //     on a device object proves the device EXISTS, only ERROR_FILE_NOT_FOUND means absent.
            //     Fixed 2026-07-29. Do not re-add elevation for a status check.
            _busy = true;
            _installFinished = false;
            RefreshActionBar();

            // A stale helper from a previous run can still be alive here (Add-AppxPackage's
            // -ForceApplicationShutdown doesn't reach it — it's a plain exe, not an app-lifecycle
            // process). Snapshot its PID(s) now so "the fresh helper came up" later means a PID
            // outside this set, not just "some helper process exists" — and so any that are still
            // hanging around once the new one is confirmed up can be cleaned up.
            int[] priorHelperPids = HelperControl.GetHelperPids();
            Version previousVersion = _installedVersion; // cached from the last RefreshSourcesAsync

            // 2-column layout: left = progress/log, right = live status (used for the UAC-wait card
            // below — visible next to the Game Bar overlay when it opens).
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            Grid.SetColumn(left, 0);
            layout.Children.Add(left);

            var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);

            ContentHost.Children.Clear();
            ContentHost.Children.Add(layout);

            left.Children.Add(UiHelpers.Title($"Installing {build.Version}"));
            left.Children.Add(UiHelpers.Body($"{build.Origin} — {build.Title}"));

            var progressBar = new ProgressBar
            {
                Height = 14, Minimum = 0, Maximum = 100, Value = 0,
                Foreground = UiHelpers.Accent,
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 8),
                IsIndeterminate = true,
            };
            // Bounded, self-scrolling box for the step log: without this the log grows the whole page
            // taller as steps stream in, and following it (scrolling the outer ContentScroller down)
            // pushes the right column's Status card / Reboot-required warning out of view — exactly the
            // essential info the user needs to see. Capping the height here and auto-scrolling only
            // this inner box keeps the page height stable and the status card always in view.
            var logPanel = new StackPanel { Margin = new Thickness(2, 4, 0, 0) };
            var logScroller = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = logPanel,
            };
            left.Children.Add(progressBar);
            left.Children.Add(logScroller);

            var statusPanel = new ContentControl
            {
                Focusable = false,
                Content = BuildBigStatusCard(StatusKind.Working, "Preparing…", "Download and package install in progress."),
            };
            var historyPanel = new StackPanel();
            right.Children.Add(new TextBlock
            {
                Text = "Status", FontSize = 15, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 0, 0, 8),
            });
            right.Children.Add(statusPanel);
            right.Children.Add(historyPanel);

            // Each step gets its own row: a ✓ once it's done, a pulsing "…" badge while it's the
            // current one — so the user can tell at a glance exactly what's finished vs. still running,
            // instead of a flat scroll of text.
            ContentControl currentLogBadge = null;
            StackPanel currentLogDetail = null;

            UIElement BuildLogRow(string text, out ContentControl badge, out StackPanel detail)
            {
                badge = new ContentControl
                {
                    Width = 20, Height = 20, Focusable = false,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                    Content = UiHelpers.Badge(StatusKind.Working, 20),
                };

                // Grid, not a horizontal StackPanel: a horizontal StackPanel measures its children with
                // infinite available width, which silently defeats TextWrapping.Wrap and let long lines
                // (e.g. the usbip reboot notice) run off the right edge of the window.
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(badge, 0);
                var textBlock = new TextBlock
                {
                    Text = text, FontSize = 15, Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(textBlock, 1);
                header.Children.Add(badge);
                header.Children.Add(textBlock);

                detail = new StackPanel { Margin = new Thickness(28, 2, 0, 0) };

                var wrapper = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                wrapper.Children.Add(header);
                wrapper.Children.Add(detail);
                return wrapper;
            }

            void FinishLogRow(ContentControl badge, bool ok)
            {
                if (badge == null) return;
                badge.Content = UiHelpers.Badge(ok ? StatusKind.Ok : StatusKind.Error, 20);
            }

            // Dispatcher.Invoke matters here: PackageInstaller.Install runs inside Task.Run further
            // down and calls this synchronously from a thread-pool thread, not just via awaited
            // continuations — same guard InstallPhase.Log already uses for the same reason.
            void Log(string s) => Dispatcher.Invoke(() =>
            {
                FinishLogRow(currentLogBadge, true);
                logPanel.Children.Add(BuildLogRow(s, out currentLogBadge, out currentLogDetail));
                logScroller.ScrollToBottom();
            });

            // Appends a sub-line under the CURRENT row instead of starting a new checkmarked row —
            // used to collapse a multi-step sub-flow (the non-silent usbip installer in particular)
            // into one group instead of one top-level tick per internal step.
            void LogDetail(string s) => Dispatcher.Invoke(() =>
            {
                currentLogDetail?.Children.Add(new TextBlock
                {
                    Text = s, FontSize = 13, Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
                });
                logScroller.ScrollToBottom();
            });
            var progress = new Progress<int>(p =>
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = p;
            });

            try
            {
                // Re-check from a hand-off screen: the bytes are already unpacked on disk. Re-downloading
                // them would be pure waste and is what the first version did — the user installs one
                // driver, presses re-check, and sits through the whole ZIP again.
                bool haveStaged = reuseStaged
                    && _stagedRoot != null
                    && ReferenceEquals(_stagedBuild, build)
                    && System.IO.Directory.Exists(_stagedRoot);

                if (haveStaged)
                {
                    SetupContext.AssetRoot = _stagedRoot;
                    Log($"Re-checking {build.Version} — already downloaded, nothing to fetch again.");
                }
                else
                {
                    bool certTrusted = await Task.Run(() => CertInstaller.IsKnownCertAlreadyTrusted());
                    string staged = await BuildDownloader.DownloadAndStageAsync(build, certTrusted, Log, progress);
                    SetupContext.AssetRoot = staged;
                    _stagedRoot = staged;
                    _stagedBuild = build;
                }

                // Straight into the actual install from here — no manual wizard walk-through. The
                // Center menu exists for fast iteration on an already-onboarded dev device: pick a
                // build, the tool triggers the install and watches it succeed.
                //
                // NOTHING below elevates. Center is an ordinary unelevated app: the two steps that
                // genuinely need administrator rights — installing the driver tools, and trusting the
                // signing certificate into the machine store — are handed to the user, who does them
                // through the vendor's installer and Windows' own certificate wizard. Everything that
                // remains here (detection, download, Add-AppxPackage) needs no rights at all.
                // Add-AppxPackage in particular is a per-user deployment and works fine unelevated —
                // that is measured, not assumed.
                //
                // History worth keeping: a 2026-07-25 change blamed unelevated running for
                // ToolDetect.PawnIO() reporting PawnIO as missing when it was installed and working.
                // The detection was at fault, not the privilege level. Do not reintroduce elevation for
                // the sake of a status check.
                progressBar.IsIndeterminate = true;
                bool ok = true;

                var hidhide = await Task.Run(() => ToolDetect.HidHide());
                var rtss = await Task.Run(() => ToolDetect.Rtss());
                var usbip = await Task.Run(() => ToolDetect.Usbip());
                var pawnio = await Task.Run(() => ToolDetect.PawnIO());

                // Each Detail says WHY a tool counted as present (device, file, or a leftover registry
                // entry) — the thing that actually matters when reading a user's log back.
                Log("Checking prerequisites…");
                foreach (var t in new[] { hidhide, rtss, usbip, pawnio })
                    LogDetail($"  {t.Name}: installed={t.Installed} ({t.Detail})");

                var missingTools = new[] { hidhide, usbip, rtss, pawnio }.Where(t => !t.Installed).ToList();
                if (missingTools.Count > 0)
                {
                    // Hand over and stop. Not a failure — there is simply something the user has to do
                    // first, and saying so on a screen with the vendor links beats a red X in a log.
                    FinishLogRow(currentLogBadge, false);
                    ShowMissingPrerequisites(build, missingTools);
                    return;
                }
                Log("Required tools (HidHide, RTSS, usbip, PawnIO) already installed.");

                // The signing certificate. Same shape: check, and hand over if it isn't trusted yet.
                string cer = CertInstaller.FindSiblingCer();
                if (cer != null && !CertInstaller.IsTrusted(CertInstaller.ThumbprintOf(cer)))
                {
                    FinishLogRow(currentLogBadge, false);
                    ShowCertificateHandoff(build, cer);
                    return;
                }
                if (cer != null) Log("Certificate already trusted.");

                string pkg = PackageInstaller.FindPackage();
                if (pkg == null)
                {
                    Log("No installable package found after staging.");
                    ok = false;
                }
                else if (ok)
                {
                    // Stop the old helper BEFORE registering the new package, the same way Install.ps1
                    // does and through the same shared protocol: ask, and kill only what does not
                    // answer. Add-AppxPackage's -ForceApplicationShutdown never reaches the deployed
                    // helper (plain exe outside the package), so without this the old build stays alive
                    // across the swap and briefly shares MSI WMI/EC, the HidHide/ViGEm mounts and the
                    // single-instance mutex with the new one.
                    await Task.Run(() => HelperControl.StopHelpers("center", Log));

                    var deps = PackageInstaller.FindDependencies(pkg);
                    ok &= await Task.Run(() => PackageInstaller.Install(pkg, deps, Log));
                }

                if (ok)
                {
                    // The helper's scheduled task is the one piece of this that DOES need administrator
                    // rights, and Center deliberately doesn't create it — the helper registers it from
                    // its own signed exe on first run, which is both a single prompt and a far better
                    // shape for Defender than an installer writing an exe plus a persistence entry.
                    // See HelperControl. All Center does is notice when it isn't there yet and say what
                    // the prompt the user is about to see is for.
                    if (!await Task.Run(() => HelperControl.ScheduledTaskExists()))
                    {
                        Log("First install on this PC — Windows will ask for permission once.");
                        LogDetail("ClawTweaks registers a scheduled task so the helper can start with the " +
                            "right permissions. Confirm the prompt when it appears. It only happens this " +
                            "once — later updates never ask again.");
                    }

                    Log("Opening Game Bar — the ClawTweaks widget will start the helper…");
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 0;
                    var helperProgress = new Progress<int>(p => progressBar.Value = p);

                    // Reinstalling the exact version that's already running doesn't restart the helper
                    // or show a UAC prompt — the "fresh, elevated PID" check below can never be
                    // satisfied, so a same-version reinstall always times out. Not a failure; the
                    // timeout message needs to say so instead of implying something went wrong.
                    bool sameVersionReinstall = previousVersion != null
                        && TryParseVersion(build.Version, out var selVerForReinstall) && selVerForReinstall == previousVersion;

                    bool up = await RunPostInstallMonitorAsync(
                        priorHelperPids, previousVersion != null, sameVersionReinstall, helperProgress, statusPanel, historyPanel);
                    progressBar.Value = 100;

                    Log(up
                        ? $"{DescribeTransition(previousVersion, build.Version)} — helper is up and running."
                        : "Installed, but the helper did not appear in time — open the Game Bar (Win+G) manually.");
                }

                FinishLogRow(currentLogBadge, ok);
                _busy = false;
                _installFinished = true;
                RefreshActionBar();

                // Fresh install or update, helper confirmed elevated and running — this is exactly the
                // trigger from the plan (Doku/PLAN_Center_Helper_Integration.md §3 Phase 3). Let the
                // install screen finish settling first (log/badge/action bar above) rather than yanking
                // the view away mid-render.
                if (ok) OpenOnboarding();
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                FinishLogRow(currentLogBadge, false);
                _busy = false;
                _installFinished = true;
                RefreshActionBar();
            }
        }

        /// <summary>Human-readable version transition for the final status ("Updated X → Y", not just "Installed Y").</summary>
        private static string DescribeTransition(Version previous, string selectedVersion)
        {
            if (previous == null) return $"Installed {selectedVersion}";
            if (!TryParseVersion(selectedVersion, out var selected)) return $"Installed {selectedVersion}";
            if (selected > previous) return $"Updated {previous} → {selected}";
            if (selected < previous) return $"Downgraded {previous} → {selected}";
            return $"Reinstalled {selected}";
        }

        /// <summary>
        /// Everything that happens after Add-AppxPackage succeeds: open the Game Bar (auto-closes
        /// itself after a few seconds so the user sees this panel, not just the overlay — long enough
        /// for the widget to actually finish loading and kick off the helper; 1s wasn't, observed live:
        /// the Game Bar closed before the widget had a chance to start it), wait for the FRESH
        /// helper (surfacing the UAC prompt prominently if registering the helper's scheduled task
        /// needs one), verify no stale helper is left over from before the update, then run the
        /// controller diagnostic (HW vs. virtual mode). Settles for a fixed ~20s total before
        /// declaring the install done, so nothing flaky shows up right after. Every step is shown live
        /// in <paramref name="statusPanel"/> (the big current-step card, the only place that keeps the
        /// checkmark-in-circle look) and appended to <paramref name="historyPanel"/> as plain, flush
        /// text lines (a permanent log of what happened, deliberately no badge/circle of its own).
        /// </summary>
        private static async Task<bool> RunPostInstallMonitorAsync(
            int[] priorHelperPids, bool isUpdate, bool sameVersionReinstall, IProgress<int> progress,
            ContentControl statusPanel, StackPanel historyPanel)
        {
            void AddHistory(bool ok, string title, string detail) => AppendHistory(historyPanel, ok, title, detail);

            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            HelperControl.OpenGameBar();
            await Task.Delay(5000);
            HelperControl.CloseGameBarBestEffort(); // best-effort — the big UAC card below is the fallback if this doesn't land

            // 1) Wait for the FRESH helper, running ELEVATED — surfacing the UAC prompt prominently
            // while we wait. A new PID can appear before its own elevation request is even shown (the
            // unelevated MSIX helper deploys first and only then elevates the deployed copy to register
            // the task), so "PID exists" alone isn't proof the UAC was confirmed. TokenElevation is the
            // verifiable signal instead of guessing from timing.
            bool FreshHelperUp() => HelperControl.GetHelperPids()
                .Any(pid => !priorHelperPids.Contains(pid) && HelperControl.IsProcessElevated(pid));

            statusPanel.Content = BuildBigStatusCard(StatusKind.Working, "Starting…",
                "Waiting for the ClawTweaks helper to start.");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool? lastUacShowing = false;
            bool up = false;
            while (sw.ElapsedMilliseconds < 60000)
            {
                if (FreshHelperUp()) { up = true; break; }

                bool uacShowing = HelperControl.IsUacPromptShowing();
                if (uacShowing != lastUacShowing)
                {
                    statusPanel.Content = uacShowing
                        ? BuildBigStatusCard(StatusKind.Warning, "Waiting for UAC…",
                            "A confirmation prompt appeared — please confirm it to continue.")
                        : BuildBigStatusCard(StatusKind.Working, "Starting…",
                            "Waiting for the ClawTweaks helper to start.");
                    lastUacShowing = uacShowing;
                }

                progress?.Report((int)Math.Min(70, sw.ElapsedMilliseconds * 70 / 60000));
                await Task.Delay(300);
            }

            if (!up)
            {
                statusPanel.Content = sameVersionReinstall
                    ? BuildBigStatusCard(StatusKind.Warning, "Timed out",
                        "Expected for a same-version reinstall — the helper doesn't restart or show a UAC prompt when nothing changed. Open the Game Bar (Win+G) to check it's still running.")
                    : BuildBigStatusCard(StatusKind.Warning, "Timed out",
                        "Open the Game Bar manually (Win+G).");
                return false;
            }

            AddHistory(true, isUpdate ? "New update — background helper started" : "Installed — background helper started", "");
            progress?.Report(70);

            // 2) Duplicate-helper check — now a VERIFICATION, not a policy. The pre-install step already
            // asked every helper to shut down (HelperControl.StopHelpers) and killed whatever refused,
            // so a survivor here means that failed. Center no longer runs its own grace-period-then-kill
            // logic: it re-uses the same shared handover and keeps the kill only as a last resort.
            statusPanel.Content = BuildBigStatusCard(StatusKind.Working, "Checking for duplicate helpers…", "");
            bool AnyStaleAlive() => priorHelperPids.Any(IsProcessAlive);

            if (priorHelperPids.Length == 0 || !AnyStaleAlive())
            {
                AddHistory(true, "No duplicate helper detected", "");
            }
            else
            {
                statusPanel.Content = BuildBigStatusCard(StatusKind.Warning, "Removing leftover helper…",
                    "A helper from before the update is still running.");

                var (handedOver, killed) = await Task.Run(() =>
                    HelperControl.StopHelpers("center post-install", null));

                AddHistory(true, "No duplicate helper detected",
                    handedOver + killed == 0
                        ? "The old helper exited on its own."
                        : $"Unexpected survivor: {handedOver} handed over, {killed} terminated.");
            }
            progress?.Report(82);

            // 3) Controller diagnostic — same probe ControllerPhase/FinalizePhase already use during
            // first-time setup, reused here rather than reinvented. Retries a few times with a short
            // delay since the helper can take a moment after starting to actually mount the controller.
            statusPanel.Content = BuildBigStatusCard(StatusKind.Working, "Checking controller mode…", "");
            var (controllerOk, ctrlTitle, ctrlDetail, ctrlCause) = await ProbeControllerModeAsync();
            if (controllerOk) AddHistory(true, ctrlTitle, ctrlDetail);
            else AddHistory(false, ctrlTitle, ctrlCause);
            progress?.Report(95);

            // 4) Settle: give everything ~20s total (from opening the Game Bar) before declaring victory.
            int remainingMs = 20000 - (int)totalSw.ElapsedMilliseconds;
            if (remainingMs > 0) await Task.Delay(remainingMs);

            statusPanel.Content = BuildBigStatusCard(StatusKind.Ok, "Installation complete", "No restart necessary.");
            return true;
        }

        /// <summary>
        /// Reuses ControllerHealth.Probe() (the same PnP/XInput probe ControllerPhase/FinalizePhase run
        /// during first-time setup) to report whether the Claw is running in HW controller mode (native
        /// XInput surface, no overlay) or virtual controller mode (a VIIPER/ViGEm pad is mounted). The
        /// helper can take a moment after starting to actually mount the controller, so this retries a
        /// few times with a short delay before giving up and reporting why.
        /// </summary>
        private static async Task<(bool ok, string title, string detail, string cause)> ProbeControllerModeAsync()
        {
            HealthResult result = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                result = await Task.Run(() => ControllerHealth.Probe());
                if (result.ClawPresent)
                {
                    if (result.VirtualPadCount > 0)
                    {
                        string name = result.VirtualPadName ?? "Virtual pad";
                        return (true, "Virtual controller mode detected", $"{name} active and running.", null);
                    }
                    return (true, "HW controller mode detected", "MSI HW Controller active and running.", null);
                }
                if (attempt < 3) await Task.Delay(1500);
            }

            string cause = result.Problems.Count > 0 ? result.Problems[0]
                : (result.Warnings.Count > 0 ? result.Warnings[0] : "Claw controller not detected.");
            return (false, "Controller mode unknown", null, cause);
        }

        private static bool IsProcessAlive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        /// <summary>Permanent history line (title + optional detail) — unlike the big status card above
        /// it, these never get overwritten, so a fact like "reboot required" survives later steps
        /// moving the status card on to something else.</summary>
        private static void AppendHistory(StackPanel historyPanel, bool ok, string title, string detail)
        {
            var stack = new StackPanel { Margin = new Thickness(2, 8, 0, 0) };
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = ok ? UiHelpers.Ok : UiHelpers.Warn,
            });
            if (!string.IsNullOrEmpty(detail))
                stack.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 13, Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(14, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            historyPanel.Children.Add(stack);
        }

        /// <summary>
        /// Large, colour-highlighted "what's happening right now" card for the install's right column
        /// — deliberately much bigger than the regular <see cref="UiHelpers.StatusRow"/> rows, since
        /// this is the one thing the user needs to notice even glancing over from behind the Game Bar
        /// overlay (the UAC prompt in particular). Shows the looping loading-spinner GIF while
        /// <see cref="StatusKind.Working"/> (via <see cref="UiHelpers.Badge"/>).
        /// </summary>
        private static Border BuildBigStatusCard(StatusKind kind, string title, string detail)
        {
            var accent = UiHelpers.BrushFor(kind);
            var badge = UiHelpers.Badge(kind, 56);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = title, FontSize = 26, FontWeight = FontWeights.Bold,
                Foreground = UiHelpers.Text, TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(detail))
                text.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 16, Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                });

            // Grid, not a horizontal StackPanel — same wrap-defeating pitfall as the log rows above;
            // a long detail line (e.g. the reboot-required notice) needs to actually wrap, not run
            // past the window edge.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(badge);
            row.Children.Add(text);

            var accentColor = ((SolidColorBrush)accent).Color;
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, accentColor.R, accentColor.G, accentColor.B)),
                BorderBrush = accent,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(22, 20, 22, 20),
                Margin = new Thickness(0, 0, 0, 12),
                Child = row,
            };
        }
        #endregion
    }
}
