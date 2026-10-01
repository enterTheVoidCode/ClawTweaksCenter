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
        /// The banner that sits ABOVE the cover. Always present on the confirm screen: with presets it
        /// shows the count and slides through the best few (name, fps@W, author, stars); with none it
        /// invites the first share. Built fresh on every launch render; the slideshow timer is (re)armed
        /// here and stopped when the launch screen goes away (ClearLaunchOverlay) or the overlay opens.
        /// </summary>
        private UIElement BuildCommunityBanner(GameEntry game)
        {
            _bannerFor = game;
            _bannerPresets = CommunityPresets.Loaded && game != null
                ? CommunityPresets.ForGame(game)
                : new List<CommunityPresets.Preset>();
            _communitySlide = 0;

            var stack = new StackPanel { Margin = new Thickness(18, 10, 18, 10) };

            var headRow = new Grid();
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "  " + Core.Loc.T("Community presets"),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headRow.Children.Add(title);

            string count = !CommunityPresets.Loaded ? Core.Loc.T("Loading…")
                : _bannerPresets.Count == 0 ? Core.Loc.T("none yet — share the first")
                : _bannerPresets.Count == 1 ? Core.Loc.T("1 preset")
                : Core.Loc.F("{0} presets", _bannerPresets.Count);
            var countBlock = new TextBlock
            {
                Text = count + "   " + Core.Loc.T("[Up] to browse"),
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(countBlock, 1);
            headRow.Children.Add(countBlock);
            stack.Children.Add(headRow);

            _bannerSlideMain = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Accent,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _bannerSlideSub = new TextBlock
            {
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            stack.Children.Add(_bannerSlideMain);
            stack.Children.Add(_bannerSlideSub);
            RenderCommunitySlide();

            _launchCommunityBanner = new Border
            {
                Child = stack,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 18),
                MinWidth = 520,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            _launchCommunityBanner.MouseLeftButtonUp += (_, __) =>
            {
                _launchFocus = LaunchFocusCommunity;
                ActivateLaunchSelection();
            };

            StartCommunitySlideshow();
            EnsureCommunityIndexForBanner(game);
            return _launchCommunityBanner;
        }

        private void RenderCommunitySlide()
        {
            if (_bannerSlideMain == null || _bannerSlideSub == null) return;

            if (_bannerPresets.Count == 0)
            {
                _bannerSlideMain.Text = Core.Loc.T("Be the first to share a preset for this game.");
                _bannerSlideSub.Text = CommunityPresets.Loaded
                    ? Core.Loc.T("Press [Up] then A to open the community.")
                    : "";
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

        private void OpenCommunityRate(CommunityPresets.Preset p)
        {
            if (p == null) return;
            _rateTarget = p;
            _rateStars = 0;
            for (int i = 0; i < _rateCats.Length; i++) _rateCats[i] = 0;
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

            // Stars: a chip row 1..5.
            panel.Children.Add(AddCommunityChipRow(
                Core.Loc.T("Stars"),
                new[] { "1", "2", "3", "4", "5" },
                null,
                () => _rateStars - 1 < 0 ? 0 : _rateStars - 1,
                v => _rateStars = v + 1));

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

            panel.Children.Add(AddCommunityActionRow(
                "",
                Core.Loc.T("Submit rating"),
                _rateStars < 1 ? Core.Loc.T("Pick one to five stars first.") : null,
                Core.Loc.T("Submit"),
                SubmitCommunityRate,
                live: true,
                topMargin: 6));

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
                    Core.Loc.T("It appears on the preset once the community bot has read the forum, which can take a while."));
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

        private void OpenCommunityCreate()
        {
            var game = _communityGame;
            if (game == null) return;

            _cwFacts = ClawProfileDetails.CommunityFactsFor(game);
            _cwTdp = _cwFacts.TdpW;
            _cwFps = 0;
            _cwPowerState = Core.PowerLine.OnMains() ? 1 : 0;
            _cwResolution = Math.Max(0, Array.IndexOf(CwResolutions, _cwFacts.Resolution));
            _cwGraphics = 2; _cwUpscaler = 0; _cwUpscalerPreset = 2; _cwUpscalerSource = 0;
            _cwFrameGen = 0; _cwFrameGenFactor = 0; _cwDetail = false;
            for (int i = 0; i < _cwDetailValues.Length; i++) _cwDetailValues[i] = 2;

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

            // The two headline numbers.
            panel.Children.Add(AddCommunityActionRow("", Core.Loc.T("Native FPS"),
                _cwFps > 0 ? _cwFps.ToString(CultureInfo.InvariantCulture) : Core.Loc.T("not set — the frame rate without frame generation"),
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("Native FPS"), _cwFps > 0 ? _cwFps.ToString(CultureInfo.InvariantCulture) : "",
                    1, 4, digitsOnly: true, onCommit: t =>
                    {
                        _cwFps = int.TryParse(t, out int v) ? Math.Max(1, Math.Min(1000, v)) : 0;
                    }),
                live: true));

            panel.Children.Add(AddCommunityActionRow("", Core.Loc.T("TDP (W)"),
                string.IsNullOrEmpty(_cwTdp) ? Core.Loc.T("not set — taken from this game's profile, or type it") : _cwTdp + " W",
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("TDP in watts"), _cwTdp, 1, 2, digitsOnly: true, onCommit: t => _cwTdp = t),
                live: true));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Power"),
                new[] { Core.Loc.T("on battery"), Core.Loc.T("plugged in") }, null,
                () => _cwPowerState, v => _cwPowerState = v));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Resolution"),
                CwResolutions, null, () => _cwResolution, v => _cwResolution = v));

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Graphics preset"),
                CwGraphics, null, () => _cwGraphics, v => { _cwGraphics = v; if (CwGraphics[v] == "custom") _cwDetail = true; }));

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

            panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Share detailed graphics"),
                new[] { Core.Loc.T("No"), Core.Loc.T("Yes") }, null, () => _cwDetail ? 1 : 0, v => _cwDetail = v == 1));

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

            panel.Children.Add(AddCommunityActionRow("", Core.Loc.T("Nickname"),
                string.IsNullOrEmpty(_communityNickname) ? Core.Loc.T("required — 5 to 13 characters") : _communityNickname,
                Core.Loc.T("Type"),
                () => OpenCommunityKeyboard(Core.Loc.T("Nickname (5–13 characters)"), _communityNickname, 5, 13,
                    digitsOnly: false, onCommit: t => _communityNickname = t),
                live: true));

            panel.Children.Add(AddCommunityActionRow("", Core.Loc.T("Share preset"),
                Core.Loc.T("Posts to the community forum."), Core.Loc.T("Share"),
                SubmitCommunityCreate, live: true, topMargin: 6));

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
            if (!int.TryParse(_cwTdp, out int tdp) || tdp < 5 || tdp > 60)
            { SetCommunityNote(Core.Loc.T("Set a TDP between 5 and 60 W.")); return; }
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
            Put("fpsLimit", _cwFacts.FpsLimit);

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
                    Core.Loc.T("Your preset is on the forum now. It joins this list once the community bot has read it, which can take a while."));
                EnsureCommunityIndexThenRedraw(forceRefresh: true);
            }
            else
            {
                _communityNote = error;
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

        // ════════════════════════════════════ the "posted" message ══════════════════════════════

        private string _communityMessageHead;
        private string _communityMessageBody;

        private void ShowCommunityMessage(string head, string body)
        {
            _communityMessageHead = head;
            _communityMessageBody = body;
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
                MaxWidth = 560,
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
            "✓",           // ✓ commit
        };

        private readonly StringBuilder _communityKbBuffer = new StringBuilder();
        private int _communityKbMin, _communityKbMax;
        private bool _communityKbDigitsOnly, _communityKbShift;
        private Action<string> _communityKbCommit;
        private string _communityKbTitle;
        private CommunityScreen _communityKbReturn;
        private int _communityKbRow, _communityKbCol;
        private TextBlock _communityKbBufferBlock, _communityKbHintBlock;
        private readonly List<List<Border>> _communityKbKeys = new List<List<Border>>();

        private void OpenCommunityKeyboard(string title, string initial, int min, int max,
                                           bool digitsOnly, Action<string> onCommit)
        {
            _communityKbBuffer.Clear();
            if (!string.IsNullOrEmpty(initial)) _communityKbBuffer.Append(initial);
            _communityKbMin = Math.Max(0, min);
            _communityKbMax = Math.Max(1, max);
            _communityKbDigitsOnly = digitsOnly;
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

            for (int r = 0; r < CommunityKbRows.Length; r++)
            {
                var rowKeys = new List<Border>();
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                string row = CommunityKbRows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    char ch = row[c];
                    bool commit = ch == '✓';
                    bool enabled = commit ? _communityKbBuffer.Length >= _communityKbMin
                                          : (!_communityKbDigitsOnly || char.IsDigit(ch));
                    string face = commit ? "✓ " + Core.Loc.T("Done")
                                 : (_communityKbShift && char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch).ToString();

                    var key = new Border
                    {
                        Child = new TextBlock { Text = face, FontSize = 18, Foreground = UiHelpers.Text,
                                                HorizontalAlignment = HorizontalAlignment.Center },
                        Background = UiHelpers.Card,
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(2),
                        BorderBrush = Brushes.Transparent,
                        Padding = new Thickness(commit ? 18 : 12, 10, commit ? 18 : 12, 10),
                        Margin = new Thickness(4),
                        MinWidth = commit ? 0 : 40,
                        Opacity = enabled ? 1.0 : 0.4,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = r + "," + c,
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
            int r = _communityKbRow, c = _communityKbCol;
            switch (dir)
            {
                case PadButton.Left:  c--; break;
                case PadButton.Right: c++; break;
                case PadButton.Up:    r--; break;
                case PadButton.Down:  r++; break;
                default: return;
            }
            if (r < 0 || r >= CommunityKbRows.Length) return;
            // Clamp the column into the new row — the rows are not all the same length.
            if (c < 0) c = 0;
            if (c >= CommunityKbRows[r].Length) c = CommunityKbRows[r].Length - 1;
            _communityKbRow = r;
            _communityKbCol = c;
            RefreshCommunityKbVisuals();
        }

        private void PressCommunityKey()
        {
            if (_communityKbRow < 0 || _communityKbRow >= CommunityKbRows.Length) return;
            string row = CommunityKbRows[_communityKbRow];
            if (_communityKbCol < 0 || _communityKbCol >= row.Length) return;
            char ch = row[_communityKbCol];

            if (ch == '✓') { CommitCommunityKeyboard(); return; }
            if (_communityKbDigitsOnly && !char.IsDigit(ch)) return;
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
            if (_communityKbRow >= 0 && _communityKbRow < CommunityKbRows.Length)
            {
                string row = CommunityKbRows[_communityKbRow];
                if (_communityKbCol >= 0 && _communityKbCol < row.Length && row[_communityKbCol] == '✓')
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
