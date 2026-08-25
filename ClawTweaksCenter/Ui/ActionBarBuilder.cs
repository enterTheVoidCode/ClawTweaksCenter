using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawTweaksCenter.Navigation;

namespace ClawTweaksCenter.Ui
{
    /// <summary>
    /// Builds the footer action bar — glyph + label tiles, pad- and mouse-clickable — shared between
    /// <see cref="MainWindow"/>'s per-phase actions and <see cref="CenterMenuWindow"/>'s fixed
    /// X/A/Y/B actions, so both windows render the same "which button does what" row identically.
    ///
    /// The tile look comes from the ControllerActionTile style in App.xaml rather than a template
    /// built here, so hover/press/focus states stay in one place with the rest of the theme.
    /// </summary>
    public static class ActionBarBuilder
    {
        private const double GlyphSize = 24;
        private const double LabelFontSize = 14;

        public static UIElement BuildChip(PadButton button, string label, bool enabled, System.Action onClick)
        {
            UIElement glyph = BuildGlyph(button);

            // Translated HERE rather than at the call sites. Every footer label in both windows
            // goes through this one method, so a lookup here localises all of them and a label that
            // is not in the table passes through as its own English. See Core/Localization.cs.
            var text = new TextBlock
            {
                Text = Core.Loc.T(label),
                FontSize = LabelFontSize,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyph);
            content.Children.Add(text);

            // Pointer input invokes the same action as the controller.
            var btn = new Button
            {
                Content = content,
                Style = (Style)Application.Current.Resources["ControllerActionTile"],
                Focusable = false,
                IsEnabled = enabled,
            };
            btn.Click += (_, __) => onClick();
            return btn;
        }

        /// <summary>
        /// The button's own artwork, or — for the shoulders and triggers, which have no bundled image
        /// — a small rounded badge with "LB"/"RB"/"LT"/"RT" in it. Both forms occupy the same
        /// GlyphSize box so a footer mixing them still lines up.
        /// </summary>
        private static UIElement BuildGlyph(PadButton button)
        {
            string text = Glyphs.TextFor(button);
            if (text == null)
            {
                var image = new Image
                {
                    Source = Glyphs.For(button),
                    Width = GlyphSize, Height = GlyphSize,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }

            return new Border
            {
                Background = (Brush)Application.Current.Resources["CardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["SubtleTextBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                MinWidth = GlyphSize + 6,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextBrush"],
                },
            };
        }

        /// <summary>Builds a noninteractive hint aligned with action tiles.</summary>
        public static UIElement BuildHint(ImageSource glyphSource, string label)
        {
            var glyph = new Image
            {
                Source = glyphSource,
                Width = GlyphSize, Height = GlyphSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);

            // Translated HERE rather than at the call sites. Every footer label in both windows
            // goes through this one method, so a lookup here localises all of them and a label that
            // is not in the table passes through as its own English. See Core/Localization.cs.
            var text = new TextBlock
            {
                Text = Core.Loc.T(label),
                FontSize = LabelFontSize,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"],
                Margin = new Thickness(10, 0, 0, 0),
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyph);
            content.Children.Add(text);

            return new Border
            {
                Padding = new Thickness(8, 5, 12, 5),
                Margin = new Thickness(3),
                MinHeight = 40,
                Child = content,
            };
        }
    }
}
