using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The banner on the launch screen and the three forms inside the community overlay (rate, share,
    /// keyboard). See CenterMenuWindow.Community.cs for the overlay plumbing and the data flow.
    /// </summary>
    public partial class CenterMenuWindow
    {
        // ════════════════════════════════════ the launch-screen banner ══════════════════════════

        /// <summary>The banner border, held so its focus outline can be set without redrawing the
        /// launch screen — the same trick the achievements row uses.</summary>
        private Border _launchCommunityBanner;

        private DispatcherTimer _communitySlideTimer;
        private int _communitySlide;
        private List<CommunityPresets.Preset> _bannerPresets = new List<CommunityPresets.Preset>();
        private TextBlock _bannerSlideMain;
        private TextBlock _bannerSlideSub;
        private GameEntry _bannerFor;

        /// <summary>True while the launch screen has a banner to move onto. Always true on the confirm
        /// screen: the banner is shown even with no presets, because sharing the first one is the point
        /// of it being there.</summary>
        private bool LaunchCommunityBannerLive => _launchCommunityBanner != null;

        /// <summary>
        /// The community pill in the TOP RIGHT CORNER of the launch screen (user, 2026-10-01): one
        /// line, fully rounded, out of the way of the controller panel below it. A drawn D-pad with its
        /// right arm lit says how to reach it - Right from Play - and A opens the overlay. Counts the
        /// presets for THIS kind of Claw (the list opens on those too), with the all-devices total
        /// after it when there are more.
        /// </summary>
        private Border BuildCommunityBanner(GameEntry game)
        {
            _bannerFor = game;
            var all = CommunityPresets.Loaded && game != null
                ? CommunityPresets.ForGame(game)
                : new List<CommunityPresets.Preset>();
            string myDevice = CommunityPresets.DeviceCode();
            _bannerPresets = all.Where(p => CommunityPresets.SameDeviceFamily(p, myDevice)).ToList();
            _bannerSlideMain = null;
            _bannerSlideSub = null;

            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(BuildDpadGlyph(PadButton.Right, 20));

            line.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Community presets"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            });

            string count = !CommunityPresets.Loaded ? Core.Loc.T("Loading…")
                : _bannerPresets.Count == 0 ? Core.Loc.T("none for your Claw")
                : _bannerPresets.Count == 1 ? Core.Loc.T("1 for your Claw")
                : Core.Loc.F("{0} for your Claw", _bannerPresets.Count);
            if (CommunityPresets.Loaded && all.Count > _bannerPresets.Count)
                count += "  ·  " + Core.Loc.F("{0} in all", all.Count);
            line.Children.Add(new TextBlock
            {
                Text = "  ·  " + count,
                FontSize = 14,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _launchCommunityBanner = new Border
            {
                Child = line,
                Background = LaunchPanelFill,
                // Fully round: half the pill's height (glyph 20 + padding 8+8 + border 2+2).
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(12, 8, 18, 8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 22, 28, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            _launchCommunityBanner.MouseLeftButtonUp += (_, __) =>
            {
                _launchFocus = LaunchFocusCommunity;
                ActivateLaunchSelection();
            };

            EnsureCommunityIndexForBanner(game);
            return _launchCommunityBanner;
        }

        /// <summary>
        /// A small D-pad drawn as a cross with one arm lit. Drawn rather than an image: the D-pad
        /// pictures in the dev repo (XboxGamingBar/Assets/ButtonIcons) are not cleared for publishing,
        /// and Center is a public repository.
        /// </summary>
        private static UIElement BuildDpadGlyph(PadButton lit, double size)
        {
            double arm = size / 3.0;
            var canvas = new Canvas { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            void Cell(double x, double y, bool on)
            {
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width = arm, Height = arm, RadiusX = arm / 4, RadiusY = arm / 4,
                    Fill = on ? Brushes.White : UiHelpers.Subtle,
                    Opacity = on ? 1.0 : 0.55,
                };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
                canvas.Children.Add(r);
            }
            Cell(arm, 0, lit == PadButton.Up);
            Cell(0, arm, lit == PadButton.Left);
            Cell(arm, arm, false);
            Cell(arm * 2, arm, lit == PadButton.Right);
            Cell(arm, arm * 2, lit == PadButton.Down);
            return canvas;
        }

        /// <summary>A footer-style button glyph (the same artwork the action bar draws) and a label -
        /// for the hints on a focused card, instead of "[A]" written out.</summary>
        private static UIElement CommunityPadHint(PadButton b, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            var img = new Image
            {
                Source = Glyphs.For(b),
                Width = 20, Height = 20,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            sp.Children.Add(img);
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
            });
            return sp;
        }

        private void RenderCommunitySlide()
        {
            if (_bannerSlideMain == null || _bannerSlideSub == null) return;

            if (_bannerPresets.Count == 0)
            {
                _bannerSlideMain.Text = Core.Loc.T("Share the first one.");
                _bannerSlideSub.Text = "";
                return;
            }

            if (_communitySlide < 0 || _communitySlide >= _bannerPresets.Count) _communitySlide = 0;
            var p = _bannerPresets[_communitySlide];

            _bannerSlideMain.Text = Core.Loc.F("{0} fps native at {1} W", p.Get("fpsNative"), p.Get("tdpW"));

            var sub = new List<string> { Core.Loc.F("by {0}", p.Get("author")) };
            string stars = CommunityPresets.RatingLine(p);
            if (stars != null) sub.Add(stars);
            else sub.Add(CommunityPresets.DeviceName(p.Get("device")));
            if (_bannerPresets.Count > 1) sub.Add($"{_communitySlide + 1}/{_bannerPresets.Count}");
            _bannerSlideSub.Text = string.Join("   ·   ", sub.Where(s => !string.IsNullOrEmpty(s)));
        }

        private void StartCommunitySlideshow()
        {
            StopCommunitySlideshow();
            if (_bannerPresets.Count <= 1) return;   // nothing to rotate
            _communitySlideTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(4),
            };
            _communitySlideTimer.Tick += (_, __) =>
            {
                if (_launchCommunityBanner == null || _bannerPresets.Count <= 1) { StopCommunitySlideshow(); return; }
                _communitySlide = (_communitySlide + 1) % _bannerPresets.Count;
                RenderCommunitySlide();
            };
            _communitySlideTimer.Start();
        }

        private void StopCommunitySlideshow()
        {
            _communitySlideTimer?.Stop();
            _communitySlideTimer = null;
        }

        /// <summary>Pulls the index the first time a launch screen with a banner is shown, and redraws
        /// the launch screen once when it lands so the count and the slideshow fill in. Same lazy shape
        /// as EnsureCatalogForLaunch.</summary>
        private void EnsureCommunityIndexForBanner(GameEntry game)
        {
            if (CommunityPresets.Loaded) return;
            _ = CommunityPresets.EnsureLoadedAsync(CancellationToken.None)
                .ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    // Only if a launch screen for this same game is still up and no overlay took over.
                    if (_launchPrompt == LaunchPrompt.None || GameMenuOverlayOpen || _optiWikiOpen) return;
                    if (_launchTarget != game) return;
                    RenderLaunchOverlay();
                    RefreshActionBar();
                }), System.Threading.Tasks.TaskScheduler.Default);
        }

        // ════════════════════════════════════ rate ══════════════════════════════════════════════

        private CommunityPresets.Preset _rateTarget;
        private int _rateStars;
        private readonly int[] _rateCats = new int[5];   // 0 = no answer; otherwise option index + 1
        private string _rateComment = "";
        /// <summary>The helper's CommunityRating.CommentMaxLength - the same cap, so the keyboard stops
        /// where the post would be cut.</summary>
        private const int RateCommentMax = 50;

        private void OpenCommunityRate(CommunityPresets.Preset p)
        {
            if (p == null) return;
            _rateTarget = p;
            _rateStars = 0;
            _rateComment = "";
            for (int i = 0; i < _rateCats.Length; i++) _rateCats[i] = 0;
            ResetCommunityScroll(CommunityScreen.Rate);
            _communityScreen = CommunityScreen.Rate;
            _communityIndex = 0;
            _communityNote = null;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void RenderCommunityRate()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            LibraryRoot.Children.Add(CommunityHead(
                Core.Loc.T("Rate this preset"),
                _rateTarget == null ? "" : Core.Loc.F("{0} — by {1}",
                    _rateTarget.Get("gameTitle"), _rateTarget.Get("author"))));

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 24) };

            // Stars and the comment side by side (user, 2026-10-01), the comment created right after
            // the stars so the cursor reaches it with one Down. Every number carries its star.
            var starsRow = AddCommunityChipRow(
                Core.Loc.T("Stars"),
                new[] { "1\u2605", "2\u2605", "3\u2605", "4\u2605", "5\u2605" },
                null,
                () => _rateStars - 1 < 0 ? 0 : _rateStars - 1,
                v => _rateStars = v + 1);

            // The comment, exactly as the widget and the helper have it: optional, 50 characters
            // (CommunityRating.CommentMaxLength), typed on the on-screen keyboard. The helper normalises
            // and caps it again - it is the one that posts.
            var commentRow = AddCommunityActionRow(
                string.IsNullOrEmpty(_rateComment) ? "\uE90A" : GlyphFieldSet,
                Core.Loc.T("Comment (optional)"),
                string.IsNullOrEmpty(_rateComment) ? Core.Loc.T("Up to 50 characters") : _rateComment,
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("Comment (50 characters)"), _rateComment, 0, RateCommentMax,
                    digitsOnly: false, onCommit: t => _rateComment = (t ?? "").Trim(), allowSpace: true),
                live: true,
                iconBrush: string.IsNullOrEmpty(_rateComment) ? null : UiHelpers.Ok);
            panel.Children.Add(CommunityPair(starsRow, commentRow));

            // One chip row per category, "No answer" first.
            for (int c = 0; c < CommunityPresets.RatingCategories.Length; c++)
            {
                var cat = CommunityPresets.RatingCategories[c];
                var labels = new List<string> { Core.Loc.T("No answer") };
                labels.AddRange(cat.Options.Select(o => o.Text));
                int captured = c;
                panel.Children.Add(AddCommunityChipRow(
                    cat.Label,
                    labels.ToArray(),
                    null,
                    () => _rateCats[captured],
                    v => _rateCats[captured] = v));
            }

            if (_rateStars < 1)
                panel.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Pick one to five stars first."),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 6),
                });
            panel.Children.Add(AddCommunityButton(Core.Loc.T("Submit rating"), Core.Loc.T("Submit"),
                                                  SubmitCommunityRate, CommunityShareFill, topMargin: 6));

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

        private async void SubmitCommunityRate()
        {
            var p = _rateTarget;
            if (p == null) return;
            if (_rateStars < 1) { _communityNote = Core.Loc.T("Pick one to five stars first."); RenderGameMenuOverlay(); RefreshActionBar(); return; }

            var lines = new List<string>
            {
                "presetId=" + p.Get("presetId"),
                "threadId=" + p.Get("threadId"),
                // The PRESET's author id, so the helper can refuse a self-rating early. The helper
                // re-derives and enforces it either way.
                "presetAuthorId=" + p.Get("authorId"),
                "stars=" + _rateStars.ToString(CultureInfo.InvariantCulture),
                "author=" + _communityNickname,
            };
            if (!string.IsNullOrWhiteSpace(_rateComment))
                lines.Add("comment=" + _rateComment.Replace('\n', ' ').Replace('\r', ' '));
            for (int c = 0; c < CommunityPresets.RatingCategories.Length; c++)
            {
                if (_rateCats[c] <= 0) continue;
                var cat = CommunityPresets.RatingCategories[c];
                lines.Add(cat.Key + "=" + cat.Options[_rateCats[c] - 1].Value);
            }

            _communityNote = Core.Loc.T("Sending your rating…");
            RenderGameMenuOverlay();
            RefreshActionBar();

            string error = await SendCommunityAsync("CommunityRate", string.Join("\n", lines),
                                                    Shared.Enums.Function.CommunityRateResult);
            if (_gameMenuOverlay != GameMenuOverlay.Community) return;

            if (string.IsNullOrEmpty(error))
            {
                ShowCommunityMessage(Core.Loc.T("Thanks — your rating is in."),
                    Core.Loc.T("It can take a few hours until your rating appears here."));
                EnsureCommunityIndexThenRedraw(forceRefresh: true);
            }
            else
            {
                _communityNote = error;
                _communityScreen = CommunityScreen.Rate;
                RenderGameMenuOverlay();
                RefreshActionBar();
            }
        }

        // ════════════════════════════════════ share (create) ════════════════════════════════════

        private static readonly string[] CwResolutions =
            { "1920x1200", "1920x1080", "1600x1000", "1280x800", "1280x720" };
        // DLSS is dropped from the offered list (the Claw is Intel) but still accepted if it ever
        // arrives — see the helper's Allowed table. "off" first, as a starting point, not an answer.
        private static readonly string[] CwUpscalers = { "off", "xess", "fsr", "tsr", "other" };
        private static readonly string[] CwUpscalerPresets =
            { "ultraperformance", "performance", "balanced", "quality", "ultraquality" };
        private static readonly string[] CwUpscalerSources = { "ingame", "optiscaler-opticlick", "optiscaler-manual" };
        private static readonly string[] CwGraphics = { "low", "medium", "high", "ultra", "custom" };
        private static readonly string[] CwFrameGen = { "off", "on" };
        private static readonly string[] CwFrameGenFactors = { "2", "3", "4" };
        private static readonly string[] CwDetailLowUltra = { "low", "medium", "high", "ultra" };
        private static readonly string[] CwShadows = { "off", "low", "medium", "high", "ultra" };
        private static readonly string[] CwAniso = { "off", "2x", "4x", "8x", "16x" };

        private ClawProfileDetails.CommunityFacts _cwFacts = new ClawProfileDetails.CommunityFacts();
        private string _cwTdp = "";
        // The frame cap and its limiter (schema v2). Prefilled from the game's profile, editable here
        // (user, 2026-10-01: the cap was taken silently and never shown). 0 = no cap.
        private int _cwCapMode;             // 0 not capped, 1 Intel, 2 RTSS - the order of the chips
        private static readonly string[] CwCapModes = { "", "intel", "rtss" };

        /// <summary>This Claw's PL1 ceiling - the highest TDP a preset can honestly state for it.
        /// The helper's MSIClawModels values: A2VM 30, EX 35, A1M 43.</summary>
        private static int CwTdpMax()
        {
            switch (Core.DeviceDetect.Detect().Model)
            {
                case Core.DeviceDetect.Model.Ex:  return 35;
                case Core.DeviceDetect.Model.A1M: return 43;
                default:                          return 30;
            }
        }
        private int _cwFps;
        private int _cwPowerState;          // 0 battery, 1 plugged
        private int _cwResolution;
        private int _cwGraphics = 2;        // high
        private int _cwUpscaler;            // off
        private int _cwUpscalerPreset = 2;  // balanced
        private int _cwUpscalerSource;      // ingame
        private int _cwFrameGen;            // off
        private int _cwFrameGenFactor;      // 2
        private bool _cwDetail;
        private readonly int[] _cwDetailValues = new int[6];   // texture, shadows, aniso, lighting, viewDist, chars

        /// <summary>
        /// Fills the six detail rows from the overall graphics preset (user, 2026-10-01): switching
        /// the detail on after picking "medium" should start from medium everywhere, so only the
        /// settings that differ need touching. Custom has no level to copy and leaves them alone.
        /// Shadows and anisotropic filtering have an extra "off" step first, so they sit one higher;
        /// anisotropic maps low/medium/high/ultra to 2x/4x/8x/16x.
        /// </summary>
        private void SeedDetailFromPreset()
        {
            int level = Array.IndexOf(CwDetailLowUltra, CwGraphics[_cwGraphics]);   // -1 for custom
            if (level < 0) return;
            _cwDetailValues[0] = level;                 // textures
            _cwDetailValues[1] = level + 1;             // shadows (off, low, …)
            _cwDetailValues[2] = level + 1;             // aniso (off, 2x, 4x, 8x, 16x)
            _cwDetailValues[3] = level;                 // lighting
            _cwDetailValues[4] = level;                 // view distance
            _cwDetailValues[5] = level;                 // characters
        }

        private void OpenCommunityCreate()
        {
            var game = _communityGame;
            if (game == null) return;

            _cwFacts = ClawProfileDetails.CommunityFactsFor(game);
            _cwTdp = _cwFacts.TdpW;
            // A cap in the profile prefills "capped, with that limiter"; the number is the FPS chip.
            _cwCapMode = string.IsNullOrEmpty(_cwFacts.FpsLimit) ? 0 : _cwFacts.FpsCapMode == "rtss" ? 2 : 1;
            _cwFps = 0;
            _cwPowerState = Core.PowerLine.OnMains() ? 1 : 0;
            _cwResolution = Math.Max(0, Array.IndexOf(CwResolutions, _cwFacts.Resolution));
            _cwGraphics = 2; _cwUpscaler = 0; _cwUpscalerPreset = 2; _cwUpscalerSource = 0;
            _cwFrameGen = 0; _cwFrameGenFactor = 0; _cwDetail = false;
            SeedDetailFromPreset();

            ResetCommunityScroll(CommunityScreen.Create);
            _communityScreen = CommunityScreen.Create;
            _communityIndex = 0;
            _communityNote = null;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void RenderCommunityCreate()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            LibraryRoot.Children.Add(CommunityHead(
                Core.Loc.F("Share your preset — {0}", _communityGame?.Title ?? ""),
                Core.Loc.T("These settings are posted publicly, with an anonymous id that links your posts.")));

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 24) };

            // The two headline numbers as plain fields; A opens the keyboard, whose top row offers the
            // common values (user, 2026-10-01). As chips on the form they took Left/Right away from
            // moving between the two columns. The CAP is not a third number: when the game was capped
            // the native FPS IS the cap, so the only question is with which limiter.
            bool fpsSet = _cwFps > 0;
            int tdpMax = CwTdpMax();
            bool tdpSet = int.TryParse(_cwTdp, out int tdpNow) && tdpNow >= 5 && tdpNow <= tdpMax;

            var fpsRow = AddCommunityActionRow(fpsSet ? GlyphFieldSet : GlyphFieldOpen, Core.Loc.T("Native FPS"),
                fpsSet ? _cwFps.ToString(CultureInfo.InvariantCulture) : Core.Loc.T("Without frame generation"),
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("Native FPS"), _cwFps > 0 ? _cwFps.ToString(CultureInfo.InvariantCulture) : "",
                    1, 4, digitsOnly: true, onCommit: t =>
                    {
                        _cwFps = int.TryParse(t, out int v) ? Math.Max(1, Math.Min(1000, v)) : 0;
                    },
                    quickValues: new[] { 30, 40, 60, 90, 120 }),
                live: true,
                iconBrush: fpsSet ? UiHelpers.Ok : FieldOpenBrush);
            // One name in every language (user, 2026-10-01: "Begrenzt" read oddly) - not translated.
            var capRow = AddCommunityChipRow("FPS Limiter",
                new[] { Core.Loc.T("Off"), "Intel", "RTSS" }, null, () => _cwCapMode, v => _cwCapMode = v);
            panel.Children.Add(CommunityPair(fpsRow, capRow));

            // TDP up to this Claw's PL1 ceiling - a value it cannot run is not a preset. PL2 is
            // deliberately not part of a preset (it left the schema's display on 2026-09-06).
            var tdpQuick = new List<int> { 8, 12, 15, 17, 20, 25, 30 }.Where(v => v <= tdpMax).ToList();
            if (!tdpQuick.Contains(tdpMax)) tdpQuick.Add(tdpMax);
            var tdpRow = AddCommunityActionRow(tdpSet ? GlyphFieldSet : GlyphFieldOpen, Core.Loc.T("TDP (W)"),
                tdpSet ? _cwTdp + " W" : Core.Loc.T("Not set"),
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("TDP in watts"), _cwTdp, 1, 2, digitsOnly: true, onCommit: t =>
                    {
                        _cwTdp = int.TryParse(t, out int v) ? Math.Max(5, Math.Min(tdpMax, v)).ToString(CultureInfo.InvariantCulture) : _cwTdp;
                    },
                    quickValues: tdpQuick),
                live: true,
                iconBrush: tdpSet ? UiHelpers.Ok : FieldOpenBrush);
            var powerRow = AddCommunityChipRow(Core.Loc.T("Power"),
                new[] { Core.Loc.T("on battery"), Core.Loc.T("plugged in") }, null,
                () => _cwPowerState, v => _cwPowerState = v);
            panel.Children.Add(CommunityPair(tdpRow, powerRow));


            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Resolution"),
                CwResolutions, null, () => _cwResolution, v => _cwResolution = v));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Graphics preset"),
                CwGraphics, null, () => _cwGraphics, v =>
                {
                    _cwGraphics = v;
                    if (CwGraphics[v] == "custom") _cwDetail = true;
                    else SeedDetailFromPreset();   // the detail rows follow the overall preset
                }));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Upscaler"),
                CwUpscalers, null, () => _cwUpscaler, v => _cwUpscaler = v));

            if (CwUpscalers[_cwUpscaler] != "off")
                panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Upscaler quality"),
                    CwUpscalerPresets, null, () => _cwUpscalerPreset, v => _cwUpscalerPreset = v));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Frame generation"),
                CwFrameGen, null, () => _cwFrameGen, v => _cwFrameGen = v));

            if (CwFrameGen[_cwFrameGen] == "on")
                panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Frame gen factor"),
                    CwFrameGenFactors.Select(f => f + "x").ToArray(), null, () => _cwFrameGenFactor, v => _cwFrameGenFactor = v));

            if (CwUpscalers[_cwUpscaler] != "off" || CwFrameGen[_cwFrameGen] == "on")
                panel.Children.Add(AddCommunityChipRow(Core.Loc.T("How it was set"),
                    new[] { Core.Loc.T("in-game"), Core.Loc.T("OptiScaler (OptiClick)"), Core.Loc.T("OptiScaler (manual)") },
                    null, () => _cwUpscalerSource, v => _cwUpscalerSource = v));

            // NOT a chip row like the answers around it: this one OPENS A SECTION (user, 2026-10-01),
            // so it looks like an expander - a chevron, no fill, an outline - and A folds it open or
            // shut. Opening seeds the six rows from the overall preset.
            var detailRow = AddCommunityActionRow(_cwDetail ? "\uE70D" : "\uE76C",
                Core.Loc.T("Share detailed graphics"),
                _cwDetail ? Core.Loc.T("Textures, shadows, lighting and more") : null,
                _cwDetail ? Core.Loc.T("Hide") : Core.Loc.T("Show"),
                () =>
                {
                    if (!_cwDetail) SeedDetailFromPreset();
                    _cwDetail = !_cwDetail;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                },
                live: true, topMargin: 4);
            detailRow.Background = Brushes.Transparent;
            // The resting outline (the cursor recolours it and gives it back - ApplyCommunitySelection).
            _communityRowRestBorder[(int)detailRow.Tag] = UiHelpers.Subtle;
            panel.Children.Add(detailRow);

            if (_cwDetail)
            {
                string[] detailLabels = { Core.Loc.T("Textures"), Core.Loc.T("Shadows"), Core.Loc.T("Anisotropic"),
                                          Core.Loc.T("Lighting"), Core.Loc.T("View distance"), Core.Loc.T("Characters") };
                string[][] detailOptions = { CwDetailLowUltra, CwShadows, CwAniso, CwDetailLowUltra, CwDetailLowUltra, CwDetailLowUltra };
                for (int d = 0; d < detailLabels.Length; d++)
                {
                    int captured = d;
                    panel.Children.Add(AddCommunityChipRow(detailLabels[d], detailOptions[d], null,
                        () => _cwDetailValues[captured], v => _cwDetailValues[captured] = v));
                }
            }

            bool nickSet = !string.IsNullOrEmpty(_communityNickname) && _communityNickname.Length >= 5 && _communityNickname.Length <= 13;
            panel.Children.Add(AddCommunityActionRow(nickSet ? GlyphFieldSet : GlyphFieldOpen, Core.Loc.T("Nickname"),
                string.IsNullOrEmpty(_communityNickname) ? Core.Loc.T("required — 5 to 13 characters") : _communityNickname,
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("Nickname (5–13 characters)"), _communityNickname, 5, 13,
                    digitsOnly: false, onCommit: t => _communityNickname = t),
                live: true,
                iconBrush: nickSet ? UiHelpers.Ok : FieldOpenBrush));

            panel.Children.Add(AddCommunityButton(Core.Loc.T("Share preset"), Core.Loc.T("Share"),
                                                  SubmitCommunityCreate, CommunityShareFill, topMargin: 6));

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

        private async void SubmitCommunityCreate()
        {
            var game = _communityGame;
            if (game == null) return;

            // Local checks first, so the common refusals do not cost a round trip. The helper validates
            // everything again — it holds the key, so it has the final say.
            if (_cwFps < 1) { SetCommunityNote(Core.Loc.T("Set the native FPS first.")); return; }
            if (!int.TryParse(_cwTdp, out int tdp) || tdp < 5 || tdp > CwTdpMax())
            { SetCommunityNote(Core.Loc.F("Set a TDP between 5 and {0} W.", CwTdpMax())); return; }
            if (string.IsNullOrEmpty(_communityNickname) || _communityNickname.Length < 5 || _communityNickname.Length > 13)
            { SetCommunityNote(Core.Loc.T("A nickname of 5 to 13 characters is required.")); return; }

            string device = !string.IsNullOrEmpty(_communityDevice) ? _communityDevice : CommunityPresets.DeviceCode();
            if (string.IsNullOrEmpty(device)) { SetCommunityNote(Core.Loc.T("This Claw was not identified.")); return; }

            var sb = new StringBuilder();
            void Put(string k, string v) { if (!string.IsNullOrEmpty(v) && v != "-") sb.Append(k).Append('=').Append(v).Append('\n'); }

            Put("device", device);
            Put("gameStore", CommunityPresets.StoreCode(game.Store));
            Put("gameId", game.Id);
            Put("gameTitle", game.Title);
            Put("powerState", _cwPowerState == 1 ? "plugged" : "battery");
            Put("tdpW", tdp.ToString(CultureInfo.InvariantCulture));
            Put("fpsNative", _cwFps.ToString(CultureInfo.InvariantCulture));
            Put("author", _communityNickname);

            // Extras taken from the game's saved profile — nice to have, not required.
            Put("cpuBoostMode", _cwFacts.CpuBoostMode);
            Put("osPowerMode", _cwFacts.OsPowerMode);
            // The cap as set on this form (prefilled from the profile), and which limiter it is on.
            // CAPPED = the native FPS IS the cap: one number, not two (user, 2026-10-01).
            if (_cwCapMode > 0)
            {
                Put("fpsLimit", _cwFps.ToString(CultureInfo.InvariantCulture));
                Put("fpsCapMode", CwCapModes[_cwCapMode]);
            }

            Put("resolution", CwResolutions[_cwResolution]);
            Put("graphicsPreset", CwGraphics[_cwGraphics]);

            string upscaler = CwUpscalers[_cwUpscaler];
            string frameGen = CwFrameGen[_cwFrameGen];
            Put("upscaler", upscaler);
            Put("frameGen", frameGen);

            // Dependencies enforced by DROPPING, not by trusting the form to have hidden the row: a
            // stale value behind a changed answer is how "off / quality" gets into a database.
            if (upscaler != "off") Put("upscalerPreset", CwUpscalerPresets[_cwUpscalerPreset]);
            if (frameGen == "on") Put("frameGenFactor", CwFrameGenFactors[_cwFrameGenFactor]);
            if (upscaler != "off" || frameGen == "on") Put("upscalerSource", CwUpscalerSources[_cwUpscalerSource]);

            if (_cwDetail)
            {
                Put("texture", CwDetailLowUltra[_cwDetailValues[0]]);
                Put("shadows", CwShadows[_cwDetailValues[1]]);
                Put("aniso", CwAniso[_cwDetailValues[2]]);
                Put("lighting", CwDetailLowUltra[_cwDetailValues[3]]);
                Put("viewDistance", CwDetailLowUltra[_cwDetailValues[4]]);
                Put("characters", CwDetailLowUltra[_cwDetailValues[5]]);
            }

            _communityNote = Core.Loc.T("Posting your preset…");
            RenderGameMenuOverlay();
            RefreshActionBar();

            string error = await SendCommunityAsync("CommunitySubmit", sb.ToString(),
                                                    Shared.Enums.Function.CommunitySubmitResult);
            if (_gameMenuOverlay != GameMenuOverlay.Community) return;

            if (string.IsNullOrEmpty(error))
            {
                ShowCommunityMessage(Core.Loc.T("Shared — thank you."),
                    Core.Loc.T("It can take a few hours until your preset appears here."));
                EnsureCommunityIndexThenRedraw(forceRefresh: true);
            }
            else
            {
                _communityNote = error;
                ResetCommunityScroll(CommunityScreen.Create);
            _communityScreen = CommunityScreen.Create;
                RenderGameMenuOverlay();
                RefreshActionBar();
            }
        }

        private void SetCommunityNote(string note)
        {
            _communityNote = note;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        /// <summary>One place for the Center→helper round-trip both forms use. Returns "" on success,
        /// a reason on refusal, or a short message when the helper could not be reached.</summary>
        private async System.Threading.Tasks.Task<string> SendCommunityAsync(string extraKey, string payload,
                                                                             Shared.Enums.Function resultFunction)
        {
            try
            {
                if (!await EnsureHelperAsync()) return Core.Loc.T("ClawTweaks is not running.");
                string reply = await _helperPipe.RequestWithResultAsync(extraKey, payload, resultFunction,
                                                                        TimeSpan.FromSeconds(30));
                return reply ?? Core.Loc.T("No answer from ClawTweaks.");
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Community " + extraKey + " failed: " + ex.Message);
                return Core.Loc.T("The request failed.");
            }
        }

        // ════════════════════════════════════ apply (use this preset) ════════════════════════════
        //
        // A on a preset card. Writes the power half of the preset into this game's per-game profile -
        // through the HELPER, never into the file directly: ProfileManager holds every profile in
        // memory and rewrites the file on its next save, so a Center-side write would vanish. The game
        // is usually not running here, so the helper writes the profile (found or created by exe path)
        // instead of setting live values the way the widget does. Same field set as the widget's
        // PerfFromCommunity: TDP, CPU boost mode, OS power mode, frame cap. Resolution and in-game
        // graphics describe what the poster did inside the game and are not ours to set.

        private CommunityPresets.Preset _applyTarget;

        private void OpenCommunityApply(CommunityPresets.Preset p)
        {
            if (p == null) return;
            _applyTarget = p;
            ResetCommunityScroll(CommunityScreen.Apply);
            _communityScreen = CommunityScreen.Apply;
            _communityIndex = 0;
            _communityNote = null;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void RenderCommunityApply()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var p = _applyTarget;
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(CommunityHead(Core.Loc.T("Use this preset"),
                p == null ? "" : Core.Loc.F("{0} — by {1}", _communityGame?.Title ?? p.Get("gameTitle"), p.Get("author"))));

            if (p != null)
            {
                var sets = new List<string>();
                if (!string.IsNullOrEmpty(p.Get("tdpW"))) sets.Add(Core.Loc.F("TDP {0} W", p.Get("tdpW")));
                if (!string.IsNullOrEmpty(p.Get("cpuBoostMode")))
                    sets.Add(Core.Loc.T("CPU boost") + " " + CommunityPresets.Display("cpuBoostMode", p.Get("cpuBoostMode")));
                if (!string.IsNullOrEmpty(p.Get("osPowerMode")))
                    sets.Add(Core.Loc.T("Power mode") + " " + CommunityPresets.Display("osPowerMode", p.Get("osPowerMode")));
                if (!string.IsNullOrEmpty(p.Get("fpsLimit")))
                    sets.Add(Core.Loc.F("FPS cap {0}", p.Get("fpsLimit"))
                             + (p.Get("fpsCapMode").Length > 0 ? " (" + CommunityPresets.Display("fpsCapMode", p.Get("fpsCapMode")) + ")" : ""));

                stack.Children.Add(new TextBlock
                {
                    Text = sets.Count > 0 ? string.Join("   ·   ", sets) : Core.Loc.T("This preset states no power values."),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Saved to this game's profile. Resolution and in-game graphics stay as they are."),
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 20),
                });
                if (sets.Count > 0)
                    stack.Children.Add(AddCommunityButton(Core.Loc.T("Apply"), Core.Loc.T("Apply"),
                                                          ConfirmCommunityApply, CommunityShareFill));
            }

            Grid.SetRow(stack, 0);
            LibraryRoot.Children.Add(stack);
            ClampCommunityIndex();
            ApplyCommunitySelection();
        }

        private async void ConfirmCommunityApply()
        {
            var p = _applyTarget;
            var game = _communityGame;
            if (p == null || game == null) return;

            // The profile is keyed on the exe. Steam does not name it; an existing profile or a post
            // that names a file really in the install folder does.
            string exe = CommunityPresets.ResolveExe(game, new[] { p }.Concat(_communityListAll));
            if (string.IsNullOrEmpty(exe))
            {
                SetCommunityNote(Core.Loc.T("Start the game once, then try again."));
                return;
            }

            var sb = new StringBuilder();
            void Put(string k, string v) { if (!string.IsNullOrEmpty(v) && v != "-") sb.Append(k).Append('=').Append(v).Append('\n'); }
            Put("exePath", exe);
            Put("gameName", game.Title);
            Put("tdpW", p.Get("tdpW"));
            Put("pl2W", p.Get("pl2W"));
            Put("cpuBoostMode", p.Get("cpuBoostMode"));
            Put("osPowerMode", p.Get("osPowerMode"));
            Put("fpsLimit", p.Get("fpsLimit"));
            Put("fpsCapMode", p.Get("fpsCapMode"));
            Put("author", p.Get("author"));

            SetCommunityNote(Core.Loc.T("Applying…"));
            string reply = await SendCommunityAsync("CommunityApplyPreset", sb.ToString(),
                                                    Shared.Enums.Function.CommunityApplyResult);
            if (_gameMenuOverlay != GameMenuOverlay.Community) return;

            if (reply == "ok" || reply == "ok-running")
            {
                // The launch screen's profile panels read the files; let them see the new one.
                try { ClawProfiles.Refresh(); } catch { }
                ShowCommunityMessage(Core.Loc.T("Preset applied."),
                    reply == "ok-running"
                        ? Core.Loc.T("It takes effect the next time the game starts.")
                        : Core.Loc.T("It is used the next time you start the game."));
            }
            else
            {
                string why = reply != null && reply.StartsWith("error=") ? reply.Substring(6) : reply;
                SetCommunityNote(string.IsNullOrEmpty(why) ? Core.Loc.T("The request failed.") : why);
            }
        }

        // ════════════════════════════════════ the "posted" message ══════════════════════════════

        private string _communityMessageHead;
        private string _communityMessageBody;

        private void ShowCommunityMessage(string head, string body)
        {
            _communityMessageHead = head;
            _communityMessageBody = body;
            ResetCommunityScroll(CommunityScreen.Message);
            _communityScreen = CommunityScreen.Message;
            _communityIndex = 0;
            _communityNote = null;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void RenderCommunityMessage()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // As wide as the rows it holds (MinWidth 620) - at 560 the "Back to the list" row
                // was cut off on the right after every submit (user, 2026-10-01).
                MaxWidth = 720,
            };
            stack.Children.Add(new TextBlock
            {
                Text = _communityMessageHead,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = _communityMessageBody,
                FontSize = 15,
                Foreground = UiHelpers.Subtle,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });

            var done = AddCommunityActionRow("", Core.Loc.T("Back to the list"), null, Core.Loc.T("OK"),
                () => { _communityScreen = CommunityScreen.List; _communityIndex = 0; RenderGameMenuOverlay(); RefreshActionBar(); },
                live: true, topMargin: 24);
            stack.Children.Add(done);

            Grid.SetRow(stack, 0);
            LibraryRoot.Children.Add(stack);
            ClampCommunityIndex();
            ApplyCommunitySelection();
        }

        // ════════════════════════════════════ the on-screen keyboard ════════════════════════════
        // Ported from the widget's MiniKeyboard. A grid of character keys driven by the D-pad; A types
        // the focused key, X backspaces, Y shifts (one-shot), B cancels, and a ✓ key commits. No
        // TextBox — a handheld with a controller cannot drive one, and this matches the widget the
        // user asked to carry over.

        private static readonly string[] CommunityKbRows =
        {
            "1234567890",
            "qwertyuiop",
            "asdfghjkl_",
            "zxcvbnm-!?",
            " ✓",          // space (comments only), then ✓ commit
        };

        // QUICK VALUES (user, 2026-10-01): the common numbers for a field - 30/60/90/120 FPS, the TDP
        // steps - as one row of keys ABOVE the digits. Pressing one takes that value and closes the
        // keyboard. They live here and not as chips on the form, because chips on the form took
        // Left/Right away from moving between the form's own fields.
        private string[] _communityKbQuick;

        /// <summary>The rows as drawn, quick values first: each key is a string (a quick value is
        /// several characters). Every index into the grid goes through this one list.</summary>
        private List<string[]> KbRows()
        {
            var rows = new List<string[]>();
            if (_communityKbQuick != null && _communityKbQuick.Length > 0) rows.Add(_communityKbQuick);
            foreach (string r in CommunityKbRows) rows.Add(r.Select(c => c.ToString()).ToArray());
            return rows;
        }

        private bool KbRowIsQuick(int r) => r == 0 && _communityKbQuick != null && _communityKbQuick.Length > 0;

        private readonly StringBuilder _communityKbBuffer = new StringBuilder();
        private int _communityKbMin, _communityKbMax;
        // Only the rating comment takes spaces; a nickname or a number must not.
        private bool _communityKbAllowSpace;
        private bool _communityKbDigitsOnly, _communityKbShift;
        private Action<string> _communityKbCommit;
        private string _communityKbTitle;
        private CommunityScreen _communityKbReturn;
        private int _communityKbRow, _communityKbCol;
        private TextBlock _communityKbBufferBlock, _communityKbHintBlock;
        private readonly List<List<Border>> _communityKbKeys = new List<List<Border>>();

        private void OpenCommunityKeyboard(string title, string initial, int min, int max,
                                           bool digitsOnly, Action<string> onCommit, bool allowSpace = false,
                                           IEnumerable<int> quickValues = null)
        {
            _communityKbBuffer.Clear();
            if (!string.IsNullOrEmpty(initial)) _communityKbBuffer.Append(initial);
            _communityKbMin = Math.Max(0, min);
            _communityKbMax = Math.Max(1, max);
            _communityKbDigitsOnly = digitsOnly;
            _communityKbAllowSpace = allowSpace && !digitsOnly;
            _communityKbQuick = quickValues?.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray();
            _communityKbShift = false;
            _communityKbCommit = onCommit;
            _communityKbTitle = title;
            _communityKbReturn = _communityScreen;
            _communityKbRow = 0;
            _communityKbCol = 0;
            _communityScreen = CommunityScreen.Keyboard;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void RenderCommunityKeyboard()
        {
            _communityKbKeys.Clear();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var outer = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 640,
            };
            outer.Children.Add(new TextBlock
            {
                Text = _communityKbTitle ?? Core.Loc.T("Enter text"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            _communityKbBufferBlock = new TextBlock
            {
                FontSize = 26,
                Foreground = UiHelpers.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 2),
            };
            _communityKbHintBlock = new TextBlock
            {
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
            };
            outer.Children.Add(_communityKbBufferBlock);
            outer.Children.Add(_communityKbHintBlock);

            var rows = KbRows();
            for (int r = 0; r < rows.Count; r++)
            {
                bool quick = KbRowIsQuick(r);
                var rowKeys = new List<Border>();
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, quick ? 10 : 0),
                };
                for (int c = 0; c < rows[r].Length; c++)
                {
                    string k = rows[r][c];
                    bool commit = !quick && k == "✓";
                    bool space = !quick && k == " ";
                    bool enabled = quick ? true
                                 : commit ? _communityKbBuffer.Length >= _communityKbMin
                                 : space ? _communityKbAllowSpace
                                 : (!_communityKbDigitsOnly || char.IsDigit(k[0]));
                    string face = commit ? "✓ " + Core.Loc.T("Done")
                                 : space ? Core.Loc.T("Space")
                                 : quick ? k
                                 : (_communityKbShift && char.IsLetter(k[0]) ? k.ToUpperInvariant() : k);

                    var key = new Border
                    {
                        Child = new TextBlock { Text = face, FontSize = 18, Foreground = UiHelpers.Text,
                                                FontWeight = quick ? FontWeights.SemiBold : FontWeights.Normal,
                                                HorizontalAlignment = HorizontalAlignment.Center },
                        Background = quick ? CommunityQuickKeyFill : UiHelpers.Card,
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(2),
                        BorderBrush = Brushes.Transparent,
                        Padding = new Thickness(commit || quick ? 18 : 12, 10, commit || quick ? 18 : 12, 10),
                        Margin = new Thickness(4),
                        MinWidth = commit ? 0 : 40,
                        Opacity = enabled ? 1.0 : 0.4,
                        Cursor = System.Windows.Input.Cursors.Hand,
                    };
                    int cr = r, cc = c;
                    key.MouseLeftButtonUp += (_, __) => { _communityKbRow = cr; _communityKbCol = cc; PressCommunityKey(); };
                    rowKeys.Add(key);
                    rowPanel.Children.Add(key);
                }
                _communityKbKeys.Add(rowKeys);
                outer.Children.Add(rowPanel);
            }

            Grid.SetRow(outer, 0);
            LibraryRoot.Children.Add(outer);
            RefreshCommunityKbVisuals();
        }

        /// <summary>The quick-value keys stand out from the digits under them.</summary>
        private static readonly Brush CommunityQuickKeyFill = Frozen(Color.FromRgb(0x2A, 0x3A, 0x55));

        private void RefreshCommunityKbVisuals()
        {
            string text = _communityKbBuffer.ToString();
            if (_communityKbBufferBlock != null) _communityKbBufferBlock.Text = text.Length == 0 ? "–" : text;
            if (_communityKbHintBlock != null)
                _communityKbHintBlock.Text = text.Length < _communityKbMin
                    ? Core.Loc.F("At least {0} characters. {1} so far.", _communityKbMin, text.Length)
                    : Core.Loc.F("{0} of {1} characters.", text.Length, _communityKbMax);

            for (int r = 0; r < _communityKbKeys.Count; r++)
                for (int c = 0; c < _communityKbKeys[r].Count; c++)
                    _communityKbKeys[r][c].BorderBrush =
                        (r == _communityKbRow && c == _communityKbCol) ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveCommunityKeyboard(PadButton dir)
        {
            var rows = KbRows();
            int r = _communityKbRow, c = _communityKbCol;
            switch (dir)
            {
                case PadButton.Left:  c--; break;
                case PadButton.Right: c++; break;
                case PadButton.Up:    r--; break;
                case PadButton.Down:  r++; break;
                default: return;
            }
            if (r < 0 || r >= rows.Count) return;
            // Clamp the column into the new row - the rows are not all the same length.
            if (c < 0) c = 0;
            if (c >= rows[r].Length) c = rows[r].Length - 1;
            _communityKbRow = r;
            _communityKbCol = c;
            RefreshCommunityKbVisuals();
        }

        private void PressCommunityKey()
        {
            var rows = KbRows();
            if (_communityKbRow < 0 || _communityKbRow >= rows.Count) return;
            string[] row = rows[_communityKbRow];
            if (_communityKbCol < 0 || _communityKbCol >= row.Length) return;
            string k = row[_communityKbCol];

            // A quick value IS the answer: take it and close.
            if (KbRowIsQuick(_communityKbRow))
            {
                _communityKbBuffer.Clear().Append(k);
                CommitCommunityKeyboard();
                return;
            }

            char ch = k[0];
            if (ch == '✓') { CommitCommunityKeyboard(); return; }
            if (_communityKbDigitsOnly && !char.IsDigit(ch)) return;
            if (ch == ' ' && !_communityKbAllowSpace) return;
            if (_communityKbBuffer.Length >= _communityKbMax) return;

            _communityKbBuffer.Append(_communityKbShift && char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch);
            _communityKbShift = false;   // one-shot
            RenderGameMenuOverlay();      // the key faces change case with shift
            RefreshActionBar();
        }

        private void CommunityKeyBackspace()
        {
            if (_communityKbBuffer.Length > 0) _communityKbBuffer.Length -= 1;
            RefreshCommunityKbVisuals();
        }

        private void CommunityKeyToggleShift()
        {
            if (_communityKbDigitsOnly) return;
            _communityKbShift = !_communityKbShift;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private string CommunityKeyLabel()
        {
            var rows = KbRows();
            if (_communityKbRow >= 0 && _communityKbRow < rows.Count)
            {
                if (KbRowIsQuick(_communityKbRow)) return Core.Loc.T("Use");
                string[] row = rows[_communityKbRow];
                if (_communityKbCol >= 0 && _communityKbCol < row.Length && row[_communityKbCol] == "✓")
                    return Core.Loc.T("Done");
            }
            return Core.Loc.T("Type");
        }

        private void CancelCommunityKeyboard()
        {
            _communityKbCommit = null;
            _communityScreen = _communityKbReturn;
            RenderGameMenuOverlay();
            RefreshActionBar();
        }

        private void CommitCommunityKeyboard()
        {
            string text = _communityKbBuffer.ToString();
            if (text.Length < _communityKbMin) return;
            var commit = _communityKbCommit;
            _communityKbCommit = null;
            _communityScreen = _communityKbReturn;
            try { commit?.Invoke(text); } catch (Exception ex) { Core.InstallLog.Write("Community keyboard commit failed: " + ex.Message); }
            RenderGameMenuOverlay();
            RefreshActionBar();
        }
    }
}
