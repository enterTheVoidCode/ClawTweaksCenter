using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Center's own settings - the ones that are about the application rather than about the games
    /// library, which has its own screen (CenterMenuWindow.Library.cs).
    ///
    /// WHY A SEPARATE SCREEN rather than two more rows on the library's. The library settings live
    /// inside the library: they are reached from it, they draw into its host, and their Back returns
    /// to the grid. Language is not a library setting - it is the language of the installer screens
    /// too, and on a machine where ClawTweaks is not installed yet the library does not exist at all,
    /// so a language buried inside it would be unreachable exactly when somebody is trying to read
    /// the install instructions.
    ///
    /// It therefore draws into ContentHost like Home, Browse and Onboarding, and is available with
    /// or without ClawTweaks installed.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private readonly List<Border> _centerSettingsRows = new List<Border>();
        private int _centerSettingsIndex;

        /// <summary>The language list is unfolded. While it is, the D-pad and A belong to IT and not
        /// to the settings rows behind it - see MoveCenterSettingsSelection.</summary>
        private bool _languageListOpen;
        private int _languageIndex;
        private readonly List<Border> _languageRows = new List<Border>();

        private const int CenterSettingsLanguageRow = 0;
        private const int CenterSettingsFullscreenRow = 1;
        private const int CenterSettingsWaitControllerRow = 2;

        private void OpenCenterSettings()
        {
            LeaveLibrary();
            _view = View.CenterSettings;
            _centerSettingsIndex = 0;
            _languageListOpen = false;
            RenderCenterSettings();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void RenderCenterSettings()
        {
            BeginContent(centred: false);
            _centerSettingsRows.Clear();

            var stack = new StackPanel { MaxWidth = 940 };
            stack.Children.Add(new TextBlock
            {
                Text = Loc.T("Center settings"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 16, 16),
            });

            // Two columns, the same shape as the library's settings - two screens that do the same
            // job and look different are two screens to learn.
            var pairs = new UniformGrid { Columns = 2 };
            pairs.Children.Add(BuildCenterSettingRow(CenterSettingsLanguageRow, Loc.T("Language"),
                Loc.NameOf(Loc.Preference)));
            pairs.Children.Add(BuildCenterSettingRow(CenterSettingsFullscreenRow, Loc.T("Fullscreen"),
                null, WindowMode.IsFullscreen(this)));
            pairs.Children.Add(BuildCenterSettingRow(CenterSettingsWaitControllerRow,
                Loc.T("Wait for the virtual controller"),
                null, Core.CenterSettings.WaitForVirtualController));
            stack.Children.Add(pairs);

            if (_languageListOpen) stack.Children.Add(BuildLanguageList());

            // Under the language row, and ONLY while the preference is "System": it is the one entry
            // whose result is not written on it. "System language" does not say which language that
            // turned out to be, and on a machine where the answer is English - which is every machine
            // we do not translate - the setting otherwise looks like it is not working.
            else if (Loc.Preference == UiLanguage.System)
                stack.Children.Add(new TextBlock
                {
                    Text = "→ " + Loc.NameOf(Loc.Current),
                    FontSize = 13,
                    Foreground = UiHelpers.Subtle,
                    Margin = new Thickness(2, 2, 0, 0),
                });

            ContentHost.Children.Add(stack);
            ApplyCenterSettingsSelection();
        }

        /// <summary>
        /// The unfolded language list.
        ///
        /// A LIST, NOT A WPF ComboBox. Center has no ComboBox anywhere - it is a gamepad interface
        /// built from panels, and a single drop-down would be the one control in the app that needs
        /// its own focus model. Unfolding in place gives what a drop-down is wanted FOR here: seeing
        /// at a glance which languages exist, instead of pressing a button five times to find out.
        ///
        /// TWO COLUMNS, and the left one decides the order: the English name, sorted, so the list
        /// reads the same whatever language is active - somebody who has landed in Korean by accident
        /// finds "German" where it was before. The right column is the language in its OWN script,
        /// which is the half that lets them recognise their own.
        /// </summary>
        private UIElement BuildLanguageList()
        {
            _languageRows.Clear();
            var list = new StackPanel { Margin = new Thickness(0, 2, 10, 0) };

            var order = LanguageOrder();
            for (int i = 0; i < order.Length; i++)
            {
                var lang = order[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new TextBlock
                {
                    Text = EnglishNameOf(lang),
                    FontSize = 16,
                    Foreground = UiHelpers.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var native = new TextBlock
                {
                    Text = Loc.NameOf(lang),
                    FontSize = 16,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(native, 1);
                grid.Children.Add(native);

                // The tick marks what is STORED, so "System" stays ticked rather than the language it
                // happens to resolve to - otherwise choosing System looks like it chose German.
                if (lang == Loc.Preference)
                {
                    var check = new TextBlock
                    {
                        Text = "\uE73E",
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 14,
                        Foreground = UiHelpers.Ok,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0),
                    };
                    Grid.SetColumn(check, 2);
                    grid.Children.Add(check);
                }

                var row = new Border
                {
                    Child = grid,
                    Background = UiHelpers.Card,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    BorderThickness = new Thickness(2),
                    BorderBrush = i == _languageIndex ? UiHelpers.Accent : Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                int captured = i;
                row.MouseLeftButtonUp += (_, __) => { _languageIndex = captured; PickLanguage(); };
                _languageRows.Add(row);
                list.Children.Add(row);
            }

            return list;
        }

        /// <summary>System first because it is the default and the way back; the rest alphabetical by
        /// their ENGLISH name, which is the column the list is read down.</summary>
        private static UiLanguage[] LanguageOrder()
        {
            var rest = new List<UiLanguage>();
            foreach (var l in (UiLanguage[])Enum.GetValues(typeof(UiLanguage)))
                if (l != UiLanguage.System) rest.Add(l);

            rest.Sort((a, b) => string.Compare(EnglishNameOf(a), EnglishNameOf(b), StringComparison.Ordinal));

            var all = new List<UiLanguage> { UiLanguage.System };
            all.AddRange(rest);
            return all.ToArray();
        }

        /// <summary>The English name, never translated - it is the sort key and the stable half of
        /// each row. Loc.NameOf gives the other half, in that language own script.</summary>
        private static string EnglishNameOf(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return "German";
                case UiLanguage.French: return "French";
                case UiLanguage.Korean: return "Korean";
                case UiLanguage.Spanish: return "Spanish";
                case UiLanguage.English: return "English";
                default: return "System";
            }
        }

        private void PickLanguage()
        {
            var order = LanguageOrder();
            if (_languageIndex >= 0 && _languageIndex < order.Length)
                Loc.Set(order[_languageIndex]);

            _languageListOpen = false;

            // The whole window, not just this screen: the footer chips, the tab strip and the header
            // chip are all drawn in the old language and none of them redraw on their own. Half a
            // translated window reads as a broken translation.
            RenderCenterSettings();
            RefreshTabStrip();
            RefreshActionBar();
        }

        /// <summary>One row: title on the left, a switch or the current value on the right. Same
        /// shape as the library's BuildSettingRow, kept separate because that one owns the library
        /// screen's row list and index.</summary>
        private Border BuildCenterSettingRow(int index, string title, string valueText, bool? on = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            UIElement state = on.HasValue
                ? BuildToggle(on.Value)
                : new TextBlock
                {
                    Text = valueText ?? string.Empty,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                };
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

            var row = new Border
            {
                Child = grid,
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 10, 10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
            };
            row.MouseLeftButtonUp += (_, __) => { _centerSettingsIndex = index; ActivateCenterSetting(); };
            _centerSettingsRows.Add(row);
            return row;
        }

        private void ApplyCenterSettingsSelection()
        {
            foreach (var row in _centerSettingsRows)
                row.BorderBrush = row.Tag is int i && i == _centerSettingsIndex
                    ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveCenterSettingsSelection(PadButton dir)
        {
            // While the list is unfolded it owns the D-pad. Up/Down walk it and nothing reaches the
            // two settings rows behind it, which is what an open drop-down does everywhere else.
            if (_languageListOpen)
            {
                int count = LanguageOrder().Length;
                int move = dir == PadButton.Up ? -1 : dir == PadButton.Down ? 1 : 0;
                if (move == 0) return;

                int target = _languageIndex + move;
                if (target < 0 || target >= count) return;

                _languageIndex = target;
                RenderCenterSettings();
                return;
            }

            if (_centerSettingsRows.Count == 0) return;

            int last = _centerSettingsRows.Count - 1;
            int next = _centerSettingsIndex;

            // One row of two, so Left/Right move and Up/Down do not. Written as the two directions
            // that DO something rather than as a grid: a second row would need the stride anyway, and
            // guessing at one now would be a rule nobody can check.
            if (dir == PadButton.Left) next--;
            else if (dir == PadButton.Right) next++;
            else return;

            if (next < 0 || next > last || next == _centerSettingsIndex) return;

            _centerSettingsIndex = next;
            ApplyCenterSettingsSelection();
            RefreshActionBar();
        }

        private void ActivateCenterSetting()
        {
            switch (_centerSettingsIndex)
            {
                case CenterSettingsLanguageRow:
                    if (_languageListOpen) { PickLanguage(); return; }

                    // Opens ON the stored choice, not at the top: the list is opened to change
                    // something, and starting anywhere else means the first thing the user has to do
                    // is find where they already are.
                    _languageIndex = Math.Max(0, Array.IndexOf(LanguageOrder(), Loc.Preference));
                    _languageListOpen = true;
                    RenderCenterSettings();
                    RefreshActionBar();
                    return;

                case CenterSettingsFullscreenRow:
                    WindowMode.Toggle(this);
                    break;

                case CenterSettingsWaitControllerRow:
                    Core.CenterSettings.WaitForVirtualController = !Core.CenterSettings.WaitForVirtualController;
                    break;
            }

            RenderCenterSettings();
            RefreshActionBar();
        }
    }
}
