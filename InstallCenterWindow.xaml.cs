using System;
using System.Linq;
using System.Windows;
using ClawTweaksSetup.Core;
using ClawTweaksSetup.Navigation;
using ClawTweaksSetup.Ui;

namespace ClawTweaksSetup
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
        private readonly InstallCenterMode _mode;

        /// <summary>Command-line marker that survives the elevated relaunch (see StartInstall): tells
        /// this window to fire the install immediately once it's shown again, instead of making the
        /// user click Install/Update a second time after already having granted the UAC prompt once.
        /// The OS-level UAC consent is the actual trust checkpoint here — a redundant in-app click on
        /// top of it adds no real signal, so there is no reason to make the user do it.</summary>
        public const string ResumeArg = "--resume-install";

        public InstallCenterWindow(InstallCenterMode mode, Version installedVersion = null, Version runningVersion = null, bool autoStart = false)
        {
            _mode = mode;
            InitializeComponent();

            // Title is context-sensitive (default "Install ClawTweaks Center" from XAML). The sub-heading
            // was removed by design; AlreadyInstalled still needs its guidance, shown via StatusText.
            switch (mode)
            {
                case InstallCenterMode.Update:
                    TitleText.Text = "Update ClawTweaks Center";
                    TitleIcon.Text = ((char)0xE777).ToString(); // Segoe Fluent "UpdateRestore"
                    break;
                case InstallCenterMode.AlreadyInstalled:
                    TitleText.Text = "ClawTweaks Center is already installed";
                    TitleIcon.Text = ((char)0xE73E).ToString(); // Segoe Fluent "CheckMark"
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = $"Version {installedVersion} is already installed. Open it from the Start Menu " +
                                       "or the ClawTweaks Game Bar widget instead of running this Setup file again.";
                    break;
            }

            // Coming from the pre-0.1.9 machine-wide install. Mode is "Install" here — correctly, since
            // there is nothing in the per-user location yet — but calling it a plain first-time install
            // would be misleading to someone who has been running Center for months. Say what is
            // actually happening, and that the old copy survives this and gets dealt with afterwards
            // (Center can't remove it without admin, and it no longer asks; see BuildLegacyInstallCard).
            if (mode == InstallCenterMode.Install && SelfInstaller.LegacyInstallPresent())
            {
                TitleText.Text = "Move ClawTweaks Center to your user folder";
                var legacy = SelfInstaller.GetLegacyInstalledVersion();
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text =
                    (legacy != null ? $"Version {legacy} is installed for all users. " : "Center is installed for all users. ") +
                    "This version installs into your own user folder instead, which is why it — and every " +
                    "future update — needs no administrator rights. The old copy is left in place; Center " +
                    "will offer to remove it once this is done.";
            }

            Loaded += (_, __) =>
            {
                _nav = new XInputNavigator(this);
                _nav.ButtonPressed += b => Dispatcher.Invoke(() =>
                {
                    if (b == PadButton.A && _mode != InstallCenterMode.AlreadyInstalled) StartInstall();
                    else if (b == PadButton.B) Application.Current.Shutdown();
                });
                _nav.Start();
                RenderActionBar();

                // Elevated relaunch already happened and the user already granted the UAC prompt once —
                // proceed straight into the install instead of landing back on this same screen waiting
                // for a second click.
                if (autoStart && _mode != InstallCenterMode.AlreadyInstalled) StartInstall();
            };
            Closed += (_, __) => _nav?.Dispose();

            // Keyboard fallback for desk testing, same convention as the other windows.
            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) { Application.Current.Shutdown(); e.Handled = true; }
            };
        }

        private void RenderActionBar()
        {
            ActionBar.Children.Clear();

            // AlreadyInstalled deliberately offers NO shortcut to launch the app from here — the
            // point is to teach the user that Center is a real installed Windows app now, opened via
            // the Start Menu or the Game Bar widget, not by re-running a downloaded Setup file.
            if (_mode != InstallCenterMode.AlreadyInstalled)
            {
                string label = _mode == InstallCenterMode.Update ? "Update" : "Install";
                ActionBar.Children.Add(ActionBarBuilder.BuildChip(PadButton.A, label, !_installing, StartInstall));
            }

            // Always available, even mid-install-attempt — the user must never be stuck on this
            // screen with no way out.
            ActionBar.Children.Add(ActionBarBuilder.BuildChip(PadButton.B, "Exit", true, () => Application.Current.Shutdown()));
        }

        private void StartInstall()
        {
            if (_installing) return;
            _installing = true;
            RenderActionBar();

            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = _mode == InstallCenterMode.Update ? "Updating..." : "Installing...";

            // No elevation, and no UAC prompt. Center installs per-user — %LOCALAPPDATA%\Programs, the
            // user's own Start Menu, HKCU's Uninstall hive — so installing and updating are ordinary
            // file and registry writes inside this user's profile. This used to relaunch elevated here
            // (and carry a ResumeArg across the prompt so the user didn't have to click Install twice);
            // moving off Program Files removed the reason for all of it. See SelfInstaller.
            bool ok = SelfInstaller.InstallAndRelaunch(msg => Dispatcher.Invoke(() => StatusText.Text = msg));
            if (ok)
            {
                // A new process is already starting from the installed location; this one is done.
                Application.Current.Shutdown();
            }
            else
            {
                _installing = false;
                StatusText.Foreground = UiHelpers.Error;
                StatusText.Text = (_mode == InstallCenterMode.Update ? "Update" : "Install") + " failed — see the log for details. Try again, or run as Administrator.";
                RenderActionBar();
            }
        }
    }
}

