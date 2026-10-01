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
    /// WHERE IT LIVES. A column under the cover on the launch screen (BuildCommunityPanel), beside
    /// the achievements, with a button in the row below (BuildCommunityButton). A opens a full overlay — a GameMenuOverlay state of its own, exactly
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
    /// APPLYING a preset (A on a card) goes through the helper too: it writes the game's per-game
    /// profile by exe path (Program.CommunityApply.cs in the dev repo) - power fields only, like the
    /// widget. Center never writes a profile file itself.
    ///
    /// Full design + contract: Doku/HANDOVER_2026-10-01_Community_Presets_Library.md.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private enum CommunityScreen { List, Rate, Create, Keyboard, Message, Apply }

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
            _communityOffsets.Clear();
            _lastCommunityScreen = null;
            _communityScreen = CommunityScreen.List;
            _communityIndex = 0;
            _communityNote = null;
            _communityAllDevices = false;
            RefreshCommunityList();

            StopCommunitySlideshow();   // the banner's timer has no screen to draw to now

            _gameMenuOverlay = GameMenuOverlay.Community;
            RenderGameMenuOverlay();
            RefreshActionBar();

            // The identity and a FRESH index land asynchronously; both redraw the list when they do.
            // Forced: opening the list is the moment someone wants the newest ratings, and the cached
            // file can be up to MaxAge old (user, 2026-10-01: a rating the widget showed was missing).
            EnsureCommunityIdentity();
            EnsureCommunityIndexThenRedraw(forceRefresh: true);
        }

        /// <summary>Default OFF: the list opens on presets measured on this kind of Claw, because watts
        /// and frame rates do not carry across models. X shows the rest (user, 2026-10-01).</summary>
        private bool _communityAllDevices;

        /// <summary>Every preset for the game, before the device filter - for the "you already shared"
        /// check and the count of the hidden ones.</summary>
        private List<CommunityPresets.Preset> _communityListAll = new List<CommunityPresets.Preset>();

        private void RefreshCommunityList()
        {
            _communityListAll = _communityGame != null ? CommunityPresets.ForGame(_communityGame)
                                                       : new List<CommunityPresets.Preset>();
            string mine = !string.IsNullOrEmpty(_communityDevice) ? _communityDevice : CommunityPresets.DeviceCode();
            _communityList = _communityAllDevices
                ? _communityListAll
                : _communityListAll.Where(p => CommunityPresets.SameDeviceFamily(p, mine)).ToList();
        }

        private void ToggleCommunityAllDevices()
        {
            ResetCommunityScroll(CommunityScreen.List);
            _communityAllDevices = !_communityAllDevices;
            RefreshCommunityList();
            _communityIndex = 0;
            RenderGameMenuOverlay();
            RefreshActionBar();
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
            if (_communityChipEditIndex >= 0)
            {
                _communityChipEditIndex = -1;
            _communityRowRestBorder.Clear();
                ApplyCommunitySelection();
                RefreshActionBar();
                return;
            }
            switch (_communityScreen)
            {
                case CommunityScreen.Keyboard:
                    CancelCommunityKeyboard();
                    return;
                case CommunityScreen.Rate:
                case CommunityScreen.Create:
                case CommunityScreen.Message:
                    if (_communityMessageToLaunch && _communityFromLaunch) { CloseCommunityToLaunch(); return; }
                    goto case CommunityScreen.Apply;
                case CommunityScreen.Apply:
                    _communityScreen = CommunityScreen.List;
                    _communityIndex = 0;
                    _communityNote = null;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                    return;
                default:
                    if (_communityGlobal) CloseCommunityBrowseToHome();
                    else if (_communityFromLaunch) CloseCommunityToLaunch();
                    else CloseGameMenuOverlay();
                    return;
            }
        }

        // ════════════════════════════════════ render dispatch ═══════════════════════════════════

        /// <summary>Called from RenderGameMenuOverlay's switch. The row lists are cleared there with
        /// the other overlays' lists, so this only builds.</summary>
        private void RenderCommunityOverlay()
        {
            // THE SCROLL POSITION SURVIVES A RE-RENDER OF THE SAME SCREEN. Picking a chip redraws the
            // whole form (rows appear and disappear with the answer), and a fresh ScrollViewer starts at
            // the top: the view jumped up while the cursor stayed on a row far below, and Left/Right then
            // changed a row nobody could see (user, 2026-10-01: "the focus jumps up, left/right do not
            // work"). Kept only for the same screen - a new screen starts at its top.
            // Remembered PER SCREEN, so coming back from the keyboard lands where the form was left.
            if (_communityScroller != null && _lastCommunityScreen.HasValue)
                _communityOffsets[_lastCommunityScreen.Value] = _communityScroller.VerticalOffset;
            _lastCommunityScreen = _communityScreen;
            double keepOffset = _communityOffsets.TryGetValue(_communityScreen, out double o) ? o : 0;

            switch (_communityScreen)
            {
                case CommunityScreen.Rate:     RenderCommunityRate();     break;
                case CommunityScreen.Create:   RenderCommunityCreate();   break;
                case CommunityScreen.Keyboard: RenderCommunityKeyboard(); break;
                case CommunityScreen.Message:  RenderCommunityMessage();  break;
                case CommunityScreen.Apply:    RenderCommunityApply();    break;
                default:                        RenderCommunityList();     break;
            }

            if (keepOffset > 0 && _communityScroller != null)
            {
                var sv = _communityScroller;
                sv.ScrollToVerticalOffset(keepOffset);
                // Again after layout: before it, the extent is zero and the offset is clamped to 0.
                Dispatcher.BeginInvoke(new Action(() => sv.ScrollToVerticalOffset(keepOffset)),
                                       System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private CommunityScreen? _lastCommunityScreen;
        private readonly Dictionary<CommunityScreen, double> _communityOffsets = new Dictionary<CommunityScreen, double>();

        /// <summary>A screen opened FRESH starts at its top - only a redraw or a return keeps the offset.</summary>
        private void ResetCommunityScroll(CommunityScreen screen) => _communityOffsets.Remove(screen);

        private void ClearCommunityRows()
        {
            _communityRows.Clear();
            _communityRowActions.Clear();
            _communityRowLabels.Clear();
            _communityChipRows.Clear();
            _communityRowAlt.Clear();
            _communityFilledRows.Clear();
            _communityRowHints.Clear();
            _communityPartner.Clear();
            _communityChipEditIndex = -1;
            _communityScroller = null;
        }

        /// <summary>Two rows drawn side by side (FPS | TDP, Stars | Comment): each index maps to its
        /// partner, so Left/Right can cross between them when neither half needs Left/Right itself.</summary>
        private readonly Dictionary<int, int> _communityPartner = new Dictionary<int, int>();

        /// <summary>
        /// A chip row that sits in a side-by-side pair does NOT take Left/Right on sight - in a pair
        /// those keys move between the two columns (user, 2026-10-01). A enters this choosing state,
        /// Left/Right then pick a chip, and A or B leave it. -1 = nobody is choosing. A full-width chip
        /// row is unaffected: there Left/Right have nothing else to do.
        /// </summary>
        private int _communityChipEditIndex = -1;

        /// <summary>A row's outline when the cursor is NOT on it - the expander row keeps a subtle
        /// one. Rows without an entry rest without an outline.</summary>
        private readonly Dictionary<int, Brush> _communityRowRestBorder = new Dictionary<int, Brush>();

        private bool ChipNeedsEdit(int index) => _communityChipRows.ContainsKey(index) && _communityPartner.ContainsKey(index);

        /// <summary>The "set" and "still open" marks for a required field: a green check, an amber ring.</summary>
        private const string GlyphFieldSet = "\uE73E";    // CheckMark
        private const string GlyphFieldOpen = "\uEA3A";   // CircleRing
        private static readonly Brush FieldOpenBrush = Frozen(Color.FromRgb(0xF0, 0xB4, 0x29));

        /// <summary>
        /// Puts two already registered rows side by side, to save height (user, 2026-10-01). The left
        /// one must have been created first, so the cursor order still reads left before right.
        /// </summary>
        private UIElement CommunityPair(Border left, Border right)
        {
            int li = (int)left.Tag, ri = (int)right.Tag;
            _communityPartner[li] = ri;
            _communityPartner[ri] = li;
            foreach (var b in new[] { left, right }) { b.MinWidth = 0; b.MaxWidth = double.PositiveInfinity; }
            var grid = new Grid { Width = 720, Margin = new Thickness(0, left.Margin.Top, 0, left.Margin.Bottom) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.Margin = new Thickness(0); right.Margin = new Thickness(0);
            Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
            grid.Children.Add(left); grid.Children.Add(right);
            return grid;
        }

        /// <summary>The button hints of a row, shown only while that row has the cursor.</summary>
        private readonly Dictionary<int, UIElement> _communityRowHints = new Dictionary<int, UIElement>();

        /// <summary>A second verb on a row, on Y - the preset card's "Rate" beside A's "Use".</summary>
        private readonly Dictionary<int, (string Label, Action Run)> _communityRowAlt =
            new Dictionary<int, (string Label, Action Run)>();

        /// <summary>Rows drawn as filled buttons - their focus outline is white, since the accent outline
        /// would vanish against an accent-coloured fill.</summary>
        private readonly HashSet<int> _communityFilledRows = new HashSet<int>();

        /// <summary>The fill of the community's own action buttons (share, submit, apply). Green, the
        /// colour the widget's confirm buttons use, and never the accent the preset figures are drawn in -
        /// which is what made "Share your preset" read as one more preset.</summary>
        private static readonly Brush CommunityShareFill = Frozen(Color.FromRgb(0x2E, 0x7D, 0x32));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Text on a filled (selected) chip: near-black on the light accent. White on light
        /// blue was hard to read (user, 2026-10-01).</summary>
        private static readonly Brush ChipActiveText = Frozen(Color.FromRgb(0x10, 0x14, 0x1A));

        /// <summary>A filled, centred button on this overlay's own cursor.</summary>
        private Border AddCommunityButton(string title, string actionLabel, Action run, Brush fill,
                                          double topMargin = 0, double bottomMargin = 10)
        {
            var text = new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var row = RegisterCommunityRow(text, run, true, topMargin, bottomMargin, null, actionLabel);
            row.Background = fill;
            _communityFilledRows.Add(_communityRows.Count - 1);
            return row;
        }

        // ════════════════════════════════════ the list ══════════════════════════════════════════

        private void RenderCommunityList()
        {
            if (_communityGlobal) { RenderCommunityBrowse(); return; }
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
                           && _communityListAll.Any(p => p.IsOwn(_communityAuthorId));
            bool pending = !haveOwn && CommunityPresets.IsPending(_communityGame, _communityAuthorId);
            if (haveOwn || pending)
                panel.Children.Add(new Border
                {
                    Child = CommunityLine(haveOwn
                            ? Core.Loc.T("You already shared a preset for this game — it is marked below.")
                            : Core.Loc.T("You shared a preset for this game. It appears here within a few hours."),
                                          UiHelpers.Subtle, 13),
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 10),
                    MinWidth = 620,
                    MaxWidth = 720,
                });
            else
                // A filled button, not a row: next to the preset cards it read as one more preset
                // (user, 2026-10-01).
                panel.Children.Add(AddCommunityButton(Core.Loc.T("Share your preset"), Core.Loc.T("Share"),
                                                      OpenCommunityCreate, CommunityShareFill, bottomMargin: 16));

            foreach (var p in _communityList)
                panel.Children.Add(BuildCommunityCard(p));

            int hidden = _communityListAll.Count - _communityList.Count;
            if (!_communityAllDevices && hidden > 0)
            {
                var more = (FrameworkElement)CommunityPadHint(PadButton.X,
                    hidden == 1 ? Core.Loc.T("1 more from other devices")
                                : Core.Loc.F("{0} more from other devices", hidden));
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(more);
            }

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
            if (!_communityAllDevices)
                return n == 0 ? Core.Loc.T("No preset for your Claw yet.")
                     : n == 1 ? Core.Loc.T("1 preset for your Claw.")
                     : Core.Loc.F("{0} presets for your Claw.", n);
            return n == 0 ? Core.Loc.T("No one has shared a preset for this game yet.")
                 : n == 1 ? Core.Loc.T("1 preset on all devices.")
                 : Core.Loc.F("{0} presets on all devices.", n);
        }

        /// <summary>
        /// One preset card, and the card IS the selectable row (user, 2026-10-01). A adopts the preset
        /// into this game's profile; Y rates it. The rating prompt used to be a full-size row under
        /// every card ("Be the first to rate this") and outweighed the preset it belonged to - it is a
        /// small line inside the card now, with the two buttons spelled out.
        /// </summary>
        private FrameworkElement BuildCommunityCard(CommunityPresets.Preset p, bool browse = false)
        {
            bool own = p.IsOwn(_communityAuthorId);
            // ⚠ NOT ON YOUR OWN PRESET, and not on a published-only entry with no thread to rate against.
            // The helper refuses a self-rating too; this only spares the round trip. Without the rule the
            // first rating on every preset is five stars from its author.
            bool canRate = !own && HasThread(p);
            var stack = new StackPanel();

            // The browse list mixes games, so each card names its game first.
            if (browse)
                stack.Children.Add(new TextBlock
                {
                    Text = p.Get("gameTitle"),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                });

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

            if (canRate && stars == null)
                stack.Children.Add(CommunityLine(Core.Loc.T("Not rated yet"), UiHelpers.Subtle, 12));

            // What A and Y do here, drawn with the footer's own button artwork, and ONLY on the focused
            // card (user, 2026-10-01: "[A] …" written out on every card was hard to read). Hidden, not
            // collapsed, so moving the cursor does not make the list below it jump.
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            if (browse)
            {
                // From Home there is no game to apply to - A rates. A card that cannot be rated still
                // takes the cursor (an empty action), or the D-pad would skip over it.
                if (canRate) hints.Children.Add(CommunityPadHint(PadButton.A, Core.Loc.T("Rate")));
                hints.Visibility = Visibility.Hidden;
                stack.Children.Add(hints);
                var browseRow = RegisterCommunityRow(stack, canRate ? () => OpenCommunityRate(p) : (Action)(() => { }),
                    live: true, topMargin: 0, bottomMargin: 10, chip: null,
                    actionLabel: canRate ? Core.Loc.T("Rate") : null);
                _communityRowHints[_communityRows.Count - 1] = hints;
                return browseRow;
            }
            hints.Children.Add(CommunityPadHint(PadButton.A, Core.Loc.T("Use this preset")));
            if (canRate) hints.Children.Add(CommunityPadHint(PadButton.Y, Core.Loc.T("Rate")));
            hints.Visibility = Visibility.Hidden;
            stack.Children.Add(hints);

            var row = RegisterCommunityRow(stack, () => OpenCommunityApply(p), live: true,
                topMargin: 0, bottomMargin: 10, chip: null, actionLabel: Core.Loc.T("Use this preset"));
            int index = _communityRows.Count - 1;
            _communityRowHints[index] = hints;
            if (canRate) _communityRowAlt[index] = (Core.Loc.T("Rate"), () => OpenCommunityRate(p));
            return row;
        }

        private static bool HasThread(CommunityPresets.Preset p) => !string.IsNullOrEmpty(p.Get("threadId"));

        private static string BuildCardDetailLine(CommunityPresets.Preset p)
        {
            var d = new List<string>();
            void Add(string label, string key, string suffix = "")
            {
                string v = p.Get(key);
                if (!string.IsNullOrEmpty(v)) d.Add((label.Length > 1 ? Core.Loc.T(label) + " " : label.Length > 0 ? label + " " : "") + CommunityPresets.Display(key, v) + suffix);
            }
            Add("", "resolution");
            Add("preset", "graphicsPreset");
            Add("upscaler", "upscaler");
            Add("", "upscalerPreset");
            Add("frame gen", "frameGen");
            Add("x", "frameGenFactor");
            Add("CPU boost", "cpuBoostMode");
            Add("cap", "fpsLimit", " FPS");
            Add("", "fpsCapMode");
            return string.Join("  ·  ", d);
        }

        private static string BuildCardGraphicsLine(CommunityPresets.Preset p)
        {
            var g = new List<string>();
            void Add(string label, string key)
            {
                string v = p.Get(key);
                if (!string.IsNullOrEmpty(v)) g.Add(Core.Loc.T(label) + " " + v);
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
            var head = new StackPanel { Margin = new Thickness(0, 32, 0, 12) };
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
                                             Action run, bool live, double topMargin = 0, double bottomMargin = 10,
                                             Brush iconBrush = null)
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
                Foreground = iconBrush ?? (live ? UiHelpers.Text : UiHelpers.Subtle),
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
                                           Func<int> get, Action<int> set,
                                           string glyph = null, Brush glyphBrush = null,
                                           Action run = null, string actionLabel = null)
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (glyph != null)
                grid.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 18,
                    Foreground = glyphBrush ?? UiHelpers.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                });
            Grid.SetColumn(titleBlock, 1);
            grid.Children.Add(titleBlock);
            Grid.SetColumn(chips, 2);
            grid.Children.Add(chips);

            // A chip row with an A action too: Left/Right pick a common value, A types any other.
            return RegisterCommunityRow(grid, run: run, live: true, topMargin: 0, bottomMargin: 10,
                chip: new CommunityChipRow { Count = labels.Length, Get = get, Set = set }, actionLabel: actionLabel);
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
            Child = new TextBlock { Text = text, FontSize = 13, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = active ? ChipActiveText : UiHelpers.Subtle },
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
                _communityRows[i].BorderBrush = i != _communityIndex
                    ? (_communityRowRestBorder.TryGetValue(i, out var rest) ? rest : Brushes.Transparent)
                    : i == _communityChipEditIndex ? FieldOpenBrush
                    : _communityFilledRows.Contains(i) ? Brushes.White : UiHelpers.Accent;

            foreach (var kv in _communityRowHints)
                kv.Value.Visibility = kv.Key == _communityIndex ? Visibility.Visible : Visibility.Hidden;

            if (_communityIndex >= 0 && _communityIndex < _communityRows.Count)
            {
                var target = _communityRows[_communityIndex];
                target.BringIntoView();
                // After a re-render the rows are not laid out yet and BringIntoView does nothing - so
                // ask again once layout has run.
                // Background runs after the Loaded-priority scroll restore in RenderCommunityOverlay,
                // so the restored offset is the starting point and this only nudges when needed.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_communityRows.Contains(target)) target.BringIntoView();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // ════════════════════════════════════ input ════════════════════════════════════════════

        /// <summary>Routed here from MoveGameMenuSelection for the Community overlay.</summary>
        private void MoveCommunitySelection(PadButton dir)
        {
            if (_communityScreen == CommunityScreen.Keyboard) { MoveCommunityKeyboard(dir); return; }

            // Left/Right pick a chip on a chip row - at once on a full-width one, only after A on one
            // that sits in a pair.
            bool editingHere = _communityChipEditIndex == _communityIndex;
            if ((dir == PadButton.Left || dir == PadButton.Right)
                && _communityChipRows.TryGetValue(_communityIndex, out var chip)
                && (!ChipNeedsEdit(_communityIndex) || editingHere))
            {
                int next = chip.Get() + (dir == PadButton.Right ? 1 : -1);
                if (next < 0 || next >= chip.Count) return;
                chip.Set(next);
                RenderGameMenuOverlay();   // cheap re-render keeps the chips and the cursor in step
                // The re-render clears the choosing state with the rows (ClearCommunityRows). Without
                // putting it back, a chip row in a pair took ONE step and then needed A again (user,
                // 2026-10-01).
                if (editingHere)
                {
                    _communityChipEditIndex = _communityIndex;
                    ApplyCommunitySelection();
                }
                RefreshActionBar();
                return;
            }

            // Left/Right cross a side-by-side pair - only when neither half is a chip row, because a
            // chip row needs Left/Right for its own answer.
            // While choosing on a paired chip row, Up/Down do nothing - A or B finish first.
            if (editingHere) return;
            bool lateralPair = _communityPartner.TryGetValue(_communityIndex, out int partner);
            if ((dir == PadButton.Left || dir == PadButton.Right) && lateralPair)
            {
                if ((dir == PadButton.Right) == (partner > _communityIndex))
                {
                    _communityIndex = partner;
                    ApplyCommunitySelection();
                    RefreshActionBar();
                }
                return;
            }

            if (dir != PadButton.Up && dir != PadButton.Down) return;
            if (_communityRows.Count == 0) return;

            int step = dir == PadButton.Down ? 1 : -1;
            int target = _communityIndex;
            // Up/Down leave a lateral pair as ONE row: start from the half on the side of travel.
            if (lateralPair) target = step > 0 ? Math.Max(_communityIndex, partner) : Math.Min(_communityIndex, partner);
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
            if (ChipNeedsEdit(_communityIndex) && _communityRowActions[_communityIndex] == null)
            {
                _communityChipEditIndex = _communityChipEditIndex == _communityIndex ? -1 : _communityIndex;
                ApplyCommunitySelection();
                RefreshActionBar();
                return;
            }
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
            if (ChipNeedsEdit(_communityIndex) && label == null)
                label = _communityChipEditIndex == _communityIndex ? Core.Loc.T("Done") : Core.Loc.T("Change");
            if (!string.IsNullOrEmpty(label)) AddAction(PadButton.A, label, true, ActivateCommunityRow);
            // Y on a card is BOUND but has no footer chip: the focused card shows it already, and the
            // same verb twice on one screen was too much (user, 2026-10-01).
            if (_communityRowAlt.TryGetValue(_communityIndex, out var alt))
                _liveActions[PadButton.Y] = () => alt.Run();
            if (_communityScreen == CommunityScreen.List)
                AddAction(PadButton.X, _communityAllDevices ? Core.Loc.T("Your Claw only") : Core.Loc.T("All devices"),
                          true, ToggleCommunityAllDevices);
            AddAction(PadButton.B, Core.Loc.T("Back"), true, CommunityBack);
        }

        // ════════════════════════════════════ index / identity ══════════════════════════════════

        private void EnsureCommunityIndexThenRedraw(bool forceRefresh)
        {
            _ = CommunityPresets.EnsureLoadedAsync(CancellationToken.None, forceRefresh)
                .ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    // Only if the overlay is still the Community list on this same game.
                    if (_gameMenuOverlay != GameMenuOverlay.Community || (_communityGame == null && !_communityGlobal)) return;
                    RefreshCommunityList();
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
                        if (_communityGame != null) RefreshCommunityList();
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
