using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;

namespace ClawTweaksCenter
{
    /// <summary>
    /// System tray residency: lets Center stay running after its window closes, and lets a second
    /// launch (typically the ClawTweaks helper reacting to a hotkey - see App.xaml.cs's single-
    /// instance check) wake the resident instance instead of starting a competing one.
    ///
    /// The wake-pipe LISTENER runs for the whole process lifetime, independent of the
    /// CenterSettings.RunInBackground setting - a second launch while Center is simply open in the
    /// foreground should still bring the existing window to front rather than opening a duplicate.
    /// RunInBackground only decides what CLOSING the window does afterward: hide and stay resident,
    /// or actually exit.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private TaskbarIcon _trayIcon;
        private CancellationTokenSource _trayListenCts;
        // Set only by the tray menu's own "Exit Center" item - the one path that must bypass the
        // Closing redirect below even when RunInBackground is on, since "Exit" typed out on the tray
        // menu itself has to mean exit.
        private bool _reallyExiting;

        private void InitializeTray()
        {
            _trayListenCts = new CancellationTokenSource();
            Core.CenterInstanceSignal.StartListening(_trayListenCts.Token,
                onShow: () => Dispatcher.Invoke(BringToFront),
                onShowLibrary: () => Dispatcher.Invoke(() =>
                {
                    BringToFront();
                    if (_view != View.Library && LibraryAvailable) OpenLibrary();
                    // If the version check has not landed yet, TryEnterLibraryOnceKnown's own
                    // machinery (already wired for --library / OpenLibraryAtStartup) is not reachable
                    // from here without duplicating it - a wake this early in the resident instance's
                    // life is not the case this is for anyway, so no fallback is worth building.
                }));

            SyncTrayIcon();

            // The standard WPF "minimize to tray" mechanism: Application.Shutdown() closes every
            // window through Close(), which raises Closing on each - cancelling it here aborts the
            // WHOLE shutdown, not just this window. That is what lets ONE handler cover every exit
            // path in the app (the titlebar X, Home's "Exit", the hand-off screens' "Exit", and the
            // LaunchBehavior.Close case after a game launch) without editing each of them separately.
            Closing += (_, e) =>
            {
                if (_reallyExiting || !Core.CenterSettings.RunInBackground) return;
                e.Cancel = true;
                Hide();
            };

            Closed += (_, __) =>
            {
                _trayListenCts?.Cancel();
                _trayIcon?.Dispose();
            };
        }

        /// <summary>Creates or removes the tray icon to match the current setting. Called once at
        /// startup and again whenever the setting is toggled, so turning it on mid-session does not
        /// need a restart to take effect.</summary>
        private void SyncTrayIcon()
        {
            bool want = Core.CenterSettings.RunInBackground;
            if (want && _trayIcon == null)
            {
                _trayIcon = BuildTrayIcon();
            }
            else if (!want && _trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private TaskbarIcon BuildTrayIcon()
        {
            var icon = new TaskbarIcon
            {
                ToolTipText = "ClawTweaks Center",
            };

            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch (Exception ex) { Core.InstallLog.Write("Tray icon extraction failed: " + ex.Message); }

            var menu = new ContextMenu();

            var openItem = new MenuItem { Header = "Open Center" };
            openItem.Click += (_, __) => BringToFront();
            menu.Items.Add(openItem);

            var libraryItem = new MenuItem { Header = "Open Library" };
            libraryItem.Click += (_, __) =>
            {
                BringToFront();
                if (_view != View.Library && LibraryAvailable) OpenLibrary();
            };
            menu.Items.Add(libraryItem);

            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "Exit Center" };
            exitItem.Click += (_, __) =>
            {
                _reallyExiting = true;
                Application.Current.Shutdown();
            };
            menu.Items.Add(exitItem);

            icon.ContextMenu = menu;
            icon.TrayLeftMouseUp += (_, __) => BringToFront();
            return icon;
        }

        private void BringToFront()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            try { Ui.WindowMode.ForceForeground(this); } catch { }
        }
    }
}
