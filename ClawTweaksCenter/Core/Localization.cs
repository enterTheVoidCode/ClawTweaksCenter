using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClawTweaksCenter.Core
{
    /// <summary>The five languages Center ships, plus "follow the OS".</summary>
    public enum UiLanguage
    {
        /// <summary>Whatever Windows is set to, if we have it. The default, and what a fresh
        /// installation runs on - see <see cref="Loc.Detect"/>.</summary>
        System,
        English,
        German,
        French,
        Korean,
        Spanish,
    }

    /// <summary>
    /// Center's translations.
    ///
    /// KEYED BY THE ENGLISH STRING, not by a symbolic id, and that is the whole design. A missing
    /// entry returns its own key, so an untranslated string renders in English instead of showing
    /// "Home_Tile_3_Title" or throwing. That makes partial coverage the NORMAL state rather than a
    /// defect: the tables below hold the strings somebody has actually checked, and everything else
    /// is correct English by construction. It is also why adding a new label to the interface needs
    /// no work here at all.
    ///
    /// The cost is the usual one for this shape: two identical English strings that need different
    /// translations cannot be told apart. Nothing in Center hits that today; the day it does, that
    /// one string gets a symbolic key and everything else stays as it is.
    ///
    /// WHERE IT IS APPLIED: at the few builders every screen goes through - the footer chips, the
    /// Home tiles, the settings rows, the status rows - not at the hundreds of call sites. One
    /// lookup at render time covers the interface, and a string that is not in a table simply
    /// passes through.
    ///
    /// DELIBERATELY CONSERVATIVE. Menu headings stay English, and a translation is only kept when it
    /// is close to the English in RENDERED WIDTH (see the check in Verify below) - Center's chips,
    /// tabs and tiles are laid out for the English word, and a label half again as long does not get
    /// a wider tile, it gets clipped. Where the honest translation is too long, the English stays.
    /// </summary>
    public static partial class Loc
    {
        /// <summary>The language in effect. Never <see cref="UiLanguage.System"/> - that is a stored
        /// preference, not a state, and it is resolved once on startup.</summary>
        public static UiLanguage Current { get; private set; } = UiLanguage.English;

        /// <summary>What the user picked, including "System". This is what the settings row shows.</summary>
        public static UiLanguage Preference { get; private set; } = UiLanguage.System;

        private static Dictionary<string, string> table;

        /// <summary>
        /// Resolves the stored preference into a live language. Call once, before the first window.
        /// </summary>
        public static void Initialise()
        {
            Preference = CenterSettings.Language;
            Current = Preference == UiLanguage.System ? Detect() : Preference;
            table = TableFor(Current);
        }

        /// <summary>Stores the preference and switches immediately.</summary>
        public static void Set(UiLanguage preference)
        {
            if (preference == Preference) return;

            Preference = preference;
            CenterSettings.Language = preference;
            Current = preference == UiLanguage.System ? Detect() : preference;
            table = TableFor(Current);
        }

        // NO "language changed" EVENT, deliberately. Center builds every screen in code and rebuilds
        // it on navigation, so the one caller of Set() redraws the window itself and everything else
        // is already redrawn by the time it is seen. An event here would be a second mechanism for
        // something that has exactly one subscriber.

        /// <summary>
        /// What Windows is set to, mapped onto what we ship. Anything else is English.
        ///
        /// CurrentUICulture, not CurrentCulture: the second one is the FORMATTING culture (dates,
        /// decimal separators) and follows the region, not the display language. A German keyboard
        /// layout with an English Windows is a common setup on this hardware, and it must stay
        /// English.
        ///
        /// Matched on the two-letter code, so de-AT and de-CH arrive at German rather than falling
        /// through to English on a technicality.
        /// </summary>
        public static UiLanguage Detect()
        {
            try
            {
                switch (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                {
                    case "de": return UiLanguage.German;
                    case "fr": return UiLanguage.French;
                    case "ko": return UiLanguage.Korean;
                    case "es": return UiLanguage.Spanish;
                }
            }
            catch { }
            return UiLanguage.English;
        }

        /// <summary>
        /// The translation, or the English back unchanged.
        ///
        /// Null and empty pass straight through: this sits inside builders that are handed optional
        /// text, and a null check at every one of them would be the same check written many times.
        /// </summary>
        public static string T(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            if (table == null) return english;
            return table.TryGetValue(english, out string s) ? s : english;
        }

        /// <summary>The language's name IN THAT LANGUAGE. Somebody who has landed in a language they
        /// cannot read has to be able to find their way out, and "German" does not help them -
        /// "Deutsch" does.</summary>
        public static string NameOf(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return "Deutsch";
                case UiLanguage.French: return "Français";
                case UiLanguage.Korean: return "한국어";
                case UiLanguage.Spanish: return "Español";
                case UiLanguage.English: return "English";
                default: return T("System language");
            }
        }

        /// <summary>The order the settings row cycles through. System first: it is the default, and
        /// it is the entry somebody looking for "put it back" wants.</summary>
        public static readonly UiLanguage[] Order =
        {
            UiLanguage.System, UiLanguage.English, UiLanguage.German,
            UiLanguage.French, UiLanguage.Korean, UiLanguage.Spanish,
        };

        public static UiLanguage Next(UiLanguage current)
        {
            int i = Array.IndexOf(Order, current);
            return Order[(i < 0 ? 0 : (i + 1) % Order.Length)];
        }

        private static Dictionary<string, string> TableFor(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return German;
                case UiLanguage.French: return French;
                case UiLanguage.Korean: return Korean;
                case UiLanguage.Spanish: return Spanish;
                default: return null;
            }
        }
    }
}
