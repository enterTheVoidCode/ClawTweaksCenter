using System;
using System.Collections.Generic;
using System.IO;
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
    /// The Misc tab: tools the user put there by hand.
    ///
    /// Everything else in the library answers "what is installed". This tab answers "what did you
    /// ask for" - a mod manager, a fan curve editor, an emulator front-end - and that is why nothing
    /// arrives here without being picked.
    ///
    /// THREE WAYS IN, and their order on the screen is the whole point. Picking from a list of
    /// already-known apps is first because it is the only one that works well with a thumbstick;
    /// the Windows file dialog is LAST because navigating a folder tree with a D-pad is miserable,
    /// and it is only there for the tool that is in none of the lists.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private enum MiscOverlay
        {
            None,
            /// <summary>Choose how to add: pick from installed apps, or browse.</summary>
            Sources,
            /// <summary>The multi-select list of everything found.</summary>
            Apps,
            // Editing an entry used to be a third screen here, reached with Y from the Misc tab. It
            // is gone: renaming and removing now live in the Start-button menu on the game itself,
            // next to favoriting and cover art, which is where a user looks for "do something with
            // THIS entry". Having both meant Remove existed twice, in two different places, on two
            // different buttons.
        }

        private MiscOverlay _miscOverlay = MiscOverlay.None;
        private int _miscMenuIndex;
        private readonly List<FrameworkElement> _miscRows = new List<FrameworkElement>();
        private List<AppCandidate> _miscCandidates = new List<AppCandidate>();
        private readonly HashSet<string> _miscChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _miscLoadingApps;
        private ScrollViewer _miscScroller;
        private ScrollViewer _miscLetterScroller;
        private StackPanel _miscLetterPanel;
        private FrameworkElement _activeMiscLetterChip;
        private CancellationTokenSource _miscScanCts;

        private bool MiscOverlayOpen => _miscOverlay != MiscOverlay.None;

        #region Entry and exit
        private void OpenMiscAdd()
        {
            if (LaunchOverlayOpen || _settingsOpen) return;
            _miscOverlay = MiscOverlay.Sources;
            _miscMenuIndex = 0;
            RenderMiscOverlay();
            RefreshActionBar();
        }

        private void CloseMiscOverlay()
        {
            _miscScanCts?.Cancel();
            _miscScanCts = null;
            _miscOverlay = MiscOverlay.None;
            _miscScroller = null;
            _miscLetterPanel = null;
            _miscLetterScroller = null;
            _miscRows.Clear();
            _miscChecked.Clear();
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>B: one step back rather than all the way out. Coming out of the app list should
        /// land on the menu it was opened from, not on the grid - otherwise picking the wrong way in
        /// costs the whole journey.</summary>
        private void MiscOverlayBack()
        {
            switch (_miscOverlay)
            {
                case MiscOverlay.Apps:
                    _miscOverlay = MiscOverlay.Sources;
                    _miscMenuIndex = 0;
                    RenderMiscOverlay();
                    RefreshActionBar();
                    return;
                default:
                    CloseMiscOverlay();
                    return;
            }
        }
        #endregion

        #region Rendering
        private void RenderMiscOverlay()
        {
            if (LibraryRoot == null) return;

            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _miscRows.Clear();

            switch (_miscOverlay)
            {
                case MiscOverlay.Sources: RenderMiscSources(); break;
                case MiscOverlay.Apps: RenderMiscApps(); break;
            }
        }

        private void RenderMiscSources()
        {
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
            };
            stack.Children.Add(MiscHeadline("Add an app"));

            stack.Children.Add(MiscRow(0, "Choose from installed apps",
                "Start menu, desktop and startup"));
            // LAST, deliberately. A folder tree is the worst thing on this screen to drive with a
            // stick, so it is the fallback rather than the offer.
            stack.Children.Add(MiscRow(1, "Browse for a file", "Windows file picker"));

            LibraryRoot.Children.Add(stack);
            ApplyMiscSelection();
        }

        private void RenderMiscApps()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 8) };
            head.Children.Add(MiscHeadline(_miscLoadingApps ? "Reading installed apps…" : "Choose what to add"));
            if (!_miscLoadingApps)
                head.Children.Add(new TextBlock
                {
                    Text = _miscChecked.Count == 1 ? "1 selected" : _miscChecked.Count + " selected",
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            if (!_miscLoadingApps && _miscCandidates.Count > 0) head.Children.Add(BuildMiscLetterStrip());

            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            if (_miscLoadingApps)
            {
                var busy = BuildLibraryMessage("This takes a moment.", true);
                Grid.SetRow(busy, 1);
                LibraryRoot.Children.Add(busy);
                return;
            }

            if (_miscCandidates.Count == 0)
            {
                var empty = BuildLibraryMessage("Nothing found to add.", false);
                Grid.SetRow(empty, 1);
                LibraryRoot.Children.Add(empty);
                return;
            }

            var list = new StackPanel { Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12) };
            for (int i = 0; i < _miscCandidates.Count; i++) list.Children.Add(BuildCandidateRow(i));

            // A plain panel, not a virtualising list. Measured on this machine the whole inventory is
            // 239 rows; virtualising would buy nothing and would break the one thing this screen has
            // to do well, which is scrolling the CURRENT row into view on every D-pad press.
            _miscScroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            Grid.SetRow(_miscScroller, 1);
            LibraryRoot.Children.Add(_miscScroller);
            ApplyMiscSelection();
        }

        #region Letter strip
        /// <summary>
        /// The initial of one candidate, as the letter strip groups them. Anything that is not a
        /// letter (7-Zip, 3_HC) collapses into one "#" bucket rather than getting a chip each - ten
        /// digit chips for three entries would push the letters that matter off the strip.
        /// </summary>
        private static string LetterOf(AppCandidate candidate)
        {
            string name = candidate?.Name;
            if (string.IsNullOrEmpty(name)) return "#";
            char c = char.ToUpperInvariant(name[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
        }

        /// <summary>Only the letters that actually have entries, in display order. A strip of 27
        /// chips where half do nothing is worse than no strip - every stop the triggers make should
        /// land on something.</summary>
        private List<string> MiscLetters()
        {
            var seen = new List<string>();
            foreach (var candidate in _miscCandidates)
            {
                string letter = LetterOf(candidate);
                if (!seen.Contains(letter)) seen.Add(letter);
            }
            seen.Sort(StringComparer.Ordinal);   // "#" sorts before "A", which is where it belongs
            return seen;
        }

        private FrameworkElement BuildMiscLetterStrip()
        {
            _miscLetterPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Same construction as the ROM system strip: scrolls sideways, fades at whichever end
            // still has content, triggers pinned to the two ends so the "there is more that way" hint
            // cannot scroll away with the letters.
            _miscLetterScroller = BuildEdgeFadedStrip(_miscLetterPanel);

            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            FillDock(dock, BuildKeyCap("LT"), BuildKeyCap("RT"), _miscLetterScroller);
            RefreshMiscLetterChips();
            return dock;
        }

        /// <summary>
        /// Repaints just the chips, leaving the list underneath alone.
        ///
        /// Separate from building the strip because the highlight has to follow ORDINARY up/down
        /// movement too, not only trigger jumps - and re-rendering the whole screen for that would
        /// throw the list's scroll position away on every single D-pad press.
        /// </summary>
        private void RefreshMiscLetterChips()
        {
            if (_miscLetterPanel == null) return;

            string current = _miscMenuIndex >= 0 && _miscMenuIndex < _miscCandidates.Count
                ? LetterOf(_miscCandidates[_miscMenuIndex]) : null;

            _miscLetterPanel.Children.Clear();
            _activeMiscLetterChip = null;

            foreach (string letter in MiscLetters())
            {
                bool active = letter == current;
                var chip = new Border
                {
                    Child = new TextBlock
                    {
                        Text = letter,
                        FontSize = 13,
                        FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = active ? UiHelpers.Text : UiHelpers.Subtle,
                    },
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    CornerRadius = new CornerRadius(11),
                    Background = active ? UiHelpers.Card : Brushes.Transparent,
                    BorderBrush = active ? UiHelpers.Accent : Brushes.Transparent,
                    BorderThickness = new Thickness(active ? 1 : 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                string captured = letter;
                chip.MouseLeftButtonUp += (_, __) => JumpToMiscLetter(captured);
                if (active) _activeMiscLetterChip = chip;
                _miscLetterPanel.Children.Add(chip);
            }

            BringChipIntoView(_miscLetterScroller, _activeMiscLetterChip);
        }

        /// <summary>Moves the cursor to the first entry under <paramref name="letter"/>.</summary>
        private void JumpToMiscLetter(string letter)
        {
            for (int i = 0; i < _miscCandidates.Count; i++)
            {
                if (LetterOf(_miscCandidates[i]) != letter) continue;
                _miscMenuIndex = i;
                ApplyMiscSelection();
                RefreshMiscLetterChips();
                if (i < _miscRows.Count) try { _miscRows[i].BringIntoView(); } catch { }
                RefreshActionBar();
                return;
            }
        }

        /// <summary>LT/RT: step to the previous or next letter that has entries.</summary>
        private void CycleMiscLetter(int delta)
        {
            var letters = MiscLetters();
            if (letters.Count == 0) return;

            string current = _miscMenuIndex >= 0 && _miscMenuIndex < _miscCandidates.Count
                ? LetterOf(_miscCandidates[_miscMenuIndex]) : letters[0];

            int i = letters.IndexOf(current) + delta;
            if (i < 0) i = letters.Count - 1;
            if (i >= letters.Count) i = 0;
            JumpToMiscLetter(letters[i]);
        }
        #endregion

        private FrameworkElement BuildCandidateRow(int index)
        {
            var candidate = _miscCandidates[index];
            bool ticked = _miscChecked.Contains(candidate.DedupeKey);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // A filled box rather than a tick glyph: at arm's length on an 8" panel the state has to
            // survive being glanced at, and a solid block reads where a hairline check mark does not.
            var box = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                BorderBrush = ticked ? UiHelpers.Accent : UiHelpers.Subtle,
                Background = ticked ? UiHelpers.Accent : Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            grid.Children.Add(box);

            var name = new TextBlock
            {
                Text = candidate.Name,
                FontSize = 16,
                Foreground = UiHelpers.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var origin = new TextBlock
            {
                Text = candidate.Origin,
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(origin, 2);
            grid.Children.Add(origin);

            var row = new Border
            {
                Child = grid,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
            };
            int captured = index;
            row.MouseLeftButtonUp += (_, __) => { _miscMenuIndex = captured; ToggleCandidate(); };
            _miscRows.Add(row);
            return row;
        }


        private static TextBlock MiscHeadline(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiHelpers.Text,
            Margin = new Thickness(0, 0, 0, 16),
        };

        /// <summary>One menu row, same shape as the settings screen uses so the two read as the same
        /// kind of screen.</summary>
        private FrameworkElement MiscRow(int index, string title, string subtitle)
        {
            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(title),
                FontSize = 18,
                Foreground = UiHelpers.Text,
            });
            if (subtitle != null)
                left.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(subtitle),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(0, 2, 0, 0),
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
            row.MouseLeftButtonUp += (_, __) => { _miscMenuIndex = captured; ActivateMiscRow(); };
            _miscRows.Add(row);
            return row;
        }

        private void ApplyMiscSelection()
        {
            foreach (var row in _miscRows)
                if (row is Border b)
                    b.BorderBrush = b.Tag is int i && i == _miscMenuIndex ? UiHelpers.Accent : Brushes.Transparent;
        }
        #endregion

        #region Navigation
        private void MoveMiscSelection(PadButton dir)
        {
            if (_miscRows.Count == 0) return;

            int next = _miscMenuIndex + (dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0);
            if (next < 0 || next >= _miscRows.Count || next == _miscMenuIndex) return;

            _miscMenuIndex = next;
            ApplyMiscSelection();
            // The strip is a readout of where the cursor is, not just a jump control, so it moves
            // with ordinary navigation too - a stale highlight is worse than none.
            RefreshMiscLetterChips();

            // Only the app list scrolls; the two small menus fit on screen whole. BringIntoView works
            // here precisely because nothing is virtualised - every row is a realised element.
            if (_miscScroller != null)
                try { _miscRows[_miscMenuIndex].BringIntoView(); } catch { }

            RefreshActionBar();
        }

        private void ActivateMiscRow()
        {
            switch (_miscOverlay)
            {
                case MiscOverlay.Sources:
                    if (_miscMenuIndex == 0) OpenMiscAppList();
                    else BrowseForExe();
                    return;

                case MiscOverlay.Apps:
                    ToggleCandidate();
                    return;

            }
        }

        private void ToggleCandidate()
        {
            if (_miscOverlay != MiscOverlay.Apps) return;
            if (_miscMenuIndex < 0 || _miscMenuIndex >= _miscCandidates.Count) return;

            string key = _miscCandidates[_miscMenuIndex].DedupeKey;
            if (!_miscChecked.Remove(key)) _miscChecked.Add(key);

            // Redrawing the whole list would lose the scroll position and the cursor with it, so the
            // one box that changed is repainted in place.
            if (_miscMenuIndex < _miscRows.Count
                && _miscRows[_miscMenuIndex] is Border row
                && row.Child is Grid grid
                && grid.Children.Count > 0
                && grid.Children[0] is Border box)
            {
                bool ticked = _miscChecked.Contains(key);
                box.BorderBrush = ticked ? UiHelpers.Accent : UiHelpers.Subtle;
                box.Background = ticked ? UiHelpers.Accent : Brushes.Transparent;
            }
            RefreshActionBar();
        }
        #endregion

        #region Adding
        private void OpenMiscAppList()
        {
            _miscOverlay = MiscOverlay.Apps;
            _miscMenuIndex = 0;
            _miscChecked.Clear();

            if (_miscCandidates.Count > 0)
            {
                // Already read once this session. The inventory costs about three seconds and does
                // not change while Center is open.
                RenderMiscOverlay();
                RefreshActionBar();
                return;
            }

            _miscLoadingApps = true;
            RenderMiscOverlay();
            RefreshActionBar();

            _miscScanCts?.Cancel();
            _miscScanCts = new CancellationTokenSource();
            var ct = _miscScanCts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                IReadOnlyList<AppCandidate> found;
                try { found = await AppInventory.ScanAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("App inventory failed: " + ex.Message);
                    found = Array.Empty<AppCandidate>();
                }

                Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested || _miscOverlay != MiscOverlay.Apps) return;
                    _miscCandidates = new List<AppCandidate>(found);
                    RemoveAlreadyAdded();
                    _miscLoadingApps = false;
                    RenderMiscOverlay();
                    RefreshActionBar();
                });
            }, ct);
        }

        /// <summary>Drops everything already in the tab. Offering a tool the user added last week
        /// would let them add it twice, and two identical tiles is a bug report.</summary>
        private void RemoveAlreadyAdded()
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in MiscStore.Load())
            {
                if (!string.IsNullOrEmpty(e.Exe)) have.Add(e.Exe.ToLowerInvariant());
                else if (!string.IsNullOrEmpty(e.Aumid)) have.Add("aumid:" + e.Aumid.ToLowerInvariant());
            }
            if (have.Count == 0) return;
            _miscCandidates.RemoveAll(c => have.Contains(c.DedupeKey));
        }

        private void CommitCheckedApps()
        {
            if (_miscChecked.Count == 0) return;

            var entries = MiscStore.Load();
            foreach (var candidate in _miscCandidates)
            {
                if (!_miscChecked.Contains(candidate.DedupeKey)) continue;
                entries.Add(new MiscEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = candidate.Name,
                    Exe = candidate.Exe,
                    Args = candidate.Args,
                    Aumid = candidate.Aumid,
                });
            }

            PublishMisc(entries);
            CloseMiscOverlay();
        }

        /// <summary>
        /// The Windows file picker. Last on the menu and unavoidable for the tool that is in none of
        /// the lists - a portable exe in a folder somewhere has no Start menu entry and no shortcut.
        /// </summary>
        private void BrowseForExe()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a program",
                Filter = "Programs (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = true,
            };

            bool? ok;
            try { ok = dialog.ShowDialog(this); }
            catch (Exception ex) { Core.InstallLog.Write("File picker failed: " + ex.Message); return; }
            if (ok != true || dialog.FileNames == null || dialog.FileNames.Length == 0) return;

            var entries = MiscStore.Load();
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) if (!string.IsNullOrEmpty(e.Exe)) have.Add(e.Exe);

            foreach (string path in dialog.FileNames)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (!have.Add(path)) continue;
                entries.Add(new MiscEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    // The file name without its extension, as the first guess at a name. It is often
                    // wrong ("citra-qt"), which is exactly why renaming exists next to it.
                    Title = Path.GetFileNameWithoutExtension(path),
                    Exe = path,
                });
            }

            PublishMisc(entries);
            CloseMiscOverlay();
        }
        #endregion

        #region Editing

        /// <summary>Writes the list, republishes the library from it, and goes looking for covers.
        /// The order matters: the file is the source MiscSource reads, so it has to be current before
        /// anything asks the library what is in the tab.</summary>
        private void PublishMisc(List<MiscEntry> entries)
        {
            MiscStore.Save(entries);
            _library.ReplaceMisc(MiscSource.Build());
            StartArtFetch();

            // AND THEN THE SAME RESCAN THE Y BUTTON RUNS. Reported 2026-09-04: after adopting exe
            // files the covers went missing and only came back on a manual refresh.
            //
            // ReplaceMisc is a shortcut around the scan, and every step the scan takes that it does
            // not is a way for the two to disagree - two were found missing the same day (see its own
            // comment). Rather than keep a second copy of the scan's work in step by hand, the
            // shortcut now paints the new entries immediately and a real scan follows to make the
            // result IDENTICAL to the manual refresh by construction, not by resemblance.
            //
            // ⚠️ The order matters and is already right: ReplaceMisc has set _miscOverride by the time
            // the scan lands, so the round in flight cannot resurrect the pre-edit snapshot.
            //
            // Affordable because of WHEN it happens: adopting, renaming or removing an app is an
            // explicit, rare action the user has just confirmed - not something on a timer.
            _libraryScanned = false;
            _ = ScanLibraryAsync();
        }
        #endregion

        #region Footer
        /// <summary>The action bar while an overlay owns the screen. Returns false when none does, so
        /// the library's own footer takes over.</summary>
        private bool RefreshMiscActionBar()
        {
            switch (_miscOverlay)
            {
                case MiscOverlay.Sources:
                    AddAction(PadButton.A, "Choose", true, ActivateMiscRow);
                    AddAction(PadButton.B, "Back", true, MiscOverlayBack);
                    return true;

                case MiscOverlay.Apps:
                    AddAction(PadButton.A, "Select", !_miscLoadingApps && _miscCandidates.Count > 0, ToggleCandidate);
                    AddAction(PadButton.X,
                        _miscChecked.Count == 0 ? "Add" : "Add " + _miscChecked.Count,
                        _miscChecked.Count > 0, CommitCheckedApps);
                    AddAction(PadButton.B, "Back", true, MiscOverlayBack);
                    // The triggers jump by initial, exactly as they step ROM systems one tab over -
                    // and like there, bound WITHOUT a footer chip because the strip they belong to
                    // already carries their keycaps at its two ends.
                    if (!_miscLoadingApps && _miscCandidates.Count > 0)
                    {
                        _liveActions[PadButton.LT] = () => CycleMiscLetter(-1);
                        _liveActions[PadButton.RT] = () => CycleMiscLetter(1);
                    }
                    return true;

                default:
                    return false;
            }
        }
        #endregion
    }
}
