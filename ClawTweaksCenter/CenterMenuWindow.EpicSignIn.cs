using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClawTweaksCenter.Library.Accounts;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The Epic sign-in: Epic's login in the user's OWN browser, in place of the QR the other two
    /// stores use - see Library\Accounts\EpicAccount.cs for why there is no QR.
    ///
    /// THE BROWSER, NOT A PAGE INSIDE CENTER (2026-10-03). An embedded page (WebView2) was built
    /// first and dropped: signing in through Apple with a passkey on an iPhone did not offer the
    /// phone there, while the user's browser did - and the browser brings their passkeys, password
    /// manager and any Epic session they already have.
    ///
    /// The login ends on Epic's redirect page, which shows the authorization code as JSON. The user
    /// copies that page; Center takes the code off the clipboard, clears it, and trades it for
    /// tokens. Nothing is typed, and no port is opened to receive the code.
    ///
    /// Runs under the same fields as the QR screens (_steamQrCts, _steamQrStatus), so every way off
    /// the screen - B, leaving settings - goes through CancelSteamQr, which stops the watch.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private System.Windows.Threading.DispatcherTimer _epicClipboardTimer;
        private bool _epicCodeTaken;

        private void StartEpicSignIn()
        {
            CancelSteamQr();
            var cts = new CancellationTokenSource();
            _steamQrCts = cts;
            _epicCodeTaken = false;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EpicAccount.LoginUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Epic sign-in could not open the browser: " + ex.GetType().Name);
                FinishEpicSignIn(cts, Core.Loc.T("Sign-in did not complete. Press A to try again."));
                return;
            }

            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _accountsRows.Clear();
            var heading = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Sign in to Epic Games"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 10),
            });
            heading.Children.Add(TabEditorHint("Sign in in the browser that just opened. When Epic shows a page with a code, select all of it and copy it (Ctrl+A, Ctrl+C). Center picks the code up by itself."));
            _steamQrStatus = new TextBlock
            {
                Text = Core.Loc.T("Waiting for the copied code..."),
                FontSize = 15,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
            };
            heading.Children.Add(_steamQrStatus);
            LibraryRoot.Children.Add(heading);
            RefreshActionBar();

            // The clipboard is read on the UI thread (it needs STA), once a second, and only while
            // this screen is up.
            _epicClipboardTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _epicClipboardTimer.Tick += (_, __) =>
            {
                if (_steamQrCts != cts) { StopEpicClipboardWatch(); return; }
                string text;
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                catch { return; } // another program holds the clipboard; next tick
                string code = EpicAccount.ExtractAuthorizationCode(text);
                if (code == null) return;
                StopEpicClipboardWatch();
                // The code is single-use and redeemed at once, but it should not sit on the
                // clipboard to be pasted somewhere by accident.
                try { if (Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
                TakeEpicCode(cts, code);
            };
            _epicClipboardTimer.Start();
        }

        /// <summary>The code is in hand: trade it for tokens.</summary>
        private void TakeEpicCode(CancellationTokenSource cts, string code)
        {
            if (code == null || _epicCodeTaken || _steamQrCts != cts) return;
            _epicCodeTaken = true;
            if (_steamQrStatus != null) _steamQrStatus.Text = Core.Loc.T("Signing in...");
            _ = RedeemEpicCodeAsync(cts, code);
        }

        private async Task RedeemEpicCodeAsync(CancellationTokenSource cts, string code)
        {
            string note;
            try
            {
                string name = await EpicAccount.SignInWithCodeAsync(code, cts.Token);
                note = string.IsNullOrEmpty(name) ? Core.Loc.T("Signed in to Epic Games.") : Core.Loc.F("Signed in as {0}.", name);
                EpicAccountAchievements.RefreshInBackground(force: true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Accounts] Epic sign-in failed: " + ex.GetType().Name + ": " + ex.Message);
                // An answer from Epic (an expired or reused code) is a retry; no answer is the network.
                note = ex is System.IO.IOException && ex.Message.Contains(" answered ")
                    ? Core.Loc.T("Sign-in did not complete. Press A to try again.")
                    : Core.Loc.T("Epic Games could not be reached. Check the connection and try again.");
            }
            FinishEpicSignIn(cts, note);
        }

        private void FinishEpicSignIn(CancellationTokenSource cts, string note)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_steamQrCts != cts) return;
                StopEpicClipboardWatch();
                _steamQrCts = null;
                _steamQrStatus = null;
                if (!_accountsOpen) return;
                _accountsNote = note;
                RenderAccounts();
                RefreshActionBar();
            });
        }

        private void StopEpicClipboardWatch()
        {
            _epicClipboardTimer?.Stop();
            _epicClipboardTimer = null;
        }
    }
}
