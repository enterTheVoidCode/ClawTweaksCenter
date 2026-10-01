using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Community presets in the Library, ported from the Game Bar widget
    /// (XboxGamingBar/Features/GamePresets/GamingWidget.Community*.cs).
    ///
    /// WHERE IT LIVES. A banner above the cover on the launch screen (BuildCommunityBanner), reached
    /// by pressing up from Play. A opens a full overlay — a GameMenuOverlay state of its own, exactly
    /// like the achievements list, so it reuses the whole overlay plumbing (render switch, back,
    /// direction and action-bar dispatch in CenterMenuWindow.GameMenu.cs). Inside it there is a small
    /// screen machine (<see cref="CommunityScreen"/>): the list of shared presets, a rating form, a
    /// sharing form, and the on-screen keyboard both forms use.
    ///
    /// WHAT TALKS TO WHOM.
    ///   • Reading the published list is self-contained: <see cref="CommunityPresets"/> fetches one
    ///     flat file from the public repo. No Discord, no helper, no secret.
    ///   • Rating and sharing need the signing key and the device-derived authorId, which live in the
    ///     HELPER (Center is a public repo). They go over the Center→helper pipe and come back on
    ///     Function.CommunitySubmitResult / CommunityRateResult / CommunityIdentityResult — the same
    ///     round-trip shape the driver check uses (RequestWithResultAsync).
    ///
    /// NOT HERE, on purpose (user, 2026-10-01): APPLYING a shared preset to the game's own settings.
    /// That is a later, separate task — the overlay shows and rates presets and lets you share one,
    /// but does not overwrite a game's profile. See the handover doc.
    ///
    /// Full design + contract: Doku/HANDOVER_2026-10-01_Community_Presets_Library.md.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private enum CommunityScreen { List, Rate, Create, Keyboard, Message }

        private CommunityScreen _communityScreen = CommunityScreen.List;
        private GameEntry _communityGame;
        private bool _communityFromLaunch;

        // The matched, best-first presets for the game the overlay is open on, captured when it opens
        // so a background index refresh cannot reshuffle the list under the cursor.
        private List<CommunityPresets.Preset> _communityList = new List<CommunityPresets.Preset>();

        // One short line under the title, for refusals and progress ("Posting…", "nickname too short").
        private string _communityNote;

        // ── this machine's identity, fetched once from the helper ────────────────────────────────
        // device WITH the 7/8 variant (Center alone only knows "a2vm"), the shared nickname, and this
        // machine's authorId (to mark YOUR posts and to let the helper refuse a self-rating early).
        private string _communityDevice = "";
        private string _communityNickname = "";
        private string _communityAuthorId = "";
        private bool _communityIdentityTried;

        // ── a self-contained selectable-row list ─────────────────────────────────────────────────
        // Deliberately NOT the shared _gameMenuRows/_gameMenuIndex: those are wired to GameMenuSplitRow
        // and the game menu's own cursor, and borrowing them for a second, differently-shaped screen is
        // how two cursors end up disagreeing about which row is selected.
        private readonly List<Border> _communityRows = new List<Border>();
        private readonly List<Action> _communityRowActions = new List<Action>();
        private readonly List<string> _communityRowLabels = new List<string>();   // the footer verb per row
        private readonly Dictionary<int, CommunityChipRow> _communityChipRows = new Dictionary<int, CommunityChipRow>();
        private int _communityIndex;
        private ScrollViewer _communityScroller;

        private sealed class CommunityChipRow
        {
            public int Count;
            public Func<int> Get;
            public Action<int> Set;
        }

        // ════════════════════════════════════ opening / closing ═════════════════════════════════

        /// <summary>A on the launch-screen banner. Opens over the launch prompt, which is left
        /// standing so B returns to it intact — the achievements list does exactly this.</summary>
        private void OpenCommunityFromLaunch(GameEntry game)
        {
            if (game == null) return;

            _communityGame = game;
            _communityFromLaunch = true;
            _communityScreen = CommunityScreen.List;
            _communityIndex = 0;
            _communityNote = null;
            _communityList = CommunityPresets.ForGame(game);

            StopCommunitySlideshow();   // the banner's timer has no screen to draw to now

            _gameMenuOverlay = GameMenuOverlay.Community;
            RenderGameMenuOverlay();
            RefreshActionBar();

            // The identity and a fresh index land asynchronously; both redraw the list when they do.
            EnsureCommunityIdentity();
            EnsureCommunityIndexThenRedraw(forceRefresh: false);
        }

        /// <summary>B on the top-level list, when the overlay was opened from the launch screen.</summary>
        private void CloseCommunityToLaunch()
        {
            _communityGame = null;
            _communityList = new List<CommunityPresets.Preset>();
            _gameMenuOverlay = GameMenuOverlay.None;
            RenderLaunchOverlay();
            RefreshActionBar();
        }

        /// <summary>B, routed here from GameMenuBack for the Community overlay.</summary>
        private void CommunityBack()
        {
            switch (_communityScreen)
            {
                case CommunityScreen.Keyboard:
                    CancelCommunityKeyboard();
                    return;
                case CommunityScreen.Rate:
                case CommunityScreen.Create:
                case CommunityScreen.Message:
                    _communityScreen = CommunityScreen.List;
                    _communityIndex = 0;
                    _communityNote = null;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                    return;
                default:
                    if (_communityFromLaunch) CloseCommunityToLaunch();
                    else CloseGameMenuOverlay();
                    return;
            }
        }

        // ════════════════════════════════════ render dispatch ═══════════════════════════════════

        /// <summary>Called from RenderGameMenuOverlay's switch. The row lists are cleared there with
        /// the other overlays' lists, so this only builds.</summary>
        private void RenderCommunityOverlay()
        {
            switch (_communityScreen)
            {
                case CommunityScreen.Rate:     RenderCommunityRate();     break;
                case CommunityScreen.Create:   RenderCommunityCreate();   break;
                case CommunityScreen.Keyboard: RenderCommunityKeyboard(); break;
                case CommunityScreen.Message:  RenderCommunityMessage();  break;
                default:                        RenderCommunityList();     break;
            }
        }

        private void ClearCommunityRows()
        {
            _communityRows.Clear();
            _communityRowActions.Clear();
            _communityRowLabels.Clear();
            _communityChipRows.Clear();
            _communityScroller = null;
        }

        // ════════════════════════════════════ the list ══════════════════════════════════════════

        private void RenderCommunityList()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            LibraryRoot.Children.Add(CommunityHead(
                Core.Loc.F("Community presets — {0}", _communityGame?.Title ?? ""),
                CommunityListStatusLine()));

            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), HorizontalAlignment = HorizontalAlignment.Center };

            // Sharing your own preset sits at the top: it is the one thing you can do here that does
            // not depend on anyone else having posted first. Once this machine has shared one, the row
            // becomes a plain, NON-selectable note rather than a dead button - a second preset for the
            // same game from the same machine is noise, and the honest answer is to point at the one
            // that is already there (the widget does the same).
            bool haveOwn = !string.IsNullOrEmpty(_communityAuthorId)
                           && _communityList.Any(p => p.IsOwn(_communityAuthorId));
            if (haveOwn)
                panel.Children.Add(new Border
                {
                    Child = CommunityLine(Core.Loc.T("You already shared a preset for this game — it is marked below."),
                                          UiHelpers.Subtle, 13),
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 10),
                    MinWidth = 620,
                    MaxWidth = 720,
                });
            else
                panel.Children.Add(AddCommunityActionRow(
                    "",
                    Core.Loc.T("Share your preset"),
                    Core.Loc.T("Post your settings for this game to the community."),
                    Core.Loc.T("Share"),
                    OpenCommunityCreate,
                    live: true));

            foreach (var p in _communityList)
                panel.Children.Add(BuildCommunityCard(p));

            _communityScroller = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 8, 0),
            };
            Grid.SetRow(_communityScroller, 1);
            LibraryRoot.Children.Add(_communityScroller);

            ClampCommunityIndex();
            ApplyCommunitySelection();
        }

        private string CommunityListStatusLine()
        {
            if (!CommunityPresets.Loaded) return Core.Loc.T("Loading shared presets…");
            int n = _communityList.Count;
            return n == 0 ? Core.Loc.T("No one has shared a preset for this game yet.")
                 : n == 1 ? Core.Loc.T("1 shared preset for this game.")
                 : Core.Loc.F("{0} shared presets for this game.", n);
        }

        /// <summary>One read-only card plus, when it is not your own, a selectable Rate row under it.
        /// The card itself is not selectable: there is nothing to do TO it — it is the thing you read,
        /// and the one action it offers (rate) is its own row so the footer can name it.</summary>
        private FrameworkElement BuildCommunityCard(CommunityPresets.Preset p)
        {
            bool own = p.IsOwn(_communityAuthorId);
            var stack = new StackPanel();

            // The two figures the whole feature exists for: how fast it ran and what it cost.
            stack.Children.Add(new TextBlock
            {
                Text = Core.Loc.F("{0} fps native at {1} W", p.Get("fpsNative"), p.Get("tdpW")),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Accent,
                TextWrapping = TextWrapping.Wrap,
            });

            var by = new List<string> { Core.Loc.F("by {0}", p.Get("author")) };
            string dev = CommunityPresets.DeviceName(p.Get("device"));
            if (!string.IsNullOrEmpty(dev)) by.Add(dev);
            if (own) by.Add(Core.Loc.T("YOURS"));
            stack.Children.Add(CommunityLine(string.Join("  ·  ", by), own ? UiHelpers.Ok : UiHelpers.Subtle, 13));

            string detail = BuildCardDetailLine(p);
            if (detail.Length > 0) stack.Children.Add(CommunityLine(detail, UiHelpers.Subtle, 12));

            string gfx = BuildCardGraphicsLine(p);
            if (gfx.Length > 0) stack.Children.Add(CommunityLine(gfx, UiHelpers.Subtle, 12));

            string stars = CommunityPresets.RatingLine(p);
            if (stars != null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = stars,
                    FontSize = 14,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07)),
                });
                foreach (string line in CommunityPresets.RatingBreakdownLines(p.Get("ratingBreakdown")))
                    stack.Children.Add(CommunityLine(line, UiHelpers.Subtle, 12));
            }

            var card = new Border
            {
                Child = stack,
                Background = UiHelpers.Card,
                BorderBrush = own ? UiHelpers.Ok : Brushes.Transparent,
                BorderThickness = new Thickness(own ? 1 : 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, own || HasThread(p) ? 8 : 12),
                Margin = new Thickness(0, 0, 0, HasThread(p) && !own ? 2 : 10),
                MinWidth = 620,
                MaxWidth = 720,
            };

            // ⚠ NOT ON YOUR OWN PRESET, and not on a published-only entry with no thread to rate against.
            // The helper refuses a self-rating too; this only spares the round trip. Without the rule the
            // first rating on every preset is five stars from its author.
            if (own || !HasThread(p)) return card;

            var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            wrap.Children.Add(card);
            wrap.Children.Add(AddCommunityActionRow(
                "",
                stars == null ? Core.Loc.T("Be the first to rate this") : Core.Loc.T("Rate this preset"),
                null,
                Core.Loc.T("Rate"),
                () => OpenCommunityRate(p),
                live: true,
                topMargin: 0,
                bottomMargin: 10));
            return wrap;
        }

        private static bool HasThread(CommunityPresets.Preset p) => !string.IsNullOrEmpty(p.Get("threadId"));

        private static string BuildCardDetailLine(CommunityPresets.Preset p)
        {
            var d = new List<string>();
            void Add(string label, string key, string suffix = "")
            {
                string v = p.Get(key);
                if (!string.IsNullOrEmpty(v)) d.Add((label.Length > 0 ? label + " " : "") + CommunityPresets.Display(key, v) + suffix);
            }
            Add("", "resolution");
            Add("preset", "graphicsPreset");
            Add("upscaler", "upscaler");
            Add("", "upscalerPreset");
            Add("frame gen", "frameGen");
            Add("x", "frameGenFactor");
            Add("CPU boost", "cpuBoostMode");
            Add("cap", "fpsLimit", " fps");
            return string.Join("  ·  ", d);
        }

        private static string BuildCardGraphicsLine(CommunityPresets.Preset p)
        {
            var g = new List<string>();
            void Add(string label, string key)
            {
                string v = p.Get(key);
                if (!string.IsNullOrEmpty(v)) g.Add(label + " " + v);
            }
            Add("textures", "texture");
            Add("shadows", "shadows");
            Add("aniso", "aniso");
            Add("lighting", "lighting");
            Add("view dist", "viewDistance");
            Add("characters", "characters");
            return g.Count > 0 ? Core.Loc.T("In game:") + " " + string.Join("  ·  ", g) : "";
        }

        // ════════════════════════════════════ shared row widgets ════════════════════════════════

        private UIElement CommunityHead(string title, string status)
        {
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });
            if (!string.IsNullOrEmpty(status))
                head.Children.Add(new TextBlock
                {
                    Text = status,
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            if (!string.IsNullOrEmpty(_communityNote))
                head.Children.Add(new TextBlock
                {
                    Text = _communityNote,
                    FontSize = 14,
                    Foreground = UiHelpers.Warn,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            return head;
        }

        private static TextBlock CommunityLine(string text, Brush brush, double size) => new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>A selectable action row (icon, title, subtitle, named action on the right). Returns
        /// the border so a caller can wrap it; registers it with this overlay's own cursor.</summary>
        private Border AddCommunityActionRow(string glyph, string title, string subtitle, string actionLabel,
                                             Action run, bool live, double topMargin = 0, double bottomMargin = 10)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                Foreground = live ? UiHelpers.Text : UiHelpers.Subtle,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(subtitle))
                left.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = live ? UiHelpers.Text : UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(icon);
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            return RegisterCommunityRow(grid, run, live, topMargin, bottomMargin, null,
                                        string.IsNullOrEmpty(actionLabel) ? null : actionLabel);
        }

        /// <summary>A selectable row that carries a set of chips (Left/Right picks one). Used for every
        /// choice on the rating and sharing forms — the same shape the game menu's split rows use, but
        /// on this overlay's own cursor.</summary>
        private Border AddCommunityChipRow(string title, string[] labels, bool[] enabled,
                                           Func<int> get, Action<int> set)
        {
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 16,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            int current = get();
            for (int i = 0; i < labels.Length; i++)
            {
                bool on = enabled == null || enabled[i];
                chips.Children.Add(CommunityChip(labels[i], i == current, on));
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(titleBlock);
            Grid.SetColumn(chips, 1);
            grid.Children.Add(chips);

            return RegisterCommunityRow(grid, run: null, live: true, topMargin: 0, bottomMargin: 10,
                chip: new CommunityChipRow { Count = labels.Length, Get = get, Set = set }, actionLabel: null);
        }

        private static Border CommunityChip(string text, bool active, bool enabled) => new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = active ? UiHelpers.Accent : UiHelpers.Subtle,
            Background = active ? UiHelpers.Accent : Brushes.Transparent,
            Opacity = enabled ? 1.0 : 0.45,
            Child = new TextBlock { Text = text, FontSize = 13, Foreground = active ? Brushes.White : UiHelpers.Subtle },
        };

        private Border RegisterCommunityRow(UIElement content, Action run, bool live,
                                            double topMargin, double bottomMargin, CommunityChipRow chip,
                                            string actionLabel)
        {
            int index = _communityRows.Count;
            var border = new Border
            {
                Child = content,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, topMargin, 0, bottomMargin),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                MinWidth = 620,
                MaxWidth = 720,
                Tag = index,
            };
            border.MouseLeftButtonUp += (_, __) => { _communityIndex = index; ActivateCommunityRow(); };
            _communityRows.Add(border);
            _communityRowActions.Add(live ? run : null);
            _communityRowLabels.Add(live ? actionLabel : null);
            if (chip != null) _communityChipRows[index] = chip;
            return border;
        }

        private void ClampCommunityIndex()
        {
            if (_communityRows.Count == 0) { _communityIndex = 0; return; }
            if (_communityIndex < 0) _communityIndex = 0;
            if (_communityIndex >= _communityRows.Count) _communityIndex = _communityRows.Count - 1;
        }

        private void ApplyCommunitySelection()
        {
            for (int i = 0; i < _communityRows.Count; i++)
                _communityRows[i].BorderBrush = i == _communityIndex ? UiHelpers.Accent : Brushes.Transparent;

            if (_communityIndex >= 0 && _communityIndex < _communityRows.Count)
                _communityRows[_communityIndex].BringIntoView();
        }

        // ════════════════════════════════════ input ════════════════════════════════════════════

        /// <summary>Routed here from MoveGameMenuSelection for the Community overlay.</summary>
        private void MoveCommunitySelection(PadButton dir)
        {
            if (_communityScreen == CommunityScreen.Keyboard) { MoveCommunityKeyboard(dir); return; }

            // Left/Right pick a chip on a chip row.
            if ((dir == PadButton.Left || dir == PadButton.Right)
                && _communityChipRows.TryGetValue(_communityIndex, out var chip))
            {
                int next = chip.Get() + (dir == PadButton.Right ? 1 : -1);
                if (next < 0 || next >= chip.Count) return;
                chip.Set(next);
                RenderGameMenuOverlay();   // cheap re-render keeps the chips and the cursor in step
                RefreshActionBar();
                return;
            }

            if (dir != PadButton.Up && dir != PadButton.Down) return;
            if (_communityRows.Count == 0) return;

            int step = dir == PadButton.Down ? 1 : -1;
            int target = _communityIndex;
            // Skip rows that have no action and no chips (read-only spacers should never happen, but a
            // dead stop is better than a cursor that lands on nothing).
            for (int i = 0; i < _communityRows.Count; i++)
            {
                target += step;
                if (target < 0 || target >= _communityRows.Count) return;
                if (_communityRowActions[target] != null || _communityChipRows.ContainsKey(target)) break;
            }
            if (target == _communityIndex) return;
            _communityIndex = target;
            ApplyCommunitySelection();
            RefreshActionBar();
        }

        /// <summary>A, routed here from the Community action.</summary>
        private void ActivateCommunityRow()
        {
            if (_communityScreen == CommunityScreen.Keyboard) { PressCommunityKey(); return; }
            if (_communityIndex < 0 || _communityIndex >= _communityRowActions.Count) return;
            _communityRowActions[_communityIndex]?.Invoke();
        }

        /// <summary>The verb A carries on the focused row, or null when A does nothing here (a chip
        /// row is driven by Left/Right, not A).</summary>
        private string FocusedCommunityLabel()
            => _communityIndex >= 0 && _communityIndex < _communityRowLabels.Count
               ? _communityRowLabels[_communityIndex] : null;

        /// <summary>The Community overlay's footer, called from RefreshGameMenuActionBar.</summary>
        private void AddCommunityActions()
        {
            if (_communityScreen == CommunityScreen.Keyboard)
            {
                AddAction(PadButton.A, CommunityKeyLabel(), true, PressCommunityKey);
                AddAction(PadButton.X, Core.Loc.T("Backspace"), true, CommunityKeyBackspace);
                AddAction(PadButton.Y, _communityKbShift ? Core.Loc.T("shift") : Core.Loc.T("Shift"),
                          !_communityKbDigitsOnly, CommunityKeyToggleShift);
                AddAction(PadButton.B, Core.Loc.T("Cancel"), true, CommunityBack);
                return;
            }

            string label = FocusedCommunityLabel();
            if (!string.IsNullOrEmpty(label)) AddAction(PadButton.A, label, true, ActivateCommunityRow);
            AddAction(PadButton.B, Core.Loc.T("Back"), true, CommunityBack);
        }

        // ════════════════════════════════════ index / identity ══════════════════════════════════

        private void EnsureCommunityIndexThenRedraw(bool forceRefresh)
        {
            _ = CommunityPresets.EnsureLoadedAsync(CancellationToken.None, forceRefresh)
                .ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    // Only if the overlay is still the Community list on this same game.
                    if (_gameMenuOverlay != GameMenuOverlay.Community || _communityGame == null) return;
                    _communityList = CommunityPresets.ForGame(_communityGame);
                    if (_communityScreen == CommunityScreen.List) { RenderGameMenuOverlay(); RefreshActionBar(); }
                }), System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>Asks the helper once per session for device/nickname/authorId. Best-effort: without
        /// it, YOURS badges and the nickname prefill are simply absent, which is honest rather than
        /// wrong.</summary>
        private void EnsureCommunityIdentity()
        {
            if (_communityIdentityTried) return;
            _communityIdentityTried = true;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (!await EnsureHelperAsync()) return;
                    string reply = await _helperPipe.RequestWithResultAsync(
                        "CommunityRequestIdentity", true,
                        Shared.Enums.Function.CommunityIdentityResult, TimeSpan.FromSeconds(6));
                    if (string.IsNullOrEmpty(reply)) return;
                    ParseCommunityIdentity(reply);
                }
                catch (Exception ex) { Core.InstallLog.Write("Community identity request failed: " + ex.Message); }

                Dispatcher.Invoke(() =>
                {
                    if (_gameMenuOverlay == GameMenuOverlay.Community && _communityScreen == CommunityScreen.List)
                    {
                        _communityList = _communityGame != null ? CommunityPresets.ForGame(_communityGame) : _communityList;
                        RenderGameMenuOverlay();
                        RefreshActionBar();
                    }
                });
            });
        }

        private void ParseCommunityIdentity(string reply)
        {
            foreach (string part in reply.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq);
                string v = part.Substring(eq + 1);
                if (k == "device") _communityDevice = v;
                else if (k == "nickname") _communityNickname = v;
                else if (k == "authorId") _communityAuthorId = v;
            }
        }
    }
}
