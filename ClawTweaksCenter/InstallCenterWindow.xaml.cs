using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    public enum InstallCenterMode { Install, Update, AlreadyInstalled }

    /// <summary>
    /// Gate shown before anything else when the running exe is not yet installed to
    /// <see cref="SelfInstaller.InstallDir"/> — see App.xaml.cs, which picks the mode:
    ///   Install           — never installed before.
    ///   Update            — this exe is a genuinely newer build than what's installed.
    ///   AlreadyInstalled  — this exe is the same version or OLDER than what's installed; nothing to
    ///                       do here except point the user at the real installed copy (Start Menu /
    ///                       Game Bar widget) instead of silently launching something else out from
    ///                       under a double-click on a Setup exe they downloaded.
    /// Installs/updates Center as a regular Windows app, then relaunches from there — the normal
    /// CenterMenuWindow/MainWindow flow only ever runs from the installed location, so the widget
    /// MSIX can never be installed before Center itself is.
    /// </summary>
    public partial class InstallCenterWindow : Window
    {
        private XInputNavigator _nav;
        private bool _installing;
        private bool _legacyBlocking;
        private readonly InstallCenterMode _mode;

        /// <summary>Command-line marker that survives the elevated relaunch (see StartInstall): tells
        /// this window to fire the install immediately once it's shown again, instead of making the
        /// user click Install/Update a second time after already having granted the UAC prompt once.
        /// The OS-level UAC consent is the actual trust checkpoint here — a redundant in-app click on
        /// top of it adds no real signal, so there is no reason to make the user do it.</summary>
        public const string ResumeArg = "--resume-install";

        /// <summary>Command line handed to the INSTALLED copy once the self-install finished. Empty in
        /// every normal run; the ClawTweaks installer's post-reboot hand-off uses it to carry
        /// --onboarding across, so a new device comes back from its restart on the onboarding list
        /// instead of on Home. Kept as an opaque string: this window has no business knowing what the
        /// switch means, only that it has to survive the relaunch.</summary>
        private readonly string _relaunchArgs;

        public InstallCenterWindow(InstallCenterMode mode, Version installedVersion = null, Version runningVersion = null,
                                   bool autoStart = false, string relaunchArgs = null)
        {
            _mode = mode;
            _relaunchArgs = relaunchArgs;
            InitializeComponent();
            Ui.ModernWindow.Apply(this);
            Ui.WindowMode.Attach(this); // after ModernWindow — see WindowMode.Attach on the ordering

            // The XAML defaults are English literals and XAML never reaches Loc, so every string this
            // window shows is set from code. This one is the default title and the tick box; the
            // switch below overrides the title per mode.
            TitleText.Text = Core.Loc.T("Install ClawTweaks Center");
            DesktopShortcutOption.Content = Core.Loc.T("Add a desktop icon");

            // Nothing is going to be installed on these two screens, so an install option would be a
            // control that does nothing. Hidden rather than disabled: a greyed-out tick still reads as
            // a promise about what the next click will do.
            if (mode == InstallCenterMode.AlreadyInstalled)
                DesktopShortcutOption.Visibility = Visibility.Collapsed;

            // Title is context-sensitive (default "Install ClawTweaks Center" from XAML). The sub-heading
            // was removed by design; AlreadyInstalled still needs its guidance, shown via StatusText.
            switch (mode)
            {
                case InstallCenterMode.Update:
                    TitleText.Text = Core.Loc.T("Update ClawTweaks Center");
                    TitleIcon.Text = ((char)0xE777).ToString(); // Segoe Fluent "UpdateRestore"

                    // NOT ticked on an update. The box only ever ADDS an icon or removes one, so on a
                    // machine that already has Center installed a ticked box means "put the icon back"
                    // - which quietly undoes a user who deleted it, on every single update. Unticked,
                    // an update leaves the desktop exactly as the user left it, and someone who does
                    // want the icon is one press away.
                    DesktopShortcutOption.IsChecked = false;
                    break;
                case InstallCenterMode.AlreadyInstalled:
                    TitleText.Text = Core.Loc.T("ClawTweaks Center is already installed");
                    TitleIcon.Text = ((char)0xE73E).ToString(); // Segoe Fluent "CheckMark"
                    StatusText.Visibility = Visibility.Visible;
                    // Split rather than interpolated: a key with a version number in it could never
                    // match twice, so the sentence is translated and the number concatenated.
                    StatusText.Text = "Version " + installedVersion + " — " +
                        Core.Loc.T("This version is already installed. Open it from the Start Menu " +
                                   "or the ClawTweaks Game Bar widget instead of running this Setup file again.");
                    break;
            }

            // Coming from the pre-0.1.9 machine-wide install. Mode is "Install" here — correctly, since
            // there is nothing in the per-user location yet. The old copy and the new one are two
            // SEPARATE installs (different folder, different registry hive) — installing on top does
            // not touch the old one, so the user is left with two "ClawTweaks Center" entries in
            // Settings → Apps and two Start Menu shortcuts, one of which is stale and will keep offering
            // to update itself. Center cannot remove the old one itself (that needs admin, and this
            // version never asks) — so instead of installing anyway and hoping the user notices
            // BuildLegacyInstallCard's cleanup offer afterwards, this blocks Install/Update up front and
            // sends the user to do the one thing only they can do: uninstall the old copy first.
            if (mode == InstallCenterMode.Install)
                ApplyLegacyGate();

            Loaded += (_, __) =>
            {
                _nav = new XInputNavigator(this);
                _nav.ButtonPressed += b => Dispatcher.Invoke(() =>
                {
                    if (b == PadButton.A && _mode != InstallCenterMode.AlreadyInstalled && !_legacyBlocking) StartInstall();
                    else if (b == PadButton.X && DesktopShortcutOption.Visibility == Visibility.Visible && !_installing) ToggleDesktopOption();
                    else if (b == PadButton.Y && _legacyBlocking) RecheckLegacy();
                    else if (b == PadButton.B) Application.Current.Shutdown();
                });
                _nav.Start();
                RenderActionBar();

                // Elevated relaunch already happened and the user already granted the UAC prompt once —
                // proceed straight into the install instead of landing back on this same screen waiting
                // for a second click. Never applies while blocked on the old install — that gate exists
                // precisely to stop an install from happening automatically.
                if (autoStart && _mode != InstallCenterMode.AlreadyInstalled && !_legacyBlocking) StartInstall();
            };
            Closed += (_, __) => _nav?.Dispose();

            // Keyboard fallback for desk testing, same convention as the other windows (F5 = re-check,
            // matching CenterMenuWindow's refresh actions).
            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) { Application.Current.Shutdown(); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.F5 && _legacyBlocking) { RecheckLegacy(); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.F11) { Ui.WindowMode.Toggle(this); e.Handled = true; }
            };
        }

        /// <summary>Flips the desktop-icon option and re-renders its chip so the label matches.</summary>
        private void ToggleDesktopOption()
        {
            DesktopShortcutOption.IsChecked = !(DesktopShortcutOption.IsChecked == true);
            RenderActionBar();
        }

        /// <summary>
        /// Checks whether the old machine-wide install is still around and sets the title/status/action
        /// bar accordingly. Called at startup and again from <see cref="RecheckLegacy"/> once the user
        /// says they removed it — re-reads the actual state rather than trusting the claim, since the
        /// whole point is that Center cannot remove the old install itself and has no other way to know.
        /// </summary>
        private void ApplyLegacyGate()
        {
            _legacyBlocking = SelfInstaller.LegacyInstallPresent();

            // While the old install blocks the way there is no Install chip at all, so the option
            // belongs off screen with it.
            DesktopShortcutOption.Visibility = _legacyBlocking ? Visibility.Collapsed : Visibility.Visible;

            if (!_legacyBlocking)
            {
                // Either there never was an old install, or the re-check just confirmed it's gone —
                // fall back to the plain first-time-install screen (XAML's defaults).
                TitleIcon.Text = "";
                TitleText.Text = Core.Loc.T("Install ClawTweaks Center");
                StatusText.Foreground = UiHelpers.Accent;
                StatusText.Visibility = Visibility.Collapsed;
                return;
            }

            TitleIcon.Text = ((char)0xE7BA).ToString(); // Segoe Fluent "Warning"
            TitleText.Text = Core.Loc.T("Transition to new Center App - Uninstall the old version");
            StatusText.Foreground = UiHelpers.Warn;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text =
                "The new ClawTweaks Center needs no administrator rights and installs to a different location.\n\n" +
                "Open Windows Settings, go to Apps, find \"ClawTweaks Center\" and uninstall it, then press Re-check.";
        }

        /// <summary>Re-reads whether the old install is still there. The user has to actually remove it
        /// in Windows Settings — this only updates what the screen shows, it does not remove anything
        /// itself (Center never elevates; see SelfInstaller and BuildLegacyInstallCard for why).</summary>
        private void RecheckLegacy()
        {
            ApplyLegacyGate();
            RenderActionBar();
        }

        private void RenderActionBar()
        {
            ActionBar.Children.Clear();

            if (_legacyBlocking)
            {
                // No Install/Update chip at all while the old install is still there — Exit and
                // Re-check are the only options, matching what the status text tells the user.
                ActionBar.Children.Add(ActionBarBuilder.BuildChip(PadButton.Y, "Re-check", true, RecheckLegacy));
            }
            // AlreadyInstalled deliberately offers NO shortcut to launch the app from here — the
            // point is to teach the user that Center is a real installed Windows app now, opened via
            // the Start Menu or the Game Bar widget, not by re-running a downloaded Setup file.
            else if (_mode != InstallCenterMode.AlreadyInstalled)
            {
                string label = _mode == InstallCenterMode.Update ? "Update" : "Install";
                ActionBar.Children.Add(ActionBarBuilder.BuildChip(PadButton.A, label, !_installing, StartInstall));

                // The chip carries the ACTION, not the state — the tick box next to it already shows
                // whether the icon is coming. Naming the chip after the current value ("Desktop icon
                // on") would leave the user working out whether pressing X confirms or reverses it.
                if (DesktopShortcutOption.Visibility == Visibility.Visible)
                    ActionBar.Children.Add(ActionBarBuilder.BuildChip(
                        PadButton.X, "Desktop icon", !_installing, ToggleDesktopOption));
            }

            // Always available, even mid-install-attempt — the user must never be stuck on this
            // screen with no way out.
            ActionBar.Children.Add(ActionBarBuilder.BuildChip(PadButton.B, "Exit", true, () => Application.Current.Shutdown()));
        }

        /// <summary>
        /// ⚠️ ASYNC, AND THAT IS THE FIX, NOT A TIDY-UP.
        ///
        /// InstallAndRelaunch BLOCKS: it closes the running Center (up to 5 s of waiting on a process
        /// to go away) and then retries the file copy three times with growing pauses (another 3 s).
        /// Run on the UI thread, as this was, the window cannot repaint for as long as that lasts -
        /// "Updating..." is assigned and never drawn, the progress lines are assigned and never
        /// drawn, and every one of the log callbacks below hits Dispatcher.Invoke from the dispatcher
        /// thread itself, which runs inline and paints nothing.
        ///
        /// From outside that is indistinguishable from a dead button: the user clicks Update and the
        /// window sits there unchanged for the better part of ten seconds. It was never doing
        /// nothing; it was doing everything with the screen frozen.
        /// </summary>
        private async void StartInstall()
        {
            // Defense in depth — RenderActionBar already omits this chip and the button handlers
            // already check _legacyBlocking, so this only matters if something else calls StartInstall
            // directly. Silently doing nothing is correct here: the status text already says why.
            if (_installing || _legacyBlocking) return;
            _installing = true;
            RenderActionBar();

            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = Core.Loc.T(_mode == InstallCenterMode.Update ? "Updating..." : "Installing...");

            // No elevation, and no UAC prompt. Center installs per-user — %LOCALAPPDATA%\Programs, the
            // user's own Start Menu, HKCU's Uninstall hive — so installing and updating are ordinary
            // file and registry writes inside this user's profile. This used to relaunch elevated here
            // (and carry a ResumeArg across the prompt so the user didn't have to click Install twice);
            // moving off Program Files removed the reason for all of it. See SelfInstaller.
            bool desktopIcon = DesktopShortcutOption.IsChecked == true;
            bool ok;
            try
            {
                ok = await Task.Run(() => SelfInstaller.InstallAndRelaunch(
                    // The installer reports progress as finished strings; this is where they
                    // reach the screen, so this is where they can be translated.
                    msg => Dispatcher.Invoke(() => StatusText.Text = Core.Loc.T(msg)), desktopIcon,
                    _relaunchArgs));
            }
            catch (Exception ex)
            {
                // async void: an escaping exception here would take the process down instead of
                // showing the user anything. InstallAndRelaunch catches its own, so this covers only
                // the unexpected - but the unexpected is exactly what must not become a silent exit.
                Core.InstallLog.Write("StartInstall threw: " + ex);
                ok = false;
            }

            if (ok)
            {
                // A new process is already starting from the installed location; this one is done.
                Application.Current.Shutdown();
            }
            else
            {
                _installing = false;
                StatusText.Foreground = UiHelpers.Error;
                StatusText.Text = Core.Loc.T(_mode == InstallCenterMode.Update
                    ? "Update failed — see the log for details. Try again, or run as Administrator."
                    : "Install failed — see the log for details. Try again, or run as Administrator.");
                RenderActionBar();
            }
        }
    }
}

