using System;
using System.Windows;
using System.Windows.Controls;

namespace ClawTweaksCenter.Ui
{
    /// <summary>
    /// The footer's chip row: ONE line, always (user, 2026-09-21).
    ///
    /// It replaced a WrapPanel, which moved chips onto a second line whenever a screen declared more
    /// than fit - on My Apps it did, and the footer grew under the library. Here a child that does not
    /// fit is left out, from the END of the row. RefreshActionBar adds the chips in the fixed pad order
    /// and the stick hint last, so the hint goes first and then the least used button. The binding of a
    /// chip that is left out stays live; only its label is missing.
    /// </summary>
    public sealed class SingleLineBar : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            double width = 0, height = 0;
            bool full = false;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, available.Height));
                if (full) continue;
                double w = child.DesiredSize.Width;
                if (width + w > available.Width) { full = true; continue; }
                width += w;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double x = 0;
            bool full = false;
            foreach (UIElement child in InternalChildren)
            {
                double w = child.DesiredSize.Width;
                if (full || x + w > final.Width + 0.5)
                {
                    full = true;
                    // Parked outside the row: a zero-size arrange still draws its content.
                    child.Arrange(new Rect(-100000, 0, w, child.DesiredSize.Height));
                    continue;
                }
                child.Arrange(new Rect(x, (final.Height - child.DesiredSize.Height) / 2, w, child.DesiredSize.Height));
                x += w;
            }
            return final;
        }
    }
}
