using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The Start-button menu for whichever game is focused in the library: favorite it, or give it a
    /// different cover from SteamGridDB.
    ///
    /// Two screens, same shape as the Misc overlay next to it - a small menu, and (for cover art) a
    /// second screen underneath one of its rows. Start was reserved for exactly this while the Misc
    /// tab was being built - see the CLAUDE.md note from that session.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private enum GameMenuOverlay
        {
            None,
            /// <summary>Favorite toggle + "Choose cover art…".</summary>
            Menu,
            /// <summary>Search box (pre-filled with the title) + a grid of portrait covers to pick from.</summary>
            ArtPicker,
        }

        // Fixed column count for the art picker grid - unlike the library's own grid it does not need
        // to adapt to window width, so the same number drives both the layout and the D-pad math.
        private const int ArtPickerColumns = 4;
        private const double ArtPickerTileWidth = 130;
        private const double ArtPickerTileHeight = 195;

        private GameMenuOverlay _gameMenuOverlay;
        // Captured at open time rather than re-read from SelectedGame throughout: the overlay owns the
        // screen, so nothing else can move the library selection while it is up, but reading through
        // one field is one less place that assumption has to hold.
        private GameEntry _gameMenuTarget;
        private int _gameMenuIndex;
        private readonly List<Border> _gameMenuRows = new List<Border>();

        // Art picker state. -1 selects the search box itself; 0.. selects a result tile.
        private TextBox _artPickerQueryBox;
        // The box is torn down and rebuilt on every re-render (a search landing, a pick failing), so
        // whatever the user typed has to be held somewhere that survives that - this is that somewhere.
        // Seeded with the game's title when the picker opens, updated from the box's own text the
        // moment a search actually runs.
        private string _artPickerQueryText = string.Empty;
        private int _artPickerIndex = -1;
        private List<ArtCandidate> _artPickerResults = new List<ArtCandidate>();
        private readonly List<Border> _artPickerTiles = new List<Border>();
        private ScrollViewer _artPickerScroller;
        private bool _artPickerSearching;
        private bool _artPickerSearched;   // distinguishes "never searched" from "searched, nothing found"
        private bool _artPickerApplying;   // downloading + committing the picked candidate
        private CancellationTokenSource _artPickerCts;

        private bool GameMenuOverlayOpen => _gameMenuOverlay != GameMenuOverlay.None;

        #region Entry and exit
        private void OpenGameMenu()
        {
            if (_closeTimer != null || _settingsOpen || MiscOverlayOpen) return;
            var target = SelectedGame;
            if (target == null) return;

            _gameMenuTarget = target;
            _gameMenuOverlay = GameMenuOverlay.Menu;
            _gameMenuIndex = 0;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void CloseGameMenuOverlay()
        {
            _artPickerCts?.Cancel();
            _artPickerCts = null;
            _gameMenuOverlay = GameMenuOverlay.None;
            _gameMenuTarget = null;
            _gameMenuRows.Clear();
            ResetArtPickerState();
            RenderLibrary();
            // Favoriting can have just created or emptied the tab, and choosing art can have just
            // filled in the picture the tab strip's own chip drawing does not read from anywhere else
            // - the strip needs a fresh look either way.
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void ResetArtPickerState()
        {
            _artPickerQueryBox = null;
            _artPickerIndex = -1;
            _artPickerResults = new List<ArtCandidate>();
            _artPickerTiles.Clear();
            _artPickerScroller = null;
            _artPickerSearching = false;
            _artPickerSearched = false;
            _artPickerApplying = false;
        }

        /// <summary>B: one step back, not all the way out - leaving the art picker should land back
        /// on the game's own menu, the same way leaving Misc's app list lands back on its sources
        /// menu.</summary>
        private void GameMenuBack()
        {
            if (_gameMenuOverlay == GameMenuOverlay.ArtPicker)
            {
                _artPickerCts?.Cancel();
                _artPickerCts = null;
                ResetArtPickerState();
                _gameMenuOverlay = GameMenuOverlay.Menu;
                RenderGameMenuOverlay();
                RefreshActionBar();
                return;
            }
            CloseGameMenuOverlay();
        }
        #endregion

        #region Rendering — menu
        /// <summary>
        /// The ONLY entry point that may put anything in LibraryRoot for this overlay - every render
        /// in this file, including a search landing or a pick finishing, must come back through here
        /// rather than calling RenderGameMenuMenu/RenderArtPicker directly.
        ///
        /// Without that rule this bled through to the screen: RenderArtPicker used to be called
        /// directly from three places (open, search-start, search-done), none of which cleared
        /// anything first. Each call APPENDED a fresh Auto+Star row pair and a fresh head/body to
        /// LibraryRoot on top of whatever the previous call had already drawn - three renders left the
        /// title, the search box, the spinner and the results grid all stacked in the same two grid
        /// cells, and worse, three "Star" rows meant the one row actually holding content only got a
        /// THIRD of the available height. That is where the "only ~20% of the cover art" screenshot
        /// came from - it was never a fetch problem.
        /// </summary>
        private void RenderGameMenuOverlay()
        {
            if (LibraryRoot == null) return;
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _gameMenuRows.Clear();
            _artPickerTiles.Clear();
            // Dropped with the tiles, not just alongside them: RenderArtPicker only assigns a new one
            // on the has-results path, so a "searching"/"nothing found" render would otherwise leave
            // the previous, now-detached ScrollViewer here for the next scroll call to talk to.
            _artPickerScroller = null;

            switch (_gameMenuOverlay)
            {
                case GameMenuOverlay.Menu: RenderGameMenuMenu(); break;
                case GameMenuOverlay.ArtPicker: RenderArtPicker(); break;
            }
        }

        private void RenderGameMenuMenu()
        {
            var game = _gameMenuTarget;
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = game?.Title ?? string.Empty,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 16),
            });

            bool isFav = game?.IsFavorite == true;
            stack.Children.Add(GameMenuRow(0,
                isFav ? "Remove from Favorites" : "Add to Favorites",
                null,
                isFav ? UiHelpers.Ok : UiHelpers.Text));

            bool hasKey = Library.SteamGridDb.HasKey;
            stack.Children.Add(GameMenuRow(1, "Choose cover art…",
                hasKey ? "Search SteamGridDB for a different cover" : "Set a SteamGridDB key in Settings first",
                UiHelpers.Text));

            LibraryRoot.Children.Add(stack);
            ApplyGameMenuSelection();
        }

        /// <summary>Same visual shape as MiscRow / BuildSettingRow, kept as its own copy rather than
        /// shared: each of the three menus wires its row clicks to its own index/activate pair, and a
        /// shared builder parameterized for all three would carry more plumbing than the four lines of
        /// XAML it saves.</summary>
        private Border GameMenuRow(int index, string title, string subtitle, Brush titleBrush)
        {
            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = title, FontSize = 18, Foreground = titleBrush });
            if (subtitle != null)
                left.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(left);

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
                MinWidth = 560,
            };
            int captured = index;
            row.MouseLeftButtonUp += (_, __) => { _gameMenuIndex = captured; ActivateGameMenuRow(); };
            _gameMenuRows.Add(row);
            return row;
        }

        private void ApplyGameMenuSelection()
        {
            foreach (var row in _gameMenuRows)
                row.BorderBrush = row.Tag is int i && i == _gameMenuIndex ? UiHelpers.Accent : Brushes.Transparent;
        }
        #endregion

        #region Navigation — menu
        private void MoveGameMenuSelection(PadButton dir)
        {
            if (_gameMenuOverlay == GameMenuOverlay.ArtPicker) { MoveArtPickerSelection(dir); return; }
            if (_gameMenuRows.Count == 0) return;

            int next = _gameMenuIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _gameMenuRows.Count || next == _gameMenuIndex) return;
            _gameMenuIndex = next;
            ApplyGameMenuSelection();
            RefreshActionBar();
        }

        private void ActivateGameMenuRow()
        {
            switch (_gameMenuIndex)
            {
                case 0:
                    FavoritesStore.Toggle(_gameMenuTarget);
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                    return;
                case 1:
                    if (Library.SteamGridDb.HasKey) OpenArtPicker();
                    return;
            }
        }
        #endregion

        #region Art picker
        private void OpenArtPicker()
        {
            _gameMenuOverlay = GameMenuOverlay.ArtPicker;
            ResetArtPickerState();
            _artPickerQueryText = _gameMenuTarget?.Title ?? string.Empty;
            RenderGameMenuOverlay();
            RefreshActionBar();
            RunArtSearch(_artPickerQueryText);
        }

        private void RenderArtPicker()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 10), MaxWidth = 720 };
            head.Children.Add(new TextBlock
            {
                Text = "Choose cover art",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 10),
            });

            _artPickerQueryBox = new TextBox
            {
                // The field, not the game's title directly - RunArtPickerSearch captures whatever the
                // user typed into it before this box gets torn down and rebuilt, otherwise every
                // search result landing snapped the visible text back to the original title.
                Text = _artPickerQueryText,
                FontSize = 16,
                Padding = new Thickness(8, 6, 8, 6),
            };
            head.Children.Add(_artPickerQueryBox);
            head.Children.Add(new TextBlock
            {
                Text = "Portrait covers only. No match? Edit the text above and search again.",
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(2, 6, 0, 0),
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            UIElement body;
            if (_artPickerSearching || _artPickerApplying)
            {
                body = BuildLibraryMessage(_artPickerApplying ? "Setting cover…" : "Searching…", true);
            }
            else if (!_artPickerSearched)
            {
                body = new Grid(); // the auto-search fired from OpenArtPicker; nothing to show yet
            }
            else if (_artPickerResults.Count == 0)
            {
                body = BuildLibraryMessage("No portrait covers found for that search.", false);
            }
            else
            {
                var grid = new UniformGrid { Columns = ArtPickerColumns, Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12) };
                for (int i = 0; i < _artPickerResults.Count; i++) grid.Children.Add(BuildArtCandidateTile(i));
                _artPickerScroller = new ScrollViewer
                {
                    Content = grid,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                };
                body = _artPickerScroller;
            }
            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyArtPickerSelection();
        }

        private Border BuildArtCandidateTile(int index)
        {
            var candidate = _artPickerResults[index];
            var image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var tile = new Border
            {
                Width = ArtPickerTileWidth,
                Height = ArtPickerTileHeight,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                Margin = new Thickness(6),
                Child = image,
            };
            int captured = index;
            tile.MouseLeftButtonUp += (_, __) => { _artPickerIndex = captured; ApplyPickedArt(); };
            _artPickerTiles.Add(tile);

            // The thumbnail, when SteamGridDB provided one, else the full image - downloading a full
            // 600x900 cover just to show a picker tile would multiply the request count by however
            // many candidates came back.
            //
            // LoadRemoteAsync, NOT LoadAsync. The two are not interchangeable and this is the exact
            // spot that got it wrong: LoadAsync hands the URL to BitmapImage.UriSource, which for an
            // http(s) source starts an async download that makes Freeze throw - every tile stayed
            // grey. See the comment on LoadRemoteAsync for the full story.
            string source = candidate.Thumb ?? candidate.Url;
            image.Tag = source;
            GameArt.LoadRemoteAsync(source, (int)ArtPickerTileWidth * 2).ContinueWith(t =>
            {
                var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (bmp == null) return;
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(image.Tag, source)) return;
                    image.Source = bmp;
                }));
            }, TaskScheduler.Default);

            return tile;
        }

        private void ApplyArtPickerSelection()
        {
            foreach (var tile in _artPickerTiles)
                tile.BorderBrush = tile.Tag is int i && i == _artPickerIndex ? UiHelpers.Accent : Brushes.Transparent;
            if (_artPickerQueryBox != null)
                _artPickerQueryBox.BorderBrush = _artPickerIndex == -1 ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveArtPickerSelection(PadButton dir)
        {
            if (_artPickerIndex == -1)
            {
                if (dir == PadButton.Down && _artPickerTiles.Count > 0) _artPickerIndex = 0;
                else return;
            }
            else
            {
                int next = _artPickerIndex;
                switch (dir)
                {
                    case PadButton.Left: if (next % ArtPickerColumns == 0) return; next -= 1; break;
                    case PadButton.Right:
                        if (next % ArtPickerColumns == ArtPickerColumns - 1 || next + 1 >= _artPickerTiles.Count) return;
                        next += 1;
                        break;
                    case PadButton.Up:
                        if (next < ArtPickerColumns) { _artPickerIndex = -1; ApplyArtPickerSelection(); RefreshActionBar(); return; }
                        next -= ArtPickerColumns;
                        break;
                    case PadButton.Down:
                        next += ArtPickerColumns;
                        if (next >= _artPickerTiles.Count)
                        {
                            // Clamp into the last, partially filled row rather than refusing to move.
                            // Straight down from the second column of the last full row would
                            // otherwise be a dead end whenever the final row is short.
                            if (_artPickerIndex >= _artPickerTiles.Count - 1) return;
                            next = _artPickerTiles.Count - 1;
                        }
                        break;
                    default: return;
                }
                _artPickerIndex = next;
            }
            ApplyArtPickerSelection();
            ScrollArtPickerSelectionIntoView();
            RefreshActionBar();
        }

        /// <summary>
        /// Keeps the cursor on screen while moving through the results.
        ///
        /// This was missing entirely, which is why the picker looked like it stopped at the bottom of
        /// the visible rows: the selection DID move on, invisibly, and every further press moved it
        /// further out of sight. Same fix, and same reason, as the Misc app list next door.
        ///
        /// Works because the results grid is a plain UniformGrid - every tile is a realised element,
        /// so BringIntoView has something real to scroll to. It would not be reliable on a
        /// virtualising list.
        /// </summary>
        private void ScrollArtPickerSelectionIntoView()
        {
            try
            {
                // Back at the search box: scroll the results to the top instead, so the grid does not
                // stay parked halfway down behind a box the user is now typing in.
                if (_artPickerIndex < 0) { _artPickerScroller?.ScrollToTop(); return; }
                if (_artPickerIndex < _artPickerTiles.Count) _artPickerTiles[_artPickerIndex].BringIntoView();
            }
            catch { }
        }

        /// <summary>X, from anywhere in the picker: runs the search with whatever the box currently
        /// holds. One button with one meaning regardless of where the cursor sits is simpler than
        /// making the query box a precondition for pressing it.</summary>
        private void RunArtPickerSearch()
        {
            // Captured into the field BEFORE the render call below tears this exact box down -
            // RenderArtPicker rebuilds it fresh every time, so this is the only moment the user's
            // edit exists anywhere but on screen.
            _artPickerQueryText = _artPickerQueryBox?.Text ?? _artPickerQueryText;
            RunArtSearch(_artPickerQueryText);
        }

        private void RunArtSearch(string query)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length == 0) return;

            _artPickerCts?.Cancel();
            _artPickerCts = new CancellationTokenSource();
            var ct = _artPickerCts.Token;

            _artPickerSearching = true;
            _artPickerSearched = false;
            _artPickerIndex = -1;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(async () =>
            {
                IReadOnlyList<ArtCandidate> results;
                try { results = await Library.SteamGridDb.SearchArtAsync(query, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("SteamGridDB art search failed: " + ex.Message);
                    results = Array.Empty<ArtCandidate>();
                }

                Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested || _gameMenuOverlay != GameMenuOverlay.ArtPicker) return;
                    _artPickerResults = new List<ArtCandidate>(results);
                    _artPickerSearching = false;
                    _artPickerSearched = true;
                    _artPickerIndex = _artPickerResults.Count > 0 ? 0 : -1;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                });
            }, ct);
        }

        /// <summary>A, on a result tile: downloads it, records it as this game's override, and returns
        /// to the game's own menu with the new cover already showing.</summary>
        private void ApplyPickedArt()
        {
            if (_artPickerApplying) return;
            if (_artPickerIndex < 0 || _artPickerIndex >= _artPickerResults.Count) return;
            var candidate = _artPickerResults[_artPickerIndex];
            var target = _gameMenuTarget;
            if (target == null) return;

            _artPickerCts?.Cancel();
            _artPickerCts = new CancellationTokenSource();
            var ct = _artPickerCts.Token;

            _artPickerApplying = true;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(async () =>
            {
                string path = null;
                try { path = await Library.SteamGridDb.DownloadForOverrideAsync(candidate, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Core.InstallLog.Write("SteamGridDB cover download failed: " + ex.Message); }

                Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _artPickerApplying = false;
                    if (path != null)
                    {
                        ArtOverrideStore.Set(target, path);
                        _gameMenuOverlay = GameMenuOverlay.Menu;
                        ResetArtPickerState();
                        RenderGameMenuOverlay();
                        RefreshActionBar();
                    }
                    else
                    {
                        RenderGameMenuOverlay();
                        RefreshActionBar();
                    }
                });
            }, ct);
        }
        #endregion

        #region Footer
        private bool RefreshGameMenuActionBar()
        {
            switch (_gameMenuOverlay)
            {
                case GameMenuOverlay.Menu:
                    AddAction(PadButton.A, "Select", true, ActivateGameMenuRow);
                    AddAction(PadButton.B, "Back", true, GameMenuBack);
                    return true;

                case GameMenuOverlay.ArtPicker:
                    if (_artPickerIndex >= 0)
                        AddAction(PadButton.A, "Set as cover", !_artPickerApplying, ApplyPickedArt);
                    else
                        AddAction(PadButton.A, "Edit search", true, () => { _artPickerQueryBox?.Focus(); _artPickerQueryBox?.SelectAll(); });
                    AddAction(PadButton.X, "Search", !_artPickerSearching && !_artPickerApplying, RunArtPickerSearch);
                    AddAction(PadButton.B, "Back", true, GameMenuBack);
                    return true;

                default:
                    return false;
            }
        }
        #endregion
    }
}
