using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Turns a news article into a reader page (user, 2026-10-03: "direkt in der App, im Reader-Modus").
    ///
    /// SmartReader (Mozilla Readability, ported) pulls the article out of the page - measured on 3 PC
    /// Gamer and 3 VideoCardz articles, all readable, 320-1150 words, images with real https sources,
    /// 0.1-1.8 s. The result goes into an HTML page of Center's own: dark, large type, one column,
    /// and a Content-Security-Policy that allows NO script at all, so nothing the article carried can
    /// run. The page itself scrolls with the left stick through a loop CenterMenuWindow.NewsReader.cs
    /// injects (host-injected scripts are not bound by the page's CSP).
    ///
    /// Edge's own reading view is not available inside WebView2, which is why this exists; the
    /// footer offers Edge's (read: URL) as the other way.
    /// </summary>
    public static class NewsReader
    {
        private static readonly HttpClient Http = CreateHttp();

        public sealed class Page
        {
            public bool Readable;
            public string Html;
        }

        /// <summary>The reader page for an article, or Readable = false when the article cannot be
        /// pulled out (the caller then shows the original page). Never throws for a network fault.</summary>
        public static async Task<Page> LoadAsync(NewsItem item, CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string html = await Http.GetStringAsync(item.Link, ct).ConfigureAwait(false);
                var article = await Task.Run(() => new SmartReader.Reader(item.Link, html).GetArticle(), ct).ConfigureAwait(false);
                if (article == null || !article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
                {
                    Core.InstallLog.Write("[News] reader: not readable (" + item.Source + "), original page instead");
                    return new Page { Readable = false };
                }
                Core.InstallLog.Write("[News] reader: " + item.Source + " article in " + sw.ElapsedMilliseconds + " ms");
                return new Page { Readable = true, Html = Build(item, article.Title, article.Content) };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[News] reader failed (" + item.Source + "): " + ex.GetType().Name);
                return new Page { Readable = false };
            }
        }

        /// <summary>Edge with its own reading view on the article: "read:" in front of the address
        /// is what Edge's address bar takes for that. Falls back to the plain page in the default
        /// browser when Edge is not where Windows says it is.</summary>
        public static void OpenInEdgeReader(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string edge = null;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                    edge = key?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(edge) && System.IO.File.Exists(edge))
                {
                    var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
                    psi.ArgumentList.Add("read:" + url);
                    Process.Start(psi);
                    return;
                }
            }
            catch (Exception ex) { Core.InstallLog.Write("[News] Edge reader failed: " + ex.GetType().Name); }
            Core.PrerequisiteGuide.OpenPage(url, m => Core.InstallLog.Write(m));
        }

        private static string Build(NewsItem item, string title, string content)
        {
            string when = item.PublishedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            var b = new StringBuilder();
            b.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
            // No script, no frames, no forms, no plugins. Pictures and video posters over https only.
            b.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https: data:; media-src https:; style-src 'unsafe-inline'; font-src 'none'\">");
            b.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            b.Append("<base href=\"").Append(WebUtility.HtmlEncode(item.Link)).Append("\">");
            b.Append("<style>").Append(Css).Append("</style></head><body><main>");
            b.Append("<div class=\"meta\">").Append(WebUtility.HtmlEncode(item.Source)).Append(" &middot; ").Append(WebUtility.HtmlEncode(when)).Append("</div>");
            b.Append("<h1>").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? item.Title : title)).Append("</h1>");
            b.Append(content);
            b.Append("</main></body></html>");
            return b.ToString();
        }

        // Center's dark palette (App.xaml): background #202020, card #2B2B2B, accent #60CDFF, subtle
        // #A0A0A0. Type at 21 px - it is read from arm's length on an 8" screen.
        private const string Css = @"
html { background:#202020; }
body { margin:0; background:#202020; color:#ECECEC; font: 21px/1.65 'Segoe UI Variable Text','Segoe UI',sans-serif; }
main { max-width: 860px; margin: 0 auto; padding: 36px 48px 120px; }
h1 { font-size: 34px; line-height:1.25; margin: 6px 0 22px; color:#FFFFFF; font-weight:600; }
h2, h3, h4 { color:#FFFFFF; line-height:1.3; margin: 1.4em 0 .5em; font-weight:600; }
.meta { color:#A0A0A0; font-size:15px; letter-spacing:.02em; }
p { margin: 0 0 1em; }
a { color:#60CDFF; text-decoration:none; pointer-events:none; }
img, video, picture { max-width:100%; height:auto; border-radius:8px; display:block; margin: 18px auto; }
figure { margin: 22px 0; } figcaption { color:#A0A0A0; font-size:15px; margin-top:6px; }
blockquote { margin: 1em 0; padding: 4px 20px; border-left: 3px solid #3A3A3A; color:#CFCFCF; }
ul, ol { padding-left: 1.3em; } li { margin: .3em 0; }
table { border-collapse: collapse; width:100%; font-size:17px; } td, th { border:1px solid #333; padding:6px 10px; }
pre, code { background:#2B2B2B; border-radius:6px; font-size:16px; } pre { padding:12px; overflow-x:auto; }
iframe, script, form, button, input { display:none !important; }
::-webkit-scrollbar { width: 8px; } ::-webkit-scrollbar-thumb { background:#3A3A3A; border-radius:4px; }
";

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
            // The article pages answer a browser; the feed fetch identifies as Center, the page fetch
            // is what the WebView2 would send anyway.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en,de;q=0.8");
            return http;
        }
    }
}
