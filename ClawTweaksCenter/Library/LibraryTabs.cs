using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Which library tabs exist, in which order, and which of them the user has hidden.
    ///
    /// ONE AUTHORITY, and everything that walks the tabs goes through it: the tab strip
    /// (RefreshTabStrip), the shoulder cycle (CycleLibraryGroup) and the editor that changes it. The
    /// strip and the cycle used to walk <c>Enum.GetValues</c> separately, which was fine while the
    /// answer was "all of them, in declaration order" and stops being fine the moment it is not - a
    /// tab visible in the strip and skipped by the shoulders is the unreachable-control trap this
    /// project has already paid for twice.
    ///
    /// STORED AS ONE STRING, not as an order plus a hidden list. Two values over the same set can
    /// disagree, and then nothing on screen says which of them won. One line, names separated by
    /// commas, a leading '-' meaning hidden:
    ///
    ///     Recent,Favorites,-Xbox,All,Steam,...
    ///
    /// NAMES, NOT ORDINALS - the same reasoning as CenterSettings.Language. A tab inserted into the
    /// middle of <see cref="LibraryGroup"/> in a later version would silently turn somebody's hidden
    /// Xbox tab into a hidden ROMs tab if this stored numbers.
    ///
    /// A GROUP THE STORED LINE DOES NOT MENTION IS APPENDED, VISIBLE. That is what makes a tab added
    /// in a future version show up for people who have already arranged theirs, instead of being
    /// invisible to exactly the users who configured this once.
    /// </summary>
    public static class LibraryTabs
    {
        private const char HiddenMarker = '-';

        /// <summary>Every group, in the user's order. Hidden ones are included - this is the list the
        /// editor shows.</summary>
        public static List<LibraryGroup> Ordered()
        {
            var all = ((LibraryGroup[])Enum.GetValues(typeof(LibraryGroup))).ToList();
            var result = new List<LibraryGroup>();

            foreach (string token in Split(Core.CenterSettings.LibraryTabs))
            {
                string name = token.TrimStart(HiddenMarker);
                if (!Enum.TryParse(name, out LibraryGroup g)) continue;   // a tab we no longer ship
                if (!all.Contains(g) || result.Contains(g)) continue;     // duplicates: first wins
                result.Add(g);
            }

            // Declaration order for anything the stored line never mentioned, which on a fresh
            // installation is all of it.
            foreach (var g in all)
                if (!result.Contains(g)) result.Add(g);

            return result;
        }

        public static bool IsHidden(LibraryGroup group)
        {
            foreach (string token in Split(Core.CenterSettings.LibraryTabs))
            {
                if (token.Length == 0 || token[0] != HiddenMarker) continue;
                if (Enum.TryParse(token.Substring(1), out LibraryGroup g) && g == group) return true;
            }
            return false;
        }

        /// <summary>
        /// What the strip draws and what the shoulders walk.
        ///
        /// NEVER EMPTY. Hiding the last tab would leave a library with no way back to its own games,
        /// and the editor already refuses to do it - this is the second net, because the stored line
        /// is a registry value a user can edit by hand.
        /// </summary>
        public static List<LibraryGroup> Visible()
        {
            var visible = Ordered().Where(g => !IsHidden(g)).ToList();
            return visible.Count > 0 ? visible : Ordered();
        }

        /// <summary>Writes the order back. <paramref name="hidden"/> is the set to hide; a set holding
        /// every group is rejected for the reason in <see cref="Visible"/>.</summary>
        public static void Save(IEnumerable<LibraryGroup> order, ICollection<LibraryGroup> hidden)
        {
            var list = order?.ToList() ?? new List<LibraryGroup>();
            if (list.Count == 0) return;
            if (hidden != null && hidden.Count >= list.Count) return;

            var text = new StringBuilder();
            foreach (var g in list)
            {
                if (text.Length > 0) text.Append(',');
                if (hidden != null && hidden.Contains(g)) text.Append(HiddenMarker);
                text.Append(g.ToString());
            }

            Core.CenterSettings.LibraryTabs = text.ToString();
        }

        private static IEnumerable<string> Split(string raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? Enumerable.Empty<string>()
                : raw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
    }
}
