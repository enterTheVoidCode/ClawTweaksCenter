using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Gaming news from three RSS feeds, the last seven days only (user, 2026-10-03). Opened from
    /// the pill left of the achievement history's, on Recent's title row (Y). See Library\GamingNews.cs
    /// for the feeds, the seven-day rule and why older items are deleted.
    ///
    /// Built like the achievement history: it owns the library area, the tab strip goes, B comes
    /// back. What is stored shows at once; the feeds are asked in the background (at most every half
    /// hour) and the list is redrawn when they answer, keeping the cursor on the same article.
    /// A opens the article inside Center in reader view (CenterMenuWindow.NewsReader.cs), Y in Edge's
    /// reading view, X refreshes.
    ///
    /// ONE TAB PER SOURCE (user, 2026-10-03), walked with LT/RT like the Store tab's sections: This
    /// Week in Video Games first and on opening, then PC Gamer, then VideoCardz. PC Gamer alone
    /// posts 25 a day and would bury the other two in one mixed list. A SOURCE WITH NOTHING STORED
    /// HAS NO TAB (user, same day): This Week in Video Games sits behind Cloudflare, which answers
    /// Center with 403 on some days - its tab then simply is not there, and comes back with its
    /// first article.
    ///
    /// Y because Recent had no use for it: Y rescans the library everywhere else, silently, and
    /// Recent is the one tab that is not a store's shelf.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _newsOpen;
        private bool _newsLoading;
        private List<NewsItem> _news = new List<NewsItem>();
        private readonly List<Border> _newsRows = new List<Border>();
        private int _newsIndex;
        private CancellationTokenSource _newsCts;
        /// <summary>The source tab, an index into GamingNews.SourceNames.</summary>
        private int _newsSource;
        /// <summary>The articles of the current tab - what the rows and the index refer to.</summary>
        private List<NewsItem> _newsShown = new List<NewsItem>();

        /// <summary>Room left and right of the column (user, 2026-10-03: the cards ran to the
        /// screen edge).</summary>
        private const double NewsSideMargin = 56;

        private const double NewsImageWidth = 192;
        private const double NewsImageHeight = 108;

        /// <summary>The pill left of the history's: key and newspaper glyph, no words - the same
        /// shape as its neighbour.</summary>
        private UIElement BuildNewsPill()
        {
            var pill = BuildCornerChip("Y", "", "", OpenNews);
            if (pill is Border b)
            {
                b.Background = UiHelpers.Card;
                b.CornerRadius = new CornerRadius(16);
                b.Padding = new Thickness(6, 5, 4, 5);
                b.ToolTip = Core.Loc.T("Gaming news");
            }
            return pill;
        }

        private void OpenNews()
        {
            if (_launchPrompt != LaunchPrompt.None || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            if (_newsOpen || _achHistoryOpen || _friendsOpen || _infoOpen || _exitPromptOpen || _letterBarOpen) return;

            _newsOpen = true;
            _newsIndex = 0;
            _newsSource = 0;
            // What is on disk at once - purged to seven days on the way out of the file.
            try { _news = GamingNews.Stored(); }
            catch { _news = new List<NewsItem>(); }
            _newsLoading = _news.Count == 0;
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
            RefreshNews(force: false);
        }

        private void RefreshNews(bool force)
        {
            _newsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _newsCts = cts;
            _ = GamingNews.RefreshAsync(force, cts.Token).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_newsCts != cts || !_newsOpen) return;
                    _newsLoading = false;
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        // The cursor stays on the article it was on, wherever the new ones put it.
                        string current = _newsIndex < _newsShown.Count ? _newsShown[_newsIndex].Id : null;
                        _news = t.Result;
                        var shown = NewsOfSource();
                        int at = current == null ? -1 : shown.FindIndex(n => n.Id == current);
                        _newsIndex = at >= 0 ? at : Math.Min(_newsIndex, Math.Max(0, shown.Count - 1));
                    }
                    else if (t.IsFaulted) Core.InstallLog.Write("[News] refresh failed: " + t.Exception?.GetBaseException().GetType().Name);
                    RenderLibrary();
                    RefreshActionBar();
                }));
            }, TaskScheduler.Default);
        }

        private void CloseNews()
        {
            ResetNewsState();
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>Everything that takes the library down at once calls this.</summary>
        private void ResetNewsState()
        {
            _newsCts?.Cancel();
            _newsCts = null;
            _newsOpen = false;
            _newsLoading = false;
            _news = new List<NewsItem>();
            _newsShown = new List<NewsItem>();
            _newsRows.Clear();
            _newsIndex = 0;
            _newsSource = 0;
            ResetNewsReaderState();
        }

        /// <summary>The sources that have articles, in tab order. All of them while nothing is
        /// stored yet, so the first load does not draw an empty strip.</summary>
        private List<string> NewsTabs()
        {
            var withItems = GamingNews.SourceNames.Where(s => _news.Any(n => n.Source == s)).ToList();
            return withItems.Count > 0 ? withItems : GamingNews.SourceNames.ToList();
        }

        private string NewsSourceName
        {
            get
            {
                var tabs = NewsTabs();
                if (_newsSource >= tabs.Count) _newsSource = 0;
                return tabs[_newsSource];
            }
        }

        private List<NewsItem> NewsOfSource()
        {
            string source = NewsSourceName;
            return _news.Where(n => n.Source == source).ToList();
        }

        private void CycleNewsSource(int delta)
        {
            int count = NewsTabs().Count;
            if (count <= 1) return;
            _newsSource = ((_newsSource + delta) % count + count) % count;
            _newsIndex = 0;
            RenderLibrary();
            RefreshActionBar();
        }

        /// <summary>LT, the three sources with their counts, RT - the Store tab's strip.</summary>
        private UIElement BuildNewsSourceStrip()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var lt = (FrameworkElement)BuildKeyCap("LT");
            lt.Margin = new Thickness(0, 0, 10, 0);
            panel.Children.Add(lt);
            int i = 0;
            foreach (string name in NewsTabs())
            {
                int index = i++;
                int count = _news.Count(n => n.Source == name);
                var chip = BuildSystemChip(name, _newsLoading ? (int?)null : count, index == _newsSource);
                chip.MouseLeftButtonUp += (_, __) => { if (index != _newsSource) CycleNewsSource(index - _newsSource); };
                panel.Children.Add(chip);
            }
            var rt = (FrameworkElement)BuildKeyCap("RT");
            rt.Margin = new Thickness(2, 0, 0, 0);
            panel.Children.Add(rt);
            return panel;
        }

        private void RenderNews()
        {
            if (_newsReaderOpen) { RenderNewsReader(); return; }

            _liveRows.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _newsRows.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _newsShown = NewsOfSource();
            var head = new StackPanel { Margin = new Thickness(NewsSideMargin, 14, NewsSideMargin, 12), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Gaming news"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 4),
            });
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.F("The last 7 days from {0}.", string.Join(", ", GamingNews.SourceNames)),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });
            head.Children.Add(BuildNewsSourceStrip());
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            UIElement body;
            if (_newsLoading)
                body = HistoryWait();
            else if (_newsShown.Count == 0)
                body = HistoryEmpty(Core.Loc.T("No news from the last 7 days. The feeds could not be reached, or there is nothing new."));
            else
            {
                var list = new StackPanel { Margin = new Thickness(NewsSideMargin, 0, NewsSideMargin, 12), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
                for (int i = 0; i < _newsShown.Count; i++)
                {
                    var row = BuildNewsRow(_newsShown[i]);
                    row.Tag = i;
                    int captured = i;
                    row.MouseLeftButtonUp += (_, __) =>
                    {
                        if (_newsIndex == captured) OpenNewsReader(_newsShown[captured]);
                        else { _newsIndex = captured; ApplyNewsSelection(); }
                    };
                    _newsRows.Add(row);
                    list.Children.Add(row);
                }
                body = HistoryScroller(list);
            }
            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyNewsSelection();
            if (_newsIndex < _newsRows.Count)
            {
                var row = _newsRows[_newsIndex];
                Dispatcher.BeginInvoke(new Action(() => { try { row.BringIntoView(); } catch { } }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Picture left (16:9, a plate with the source's name when there is none), then the
        /// title, "PC Gamer · Today 14:05", and the summary in two lines.</summary>
        private Border BuildNewsRow(NewsItem n)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NewsImageWidth + 16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var plate = new Border
            {
                Width = NewsImageWidth,
                Height = NewsImageHeight,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            string path = GamingNews.ImagePath(n);
            if (path != null)
            {
                var image = new Image { Stretch = Stretch.UniformToFill };
                plate.Child = image;
                GameArt.LoadAsync(path, (int)(NewsImageWidth * 2)).ContinueWith(t =>
                {
                    var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (bmp != null) image.Dispatcher.BeginInvoke(new Action(() => image.Source = bmp));
                }, TaskScheduler.Default);
            }
            else
            {
                plate.Child = new TextBlock
                {
                    Text = n.Source,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            grid.Children.Add(plate);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            text.Children.Add(new TextBlock
            {
                Text = n.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 44,
            });
            text.Children.Add(new TextBlock
            {
                Text = n.Source + "  ·  " + NewsWhen(n.PublishedUtc),
                FontSize = 12,
                Foreground = UiHelpers.Accent,
                Margin = new Thickness(0, 3, 0, 3),
            });
            if (!string.IsNullOrEmpty(n.Summary))
                text.Children.Add(new TextBlock
                {
                    Text = n.Summary,
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 36,
                });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return new Border
            {
                Child = grid,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }

        /// <summary>"Today 14:05", "Yesterday 09:21", else the weekday and date - in the UI's culture.</summary>
        private static string NewsWhen(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var culture = CultureInfo.CurrentCulture;
            string time = local.ToString("t", culture);
            if (local.Date == DateTime.Today) return Core.Loc.F("Today {0}", time);
            if (local.Date == DateTime.Today.AddDays(-1)) return Core.Loc.F("Yesterday {0}", time);
            return local.ToString("ddd", culture) + ", " + local.ToString("M", culture) + ", " + time;
        }

        private void ApplyNewsSelection()
        {
            foreach (var row in _newsRows)
                row.BorderBrush = row.Tag is int i && i == _newsIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveNewsSelection(PadButton dir)
        {
            // In the reader the stick and the D-pad scroll the page (ScrollNewsReader), not a list.
            if (_newsReaderOpen || _newsRows.Count == 0) return;
            int next = _newsIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _newsRows.Count || next == _newsIndex) return;
            _newsIndex = next;
            ApplyNewsSelection();
            try { _newsRows[_newsIndex].BringIntoView(); } catch { }
        }

        private NewsItem SelectedNews => _newsIndex >= 0 && _newsIndex < _newsShown.Count ? _newsShown[_newsIndex] : null;

        private void AddNewsActions()
        {
            if (_newsReaderOpen) { AddNewsReaderActions(); return; }
            AddAction(PadButton.A, "Read", SelectedNews != null, () => OpenNewsReader(SelectedNews));
            AddAction(PadButton.X, "Refresh", !_newsLoading, () => { _newsLoading = _news.Count == 0; RenderLibrary(); RefreshNews(force: true); });
            AddAction(PadButton.Y, "Edge reader", SelectedNews != null, () => NewsReader.OpenInEdgeReader(SelectedNews?.Link));
            // Labelled in the strip, no chip - like the Store tab's sections.
            _liveActions[PadButton.LT] = () => CycleNewsSource(-1);
            _liveActions[PadButton.RT] = () => CycleNewsSource(1);
            AddAction(PadButton.B, "Back", true, CloseNews);
        }
    }
}
