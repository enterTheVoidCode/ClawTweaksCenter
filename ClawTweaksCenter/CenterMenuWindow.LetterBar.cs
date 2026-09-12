using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Audio;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The letter bar: jump a long shelf to one initial.
    ///
    /// LT opens it in the two tabs that can run to hundreds of covers - All and Not Installed. It
    /// takes the top right corner of the tab strip, where the Steam friends chip normally sits: that
    /// chip is only worth the space in Recent and the store tabs, so the corner is free exactly where
    /// this is needed (user, 2026-09-12).
    ///
    /// Left/Right pick a letter, A applies it, B leaves. While it is open it OWNS the d-pad - the
    /// grid behind it does not move, because a picker whose cursor and whose list both move on the
    /// same press is a picker nobody can aim.
    ///
    /// The letters are the ones that EXIST in the tab, never a fixed A-Z. A row of 26 chips where
    /// nine do nothing is a row that has to be tried to be understood.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _letterBarOpen;
        private int _letterBarIndex;

        /// <summary>The initial the shelf is filtered to, or null for everything. One character, and
        /// <see cref="OtherKey"/> stands for every title that starts with neither a letter nor a
        /// digit.</summary>
        private string _letterFilter;

        private readonly List<Border> _letterBarChips = new List<Border>();
        private List<string> _letterBarKeys = new List<string>();

        /// <summary>Titles that start with punctuation, a bracket, a Japanese character - anything
        /// that is not A-Z or 0-9. One bucket rather than one chip each: they are a handful, and they
        /// have no order anybody would look for them in.</summary>
        private const string OtherKey = "#";

        /// <summary>Clearing the filter is the FIRST chip, not a B press. B closes the bar, and
        /// closing a picker is not the same gesture as undoing the choice it made.</summary>
        private const string AllKey = "*";

        /// <summary>Only where a shelf gets long enough to be worth jumping around in, and only where
        /// the corner is free: the friends chip owns it in Recent and the store tabs.</summary>
        private bool LibraryGroupTakesLetterBar =>
            _libraryGroup == LibraryGroup.All || _libraryGroup == LibraryGroup.NotInstalled;

        private bool LetterBarAvailable =>
            _libraryScanned && LibraryGroupTakesLetterBar && !LibraryOverlayOwnsScreen;

        /// <summary>
        /// The initials present in a list, in reading order: digits, then letters, then the rest.
        ///
        /// Built from the UNFILTERED tab. Deriving them from what is on screen would leave a filtered
        /// shelf offering exactly one letter - its own.
        /// </summary>
        private static List<string> LetterKeysOf(IReadOnlyList<GameEntry> games)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            bool other = false;
            foreach (var g in games)
            {
                string key = LetterKeyOf(g?.Title);
                if (key == OtherKey) other = true;
                else if (key != null) keys.Add(key);
            }

            var result = keys.ToList();
            if (other) result.Add(OtherKey);
            return result;
        }

        /// <summary>The bucket a title belongs to, or null for a title that is not there at all.</summary>
        private static string LetterKeyOf(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            char c = title.TrimStart()[0];
            if (char.IsDigit(c)) return c.ToString(CultureInfo.InvariantCulture);
            // ToUpperInvariant, so "Ärger" and "ärger" land on the same chip. The accented letter
            // keeps its own chip rather than being folded onto A: it is what the title starts with,
            // and a user looking for it looks under what they see.
            if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString(CultureInfo.InvariantCulture);
            return OtherKey;
        }

        /// <summary>The tab's games, narrowed to the chosen initial. No filter means no work.</summary>
        private IReadOnlyList<GameEntry> ApplyLetterFilter(IReadOnlyList<GameEntry> games)
        {
            if (string.IsNullOrEmpty(_letterFilter) || _letterFilter == AllKey) return games;
            return games.Where(g => LetterKeyOf(g?.Title) == _letterFilter).ToList();
        }

        private void OpenLetterBar()
        {
            if (!LetterBarAvailable) return;

            _letterBarKeys = LetterKeysOf(_library.ForGroup(_libraryGroup, _romSystem));
            if (_letterBarKeys.Count == 0) return;
            _letterBarKeys.Insert(0, AllKey);

            // Opens ON the letter that is in force, so the bar says where the shelf stands rather
            // than starting from the left every time.
            int at = _letterFilter == null ? 0 : _letterBarKeys.IndexOf(_letterFilter);
            _letterBarIndex = at < 0 ? 0 : at;

            _letterBarOpen = true;
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>Closes the bar. <paramref name="clearFilter"/> for a tab change - a letter is a
        /// property of the shelf it was picked on, and carrying it to the next tab would look like a
        /// tab that lost most of its games.</summary>
        private void CloseLetterBar(bool clearFilter)
        {
            if (clearFilter) _letterFilter = null;
            if (!_letterBarOpen) return;

            _letterBarOpen = false;
            _letterBarChips.Clear();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void MoveLetterBar(PadButton dir)
        {
            if (_letterBarKeys.Count == 0) return;

            int next = _letterBarIndex;
            switch (dir)
            {
                // Wrapping, because the row is short and its two ends are next to each other on
                // screen - unlike a grid, where running off the edge means something.
                case PadButton.Left: next = (_letterBarIndex - 1 + _letterBarKeys.Count) % _letterBarKeys.Count; break;
                case PadButton.Right: next = (_letterBarIndex + 1) % _letterBarKeys.Count; break;
                // Up and down leave. The bar is one row, and a press that cannot go anywhere is
                // better spent on the way out than swallowed.
                case PadButton.Up:
                case PadButton.Down: CloseLetterBar(clearFilter: false); return;
                default: return;
            }

            if (next == _letterBarIndex) return;
            _letterBarIndex = next;
            ApplyLetterBarSelection();
        }

        private void ApplyLetterChoice()
        {
            if (_letterBarKeys.Count == 0) { CloseLetterBar(clearFilter: false); return; }

            string chosen = _letterBarKeys[Math.Clamp(_letterBarIndex, 0, _letterBarKeys.Count - 1)];
            _letterFilter = chosen == AllKey ? null : chosen;
            _letterBarOpen = false;
            _letterBarChips.Clear();

            // Back to the first tile: the cursor was standing on a game that the new filter may not
            // show any more, and a selection pointing past the end of the list is the one state the
            // grid cannot draw.
            _libSelectedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void ApplyLetterBarSelection()
        {
            for (int i = 0; i < _letterBarChips.Count; i++)
            {
                bool on = i == _letterBarIndex;
                _letterBarChips[i].Background = on ? UiHelpers.Accent : Brushes.Transparent;
                _letterBarChips[i].BorderBrush = on ? UiHelpers.Accent : UiHelpers.Subtle;
            }
        }

        /// <summary>
        /// The bar itself, or the one chip that says a filter is in force.
        ///
        /// The chip matters as much as the bar: a shelf showing 40 of 800 games with nothing on
        /// screen to explain it reads as a library that lost something.
        /// </summary>
        private UIElement BuildLetterCorner()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // The key cap in both states, the way the friends chip names RT. It is the only thing
            // that says where the bar came from and how to get it back - there is no footer chip for
            // LT, by the same rule that keeps the ROM triggers out of the footer.
            if (!_letterBarOpen)
            {
                if (_letterFilter == null) return null;
                row.Children.Add(BuildKeyCap("LT"));
                row.Children.Add(new Border { Width = 8 });
                row.Children.Add(BuildLetterChip(_letterFilter, selected: true));
                return row;
            }

            row.Children.Add(BuildKeyCap("LT"));
            row.Children.Add(new Border { Width = 8 });
            _letterBarChips.Clear();
            for (int i = 0; i < _letterBarKeys.Count; i++)
            {
                var chip = BuildLetterChip(_letterBarKeys[i], i == _letterBarIndex);
                int captured = i;
                chip.MouseLeftButtonUp += (_, __) => { _letterBarIndex = captured; ApplyLetterChoice(); };
                _letterBarChips.Add(chip);
                row.Children.Add(chip);
            }

            // A scroller, because the row is as long as the alphabet the shelf happens to use and the
            // corner is not. Horizontal only - it is one line by construction.
            return new ScrollViewer
            {
                Content = row,
                MaxWidth = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private Border BuildLetterChip(string key, bool selected)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    // The clear-the-filter chip is the only one that is a word rather than a letter,
                    // and it has to be: "*" beside A, B, C would read as one more initial.
                    Text = key == AllKey ? Core.Loc.T("All") : key,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = selected ? Brushes.Black : UiHelpers.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                MinWidth = key == AllKey ? 44 : 26,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderBrush = selected ? UiHelpers.Accent : UiHelpers.Subtle,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>The chips for the bar, added to the library's action bar while it is open.</summary>
        private void AddLetterBarActions()
        {
            AddAction(PadButton.A, "Choose", true, ApplyLetterChoice);
            AddAction(PadButton.B, "Close", true, () => CloseLetterBar(clearFilter: false));
        }
    }
}
