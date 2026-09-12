using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Audio;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Sound settings: interface sounds, background music and a volume for each. Reached from the
    /// library settings, the same way the tab editor is.
    ///
    /// It lives INSIDE the settings screen: _soundSettingsOpen implies _settingsOpen, so every guard
    /// that keeps the library still while settings are up covers this too.
    ///
    /// Up/Down pick a row, A switches a switch, Left/Right change a volume or step to the next sound
    /// set. Every change is heard straight away - the navigation sound Invoke plays for the press is
    /// the preview of the effects volume, the music follows its own volume on the next buffer, and a
    /// new sound set plays itself once so the choice is made by ear rather than by its name.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _soundSettingsOpen;
        private int _soundSettingsIndex;
        private readonly List<Border> _soundSettingsRows = new List<Border>();

        // The order is the order of the screen, the way the settings grid works: the switch first,
        // then what it sounds like, then the music with the same shape underneath.
        private const int SoundRowEffects = 0;
        private const int SoundRowEffectsVolume = 1;
        private const int SoundRowNavigate = 2;
        private const int SoundRowBack = 3;
        private const int SoundRowMusic = 4;
        private const int SoundRowMusicVolume = 5;
        private const int SoundRowCount = 6;

        /// <summary>Ten presses from silent to full. Finer than that is not a difference anyone hears
        /// on a handheld speaker, and coarser leaves no room between quiet and off.</summary>
        private const int VolumeStep = 10;

        private void OpenSoundSettings()
        {
            _soundSettingsOpen = true;
            _soundSettingsIndex = 0;
            RenderSoundSettings();
            RefreshActionBar();
        }

        private void CloseSoundSettings()
        {
            _soundSettingsOpen = false;
            _soundSettingsRows.Clear();
            RenderLibrarySettings();
            RefreshActionBar();
        }

        private void RenderSoundSettings()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            _soundSettingsRows.Clear();

            // Heading beside the rows, like the tab editor: one layout for the settings sub-screens.
            var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Sound settings"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            // Left/Right have no footer chip, so they are named here.
            heading.Children.Add(TabEditorHint("Change a value with left and right."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new StackPanel { Width = 460 };
            list.Children.Add(BuildSoundRow(SoundRowEffects, "Interface sounds", Core.CenterSettings.InterfaceSounds, null));
            list.Children.Add(BuildSoundRow(SoundRowEffectsVolume, "Effects volume", null, Core.CenterSettings.EffectsVolume));
            list.Children.Add(BuildSoundRow(SoundRowNavigate, "Navigate", null, null,
                VariantLabel(UiSounds.VariantOf(UiSound.Navigate))));
            list.Children.Add(BuildSoundRow(SoundRowBack, "Go back", null, null,
                VariantLabel(UiSounds.VariantOf(UiSound.Back))));
            list.Children.Add(BuildSoundRow(SoundRowMusic, "Background music", Core.CenterSettings.BackgroundMusic, null));
            list.Children.Add(BuildSoundRow(SoundRowMusicVolume, "Music volume", null, Core.CenterSettings.MusicVolume));
            Grid.SetColumn(list, 1);
            columns.Children.Add(list);

            LibraryRoot.Children.Add(columns);
            ApplySoundSettingsSelection();
        }

        /// <summary>The set names are what the FOLDERS they came from are called, capitalised. They
        /// are not translated: they name a recording, the way a font name does.</summary>
        private static string VariantLabel(string variant)
            => string.IsNullOrEmpty(variant) ? string.Empty
                : char.ToUpperInvariant(variant[0]) + variant.Substring(1);

        /// <summary>A switch row, a volume row (a bar plus the number, so the level reads from across
        /// the room and the exact value is there for anyone who wants it), or a row that names the
        /// sound set it is on.</summary>
        private Border BuildSoundRow(int index, string title, bool? on, int? volume, string valueText = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(title),
                FontSize = 17,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            UIElement state;
            if (on.HasValue)
            {
                state = BuildToggle(on.Value);
            }
            else if (valueText != null)
            {
                state = new TextBlock
                {
                    Text = valueText,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    MinWidth = 110,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                };
            }
            else
            {
                int level = volume ?? 0;
                var track = new Grid { Width = 120, Height = 6, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = UiHelpers.Subtle, Opacity = 0.35, CornerRadius = new CornerRadius(3) });
                track.Children.Add(new Border
                {
                    Background = UiHelpers.Accent,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 120 * level / 100.0,
                });

                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
                panel.Children.Add(track);
                panel.Children.Add(new TextBlock
                {
                    Text = level + " %",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Subtle,
                    MinWidth = 56,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                state = panel;
            }
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

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
            };
            row.MouseLeftButtonUp += (_, __) => { _soundSettingsIndex = index; ActivateSoundSetting(); };
            _soundSettingsRows.Add(row);
            return row;
        }

        private void ApplySoundSettingsSelection()
        {
            foreach (var row in _soundSettingsRows)
                row.BorderBrush = row.Tag is int i && i == _soundSettingsIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private static bool IsVolumeRow(int index) => index == SoundRowEffectsVolume || index == SoundRowMusicVolume;

        /// <summary>The rows that step through a list of sound sets rather than a number.</summary>
        private static bool IsVariantRow(int index) => index == SoundRowNavigate || index == SoundRowBack;

        private static UiSound VariantRowSound(int index) => index == SoundRowBack ? UiSound.Back : UiSound.Navigate;

        private void MoveSoundSettingsSelection(PadButton dir)
        {
            switch (dir)
            {
                case PadButton.Up:
                    if (_soundSettingsIndex == 0) return;
                    _soundSettingsIndex--;
                    break;
                case PadButton.Down:
                    if (_soundSettingsIndex >= SoundRowCount - 1) return;
                    _soundSettingsIndex++;
                    break;
                case PadButton.Left:
                case PadButton.Right:
                    if (IsVariantRow(_soundSettingsIndex))
                    {
                        StepVariant(_soundSettingsIndex, dir == PadButton.Right ? 1 : -1);
                        RenderSoundSettings();
                        return;
                    }
                    if (!IsVolumeRow(_soundSettingsIndex)) return;
                    ChangeVolume(_soundSettingsIndex, dir == PadButton.Right ? VolumeStep : -VolumeStep);
                    RenderSoundSettings();
                    return;
                default:
                    return;
            }
            ApplySoundSettingsSelection();
            RefreshActionBar();
        }

        private static void ChangeVolume(int row, int delta)
        {
            if (row == SoundRowEffectsVolume)
            {
                Core.CenterSettings.EffectsVolume = SteppedVolume(Core.CenterSettings.EffectsVolume, delta);
                UiSounds.EffectsVolume = Core.CenterSettings.EffectsVolume / 100f;
            }
            else
            {
                Core.CenterSettings.MusicVolume = SteppedVolume(Core.CenterSettings.MusicVolume, delta);
                UiSounds.MusicVolume = Core.CenterSettings.MusicVolume / 100f;
            }
        }

        /// <summary>
        /// One step, landing on a round number.
        ///
        /// The defaults are deliberately off the grid (48 and 28, a fifth below what they were), and
        /// plain addition would carry that offset for the life of the installation - 58, 68, 78.
        /// Snapping costs the first press a couple of points and gives every press after it a value
        /// that can be read out loud.
        /// </summary>
        private static int SteppedVolume(int current, int delta)
        {
            int next = (int)Math.Round((current + delta) / (double)VolumeStep) * VolumeStep;
            return Math.Clamp(next, 0, 100);
        }

        /// <summary>
        /// The next set for this row, wrapping, and it plays itself once.
        ///
        /// The preview is the point of the row: the names say which recording it is, not what it
        /// sounds like. It goes through UiSounds.Play, so it obeys the interface-sounds switch and
        /// the effects volume - a preview that is louder than the real thing previews nothing.
        /// </summary>
        private static void StepVariant(int row, int delta)
        {
            UiSound sound = VariantRowSound(row);
            string[] sets = UiSounds.VariantsFor(sound);
            if (sets.Length < 2) return;

            int index = Array.IndexOf(sets, UiSounds.VariantOf(sound));
            if (index < 0) index = 0;
            string next = sets[((index + delta) % sets.Length + sets.Length) % sets.Length];

            UiSounds.SetVariant(sound, next);
            if (sound == UiSound.Back) Core.CenterSettings.BackSound = next;
            else Core.CenterSettings.NavigateSound = next;
            UiSounds.Play(sound);
        }

        /// <summary>A on a switch flips it, and on a sound set steps to the next one. A volume row has
        /// no A - the mouse click lands here too, and does nothing there rather than guessing a
        /// direction.</summary>
        private void ActivateSoundSetting()
        {
            switch (_soundSettingsIndex)
            {
                case SoundRowEffects:
                    Core.CenterSettings.InterfaceSounds = !Core.CenterSettings.InterfaceSounds;
                    UiSounds.EffectsEnabled = Core.CenterSettings.InterfaceSounds;
                    break;
                case SoundRowMusic:
                    // RefreshActionBar below runs UpdateLibraryMusic: the settings screen is still the
                    // library, so the music starts or stops while the user is standing on the switch.
                    Core.CenterSettings.BackgroundMusic = !Core.CenterSettings.BackgroundMusic;
                    break;
                case SoundRowNavigate:
                case SoundRowBack:
                    StepVariant(_soundSettingsIndex, 1);
                    break;
                default:
                    return;
            }
            RenderSoundSettings();
            RefreshActionBar();
        }

        private void AddSoundSettingsActions()
        {
            if (IsVariantRow(_soundSettingsIndex))
                AddAction(PadButton.A, "Change", true, ActivateSoundSetting);
            else if (!IsVolumeRow(_soundSettingsIndex))
                AddAction(PadButton.A, "Toggle", true, ActivateSoundSetting);
            AddAction(PadButton.B, "Back", true, CloseSoundSettings);
        }
    }
}
