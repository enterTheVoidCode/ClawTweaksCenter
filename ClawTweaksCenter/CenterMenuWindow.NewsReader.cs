using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The news article inside Center (user, 2026-10-03): A on an article opens it here, in reader
    /// view (Library\NewsReader.cs builds the page), in a WebView2 that fills the library area.
    ///
    /// SMOOTH WITH THE LEFT STICK. The navigator reports the stick (and the D-pad) as a scroll amount
    /// about 25 times a second; stepping the page by that would judder. So Center turns it into a
    /// SPEED and hands only that to the page, where a requestAnimationFrame loop - injected by the
    /// host, so the reader page's no-script policy does not apply to it - moves the page every frame.
    /// The response is curved: a light push reads, a full push skims. With no report for 120 ms the
    /// page stops by itself, so a lost message can never leave it scrolling.
    ///
    ///   X  switches between reader view and the original page (the original runs its own scripts,
    ///      as in any browser; the reader page runs none).
    ///   Y  opens the article in Edge's own reading view.
    ///   B  back to the list.
    ///
    /// A new WebView2 per article, disposed on the way out: an HwndHost sits above every WPF element
    /// in its rectangle, and one left behind - hidden or not - is how a stale page ends up over the
    /// next screen. The browser environment (its profile folder) is created once and shared.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _newsReaderOpen;
        private bool _newsReaderLoading;
        private bool _newsReaderOriginal;
        private NewsItem _newsReaderItem;
        private WebView2 _newsWeb;
        private Grid _newsReaderHost;
        private CancellationTokenSource _newsReaderCts;

        private static Task<CoreWebView2Environment> _webEnvironment;

        /// <summary>Full speed in px/s, reached at a full push (or the D-pad).</summary>
        private const double ReaderMaxSpeed = 2200;

        private static Task<CoreWebView2Environment> WebEnvironment()
        {
            // Center's own profile folder, never the user's Edge profile.
            return _webEnvironment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks", "Center", "WebView2"));
        }

        private const string ScrollLoopScript = @"(function(){
  if (window.__ctw) return;
  var v = 0, t = 0, last = performance.now();
  window.__ctw = function(nv){ v = nv; t = performance.now(); };
  function f(now){
    var dt = Math.min(0.05, (now - last) / 1000); last = now;
    if (now - t > 120) v = 0;
    if (v) window.scrollBy(0, v * dt);
    requestAnimationFrame(f);
  }
  requestAnimationFrame(f);
})();";

        private void OpenNewsReader(NewsItem item)
        {
            if (item == null || _newsReaderOpen) return;
            _newsReaderOpen = true;
            _newsReaderLoading = true;
            _newsReaderOriginal = false;
            _newsReaderItem = item;
            RenderLibrary();
            RefreshActionBar();
            _ = LoadNewsReaderAsync(item, original: false);
        }

        private async Task LoadNewsReaderAsync(NewsItem item, bool original)
        {
            _newsReaderCts?.Cancel();
            var cts = new CancellationTokenSource();
            _newsReaderCts = cts;
            try
            {
                var web = await EnsureNewsWebAsync().ConfigureAwait(true);
                if (_newsReaderCts != cts || !_newsReaderOpen || web == null) return;

                if (!original)
                {
                    var page = await NewsReader.LoadAsync(item, cts.Token).ConfigureAwait(true);
                    if (_newsReaderCts != cts || !_newsReaderOpen) return;
                    if (page.Readable)
                    {
                        _newsReaderOriginal = false;
                        web.CoreWebView2.NavigateToString(page.Html);
                        return;
                    }
                }
                // The original page: asked for, or the article could not be pulled out of it.
                _newsReaderOriginal = true;
                web.CoreWebView2.Navigate(item.Link);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // No WebView2 runtime, or it would not start: the browser is the way that still works.
                Core.InstallLog.Write("[News] reader view unavailable: " + ex.GetType().Name + ": " + ex.Message);
                if (_newsReaderCts == cts && _newsReaderOpen)
                {
                    CloseNewsReader();
                    Core.PrerequisiteGuide.OpenPage(item.Link, m => Core.InstallLog.Write(m));
                }
            }
            finally { if (_newsReaderCts == cts) RefreshActionBar(); }
        }

        /// <summary>The WebView2 for this article, created and set up on first use.</summary>
        private async Task<WebView2> EnsureNewsWebAsync()
        {
            if (_newsWeb?.CoreWebView2 != null) return _newsWeb;
            if (_newsWeb == null)
            {
                _newsWeb = new WebView2
                {
                    DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x20, 0x20, 0x20),
                    Visibility = Visibility.Hidden,
                };
                PlaceNewsWeb();
            }
            var web = _newsWeb;
            await web.EnsureCoreWebView2Async(await WebEnvironment().ConfigureAwait(true)).ConfigureAwait(true);
            if (web != _newsWeb) return null;

            var s = web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ScrollLoopScript).ConfigureAwait(true);

            // In reader view nothing leaves the page; on the original page links work, but in place -
            // a second window would be a browser Center cannot steer.
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!_newsReaderOriginal && e.IsUserInitiated) e.Cancel = true;
            };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (_newsReaderOriginal && e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) web.CoreWebView2.Navigate(e.Uri);
            };
            web.CoreWebView2.NavigationCompleted += (_, __) =>
            {
                if (web != _newsWeb) return;
                _newsReaderLoading = false;
                web.Visibility = Visibility.Visible;
                if (_newsReaderHost?.Children.Count > 1) _newsReaderHost.Children[1].Visibility = Visibility.Collapsed;   // the spinner
                RefreshActionBar();
            };
            return web;
        }

        /// <summary>Puts the WebView2 and the spinner behind it into the library area.</summary>
        private void PlaceNewsWeb()
        {
            if (_newsReaderHost == null || _newsWeb == null) return;
            if (_newsWeb.Parent is Panel old && old != _newsReaderHost) old.Children.Remove(_newsWeb);
            if (_newsWeb.Parent == null) _newsReaderHost.Children.Insert(0, _newsWeb);
        }

        private void RenderNewsReader()
        {
            // Drawn once per article. A redraw from elsewhere (an achievement refresh, a language
            // change) must not take the WebView2 out of the tree and put it back - that recreates its
            // window and loses the page.
            if (_newsReaderHost != null && LibraryRoot.Children.Contains(_newsReaderHost)) return;

            _liveRows.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _newsRows.Clear();

            _newsReaderHost = new Grid { Margin = new Thickness(NewsSideMargin, 10, NewsSideMargin, 8) };
            var wait = HistoryWait();
            _newsReaderHost.Children.Add(wait);
            LibraryRoot.Children.Add(_newsReaderHost);
            PlaceNewsWeb();
            if (_newsWeb != null && !_newsReaderLoading) wait.Visibility = Visibility.Collapsed;
        }

        private void CloseNewsReader()
        {
            ResetNewsReaderState();
            RenderLibrary();
            RefreshActionBar();
        }

        /// <summary>Takes the WebView2 down for good. Called on B, and by everything that closes the
        /// news screen or the library.</summary>
        private void ResetNewsReaderState()
        {
            _newsReaderCts?.Cancel();
            _newsReaderCts = null;
            _newsReaderOpen = false;
            _newsReaderLoading = false;
            _newsReaderOriginal = false;
            _newsReaderItem = null;
            var web = _newsWeb;
            _newsWeb = null;
            if (web != null)
            {
                if (web.Parent is Panel p) p.Children.Remove(web);
                try { web.Dispose(); } catch { }
            }
            if (_newsReaderHost != null) LibraryRoot.Children.Remove(_newsReaderHost);
            _newsReaderHost = null;
        }

        /// <summary>The navigator's scroll report (stick or D-pad, ~25 per second) as a speed for the
        /// page's own loop.</summary>
        private void ScrollNewsReader(double amount)
        {
            if (!_newsReaderOpen || _newsWeb?.CoreWebView2 == null) return;
            // 46 is the navigator's full deflection per tick. Curved, so small pushes are fine control.
            double x = Math.Max(-1, Math.Min(1, amount / 46.0));
            double speed = Math.Sign(x) * Math.Pow(Math.Abs(x), 1.7) * ReaderMaxSpeed;
            try { _ = _newsWeb.CoreWebView2.ExecuteScriptAsync("window.__ctw&&window.__ctw(" + speed.ToString("0.0", CultureInfo.InvariantCulture) + ")"); }
            catch { }
        }

        private void ToggleNewsReaderOriginal()
        {
            if (_newsReaderItem == null || _newsReaderLoading) return;
            _newsReaderLoading = true;
            if (_newsWeb != null) _newsWeb.Visibility = Visibility.Hidden;
            if (_newsReaderHost?.Children.Count > 1) _newsReaderHost.Children[1].Visibility = Visibility.Visible;
            RefreshActionBar();
            _ = LoadNewsReaderAsync(_newsReaderItem, original: !_newsReaderOriginal);
        }

        private void AddNewsReaderActions()
        {
            AddAction(PadButton.X, _newsReaderOriginal ? "Reader view" : "Original page", !_newsReaderLoading, ToggleNewsReaderOriginal);
            AddAction(PadButton.Y, "Edge reader", _newsReaderItem != null, () => NewsReader.OpenInEdgeReader(_newsReaderItem?.Link));
            AddAction(PadButton.B, "Back", true, CloseNewsReader);
        }
    }
}
