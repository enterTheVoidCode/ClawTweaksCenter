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
    /// One row per store that is BUILT - Steam, Xbox and Epic today. A store appears here the day it works, not
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
        private const int AccountsXboxRow = 1;
        private const int AccountsEpicRow = 2;

        /// <summary>The big code beside the QR on the Xbox screen - Microsoft's page asks for it
        /// when the QR is not used.</summary>
        private TextBlock _signInCode;

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
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // The title over BOTH columns, and the boxes and the store rows below it starting on the
            // same line (user, 2026-10-03: the boxes sat lower than the stores).
            var title = new TextBlock
            {
                Text = Core.Loc.T("Accounts"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetColumnSpan(title, 2);
            columns.Children.Add(title);

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 28, 0) };
            Grid.SetRow(heading, 1);
            // Three small boxes rather than one sentence about Steam (user, 2026-10-03): what an
            // account brings, what Center sees of it, and how each one signs in.
            heading.Children.Add(BuildAccountsInfoBox("What an account brings",
                "Achievements and friend activity, purchased games, the wishlist and deals can only be read through the stores' own interfaces. More features will follow."));
            heading.Children.Add(BuildAccountsInfoBox("Your sign-in stays yours",
                "ClawTweaks never sees your password. You only grant access to selected areas such as friends, achievements and purchased games."));
            heading.Children.Add(BuildAccountsInfoBox("How to sign in",
                "Steam and Xbox take seconds with a QR code on your phone. Epic asks for your user name and password in the browser."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new StackPanel { Width = 460 };
            string value = SteamAccount.IsSignedIn
                ? SteamAccount.AccountName
                : Core.Loc.T("Not signed in");
            list.Children.Add(BuildAccountRow(AccountsSteamRow, "Steam", value, SteamAccount.IsSignedIn));
            // The gamertag only when XSTS handed one out; signed in without it still says so.
            string xbox = XboxAccount.IsSignedIn
                ? (string.IsNullOrEmpty(XboxAccount.Gamertag) ? Core.Loc.T("Signed in") : XboxAccount.Gamertag)
                : Core.Loc.T("Not signed in");
            list.Children.Add(BuildAccountRow(AccountsXboxRow, "Xbox", xbox, XboxAccount.IsSignedIn));
            string epic = EpicAccount.IsSignedIn
                ? (string.IsNullOrEmpty(EpicAccount.DisplayName) ? Core.Loc.T("Signed in") : EpicAccount.DisplayName)
                : Core.Loc.T("Not signed in");
            list.Children.Add(BuildAccountRow(AccountsEpicRow, "Epic Games", epic, EpicAccount.IsSignedIn));
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
            list.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(list, 1);
            Grid.SetColumn(list, 1);
            columns.Children.Add(list);

            LibraryRoot.Children.Add(columns);
            ApplyAccountsSelection();
        }

        private static Border BuildAccountsInfoBox(string title, string text)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(title),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3),
            });
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(text),
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });
            return new Border
            {
                Child = stack,
                Background = UiHelpers.Card,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
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
            else if (_accountsIndex == AccountsXboxRow) StartXboxSignIn();
            else if (_accountsIndex == AccountsEpicRow) StartEpicSignIn();
        }

        private bool SelectedAccountSignedIn =>
            _accountsIndex == AccountsSteamRow ? SteamAccount.IsSignedIn
            : _accountsIndex == AccountsXboxRow ? XboxAccount.IsSignedIn
            : _accountsIndex == AccountsEpicRow && EpicAccount.IsSignedIn;

        private void SignOutSelectedAccount()
        {
            if (!SelectedAccountSignedIn) return;
            if (_accountsIndex == AccountsSteamRow)
            {
                SteamAccount.SignOut();
                Core.InstallLog.Write("[Accounts] Steam signed out by the user");
                _accountsNote = Core.Loc.T("Signed out. Achievements come from this device again.");
            }
            else if (_accountsIndex == AccountsEpicRow)
            {
                EpicAccount.SignOut();
                Core.InstallLog.Write("[Accounts] Epic signed out by the user");
                _accountsNote = Core.Loc.T("Signed out of Epic Games.");
            }
            else
            {
                XboxAccount.SignOut();
                Core.InstallLog.Write("[Accounts] Xbox signed out by the user");
                // Xbox has no local achievements to fall back on - say that, not "from this device".
                _accountsNote = Core.Loc.T("Signed out of Xbox.");
            }
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
            bool signedIn = SelectedAccountSignedIn;
            AddAction(PadButton.A, signedIn ? "Sign in again" : "Sign in", true, ActivateAccount);
            // Y, not A: a sign-out that takes a phone to undo should not sit on the button that is
            // pressed most.
            if (signedIn) AddAction(PadButton.Y, "Sign out", true, SignOutSelectedAccount);
            AddAction(PadButton.B, "Back", true, CloseAccounts);
        }

        /// <summary>What the settings row shows without opening the screen.</summary>
        private static string AccountsSummary()
        {
            var names = new System.Collections.Generic.List<string>();
            if (SteamAccount.IsSignedIn) names.Add("Steam");
            if (XboxAccount.IsSignedIn) names.Add("Xbox");
            if (EpicAccount.IsSignedIn) names.Add("Epic Games");
            return names.Count > 0 ? string.Join(", ", names) : Core.Loc.T("None");
        }

        // ── the accounts corner on the friends and history screens ──────────────────────────────

        /// <summary>
        /// Top right on the friends screen and the achievement history (user, 2026-10-03): which
        /// stores are connected - each store's logo with a green or grey dot - and X to go straight to
        /// the accounts screen. As compact as it gets: it answers "why is Xbox missing here" without
        /// becoming the subject of the screen.
        /// </summary>
        private UIElement BuildAccountsCorner()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            void Add(Library.LibraryGroup group, string name, bool on)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), ToolTip = name };
                var logo = Library.StoreIcons.For(group);
                if (logo != null)
                    item.Children.Add(new Image { Source = logo, Width = 16, Height = 16, SnapsToDevicePixels = true, Opacity = on ? 1.0 : 0.45, VerticalAlignment = VerticalAlignment.Center });
                else
                    item.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = on ? UiHelpers.Text : UiHelpers.Subtle, VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = on ? UiHelpers.Ok : UiHelpers.Subtle,
                    Opacity = on ? 1.0 : 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0),
                });
                row.Children.Add(item);
            }
            Add(Library.LibraryGroup.Steam, "Steam", SteamAccount.IsSignedIn);
            Add(Library.LibraryGroup.Xbox, "Xbox", XboxAccount.IsSignedIn);
            Add(Library.LibraryGroup.Epic, "Epic Games", EpicAccount.IsSignedIn);

            row.Children.Add(BuildKeyCap("X"));
            row.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Accounts"),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            });
            row.MouseLeftButtonUp += (_, __) => OpenAccountsFromLibrary();

            return new Border
            {
                Child = row,
                Background = FooterPillBrush,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
        }

        /// <summary>X on the friends screen or the history: close it and open the accounts screen
        /// inside the settings. B from there walks back through the settings to the library.</summary>
        private void OpenAccountsFromLibrary()
        {
            if (_friendsOpen) CloseFriends();
            if (_achHistoryOpen) ResetAchievementHistoryState();
            OpenLibrarySettings();
            SelectSettingsRow(SettingsAccountsRow);
            OpenAccounts();
        }

        // ── the account's achievements reaching the screen ──────────────────────────────────────

        /// <summary>Waits for the cursor to rest before asking for a game's list: scrolling past
        /// thirty covers should not be thirty requests.</summary>
        private System.Windows.Threading.DispatcherTimer _achPrefetchTimer;
        private Library.GameEntry _achPrefetchGame;

        private void HookAccountAchievements()
        {
            Library.Accounts.SteamAccountAchievements.Changed += key =>
                Dispatcher.BeginInvoke(new Action(() => OnAccountAchievementsChanged(Library.GameStore.Steam, key)));
            Library.Accounts.XboxAccountAchievements.Changed += key =>
                Dispatcher.BeginInvoke(new Action(() => OnAccountAchievementsChanged(Library.GameStore.Xbox, key)));
            Library.Accounts.EpicAccountAchievements.Changed += key =>
                Dispatcher.BeginInvoke(new Action(() => OnAccountAchievementsChanged(Library.GameStore.Epic, key)));
        }

        private void SchedulePrefetchAchievements(Library.GameEntry g)
        {
            if (g == null) return;
            bool signedIn = g.Store == Library.GameStore.Steam ? SteamAccount.IsSignedIn
                          : g.Store == Library.GameStore.Xbox ? XboxAccount.IsSignedIn
                          : g.Store == Library.GameStore.Epic && EpicAccount.IsSignedIn;
            if (!signedIn) return;
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
        private void OnAccountAchievementsChanged(Library.GameStore store, string key)
        {
            // key: the appid (Steam), package family name (Xbox) or AppName (Epic) of one game, null
            // for a whole table.
            bool Concerns(Library.GameEntry g) => g != null && g.Store == store
                && (key == null || string.Equals(key, Library.SteamAchievements.AccountKeyOf(g), StringComparison.OrdinalIgnoreCase));

            if (Concerns(SelectedGame) && !LaunchOverlayOpen && !GameMenuOverlayOpen && !_settingsOpen) UpdateSelectedTitle();

            if (Concerns(_launchTarget) && _launchPrompt == LaunchPrompt.Confirm && !GameMenuOverlayOpen)
                RenderLaunchOverlay();
        }

        // ── the Steam QR screen ─────────────────────────────────────────────────────────────────

        private void StartSteamQr()
        {
            CancelSteamQr();
            var cts = new CancellationTokenSource();
            _steamQrCts = cts;
            RenderSignInScreen("Sign in to Steam", "Open the Steam app on your phone, tap the shield, and scan this code.",
                "Connecting to Steam...", showCode: false);
            RefreshActionBar();
            _ = RunSteamQrAsync(cts);
        }

        /// <summary>The QR sign-in screen, for either store: heading and hint on the left, status
        /// under them, the code on white on the right - with the typed code under it when the store
        /// has one.</summary>
        private void RenderSignInScreen(string title, string hint, string status, bool showCode)
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
                Text = Core.Loc.T(title),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            heading.Children.Add(TabEditorHint(hint));
            if (showCode)
            {
                // Big and spaced: it is read off the screen and typed on a phone.
                _signInCode = new TextBlock
                {
                    Text = "········",
                    FontSize = 34,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
                    Foreground = UiHelpers.Text,
                    Margin = new Thickness(0, 14, 0, 0),
                };
                heading.Children.Add(_signInCode);
            }
            _steamQrStatus = new TextBlock
            {
                Text = Core.Loc.T(status),
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

        // ── the Xbox sign-in screen ─────────────────────────────────────────────────────────────
        // Same screen and the same fields as Steam's: one sign-in at a time, and every way off it
        // goes through CancelSteamQr. The difference is the code: Microsoft's device flow shows a
        // page and a code, so the QR carries the page with the code filled in, and the code stands
        // beside it for whoever types instead of scanning.

        private void StartXboxSignIn()
        {
            CancelSteamQr();
            var cts = new CancellationTokenSource();
            _steamQrCts = cts;
            RenderSignInScreen("Sign in to Xbox",
                "Scan the QR code with your phone or open microsoft.com/link, enter the code below there, then sign in with your Xbox account.",
                "Asking Microsoft for a code...", showCode: true);
            RefreshActionBar();
            _ = RunXboxSignInAsync(cts);
        }

        private async Task RunXboxSignInAsync(CancellationTokenSource cts)
        {
            string note;
            try
            {
                string gamertag = await XboxAccount.SignInAsync(code =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_steamQrCts != cts || _steamQrImage == null) return;
                        _steamQrImage.Source = BuildQr(code.VerificationUri);
                        if (_signInCode != null) _signInCode.Text = code.UserCode;
                        _steamQrStatus.Text = Core.Loc.T("Waiting for your phone...");
                    }));
                }, cts.Token);
                note = string.IsNullOrEmpty(gamertag) ? Core.Loc.T("Signed in to Xbox.") : Core.Loc.F("Signed in as {0}.", gamertag);
                XboxAccountAchievements.RefreshTitlesInBackground(force: true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Xbox sign-in failed: " + ex.GetType().Name + ": " + ex.Message);
                // "Not reachable" only for a real network failure. A service that ANSWERED with a
                // refusal (XSTS 400 + XErr) was reachable, and saying otherwise sent the user
                // looking at their Wi-Fi (2026-10-03).
                bool answered = ex.Message.Contains(" answered ");
                note = ex is TimeoutException || ex is InvalidOperationException || answered
                    ? Core.Loc.T("Sign-in did not complete. Press A to get a new code.")
                    : Core.Loc.T("Microsoft could not be reached. Check the connection and try again.");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_steamQrCts != cts) return;
                _steamQrCts = null;
                _steamQrImage = null;
                _steamQrStatus = null;
                _signInCode = null;
                if (!_accountsOpen) return;
                _accountsNote = note;
                RenderAccounts();
                RefreshActionBar();
            });
        }

        private void CancelSteamQr()
        {
            StopEpicClipboardWatch();
            _signInCode = null;
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
