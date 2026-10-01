using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Every published community preset, reached from a Home tile (user, 2026-10-01) - the widget's
    /// browse list (XboxGamingBar/Features/GamePresets/GamingWidget.CommunityBrowse.cs) in Center.
    ///
    /// It is the Community overlay in a second mode, not a screen of its own: the rating form, the
    /// keyboard and the message screen are the ones the per-game list uses, and a second copy of them
    /// would drift. <see cref="_communityGlobal"/> is the switch - with it set there is no game,
    /// <see cref="CommunityScreen.List"/> draws this browse list, and B on the list goes back to Home.
    ///
    /// Same shape as the widget: a keypad of the letters that HAVE presets (an empty letter is hidden,
    /// not dimmed - a young database leaves most of them empty), then ten presets per page. Same
    /// device filter as the per-game list: this kind of Claw first, X for all devices.
    ///
    /// No "Use this preset" here: applying writes a game's profile by exe path, and from Home there is
    /// no game to write it to. The game's own list in the library does that.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private const int BrowsePageSize = 10;

        private bool _communityGlobal;
        private char _browseLetter;
        private int _browsePage;

        /// <summary>The Home tile. The overlay lives in the library's host, so the library is opened
        /// underneath first - the same way Home's Library Settings tile does it.</summary>
        private void OpenCommunityBrowseFromHome()
        {
            OpenLibrary();

            _communityGame = null;
            _communityGlobal = true;
            _communityFromLaunch = false;
            _communityOffsets.Clear();
            _lastCommunityScreen = null;
            _communityScreen = CommunityScreen.List;
            _communityIndex = 0;
            _communityNote = null;
            _communityAllDevices = false;
            _browseLetter = '\0';
            _browsePage = 0;
            _communityList = new List<CommunityPresets.Preset>();
            _communityListAll = new List<CommunityPresets.Preset>();

            _gameMenuOverlay = GameMenuOverlay.Community;
            RenderGameMenuOverlay();
            RefreshActionBar();

            EnsureCommunityIdentity();
            EnsureCommunityIndexThenRedraw(forceRefresh: true);
        }

        private void CloseCommunityBrowseToHome()
        {
            _communityGlobal = false;
            _communityList = new List<CommunityPresets.Preset>();
            _communityListAll = new List<CommunityPresets.Preset>();
            _gameMenuOverlay = GameMenuOverlay.None;
            _gameMenuRows.Clear();
            GoHome();
        }

        /// <summary>The presets the browse list shows: every one, or only this kind of Claw's.</summary>
        private List<CommunityPresets.Preset> BrowseFiltered(out int hidden)
        {
            var all = CommunityPresets.All();
            string mine = !string.IsNullOrEmpty(_communityDevice) ? _communityDevice : CommunityPresets.DeviceCode();
            var shown = _communityAllDevices ? all : all.Where(p => CommunityPresets.SameDeviceFamily(p, mine)).ToList();
            hidden = all.Count - shown.Count;
            return shown;
        }

        private void RenderCommunityBrowse()
        {
            ClearCommunityRows();
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var shown = BrowseFiltered(out int hidden);
            int n = shown.Count;
            string status = !CommunityPresets.Loaded ? Core.Loc.T("Loading shared presets…")
                : !_communityAllDevices
                    ? (n == 0 ? Core.Loc.T("No preset for your Claw yet.")
                       : n == 1 ? Core.Loc.T("1 preset for your Claw.")
                       : Core.Loc.F("{0} presets for your Claw.", n))
                    : (n == 0 ? Core.Loc.T("No one has shared a preset yet.")
                       : n == 1 ? Core.Loc.T("1 preset on all devices.")
                       : Core.Loc.F("{0} presets on all devices.", n));
            LibraryRoot.Children.Add(CommunityHead(Core.Loc.T("Community presets"), status));

            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 24), HorizontalAlignment = HorizontalAlignment.Center };

            var byLetter = new SortedDictionary<char, List<CommunityPresets.Preset>>();
            foreach (var p in shown)
            {
                char b = CommunityPresets.BrowseBucket(p.Get("gameTitle"));
                if (!byLetter.TryGetValue(b, out var l)) byLetter[b] = l = new List<CommunityPresets.Preset>();
                l.Add(p);
            }
            // '#' sorts before 'A' in char order, which is where the widget puts it too.
            var letters = byLetter.Keys.ToList();

            if (letters.Count > 0)
            {
                if (!letters.Contains(_browseLetter)) { _browseLetter = letters[0]; _browsePage = 0; }

                var entries = byLetter[_browseLetter];
                // Title first, newest first within a title - the newest measurement is the one most
                // likely to match a current build of the game.
                entries.Sort((x, y) =>
                {
                    int t = string.Compare(x.Get("gameTitle"), y.Get("gameTitle"), StringComparison.OrdinalIgnoreCase);
                    return t != 0 ? t : string.CompareOrdinal(y.Get("createdAt"), x.Get("createdAt"));
                });
                int pages = Math.Max(1, (entries.Count + BrowsePageSize - 1) / BrowsePageSize);
                _browsePage = Math.Max(0, Math.Min(_browsePage, pages - 1));

                panel.Children.Add(AddCommunityWrapChipRow(
                    letters.Select(c => c.ToString()).ToArray(),
                    () => letters.IndexOf(_browseLetter),
                    i => { _browseLetter = letters[i]; _browsePage = 0; ResetCommunityScroll(CommunityScreen.List); }));

                if (pages > 1)
                    panel.Children.Add(AddCommunityChipRow(Core.Loc.T("Page"),
                        Enumerable.Range(1, pages).Select(i => i.ToString()).ToArray(), null,
                        () => _browsePage, i => _browsePage = i));

                foreach (var p in entries.Skip(_browsePage * BrowsePageSize).Take(BrowsePageSize))
                    panel.Children.Add(BuildCommunityCard(p, browse: true));
            }

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

        /// <summary>
        /// The letter keypad: one full-width chip row whose chips wrap onto more lines. Left/Right walk
        /// the letters in order across the line breaks - a two-dimensional keypad would need Up/Down,
        /// and those move between rows here (user rule, 2026-10-01).
        /// </summary>
        private Border AddCommunityWrapChipRow(string[] labels, Func<int> get, Action<int> set)
        {
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            int current = get();
            for (int i = 0; i < labels.Length; i++)
            {
                var chip = CommunityChip(labels[i], i == current, true);
                chip.MinWidth = 34;
                chip.Margin = new Thickness(3, 3, 3, 3);
                ((TextBlock)chip.Child).HorizontalAlignment = HorizontalAlignment.Center;
                wrap.Children.Add(chip);
            }
            return RegisterCommunityRow(wrap, run: null, live: true, topMargin: 0, bottomMargin: 12,
                chip: new CommunityChipRow { Count = labels.Length, Get = get, Set = set }, actionLabel: null);
        }
    }
}
