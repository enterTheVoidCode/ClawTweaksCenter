using System;
using System.Windows.Media.Imaging;

namespace ClawTweaksCenter.Navigation
{
    /// <summary>Resolves the bundled Xbox glyph image for a <see cref="PadButton"/>.</summary>
    public static class Glyphs
    {
        private const string Base = "pack://application:,,,/Assets/xbox/";

        /// <summary>The short text a shoulder/trigger is drawn as, or null when the button has a real
        /// glyph image. There is no bundled artwork for LB/RB/LT/RT, and picking one of the existing
        /// images as a stand-in would label the wrong button — the footer draws these as text
        /// instead (see ActionBarBuilder).</summary>
        public static string TextFor(PadButton b)
        {
            switch (b)
            {
                case PadButton.LB: return "LB";
                case PadButton.RB: return "RB";
                case PadButton.LT: return "LT";
                case PadButton.RT: return "RT";
                default: return null;
            }
        }

        public static BitmapImage For(PadButton b)
        {
            if (TextFor(b) != null) return null;
            string file;
            switch (b)
            {
                case PadButton.A: file = "xbox_button_color_a.png"; break;
                case PadButton.B: file = "xbox_button_color_b.png"; break;
                case PadButton.X: file = "xbox_button_color_x.png"; break;
                case PadButton.Y: file = "xbox_button_color_y.png"; break;
                case PadButton.Menu: file = "xbox_button_menu.png"; break;
                case PadButton.View: file = "xbox_button_view.png"; break;
                default: file = "xbox_button_view.png"; break;
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(Base + file, UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
    }
}
