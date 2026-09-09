using System;
using System.Collections.Generic;
using System.IO;
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
    /// Pictures the user brought themselves: a cover for one game, and the background of the whole
    /// Center window.
    ///
    /// -- The shape, and why it is not a file dialog ---------------------------------------------
    /// The user names a FOLDER once (Library settings, or the first time they pick a cover) and
    /// everything underneath it becomes a grid of tiles the D-pad walks - the same grid the
    /// SteamGridDB picker already uses. Center is a gamepad surface; a Windows file dialog is a
    /// mouse, and Misc's "browse for an exe" only gets away with it because a portable tool that is
    /// in no list has no other route. Choosing art is something people do again and again, and a
    /// mouse-only step in that loop is a step that does not get taken.
    ///
    /// It runs as two more GameMenuOverlay states rather than a top-level overlay of its own, and
    /// that is deliberate: the library already routes rendering, D-pad movement, the action bar and
    /// B through GameMenuOverlayOpen at eight separate call sites. A ninth kind of overlay would
    /// have to be added to every one of them, and the one that got missed would be a screen the pad
    /// walks straight past. See the memory note on explicit registries swallowing new code.
    ///
    /// The background picker is opened from Library settings and has NO game. It parks _settingsOpen
    /// for the duration instead of running alongside it, because the settings screen wins those same
    /// routing checks (MoveSelection tests _settingsOpen first) - so leaving it set would give the
    /// picker a screen it cannot steer.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private enum UserArtPurpose
        {
            /// <summary>A cover for _gameMenuTarget.</summary>
            Cover,
            /// <summary>The picture behind the whole window.</summary>
            Background,
        }

        // Portrait, exactly like the SteamGridDB picker: whatever the file happens to be, it is going
        // into a 2:3 tile, and showing it in that shape here is what stops a pick from looking
        // different once it is on the shelf.
        private const int UserArtCoverColumns = 5;
        private const double UserArtCoverTileWidth = 130;
        private const double UserArtCoverTileHeight = 195;

        // Landscape for the background, and fewer per row: it is going behind a 16:10 window, so a
        // portrait thumbnail would promise a framing the window cannot give it.
        private const int UserArtBackgroundColumns = 4;
        private const double UserArtBackgroundTileWidth = 208;
        private const double UserArtBackgroundTileHeight = 117;

        /// <summary>
        /// What the background is decoded to. Fixed rather than read from the window: this runs
        /// before the first layout pass on startup, where ActualWidth is still 0, and a background
        /// decoded to nothing is a background that never appears. 1920 is the panel this is built
        /// for, and over-decoding by a few hundred pixels costs memory once.
        /// </summary>
        private const int BackgroundDecodeWidth = 1920;

        /// <summary>
        /// How much of the background is covered by the window's own colour.
        ///
        /// A FIXED NUMBER, not a setting (user, 2026-09-09). Every label, chip and tile in Center is
        /// drawn for a dark flat background, and a picture is whatever the user happens to have -
        /// a bright screenshot behind the footer makes the button hints unreadable. One value that
        /// always works beats a slider that lets someone make the app illegible and then not know
        /// why.
        /// </summary>
        private const double BackgroundScrimOpacity = 0.65;

        private UserArtPurpose _userArtPurpose;
        /// <summary>Where B goes back to. The picker is reachable from the game menu and from Library
        /// settings, and those are different screens rather than two doors into one.</summary>
        private bool _userArtFromSettings;

        /// <summary>Set when the caller only wanted the FOLDER named (the Library settings row), so
        /// choosing one goes back instead of opening a grid of pictures nobody asked to see.</summary>
        private bool _userArtFolderOnly;

        private List<string> _userArtImages = new List<string>();
        private readonly List<Border> _userArtTiles = new List<Border>();
        private int _userArtIndex;
        private ScrollViewer _userArtScroller;
        private bool _userArtScanning;
        private bool _userArtScanned;
        private bool _userArtApplying;
        private CancellationTokenSource _userArtCts;

        private readonly List<Border> _userArtFolderRows = new List<Border>();
        private readonly List<Action> _userArtFolderActions = new List<Action>();
        private int _userArtFolderIndex;

        /// <summary>The sentinel that stands for "no background at all" in the tile list. An empty
        /// string can never be a file path, so nothing else can collide with it.</summary>
        private const string UserArtNoneSentinel = "";

        #region Entry and exit
        /// <summary>
        /// Opens the picker. Lands on the folder chooser when no folder has been named yet, so the
        /// first cover somebody picks is also where the folder gets set up - nobody has to find the
        /// settings screen first to use a feature they just discovered.
        /// </summary>
        private void OpenUserArtPicker(UserArtPurpose purpose, bool folderOnly = false)
        {
            _userArtPurpose = purpose;
            _userArtFromSettings = _settingsOpen;
            // Parked, not left running: the settings screen owns the D-pad while it is set.
            _settingsOpen = false;

            // AFTER the reset, not before: ResetUserArtState clears this flag along with everything
            // else, so setting it first would hand the settings row a full picture grid instead of
            // the folder chooser it asked for.
            ResetUserArtState();
            _userArtFolderOnly = folderOnly;
            _gameMenuOverlay = !folderOnly && UserImageLibrary.HasFolder
                ? GameMenuOverlay.UserArt
                : GameMenuOverlay.UserArtFolder;
            RenderGameMenuOverlay();
            RefreshActionBar();
            if (_gameMenuOverlay == GameMenuOverlay.UserArt) StartUserArtScan();
        }

        private void ResetUserArtState()
        {
            _userArtCts?.Cancel();
            _userArtCts = null;
            _userArtImages = new List<string>();
            _userArtTiles.Clear();
            _userArtFolderRows.Clear();
            _userArtFolderActions.Clear();
            _userArtIndex = 0;
            _userArtFolderIndex = 0;
            _userArtScroller = null;
            _userArtScanning = false;
            _userArtScanned = false;
            _userArtApplying = false;
            _userArtFolderOnly = false;
        }

        /// <summary>B, from either of the two picker screens: back to whichever screen opened it.</summary>
        private void UserArtBack()
        {
            bool toSettings = _userArtFromSettings;
            ResetUserArtState();

            if (toSettings)
            {
                _gameMenuOverlay = GameMenuOverlay.None;
                _gameMenuTarget = null;
                _settingsOpen = true;
                RenderLibrarySettings();
                RefreshActionBar();
                return;
            }

            _gameMenuOverlay = GameMenuOverlay.Menu;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }
        #endregion

        #region Folder chooser
        private void RenderUserArtFolder()
        {
            _userArtFolderRows.Clear();
            _userArtFolderActions.Clear();

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
                MinWidth = 560,
            };
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Where are your pictures?"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Pick a folder once. Sub-folders are included."),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
            });

            var suggestions = UserImageLibrary.Suggestions();
            foreach (var suggestion in suggestions)
            {
                string path = suggestion.Path;
                stack.Children.Add(UserArtFolderRow("\uE8B7", suggestion.Label, path, () => ChooseUserArtFolder(path)));
            }

            if (suggestions.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("No suggestions on this machine."),
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            // The mouse escape hatch, and it is LAST for a reason: it is the only row here that
            // cannot be driven with the pad, so it is the answer for the folder that is not one of
            // the three above rather than the way this is meant to be used. Same trade-off as Misc's
            // "browse for an exe", and the same placement.
            stack.Children.Add(UserArtFolderRow("\uE838", "Browse\u2026",
                Core.Loc.T("Needs the mouse or the touchscreen"), BrowseForUserArtFolder));

            LibraryRoot.Children.Add(stack);
            ApplyUserArtFolderSelection();
        }

        private Border UserArtFolderRow(string glyph, string title, string subtitle, Action run)
        {
            int index = _userArtFolderActions.Count;
            _userArtFolderActions.Add(run);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = Core.Loc.T(title), FontSize = 18, Foreground = UiHelpers.Text });
            left.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(left, 1);

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = UiHelpers.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(icon);
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
            };
            int captured = index;
            row.MouseLeftButtonUp += (_, __) => { _userArtFolderIndex = captured; ActivateUserArtFolderRow(); };
            _userArtFolderRows.Add(row);
            return row;
        }

        private void ApplyUserArtFolderSelection()
        {
            foreach (var row in _userArtFolderRows)
                row.BorderBrush = row.Tag is int i && i == _userArtFolderIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveUserArtFolderSelection(PadButton dir)
        {
            if (_userArtFolderRows.Count == 0) return;
            int next = _userArtFolderIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _userArtFolderRows.Count || next == _userArtFolderIndex) return;
            _userArtFolderIndex = next;
            ApplyUserArtFolderSelection();
            RefreshActionBar();
        }

        private void ActivateUserArtFolderRow()
        {
            if (_userArtFolderIndex < 0 || _userArtFolderIndex >= _userArtFolderActions.Count) return;
            _userArtFolderActions[_userArtFolderIndex]?.Invoke();
        }

        private void ChooseUserArtFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Core.CenterSettings.UserImageFolder = path;
            Core.InstallLog.Write("[UserArt] image folder set to " + path);

            // Named from Library settings: that row asked for a folder and nothing else, so going
            // straight into a grid of pictures would be answering a question nobody asked.
            if (_userArtFolderOnly) { UserArtBack(); return; }

            bool folderOnly = _userArtFolderOnly;
            ResetUserArtState();
            _userArtFolderOnly = folderOnly;
            _gameMenuOverlay = GameMenuOverlay.UserArt;
            RenderGameMenuOverlay();
            RefreshActionBar();
            StartUserArtScan();
        }

        /// <summary>
        /// The Windows folder dialog, for a folder that is none of the three suggestions.
        ///
        /// A DIALOG IS THE FALLBACK HERE, NOT THE FEATURE. It is a mouse surface on a machine that is
        /// usually held in two hands - which is the whole reason this screen exists - but a path that
        /// nothing can guess needs either this or a keyboard, and this is the shorter of the two.
        /// </summary>
        private void BrowseForUserArtFolder()
        {
            string picked = null;
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = Core.Loc.T("Where are your pictures?"),
                    Multiselect = false,
                };
                if (dialog.ShowDialog(this) == true) picked = dialog.FolderName;
            }
            catch (Exception ex) { Core.InstallLog.Write("[UserArt] folder dialog failed: " + ex.Message); }

            if (!string.IsNullOrWhiteSpace(picked)) ChooseUserArtFolder(picked);
        }
        #endregion

        #region Image grid
        /// <summary>
        /// Walks the folder off the UI thread.
        ///
        /// OFF-THREAD IS NOT OPTIONAL. The folder is whatever the user named - a downloads tree with
        /// years in it is a normal answer - and doing that walk inline would freeze the window for as
        /// long as it takes, which reads as Center hanging on the button press.
        /// </summary>
        private void StartUserArtScan()
        {
            _userArtCts?.Cancel();
            _userArtCts = new CancellationTokenSource();
            var ct = _userArtCts.Token;

            _userArtScanning = true;
            _userArtScanned = false;
            _userArtImages = new List<string>();
            _userArtIndex = 0;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(() =>
            {
                var found = UserImageLibrary.Scan();
                Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested || _gameMenuOverlay != GameMenuOverlay.UserArt) return;
                    _userArtImages = found;
                    // "No background" rides in front of the pictures rather than on a button of its
                    // own: it is the same decision as picking one, so it belongs in the same list.
                    if (_userArtPurpose == UserArtPurpose.Background && HasBackgroundImage)
                        _userArtImages.Insert(0, UserArtNoneSentinel);
                    _userArtScanning = false;
                    _userArtScanned = true;
                    _userArtIndex = 0;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                });
            }, ct);
        }

        private int UserArtColumns =>
            _userArtPurpose == UserArtPurpose.Background ? UserArtBackgroundColumns : UserArtCoverColumns;

        private void RenderUserArtGrid()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 10), MaxWidth = 900 };
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(_userArtPurpose == UserArtPurpose.Background ? "Choose a background" : "Choose one of your images"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 4),
            });
            head.Children.Add(new TextBlock
            {
                Text = UserImageLibrary.Folder,
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            UIElement body;
            if (_userArtScanning || _userArtApplying)
            {
                body = BuildLibraryMessage(_userArtApplying ? "Setting the picture…" : "Looking for pictures…", true);
            }
            else if (!_userArtScanned)
            {
                body = new Grid();
            }
            else if (_userArtImages.Count == 0)
            {
                // Names the folder it looked in. "No pictures found" on its own is the message people
                // read as broken; with the path underneath it is a fact they can act on.
                body = BuildLibraryMessage("No JPG, PNG or BMP files in that folder.", false);
            }
            else
            {
                var grid = new UniformGrid
                {
                    Columns = UserArtColumns,
                    Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12),
                };
                for (int i = 0; i < _userArtImages.Count; i++) grid.Children.Add(BuildUserArtTile(i));
                _userArtScroller = new ScrollViewer
                {
                    Content = grid,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                };
                body = _userArtScroller;
            }
            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyUserArtSelection();
        }

        private Border BuildUserArtTile(int index)
        {
            string path = _userArtImages[index];
            bool isNone = path.Length == 0;
            bool background = _userArtPurpose == UserArtPurpose.Background;

            var tile = new Border
            {
                Width = background ? UserArtBackgroundTileWidth : UserArtCoverTileWidth,
                Height = background ? UserArtBackgroundTileHeight : UserArtCoverTileHeight,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                Margin = new Thickness(6),
            };

            if (isNone)
            {
                tile.Child = new TextBlock
                {
                    Text = Core.Loc.T("No background"),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                };
            }
            else
            {
                var image = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                tile.Child = image;

                // LoadAsync, NOT LoadRemoteAsync - the mirror image of the trap next door. These are
                // files on disk, where BitmapImage.UriSource decodes synchronously and Freeze is
                // legal; the remote path exists only because an http source starts an async download
                // instead.
                image.Tag = path;
                GameArt.LoadAsync(path, (int)tile.Width * 2).ContinueWith(t =>
                {
                    var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (bmp == null) return;
                    image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(image.Tag, path)) return;
                        image.Source = bmp;
                    }));
                }, TaskScheduler.Default);
            }

            int captured = index;
            tile.MouseLeftButtonUp += (_, __) => { _userArtIndex = captured; ApplyUserArtSelection(); ApplyPickedUserArt(); };
            _userArtTiles.Add(tile);
            return tile;
        }

        private void ApplyUserArtSelection()
        {
            foreach (var tile in _userArtTiles)
                tile.BorderBrush = tile.Tag is int i && i == _userArtIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveUserArtSelection(PadButton dir)
        {
            if (_userArtTiles.Count == 0) return;
            int columns = UserArtColumns;
            int next = _userArtIndex;
            switch (dir)
            {
                case PadButton.Left: if (next % columns == 0) return; next -= 1; break;
                case PadButton.Right:
                    if (next % columns == columns - 1 || next + 1 >= _userArtTiles.Count) return;
                    next += 1;
                    break;
                case PadButton.Up:
                    if (next < columns) return;
                    next -= columns;
                    break;
                case PadButton.Down:
                    next += columns;
                    // Clamp into a short last row rather than refusing to move - straight down from
                    // the second column of the last full row would otherwise be a dead end.
                    if (next >= _userArtTiles.Count)
                    {
                        if (_userArtIndex >= _userArtTiles.Count - 1) return;
                        next = _userArtTiles.Count - 1;
                    }
                    break;
                default: return;
            }
            _userArtIndex = next;
            ApplyUserArtSelection();
            ScrollUserArtSelectionIntoView();
            RefreshActionBar();
        }

        private void ScrollUserArtSelectionIntoView()
        {
            try
            {
                if (_userArtIndex >= 0 && _userArtIndex < _userArtTiles.Count) _userArtTiles[_userArtIndex].BringIntoView();
            }
            catch { }
        }
        #endregion

        #region Applying
        /// <summary>A, on a tile: copies the picture into Center's own cache and puts it to work.</summary>
        private void ApplyPickedUserArt()
        {
            if (_userArtApplying) return;
            if (_userArtIndex < 0 || _userArtIndex >= _userArtImages.Count) return;
            string source = _userArtImages[_userArtIndex];

            if (_userArtPurpose == UserArtPurpose.Background)
            {
                ApplyPickedBackground(source);
                return;
            }

            var target = _gameMenuTarget;
            if (target == null) return;

            _userArtApplying = true;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(() =>
            {
                string copied = UserImageLibrary.CopyIntoCache(source, "custom_");
                Dispatcher.Invoke(() =>
                {
                    _userArtApplying = false;
                    if (copied != null)
                    {
                        // The same store the SteamGridDB picker writes to, on purpose: a hand-picked
                        // cover is a hand-picked cover, and a second index would be a second opinion
                        // about which one a tile should draw.
                        ArtOverrideStore.Set(target, copied);
                        ResetUserArtState();
                        _gameMenuOverlay = GameMenuOverlay.Menu;
                    }
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                });
            });
        }

        private void ApplyPickedBackground(string source)
        {
            string previous = Core.CenterSettings.BackgroundImagePath;

            if (source.Length == 0)
            {
                Core.CenterSettings.BackgroundImagePath = string.Empty;
                DeleteCachedBackground(previous);
                ApplyBackgroundImage();
                UserArtBack();
                return;
            }

            _userArtApplying = true;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(() =>
            {
                string copied = UserImageLibrary.CopyIntoCache(source, "background_");
                Dispatcher.Invoke(() =>
                {
                    _userArtApplying = false;
                    if (copied != null)
                    {
                        Core.CenterSettings.BackgroundImagePath = copied;
                        // Exactly one setting points at this file, which is what makes deleting the
                        // old one safe here and wrong for a cover override - several games can share
                        // one picked picture.
                        DeleteCachedBackground(previous);
                        ApplyBackgroundImage();
                        UserArtBack();
                        return;
                    }
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                });
            });
        }

        /// <summary>Removes a superseded background copy - and ONLY one of our own copies. A path
        /// outside the art cache would be the user's own file, and this must never delete that.</summary>
        private static void DeleteCachedBackground(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.Equals(dir, SteamGridDb.CacheDir, StringComparison.OrdinalIgnoreCase)) return;
                if (!Path.GetFileName(path).StartsWith("background_", StringComparison.OrdinalIgnoreCase)) return;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
        #endregion

        #region The background itself
        private static bool HasBackgroundImage
        {
            get
            {
                string path = Core.CenterSettings.BackgroundImagePath;
                if (string.IsNullOrEmpty(path)) return false;
                try { return File.Exists(path); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Draws (or clears) the window background.
        ///
        /// CROPPED, NOT STRETCHED (user, 2026-09-09): UniformToFill keeps the aspect ratio and lets
        /// the overflow go off the edges. Stretching would fit every picture exactly and distort
        /// every one that is not 16:10 - and a distorted face or logo does not read as "my picture
        /// does not fit", it reads as Center rendering it wrong.
        ///
        /// A stored path that no longer exists clears the background rather than leaving a blank
        /// Image element behind - the file lives in our own cache, so that only happens if somebody
        /// emptied it by hand, and the honest answer to that is the flat background we started with.
        /// </summary>
        private void ApplyBackgroundImage()
        {
            if (BackgroundImage == null || BackgroundScrim == null) return;

            string path = Core.CenterSettings.BackgroundImagePath;
            bool present;
            try { present = !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { present = false; }

            if (!present)
            {
                BackgroundImage.Source = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundScrim.Visibility = Visibility.Collapsed;
                if (BackgroundBlur != null)
                {
                    BackgroundBlur.Source = null;
                    BackgroundBlur.Visibility = Visibility.Collapsed;
                }
                ApplyFooterChrome();
                return;
            }

            BackgroundScrim.Opacity = BackgroundScrimOpacity;
            GameArt.LoadAsync(path, BackgroundDecodeWidth).ContinueWith(t =>
            {
                var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (BackgroundImage == null || BackgroundScrim == null) return;
                    if (bmp == null)
                    {
                        // A file that will not decode is not a background. Saying so in the log is
                        // the difference between "Center ignores my picture" and a corrupt JPEG.
                        Core.InstallLog.Write("[UserArt] background failed to decode: " + path);
                        BackgroundImage.Visibility = Visibility.Collapsed;
                        BackgroundScrim.Visibility = Visibility.Collapsed;
                        if (BackgroundBlur != null) BackgroundBlur.Visibility = Visibility.Collapsed;
                        ApplyFooterChrome();
                        return;
                    }
                    BackgroundImage.Source = bmp;
                    BackgroundImage.Visibility = Visibility.Visible;
                    BackgroundScrim.Visibility = Visibility.Visible;

                    // The footer gets the same picture, blurred and masked down to its own strip -
                    // the header shows the background through it already, and a footer that stayed
                    // opaque was the one band the picture stopped at (user, 2026-09-09). Blurred
                    // rather than sharp because the chips, the clock and the battery sit on it.
                    if (BackgroundBlur != null)
                    {
                        BackgroundBlur.Source = bmp;
                        BackgroundBlur.Visibility = Visibility.Visible;
                        RefreshFooterBlurMask();
                    }
                    ApplyFooterChrome();
                }));
            }, TaskScheduler.Default);
        }
        #endregion

        #region Footer
        private bool RefreshUserArtActionBar()
        {
            switch (_gameMenuOverlay)
            {
                case GameMenuOverlay.UserArtFolder:
                    AddAction(PadButton.A, "Use this folder", _userArtFolderActions.Count > 0, ActivateUserArtFolderRow);
                    AddAction(PadButton.B, "Back", true, UserArtBack);
                    return true;

                case GameMenuOverlay.UserArt:
                    bool none = _userArtIndex >= 0 && _userArtIndex < _userArtImages.Count
                                && _userArtImages[_userArtIndex].Length == 0;
                    string label = none
                        ? "Remove background"
                        : _userArtPurpose == UserArtPurpose.Background ? "Use as background" : "Set as cover";
                    AddAction(PadButton.A, label, !_userArtApplying && !_userArtScanning && _userArtTiles.Count > 0, ApplyPickedUserArt);
                    AddAction(PadButton.X, "Change folder", !_userArtApplying, () =>
                    {
                        ResetUserArtState();
                        _gameMenuOverlay = GameMenuOverlay.UserArtFolder;
                        RenderGameMenuOverlay();
                        RefreshActionBar();
                    });
                    AddAction(PadButton.Y, "Rescan", !_userArtApplying && !_userArtScanning, StartUserArtScan);
                    AddAction(PadButton.B, "Back", true, UserArtBack);
                    return true;

                default:
                    return false;
            }
        }
        #endregion
    }
}
