using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawTweaksCenter.Library.Accounts;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;
using QRCoder;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The store accounts achievements are read from. Reached from Library settings like the hidden
    /// games list, and it lives inside the settings screen the same way: _accountsOpen implies
    /// _settingsOpen. See Doku\ACHIEVEMENTS_Plan.md.
    ///
    /// One row per store that is BUILT - Steam today. A store appears here the day it works, not
    /// before: a row that says "coming soon" is a promise on a screen people open to get something
    /// done.
    ///
    /// Signing in to Steam is a QR code and nothing to type, which is the point on a handheld: the
    /// Steam app on the phone scans it and asks for a confirmation there.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _accountsOpen;
        private int _accountsIndex;
        private readonly System.Collections.Generic.List<Border> _accountsRows = new System.Collections.Generic.List<Border>();

        /// <summary>Non-null while the QR screen is up. Cancelling it ends the auth session and
        /// disconnects; every way off the screen goes through CancelSteamQr.</summary>
        private CancellationTokenSource _steamQrCts;
        private Image _steamQrImage;
        private TextBlock _steamQrStatus;

        /// <summary>The last thing that happened, shown on the list under the Steam row - "signed in",
        /// "Steam could not be reached". Cleared when the screen is opened again.</summary>
        private string _accountsNote;

        private const int AccountsSteamRow = 0;

        private void OpenAccounts()
        {
            _accountsOpen = true;
            _accountsIndex = 0;
            _accountsNote = null;
            RenderAccounts();
            RefreshActionBar();
        }

        private void CloseAccounts()
        {
            CancelSteamQr();
            _accountsOpen = false;
            _accountsRows.Clear();
            RenderLibrarySettings();
            RefreshActionBar();
        }

        /// <summary>Called by everything that takes the settings screen down at once, so a QR
        /// session never outlives the screen it was drawn on.</summary>
        private void ResetAccountsState()
        {
            CancelSteamQr();
            _accountsOpen = false;
            _accountsRows.Clear();
        }

        private void RenderAccounts()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _accountsRows.Clear();

            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Accounts"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            heading.Children.Add(TabEditorHint("Achievements come from the account instead of what Steam last saved on this device."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new StackPanel { Width = 460 };
            string value = SteamAccount.IsSignedIn
                ? SteamAccount.AccountName
                : Core.Loc.T("Not signed in");
            list.Children.Add(BuildAccountRow(AccountsSteamRow, "Steam", value, SteamAccount.IsSignedIn));
            if (!string.IsNullOrEmpty(_accountsNote))
            {
                list.Children.Add(new TextBlock
                {
                    Text = _accountsNote,
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 0, 0, 0),
                });
            }
            Grid.SetColumn(list, 1);
            columns.Children.Add(list);

            LibraryRoot.Children.Add(columns);
            ApplyAccountsSelection();
        }

        private Border BuildAccountRow(int index, string store, string value, bool signedIn)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = store,
                FontSize = 17,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var state = new TextBlock
            {
                Text = value,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = signedIn ? UiHelpers.Ok : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

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
            };
            row.MouseLeftButtonUp += (_, __) => { _accountsIndex = index; ActivateAccount(); };
            _accountsRows.Add(row);
            return row;
        }

        private void ApplyAccountsSelection()
        {
            foreach (var row in _accountsRows)
                row.BorderBrush = row.Tag is int i && i == _accountsIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveAccountsSelection(PadButton dir)
        {
            if (_steamQrCts != null || _accountsRows.Count == 0) return;
            int next = _accountsIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _accountsRows.Count || next == _accountsIndex) return;
            _accountsIndex = next;
            ApplyAccountsSelection();
            RefreshActionBar();
        }

        private void ActivateAccount()
        {
            if (_accountsIndex == AccountsSteamRow) StartSteamQr();
        }

        private void SignOutSelectedAccount()
        {
            if (_accountsIndex != AccountsSteamRow || !SteamAccount.IsSignedIn) return;
            SteamAccount.SignOut();
            Core.InstallLog.Write("[Accounts] Steam signed out by the user");
            _accountsNote = Core.Loc.T("Signed out. Achievements come from this device again.");
            RenderAccounts();
            RefreshActionBar();
        }

        private void AddAccountsActions()
        {
            if (_steamQrCts != null)
            {
                AddAction(PadButton.B, "Cancel", true, CancelSteamQrAndReturn);
                return;
            }
            bool signedIn = _accountsIndex == AccountsSteamRow && SteamAccount.IsSignedIn;
            AddAction(PadButton.A, signedIn ? "Sign in again" : "Sign in", true, ActivateAccount);
            // Y, not A: a sign-out that takes a phone to undo should not sit on the button that is
            // pressed most.
            if (signedIn) AddAction(PadButton.Y, "Sign out", true, SignOutSelectedAccount);
            AddAction(PadButton.B, "Back", true, CloseAccounts);
        }

        /// <summary>What the settings row shows without opening the screen.</summary>
        private static string AccountsSummary()
        {
            return SteamAccount.IsSignedIn ? "Steam" : Core.Loc.T("None");
        }

        // ── the account's achievements reaching the screen ──────────────────────────────────────

        /// <summary>Waits for the cursor to rest before asking for a game's list: scrolling past
        /// thirty covers should not be thirty requests.</summary>
        private System.Windows.Threading.DispatcherTimer _achPrefetchTimer;
        private Library.GameEntry _achPrefetchGame;

        private void HookAccountAchievements()
        {
            Library.Accounts.SteamAccountAchievements.Changed += appId =>
                Dispatcher.BeginInvoke(new Action(() => OnAccountAchievementsChanged(appId)));
        }

        private void SchedulePrefetchAchievements(Library.GameEntry g)
        {
            if (g == null || g.Store != Library.GameStore.Steam || !SteamAccount.IsSignedIn) return;
            _achPrefetchGame = g;
            if (_achPrefetchTimer == null)
            {
                _achPrefetchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _achPrefetchTimer.Tick += (_, __) =>
                {
                    _achPrefetchTimer.Stop();
                    Library.SteamAchievements.Prefetch(_achPrefetchGame);
                };
            }
            _achPrefetchTimer.Stop();
            _achPrefetchTimer.Start();
        }

        /// <summary>
        /// New numbers arrived. Redraws only what shows them and only when nothing is mid-gesture:
        /// the library line for the selected game, and the launch screen while it is still asking
        /// "Start X?" for that game. The open achievement list is left alone - it is a snapshot the
        /// user is scrolling, and it re-reads when it is opened again.
        /// </summary>
        private void OnAccountAchievementsChanged(string appId)
        {
            var selected = SelectedGame;
            bool forSelected = selected != null && selected.Store == Library.GameStore.Steam
                               && (appId == null || appId == selected.Id);
            if (forSelected && !LaunchOverlayOpen && !GameMenuOverlayOpen && !_settingsOpen) UpdateSelectedTitle();

            var target = _launchTarget;
            if (target != null && _launchPrompt == LaunchPrompt.Confirm && !GameMenuOverlayOpen
                && target.Store == Library.GameStore.Steam && (appId == null || appId == target.Id))
                RenderLaunchOverlay();
        }

        // ── the Steam QR screen ─────────────────────────────────────────────────────────────────

        private void StartSteamQr()
        {
            CancelSteamQr();
            var cts = new CancellationTokenSource();
            _steamQrCts = cts;
            RenderSteamQr();
            RefreshActionBar();
            _ = RunSteamQrAsync(cts);
        }

        private void RenderSteamQr()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _accountsRows.Clear();

            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 32, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Sign in to Steam"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            heading.Children.Add(TabEditorHint("Open the Steam app on your phone, tap the shield, and scan this code."));
            _steamQrStatus = new TextBlock
            {
                Text = Core.Loc.T("Connecting to Steam..."),
                FontSize = 15,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
            };
            heading.Children.Add(_steamQrStatus);
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            // Dark on WHITE whatever the theme: phone scanners are tuned for that, and an inverted
            // code fails on some of them. The quiet zone is part of the bitmap.
            _steamQrImage = new Image
            {
                Width = 300,
                Height = 300,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(_steamQrImage, BitmapScalingMode.NearestNeighbor);
            var frame = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8),
                Width = 316,
                Height = 316,
                Child = _steamQrImage,
            };
            Grid.SetColumn(frame, 1);
            columns.Children.Add(frame);

            LibraryRoot.Children.Add(columns);
        }

        private async Task RunSteamQrAsync(CancellationTokenSource cts)
        {
            string note;
            try
            {
                string account = await SteamAccount.SignInWithQrAsync(url =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_steamQrCts != cts || _steamQrImage == null) return;
                        _steamQrImage.Source = BuildQr(url);
                        _steamQrStatus.Text = Core.Loc.T("Waiting for your phone...");
                    }));
                }, cts.Token);
                note = Core.Loc.F("Signed in as {0}.", account);
                SteamAccountAchievements.RefreshProgressInBackground(force: true);
            }
            catch (OperationCanceledException)
            {
                return; // the user left the screen; whoever cancelled has already redrawn
            }
            catch (IOException)
            {
                note = Core.Loc.T("Steam could not be reached. Check the connection and try again.");
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Steam QR sign-in failed: " + ex.GetType().Name + ": " + ex.Message);
                // The code expires if nobody scans it, and a refusal on the phone ends up here too.
                note = Core.Loc.T("Sign-in did not complete. Press A to get a new code.");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_steamQrCts != cts) return;
                _steamQrCts = null;
                _steamQrImage = null;
                _steamQrStatus = null;
                if (!_accountsOpen) return;
                _accountsNote = note;
                RenderAccounts();
                RefreshActionBar();
            });
        }

        private void CancelSteamQr()
        {
            var cts = _steamQrCts;
            _steamQrCts = null;
            _steamQrImage = null;
            _steamQrStatus = null;
            // Cancelled, NOT disposed: the sign-in task still holds its token and is unwinding on a
            // worker thread. It disposes nothing either; the source is left to the GC.
            try { cts?.Cancel(); } catch { }
        }

        private void CancelSteamQrAndReturn()
        {
            CancelSteamQr();
            RenderAccounts();
            RefreshActionBar();
        }

        private static BitmapImage BuildQr(string url)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            byte[] png = new PngByteQRCode(data).GetGraphic(8);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
    }
}
