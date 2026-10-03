using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The notification list, and the counter beside the clock that leads to it.
    ///
    /// ── WHY IT IS IN THE FOOTER AND NOT A BANNER ────────────────────────────────────────────────
    /// The messages that collect here - a driver update, something pending in Windows Update, a new
    /// widget build - are all "worth knowing, none of them now". A banner on the start screen would
    /// interrupt the one thing the user opened Center to do; a small count next to the clock is
    /// there whenever they happen to look, on both screens they can start on.
    ///
    /// ── LT, and only where LT is free ───────────────────────────────────────────────────────────
    /// The letter bar owns LT in All and Not installed, the ROM tab cycles systems with it. So the
    /// binding is decided in RefreshActionBar, AFTER every screen has said what it wants, and the
    /// keycap appears only when the binding actually exists. That is the letter bar's own rule and
    /// the reason nobody has to press a key to find out whether it does anything.
    ///
    /// ⚠️ The COUNT stays visible either way. The keycap is an affordance and hiding it where the key
    /// is taken is right; hiding the fact that three things are waiting, because this happens to be
    /// the ROM tab, would be hiding information for a reason the user cannot see.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private int _notificationsSelectedIndex;
        private List<Core.Notification> _notificationsShown = new List<Core.Notification>();

        /// <summary>Where B goes back to. The list is reachable from Home and from the library, and
        /// dropping someone onto the start screen after they opened it mid-library would be a worse
        /// answer than remembering.</summary>
        private View _notificationsCameFrom = View.Home;

        /// <summary>Set while the list is on screen so the store's Changed event can redraw it.
        /// Subscribed once, in the constructor path, because a per-open subscription that is missed
        /// on one exit path leaks a handler for the lifetime of the window.</summary>
        private void HookNotifications()
        {
            Core.Notifications.Changed += () =>
            {
                // Raised from a background check, so it arrives on a worker thread.
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)OnNotificationsChanged); return; }
                OnNotificationsChanged();
            };
        }

        private void OnNotificationsChanged()
        {
            RefreshActionBar();                     // redraws the counter through its post-step
            if (_view == View.Notifications) RenderNotifications();
        }

        // ── The counter beside the clock ────────────────────────────────────────────────────────
        private void RefreshNotificationIndicator(bool bindLeftTrigger)
        {
            if (FooterNotify == null) return;

            int unread = Core.Notifications.UnreadCount();

            // ALWAYS on screen, even at zero (user, 2026-09-13). A bell that exists only while
            // something is wrong cannot be learned - people would find it once, in the moment they
            // are least inclined to go exploring. At zero it is dimmed and carries no number, and
            // opening it says so instead of showing a blank screen.
            if (_view == View.Notifications)
            {
                // The one exception: pointing at the screen you are already looking at is noise.
                FooterNotify.Visibility = Visibility.Collapsed;
                FooterNotify.Child = null;
                return;
            }

            var tone = unread > 0 ? UiHelpers.Accent : UiHelpers.Subtle;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
            row.Children.Add(new TextBlock
            {
                Text = "",                    // Segoe MDL2 "Ringer"
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = tone,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, unread > 0 ? 5 : 0, 0),
            });
            if (unread > 0)
                row.Children.Add(new TextBlock
                {
                    Text = unread.ToString(),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = tone,
                    VerticalAlignment = VerticalAlignment.Center,
                });

            if (bindLeftTrigger)
            {
                _liveActions[PadButton.LT] = OpenNotifications;
                var cap = BuildKeyCap("LT");
                if (cap is FrameworkElement fe) fe.Margin = new Thickness(9, 0, 0, 0);
                row.Children.Add(cap);
            }

            // A PILL OF ITS OWN (user, 2026-10-03): bell, count and LT together on a card, so they
            // read as one control and stand apart from the clock beside them.
            FooterNotify.Background = FooterPillBrush;
            FooterNotify.CornerRadius = new CornerRadius(14);
            FooterNotify.Padding = new Thickness(11, 4, bindLeftTrigger ? 5 : 11, 4);
            FooterNotify.Child = row;
            FooterNotify.Visibility = Visibility.Visible;
            FooterNotify.MouseLeftButtonUp -= NotifyClicked;
            FooterNotify.MouseLeftButtonUp += NotifyClicked;
        }

        private void NotifyClicked(object sender, MouseButtonEventArgs e) => OpenNotifications();

        // ── The list ────────────────────────────────────────────────────────────────────────────
        private void OpenNotifications()
        {
            _notificationsCameFrom = _view == View.Notifications ? _notificationsCameFrom : _view;
            if (_view == View.Library) LeaveLibrary();
            _view = View.Notifications;
            _notificationsSelectedIndex = 0;
            RenderNotifications();

            // ⚠️ Opened FROM the library, the tab strip stayed on screen: LeaveLibrary collapses the
            // library host but the strip is drawn from _view, and nothing had told it the view had
            // changed. Tabs are navigation for a shelf that is no longer there (user, 2026-09-13).
            RefreshTabStrip();
            RefreshActionBar();
        }

        private void CloseNotifications()
        {
            if (_notificationsCameFrom == View.Library) { OpenLibrary(); return; }
            GoHome();   // brings the tab strip back on its own
        }

        private void RenderNotifications()
        {
            BeginContent(centred: false);
            _notificationsShown = Core.Notifications.All();

            // A column, not the full window. Notification cards are one or two lines of text, and
            // stretched across a 16:9 screen the eye has to travel the whole width to read a
            // sentence that ends after a third of it (user, 2026-09-13).
            var column = new StackPanel { MaxWidth = 900, Margin = new Thickness(8, 0, 24, 0) };
            column.Children.Add(UiHelpers.Title("Notifications"));

            if (_notificationsShown.Count == 0)
            {
                column.Children.Add(UiHelpers.StatusRow(StatusKind.Ok, "No new notifications",
                    "Center tells you here when a driver, a Windows update or a new widget build turns up."));
                ContentHost.Children.Add(column);
                return;
            }

            for (int i = 0; i < _notificationsShown.Count; i++)
                column.Children.Add(BuildNotificationCard(_notificationsShown[i], i));

            ContentHost.Children.Add(column);
        }

        private Border BuildNotificationCard(Core.Notification n, int index)
        {
            bool selected = index == _notificationsSelectedIndex;

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = GlyphFor(n.Kind),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = n.IsUnread ? UiHelpers.Accent : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            head.Children.Add(new TextBlock
            {
                Text = n.Title ?? "",
                FontSize = 15,
                // Unread is carried by WEIGHT and by the dot below, not by colour alone - the cards
                // sit on the same background and a colour difference at this size is easy to miss.
                FontWeight = n.IsUnread ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (n.IsUnread)
                head.Children.Add(new Border
                {
                    Width = 7, Height = 7,
                    CornerRadius = new CornerRadius(999),
                    Background = UiHelpers.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                });

            var stack = new StackPanel();
            stack.Children.Add(head);

            if (!string.IsNullOrWhiteSpace(n.Detail))
                stack.Children.Add(new TextBlock
                {
                    Text = n.Detail,
                    FontSize = 13, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 4, 0, 0),
                });

            stack.Children.Add(new TextBlock
            {
                Text = FormatWhen(n.CreatedUtc),
                FontSize = 12, Foreground = UiHelpers.Subtle, Opacity = 0.8,
                Margin = new Thickness(24, 5, 0, 0),
            });

            var pad = new Thickness(16, 12, 16, 12);
            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0),
                Padding = selected ? Deflate(pad, 2) : pad,
                Cursor = Cursors.Hand,
                Child = stack,
            };
            card.MouseLeftButtonUp += (_, __) =>
            {
                _notificationsSelectedIndex = index;
                ActivateNotification(n);
            };
            return card;
        }

        /// <summary>Opening one takes you where it is about, and marks it read on the way. Marking it
        /// read WITHOUT going anywhere would make the card a thing you dismiss rather than act on.</summary>
        private void ActivateNotification(Core.Notification n)
        {
            Core.Notifications.MarkRead(n.Key);
            switch (n.Kind)
            {
                case "driver":
                case "windows":
                    OpenDrivers();
                    break;
                case "widget":
                    OpenBrowse();
                    break;
                case "fse":
                    // The setup that registers the FSE package is on the releases page; Center does
                    // not download executables itself (self-updater removed, see the dev guidelines).
                    Core.PrerequisiteGuide.OpenPage(Core.SetupVersionCheck.ReleasesPageUrl, m => Core.InstallLog.Write(m));
                    RenderNotifications();
                    RefreshActionBar();
                    break;
                case "announcement":
                    // Nowhere to send anyone. The message IS the content, and it is text fetched
                    // over the network - nothing in it may drive navigation.
                    RenderNotifications();
                    RefreshActionBar();
                    break;
                default:
                    RenderNotifications();
                    RefreshActionBar();
                    break;
            }
        }

        /// <summary>
        /// One glyph per kind, so the list is readable before a single word of it is.
        ///
        /// Segoe MDL2 Assets. If one of these renders as an empty box the code point is wrong for
        /// the installed font - that is the symptom to look for, not a layout problem.
        /// </summary>
        private static string GlyphFor(string kind)
        {
            switch (kind)
            {
                case "announcement": return "\uE789";  // Megaphone - the project talking to you
                case "driver": return "\uE977";        // PC1 - the device itself
                case "windows": return "\uE90F";       // Repair - the toolbox
                case "widget": return "\uE896";        // Download
                case "fse": return "\uE7FC";           // Game - the full-screen (Xbox) mode
                default: return "\uE946";              // Info
            }
        }

        /// <summary>Today shows a time, everything else a date. "3 days ago" was the other option and
        /// it answers a question nobody asked here - the list is short and already newest-first.</summary>
        private static string FormatWhen(DateTime createdUtc)
        {
            var local = createdUtc.ToLocalTime();
            return local.Date == DateTime.Now.Date
                ? local.ToString("t")
                : local.ToString("d MMM, HH:mm");
        }

        // ── Navigation ──────────────────────────────────────────────────────────────────────────
        private void MoveNotificationsSelection(PadButton dir)
        {
            if (_notificationsShown.Count == 0) return;
            int next = _notificationsSelectedIndex;
            if (dir == PadButton.Up) next--;
            else if (dir == PadButton.Down) next++;
            else return;

            if (next < 0) next = 0;
            if (next > _notificationsShown.Count - 1) next = _notificationsShown.Count - 1;
            if (next == _notificationsSelectedIndex) return;

            _notificationsSelectedIndex = next;
            RenderNotifications();
        }

        private void RefreshNotificationsActionBar()
        {
            bool any = _notificationsShown.Count > 0;
            bool anyUnread = Core.Notifications.UnreadCount() > 0;

            AddAction(PadButton.A, "Open", any, () =>
            {
                if (_notificationsSelectedIndex >= 0 && _notificationsSelectedIndex < _notificationsShown.Count)
                    ActivateNotification(_notificationsShown[_notificationsSelectedIndex]);
            });
            AddAction(PadButton.Y, "Mark all as read", anyUnread, Core.Notifications.MarkAllRead);
            AddAction(PadButton.B, "Back", true, CloseNotifications);
            AddScrollHint();
        }
    }
}
