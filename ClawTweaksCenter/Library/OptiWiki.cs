using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// OptiScaler's per-game wiki page, fetched and reduced to key/value rows.
    ///
    /// ── Why parsing this mechanically is safe ────────────────────────────────────────────────────
    /// The pages are not prose. Every per-game page is a two-column AsciiDoc table with the same
    /// small key set. A page that does not look like that yields nothing and says so, rather than
    /// rendering half a table.
    ///
    /// ── THE FILE EXTENSION IS .asciidoc, NOT .md ─────────────────────────────────────────────────
    /// The obvious URL is the wrong one: the two INDEX pages are Markdown, the per-game pages are
    /// AsciiDoc, and asking for the wrong extension returns a plain 404 that reads exactly like "no
    /// such game". Both are tried, AsciiDoc first.
    ///
    /// ── Provenance ───────────────────────────────────────────────────────────────────────────────
    /// The panel names the wiki as its source. The OptiScaler repository is GPL-3.0; its WIKI states
    /// no licence, which is a question worth answering before this content travels further than
    /// being displayed with attribution.
    ///
    /// The same reduction runs in the ClawTweaks widget. It is deliberately duplicated rather than
    /// shared: the two applications are separate repositories with no common assembly, and the thing
    /// being parsed is somebody else's document format, not our contract.
    /// </summary>
    public static class OptiWiki
    {
        /// <summary>Rows worth showing, in this order. The rest of the table is maintainer
        /// bookkeeping ("Reported By") or empty more often than not.</summary>
        private static readonly string[] Keys =
        {
            "Upscaler Inputs", "FG Inputs", "Filename", "Last Tested Version",
            "Settings", "FG-Settings", "Known Issues", "Notes",
        };

        /// <summary>Pages parsed this session. The wiki changes on the order of days and a panel is
        /// reopened constantly, so one fetch per page per session.</summary>
        private static readonly Dictionary<string, List<KeyValuePair<string, string>>> Cache =
            new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pages that answered with nothing usable. Cached too - retrying a 404 on every
        /// redraw would be a request per second for a page that does not exist.</summary>
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        public static string BrowserUrl(string page)
            => "https://github.com/optiscaler/OptiScaler/wiki/" + Uri.EscapeDataString(page ?? string.Empty);

        /// <summary>The rows for one page, or null when it could not be read. Never throws.</summary>
        public static async Task<List<KeyValuePair<string, string>>> GetAsync(string page, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(page)) return null;

            lock (Gate)
            {
                if (Cache.TryGetValue(page, out var cached)) return cached;
                if (Misses.Contains(page)) return null;
            }

            var rows = await FetchAsync(page, ct).ConfigureAwait(false);

            lock (Gate)
            {
                if (rows != null && rows.Count > 0) Cache[page] = rows;
                else Misses.Add(page);
            }
            return rows != null && rows.Count > 0 ? rows : null;
        }

        private static async Task<List<KeyValuePair<string, string>>> FetchAsync(string page, CancellationToken ct)
        {
            foreach (string ext in new[] { ".asciidoc", ".md" })
            {
                try
                {
                    string url = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/"
                                 + Uri.EscapeDataString(page) + ext;

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");

                    string text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var rows = ParseRows(text);
                    if (rows.Count > 0) return rows;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    // A 404 on .asciidoc is the NORMAL first answer for the handful of Markdown
                    // pages, so this is not an error - the loop simply tries the other extension.
                    Core.InstallLog.Write("[OptiWiki] '" + page + ext + "' not usable: " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// Pulls the key/value rows out of one page.
        ///
        /// The shape being parsed, verbatim from the wiki:
        /// <code>
        /// |**Upscaler Inputs**
        /// |DLSS
        ///
        /// |**Known Issues**
        /// a|
        /// * first bullet
        /// ** nested
        /// </code>
        /// A key line is <c>|**Name**</c>; everything up to the next key line is the value. That is
        /// why this is a state machine and not one regex per row - the <c>a|</c> blocks run to many
        /// lines and carry their own list markup.
        /// </summary>
        private static List<KeyValuePair<string, string>> ParseRows(string text)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(text)) return rows;

            var wanted = new HashSet<string>(Keys, StringComparer.OrdinalIgnoreCase);
            string key = null;
            var value = new StringBuilder();

            void Flush()
            {
                if (key != null && wanted.Contains(key))
                {
                    string v = ToPlainText(value.ToString());
                    // "-" is how the wiki writes "nothing to say here". A row whose value is a dash
                    // costs space and answers nothing.
                    if (v.Length > 0 && v != "-") rows.Add(new KeyValuePair<string, string>(key, v));
                }
                key = null;
                value.Clear();
            }

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();

                if (line.StartsWith("|**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal))
                {
                    Flush();
                    key = line.Substring(3, line.Length - 5).Trim();
                    continue;
                }

                if (key == null) continue;
                if (line == "|===" || line.StartsWith("[cols", StringComparison.Ordinal)) { Flush(); continue; }

                // The cell markers themselves carry no text.
                if (line == "a|" || line == "|") continue;
                if (line.StartsWith("|", StringComparison.Ordinal)) line = line.Substring(1);

                value.Append(line).Append('\n');
            }
            Flush();

            // The caller's order rather than the page's, so every game's panel reads the same way
            // regardless of how its page happens to be laid out.
            rows.Sort((a, b) => Array.IndexOf(Keys, a.Key).CompareTo(Array.IndexOf(Keys, b.Key)));
            return rows;
        }

        /// <summary>
        /// AsciiDoc down to plain text: bullets become "- ", nested bullets indent, links keep their
        /// LABEL and lose their URL, inline emphasis goes.
        ///
        /// Deliberately lossy and deliberately small. The alternative is an AsciiDoc renderer, and
        /// what is worth showing here is a handful of short facts plus a caveat paragraph - the
        /// browser button is one press away for anyone who needs the whole page.
        /// </summary>
        private static string ToPlainText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            var lines = new List<string>();
            foreach (string raw in s.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line == "---") continue;

                // "**" / "***" are nesting depth, not emphasis, at the start of a line.
                int depth = 0;
                while (depth < line.Length && line[depth] == '*') depth++;
                bool bullet = depth > 0 && depth < line.Length && line[depth] == ' ';
                if (bullet) line = line.Substring(depth).Trim();

                line = Regex.Replace(line, @"https?://\S*?\[([^\]]*)\]", "$1");   // url[label] -> label
                line = Regex.Replace(line, @"https?://\S+", string.Empty);        // bare urls left over
                line = line.Replace("**", string.Empty).Replace("`", string.Empty)
                           .Replace("```ini", string.Empty).Replace("```", string.Empty);

                // Two things the wiki uses that survive the pass above, both measured on real pages:
                // AsciiDoc passthrough ("+++<s>text</s>+++", which is how the authors strike text
                // out) and single-asterisk emphasis ("*is crashing*"). Rendered as-is they read as
                // corruption rather than as markup. Bullets were already consumed above, so a lone
                // asterisk pair here can only be emphasis.
                line = line.Replace("+++", string.Empty);
                line = Regex.Replace(line, @"</?[a-zA-Z][a-zA-Z0-9]*\s*/?>", string.Empty);
                line = Regex.Replace(line, @"\*([^*]+)\*", "$1");
                line = line.Trim();
                if (line.Length == 0) continue;

                lines.Add(bullet ? new string(' ', Math.Max(0, (depth - 1) * 2)) + "• " + line : line);
            }

            return string.Join("\n", lines).Trim();
        }
    }
}
