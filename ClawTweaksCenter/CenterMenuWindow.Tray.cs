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
                }),
                onToggleLibrary: () => Dispatcher.Invoke(ToggleLibraryFromSignal),
                onShowHome: () => Dispatcher.Invoke(() =>
                {
                    BringToFront();
                    if (_view != View.Home) GoHome();
                }));

            SyncTrayIcon();

            // The standard WPF "minimize to tray" mechanism: Application.Shutdown() closes every
            // window through Close(), which raises Closing on each - cancelling it here aborts the
            // WHOLE shutdown, not just this window. That is what lets ONE handler cover every exit
            // path in the app (the titlebar X, Home's "Exit", the hand-off screens' "Exit", and the
            // LaunchBehavior.Close case after a game launch) without editing each of them separately.
            Closing += (_, e) =>
            {
                bool resident = !_reallyExiting && Core.CenterSettings.RunInBackground;
                // Logged on BOTH outcomes, not just the interesting one. A window that closed when it
                // was supposed to hide leaves no trace of having made that decision, and working out
                // afterwards which of the two branches ran is exactly the question a wedged process
                // cannot answer. It cost a session once.
                Core.InstallLog.Write(resident
                    ? "Closing: staying resident (hiding to tray)."
                    : $"Closing: really closing (reallyExiting={_reallyExiting}, runInBackground={Core.CenterSettings.RunInBackground}).");

                if (!resident) return;
                e.Cancel = true;
                Hide();
            };

            Closed += (_, __) =>
            {
                _trayListenCts?.Cancel();
                _trayIcon?.Dispose();

                // ⚠️ THE PROCESS DOES NOT END BY ITSELF HERE. A --background start sets
                // ShutdownMode.OnExplicitShutdown (there is no first window whose closing could end
                // the app), so reaching this point without shutting down leaves a process with no
                // window, no tray icon and a cancelled wake listener - and it still holds the
                // single-instance mutex. Every later launch then signals into a pipe nobody is
                // listening on and quietly exits, so the button does nothing, for ever, until
                // somebody finds the process in Task Manager.
                //
                // Measured on 2026-08-24: 272 MB, 26 threads, MainWindowHandle 0, no pipe.
                //
                // If the Closing handler above let the close through, residency was not wanted (or
                // could not be confirmed). Then this process has nothing left to do.
                Core.InstallLog.Write("Closed: window is gone - shutting the process down.");
                try { Application.Current?.Shutdown(); } catch { }
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

            // WITHOUT THIS THERE IS NO ICON. TaskbarIcon creates the actual Shell_NotifyIcon entry
            // from its Loaded event, and this one is built in code and never enters the visual tree,
            // so Loaded never fires. Everything else here - menu, tooltip, click handlers - is wired
            // to a notification icon that was never registered with the shell, which is exactly as
            // silent as it sounds: no exception, no icon.
            //
            // Efficiency mode explicitly OFF. It would put the process into EcoQoS while the window
            // is hidden, and hidden-but-resident is the entire point of this feature - the promise is
            // that the library is there instantly, not that the process is throttled while waiting.
            try { if (!icon.IsCreated) icon.ForceCreate(enablesEfficiencyMode: false); }
            catch (Exception ex) { Core.InstallLog.Write("Tray icon creation failed: " + ex.Message); }

            return icon;
        }

        /// <summary>
        /// The ClawTweaks button, both directions. Away only when the library is BOTH on screen and
        /// the window the user is actually looking at; anything else means "bring me the library".
        ///
        /// IsActive rather than IsVisible alone, and that distinction is the whole feature: a Center
        /// window sitting behind a fullscreen game is visible by every WPF measure. Hiding it there
        /// would make the button do nothing the user can see, and the second press - the one that
        /// should have brought the library back - would look like the first one that worked.
        ///
        /// Putting it away goes through Close(), not Hide(): the Closing handler above already knows
        /// what closing means on this machine (hide and stay resident, or actually exit). Calling
        /// Hide() here would quietly leave an invisible, unreachable process behind for anyone who
        /// has Run in background off.
        /// </summary>
        private void ToggleLibraryFromSignal()
        {
            bool showingLibrary = IsVisible
                                  && WindowState != WindowState.Minimized
                                  && IsActive
                                  && _view == View.Library;

            if (showingLibrary) { Close(); return; }

            BringToFront();
            if (_view != View.Library && LibraryAvailable) OpenLibrary();
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
