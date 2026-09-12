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
    /// Up/Down pick a row, A switches a switch, Left/Right change a volume. A volume change is heard
    /// straight away - the navigation sound Invoke plays for the press is the preview of the effects
    /// volume, and the music follows its own volume on the next buffer.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private bool _soundSettingsOpen;
        private int _soundSettingsIndex;
        private readonly List<Border> _soundSettingsRows = new List<Border>();

        private const int SoundRowEffects = 0;
        private const int SoundRowEffectsVolume = 1;
        private const int SoundRowMusic = 2;
        private const int SoundRowMusicVolume = 3;
        private const int SoundRowCount = 4;

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
            heading.Children.Add(TabEditorHint("Change a volume with left and right."));
            Grid.SetColumn(heading, 0);
            columns.Children.Add(heading);

            var list = new StackPanel { Width = 460 };
            list.Children.Add(BuildSoundRow(SoundRowEffects, "Interface sounds", Core.CenterSettings.InterfaceSounds, null));
            list.Children.Add(BuildSoundRow(SoundRowEffectsVolume, "Effects volume", null, Core.CenterSettings.EffectsVolume));
            list.Children.Add(BuildSoundRow(SoundRowMusic, "Background music", Core.CenterSettings.BackgroundMusic, null));
            list.Children.Add(BuildSoundRow(SoundRowMusicVolume, "Music volume", null, Core.CenterSettings.MusicVolume));
            Grid.SetColumn(list, 1);
            columns.Children.Add(list);

            LibraryRoot.Children.Add(columns);
            ApplySoundSettingsSelection();
        }

        /// <summary>A switch row or a volume row: a bar plus the number, so the level reads from
        /// across the room and the exact value is there for anyone who wants it.</summary>
        private Border BuildSoundRow(int index, string title, bool? on, int? volume)
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
                Core.CenterSettings.EffectsVolume += delta;
                UiSounds.EffectsVolume = Core.CenterSettings.EffectsVolume / 100f;
            }
            else
            {
                Core.CenterSettings.MusicVolume += delta;
                UiSounds.MusicVolume = Core.CenterSettings.MusicVolume / 100f;
            }
        }

        /// <summary>A on a switch flips it. A volume row has no A - the mouse click lands here too,
        /// and does nothing there rather than guessing a direction.</summary>
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
                default:
                    return;
            }
            RenderSoundSettings();
            RefreshActionBar();
        }

        private void AddSoundSettingsActions()
        {
            if (!IsVolumeRow(_soundSettingsIndex))
                AddAction(PadButton.A, "Toggle", true, ActivateSoundSetting);
            AddAction(PadButton.B, "Back", true, CloseSoundSettings);
        }
    }
}
