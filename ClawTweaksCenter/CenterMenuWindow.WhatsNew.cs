using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// "What's new": the notes of the last <see cref="Core.WhatsNew.Max"/> versions, newest first,
    /// one card each (user, 2026-10-03). Comes up by itself once after an update has gone through,
    /// and from the start screen's tile any time. See Core\WhatsNew.cs for where the notes live.
    ///
    /// A reading screen: nothing on it is selectable, so the D-pad and the left stick scroll it.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private View _whatsNewCameFrom = View.Home;
        /// <summary>Opened by the update itself: the newest card says it is the version just
        /// installed, and the heading names it.</summary>
        private bool _whatsNewAfterUpdate;

        private const double WhatsNewScrollStep = 140;

        private void OpenWhatsNew(bool afterUpdate)
        {
            _whatsNewAfterUpdate = afterUpdate;
            if (_view != View.WhatsNew) _whatsNewCameFrom = _view;
            if (_view == View.Library) LeaveLibrary();
            _view = View.WhatsNew;
            RenderWhatsNew();
            ContentScroller?.ScrollToTop();
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void CloseWhatsNew()
        {
            _whatsNewAfterUpdate = false;
            if (_whatsNewCameFrom == View.Library) { OpenLibrary(); return; }
            GoHome();
        }

        private void RenderWhatsNew()
        {
            BeginContent(centred: false);
            var entries = Core.WhatsNew.All();
            var running = Core.WhatsNew.Running;

            var column = new StackPanel { MaxWidth = 900, Margin = new Thickness(8, 0, 24, 16) };
            // The announced version is the newest entry: the running one after a real update, the
            // next release in a debug build's preview.
            column.Children.Add(UiHelpers.Title(_whatsNewAfterUpdate
                ? Core.Loc.F("New in Center {0}", (entries.Count > 0 ? entries[0].Version : running).ToString())
                : Core.Loc.T("What's new in Center")));

            if (entries.Count == 0)
                column.Children.Add(UiHelpers.StatusRow(StatusKind.Ok, "No release notes.", ""));

            foreach (var e in entries)
            {
                var stack = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                head.Children.Add(new TextBlock
                {
                    Text = Core.Loc.F("Version {0}", e.Heading),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiHelpers.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (e.Version == running)
                    head.Children.Add(new Border
                    {
                        Background = UiHelpers.Accent,
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(8, 1, 8, 2),
                        Margin = new Thickness(12, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = Core.Loc.T("Installed now"),
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brushes.Black,
                        },
                    });
                stack.Children.Add(head);
                ReleaseNotes.RenderInto(stack, e.Markdown);

                column.Children.Add(new Border
                {
                    Child = stack,
                    Background = UiHelpers.Card,
                    BorderBrush = e.Version == running ? UiHelpers.Accent : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(18, 14, 18, 16),
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            ContentHost.Children.Add(column);
        }

        /// <summary>Up and Down scroll - there is nothing to select on a page of notes.</summary>
        private void MoveWhatsNew(PadButton dir)
        {
            if (ContentScroller == null) return;
            if (dir == PadButton.Down) ContentScroller.ScrollToVerticalOffset(ContentScroller.VerticalOffset + WhatsNewScrollStep);
            else if (dir == PadButton.Up) ContentScroller.ScrollToVerticalOffset(ContentScroller.VerticalOffset - WhatsNewScrollStep);
        }

        private void RefreshWhatsNewActionBar()
        {
            AddAction(PadButton.B, _whatsNewAfterUpdate ? "Continue" : "Back", true, CloseWhatsNew);
        }
    }
}
